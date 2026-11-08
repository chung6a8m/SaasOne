# Shared helpers have no side effects when dot-sourced.
Set-StrictMode -Version Latest
function Write-M6Json {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)]$Value)
    $full = [IO.Path]::GetFullPath($Path)
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($full)) | Out-Null
    $temp = $full + '.' + [guid]::NewGuid().ToString('N') + '.tmp'
    try {
        [IO.File]::WriteAllText($temp, ($Value | ConvertTo-Json -Depth 32), [Text.UTF8Encoding]::new($false))
        [IO.File]::Move($temp, $full, $true)
    } finally { if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Force } }
}
function Get-M6Sha {
    $sha = (& git rev-parse HEAD | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $sha -cnotmatch '^[0-9a-f]{40}$') { throw 'source_revision_unknown' }
    return $sha
}
function Invoke-M6Checked {
    param([string]$Executable, [string[]]$Arguments)
    & $Executable @Arguments | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "command_failed:$Executable`:$LASTEXITCODE" }
}
function Protect-M6Text {
    param([AllowEmptyString()][string]$Text)
    # Actions interprets mask commands in its console, not inside uploaded files.
    # Child-created secrets are absent from the parent's environment snapshot.
    # Drop the entire payload (including percent-encoded CR/LF) before persistence,
    # while preserving real line endings and the surrounding evidence structure.
    $result = [regex]::Replace($Text, '(?i)::add-mask::[^\r\n]*', '::add-mask::[REDACTED]')
    $result = [regex]::Replace($result, '(?i)(Password|Pwd|ClientSecret|client_secret|refresh_token|access_token|id_token|authorization|SAAS_BACKUP_MANIFEST_KEY)\s*[=:]\s*(?:"[^"]*"|''[^'']*''|[^;\s<]+)', '$1=[REDACTED]')
    $result = [regex]::Replace($result, 'eyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+', '[REDACTED_JWT]')
    return [regex]::Replace($result, '(?<![A-Za-z0-9_])[A-Za-z0-9_+/=-]{80,}(?![A-Za-z0-9_])', '[REDACTED_LONG_VALUE]')
}
function Read-M6Trx {
    param([Parameter(Mandatory)][string]$Path)
    $settings = [Xml.XmlReaderSettings]::new()
    $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
    $settings.XmlResolver = $null
    $reader = [Xml.XmlReader]::Create([IO.Path]::GetFullPath($Path), $settings)
    try { $xml = [xml]::new(); $xml.XmlResolver = $null; $xml.Load($reader) } finally { $reader.Dispose() }
    $nodes = @($xml.SelectNodes('//*[local-name()="UnitTestResult"]'))
    $counters = @($xml.SelectNodes('//*[local-name()="ResultSummary"]/*[local-name()="Counters"]'))
    if ($nodes.Count -eq 0 -or $counters.Count -ne 1) { throw 'trx_zero_or_missing_results' }
    if ([int]$counters[0].total -ne $nodes.Count -or [int]$counters[0].executed -ne $nodes.Count) { throw 'trx_counter_mismatch' }
    $results = @($nodes | ForEach-Object { [pscustomobject]@{ testName = [string]$_.testName; outcome = [string]$_.outcome } })
    $passed = @($results | Where-Object outcome -CEQ 'Passed').Count
    if ([int]$counters[0].passed -ne $passed) { throw 'trx_counter_mismatch' }
    return [pscustomobject]@{ xml = $xml; results = $results; total = $results.Count; passed = $passed; nonPassed = $results.Count - $passed }
}
function Resolve-M6Artifact {
    param([string]$Root, [string]$Relative)
    if ([string]::IsNullOrEmpty($Relative) -or $Relative.Length -gt 512 -or [IO.Path]::IsPathRooted($Relative) -or
        $Relative.Contains('\') -or $Relative.Contains(':') -or $Relative -match '[\x00-\x1f\x7f]') { throw 'artifact_path_invalid' }
    $current = [IO.Path]::GetFullPath($Root)
    foreach ($part in @('') + $Relative.Split('/')) {
        if ($part -ne '') {
            if ($part -in @('.', '..') -or $part -ne $part.Trim() -or $part.EndsWith('.')) { throw 'artifact_path_invalid' }
            $current = Join-Path $current $part
        }
        if (Test-Path -LiteralPath $current) {
            if ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'artifact_link_forbidden' }
        }
    }
    if (($Relative.Split('/') -ccontains '')) { throw 'artifact_path_invalid' }
    return $current
}
function Close-M6Package {
    param($Package)
    if ($null -ne $Package) { foreach ($handle in $Package.handles) { $handle.Dispose() } }
}
function Open-M6Package {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Root, [Parameter(Mandatory)][string]$TrustedDigest)
    # This verifier runs from the trusted checkout, BEFORE any code from the package executes.
    if ($TrustedDigest -notmatch '^[a-fA-F0-9]{64}$') { throw 'release_trust_required' }
    $handles = [Collections.Generic.List[IO.FileStream]]::new()
    try {
        $rootFull = [IO.Path]::GetFullPath($Root)
        $manifestPath = Resolve-M6Artifact $rootFull 'release.json'
        $handle = [IO.File]::Open($manifestPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
        $handles.Add($handle)
        if ($handle.Length -lt 1 -or $handle.Length -gt 4194304) { throw 'release_manifest_invalid' }
        $digest = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($handle))
        if ($digest -ine $TrustedDigest) { throw 'release_manifest_untrusted' }
        $handle.Position = 0
        $reader = [IO.StreamReader]::new($handle, [Text.Encoding]::UTF8, $true, 4096, $true)
        try { $manifest = $reader.ReadToEnd() | ConvertFrom-Json -Depth 32 } finally { $reader.Dispose() }
        if ($manifest.version -ne 1 -or $manifest.sourceSha -cnotmatch '^[0-9a-f]{40}$' -or
            $manifest.runtimeIdentifier -notin @('linux-x64', 'win-x64') -or @($manifest.files).Count -lt 1 -or @($manifest.files).Count -gt 20000) { throw 'release_manifest_invalid' }
        $expected = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        $null = $expected.Add('release.json')
        foreach ($file in $manifest.files) {
            $path = Resolve-M6Artifact $rootFull ([string]$file.path)
            if (-not $expected.Add([string]$file.path) -or $file.length -lt 0 -or $file.sha256 -notmatch '^[a-fA-F0-9]{64}$') { throw 'release_inventory_invalid' }
            $input = [IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
            $handles.Add($input)
            if ($input.Length -ne $file.length -or [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($input)) -ine $file.sha256) { throw 'release_artifact_changed' }
        }
        $actual = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($entry in Get-ChildItem -LiteralPath $rootFull -Recurse -Force) {
            if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'artifact_link_forbidden' }
            if (-not $entry.PSIsContainer) { $null = $actual.Add([IO.Path]::GetRelativePath($rootFull, $entry.FullName).Replace('\', '/')) }
        }
        if (-not $actual.SetEquals($expected) -or -not $actual.Contains('host/SaaS.Web.dll')) { throw 'release_inventory_not_closed' }
        return [pscustomobject]@{ root = $rootFull; manifest = $manifest; digest = $digest; handles = $handles }
    } catch { foreach ($handle in $handles) { $handle.Dispose() }; throw }
}

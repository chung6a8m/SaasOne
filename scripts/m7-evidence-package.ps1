# Literal-path packaging helpers; no side effects when dot-sourced.
Set-StrictMode -Version Latest
function Get-M7FileDigest {
    param([Parameter(Mandatory)][string]$Path)
    # TRX collisions legitimately produce names such as net10.0[1].trx.
    # -Path treats brackets as wildcards and may omit the file or hash a decoy.
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}
function Write-M7EvidencePackage {
    param([Parameter(Mandatory)][string]$Root, [Parameter(Mandatory)][string]$SourceSha)
    $rootFull=[IO.Path]::GetFullPath($Root)
    $manifest=Join-Path $rootFull 'hashes.json'
    $zip=$rootFull+'.zip'
    if (Test-Path -LiteralPath $manifest) { throw 'm7_package_manifest_already_exists' }
    if (Test-Path -LiteralPath $zip) { throw 'm7_package_archive_already_exists' }
    foreach ($item in @((Get-Item -LiteralPath $rootFull -Force)) + @(Get-ChildItem -LiteralPath $rootFull -Force -Recurse)) {
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'm7_package_link_forbidden' }
    }
    $files=@(Get-ChildItem -LiteralPath $rootFull -Force -Recurse -File | Sort-Object FullName | ForEach-Object {
        @{path=[IO.Path]::GetRelativePath($rootFull,$_.FullName).Replace('\','/');length=$_.Length;sha256=(Get-M7FileDigest $_.FullName)}
    })
    Write-M6Json $manifest @{schemaVersion=1;sourceSha=$SourceSha;files=$files}
    [IO.Compression.ZipFile]::CreateFromDirectory($rootFull,$zip)
    [IO.File]::WriteAllText($zip+'.sha256',(Get-M7FileDigest $zip)+'  '+[IO.Path]::GetFileName($zip)+"`n")
}

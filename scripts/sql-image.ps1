# Shared SQL fixture image trust policy (M0 through M6). This file has no side effects when dot-sourced.
# A Docker pull is not required when the exact previously pulled registry digest is already local.
function Get-SaasPinnedSqlImageDigest {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Image)

    $requiredTag = 'mcr.microsoft.com/mssql/server:2022-CU27-GDR1-ubuntu-22.04'
    $requiredDigest = 'mcr.microsoft.com/mssql/server@sha256:cf8f0ab2ebe61feec2eed2629061a07d247f791c2a8ddda74a4ac9f06a291718'
    if ($Image -cne $requiredTag) { throw 'BLOCKED:sql_image_tag_mismatch' }

    # RepoDigests records the registry-provided digest after pull; a locally retagged
    # image without this trusted digest must not be used as proof of the pinned release.
    $localDigests = @(& docker image inspect $Image --format '{{range .RepoDigests}}{{println .}}{{end}}' 2>$null)
    if ($LASTEXITCODE -eq 0) {
        if ($localDigests -ccontains $requiredDigest) {
            Write-Host "Using verified cached SQL image: $requiredDigest"
            return $requiredDigest
        }
        throw 'BLOCKED:sql_image_digest_mismatch'
    }

    Write-Host "Pinned SQL image not cached; pulling $requiredTag"
    & docker pull $Image | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'BLOCKED:pinned_sql_image_pull_failed' }
    $pulledDigests = @(& docker image inspect $Image --format '{{range .RepoDigests}}{{println .}}{{end}}' 2>$null)
    if ($LASTEXITCODE -ne 0 -or -not ($pulledDigests -ccontains $requiredDigest)) {
        throw 'BLOCKED:sql_image_digest_mismatch'
    }
    Write-Host "Verified pulled SQL image: $requiredDigest"
    return $requiredDigest
}

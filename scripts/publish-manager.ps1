#requires -Version 7.0

[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string[]]$RuntimeIdentifiers = @('win-x64', 'win-arm64'),

    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts\release'),

    [string]$ReleaseKeyRingPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$ProjectRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$Version = (Get-Content -LiteralPath (Join-Path $ProjectRoot 'VERSION') -Raw).Trim()
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

foreach ($rid in $RuntimeIdentifiers) {
    $publishDirectory = Join-Path $ProjectRoot ".build\manager\$rid"
    if (Test-Path -LiteralPath $publishDirectory) {
        Remove-Item -LiteralPath $publishDirectory -Recurse -Force
    }

    $publishArguments = @(
        'publish', (Join-Path $ProjectRoot 'src\ATLWSL.Manager\ATLWSL.Manager.csproj'),
        '--configuration', 'Release', '--runtime', $rid, '--self-contained', 'true',
        '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true',
        '--output', $publishDirectory
    )
    if ($ReleaseKeyRingPath) { $publishArguments += "-p:ReleaseKeyRingPath=$ReleaseKeyRingPath" }
    & dotnet @publishArguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for $rid."
    }
    $sourceTimestamp = [DateTimeOffset]::FromUnixTimeSeconds([long](& git -C $ProjectRoot show -s --format=%ct HEAD)).UtcDateTime
    Get-ChildItem -LiteralPath $publishDirectory -Recurse -Force | ForEach-Object { $_.LastWriteTimeUtc = $sourceTimestamp }

    $archive = Join-Path $OutputDirectory "ATL-WSL-Manager-$Version-$rid.zip"
    if (Test-Path -LiteralPath $archive) {
        Remove-Item -LiteralPath $archive -Force
    }
    Compress-Archive -Path (Join-Path $publishDirectory '*') -DestinationPath $archive -CompressionLevel Optimal
    Get-FileHash -LiteralPath $archive -Algorithm SHA256 |
        ForEach-Object { "$($_.Hash.ToLowerInvariant())  $(Split-Path $archive -Leaf)" } |
        Set-Content -LiteralPath "$archive.sha256" -Encoding utf8NoBOM
}

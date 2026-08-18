#Requires -Version 5.1

[CmdletBinding()]
param(
    [ValidateSet('Install', 'Update', 'Repair', 'Uninstall')]
    [string]$Operation = 'Install',
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._ -]{0,63}$')]
    [string]$DistroName = 'ATL-WSL',
    [string]$InstallLocation,
    [string]$ManifestPath,
    [string]$ManifestUrl = 'https://github.com/LamPPKK/ATL-WSL/releases/latest/download/release-manifest.json',
    [switch]$RemoveDistroData,
    [string]$ConfirmationDistroName,
    [string]$ExportPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$temporary = Join-Path ([IO.Path]::GetTempPath()) ("atl-wsl-" + [guid]::NewGuid().ToString('N'))

function Save-Download([string]$Url, [string]$Destination) {
    if (-not $Url.StartsWith('https://', [StringComparison]::OrdinalIgnoreCase)) { throw "URL must use HTTPS: $Url" }
    Invoke-WebRequest -Uri $Url -OutFile $Destination -UseBasicParsing
}

function Confirm-Artifact($Descriptor, [string]$Path) {
    $hash = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($hash -ne ([string]$Descriptor.sha256).ToLowerInvariant()) { throw "SHA-256 mismatch for $(Split-Path $Path -Leaf)." }
    if ((Get-Item -LiteralPath $Path).Length -ne [long]$Descriptor.sizeBytes) { throw "Size mismatch for $(Split-Path $Path -Leaf)." }
}

function Install-ManagerIntegration([string]$Source, [string]$Version, [string]$DistributionName) {
    $managerRoot = Join-Path $env:LOCALAPPDATA 'Programs\ATL-WSL Manager'
    $target = Join-Path $managerRoot $Version
    if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Recurse -Force }
    New-Item -ItemType Directory -Path $target -Force | Out-Null
    Copy-Item -Path (Join-Path $Source '*') -Destination $target -Recurse -Force
    $executable = Join-Path $target 'ATL-WSL Manager.exe'
    if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw 'Signed manager archive is incomplete.' }
    $shortcutPath = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\ATL-WSL Manager.lnk'
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($shortcutPath)
    $shortcut.TargetPath = $executable
    $shortcut.Arguments = "--distro `"$DistributionName`""
    $shortcut.WorkingDirectory = $target
    $shortcut.Description = 'Manage ATL-WSL runtime and Android packages'
    $shortcut.Save()
}

function Remove-ManagerIntegration {
    $shortcutPath = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\ATL-WSL Manager.lnk'
    $managerRoot = Join-Path $env:LOCALAPPDATA 'Programs\ATL-WSL Manager'
    Remove-Item -LiteralPath $shortcutPath -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $managerRoot -Recurse -Force -ErrorAction SilentlyContinue
}

try {
    if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT -or [Environment]::OSVersion.Version.Build -lt 22000) {
        throw 'ATL-WSL stable requires Windows 11.'
    }
    $architecture = switch ([Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()) {
        'X64' { 'x64' }
        'Arm64' { 'arm64' }
        default { throw 'ATL-WSL stable supports x64 and ARM64 only.' }
    }
    New-Item -ItemType Directory -Path $temporary | Out-Null

    if ($ManifestPath) {
        $resolvedManifest = [IO.Path]::GetFullPath($ManifestPath)
        $resolvedSignature = $resolvedManifest + '.sig'
    }
    else {
        $resolvedManifest = Join-Path $temporary 'release-manifest.json'
        $resolvedSignature = $resolvedManifest + '.sig'
        Save-Download $ManifestUrl $resolvedManifest
        Save-Download ($ManifestUrl + '.sig') $resolvedSignature
    }
    if (-not (Test-Path -LiteralPath $resolvedSignature -PathType Leaf)) { throw 'Detached manifest signature is missing.' }
    $manifest = Get-Content -LiteralPath $resolvedManifest -Raw | ConvertFrom-Json
    if ([int]$manifest.schemaVersion -ne 2 -or [string]$manifest.product -ne 'atl-wsl') { throw 'Unsupported release manifest.' }
    $manager = @($manifest.artifacts | Where-Object { $_.role -eq 'manager' -and $_.architecture -eq $architecture })
    if ($manager.Count -ne 1) { throw "Manifest must contain one $architecture manager artifact." }

    $managerArchive = Join-Path $temporary 'manager.zip'
    Save-Download ([string]$manager[0].url) $managerArchive
    Confirm-Artifact $manager[0] $managerArchive
    $bootstrap = Join-Path $temporary 'manager'
    Expand-Archive -LiteralPath $managerArchive -DestinationPath $bootstrap
    $managerExecutable = Join-Path $bootstrap 'ATL-WSL Manager.exe'
    $authenticode = Get-AuthenticodeSignature -LiteralPath $managerExecutable
    if ($authenticode.Status -ne [System.Management.Automation.SignatureStatus]::Valid) {
        throw "Manager Authenticode signature is not trusted: $($authenticode.StatusMessage)"
    }
    & $managerExecutable --verify-manifest $resolvedManifest $resolvedSignature
    if ($LASTEXITCODE -ne 0) { throw 'Ed25519 release manifest verification failed.' }

    if (-not $InstallLocation) { $InstallLocation = Join-Path $env:LOCALAPPDATA "ATL-WSL\Distros\$DistroName" }
    $headless = @('--headless', $Operation.ToLowerInvariant(), $resolvedManifest, $resolvedSignature)
    switch ($Operation) {
        'Install' { $headless += @($DistroName, [IO.Path]::GetFullPath($InstallLocation)) }
        'Uninstall' {
            if ($RemoveDistroData -and $ConfirmationDistroName -cne $DistroName) { throw "Type $DistroName exactly to confirm permanent removal." }
            $headless += @([string][bool]$RemoveDistroData, [string]$ConfirmationDistroName)
            if ($ExportPath) { $headless += [IO.Path]::GetFullPath($ExportPath) }
        }
    }
    & $managerExecutable @headless
    if ($LASTEXITCODE -ne 0) { throw "$Operation failed. The lifecycle transaction was rolled back or marked degraded." }

    if ($Operation -eq 'Uninstall') {
        Remove-ManagerIntegration
    }
    else {
        Install-ManagerIntegration $bootstrap ([string]$manifest.version) $DistroName
    }
    Write-Host "ATL-WSL $Operation completed." -ForegroundColor Green
}
catch {
    Write-Error $_ -ErrorAction Continue
    exit 1
}
finally {
    if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Recurse -Force -ErrorAction SilentlyContinue }
}

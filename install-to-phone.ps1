# Installs (or updates) DriveDeck on a Windows 10 Mobile phone over Wi-Fi.
# Phone: Settings > Update & security > For developers > Developer mode ON, Device discovery ON.
# Phone and PC must be on the same Wi-Fi. On the first connection you type the pairing code shown on the phone.
# Works both from the source tree (after build.ps1) and from an unzipped release.
param([string]$Ip)

$ErrorActionPreference = 'Stop'

$wadc = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Recurse -Filter WinAppDeployCmd.exe -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match '\\x86\\|\\x64\\' } | Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
if (-not $wadc) { throw "WinAppDeployCmd.exe not found. Install the Windows 10 SDK (10.0.19041 includes it)." }

$appx = Get-ChildItem $PSScriptRoot -Recurse -Filter 'DriveDeck.App_*_ARM.appx' |
        Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $appx) { throw "No DriveDeck package found next to this script. Run build.ps1 or unzip a release first." }

# WinAppDeployCmd cannot handle long paths: map the package folder to a temporary drive letter.
$letter = 'R','Q','W','Y','X','V' | Where-Object { -not (Test-Path "$($_):\") } | Select-Object -First 1
subst "$($letter):" $appx.DirectoryName
try {
    $pkg = "$($letter):\$($appx.Name)"
    $deps = @(Get-ChildItem "$($letter):\Dependencies\arm" -Filter *.appx -ErrorAction SilentlyContinue | ForEach-Object FullName)

    if (-not $Ip) {
        Write-Host "Looking for phones on this network..."
        & $wadc devices
        $Ip = Read-Host "Phone IP address (from the list above, or shown under 'For developers' on the phone)"
    }
    Write-Host "If this PC is not paired yet: on the phone, under 'Device discovery', tap 'Pair' to see a code."
    $pin = Read-Host "Pairing code (just press Enter if already paired)"
    $pinArgs = @(); if ($pin) { $pinArgs = @('-pin', $pin) }

    Write-Host "Installing $($appx.Name) and $($deps.Count) dependencies..."
    & $wadc install -file $pkg -dependency $deps -ip $Ip @pinArgs
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Install failed - trying update of an existing installation..."
        & $wadc update -file $pkg -ip $Ip @pinArgs
    }
}
finally { subst "$($letter):" /d }

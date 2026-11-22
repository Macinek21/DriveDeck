# Builds the ARM release package into .\out and runs the unit tests.
# Requirements: Visual Studio 2022 with the "Universal Windows Platform development" workload,
# Windows 10 SDK 10.0.19041, and the .NET SDK (for the tests).
$ErrorActionPreference = 'Stop'

# Locate MSBuild from any Visual Studio 2022 edition.
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path $vswhere)) { throw "Visual Studio 2022 is not installed." }
$msb = & $vswhere -latest -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
if (-not $msb) { throw "MSBuild not found." }

# Sideload signing: a local self-signed certificate whose subject matches the manifest Publisher.
$cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq 'CN=DriveDeck Dev' -and $_.NotAfter -gt (Get-Date) -and $_.HasPrivateKey } | Select-Object -First 1
if (-not $cert) {
    Write-Host "Creating a local self-signed signing certificate (CN=DriveDeck Dev)..."
    $cert = New-SelfSignedCertificate -Type Custom -Subject "CN=DriveDeck Dev" -KeyUsage DigitalSignature -FriendlyName "DriveDeck sideload signing" `
        -CertStoreLocation "Cert:\CurrentUser\My" -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3", "2.5.29.19={text}") -NotAfter (Get-Date).AddYears(5)
}

# The .NET Native compiler cannot handle long paths, so the folder is mapped to a temporary drive letter.
$letter = 'R','Q','W','Y','X','V' | Where-Object { -not (Test-Path "$($_):\") } | Select-Object -First 1
subst "$($letter):" $PSScriptRoot
try {
    & dotnet test "$($letter):\tests\Core.Tests\Core.Tests.csproj" --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "Unit tests failed" }
    & $msb "$($letter):\src\App\DriveDeck.App.csproj" /restore /p:Configuration=Release /p:Platform=ARM "/p:PackageCertificateThumbprint=$($cert.Thumbprint)" /nologo /v:m
    if ($LASTEXITCODE -ne 0) { throw "Build failed" }
}
finally { subst "$($letter):" /d }

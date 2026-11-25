param()
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$sdkDirectory = Join-Path $projectRoot '.tools\dotnet'
$dotnetPath = Join-Path $sdkDirectory 'dotnet.exe'
if (Test-Path -LiteralPath $dotnetPath) { & $dotnetPath --version; exit $LASTEXITCODE }
$toolsDirectory = Join-Path $projectRoot '.tools'
New-Item -ItemType Directory -Path $toolsDirectory -Force | Out-Null
$archivePath = Join-Path $toolsDirectory 'dotnet-sdk.zip'
Write-Host 'Downloading the pinned .NET 10 SDK from Microsoft...'
Invoke-WebRequest 'https://builds.dotnet.microsoft.com/dotnet/Sdk/10.0.100/dotnet-sdk-10.0.100-win-x64.zip' -OutFile $archivePath
$expectedHash = '24b033418a3969effd49b4651ef7ebbffeb284773b99545d78dce61a82e57f38db7facdb013c609ba15573c072f0e093363ae470824a6847f3c6111078c1fb64'
if ((Get-FileHash -LiteralPath $archivePath -Algorithm SHA512).Hash -ne $expectedHash) { throw 'SDK checksum mismatch. Nothing was installed.' }
Expand-Archive -LiteralPath $archivePath -DestinationPath $sdkDirectory -Force
& $dotnetPath --info
exit $LASTEXITCODE

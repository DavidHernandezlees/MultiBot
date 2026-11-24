$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $root 'app\LicenseServer'
$project = Join-Path $root 'src\MultiAiRobot.LicenseServer\MultiAiRobot.LicenseServer.csproj'
& dotnet publish $project -c Release -r win-x64 --self-contained true -o $out
if ($LASTEXITCODE -ne 0) { throw 'License service publish failed.' }
& (Join-Path $PSScriptRoot 'Build-BetBurger-Tip-Store.ps1')
& (Join-Path $PSScriptRoot 'Build-MultiBotDesk.ps1')

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
Push-Location $projectRoot
try {
    dotnet build MultiAiRobot.sln -c Release --disable-build-servers -m:1
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    dotnet run --project tests/MultiAiRobot.Checks -c Release --no-build -- --server "$projectRoot/src/MultiAiRobot.LicenseServer/bin/Release/net10.0/MultiAiRobot.LicenseServer.dll" --report "$projectRoot/verification/acceptance-results.json"
    if ($LASTEXITCODE -ne 0) { throw 'Acceptance checks failed.' }
} finally { Pop-Location }

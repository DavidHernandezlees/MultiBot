param([switch]$Checks, [string]$OutputDirectory, [string]$DesktopDirectory)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$sourceFiles = (Get-ChildItem -LiteralPath (Join-Path $root 'src\BetBurgerTipStore') -Filter '*.cs').FullName
$outDir = if ($OutputDirectory) { $OutputDirectory } else { Join-Path $root 'app\BetBurgerTipStore' }
[void](New-Item -ItemType Directory -Force -Path $outDir)
$options = @('/nologo', '/optimize+', '/platform:x64', '/target:exe', '/r:System.Net.Http.dll', '/r:System.Web.Extensions.dll')
& $compiler @options "/out:$outDir\BetBurgerTipStore.exe" @sourceFiles
if ($LASTEXITCODE -ne 0) { throw 'Tip store compilation failed.' }
if ($Checks) {
    $checkDir = Join-Path ([IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N'))
    [void](New-Item -ItemType Directory -Path $checkDir)
    $fixtures = Join-Path $root 'tests\BetBurgerTipStore'
    $checkFiles = (Get-ChildItem -LiteralPath $fixtures -Filter '*.cs').FullName
    & $compiler @options "/out:$checkDir\Checks.exe" '/main:MultiAiRobot.Feed.Checks' @sourceFiles @checkFiles
    if ($LASTEXITCODE -ne 0) { throw 'Tip store checks did not compile.' }
    & "$checkDir\Checks.exe" $checkDir $fixtures
    if ($LASTEXITCODE -ne 0) { throw 'Tip store checks failed.' }
    if ($DesktopDirectory) {
        if ([Environment]::Version.Major -lt 10) { throw 'Desktop decoder checks require PowerShell running on .NET 10 or later.' }
        & (Join-Path $root 'tests\BetBurgerTipStore\Check-Desktop.ps1') -DesktopDirectory $DesktopDirectory -PacketDirectory $checkDir
    }
    Write-Output "Test packets: $checkDir"
}

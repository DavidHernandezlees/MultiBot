param([switch]$Checks, [string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$out = if ($OutputDirectory) { $OutputDirectory } else { Join-Path $root 'app\MultiBotDesk' }
[void](New-Item -ItemType Directory -Force -Path $out)
$files = @((Get-ChildItem (Join-Path $root 'src\MultiBotDesk\*.cs')).FullName)
$files += (Get-ChildItem (Join-Path $root 'src\BetBurgerTipStore\*.cs')).FullName
$options = @('/nologo', '/optimize+', '/platform:x64', '/utf8output', '/codepage:65001', '/r:System.Net.Http.dll', '/r:System.Web.Extensions.dll', '/r:System.Drawing.dll', '/r:System.Windows.Forms.dll')
& $compiler @options '/target:winexe' '/main:MultiBot.Desk.DesktopApp' "/out:$out\MultiBotDesk.exe" @files
if ($LASTEXITCODE -ne 0) { throw 'Desktop app build failed.' }
if ($Checks) {
    $scratch = Join-Path ([IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N'))
    [void](New-Item -ItemType Directory -Path $scratch)
    & $compiler @options '/target:exe' '/main:MultiBot.Desk.WindowChecks' "/out:$scratch\WindowChecks.exe" @files (Join-Path $root 'tests\MultiBotDesk\WindowChecks.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Desktop checks did not compile.' }
    & "$scratch\WindowChecks.exe" $scratch (Join-Path $root 'tests\BetBurgerTipStore')
    if ($LASTEXITCODE -ne 0) { throw 'Desktop checks failed.' }
}

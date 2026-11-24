param([Parameter(Mandatory)][string]$DesktopDirectory, [Parameter(Mandatory)][string]$PacketDirectory)
$ErrorActionPreference = 'Stop'
$desktop = (Resolve-Path -LiteralPath $DesktopDirectory).Path
foreach ($name in 'System.Formats.Nrbf.dll','MultiAiRobot.dll') {
    [void][Runtime.Loader.AssemblyLoadContext]::Default.LoadFromAssemblyPath((Join-Path $desktop $name))
}
$receiver = [MultiAiRobot.Services.BetburgerClient]::new()
$received = [Collections.Generic.List[MultiAiRobot.Models.BetburgerInfo]]::new()
$receiver.Method_060002B3([Action[Collections.Generic.List[MultiAiRobot.Models.BetburgerInfo]]] {param($tips) $received.AddRange($tips)})
$dispatch = $receiver.GetType().GetMethod('Method_060002C2', [Reflection.BindingFlags]'NonPublic,Instance')
foreach ($packetName in 'prematch','live','breaking-prematch','breaking-live') {
    $mode = $packetName -replace '^breaking-',''
    $outcome = if ($packetName.StartsWith('breaking-')) { 'Total Over 2.5' } else { 'Over 2.5' }
    $link = if ($packetName.StartsWith('breaking-')) { '' } else { 'FI=1234567&ID=7654321&OD=23/20' }
    $received.Clear()
    $bytes = [IO.File]::ReadAllBytes((Join-Path $PacketDirectory "$packetName.packet"))
    $packet = [MultiAiRobot.Services.Protocol.NetPacket]::ParseBody([BitConverter]::ToUInt16($bytes,2), [byte[]]$bytes[8..($bytes.Length-1)])
    [void]$dispatch.Invoke($receiver, @($packet))
    $tip = $received | Select-Object -First 1
    if ($received.Count -ne 1 -or $tip.Bookmaker -ne 'Bet365' -or $tip.Sport -ne 'Soccer' -or $tip.Percent -ne 3.75 -or $tip.Odds -ne 2.15 -or $tip.IsLive -ne ($mode -eq 'live') -or $tip.Outcome -ne $outcome -or $tip.DirectLink -ne $link) { throw "Published desktop failed to decode $packetName packet." }
    Write-Output "PASS published MultiAiRobot 1.2.0 decoded $packetName tip."
}

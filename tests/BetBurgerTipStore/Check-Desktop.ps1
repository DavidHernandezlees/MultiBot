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
foreach ($mode in 'prematch','live') {
    $received.Clear()
    $bytes = [IO.File]::ReadAllBytes((Join-Path $PacketDirectory "$mode.packet"))
    $packet = [MultiAiRobot.Services.Protocol.NetPacket]::ParseBody([BitConverter]::ToUInt16($bytes,2), [byte[]]$bytes[8..($bytes.Length-1)])
    [void]$dispatch.Invoke($receiver, @($packet))
    $tip = $received | Select-Object -First 1
    if ($received.Count -ne 1 -or $tip.Bookmaker -ne 'Bet365' -or $tip.Sport -ne 'Soccer' -or $tip.Percent -ne 3.75 -or $tip.Odds -ne 2.15 -or $tip.IsLive -ne ($mode -eq 'live') -or $tip.Outcome -ne 'Over 2.5' -or $tip.DirectLink -ne 'FI=1234567&ID=7654321&OD=23/20') { throw "Published desktop failed to decode $mode packet." }
    Write-Output "PASS published MultiAiRobot 1.2.0 decoded $mode tip."
}

# MultiBot

MultiBot provides BreakingBet and BetBurger tip feeds and a local licensing service for MultiAiRobot 1.2.0 on Windows x64. Both collectors save quotes to SQLite and serve the desktop over TCP.

## Open the Windows app

Download and open [MultiBotDesk.exe](https://raw.githubusercontent.com/DavidHernandezlees/MultiBot/main/app/MultiBotDesk/MultiBotDesk.exe) on Windows x64. It opens your latest 50 saved tips. To collect new tips, enter your BreakingBet access key ID and secret, choose a feed, and click **Start server**. Connect MultiAiRobot to the address displayed in the app. **Stop server** or closing the window releases the connection. The app keeps your credentials in memory.

## Start the BreakingBet tip server from the console

Run `07-Start-BreakingBet-Tip-Server.cmd`, choose pre-match or live, and enter your BreakingBet access key ID and secret. In the desktop Tip Server settings, connect to `127.0.0.1:14010`. BreakingBet tips appear with their market, odds and overvaluation percentage. Run `08-View-BreakingBet-Tips.cmd` to view history. [Setup, filter options and desktop compatibility](BREAKINGBET_SETUP.md)

## Start the BetBurger tip store

1. Run `05-Start-BetBurger-Tip-Store.cmd`. The compiled tip store is included.
2. Select pre-match or live and enter your BetBurger multifilter ID and API token at the local prompts.
3. In your existing desktop app, open **Settings → Tip Server**, enter `127.0.0.1` and port `14010`, and select the matching bookmaker and feed mode.
4. Run `06-View-BetBurger-Tips.cmd` to inspect the saved quotes. For an offline check, import the included sample response using the commands in [the setup guide](BETBURGER_SETUP.md).

## Run the licensing service

With the .NET 10 SDK installed, run `.\scripts\Publish-Windows.ps1` in PowerShell. Then run `01-Create-Administrator.cmd` once, start `02-Start-License-Server.cmd`, and open `03-Open-License-Manager.cmd`. The local console runs at `http://127.0.0.1:5173/`. The [API reference](docs/API.md) describes account, license and session operations.

## Verify the build

Run `.\scripts\Build-BetBurger-Tip-Store.ps1 -Checks` on Windows to compile and test the collector. With PowerShell on .NET 10, add `-DesktopDirectory "D:\path\to\app\Desktop"` to test packet compatibility against your installed desktop DLL. Run `.\scripts\Run-Checks.ps1` with the .NET 10 SDK and .NET Framework 4.8 targeting support to build the solution and run the license service acceptance tests.

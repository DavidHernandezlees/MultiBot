# BetBurger tip store for MultiAiRobot 1.2.0

Run the collector on the same Windows x64 PC as MultiAiRobot. It writes BetBurger Valuebets V2 results to SQLite even while the desktop app is closed. You can check feed quality and bookmaker coverage before starting a desktop simulation.

## Connect your feed

1. In your BetBurger API account, select a live or pre-match Valuebets subscription and create a multifilter for the bookmaker, region, sports and markets you intend to use. Copy the numeric filter ID from My Account. Keep the private API token available for the local prompt. [BetBurger setup guide](https://www.betburger.com/manual/how-to-deal-with-api)
2. Open your local MultiBot checkout. Double-click `05-Start-BetBurger-Tip-Store.cmd`. Choose the matching feed mode, enter the filter ID (or several IDs separated by commas) and paste your API token when prompted. The interactive prompt hides the token. Keep the console open.
3. Check the console for the number of results and eligible tips. A successful poll can return zero results. Run `06-View-BetBurger-Tips.cmd` to see the latest 50 stored valuebets with their event, selection, price, edge and status.
4. To receive tips in MultiAiRobot, start the local license service and desktop app as described in [the project README](README.md). In **Settings → Tip Server**, enter **127.0.0.1** and **14010**. The license service has its own port, **5173**.
5. In the desktop app, select the bookmaker and feed mode that match the multifilter. Enable **Simulation mode (no real bets)**, save and start the bot. Its log should show a tip-server connection. When there is a fresh matching result, the collector reports it as forwarded and the desktop shows the tip or explains why it was filtered.

The integration reads the API using your local BetBurger token. The desktop continues to use its own license and bookmaker settings. A new BetBurger login session can invalidate the old API token. If the collector reports HTTP 401, restart it with the current token. HTTP 400 indicates a request/filter problem. HTTP 402 or 403 means the credentials or subscription do not grant the requested access.

## What is stored

Open `%LOCALAPPDATA%\MultiAiRobot\BetBurger\tips.sqlite3` with a SQLite viewer. The `tips` table keeps one row per feed mode and provider valuebet ID. Each row contains the latest odds, edge, quote time, selection label, event, two JSON columns with the provider’s quote and event, and the first and latest observation times. Repeated polls update that row. The `deliveries` table records desktop delivery attempts separately.

Every identified quote is stored, including paused, stale and unmapped quotes. Delivery uses the current poll, a matched bookmaker event, a positive edge, a fresh price and a selection-specific direct link. The event’s BetBurger ID is distinct from the bookmaker’s own selection ID. The collector passes through the bookmaker links supplied in the response. A missing selection link is visible as **incomplete selection** in history.

The desktop protocol has no acknowledgement. The store records the target before sending it. If the connection fails mid-send, a tip can be missed. This prevents a reconnect or a second reference bookmaker from causing an automatic duplicate for the same target. Delivery history records attempts, and the desktop log shows what it accepted or skipped.

## Bookmakers and markets

Built-in bookmaker labels cover Bet365, PaddyPower, TonyBet, Bwin, Stoiximan, Goldenpalace, Betvictor and the listed regional Bwin/Betano variants. The desktop uses its existing bookmaker and clone rules. Confirm that the selection link works with the regional site you use; a shared bookmaker name does not establish that two sites use the same selection IDs.

The standard outcomes include match winners, 1X2, draw no bet, double chance, handicaps and totals. Other variations retain their provider market ID and line. The store keeps the full provider row, including player and period identifiers. To extend the labels, add `labels.json` beside `app\BetBurgerTipStore\BetBurgerTipStore.exe` and restart. Use BetBurger's current IDs and use `{line}` for a numeric market parameter. [BetBurger ID reference](https://www.betburger.com/api/entity_ids)

```json
{
  "bookmakers": { "10": "Bet365" },
  "sports": { "7": "Soccer" },
  "markets": { "19": "Total over {line}" }
}
```

The published desktop 1.2.0 reader uses the same incoming flag for live mode and its diagnostic lay label. The collector excludes actual lay quotes and sets the mode bit the reader expects. A live back quote can therefore appear as `isLay=True` in that version's diagnostic log. The SQLite provider payload preserves the original lay flag.

## Test with a saved response

From the repository root, these PowerShell commands import a synthetic response into a separate temporary database and show the stored row. Import runs offline and does not open a tip listener. The sample has a historical timestamp, so its status reads **stale quote**.

```powershell
$env:BETBURGER_DATA_DIR = Join-Path $env:TEMP 'MultiAiRobot-BetBurger-example'
.\app\BetBurgerTipStore\BetBurgerTipStore.exe --import .\tests\BetBurgerTipStore\sample.json prematch
.\app\BetBurgerTipStore\BetBurgerTipStore.exe --history
Remove-Item Env:BETBURGER_DATA_DIR
```

## Configuration

| Environment variable | Default or behavior |
| --- | --- |
| `BETBURGER_API_TOKEN` | Hidden prompt when absent. The collector keeps the token in process memory. |
| `BETBURGER_FILTER_IDS` | Prompt for one or more numeric multifilter IDs. |
| `BETBURGER_MODE` | Prompt. Pressing Enter selects `prematch`; `live` selects the live API. |
| `BETBURGER_PORT` | 14010 on IPv4 loopback. One desktop connection per running collector. |
| `BETBURGER_POLL_SECONDS` | 5 seconds. Transient failures back off and HTTP 429 respects Retry-After. |
| `BETBURGER_MAX_AGE_SECONDS` | 300 for pre-match and 30 for live, measured from the provider update time. |
| `BETBURGER_DATA_DIR` | `%LOCALAPPDATA%\MultiAiRobot\BetBurger` |

## Build and verify

This repository includes the x64 executable and C# source. The collector uses .NET Framework 4.8 and the SQLite engine included in Windows 11. From the repository root run `powershell -File .\scripts\Build-BetBurger-Tip-Store.ps1 -Checks`. To also check the desktop decoder, use PowerShell on .NET 10 and pass `-DesktopDirectory` with the path to your existing MultiAiRobot 1.2.0 desktop binaries. Run `scripts/Publish-Windows.ps1` to publish the license service and build the tip store.

The API uses a form-encoded POST to `/api/v2/valuebets/bot_pro_search` with `access_token` and repeated `search_filter[]` fields. [Pre-match API](https://rest-api-pr.betburger.com/doc.html) · [Live API](https://rest-api-lv.betburger.com/doc.html)

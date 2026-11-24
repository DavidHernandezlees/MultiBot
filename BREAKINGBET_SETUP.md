# BreakingBet tip server

## Start on Windows x64

1. Run `07-Start-BreakingBet-Tip-Server.cmd` from the checkout. Choose `prematch` or `live`, then enter your BreakingBet access key ID and secret key at the local prompts. Your key needs Valuebets access for the selected mode. The secret prompt hides your input.
2. In MultiAiRobot 1.2.0, open **Settings → Tip Server** and set the host to `127.0.0.1` and port to `14010`. Select a bookmaker, sport and feed mode covered by your filter. Only one server can use that port at a time.
3. Keep the server console open. It shows the number of items received, eligible and forwarded. Run `08-View-BreakingBet-Tips.cmd` to see the latest 50 stored tips, including quotes received while the desktop was disconnected.

The server reuses the active BreakingBet filter for your access key and feed. If that filter is missing or expires, it creates a default filter. To apply specific criteria, set `BREAKINGBET_FILTER_FILE` to a local JSON file before launching. Providing a file updates the remote filter for that key and feed. For example, this filter selects Bet365:

```json
{"bookmakers":{"39":{"allowed":true}}}
```

## What the desktop receives

The server joins each valuebet to its event, bookmaker and market. It sends decimal odds, BreakingBet's overvaluation percentage, event time, market and period. BreakingBet supplies bookmaker-specific `crumbs`. The server retains those in SQLite and leaves the legacy desktop betslip link empty. Eligible tips have the status `display only` in history. A desktop bookmaker panel may show **No betslip link**.

The server saves identified items even when they are masked, stale, unmapped or ambiguous. It forwards fresh, unmasked, single-outcome back quotes with a positive overvaluation from the selected feed. When an odd omits its last price-change time, the server uses the provider's snapshot time. When the odd supplies that time, both the odd and the snapshot must be fresh. Timestamps without an offset are interpreted as UTC. The desktop receives each eligible selection at most once across polls and restarts. The TCP protocol has no delivery acknowledgement.

## Configuration

| Environment variable | Purpose |
| --- | --- |
| `BREAKINGBET_ACCESS_KEY_ID`, `BREAKINGBET_SECRET_KEY` | Optional alternatives to the local credential prompts |
| `BREAKINGBET_MODE` | `prematch` or `live` |
| `BREAKINGBET_FILTER_FILE` | Optional JSON filter to apply at startup and restore after expiry |
| `BREAKINGBET_PORT` | Local TCP port, default `14010` |
| `BREAKINGBET_POLL_SECONDS` | Seconds between polls, default 5, minimum 2 |
| `BREAKINGBET_MAX_AGE_SECONDS` | Freshness limit, default 300 for pre-match and 30 for live |
| `BREAKINGBET_DATA_DIR` | Defaults to `%LOCALAPPDATA%\MultiAiRobot\BreakingBet`. Contains `tips.sqlite3` and the English dictionary cache |
| `BREAKINGBET_DICTIONARY_FILE` | Offline import only. Defaults to the cached dictionary in the data directory |

The server renews its bearer token after four minutes and caches English dictionaries for six hours. HTTP 202 means a filter is warming up. HTTP 429 triggers a delay and honors the provider's Retry-After header. HTTP 401 or 403 after token renewal usually requires checking the key or feed subscription. [Provider workflow](https://api.breaking-bet.com/workflow.html) · [Valuebets schema](https://api.breaking-bet.com/specs/en/valuebets.yaml) · [Rate limits](https://api.breaking-bet.com/rate-limits.html)

## Offline check

From the repository root, use these PowerShell commands to import the synthetic example and read it back. The sample has a fixed snapshot time, so a later import records a stale quote. Import only writes history.

```powershell
$env:BREAKINGBET_DATA_DIR = Join-Path $env:TEMP 'MultiBot-BreakingBet-example'
$env:BREAKINGBET_DICTIONARY_FILE = '.\tests\BetBurgerTipStore\breakingbet-dictionaries.json'
.\app\BetBurgerTipStore\BetBurgerTipStore.exe --breakingbet --import .\tests\BetBurgerTipStore\breakingbet-items.json prematch
.\app\BetBurgerTipStore\BetBurgerTipStore.exe --breakingbet --history
Remove-Item Env:BREAKINGBET_DATA_DIR, Env:BREAKINGBET_DICTIONARY_FILE
```

Run `.\scripts\Build-BetBurger-Tip-Store.ps1 -Checks` to rebuild the included executable and test both providers. Under PowerShell on .NET 10, add `-DesktopDirectory 'D:\path\to\app\Desktop'` to verify all four desktop packet fixtures.

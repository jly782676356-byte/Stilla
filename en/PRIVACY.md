# Privacy: what Stilla reads, what it writes, and when it uses the network

Applies to version 0.1.1. The source is in this repository and every statement below can be matched to the code (file paths in brackets).

## Network

| When | Network? |
|---|---|
| **Quota source = status line**, or "Auto" (the default) while the status line has fresh data | This item does **not** use the network and **does not read the login token**. No telemetry, no crash reporting. |
| **Update check** (on by default, can be turned off in Settings) | About 4 seconds after each launch it sends **one GET request** to `https://api.github.com/repos/<owner>/<repo>/releases/latest`, asking only for "the version number and page address of the latest Release". The request carries only `User-Agent: Stilla/<version>` and `Accept: application/vnd.github+json`: **no cookies, no token, none of your data** (GitHub can of course see your IP address, as with any website). Nothing is downloaded or installed automatically; a link returned by the API is only used if it starts with `https://github.com/`. To turn it off: untick it in Settings, or set the environment variable `STILLA_NO_UPDATE_CHECK=1`. If you fork the project and leave the repository address unchanged, your build checks this project for new versions; `tools\set-repo.ps1` points it at your own repository. (`src/Update.cs`) |
| Online query, or "Auto" when the status line has no fresh data (**both need your confirmation in a dialog**; "Auto" is the default source, so the first time the status line has no data you are asked once; "No" means the token is never read and the network is never used, and you are not asked again; the answer is stored as `onl=` in `settings.txt`) | Requests `https://api.anthropic.com/api/oauth/usage` with the Claude login token of this machine (`Bearer`). Once every 60 seconds, read-only, sends no messages. The token is only used in memory; it is not saved and not sent anywhere else. (`UsageClient` in `src/Main.cs`) |

E-mail, cloud sync, crash reports, usage statistics: none. "Go to update page" and "Donate..." simply open a web address in your default browser.

## Local files it reads (read-only, opened in shared mode, so Claude Code is never blocked from writing)

| File | What is read | When |
|---|---|---|
| `<Claude data folder>\projects\**\*.jsonl` (session logs) | Lines that contain `usage` are parsed: timestamp, session ID, message ID, model name, the token counts, thinking effort, working directory (used to show the project name), session title (`ai-title` / `custom-title`). **Each whole JSON line is parsed into memory, so the conversation text passes through memory too, but the program does not take it, store it or upload it.** | When "Show usage statistics" is on, or while "Log usage to file" is on |
| `<Claude data folder>\sessions\*.json` | Session name and where the name came from | Same, every 30 seconds |
| `<Claude data folder>\.credentials.json` | To recognize the account / plan: `organizationUuid`, `subscriptionType`, `rateLimitTier`. **The `accessToken` is only taken out after you agreed to the online query ("Yes" in the dialog, or choosing "Online query" by hand), to call the endpoint** | Every 30 seconds |
| `~\.claude.json` (or `<Claude data folder>\.claude.json`) | `oauthAccount`: e-mail (only the masked form such as `a***9@example.com` is stored and shown), subscription creation time, organization type | Every 30 seconds |
| VS Code `state.vscdb` (under `%APPDATA%\Code` / `Code - Insiders` / `Cursor` / `VSCodium` / `Windsurf`) | Opened read-only; takes the session tab titles from the `Anthropic.claude-code` key | Every 10 minutes, through `scripts\sync_vscode_titles.py` (needs Python; not run without it) |

"Claude data folder": `CLAUDE_CONFIG_DIR`, or `~\.claude`, plus the folders detected automatically and the ones you added by hand. Right-click → Claude data folders shows all of them.

## Files it writes

All in `%APPDATA%\Stilla\` (portable mode: `data\` next to `Stilla.exe`):

| File | Contents |
|---|---|
| `settings.txt` | Window position, size, transparency, skin, options (plain numbers; including `usage=` simple / detailed mode, `src=` quota source and `onl=` whether the online query was agreed to) |
| `rate_limits.json` | Written by the status-line helper: the 5-hour / weekly percentages and reset times |
| `roots.txt` | The Claude data folders you added by hand |
| `update.txt` | The latest version number last seen, the update page address, which version you have already been notified about, the time of the last check |
| `statusline-chain-*.txt` | Your original status-line command (used for chaining), one per config folder |
| `logs\usage_detail_v2.csv`, `usage_tags.csv` | One row per API message: time, **session ID, project name (from the working directory)**, model, the token counts, task ID, tool call count / names, thinking effort |
| `logs\session_index.csv` | One row per session: **title, start / end time, session file path**, token totals |
| `logs\quota_snapshots.csv` | Snapshots of the 5-hour / weekly percentages and reset times |
| `logs\accounts.csv`, `account_timeline.csv` | Your account profile: name, plan, subscription start date, monthly fee, **the bound organization ID, masked e-mail**, switch history |
| `logs\quota_conversion*.csv / .txt`, `vscode_titles.csv`, `read_progress*.txt` | Estimation coefficients, the VS Code title cache, read progress |

**These CSV files contain your session titles, project paths, organization ID and subscription details. Do not paste or upload the `logs` folder when you report a problem.** To remove everything: delete the whole `%APPDATA%\Stilla` (the uninstaller can do this for you if you choose so).

### Files it changes only with your consent

The `statusLine` entry in `~\.claude\settings.json` (or `<CLAUDE_CONFIG_DIR>\settings.json`): only changed when you tick it in the installer or click "Yes" in the settings dialog. Before changing it, a backup is made as `settings.json.stilla-backup-<time>`; if the JSON cannot be parsed it is left alone; your existing status line is kept as is (chained); uninstalling or "Undo the status line setup" restores it. Stilla **never modifies** the session logs, `.credentials.json` or `.claude.json`, and never refreshes or rewrites the login token.

## Other programs it starts

- `python` (in the background, hidden window, optional): runs `scripts\quota_fit.py` (quota conversion, once an hour) and `scripts\sync_vscode_titles.py` (every 10 minutes). The scripts are purely local and use no network.
- `explorer.exe`: when you click "open the logs folder / open the session log".

## Other

- System notifications (tray balloons): account switches, subscription renewal reminders.
- The installer writes its uninstall information under `HKCU` (current user) and, if you tick "start with Windows", a `Run` entry; no administrator rights are needed, and the uninstaller removes both.

# Changelog

## 0.1.1

**New: English-UI edition**
- Installers now come in two UI languages: `Stilla-Setup-0.1.1-zh.exe` (Chinese) and `Stilla-Setup-0.1.1-en.exe` (English), each with a portable zip. Both editions have exactly the same features.
- The English edition's source lives in the [`en/`](.) folder of the repository: UI text and code comments are translated, plus 11 English-only layout adjustments (wider buttons, re-balanced columns and so on).
- The two editions name their data files differently (`usage_detail_v2.csv` vs `用量明细_v2.csv`), so switching edition on one machine never reads the other's history. Both install to the same place: the later install replaces the earlier one, so only one can be installed at a time.

**Other**
- `LICENSE` is now the unmodified MIT text, which GitHub recognises; the "not affiliated with Anthropic" notice moved to `NOTICE`.
- README: two demo animations, SmartScreen guidance and a SHA-256 check; one outdated sentence in `PRIVACY.md` corrected.
- No functional change to the program itself; the version number goes to 0.1.1.

## 0.1.0 (first public release)

Cleaned up from the author's internal build (skins, usage panel, quota conversion, accounts and subscriptions were already there). Changes made for the public release:

**How the numbers are obtained**
- New: Claude Code's official `statusLine` as a source, through a console helper `Stilla.Statusline.exe` (reads no login token, no network).
- The default quota source is the new "Auto": status line first; the online query is only used when the status line has no fresh data, and **only after the user agrees in a dialog** (it reads the login token and calls an undocumented endpoint, with a terms notice; the answer is stored as `onl=` in `settings.txt`, and "No" keeps status-line only and is not asked again).
- The former "read the token + undocumented endpoint" is kept as the optional "Online query", with a risk warning when you switch to it by hand; "Status line only" can be chosen too.
- One-click write / undo of the `statusLine` entry in `settings.json`: backup, an existing status line is kept (chained), nothing is touched when the JSON cannot be parsed.

**Works on different computers / different clients**
- Several Claude data folders are detected automatically (`CLAUDE_CONFIG_DIR`, `~\.claude`, side-by-side `.claude*` folders, desktop-app / Store-version folders) and can be added by hand.
- Data now lives in `%APPDATA%\Stilla` (no longer next to the exe, so an install under `Program Files` can write too); portable mode is supported.
- The VS Code title sync supports Insiders / Cursor / VSCodium / Windsurf.
- A single-file installer `Stilla-Setup-0.1.0-en.exe` (the Chinese-UI edition is `-zh.exe`): no administrator rights, silent install possible, clean uninstall.

**UI**
- It now opens in **detailed mode** (quota + usage panel), so everyone sees all core features at once; a new "▴ Quota only / ▾ Show details" button under the quota card switches between simple mode (quota only) and detailed mode, the same in all 16 skins, in the same place, and the choice is remembered.
- The project is renamed Stilla; the word "Claude" is gone from the title.
- Fixed display problems in several skins: the account tag covering the title (2-B, 3-J); "100%" cut to "1…" and the "not started" text truncated (3-J, 3-I, 2-A, 2-C, 2-E, 3-H; the font now shrinks first when it does not fit); error / no-data / stale-data messages invisible in 2-D, 3-F, 3-G, 3-H, 3-J; three-digit numbers piling up in 2-C; the view buttons of 2-D truncated; the reset-time row of 3-F covered by the bottom bar; the "Reload" and "×" of 3-G struck through by the border line; the footer of 3-I touching the card edge; label values in all skins now shrink when they do not fit.
- The "DeepSeek estimate row" is hidden by default (it can be switched on in the right-click menu).

**Settings window, update reminder, donate**
- A new "Settings…" window (right-click menu / tray menu / click the red dot).
- Automatic update check: once at every launch by default (can be turned off); when a new version exists a small red dot appears to the right of the Stilla mark (each of the 16 skins has its own position) and the tray notifies once; "Go to update page" opens the Release page in the browser and nothing is downloaded or installed automatically. Only the latest version number is requested from GitHub, with no user data.
- Donate entry: Settings → Donate… opens the `#donate` section of the README.
- `tools\set-repo.ps1`: replaces the repository placeholder `OWNER` with your user name in one step; `tools\make-release.ps1`: collects the Release files (installer, portable zip, SHA256, notes).
- The build now also produces a portable zip; installer / zip file names carry a language suffix (`-zh` / `-en`).

**Other**
- `quota_fit.py` no longer fails on its first run when there is no data yet.
- A UI rendering test bench `tools\ui-check` (fake data, no window pops up).

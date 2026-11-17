# Changelog

## 0.2.0

A batch of features from the author's own build is now in the public version: session status lights and reminders, jumping to a session's window, speed and the cap forecast, a weekly report, the API and Trend views, three profiles, hover tips and more. If you upgrade from 0.1.x, read the "Upgrade notes" at the end first.

**New: session state and reminders**
- **One status light per session**: green = running (being written to), blue = waiting for you (permission prompt, question, etc.), and the closer the cache is to expiring the more yellow, then red. The state comes from the session state Claude Code writes into `sessions\*.json`; that file is not documented, so if its format changes the lights may be wrong.
- **System notification before the cache expires**: the lead time is set in ⋮ → Reminder settings (5 / 10 / 15 / 20 / 30 minutes, default 10; `alertmin` in `settings.txt`). There is also a **quota cap warning**: one notification when the 5-hour / weekly quota reaches 70 / 80 / 90 / 95% (default 90%, `limitwarn`).
- **Email reminder (optional, off by default)**: tick the sessions you want to watch and how many minutes ahead, and Stilla sends you an email when their cache is about to expire. Bring your own SMTP server: Stilla connects straight to the server you typed in, and the password is stored DPAPI-encrypted in `mail.txt`. The mail contains the session title, the time left and so on; see `PRIVACY.md`.
- **↗ jump**: click the ↗ right of a session name in the Session view to go to the window it lives in. For a VS Code extension session: finds the VS Code window by folder name, then uses `vscode://anthropic.claude-code/open?session=<session ID>` to bring the extension back to that session. For a terminal session: brings the parent process's window to the front (a tab in Windows Terminal cannot be selected).

**New: speed, forecast, statistics**
- The Session view gets two new columns, "Speed" and "Share": how fast a session used quota over the last N minutes (15 by default; 5 minutes to 48 hours), and how much it used in total; sessions that have gone idle show their numbers from when they were last active, in gray. Under the Total row there is a new **cap forecast**: at the current speed, in about how many hours the 5-hour quota hits the cap, and whether the weekly quota runs out before the reset. These need the quota conversion (optional, needs Python).
- **Click a column header to sort** (Session, Model and Compare views): the first click sorts descending, the second reverses it, the third restores the default order.
- **Click a session in the Model view**: a popup with its numbers per "model × tier" and a timeline of mid-session switches; it can be copied as text.
- **API view**: lists the usage and estimated cost of non-Claude models (for example DeepSeek) found in the local logs.
- **Trend view**: bar chart of the quota used per hour over the last 24 hours / per day over the last 7 days.
- **Weekly report**: an HTML report for the previous week is generated automatically (right-click → Weekly report generates one by hand): daily cost, hourly heatmap, model × tier, the sessions that burned the most quota, change vs. last week, an estimate of the cost of reloads after caches expired. Saved in `%APPDATA%\Stilla\logs\weekly_reports\`.
- **Estimate self-check**: a small hint in the Total row compares the estimate with how far the quota reading actually rose and warns on a big deviation; right-click → Estimate self-check details... shows the details.
- **Export**: ⋮ → Export data of the current time tab: the session table or the model × tier details, copied to the clipboard or saved as CSV.
- Amounts converted at official list prices (API equivalent) now appear in the hover tip of a session name, in exports, in the model detail popup, in the Plan view, in the Trend view and in the weekly report.

**New: settings and conveniences**
- **Three profiles**: Everything / Recommended / Minimal decide with one click which background features, views and columns are on and how often background reading happens; "Features and display settings..." lets you adjust every item, and a changed set becomes "Custom".
- **Pause background reading (for gaming)**: one click in the right-click / tray menu; the quota reading goes on.
- **Hover tips**: rest the mouse on something and a small note appears explaining it (on by default, can be switched off in the right-click menu).
- Time tab presets and saved favorite ranges, window size presets and row spacing are all in the ⋮ menu at the right end of the time tabs.

**Changed**
- **Views renumbered**: the old "Tier" view was merged into "Compare"; the views are now Session · Model · Compare · Plan · API · Trend.
- **New installs start on the "Recommended" profile**: all features on, but the "Input" column, the API view and the DeepSeek cost row are hidden (switch them on in "Features and display settings...", or pick "Everything"). The right-click item "Show DeepSeek estimate row" moved into "Features and display settings..."; if you had switched it on in 0.1.x it stays on after the upgrade (the profile then reads "Custom").
- With the usage panel closed, an enabled cache reminder now also reads the newly added part of the session logs every ten-odd seconds (0.1.x had no such feature); to make Stilla read no session files at all, see "I do not want to be tracked" in the README FAQ.
- In the source, `Widget` is now split over several files (`Features.cs`, `Extras.cs`, `WidgetExtra.cs`, `Tips.cs`, `Details.cs`, `Jump.cs`, `Mail.cs`, `Report.cs`); it is still one partial class.
- The test bench `tools\ui-check` gets layout-check scenarios (`run.ps1 -Scenario q_all` lists clipped / overlapping / border-touching text). The logic tests (`run-logic.ps1`) exist only in the Chinese tree (the repository root).

**Unchanged**
- Reading the quota through the status line (`Stilla.Statusline.exe`), the three ways of choosing the quota source with "Auto" as the default, the update check, the donate entry.
- The login token is not read unless you agree to the online query, and nothing is uploaded. The only new network use in 0.2.0: after you turn on the email reminder yourself, mail is sent to the SMTP server you typed in.

**Upgrade notes**
- Just run the new installer; the settings and history in `%APPDATA%\Stilla` are kept.
- The window size saved by 0.1.x is dropped once (the new panel is wider, and keeping the old size would squeeze out the new columns) and the column widths go back to the defaults; the view you were on is mapped to the new numbering.
- More detail in the last section of `docs/USAGE.md`, "Upgrading to 0.2.0".

**Known issues in 0.2.0** (not fixed in this release):
- A session title containing a line break stops the cache email from being sent (the title goes into the subject); a failure notice appears instead.
- The weekly report can be generated with no data in it when the usage history has no entries for the previous week.
- The email form does not check address syntax until sending.

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

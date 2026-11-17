# Privacy: what Stilla reads, what it writes, and when it uses the network

> **中文:** [PRIVACY.md (Chinese edition)](../PRIVACY.md)

Applies to version 0.2.0. The source is in this repository and every statement below can be matched to the code (file paths in brackets).

**What 0.2.0 adds compared with 0.1.1:**

- Network: **one more place, and it is off by default**: once you turn on "Email reminders..." yourself, Stilla sends mail to **the SMTP server you typed in** (what the mail contains is listed below). The other network uses (the update check and the online query) are the same as in 0.1.1.
- Reading: it also reads the session state Claude Code writes (`status` and a few other fields in `sessions\*.json`); at the moment you click ↗ it reads the titles of VS Code windows and the parent / child relation of processes; it reads Stilla's own logs back (for speed, trend and weekly report) and `mail.txt`.
- Writing: `mail.txt`, the weekly report HTML, files you export yourself, and an `error.log` that only appears if you set `STILLA_DEBUG=1`; `settings.txt` has more options.
- Unchanged: the login token is not read unless you agree to the online query, nothing is uploaded, there is no telemetry or crash reporting, and Claude Code's session logs are never modified.

**What the 0.2.0 features read, write and send**

- **Email reminders (off by default).** Stilla connects to an SMTP server only after you tick "Enable email reminders" in the "Email reminders" dialog (right-click → "Email reminders...", or ⋮ → Reminder settings → "Email reminders...", or the @ in the Session view header), fill in the server / port / user name / password / recipient, and tick at least one session. It connects to **the server you typed in** (nothing reaches the Stilla project). The "Send test email" button is a separate path: only when you click it, with the form filled in, one fixed test message is sent (this works even while reminders are not enabled). Mail is sent with .NET's built-in `SmtpClient`, using STARTTLS (TLS 1.2) only; Stilla does not change the certificate checks (the .NET defaults apply). If you untick "STARTTLS / SSL" (and the server is not this machine), Stilla asks you to confirm first, because the password and the mail would travel in clear text; with port 465 ("direct SSL", which this component does not support) it also asks first. At most 30 mails per hour (test mails included). The mail contains: the session title (in full), minutes left and the expiry time of the prompt cache, whether the session is waiting for you, the context size, the estimated cost of reloading after expiry, the account ID and plan name, and the send time. It does **not** contain conversation text, project paths, login tokens or your login e-mail (a session without a title shows "project folder name · first characters of the session ID"). A title can reveal what you are working on, and it passes through your mail provider.
- **Mail settings** live in `%APPDATA%\Stilla\mail.txt` (portable mode: `data\mail.txt`): server, port, user name, sender, recipient and the ticked sessions' IDs and titles are **plain text**; the **password** is encrypted with Windows DPAPI (only the same Windows account on this PC can decrypt it, and it is bound to server + user name: change either and it can no longer be decrypted). The password is never written to a log, the clipboard or the UI; while a mail is being sent it is briefly held in memory in clear text, as it has to be to log in.
- **The ↗ jump.** Only at the moment you click: Stilla goes through the visible windows (class name and title length of each; for windows of the Chromium class `Chrome_WidgetWin_1` it also looks up the process name, and only for processes whose name starts with `Code` does it read the window title, to find the window by folder name) and, for terminal sessions, the parent / child relation of system processes (process IDs only, not process memory and not command lines); then it asks Windows to open `vscode://anthropic.claude-code/open?session=<session ID>`, which your local VS Code handles. What it reads is used once in memory, never saved or sent.
- **Weekly report.** By default one report for the previous week is generated automatically (not in the Minimal profile; you can also generate it by hand from the right-click menu → Weekly report) and saved to `%APPDATA%\Stilla\logs\weekly_reports\weekly_report_<Monday date>.html`. It is a self-contained HTML file with a content-security policy (no scripts, no external requests) and needs no network; everything that comes from sessions is HTML-escaped. It contains money / quota conversions, usage per day and per hour, model × tier, **the 10 most expensive sessions (with titles and time spans)** and account / plan switches. **The file contains your session titles; look at it before you send it to anyone.**
- **Export.** "...save as CSV..." (in the export menu) opens `logs\export` as the starting folder; where the file goes is your choice; "...copy to clipboard" only uses the clipboard. Exported / copied text contains session titles and IDs; text starting with `=`, `+`, `-` or `@` gets a leading apostrophe so Excel does not run it as a formula.

## Network

| When | Network? |
|---|---|
| **Quota source = status line**, or "Auto" (the default) while the status line has fresh data | This item does **not** use the network and **does not read the login token**. No telemetry, no crash reporting. |
| **Update check** (on by default, can be turned off in Settings) | About 4 seconds after each launch it sends **one GET request** to `https://api.github.com/repos/<owner>/<repo>/releases/latest`, asking only for "the version number and page address of the latest Release". The request carries only `User-Agent: Stilla/<version>` and `Accept: application/vnd.github+json`: **no cookies, no token, none of your data** (GitHub can of course see your IP address, as with any website). Nothing is downloaded or installed automatically; a link returned by the API is only used if it starts with `https://github.com/`. To turn it off: untick it in Settings, or set the environment variable `STILLA_NO_UPDATE_CHECK=1`. If you fork the project and leave the repository address unchanged, your build checks this project for new versions; `tools\set-repo.ps1` points it at your own repository. (`src/Update.cs`) |
| Online query, or "Auto" when the status line has no fresh data (**both need your confirmation in a dialog**; "Auto" is the default source, so the first time the status line has no data you are asked once; "No" means the token is never read and the network is never used, and you are not asked again; the answer is stored as `onl=` in `settings.txt`) | Requests `https://api.anthropic.com/api/oauth/usage` with the Claude login token of this machine (`Bearer`). Once every 60 seconds, read-only, sends no messages. The token is only used in memory; it is not saved and not sent anywhere else. (`UsageClient` in `src/Main.cs`) |
| **Email reminders** (**off by default**; you have to tick "Enable email reminders" in the "Email reminders" dialog, fill in server, user name, password and recipient, and tick at least one session) | Connects with .NET's built-in `SmtpClient` to **the SMTP server you typed in** (the "STARTTLS / SSL" box is ticked by default; time-out 25 seconds), logs in with the user name and password you typed in, and sends two kinds of mail: the reminder when a ticked session's cache time left drops to the number of minutes you set, and the test mail when you click "Send test email" (this one needs no prior enabling, only that you click it with the form filled in). **No other server is involved and the Stilla project receives nothing.** At most 30 mails per hour (test mails included); when "STARTTLS / SSL" is unticked (and the server is not this machine) or port 465 is used, the test mail and enabling ask you to confirm first. What the mail contains: see "What is in the mail" below. (`src/Mail.cs`, `src/Extras.cs`) |

Cloud sync, crash reports, usage statistics: none. "Go to update page" and "Donate..." simply open a web address in your default browser.

### What is in the mail

Only sent after you turn on email reminders, and only the two kinds above. The reminder mail has a fixed layout (`SendCacheMail` in `src/Extras.cs`):

- **Subject**: `【Claude】"<session title, cut to 22 characters plus …>" cache expires in N min`
- **Body**:
  - Session: **the full session title**
  - Cache left: about N min (the time it expires)
  - Status: waiting for you (permission prompt / question etc.), or idle
  - Context size (tokens) and the cache lifetime (minutes)
  - When a price table exists: roughly what reloading after expiry costs in US dollars, and what continuing costs
  - Account: account ID and plan name (for example `B · Pro`); no line if no account was recognized
  - The time of sending
  - Two fixed sentences (how to keep the cache alive, and why you got this mail)

The mail does **not** contain: conversation text, project paths, login tokens, your login e-mail. But **the session title appears in the subject and in the body and can reveal what you are working on**, and it passes through your SMTP server and your mailbox; a session without a title shows "project folder name · first characters of the session ID" in its place. The test mail is one fixed sentence ("If you received this email, the SMTP settings for email reminders are correct.").

Whether the connection is encrypted depends on the "STARTTLS / SSL" box in the dialog (ticked by default). If you untick it (and the server is not this machine), the program warns you first: the mail password and the mail text would travel over the network in clear text. Line breaks and other control characters in the subject are replaced by spaces; at most 30 mails per hour (test mails included), after that nothing more is sent.

## Local files it reads (read-only, opened in shared mode, so Claude Code is never blocked from writing)

| File | What is read | When |
|---|---|---|
| `<Claude data folder>\projects\**\*.jsonl` (session logs) | Lines that contain `usage` are parsed: timestamp, session ID, message ID, model name, the token counts, thinking effort, working directory (used to show the project name), session title (`ai-title` / `custom-title`). **Each whole JSON line is parsed into memory, so the conversation text passes through memory too, but the program does not take it, store it or upload it.** | When "Show usage statistics" is on, or "Log usage to file" is on, or "Notify before the cache expires" is on (while the panel is closed only the newly added part is read, about every 15 seconds; 30 in the Minimal profile). Not while "Pause background reading (for gaming)" is on |
| `<Claude data folder>\sessions\*.json` | Session name and where the name came from (every 30 seconds); **session state**: `status` (busy / idle / waiting for you), `waitingFor` (what it waits for), `pid` (Claude Code's process ID), `cwd` (the session's folder), `entrypoint` (VS Code extension / terminal) (about every 2 seconds; used for the status lights and the ↗ jump). After reading `pid` it checks whether that process still exists and when it started; files whose process is gone are ignored | Same as above |
| `<Claude data folder>\.credentials.json` | To recognize the account / plan: **the whole file is parsed into memory, but only `organizationUuid`, `subscriptionType` and `rateLimitTier` are used**. The `accessToken` field is not used, saved or sent, unless you agreed to the online query ("Yes" in the dialog, or choosing "Online query" by hand); only then is it used to call the endpoint | Every 30 seconds |
| `~\.claude.json` (or `<Claude data folder>\.claude.json`) | The whole file is parsed into memory; only `oauthAccount` is used: e-mail (only the masked form such as `a***9@example.com` is stored and shown), account and organization IDs, subscription creation time, organization type | Every 30 seconds |
| VS Code `state.vscdb` (under `%APPDATA%\Code` / `Code - Insiders` / `Cursor` / `VSCodium` / `Windsurf`) | Opened read-only; takes the session tab titles from the `Anthropic.claude-code` key | Every 10 minutes, through `scripts\sync_vscode_titles.py` (needs Python; not run without it; not run while paused) |
| Stilla's own logs: `logs\usage_detail_v2.csv`, `session_index.csv`, `quota_snapshots.csv`, `quota_conversion.csv`, `accounts.csv`, `account_timeline.csv`, `vscode_titles.csv`, `usage_tags.csv`, `ultra_periods.csv` | Read back for: speed and share over long windows (last 8 days), the trend chart, the switch log in the Model view, the "Today / Yesterday / This week / Custom" tabs, the API view, the weekly report, the estimate self-check | When the matching view or tab is open, or while a weekly report is generated (in the background, low priority) |
| `mail.txt` (root of the data folder) | The email reminder settings (see "Files it writes") | At start |
| **Window and process information** (from Windows itself, not files) | **Only at the moment you click ↗**: see "The ↗ jump" above. What it reads is used once in memory, never saved or sent. (`src/Jump.cs`) | When you click ↗ |

"Claude data folder": `CLAUDE_CONFIG_DIR`, or `~\.claude`, plus the folders detected automatically and the ones you added by hand. Right-click → Claude data folders shows all of them.

## Files it writes

All in `%APPDATA%\Stilla\` (portable mode: `data\` next to `Stilla.exe`):

| File | Contents |
|---|---|
| `settings.txt` | Window position, size, transparency, skin, options (plain numbers; including `usage=` simple / detailed mode, `src=` quota source and `onl=` whether the online query was agreed to; since 0.2.0 also bit masks for views / columns / background features, the speed window, the minutes before expiry, the quota warning percentage, the hover-tips switch and so on) |
| `mail.txt` | The email reminder settings (it only exists after you have opened "Email reminders" and clicked OK): enabled or not, SMTP server, port, encryption on / off, user name, sender, recipient (**these are plain text**), the **password** (encrypted with Windows DPAPI: only the same Windows account on this PC can decrypt it, a copy on another PC cannot be decrypted; and it is bound to server + user name: change either and it can no longer be decrypted, so it is never quietly sent to another server), and the sessions you ticked (**session ID, minutes, session title, plain text**) |
| `rate_limits.json` | Written by the status-line helper: the 5-hour / weekly percentages and reset times |
| `roots.txt` | The Claude data folders you added by hand |
| `update.txt` | The latest version number last seen, the update page address, which version you have already been notified about, the time of the last check |
| `statusline-chain-*.txt` | Your original status-line command (used for chaining), one per config folder |
| `error.log` | Only if you set the environment variable `STILLA_DEBUG=1`: the text of an exception that was not handled in the window. It can contain file paths. Without that variable nothing is written |
| `logs\usage_detail_v2.csv`, `usage_tags.csv` | One row per API message: time, **session ID, project name (from the working directory)**, model, the token counts, task ID, tool call count / names, thinking effort |
| `logs\session_index.csv` | One row per session: **title, start / end time, session file path**, token totals |
| `logs\quota_snapshots.csv` | Snapshots of the 5-hour / weekly percentages and reset times |
| `logs\accounts.csv`, `account_timeline.csv` | Your account profile: name, plan, subscription start date, monthly fee, **the bound organization ID, masked e-mail**, switch history |
| `logs\quota_conversion*.csv / .txt`, `vscode_titles.csv`, `ultra_periods.csv`, `read_progress*.txt` | Estimation coefficients and the price table, the VS Code title cache, the times Ultracode was on (per session ID), read progress (session file paths and read positions) |
| `logs\weekly_reports\weekly_report_<Monday date>.html` | The **weekly report** (new in 0.2.0): one for the previous week is generated automatically, or by hand. A self-contained HTML file with a content-security policy (no scripts, no external requests); everything that comes from sessions is HTML-escaped. It contains: the week's amounts and quota conversions, usage per day and per hour, model × tier, **the 10 most expensive sessions (with titles and time spans)**, account IDs and plan switches. It is written to a `.tmp` file first and then renamed |
| `logs\export\` (new in 0.2.0) | The folder the save dialog starts in when you use ⋮ → "Export data of the current time tab" → "...save as CSV..." (the folder is created); **where the file goes is your choice**, and it contains session titles and session IDs. "...copy to clipboard" writes no file |

**These CSV files, the weekly reports, exported files and `mail.txt` contain your session titles, project paths, organization ID, subscription details or e-mail addresses. Do not paste or upload the `logs` folder or these files when you report a problem.** To remove everything: delete the whole `%APPDATA%\Stilla` (the uninstaller can do this for you if you choose so).

Two more places put something on the clipboard, and only when you click: "...copy to clipboard" in the export menu, and "Copy as text" in the model detail popup (it contains the session title).

### Files it changes only with your consent

The `statusLine` entry in `~\.claude\settings.json` (or `<CLAUDE_CONFIG_DIR>\settings.json`): only changed when you tick it in the installer or click "Yes" in the settings dialog. Before changing it, a backup is made as `settings.json.stilla-backup-<time>`; if the JSON cannot be parsed it is left alone; your existing status line is kept as is (chained); uninstalling or "Undo the status line setup..." restores it. Stilla **never modifies** the session logs, `.credentials.json` or `.claude.json`, and never refreshes or rewrites the login token.

## Other programs it starts

- `python` (in the background, hidden window, optional): runs `scripts\quota_fit.py` (quota conversion; the interval depends on the profile, usually every half hour to a few hours, more often while the coefficients are only inferred) and `scripts\sync_vscode_titles.py` (every 10 minutes). The scripts are purely local and use no network. The Minimal profile does not run them, and neither does "Pause background reading (for gaming)".
- `Stilla.Statusline.exe` (started by Claude Code, not by the widget): reads the JSON that Claude Code sends, keeps only the 5-hour / weekly percentages and reset times in `rate_limits.json`, and, if you had a status line of your own, hands the input on to your original command (through `cmd.exe`) and prints its result.
- `explorer.exe`: when you click "Open the Stilla data folder", "Open log folder" or "Open session record" (or the folder button in the Settings window).
- The Windows `vscode:` protocol (when you click ↗): Stilla asks the system to open `vscode://anthropic.claude-code/open?session=<session ID>`, which your local VS Code (and the Claude Code extension in it) handles; this step is local, Stilla itself does not touch the network.
- The system default program (right-click → Weekly report → open a report or the report folder, or clicking the "Weekly report ready" notification): it simply opens the local file or folder in your default browser / Explorer.

## Other

- System notifications (tray balloons): account switches, subscription renewal reminders, cache about to expire (session title, minutes left, context size), quota cap warnings, weekly report ready or failed, errors of the jump or of sending mail. The text of some notifications contains session titles; keep that in mind when you share your screen.
- The installer writes its uninstall information under `HKCU` (current user) and, if you tick "Start with Windows", a `Run` entry; no administrator rights are needed, and the uninstaller removes both.
- "Pause background reading (for gaming)" pauses reading the session logs (for the panel and for "Log usage to file"), the cache and email reminders, the Python scripts and the weekly report; the quota reading and the update check go on. To make Stilla read no session files at all, turn off all three: "Show usage statistics", "Notify before the cache expires" and "Log usage to file".

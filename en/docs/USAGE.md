# Stilla guide (0.2.0)

> **中文:** [docs/USAGE.md (Chinese edition)](../../docs/USAGE.md): the same guide with the Chinese menu names.

*Menu, button and column names in this guide are the ones you see in the English UI.* Upgrading from 0.1.x? Read the last section, "Upgrading to 0.2.0", first.

## Contents

- [The widget](#the-widget)
- [Settings window](#settings-window)
- [Simple mode and detailed mode](#simple-mode-and-detailed-mode)
- [Usage panel](#usage-panel)
- [Session status lights and cache reminders](#session-status-lights-and-cache-reminders)
- [Jumping to a session's window](#jumping-to-a-sessions-window)
- [Email reminders (optional)](#email-reminders-optional)
- [Speed, share and the cap forecast](#speed-share-and-the-cap-forecast)
- [Sorting and export](#sorting-and-export)
- [Model view: click a session for details](#model-view-click-a-session-for-details)
- [API view](#api-view)
- [Trend view](#trend-view)
- [Weekly report](#weekly-report)
- [Estimate self-check](#estimate-self-check)
- [Features and display settings, profiles, pausing background reading](#features-and-display-settings-profiles-pausing-background-reading)
- [Hover tips](#hover-tips)
- [The menu at the right end of the time tabs](#the-menu-at-the-right-end-of-the-time-tabs)
- [Quota conversion (optional, needs Python)](#quota-conversion-optional-needs-python)
- [Accounts and subscriptions](#accounts-and-subscriptions)
- [Where files are kept](#where-files-are-kept)
- [Keys in settings.txt you can edit by hand](#keys-in-settingstxt-you-can-edit-by-hand)
- [Command-line options](#command-line-options)
- [Upgrading to 0.2.0](#upgrading-to-020)

## The widget

- Drag anywhere on the window to move it; drag an edge / corner to resize (position and size are remembered).
- "Refresh" in the top right refreshes immediately, "×" hides it to the tray (the program keeps running). Double-click the tray icon to show / hide; only "Exit" in the tray menu really quits.
- Right-click menu (in this order):
  - **Refresh now**
  - **Quota data source**: "Status line only (reads no login token, no network)" / "Online query (experimental, reads the login token)" / "Auto (default): status line first, online query when it has no data" (**the default**: the status line is used first; the online query only when there is no fresh status-line data, and only after you agree once, see the README); "Set up the Claude Code status line..." and "Undo the status line setup..."
  - **Claude data folders**: which folders are read right now; "Add folder..."; "Detect again"; "Clear folders added by hand"; "Open the Stilla data folder"
  - **Background opacity** / **Text opacity**: two independent sliders. The background always keeps a little opacity, otherwise the mouse would click through it and the window could not be dragged
  - **Log usage to file** (on by default)
  - **Notify before the cache expires (reads sessions every 15 s even if the panel is closed)** (on by default; the label shows the default pace, the Minimal profile reads every 30 s)
  - **Show usage statistics** (ticked by default): detailed mode, with the usage panel; unticked = simple mode, quota only. It is the same switch as the toggle button under the quota card
  - **Time tab settings...**: tick which time tabs the panel shows
  - **Features and display settings...**, **Profile**, **Estimate self-check details...**, **Weekly report**, **Pause background reading (for gaming)**, **Hover tips (explain things when the mouse rests on them)**, **Email reminder...**: new in 0.2.0, each is described below
  - **Accounts and subscriptions**
  - **Skin** (16) / **Light / dark** / **Always on top**
  - **Settings...**: see "Settings window" below
  - **Exit**
- Tray menu: Show / hide, Light / dark, Refresh now, Pause background reading (for gaming), Settings..., Exit.
- Hotkeys (global while the program runs; if another program already owns a combination it simply cannot be registered, use the menu): `Ctrl+Alt+B` paper ↔ ink, `Ctrl+Alt+N` next skin.
- Colors of the rings / numbers: below 60% the normal color, 60–85% orange, above 85% red (some skins use other cues).
- The line at the bottom: "Status line - 12:34:56" = the time of the data; "No update for N min" = the status-line data is too old (Claude Code is not running); "No status line data · right-click" = not set up yet or nothing received yet ("Waiting for Claude Code (Window reset)" means both saved windows have just reset and Claude Code has not delivered new numbers yet). With the online query it reads "Updated 12:34:56 - every 60 s".
- In 0.1.x the right-click menu had "Show DeepSeek estimate row". It has moved: it is now "DeepSeek cost estimate row at the bottom" under "Features and display settings..." → "Other display items" (hidden by default; if you had it on in 0.1.x it stays on).

## Settings window

Right-click menu / tray menu → "Settings...", or click the small red dot to the right of the Stilla mark.

- **Updates**
  - "Check for updates automatically at every launch" (on by default): about 4 seconds after launch it asks GitHub once for the latest version number.
  - "Check now": checks immediately; the result shows on the line above.
  - "Go to update page": opens the latest Release page in the default browser (shown in red when a new version exists). Nothing is downloaded or installed automatically.
  - When a new version exists, **a red dot appears to the right of the Stilla mark / title on every compact card** (the position differs slightly between skins), the tray tooltip gets a ● marker, and the tray balloon is shown only once per new version. The dot disappears by itself after you update.
- **Support this project → Donate...**: opens the "Support this project" section of the project README (GitHub Sponsors, WeChat, Alipay).
- **About**: version number and the disclaimer; the "Open data folder" button is at the bottom of the window.

## Simple mode and detailed mode

The default is **detailed mode**: the quota card on top, the usage panel below it, so all core features are in view as soon as it opens. There is a small button right under the quota card:

- In detailed mode it reads "▴ Quota only": one click shrinks the window to **simple mode** (quota only, a smaller and narrower window; the usage panel no longer reads session files, see the note below);
- In simple mode it reads "▾ Show details": one click returns to detailed mode.

The button stays in the same place in both modes and in all 16 skins; "Show usage statistics" in the right-click menu is the same switch. The choice is remembered in `settings.txt` (`usage=`).

> In simple mode, as long as "Notify before the cache expires" is still on (the default), Stilla still reads the newly added part of the session logs every ten-odd seconds, to keep track of the caches. To make it read no session files at all: turn off "Show usage statistics", and also untick "Notify before the cache expires" and "Log usage to file" (the latter reads the newly added part once a minute and writes it into `logs\`).

## Usage panel

Part of the detailed mode (or right-click → Show usage statistics). The window gets wider; if it would go past the right edge of the screen it moves left automatically. Default width 600 (in base units, scaled with the display), narrowest 520; in simple mode the narrowest is 240. When the window is too narrow for all columns, Stilla hides the least important ones for that moment (Input, Cache W, Cache R, Share, Output, Speed, in that order) and brings them back when you widen the window.

- **Time tabs** (6 shown by default, up to 11 selectable): "5 min", "15 min", "30 min", "1 h", "3 h", "This 5h", "Prev 5h" (live statistics); "Today", "Yesterday", "This week", "Custom" (read from the log). Click a tab to switch; the default tab is "15 min". Shown by default: 15 min, 1 h, This 5h, Prev 5h, Today, Custom.
  - "This 5h" runs 5 hours back from the quota's 5-hour reset time; if the reset time is unknown it is the rolling last 5 hours.
  - "Custom" opens the "Custom range" dialog (start, end, and the shortcuts "Last 1 h", "Last 6 h", "Today", "Yesterday", "This week").
  - Which tabs are shown: right-click → Time tab settings..., or the ⋮ at the end of the tab strip (see below).
- **Six views** (the buttons at the right of the header; which ones are shown is decided by "Features and display settings..."):
  - **Session**: one row per session: status light and name, then the columns **Expires** (cache time left), **Speed**, **Share**, **Input**, **Output**, **Cache W** (cache write), **Cache R** (cache read). The "Input" column is hidden by default (see "profiles" below). Sessions being written to come first, then those whose cache has not expired, then expired ones; within a group the most recently used first. Click a row to open the "Session details" window (cache hit rate, the latest 300 messages, "Copy session ID", "Open log folder", "Open session record"; it reads the usage log, so "Log usage to file" has to be on).
  - **Model**: one row per session, with the models and tiers it used as tags at the end of the row; click a row for a detail popup, see "Model view: click a session for details". The tier tag is the thinking effort (low, medium, high, xhigh, max; "ultra" when Ultracode was on; "+fast" is added in fast mode; "Unlabeled" when no tag is known).
  - **Compare**: all sessions merged, one row per "model + tier", with the columns "token", "Output", "Think", "5h est." and "Mult." (how many times more quota per million tokens than the cheapest row). **The separate "Tier" view of 0.1.x was merged into this one.**
  - **Plan**: one row per account: usage in the current period ("Period use": the API-equivalent amount at official list prices), "Fee" (monthly), "Payback" (multiple), "Renews" (date); the last three lines are the subscription period, this period's API-equivalent amount, and the weekly quota forecast (worked out from the quota readings of the last 12 hours: will it run out before the reset?).
  - **API**: see "API view". **Hidden by default in the Recommended profile.**
  - **Trend**: see "Trend view".
- Column widths can be changed by dragging the dividers. Click "▾ N more" under the session names to show them all ("▴ Collapse · scroll" when they do not fit: page with the mouse wheel). Drag the line above "Total" (it has three small dots in the middle) to change the row spacing; double-click it to reset.
- The "Total" row adds up the rows of the current time tab. In the Session view the Expires, Speed and Share columns are not calculated per time tab, see their own sections.
- Expires = how long this session's cache still lasts after its latest cache read / write (a 5-minute or a 1-hour lifetime, depending on which kind the session wrote last). After it expires the next message has to write the whole context into the cache again, which costs much more than continuing.

## Session status lights and cache reminders

In the Session view every row has a small dot at the far left, and the text of the **Expires** column uses the same color:

| Color | Meaning |
|---|---|
| Green | **Running**: Claude Code reports it is busy, or the session log had new content in the last 60 seconds. The cache renews itself with every request, so it does not expire. Expires reads "Running" |
| Blue | **Waiting for you**: Claude Code is waiting for you (permission prompt / question / plan approval etc.). It is neither running nor finished; the cache keeps counting down. Expires reads "Waiting m:ss" |
| White (normal text color) | 30 – 60 minutes of cache left |
| Yellow | 10 – 30 minutes left |
| Red | Under 10 minutes left |
| Gray | "Expired", or "No cache" |

The closer the cache gets to expiring, the more yellow, then red: the idea is that you send a message while the cache is still alive and avoid paying for a reload.

- **Where the state comes from**: green / blue come from the `status` field Claude Code writes into `<Claude data folder>\sessions\<pid>.json` (re-read about every 2 seconds, read-only; files whose process no longer exists are ignored). Claude Code does not document that file, so its format may change; if it cannot be read, "Running" still follows "new records in the last 60 seconds", but "Waiting for you" cannot be shown.
- **Summaries**: the small dot at the left end of the account chip on the compact card and the dot at the bottom right of the tray icon show the most urgent state among all sessions; the tray tooltip lists how many sessions there are of each ("red N yellow N green N blue N").
- **System notification**: when a session's cache time left has **just dropped** to within the lead time, a system notification (tray balloon, titled "Claude cache expires within N min") is shown, listing at most 3 sessions: title, minutes left, context size, and (when a price table exists) "reload after expiry ~$X (continue now: ~$Y)".
  - Switch: right-click → "Notify before the cache expires..." (on by default), `alert=` in `settings.txt`.
  - Lead time: ⋮ → Reminder settings → "How early to notify before the cache expires" → in 5 / 10 / 15 / 20 / 30 min (default 10), `alertmin=`.
  - It only notifies when: Stilla actually saw the time drop in from above the lead time (sessions that were already below it at startup are not notified late); the session's cache lifetime is at least 30 minutes (a 5-minute cache is red almost all the time, so notifying would be pointless); the context is at least 50,000 tokens; and the session is not running right now. Nothing is notified while background reading is paused.
- **Quota cap warning**: one notification when the 5-hour or the weekly quota reaches a percentage (once per window). ⋮ → Reminder settings → "Quota cap warning (notify when the 5-hour / weekly quota reaches this %)" → Close / At 70% / At 80% / At 90% / At 95%, default 90%, `limitwarn=` (0 = off).

## Jumping to a session's window

In the Session view every session name has a **↗** to its right. Click it and Stilla brings the window the session lives in to the front:

- **A session opened in the VS Code extension**: Stilla looks through the VS Code windows for the one that belongs to the session's folder name (it tries the last folder of the session's working directory first, then up to 3 levels above it). It first looks for a window whose title has a whole segment equal to that name (the VS Code title format is `file - folder name - Visual Studio Code`); failing that it takes a window whose title merely contains the name (names shorter than 3 characters are not tried this way). That window is brought to the front (and restored if minimized), and then Stilla opens `vscode://anthropic.claude-code/open?session=<session ID>` so the Claude Code extension returns to this session in that window. If no matching window is found but other VS Code windows are open, the session opens in the frontmost VS Code window and a notification says so.
- **A session opened in a terminal**: Stilla walks up the parent processes of the Claude Code process (up to 8 levels) to the first one that has a window (Windows Terminal / console window) and brings it to the front. **With several tabs in Windows Terminal it can only raise the window, not select a tab.**

Notes:

- Only windows of a process whose name starts with `Code` (VS Code and VS Code Insiders) are looked at; windows of Cursor, VSCodium or Windsurf are not matched.
- The match is by the folder name in the window title, so several windows with a same-named folder can be confused; the `vscode://…` address is handled by the Claude Code extension and may stop working if the extension changes it. Problems show up as a notification ("Jump failed: …" and similar).
- Switch: "Features and display settings..." → Other display items → "Jump arrow (↗) to the right of the session name" (on in the Everything and Recommended profiles, not shown in Minimal).
- Window titles and processes are only listed at the moment you click ↗, never otherwise.

## Email reminders (optional)

Besides the system notification, Stilla can send an email to yourself when a cache is about to expire (for when you are away from the PC). **It is off by default and goes through no third-party server**: you enter an SMTP server of your own (Gmail, Outlook, QQ Mail or any other) and Stilla connects straight to it.

Entry points (all open the same "Email reminders" dialog): right-click → "Email reminders..."; ⋮ → Reminder settings → "Email reminders..."; the **@** button in the Session view header (once reminders are enabled and set up it shows how many sessions are ticked, for example "@2"; sessions with a reminder ticked get an @ after their name).

In the dialog:

- **Enable email reminders**: nothing is sent unless this is ticked.
- **Email settings**: "Preset" ("Custom", "Gmail", "Outlook / Hotmail" or "QQ Mail": picking one fills in the server, port 587 and encryption), "Server", "Port", "STARTTLS / SSL" (ticked by default, leave it ticked), "User", "Password", "From" (empty = the user name), "To" (several recipients separated by commas, semicolons or spaces). Gmail / Outlook / QQ Mail need an app password / authorization code, not the login password; use port 587 (STARTTLS), because 465 is "direct SSL", which the mail component used here does not support, so it usually just times out (a warning appears if you use 465). If you untick the encryption box (and the server is not this machine), a warning appears when you click OK with reminders enabled, or when you click "Send test email": the password and the mail text would cross the network in clear text.
- **Send test email**: immediately sends one fixed test mail with the settings in the form; the result shows in the dialog.
- **Sessions**: a list with the columns "Alert" (tick), "Session", "Now", "Cache TTL" and "Remind at (min)". Tick the sessions to watch and enter the minutes of cache left at which to send (1 – 600; several values allowed, for example `30,10`); "Default" ... "min before expiry" is the value filled in when you tick a session; "All" / "None" tick or untick everything.

When mail is sent:

- All of these must hold: reminders are enabled, the mail settings are complete (server, user name, password, recipient), at least one session is ticked, and background reading is not paused.
- One mail is sent when a ticked session's cache time left **has just dropped** to the minutes you set; each minute value is sent once per cache cycle. Nothing is sent while the session is running (the cache renews itself), and sessions that were already below the value at startup / when you ticked them are not sent late.
- A session with a 5-minute cache never triggers a minute value of 5 or more (otherwise it would send a mail for every reply).
- Fuse: at most 30 mails per hour (test mails included); after that nothing more is sent, so that a fault somewhere cannot flood your mailbox.
- A failed send shows a tray notification (at most one every 30 minutes); the dialog shows the result of the latest send ("Last sent: ...").

**What is in the mail** (everything; there is nothing else):

- Subject: `【Claude】"<session title, cut to 22 characters plus …>" cache expires in N min`
- Body: the session title (in full), about how many minutes of cache are left and when it expires, the status (waiting for you / idle), the context size and cache lifetime, (when a price table exists) the estimated cost of reloading, the account ID and plan name (for example `B · Pro`), the time of sending, and two fixed sentences.
- No conversation text, no project paths, no login token and no login e-mail. The session title may reveal what you are working on; it passes through your SMTP server and your mailbox, so decide with that in mind (a session without a title shows "project folder name · first characters of the session ID").

**How the password is stored**: in `%APPDATA%\Stilla\mail.txt` (portable mode: in `data\`), encrypted with Windows DPAPI (only the same Windows account on this PC can decrypt it) and bound to "server + user name": if the file is copied to another PC / account, or someone changes the server / user name in the file, the password cannot be decrypted and has to be entered again; if you change the server or the user name in the dialog without typing the password again, Stilla asks you to enter it again, so the old password is never quietly sent to a new server. The server, user name, sender, recipient and the titles of the ticked sessions in that file are plain text.

## Speed, share and the cap forecast

Two columns in the Session view:

- **Speed** (the header reads for example "Speed 15m"): how fast this session used quota over the last N minutes. Left = 5-hour quota, right = weekly quota, both in "quota percentage points per hour". Example: `30 / 3.97` = at this intensity it uses 30% of the 5-hour quota and 3.97% of the weekly quota per hour.
- **Share** ("Share 15m"): how much quota this session used in total in the last N minutes (not divided by the window), again 5-hour on the left and weekly on the right, in percentage points. Share ÷ window hours = Speed.

General rules:

- The **statistics window** is the last 15 minutes by default, shared by both columns. Click the text "Speed" or "Share" in the header and pick 5 min … 48 h, or "Custom..." (the "Speed window" dialog, 1 – 2880 minutes); ⋮ → "Speed window (now …)" works too. It is saved as `speedwin=` (minutes) in `settings.txt`. The small triangles at the right of the header are for sorting, not for picking the window.
- **Gray** numbers = the session has gone idle in the window (no usage, or its cache expired); they show the numbers of a window of the same length when it was last active. For reference only, not counted in the totals.
- **"≈"** = the quota coefficient is inferred from the plan multiplier (this account / plan has too little data to be measured), so it can be off by several times. **"—"** = no data, or the current account has no quota coefficient yet.
- Windows longer than 3 hours, and the gray numbers, need the last 8 days of the usage log: "Speed history (...)" in "Features and display settings..." has to be on (it is in the Everything and Recommended profiles). The history is read while the panel is open on the Session or Trend view.
- Speed, share, the cap forecast and the API-equivalent amounts are all worked out with the quota conversion table (see "Quota conversion" below), which **is produced by an optional Python script**. Without Python, or while there is not enough data, they show "—" and everything else works as usual.

**Cap forecast**: the line under the Total row of the Session view. It assumes the speed of the Total row continues, and says how long the 5-hour and the weekly quota would last, for example:

> Speed over the last 15 min · 5h cap in ~2.5 h (before reset by 1.2 h) · Week cap in ~5.4 d (after reset)

Red (any "before reset by …") = at this pace the quota runs out before the reset; "(after reset)" = it would only run out after the reset, so it will not run out in time; "no usage" = nothing used in the window; "full" = already at 100%. This is only an extrapolation and swings a lot on a very short window: for a steadier forecast use a longer statistics window (15 – 60 min). Switch: "Features and display settings..." → Other display items → "Cap forecast under the Total row" (on by default). While the conversion is not ready the line says so ("Cap forecast: the quota conversion for this account is not ready yet, ...").

The Plan view has a separate weekly line at the bottom ("Week 61% (3.2 d left) · 12 h pace: ..."); it works from the quota readings themselves (the earliest one within the last 12 hours, at least 1.5 hours old) and does not depend on the quota conversion table.

## Sorting and export

**Sorting**: in the Session, Model and Compare views, click a column header. The first click sorts descending (time = newest first, usage = biggest first; the name column of the Compare view sorts A → Z), the second click reverses it, the third restores the default order. Each view remembers its own (`sort0=`, `sort1=`, `sort2=`).

**Export**: ⋮ → "Export data of the current time tab":

- "Session table" and "Model × tier details", each in two ways: **copy to clipboard** (tab-separated, paste straight into Excel) and **save as CSV...** (UTF-8 with BOM so Excel opens it directly; the save dialog starts in `%APPDATA%\Stilla\logs\export\`, and where you save is your choice; the default file name is `session_table_<tab>_<date>_<time>.csv` or `model_tier_details_<tab>_<date>_<time>.csv`).
- Columns of the session table: Session, Session ID, Cache status, Cache left (min), 5h speed (%/h), Weekly speed (%/h), Share 5h (%), Share weekly (%), Input, Output, Cache W, Cache R, Cost (USD), Cost basis. Columns of the model × tier details: Session, Session ID, Model, Tier, Messages, Thinking tokens, Input, Output, Cache W, Cache R, 5h quota % (est.), Weekly quota % (est.), API equiv. (USD).
- "Cost (USD)" and "API equiv." are the amounts at official list prices; **subscribers do not actually pay them**, they are for comparing sizes. In 0.2.0 "Cost (USD)" is the session's usage in the current time tab and "Cost basis" says "Current time tab".
- Text that starts with `=`, `+`, `-` or `@` gets a leading apostrophe in the CSV and in the clipboard text, so that Excel does not run it as a formula.
- **Exported files contain session titles and session IDs: do not paste them online carelessly.**

## Model view: click a session for details

Click a session row in the Model view and a small window opens right below it (click the same row again, click elsewhere, press Esc or click "×" to close it):

- **Totals by tier**: for the current time tab, one row per "model × tier" of this session: Tier, Model, Period, Msgs, Input, Output, Think, Cache W, Cache R, "5h est.", "Wk est.", "API equiv."; the last row is the total.
- **Switch log**: in time order, one row per run in which the model and tier did not change, with its start, end and usage, so you can see which model / tier the session switched from and to midway (runs of subagent tasks are listed after them, marked "(subagent)"). It appears when the session used more than one model × tier; when there are too many runs only the latest 20 are listed, and a line says how many earlier ones are not shown.
- Buttons: "Session details" (opens the details window of this session) and "Copy as text" (tab-separated, paste into a spreadsheet or notes; **it contains the session title**; the button reads "Copied" afterwards).

A ▾ at the start of a row means the session changed model or tier during this period.

## API view

Counts only usage of **non-Claude models called through an API** that appears in the session logs on this machine (DeepSeek, for example): model name and date range, then "Msgs", "Input", "Output" and "Est. cost".

- The estimated cost is worked out at DeepSeek off-peak prices in CNY (the bottom lines of the view show the prices used); **only DeepSeek has a price table, other models show "—" and are left out of the total**. It is a comparison aid; you did not pay this amount.
- Usage of a Claude subscription is in the Plan view; the Anthropic Console's API usage needs an Admin Key, which this machine does not have, so it cannot be shown here.
- The Recommended profile **hides** this view by default. To see it: "Features and display settings..." → "Views shown in the panel" → tick API (or switch the profile to Everything).

## Trend view

One bar chart: "24h" = the 5-hour quota used in each of the last 24 hours; "7d" = the weekly quota used on each of the last 7 days (switch at the top right of the chart; saved as `trendr=`).

- Each bar is converted with the coefficients of the **account:plan in force at that time**; where the account / plan was switched a dotted line is drawn and labeled.
- The dashed line is the speed that would use up exactly the whole quota window (20 %/h for the 5-hour quota, about 14.3 %/day for the weekly quota); a bar above it (red) means that if that hour's / day's intensity lasted a whole window it would use up the quota.
- Underneath: the total (as a share of the weekly quota, or in 5-hour windows), the API-equivalent amount, the peak and the average of the active hours / days. Records without a reliable coefficient are left out, and the line says how many.
- Data older than 3 hours comes from the last 8 days of the usage log, so "Speed history (...)" must be on (it is in the Everything and Recommended profiles).
- Rest the mouse on a bar to see that slot's numbers.

## Weekly report

One week (Monday 00:00 to Sunday 24:00, local time) of usage in one HTML file; double-click it to open it in a browser. **It uses no network and loads no external resources.**

Contents: an overview (the week's API-equivalent amount and the change vs. the previous week, the number of 5-hour quota windows and weekly-quota points it equals, tokens and messages, active sessions and active hours, the peak hour), a table with one row per day ("Daily"), an **hourly heatmap**, "Model x tier", the **"Top 10 sessions by quota used"** (with titles and time spans), an estimate of the cost of reloading after caches expired, the estimate factors (measured or inferred), and the account / plan switches.

- **Automatic**: while "Automatic weekly summary" is on (Everything and Recommended profiles; off in Minimal), Stilla checks once an hour while it is running: if the previous complete week has no report yet, it generates one in the background and shows a notification ("Weekly report ready"); click the notification to open it. So it usually happens the first time the program runs in a new week. On a fresh install it also makes last week's report as soon as the usage log has been written to since the start of last week (the check looks at when the log was last written, not at whether last week itself had any usage). Nothing is generated while background reading is paused.
- **By hand**: right-click → Weekly report → "Generate and open this week's summary" (up to now) / "Generate and open last week's summary" / "Open weekly report folder". Generating by hand does not depend on the switch above.
- The file is `%APPDATA%\Stilla\logs\weekly_reports\weekly_report_<Monday date>.html`; generating the same week again overwrites it.
- The report reads the usage log in `logs\` (`usage_detail_v2.csv` and others), so "Log usage to file" has to be on; the amounts and quota conversions also depend on the quota conversion table (without it they show 0 or a "no factor" note).
- **The report contains session titles and account IDs: look at it before you send it to anyone.**

## Estimate self-check

The quota conversion is an estimate; this feature checks whether it is reliable by comparing "how far the quota reading rose" with "how far the coefficient-based estimate rose". The reading is the server's true value (a whole percent); the estimate is price × coefficient.

- In the **Total row of the Session view** there is a small hint: "Est. ✓ OK" (the estimate matches), "Est. high N×" or "Est. low N×". When the deviation is large, the bottom line of the panel turns into a warning sentence. Click the hint, or right-click → "Estimate self-check details...", to open the dialog "Estimate self-check": basis (account:plan), whether the coefficients are measured or inferred, the prediction error, how far the reading rose in the check window vs. the estimate, and the likely reasons.
- The check counts as a match when estimate ÷ reading is between 0.72 and 1.35 (the dialog calls this "within ±30%"). "High" is mostly caused by inferred coefficients / a plan or account that was just changed; "low" is mostly the same account also using quota on the web, a phone or another computer (not recorded on this PC).
- When the 5-hour reading rose by less than 6 points in the last 2.5 hours there is nothing to check, and the dialog says so.
- Two switches: "Features and display settings..." → Background features → "Estimate self-check (...)", and → Other display items → "Estimate self-check hint in the Total row".
- "Est. error ~N%" in the Total row of the Model and Compare views is something else: the quota conversion's own "prediction error" (each block of data is fitted on the data before it and then predicted).

## Features and display settings, profiles, pausing background reading

**Right-click → "Features and display settings..."** is one dialog for all switches:

- At the top the **Profile**: Everything / Recommended / Minimal / Custom. Clicking one resets all options below to it (right-click → Profile also lets you pick the first three directly). Change any option below and the profile becomes "Custom"; change it back to exactly match a profile and it is recognized as that one again.
- **Background features** (8 items; turning them off saves resources): "Cache expiry reminder", "Speed history", "Auto-refit quota conversion" (runs the Python script in the background), "VS Code session title sync" (runs Python in the background every 10 minutes), "Estimate self-check", "Automatic weekly summary", "Log usage to files", "Free memory automatically when usage gets too high".
- **Views shown in the panel** (6): Session, Model, Compare, Plan, API, Trend. **Columns shown in the Session view** (7): Expires, Speed, Input, Output, Cache W, Cache R, Share. **Other display items** (5): "Cap forecast under the Total row", "DeepSeek cost estimate row at the bottom", "Account chip at the top right of the compact card", "Jump arrow (↗) to the right of the session name", "Estimate self-check hint in the Total row". At least one view and one column must stay on.

The three profiles:

| | Everything | Recommended (default for new installs) | Minimal |
|---|---|---|---|
| Background features | all 8 on | all 8 on | only the cache expiry reminder, "Log usage to files" and the memory trimming |
| Views | Session, Model, Compare, Plan, API, Trend | Session, Model, Compare, Plan, Trend (API hidden) | Session, Model |
| Session columns | all 7 | all but Input | Expires, Speed, Output, Share |
| Other display items | all 5 | cap forecast, account chip, ↗, estimate self-check hint (DeepSeek row hidden) | cap forecast, account chip |
| Background pace | busiest | moderate | lightest, for gaming or a busy PC |

Background pace (the 0.2.0 values; the Custom profile uses the Recommended pace): tracking session state while the panel is closed every 15 / 15 / 30 seconds; re-reading the log history every 3 / 5 / 15 minutes; recomputing the quota conversion every 30 / 60 / 180 minutes (while the coefficients are only inferred: every 5 / 10 / 30 minutes, to replace them with measured ones sooner).

**Pause background reading (for gaming)**: in the right-click menu, the tray menu and the ⋮ menu, or click the "⏸ Reading paused (click to resume)" note that appears in the panel header (it shortens to "⏸ Paused" when the window is narrow). While paused: session logs are not read (neither for the panel nor for "Log usage to file"), there are no cache or email reminders, the VS Code title sync and the quota conversion refit do not run, and no weekly report is generated. **The quota reading and the update check go on.** The switch is not saved: after a restart reading is back on, and on resume Stilla catches up with one read right away.

## Hover tips

Rest the mouse on something in the widget for about half a second and a small note appears next to it: what it is, how it is calculated, what a click does (the colors of the cache state, how speed and share are calculated, how to read the cap forecast, what each column means, and so on). A click hides it; if you keep resting, it also hides after about 25 seconds. The note never takes focus and never blocks the mouse.

On by default. Switch: right-click → "Hover tips (explain things when the mouse rests on them)", or the item of the same name in the ⋮ menu; `tips=` in `settings.txt`.

## The menu at the right end of the time tabs

Click the ⋮ (three vertical dots) at the far right of the time tab strip for a menu with these settings:

- **Time tabs to show (ticked = shown)**: tick or untick tabs (it takes effect at once and the menu stays open; at least one stays on); "Restore default time tabs".
- **Time tab presets**: "Daily (15 min / This 5h / Today)", "Review (Today / Yesterday / This week)", "Basic (This 5h / Today)", "Default", "All 11 tabs"; "Save the tabs now shown as 'My preset'" adds a "My preset" item.
- **Custom range**: "Set custom range..."; up to 5 saved favorites (one click switches to it); "Save current custom range as favorite"; "Clear saved ranges".
- **Speed window (now …)**: Last 5 min / 15 min / 30 min / 1 h / 3 h / 6 h / 24 h / 48 h, or "Custom..." (see above).
- **Window size**: "Compact (narrowest; shrinks automatically when not all columns fit)", "Standard (fits exactly 7 columns)", "Wide (roomy, shows a few more rows)", "Tall (standard width, shows 18 more rows)". One click applies it and opens the usage panel if it is closed.
- **Views to show** / **Session view columns** / **Other display items**: the same switches as in "Features and display settings...", effective with one click here.
- **Row spacing**: Compact / Standard / Comfortable / Roomy / Very roomy (14 / 16 / 20 / 26 / 34), or drag the line above "Total".
- **Pause background reading (for gaming)** and **Hover tips**.
- **Reminder settings**: "How early to notify before the cache expires", "Quota cap warning", "Email reminders..." (see above).
- **Export data of the current time tab** (see "Sorting and export").
- Profile, "Features and display settings...", "Estimate self-check details...", "Weekly report": the same as in the right-click menu.

## Quota conversion (optional, needs Python)

Quota use ≈ dollars at official prices × quota percentage points per dollar. The coefficient is worked out by `scripts\quota_fit.py` from your own quota snapshots and usage logs (it runs in the background at low priority, at the interval of the profile) and is fitted separately per "account:plan". While there is not enough data (it needs a few days, spanning several 5-hour windows) it shows "—"; a "≈" in front of a number means it is inferred from official prices and has not been measured.

These numbers are **estimates**, not a bill. The quota readings are whole percentages, and quota used on the web / on a phone / on another computer by the same account is not recorded on this machine.

The result is written to `logs\quota_conversion.csv`, which also holds the price table the program uses for the API-equivalent amounts. **Without this table, speed, share, the cap forecast, the API-equivalent amounts, the estimate self-check and the amounts in the weekly report show "—" or 0**; session and token statistics are not affected.

## Accounts and subscriptions

- The current account and plan are recognized automatically (from the organization ID / plan in `.credentials.json` and the account details in `.claude.json`; **only the masked e-mail is stored**); when you switch accounts it switches too and the tray shows a notice.
- You can name an account and enter its monthly fee and subscription start date (used for the "payback" multiple and the renewal date; the tray reminds you 3 days before renewal). Entry points: right-click → Accounts and subscriptions → "Manage accounts and subscriptions...", or the small account chip at the top right of the compact card (some skins do not have this chip).

## Where files are kept

See [PRIVACY.md](../PRIVACY.md). Portable mode: put an empty file `portable.txt` next to `Stilla.exe`. The environment variable `STILLA_HOME` can point to another location.

New in 0.2.0: `mail.txt` (the email reminder settings, in the root of the data folder next to `settings.txt`), `logs\weekly_reports\` (the weekly reports), `logs\export\` (where the save dialog starts when you export a CSV; files only appear if you save), and `error.log` (only if you set `STILLA_DEBUG=1`).

## Keys in settings.txt you can edit by hand

`%APPDATA%\Stilla\settings.txt` is a text file with one `key=integer` per line. Normally you change things in the UI; if you edit it by hand, **quit Stilla first** (the program rewrites the file again and again while it runs); values that are not recognized or out of range are ignored.

| Key | Meaning | Values / default |
|---|---|---|
| `usage` | Usage panel: 1 = detailed mode, 0 = simple mode | default 1 |
| `src` / `onl` | Quota data source / whether the online query was agreed to | see the README; default `src` = Auto |
| `upd` | Update check at launch | default 1 |
| `log` | Log usage to file | default 1 (with `fmask` present, `fmask` decides) |
| `alert` | Notify before the cache expires | default 1 (with `fmask` present, `fmask` decides) |
| `alertmin` | Lead time of the notification (minutes) | 1 – 120, default 10; the menu offers 5 / 10 / 15 / 20 / 30 |
| `limitwarn` | Quota cap warning (notify at this %) | 0 – 100, default 90; 0 = off |
| `speedwin` | Window for Speed / Share (minutes) | 1 – 2880, default 15 |
| `tips` | Hover tips | default 1 |
| `view` | Current view | 0 Session · 1 Model · 2 Compare · 3 Plan · 4 API · 5 Trend |
| `trendr` | Range of the Trend view | 0 = 24h, 1 = 7d |
| `sort0` `sort1` `sort2` | Sort order of the Session / Model / Compare view | 0 = default order; other values are encoded by the program, set them by clicking in the UI |
| `rowh` | Table row spacing | 14 – 40, default 16 |
| `tabs` | Which time tabs are shown (bit mask, bit i = tab i; order: 5 min, 15 min, 30 min, 1 h, 3 h, This 5h, Prev 5h, Today, Yesterday, This week, Custom) | default 1258 |
| `tabpreset` | Time tab bit mask of "My preset" | 0 = never saved |
| `fmask` | Background features (bit mask): 1 cache expiry reminder · 2 speed history · 4 auto-refit quota conversion · 8 VS Code title sync · 16 estimate self-check · 32 automatic weekly summary · 64 log usage to files · 128 free memory automatically | Everything / Recommended 255, Minimal 193 |
| `vmask` | Views shown: 1 Session · 2 Model · 4 Compare · 8 Plan · 16 API · 32 Trend | Everything 63, Recommended 47, Minimal 3 |
| `smask` | Session view columns: 1 Expires · 2 Speed · 4 Input · 8 Output · 16 Cache W · 32 Cache R · 64 Share | Everything 127, Recommended 123, Minimal 75 |
| `dmask` | Other display items: 1 cap forecast · 2 DeepSeek cost row · 4 account chip · 8 ↗ jump · 16 estimate self-check hint | Everything 31, Recommended 29, Minimal 5 |
| `profile` | Current profile (0 Everything · 1 Recommended · 2 Minimal · 3 Custom) | a record only: at start the profile is recognized again from the four masks, so changing it has no effect |
| `cc0` – `cc7` | Column width ratios | change when you drag a divider; delete these lines = default widths |
| `x` `y` `w` `h2` | Window position and size | `w` / `h2` 1 – 10000; delete `w` / `h2` = default size |

The other keys (`bg` `fg` `top` `skin` `dark` `crf0`…`crt4` `cfrom` `cto` and so on) are opacity, always on top, skin and saved custom ranges: use the menus for them. `sv=2` is the version marker of the settings file (if you delete it, the window size is reset once at the next start), and `colv=2` marks the column order of the Session view. The email reminder settings are in a separate file, `mail.txt` (format: see the comment at the top of `src\Mail.cs`); please use the dialog for them, the password is encrypted and cannot be written by hand.

## Command-line options

- `Stilla.exe --demo`: demo mode (fake quota, settings kept in a temporary folder, the real settings are not touched, no update check), for screenshots / tests.
- `Stilla.Statusline.exe [--quiet] [--chain "command" | --chain-file file]`: for Claude Code's `statusLine`. To troubleshoot, set the environment variable `STILLA_DEBUG=1` and errors are written to stderr.

## Upgrading to 0.2.0

Just run the new installer (or unzip the new zip over the old files): the installer first closes a running Stilla and replaces only the program files, leaving the settings and history in `%APPDATA%\Stilla` alone; the portable edition keeps the `data` folder next to it. The quota source, skin, opacity, time tabs, usage logging and the other old settings carry over.

What changes:

- **The views were renumbered**: the five views of 0.1.x were Session · Model · Compare · Tier · Plan; 0.2.0 has Session · Model · Compare · Plan · API · Trend. The "Tier" view was merged into "Compare"; the view you were on is mapped to the new one.
- **The window size is reset once**: the size saved by 0.1.x was chosen for the old panel; the new panel is wider and the old size would squeeze the new columns out, so the first start after the upgrade uses the default size. After you resize it again it is remembered.
- **Column widths go back to the defaults**: the widths saved by 0.1.x are not read (there are more columns now, so the old ratios no longer fit).
- **New installs start on the Recommended profile**: all features on, but the "Input" column, the API view and the DeepSeek cost row are hidden. People who upgrade start from the same place (except that a DeepSeek row you had switched on in 0.1.x stays on, see the next item): if you want them, tick them in "Features and display settings...", or pick the Everything profile.
- **Right-click menu**: "Show DeepSeek estimate row" is gone from it (if you had switched it on in 0.1.x it stays on after the upgrade and the profile then reads "Custom"; otherwise it is hidden by default); it is under "Features and display settings..." → Other display items.
- **New background reading**: with the usage panel closed, the cache reminder now also reads the newly added part of the session logs every ten-odd seconds; the status lights read the state in `sessions\*.json` (about every 2 seconds); speed, trend and the weekly report read the last 8 days of the log. For less, choose the Minimal profile, or turn off the matching items in "Features and display settings...".
- **Unchanged**: how the quota is obtained (`Stilla.Statusline.exe`), the update check, the donate entry, and the rule that your data stays on this machine. The only new network use in 0.2.0 is this: once you turn on email reminders yourself, mail is sent to the SMTP server you typed in (see [PRIVACY.md](../PRIVACY.md)).

# Stilla guide (0.1.1)

## The widget

- Drag anywhere on the window to move it; drag an edge / corner to resize (position and size are remembered).
- "Reload" in the top right refreshes immediately, "×" hides it to the tray (the program keeps running). Double-click the tray icon to show / hide; only "Exit" in the tray menu really quits.
- Right-click menu:
  - **Refresh now**
  - **Quota data source**: status line only / online query (experimental) / auto (**the default**: status line first, online query only when it has no fresh data and only after you agree, see the README); set up / undo the Claude Code status line
  - **Claude data folders**: which folders are read right now; Add folder…; detect again; clear the folders you added
  - **Background transparency / text transparency**: two independent sliders. The background always keeps a little opacity, otherwise the mouse would click through it and the window could not be dragged
  - **Log usage to file** (on by default)
  - **Show usage statistics** (ticked by default): detailed mode, with the usage panel; unticked = simple mode, quota only. It is the same switch as the toggle button under the quota card
  - **Time tab settings…**: choose which time tabs the panel shows
  - **Show DeepSeek estimate row** (off by default): the last row of the panel, "what these tokens would cost at DeepSeek's off-peak price"; purely an estimate
  - **Accounts and subscriptions**
  - **Skin** (16) / **Light / dark** / **Always on top**
  - **Settings…**: see "Settings window" below
  - **Exit**
- Hotkeys (global while the program runs; if another program already owns a combination it simply cannot be registered, use the menu): `Ctrl+Alt+B` paper ↔ ink, `Ctrl+Alt+N` next skin.
- Colors of the rings / numbers: below 60% the normal color, 60–85% orange, above 85% red (some skins use other cues).
- The line at the bottom shows the source and the time of the data (for example "Status line - 12:34:56"); a different message appears when the status-line data is too old (Claude Code is not running) or when nothing has been received yet (not set up, or never received).

## Settings window

Right-click menu / tray menu → "Settings…", or click the small red dot to the right of the Stilla mark.

- **Updates**
  - "Check for updates automatically at every launch" (on by default): about 4 seconds after launch it asks GitHub once for the latest version number.
  - "Check now": checks immediately; the result shows on the line above.
  - "Go to update page": opens the latest Release page in the default browser (shown in red when a new version exists). Nothing is downloaded or installed automatically.
  - When a new version exists, **a red dot appears to the right of the Stilla mark / title on every compact card** (the position differs slightly between skins), the tray tooltip gets a ● marker, and the tray balloon is shown only once per new version. The dot disappears by itself after you update.
- **Support this project → Donate…**: opens the "Support this project" section of the project README (GitHub Sponsors and QR codes).
- **About**: version number, disclaimer, "Open data folder".

## Simple mode and detailed mode

The default is **detailed mode**: the quota card on top, the usage panel below it, so all core features are in view as soon as it opens. There is a small button right under the quota card:

- In detailed mode it reads "▴ Quota only": one click shrinks the window to **simple mode** (quota only, a smaller and narrower window, and no session files are read any more);
- In simple mode it reads "▾ Show details": one click returns to detailed mode.

The button stays in the same place in both modes and in all 16 skins; "Show usage statistics" in the right-click menu is the same switch. The choice is remembered in `settings.txt` (`usage=`).

## Usage panel

Part of the detailed mode (or right-click → Show usage statistics). The window gets wider; if it would go past the right edge of the screen it moves left automatically. Minimum size: simple mode 240 × (skin height + 22), detailed mode 420 × (skin height + 22 + 196); the 22 is the toggle bar.

- **Time tabs** (6 shown by default, up to 11 selectable): 5 min, 15 min, 30 min, 1 h, 3 h, This 5h, Prev 5h (live statistics); Today, Yesterday, This week, Custom (read from the log). Click a tab to switch.
  - "This 5h" runs 5 hours back from the quota's 5-hour reset time; if the reset time is unknown it is the rolling last 5 hours.
- **Five views** (the buttons at the right of the header):
  - **Session**: one row per session: cache time left, input, output, cache write, cache read. Sessions being written to come first, then those whose cache has not expired. Click a row to open the detail window (cache hit rate, the latest 300 messages, copy the session ID, open the session log file).
  - **Model**: one row per session, with the models and thinking tiers it used as tags at the end of the row; click the ▸ at the start of a row to expand each "model × tier" of that session.
  - **Compare**: all sessions merged, one row per "model + tier", with "how many times more quota per million tokens than the cheapest row".
  - **Tier**: totals by thinking effort (low … max; with Ultracode on it is called ultra; fast mode adds +fast).
  - **Plan**: one row per account: usage in the current period (the API-equivalent amount at official list prices), monthly fee, payback multiple, renewal date.
- Column widths can be changed by dragging the dividers; click "N more" next to the session names to expand them all.
- Cache time left = how long the cache of this session still lasts after its latest cache read / write (mostly a 1-hour lifetime). After it expires the next message has to write the whole context to the cache again, which costs much more than continuing.

## Quota conversion (optional, needs Python)

Quota use ≈ dollars at official prices × quota percentage points per dollar. The coefficient is worked out by `scripts\quota_fit.py` from your own quota snapshots and usage logs (it runs once an hour in the background at low priority) and is fitted separately per "account:plan". While there is not enough data (it needs a few days, spanning several 5-hour windows) it shows "—"; a "≈" in front of a number means it is inferred from official prices and has not been measured.

These numbers are **estimates**, not a bill. The quota readings are whole percentages, and quota used on the web / on a phone / on another computer by the same account is not recorded on this machine.

## Accounts and subscriptions

- The current account and plan are recognized automatically (from the organization ID / plan in `.credentials.json` and the account details in `.claude.json`; **only the masked e-mail is stored**); when you switch accounts it switches too and the tray shows a notice.
- You can name an account and enter its monthly fee and subscription start date (used for the "payback" multiple and the renewal date; the tray reminds you 3 days before renewal). Entry points: right-click → Accounts and subscriptions, or the small account tag at the top right of the compact card (some skins do not have this tag).

## Where files are kept

See [PRIVACY.md](../PRIVACY.md). Portable mode: put an empty file `portable.txt` next to `Stilla.exe`. The environment variable `STILLA_HOME` can point to another location.

## Command-line options

- `Stilla.exe --demo`: demo mode (fake quota, settings kept in a temporary folder, the real settings are not touched, no update check), for screenshots / tests.
- `Stilla.Statusline.exe [--quiet] [--chain "command" | --chain-file file]`: for Claude Code's `statusLine`. To troubleshoot, set the environment variable `STILLA_DEBUG=1` and errors are written to stderr.

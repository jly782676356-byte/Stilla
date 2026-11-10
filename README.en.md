# Stilla

**A small always-on-top quota widget for Claude Code (Windows).** It shows how much of your 5-hour and weekly limits you have used, when they reset, and breaks token usage down by session / model.

> **Stilla is an independent community project. It is not created, endorsed, or officially supported by Anthropic.**
> "Claude" is a trademark of Anthropic, PBC; it is used here only to say what the tool works with.

[中文](README.md)  ·  The UI is currently Chinese only.

![Stilla](docs/screenshots/hero.png)

## Features

- Floating widget: 5-hour / weekly percent used, reset time, countdown; draggable, resizable, adjustable transparency, tray icon.
- Two modes: it opens in **detailed mode** (quota + usage panel, every core feature in view); one click on the bar under the quota card ("▴ 收起，只看额度") shrinks it to a **simple, quota-only window**, and "▾ 展开详细统计" brings the panel back.
- 16 visual skins, each in light and dark (`Ctrl+Alt+N` next skin, `Ctrl+Alt+B` light/dark).
- Usage panel: per-session / per-model token breakdown over 11 selectable time ranges, prompt-cache time left, quota estimates (optional, needs Python), account / plan accounting.
- Settings window (right-click → 设置…): **update check** (once at each launch by default; a small red dot appears next to the Stilla mark when a new version exists, and one click opens the release page) and a **donate** entry.
- Does not send messages or call any model, so it costs no tokens.

## Install

Windows 10 / 11 only (uses the .NET Framework 4 that ships with Windows).

1. Download `Stilla-Setup-0.1.0.exe` from Releases and run it (no admin rights needed).
2. Tick **"Configure the Claude Code status line"**. This edits `~/.claude/settings.json`: it makes a backup first, keeps any status line you already have (it chains to it), and is undone on uninstall. You can also do it later from the widget's right-click menu.
3. Restart Claude Code and send a message.

The installer is **not code-signed**, so SmartScreen may warn. You can build from source instead: `powershell -ExecutionPolicy Bypass -File build.ps1` (only needs the `csc.exe` that ships with Windows).

## Where the numbers come from

| Mode | How | Notes |
|---|---|---|
| **Status line** (default) | Claude Code's documented [`statusLine`](https://code.claude.com/docs/en/statusline) feature pipes `rate_limits` to `Stilla.Statusline.exe`, which saves it locally | No token access, no network. Updates only while Claude Code runs it; the **VS Code extension and desktop app may not run the status line** (community reports) |
| **Online query** (experimental) | Reads your local Claude login token and calls the undocumented endpoint behind `/usage` | May break at any time; Anthropic's [terms](https://code.claude.com/docs/en/legal-and-compliance) restrict using subscription credentials in tools other than Claude Code. Your call. A warning is shown when you switch to it |
| **Auto** (default) | Status line data if fresh (< 10 min); otherwise online query, **but only after you agree**: the first time there is no status-line data a dialog explains what it does (reads the login token, calls an undocumented endpoint, terms notice). "Yes" enables it, "No" keeps status-line only and it is not asked again | Same caveats as online query once you agree |

## Compatibility

Session usage is read from the session logs Claude Code writes locally (`projects\**\*.jsonl`). Stilla looks in `CLAUDE_CONFIG_DIR` (or `~\.claude`), sibling `.claude*` folders, and desktop-app locations under `%APPDATA%` / `%LOCALAPPDATA%`; other places (WSL, other drives) can be added from the right-click menu (**Claude 数据目录 → 添加目录…**). Desktop-app locations are heuristics and **not verified on a real machine**.

## Privacy

With the default Auto mode Stilla does not read the login token unless you agree in the dialog (and never needs it while the status line delivers data); the only other network use is the update check. See [PRIVACY.md](PRIVACY.md) (Chinese) for exactly which local files are read and written. Usage logs contain session titles and project paths; do not paste the `logs` folder into issues.

## Support the project

Stilla is free and open source, with no paid features; donating is entirely optional and unlocks nothing. If it is useful to you, you can support its upkeep through [GitHub Sponsors](https://github.com/sponsors/jly782676356-byte) (one-time or monthly). WeChat / Alipay QR codes are in the [Chinese README](README.md#donate).

## Status

Hobby project, no response-time promise. It depends on Claude Code's local file formats and `statusLine` fields, which can change. What has and has not been verified is listed (in Chinese) at the end of [README.md](README.md).

License: [MIT](LICENSE)

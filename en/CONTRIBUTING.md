# Contributing

> **中文:** [CONTRIBUTING.md (Chinese edition)](../CONTRIBUTING.md)

Issues and pull requests are welcome. This is a hobby project, so replies may be slow.

## When you report a problem, please include

- Your Claude Code version and the client you use (command line / VS Code extension / desktop app / JetBrains)
- Stilla version, Windows version, display scaling
- Which "Quota data source" mode, and the folders listed under right-click → Claude data folders
- For problems with the ↗ jump or the status lights: whether you use the VS Code extension or a terminal, and roughly how the windows were opened
- For screenshots use `Stilla.exe --demo`, and **do not paste files from `%APPDATA%\Stilla\logs`, nor weekly reports, exported CSV files or `mail.txt`** (they contain session titles and project paths, and `mail.txt` contains e-mail addresses)

## Build and test

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1                                  # compile, warnings are errors
powershell -ExecutionPolicy Bypass -File tools\ui-check\run.ps1 -Scenario all       # UI rendering (fake data, output in tools\ui-check\out)
powershell -ExecutionPolicy Bypass -File tools\ui-check\run.ps1 -Scenario q_all     # 0.2.0 layout check: renders and lists clipped / overlapping / border-touching text
```

- `run.ps1` compiles the test harness, generates fake data and renders the chosen scenario to PNG (no visible window pops up). `-Scenario` can be: the older scenarios `compact` `edge` `scale` `panel` `views` `resize` `panelscale` `toggle` `consent` (`all` runs these), `hero` `badge` `settings` (used for the screenshots), and the 0.2.0 layout-check scenarios `q_default` `q_simple` `q_views` `q_every` `q_sizes` `q_scale` `q_edge` `q_tips` `q_dialogs` (`q_all` = all of them). Besides the PNGs, the `q_*` scenarios write `issues_<scenario>.txt`, which lists the clipped / overlapping / border-touching text they found. Optional parameters: `-Out` (output folder), `-Work` (work folder for the build and the fake data; give each parallel run its own; **it is emptied at every run**, so only point it at a folder made for this), `-Src` (source folder, `src` by default).
- English text is usually longer than the Chinese text, so clipped labels show up in this tree first: after changing any label or layout, run `q_all` and read the `issues_*.txt` files.
- **The logic tests are not in this folder.** `tools\ui-check\run-logic.ps1` and `LogicTests.cs` assert on the Chinese UI texts, so they exist only in the Chinese tree (the repository root). If you change logic that affects numbers (quota conversion, speed, forecast, weekly report, email rules…), make the change in the Chinese tree (this folder is generated from it, see the last point under "Code conventions") and run `run-logic.ps1` there: it only reads fake data and keeps its settings in a temporary folder; options `-Work`, `-Filter` (run only the test groups whose name contains the text) and `-Strict` (count known issues as failures); exit code 0 = no failure, 1 = failures, 2 = compile or start-up failure.

If you change the UI (`src\Skins*.cs`, or the drawing and layout code in `Main.cs` / `WidgetExtra.cs` / `Tips.cs` and so on), please run the rendering test and check that no text is truncated or overlapping in the 16 skins in paper / ink, in the `edge` scenarios (no data, 100%, 0%, unknown reset time…) and in the `scale` scenarios; for the new panel also run `q_all`.

## Source layout

In `src\`:

- `Main.cs`: the main window `Widget` (one very large partial class), quota reading, usage statistics and logging.
- `WidgetExtra.cs`, `Extras.cs`, `Features.cs`, `Tips.cs`, `Details.cs`, `Jump.cs`, `Mail.cs`, `Report.cs`: new in 0.2.0, **more parts of `Widget`** (the same partial class) and a few separate classes. Roughly:
  - `Features.cs`: the feature switches, the three profiles (`Feat`) and the "Features and display settings" dialog
  - `WidgetExtra.cs`: applying a profile, the estimate self-check, the trend chart, the weekly report entry, the ⋮ menu
  - `Extras.cs`: pausing, time tab presets, the speed window, reminder settings, export, row spacing
  - `Tips.cs`: hover tips (all fixed texts are in `TipText`; tips that can only be written from the current data are `XxxTip()` methods)
  - `Details.cs`: the detail popup that opens when you click a session in the Model view
  - `Jump.cs`: the ↗ jump, and the log history used for speed statistics
  - `Mail.cs`: reading and writing the email reminder settings, sending, and the settings dialog
  - `Report.cs`: the weekly report
- `Source.cs`, `Shared.cs`, `Update.cs`, `Settings.cs`, `Accounts.cs`, `Skins.cs`, `Skins2.cs`, `Installer.cs`, `Bridge.cs`: the quota source, shared code, the update check, the settings window, accounts, the 16 skins, the installer and the status-line helper.

## Code conventions

- **C# 5**: compiled with the `csc.exe` that ships with Windows (no SDK needed), so do not use `?.`, `$"…"`, expression-bodied members, `nameof` or `out var`. The official build treats warnings as errors, so do not introduce new warnings.
- `src\Shared.cs` is compiled into both the widget and the console helper `Stilla.Statusline.exe`, so it **must not reference WinForms**.
- Keep new text constants in one place; text that does not fit in the UI goes through `SkinDraw.Fit` (shrink the font first, then an ellipsis) so that it never ends up as "1…". Fixed hover-tip texts go into `TipText`.
- A new settings key: change both `LoadSettings` and `SaveSettings` (`Main.cs`), and add a row to "Keys in settings.txt you can edit by hand" in `docs\USAGE.md`. A background-feature switch is a bit in `Feat` (`Features.cs`); new background reading has to respect "Pause background reading" and the profile.
- **Anything new that reads a new file, writes a new file or uses the network must update `PRIVACY.md`** (the three tables: network / reads / writes), and has to be off by default or ask the user first. The email reminder is currently the only feature that sends data to a server the user configured; its password is kept with DPAPI and must never go into a log or the UI.
- Never write anything into `~\.claude` unless the user explicitly agreed in the UI (currently only the `statusLine` entry of `settings.json`, see `ClaudeSettings`).
- Update check (`src\Update.cs`): for tests set the environment variable `STILLA_UPDATE_URL=http://localhost:<port>/...` to a fake server on this machine (an `http://localhost` link is only accepted while this variable exists); `STILLA_NO_UPDATE_CHECK=1` turns the check off. Do not commit the repository placeholder `OWNER` as anything other than a real user name.
- Tests / screenshots always use fake data: `STILLA_DEMO=1`, `STILLA_HOME=<temporary folder>`, `CLAUDE_CONFIG_DIR=<folder generated by tools\make_demo_data.py>`, `STILLA_ISOLATED=1` (`run.ps1` already sets them for you). **Never use real session titles, project paths or e-mail addresses as test data or in screenshots.**
- The `src` of this folder is **generated** from the Chinese-edition source in the repository root (see "Build from source" in the README): do not edit it directly. Describe the English wording change or problem in an issue or a PR and it will be folded into the translation table.

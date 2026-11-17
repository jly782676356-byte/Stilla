# 参与贡献

> **English:** [CONTRIBUTING.md (English edition)](https://github.com/jly782676356-byte/Stilla/blob/main/en/CONTRIBUTING.md)

欢迎 issue 和 PR。这是个业余项目，响应可能比较慢。

## 报问题时请带上

- Claude Code 版本，和你用的客户端（命令行 / VS Code 扩展 / 桌面端 / JetBrains）
- Stilla 版本、Windows 版本、屏幕缩放比例
- 哪个「额度数据来源」模式、右键 → Claude 数据目录 里列出的目录
- 跳转（↗）或状态灯的问题：你用的是 VS Code 扩展还是终端，窗口大概是怎么开的
- 截图时请用 `Stilla.exe --demo`，**不要贴 `%APPDATA%\Stilla\logs` 里的文件，也不要贴周报、导出的 CSV 和 `mail.txt`**（里面有会话标题、项目路径，`mail.txt` 里还有邮箱地址）

## 构建与测试

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1                                  # 编译，警告当错误
powershell -ExecutionPolicy Bypass -File tools\ui-check\run.ps1 -Scenario all       # 界面渲染（假数据，输出 tools\ui-check\out）
powershell -ExecutionPolicy Bypass -File tools\ui-check\run.ps1 -Scenario q_all     # 0.2.0 的版面检查：渲染 + 列出被截断 / 重叠 / 贴边的文字
powershell -ExecutionPolicy Bypass -File tools\ui-check\run-logic.ps1               # 逻辑测试（不出截图、不弹窗口，假数据）
```

- `run.ps1`：编译测试台，生成假数据，按场景渲染成 PNG（不弹出看得见的窗口）。`-Scenario` 可选：旧场景 `compact` `edge` `scale` `panel` `views` `resize` `panelscale` `toggle` `consent`（`all` 就是跑这几个），截图用的 `hero` `badge` `settings`，以及 0.2.0 的版面检查场景 `q_default` `q_simple` `q_views` `q_every` `q_sizes` `q_scale` `q_edge` `q_tips` `q_dialogs`（`q_all` = 全部）。`q_*` 场景除了 PNG，还会写 `issues_<场景>.txt`，列出发现的被截断 / 重叠 / 贴边的文字。可选参数：`-Out`（输出文件夹）、`-Work`（编译和假数据的工作文件夹，几个人同时跑要各给一个；**每次运行都会把它清空**，只给专门用的文件夹）、`-Src`（源码文件夹，默认 `src`）。
- `run-logic.ps1`：逻辑测试（`tools\ui-check\LogicTests.cs`），只读假数据，设置放在临时文件夹。可选参数：`-Work`（工作文件夹，每次运行会清空，所以只能给空的或者它自己建过的）、`-Filter`（只跑名字里带这段文字的测试组）、`-Strict`（把已知问题也算失败）。退出码：0 = 没有失败，1 = 有失败，2 = 编译或启动失败；结果也写在 `<Work>\results.txt`。

改了界面（`src\Skins*.cs`，或者 `Main.cs` / `WidgetExtra.cs` / `Tips.cs` 等里的绘制和布局）请跑一遍渲染测试，看 16 种风格在纸色 / 墨底、`edge`（没数据、100%、0%、重置时间未知…）和 `scale` 场景下有没有文字被截断、重叠，新面板的话再跑 `q_all`。改了会影响数字的逻辑（额度换算、速度、预测、周报、邮件规则…）请跑一遍 `run-logic.ps1`。

## 源码结构

`src\` 里：

- `Main.cs`：主窗口 `Widget`（一个很大的 partial 类）、额度读取、用量统计和日志。
- `WidgetExtra.cs`、`Extras.cs`、`Features.cs`、`Tips.cs`、`Details.cs`、`Jump.cs`、`Mail.cs`、`Report.cs`：0.2.0 新增，**`Widget` 的另一些部分**（同一个 partial 类）和几个独立的类。大致分工：
  - `Features.cs`：功能开关、三档调度（`Feat`）和「功能与显示设置」对话框
  - `WidgetExtra.cs`：档位的应用、估算自检、趋势图、周报入口、「⋮」菜单
  - `Extras.cs`：暂停、时间标签预设、速度窗口、提醒设置、导出、行距
  - `Tips.cs`：悬停说明（所有固定说明文字都在 `TipText` 里，要读当前数据才能写的说明是 `XxxTip()` 方法）
  - `Details.cs`：模型视图点会话弹出的详情小窗
  - `Jump.cs`：↗ 跳转，以及速度统计用的日志历史
  - `Mail.cs`：邮件提醒的设置存取、发信和设置对话框
  - `Report.cs`：周报
- `Source.cs`、`Shared.cs`、`Update.cs`、`Settings.cs`、`Accounts.cs`、`Skins.cs`、`Skins2.cs`、`Installer.cs`、`Bridge.cs`：额度来源、共享代码、更新检查、设置窗口、账号、16 种风格、安装程序、状态栏小程序。

## 代码约定

- **C# 5**：用 Windows 自带的 `csc.exe` 编译（不依赖 SDK），所以不要用 `?.`、`$"…"`、表达式体成员、`nameof`、`out var`。正式构建把警告当错误，别引入新警告。
- `src\Shared.cs` 同时被悬浮窗和控制台小程序 `Stilla.Statusline.exe` 编译，**不能引用 WinForms**。
- 新增文字常量尽量放在一处；界面里放不下的文字用 `SkinDraw.Fit`（先缩字号，再省略号），别让它被截成「1…」。悬停说明的固定文字放进 `TipText`。
- 新增一个设置键：同时改 `LoadSettings` 和 `SaveSettings`（`Main.cs`），并在 `docs\USAGE.md` 的「settings.txt 里可以手动改的键」里补一行。后台功能开关是 `Feat`（`Features.cs`）里的一位，新的后台读取要遵守「暂停后台读取」和对应的档位。
- **新增任何会读新文件、写新文件或联网的功能，同步改 `PRIVACY.md`**（读 / 写 / 联网三张表），并且默认关闭或先征得用户同意。邮件提醒是现在唯一一个会向用户自己配置的服务器发数据的功能；它的密码用 DPAPI 存放，不要写进日志或界面。
- 不要往 `~\.claude` 里写任何东西，除非是用户在界面里明确同意的（现在只有 `settings.json` 的 `statusLine`，见 `ClaudeSettings`）。
- 更新检查（`src\Update.cs`）：测试时用环境变量 `STILLA_UPDATE_URL=http://localhost:<端口>/...` 指向本机的假服务器（只在这个变量存在时才允许 `http://localhost` 链接）；`STILLA_NO_UPDATE_CHECK=1` 关闭检查。仓库地址占位符 `OWNER` 不要提交成真实用户名以外的东西。
- 测试 / 截图一律用假数据：`STILLA_DEMO=1`、`STILLA_HOME=<临时目录>`、`CLAUDE_CONFIG_DIR=<`tools\make_demo_data.py` 生成的目录>`、`STILLA_ISOLATED=1`（`run.ps1` / `run-logic.ps1` 已经替你设好）。**别拿真实的会话标题、项目路径、邮箱做测试数据或截图。**
- `en\` 是从中文版源码生成的，不要直接改里面的 `src`（见 README「从源码构建」）。

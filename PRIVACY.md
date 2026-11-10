# 隐私：Stilla 读什么、写什么、联不联网

适用版本：0.1.1。源码都在仓库里，下面每一条都能在代码里对上（文件路径见括号）。

## 联网

| 什么时候 | 联网吗 |
|---|---|
| **额度来源 = 状态栏**，或「自动」（默认）里状态栏有新数据时 | 这一项**不联网、不读登录令牌**。没有遥测、没有崩溃上报。 |
| **检查更新**（默认开，设置里可关） | 每次启动约 4 秒后，向 `https://api.github.com/repos/<owner>/<repo>/releases/latest` 发**一个 GET 请求**，只问"最新 Release 的版本号和页面地址"。请求里只有 `User-Agent: Stilla/<版本号>` 和 `Accept: application/vnd.github+json`，**不带 cookie、不带令牌、不带你的任何数据**（GitHub 当然能看到你的 IP，和访问任何网站一样）。不会自动下载、不会自动安装；接口返回的链接只有以 `https://github.com/` 开头才会采用。关掉：设置 → 取消勾选，或环境变量 `STILLA_NO_UPDATE_CHECK=1`。如果你 fork 了本项目、没有改仓库地址，你构建出来的版本会检查本项目的新版本；用 `tools\set-repo.ps1` 可以改成你自己的仓库。（`src/Update.cs`） |
| 在线查询，或「自动」里状态栏没有新数据时（**都需要你在弹窗里确认**；「自动」是默认来源，所以第一次状态栏没数据时会弹一次提问，点「否」就不会读令牌、不会联网，之后不再问；答案记在 `settings.txt` 的 `onl=`） | 请求 `https://api.anthropic.com/api/oauth/usage`，带上本机 Claude 登录令牌（`Bearer`）。每 60 秒一次，只读，不发消息。令牌只在内存里用，不保存、不发给别处。（`src/Main.cs` 的 `UsageClient`） |

邮件、云同步、崩溃上报、使用统计：都没有。点「前往更新页面」/「捐赠…」只是用你的默认浏览器打开一个网址。

## 读本机的哪些文件（只读，共享方式打开，不会阻止 Claude Code 写入）

| 文件 | 读什么 | 什么时候读 |
|---|---|---|
| `<Claude 数据目录>\projects\**\*.jsonl`（会话记录） | 含 `usage` 的行会被解析：时间戳、会话 ID、消息 ID、模型名、各类 token 数、思考强度、工作目录（用来显示项目名）、会话标题（`ai-title` / `custom-title`）。**整行 JSON 会被解析进内存，所以对话文字也经过内存，但程序不取、不保存、不上传。** | 打开"显示用量统计"，或开着"记录用量到日志"时 |
| `<Claude 数据目录>\sessions\*.json` | 会话名称、名称来源 | 同上，每 30 秒 |
| `<Claude 数据目录>\.credentials.json` | 识别账号 / 套餐：`organizationUuid`、`subscriptionType`、`rateLimitTier`。**只有你同意了在线查询（弹窗点「是」，或手动选了「在线查询」）才会取出 `accessToken` 用来请求接口** | 每 30 秒 |
| `~\.claude.json`（或 `<Claude 数据目录>\.claude.json`） | `oauthAccount`：邮箱（界面和档案里只存、只显示打码后的形式，如 `a***9@example.com`）、订阅创建时间、组织类型 | 每 30 秒 |
| VS Code 的 `state.vscdb`（`%APPDATA%\Code` / `Code - Insiders` / `Cursor` / `VSCodium` / `Windsurf` 下） | 只读打开，取 `Anthropic.claude-code` 键里的会话标签标题 | 每 10 分钟，通过 `scripts\sync_vscode_titles.py`（需要 Python；没有就不执行） |

"Claude 数据目录"：`CLAUDE_CONFIG_DIR`，或 `~\.claude`，加上自动检测到的 / 你手动添加的目录。右键 → Claude 数据目录 可以看到全部。

## 写哪些文件

全部在 `%APPDATA%\Stilla\`（便携模式：`Stilla.exe` 旁的 `data\`）：

| 文件 | 内容 |
|---|---|
| `settings.txt` | 窗口位置、大小、透明度、风格、选项（纯数字；包括 `usage=` 简洁 / 详细模式、`src=` 额度来源、`onl=` 是否同意在线查询） |
| `rate_limits.json` | 状态栏小程序写的：5 小时 / 每周的百分比和重置时间 |
| `roots.txt` | 你手动添加的 Claude 数据目录 |
| `update.txt` | 最近一次知道的最新版本号、更新页面地址、已经提醒过哪个版本、上次检查时间 |
| `statusline-chain-*.txt` | 你原来的状态栏命令（串联用），每个配置目录一份 |
| `logs\用量明细_v2.csv`、`用量标注.csv` | 每条 API 消息一行：时间、**会话 ID、项目名（来自工作目录）**、模型、各类 token、任务 ID、工具次数 / 名称、思考强度 |
| `logs\会话索引.csv` | 每个会话一行：**标题、开始 / 结束时间、会话文件路径**、各类 token 总数 |
| `logs\额度快照.csv` | 5 小时 / 每周百分比和重置时间的快照 |
| `logs\账号.csv`、`账号时段.csv` | 你的账号档案：名称、计划、订阅开始日期、月费、**绑定的组织 ID、打码邮箱**、切换记录 |
| `logs\额度换算*.csv / .txt`、`vscode标题.csv`、`读取进度*.txt` | 估算系数、VS Code 标题缓存、读取进度 |

**这些 CSV 里有你的会话标题、项目路径、组织 ID 和订阅信息。报问题时不要贴 / 上传 `logs` 文件夹。** 想清除：删掉整个 `%APPDATA%\Stilla`（卸载时可以选择一起删）。

### 只在你同意时才会改的文件

`~\.claude\settings.json`（或 `<CLAUDE_CONFIG_DIR>\settings.json`）里的 `statusLine`：只有你在安装程序里勾选、或在设置对话框里点「是」时才会改。改之前备份成 `settings.json.stilla-backup-<时间>`；JSON 解析不了就不动；你已有的状态栏原样保留（串联）；卸载 / 「撤销状态栏设置」会还原。Stilla **从不修改**会话记录、`.credentials.json`、`.claude.json`，也从不刷新或改写登录令牌。

## 启动别的程序

- `python`（后台、隐藏窗口、可选）：运行 `scripts\quota_fit.py`（额度换算，每小时一次）和 `scripts\sync_vscode_titles.py`（每 10 分钟）。脚本是纯本地的，不联网。
- `explorer.exe`：点"打开日志文件夹 / 打开会话记录"时。

## 其他

- 系统通知（托盘气泡）：账号切换、订阅续费提醒。
- 安装程序在 `HKCU`（当前用户）里写卸载信息，勾选"开机自动启动"时写 `Run` 项；不需要管理员权限；卸载时清理。

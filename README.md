# Stilla

**给 Claude Code 用的额度悬浮窗。** 一个很小的、始终置顶的 Windows 窗口，随时显示 5 小时额度和每周额度用了多少、什么时候重置，还能按会话 / 模型统计 token 用量。

> **Stilla 是独立的社区项目，不是 Anthropic 出品，也没有得到 Anthropic 的认可或支持。**
> "Claude" 是 Anthropic, PBC 的商标，这里只用来说明本工具是给 Claude Code 用的。

> *Stilla is an independent community project, not created, endorsed or supported by Anthropic. "Claude" is a trademark of Anthropic, PBC, used here only to say what the tool works with.*

**English:** a tiny always-on-top Windows widget for Claude Code. It shows how much of your 5-hour and weekly limits you have used, when they reset, and where your tokens go (by session / model). The app's UI is currently Chinese only. Full English page: [README.en.md](README.en.md).

![Stilla](docs/screenshots/hero.png)

*截图均为虚构的演示数据。 / All screenshots show made-up demo data.*

## 它解决这几件事

| 你遇到的问题 | Stilla 怎么做 |
|---|---|
| 不知道还剩多少 | 两滴墨常驻，60% 变橙、85% 变红，到顶前先知道 |
| 不知道谁最费 | 会话视图一行一个会话，缓存剩余也在上面 |
| 不知道哪个模型花得多 | 模型视图折成额度，思考档位也单独列出（额度折算需要 Python，可选） |
| token 多就以为费额度 | 对比视图并排占比，缓存读便宜，看额度更准 |
| 订阅不知值不值 | 折成 API 金额，算出回本倍数 |
| 怕额度提前用完 | 按近 12 小时的速度预测每周额度会不会在重置前用完，提前调整 |
| 续费日记不住 | 自动识别账号，续费前系统通知 |
| 想看某个时段 | 11 个时间标签，可自定义 |
| 怕挡着写代码 | 16 种风格，透明度可调，`Ctrl+Alt+B` 反色 |

额度折算是估算，误差约 5%，用来判断趋势，不当账单（程序里会显示当前的"估算误差"）。

## 能做什么

- **额度悬浮窗**：5 小时 / 每周额度的已用百分比、重置时间、倒计时。可拖动、可调大小、背景和文字透明度分别可调、缩到托盘。
- **简洁 / 详细两种模式**：默认打开就是详细模式（额度 + 用量面板，全部核心功能都在眼前）；点紧凑卡下面那个「▴ 收起，只看额度」按钮，缩成只有额度的简洁窗口，再点「▾ 展开详细统计」回来。
- **16 种界面风格**，每种都有纸色 / 墨底两套（`Ctrl+Alt+N` 换风格，`Ctrl+Alt+B` 黑白切换）。见 [全部风格（纸色）](docs/screenshots/skins-light.png) · [（墨底）](docs/screenshots/skins-dark.png)。
- **用量面板**（详细模式里就有；右键 → 显示用量统计 也可以开关）：按时间段（5 分钟 … 本周，共 11 种可选）统计各会话的输入 / 输出 / 缓存写入 / 缓存读取，以及"缓存还剩多久过期"。五个视图：会话 · 模型 · 对比 · 档位 · 订阅。
- **额度换算**：用你自己的额度读数和用量日志反推"每美元折算多少额度百分点"，估算每个会话 / 模型 / 思考档位各占了多少额度（需要 Python，可选）。
- **账号与订阅**：按登录账号 / 套餐分开统计，算 API 等价金额和"回本"倍数。
- **设置窗口**（右键 → 设置…）：**自动检查更新**（默认每次启动时查一次；有新版本时 Stilla 标志右边出现一个小红点，点「前往更新页面」在浏览器里打开下载页）、**捐赠**入口。
- **不耗 token**：不发消息、不调用模型。空闲时 CPU 约 0.1%。

完整说明见 [docs/USAGE.md](docs/USAGE.md)。

## 安装

只支持 **Windows 10 / 11**（用系统自带的 .NET Framework 4，不用另装运行库）。

1. 从 [Releases](../../releases) 下载 `Stilla-Setup-0.1.0-zh.exe`（目前只有中文界面），双击安装（不需要管理员权限，装在 `%LOCALAPPDATA%\Programs\Stilla`）。不想安装的话下载 `Stilla-0.1.0-portable-zh.zip`，解压即用。
2. 安装程序里**勾选「配置 Claude Code 状态栏」**（见下）。会修改 `~/.claude/settings.json`：先备份、保留你已有的状态栏、卸载时撤销。不勾也行，装好后在 Stilla 右键菜单里再设。
3. 重启 Claude Code，发一条消息，悬浮窗就有数据了。

> 安装包**没有代码签名**，Windows SmartScreen 可能提示"未知发布者"（点「更多信息 → 仍要运行」），个别杀毒软件可能误报。不放心可以自己 [从源码构建](#从源码构建)，或对照 Release 页上的 SHA-256。

静默安装 / 卸载：`Stilla-Setup-0.1.0-zh.exe /S [/D=安装目录] [/nostatusline] [/desktop] [/autostart] [/nolaunch]`；`Stilla-Setup.exe /uninstall /S [/purge]`。

## 额度数字从哪来

Claude Code 没有公开的"查额度"接口，所以有三种方式，在右键 → **额度数据来源** 里选，**默认是「自动」**：状态栏有新数据就用状态栏；没有时才问你要不要改用在线查询（只问一次，见下）。

| 方式 | 做法 | 优点 | 缺点 |
|---|---|---|---|
| **状态栏** | Claude Code 官方的 [`statusLine`](https://code.claude.com/docs/en/statusline) 功能会把 `rate_limits`（5 小时 / 每周的百分比和重置时间）通过 stdin 交给 `Stilla.Statusline.exe`，它存到本地文件，悬浮窗读这个文件 | 官方有文档的做法；**不读登录令牌，不联网** | 只在 Claude Code 运行时更新；**VS Code 扩展和桌面端不一定会运行状态栏**（见下） |
| **在线查询**（实验性） | 读本机 Claude 登录令牌，请求 Claude Code `/usage` 用的、**没有公开文档的**接口 | 不依赖 Claude Code 的哪个客户端 | 接口随时可能改；Anthropic 的[服务条款](https://code.claude.com/docs/en/legal-and-compliance)对"订阅登录凭据用在 Claude Code 以外的工具里"有限制，**是否合适请你自己判断**。切换时会弹出风险提示 |
| **自动**（默认） | 状态栏数据新鲜（10 分钟内）就用它；没有时才在线查询，而且**要先经过你同意**：第一次状态栏没数据时会弹一个说明（读登录令牌、请求没有公开文档的接口、服务条款提示），点「是」才会启用，点「否」就只用状态栏，之后不再问 | 命令行用户只靠状态栏就够；VS Code / 桌面端用户同意后也能立刻有数据 | 同意后同"在线查询" |

**串联已有状态栏**：如果你的 `settings.json` 里已经有自己的 `statusLine`，安装程序 / 设置对话框不会覆盖它：原命令会保存下来，由 `Stilla.Statusline.exe --chain-file …` 转交执行，你原来的状态栏照常显示。

手动配置（不想让程序改你的文件）：把下面这段加到 `~/.claude/settings.json`，路径换成你的：

```json
{
  "statusLine": {
    "type": "command",
    "command": "\"C:\\Users\\你\\AppData\\Local\\Programs\\Stilla\\Stilla.Statusline.exe\" --quiet"
  }
}
```

## 兼容性：不同的 Claude 客户端 / 不同的安装位置

会话用量统计读的是 Claude Code 写在本机的会话记录（`projects\**\*.jsonl`）。Stilla 会**自动找**这些目录，不假设固定位置：

| 来源 | 怎么找 | 状态 |
|---|---|---|
| 命令行（`claude`） | `CLAUDE_CONFIG_DIR` 环境变量，没设就是 `~\.claude` | 按 Claude Code 文档实现 |
| VS Code 扩展 | 同上 | 作者在自己的机器上用着（会话记录来自 VS Code 扩展） |
| 多配置目录（`~\.claude-work` 这种并列目录） | 自动扫描用户目录下的 `.claude*` | 已用假目录测试 |
| 桌面端 / Microsoft Store 版 | 自动扫描 `%APPDATA%\Claude`、`%LOCALAPPDATA%\Claude`、`Packages\*Claude*` 下名叫 `.claude` 的文件夹 | **没有在真机上验证**（作者的机器没有桌面端） |
| WSL、别的盘、别的位置 | 右键 → **Claude 数据目录 → 添加目录…** 手动加 | 可用，需要手动 |
| JetBrains 插件 | 同命令行 | 未验证 |

右键 → **Claude 数据目录** 能看到当前实际读了哪些目录。

**额度百分比**则取决于数据来源（上一节）：状态栏方式在**命令行版**最可靠；社区反馈 **VS Code 扩展不渲染 / 不一定触发 `statusLine`**，桌面端也没有明确资料。如果你主要用 VS Code 或桌面端，状态栏可能一直没有数据 —— 这时在默认的「自动」里点「是」同意在线查询，或者手动选「在线查询」（注意上面的条款提示）；也可以只用会话用量统计功能（它不依赖额度来源）。

## 更新

- **每次启动时检查一次**（右键 → 设置… 里可以关）：向 GitHub 请求一次"最新 Release 是哪个版本"，只带 `User-Agent: Stilla/版本号`，不发送你的任何数据。
- 有新版本：Stilla 标志右边出现一个**小红点**（托盘也会弹一次提示）。点小红点，或右键 → 设置… → **前往更新页面**，在浏览器里打开下载页。**不会自动下载、不会自动安装**。
- 免联网：设置里取消勾选，或设环境变量 `STILLA_NO_UPDATE_CHECK=1`。
- 检查更新要能连上 GitHub（`api.github.com`）。连不上时设置窗口会写"检查失败"，这时不会出现小红点，可以自己打开 [Releases](../../releases) 看。

## 下一版预告（尚未包含在 0.1.0）

下面这些功能已经在作者自己的电脑上用着，会抽空尽快整理上线，不承诺日期。上线后，已经装了 Stilla 的人会在悬浮窗上看到更新小红点，不用自己去搜更新。

| 你遇到的问题 | 下一版怎么做 |
|---|---|
| 缓存过期，重新载入贵 | 每个会话一盏灯：绿 = 运行中，蓝 = 等你处理，倒计时越短越黄越红 |
| 会话一多，找不到等回复的 | 会话名点击转跳到所在窗口 |
| 忙别的忘了续缓存 | 快过期弹系统通知；还能发邮件到你自己的邮箱（自备 SMTP） |
| 不知道谁在烧额度 | 每个会话显示近 15 分钟的速度和占用 |
| 撑不撑得到重置没底 | 合计行直接写：照现在的速度，5 小时额度约几小时后触顶，每周额度会不会提前用完 |
| 一周花在哪记不清 | 每周自动出 HTML 周报：每天花费、小时热力图、模型 × 档位、最烧的会话、较上周变化 |
| 找不到最费的会话 | 表头点一下排序，成本列显示 API 等价金额 |
| 会话用了什么模型 | 模型视图点会话：各模型 × 档位的用量，中途切换的时间线，可复制 |
| 接了非 Claude 模型，花销没人算 | API 视图列出用量和估算费用 |
| 折算准不准、数字怎么算的 | 估算自检有偏差会提示，鼠标停一下弹出说明 |
| 打游戏怕占资源 | 三档调度，精简只留缓存提醒；还能一键暂停后台读取 |

<a name="donate"></a>

## 支持这个项目

Stilla 免费开源，没有任何付费功能，捐赠完全自愿，也不会解锁任何东西。如果它对你有帮助，欢迎支持作者继续维护：

- **GitHub Sponsors**（一次性或按月，海外用户用这个方便）：<https://github.com/sponsors/jly782676356-byte>
- **微信 / 支付宝**（扫码）：

| 微信 | 支付宝 |
|---|---|
| <img src="assets/donate/wx.jpg" alt="微信收款码" width="220"> | <img src="assets/donate/zfb.jpg" alt="支付宝收款码" width="220"> |

程序里 设置… → 捐赠… 会打开这一节。

## 隐私

默认的「自动」来源下，**没有经过你同意就不读登录令牌**，状态栏有数据时也根本用不到它；不上传任何东西。联网只有一处：启动时检查更新（上面说的，可以关）；你同意在线查询（或手动选了「在线查询」）之后才会多一处。读哪些本地文件、写哪些文件、什么情况下会联网，全部写在 [PRIVACY.md](PRIVACY.md)。一句话：用量日志里会有会话标题和项目路径，**报问题时不要贴 `logs` 文件夹**。

## 常见问题

- **想只看额度、不要用量面板**：点紧凑卡下面的「▴ 收起，只看额度」（或右键取消「显示用量统计」）。
- **悬浮窗底部写"未收到状态栏数据"**：还没配状态栏，或配了但 Claude Code 还没运行过状态栏。右键 → 额度数据来源 → 设置 Claude Code 状态栏；然后重启 Claude Code 并发一条消息。VS Code / 桌面端见上面的兼容性说明。
- **"已 N 分钟没更新"**：状态栏数据只在 Claude Code 运行时更新。
- **没有"额度估算"/ 会话标题不对**：这两项靠 Python 脚本（`scripts\`），没装 Python 时自动不可用，其余照常。会话标题优先用 VS Code 里显示的标题。
- **想换位置 / 便携使用**：在 `Stilla.exe` 旁放一个空文件 `portable.txt`，设置和日志就放到旁边的 `data` 文件夹。
- **不想被统计**：右键 → 取消"记录用量到日志"；不勾"显示用量统计"时完全不读会话文件。

## 从源码构建

不需要 Visual Studio，不需要 .NET SDK，Windows 自带的 .NET Framework 4 编译器就够：

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1
```

输出在 `dist\`：`Stilla.exe`、`Stilla.Statusline.exe`、`scripts\`、`Stilla-Setup-0.1.0-zh.exe`、`Stilla-0.1.0-portable-zh.zip`。要发 Release 的话，再运行 `tools\make-release.ps1 -OutDir <文件夹>`，会整理好安装包、zip、`SHA256SUMS` 和 Release 说明。源码是 C# 5（系统自带 `csc.exe` 的版本），所以不要用 `?.` 和字符串插值。贡献说明见 [CONTRIBUTING.md](CONTRIBUTING.md)。

**自己 fork 一份来改的话**：运行 `tools\set-repo.ps1 -Owner 你的用户名` 把更新检查 / 捐赠入口指向你自己的仓库，然后重新构建（不改的话，你构建出来的版本会去检查本项目的新版本）。

界面渲染测试（16 种风格 × 纸色 / 墨底 × 多种窗口尺寸 / DPI 缩放，用假数据画成 PNG，不弹窗口）：

```powershell
powershell -ExecutionPolicy Bypass -File tools\ui-check\run.ps1 -Scenario all
```

## 验证状态（诚实版）

**已在开发机上验证**：全部代码编译（警告当错误）；`Stilla.Statusline.exe` 读 stdin / 写文件 / 串联原状态栏 / 容错（用合成输入）；对 `settings.json` 的写入与撤销（备份、保留其他字段、坏 JSON 不动、已有状态栏串联与还原）；安装 / 卸载（含带空格的路径、清理干净）；多数据目录检测（用假目录）；16 种风格在纸色 / 墨底、多种窗口尺寸、1.25× / 1.5× / 2× 缩放下的渲染。

**没有验证**：真实的 Claude Code 是否会按预期调用状态栏小程序（命令行 / VS Code / 桌面端都没有实测）；桌面端会话目录的真实位置；WSL；多显示器间 DPI 切换；「在线查询」模式在今天的可用性（沿用原实现，这次整理没有重新联网测试）。

## 项目状态

个人业余项目，没有响应时间承诺。它依赖 Claude Code 本地文件的格式和 `statusLine` 的字段，这些随 Claude Code 版本变化，可能随时失效 —— 遇到请开 issue，并写上 Claude Code 版本和你用的客户端。

## 许可证

[MIT](LICENSE)

# 参与贡献

> **English:** [CONTRIBUTING.md (English edition)](https://github.com/jly782676356-byte/Stilla/blob/main/en/CONTRIBUTING.md)

欢迎 issue 和 PR。这是个业余项目，响应可能比较慢。

## 报问题时请带上

- Claude Code 版本，和你用的客户端（命令行 / VS Code 扩展 / 桌面端 / JetBrains）
- Stilla 版本、Windows 版本、屏幕缩放比例
- 哪个「额度数据来源」模式、右键 → Claude 数据目录 里列出的目录
- 截图时请用 `Stilla.exe --demo`，**不要贴 `%APPDATA%\Stilla\logs` 里的文件**（里面有会话标题和项目路径）

## 构建与测试

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1                                  # 编译，警告当错误
powershell -ExecutionPolicy Bypass -File tools\ui-check\run.ps1 -Scenario all       # 界面渲染（假数据，输出 tools\ui-check\out）
```

改了界面（`src\Skins*.cs`）请跑一遍渲染测试，看 16 种风格在纸色 / 墨底、`edge`（没数据、100%、0%、重置时间未知…）和 `scale` 场景下有没有文字被截断、重叠。

## 代码约定

- **C# 5**：用 Windows 自带的 `csc.exe` 编译（不依赖 SDK），所以不要用 `?.`、`$"…"`、表达式体成员。
- `src\Shared.cs` 同时被悬浮窗和控制台小程序 `Stilla.Statusline.exe` 编译，**不能引用 WinForms**。
- 新增文字常量尽量放在一处；界面里放不下的文字用 `SkinDraw.Fit`（先缩字号，再省略号），别让它被截成「1…」。
- 不要往 `~\.claude` 里写任何东西，除非是用户在界面里明确同意的（现在只有 `settings.json` 的 `statusLine`，见 `ClaudeSettings`）。
- 更新检查（`src\Update.cs`）：测试时用环境变量 `STILLA_UPDATE_URL=http://localhost:<端口>/...` 指向本机的假服务器（只在这个变量存在时才允许 `http://localhost` 链接）；`STILLA_NO_UPDATE_CHECK=1` 关闭检查。仓库地址占位符 `OWNER` 不要提交成真实用户名以外的东西。
- 测试 / 截图一律用假数据：`STILLA_DEMO=1`、`STILLA_HOME=<临时目录>`、`CLAUDE_CONFIG_DIR=<`tools\make_demo_data.py` 生成的目录>`、`STILLA_ISOLATED=1`。

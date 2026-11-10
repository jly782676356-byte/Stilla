// Stilla.Statusline.exe 的入口：控制台程序，给 Claude Code 的 statusLine 调用。
// 用法（写进 ~/.claude/settings.json）：
//   "statusLine": { "type": "command", "command": "\"C:\\路径\\Stilla.Statusline.exe\" --quiet" }
// 想保留原来的状态栏：--chain "你原来的命令"，原命令会收到同样的 stdin，它的输出原样显示。

namespace Stilla
{
    internal static class BridgeProgram
    {
        private static int Main(string[] args)
        {
            return StatusLineCli.Run(args);
        }
    }
}

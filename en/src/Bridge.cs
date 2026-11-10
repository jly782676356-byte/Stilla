// Entry point of Stilla.Statusline.exe: a console program that Claude Code's statusLine calls.
// Usage (put it in ~/.claude/settings.json):
//   "statusLine": { "type": "command", "command": "\"C:\\path\\Stilla.Statusline.exe\" --quiet" }
// To keep your existing status line: --chain "your original command"; the original command receives the same stdin and its output is shown unchanged.

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

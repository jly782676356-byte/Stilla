# -*- coding: utf-8 -*-
"""把 VS Code 里 Claude Code 扩展显示的会话标题导出到「logs\vscode标题.csv」，悬浮窗读它来显示会话名。

数据来源：VS Code 工作区数据库（state.vscdb）里 Anthropic.claude-code 键的 panelTabSessions。只读方式打开，不修改 VS Code 的任何数据。
用法：python sync_vscode_titles.py [数据目录]   （悬浮窗每 10 分钟自动调用一次；没装 Python 时这个功能自动不可用，其余照常）
"""
import csv
import glob
import json
import os
import sqlite3
import sys

base = sys.argv[1] if len(sys.argv) > 1 else os.path.dirname(os.path.abspath(__file__))
# VS Code 和它的常见衍生版（Insiders、Cursor、VSCodium、Windsurf）：哪个装了就读哪个
dbs = []
for app in ('Code', 'Code - Insiders', 'Cursor', 'VSCodium', 'Windsurf'):
    code_user = os.path.join(os.environ.get('APPDATA', ''), app, 'User')
    dbs += glob.glob(os.path.join(code_user, 'workspaceStorage', '*', 'state.vscdb'))
    dbs.append(os.path.join(code_user, 'globalStorage', 'state.vscdb'))

mapping = {}
for db in dbs:
    if not os.path.exists(db):
        continue
    try:
        con = sqlite3.connect('file:' + db.replace('\\', '/') + '?mode=ro', uri=True)
        row = con.execute("SELECT value FROM ItemTable WHERE key = 'Anthropic.claude-code'").fetchone()
        con.close()
    except sqlite3.Error:
        continue
    if not row:
        continue
    try:
        data = json.loads(row[0])
    except ValueError:
        continue
    for tab in data.get('panelTabSessions', []):
        sid, title = tab.get('sessionId'), tab.get('title')
        if sid and title and sid not in mapping:
            mapping[sid] = title.strip()

out = os.path.join(base, 'vscode标题.csv')
tmp = out + '.tmp'
with open(tmp, 'w', encoding='utf-8-sig', newline='') as f:
    writer = csv.writer(f)
    writer.writerow(['会话ID', 'VSCode标题'])
    for sid in sorted(mapping):
        writer.writerow([sid, mapping[sid]])
os.replace(tmp, out)
print('已导出 %d 个会话的标题：%s' % (len(mapping), out))

# -*- coding: utf-8 -*-
"""Exports the session titles that the Claude Code extension shows in VS Code to "logs\vscode_titles.csv", which the widget reads to show session names.

Data source: the panelTabSessions of the Anthropic.claude-code key in the VS Code workspace database (state.vscdb). Opened read-only; no VS Code data is modified.
Usage: python sync_vscode_titles.py [data folder]   (the widget calls it automatically every 10 minutes; without Python this feature is simply unavailable and everything else works as usual)
"""
import csv
import glob
import json
import os
import sqlite3
import sys

base = sys.argv[1] if len(sys.argv) > 1 else os.path.dirname(os.path.abspath(__file__))
# VS Code and its common derivatives (Insiders, Cursor, VSCodium, Windsurf): read whichever is installed
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

out = os.path.join(base, 'vscode_titles.csv')
tmp = out + '.tmp'
with open(tmp, 'w', encoding='utf-8-sig', newline='') as f:
    writer = csv.writer(f)
    writer.writerow(['Session ID', 'VSCode title'])
    for sid in sorted(mapping):
        writer.writerow([sid, mapping[sid]])
os.replace(tmp, out)
print('Exported the titles of %d sessions: %s' % (len(mapping), out))

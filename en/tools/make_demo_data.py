#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Generates a "fake Claude Code data folder" for screenshots and UI tests (it contains no real data).

Usage:
    python tools/make_demo_data.py <output folder>
Then:
    set CLAUDE_CONFIG_DIR=<output folder>\\claude
    set STILLA_HOME=<output folder>\\home
    Stilla.exe --demo

Contents: 6 sessions (one with a very long title, one without a title, one with subtasks), with all times close to "now", so regenerate it before every test.
"""
import json
import os
import random
import sys
import datetime as dt

out = sys.argv[1] if len(sys.argv) > 1 else os.path.join(os.getcwd(), 'demo')
cfg = os.path.join(out, 'claude')
home = os.path.join(out, 'home')
logs = os.path.join(home, 'logs')
proj = os.path.join(cfg, 'projects', 'C--demo-project-a')
os.makedirs(proj, exist_ok=True)
os.makedirs(logs, exist_ok=True)

rnd = random.Random(20260110)
now = dt.datetime.now(dt.timezone.utc)

# (session id prefix, title, model, minutes ago it started, message count, minutes between messages, cache validity in minutes, number of subtasks)
SESSIONS = [
    ('a1b2c3', 'Refactor the login module', 'claude-sonnet-5-5', 170, 40, 4.0, 60, 2),
    ('b2c3d4', 'Fix flaky test failures', 'claude-opus-5-5', 120, 28, 4.0, 60, 0),
    ('c3d4e5', 'Tidy up the release notes', 'claude-sonnet-5-5', 95, 20, 4.5, 5, 0),
    ('d4e5f6', 'An extremely long session title that is used to check that the ellipsis displays correctly', 'claude-haiku-5-5', 70, 22, 3.0, 60, 1),
    ('e5f6a7', 'Explore the data pipeline', 'claude-sonnet-5-5', 300, 14, 20.0, 60, 0),
    ('f6a7b8', None, 'claude-sonnet-5-5', 40, 9, 4.0, 60, 0),       # No title: shown as "project name - session ID"
]


def iso(t):
    return t.strftime('%Y-%m-%dT%H:%M:%S.') + '%03dZ' % (t.microsecond // 1000)


def usage(model, ttl, ctx):
    inp = rnd.randint(2, 90)
    out_t = rnd.randint(150, 4200)
    cw = rnd.randint(500, 9000)
    cr = ctx + rnd.randint(0, 8000)
    cc = {'ephemeral_5m_input_tokens': 0, 'ephemeral_1h_input_tokens': 0}
    cc['ephemeral_1h_input_tokens' if ttl == 60 else 'ephemeral_5m_input_tokens'] = cw
    return {'input_tokens': inp, 'output_tokens': out_t,
            'cache_creation_input_tokens': cw, 'cache_read_input_tokens': cr, 'cache_creation': cc}


def write_session(path, sid, title, model, start_min, n, gap, ttl, is_sub=False):
    lines = []
    if title and not is_sub:
        lines.append(json.dumps({'type': 'ai-title', 'aiTitle': title}, ensure_ascii=False))
    t = now - dt.timedelta(minutes=start_min)
    ctx = 30000
    for i in range(n):
        t = t + dt.timedelta(minutes=gap * rnd.uniform(0.7, 1.3))
        if t > now - dt.timedelta(seconds=20):
            t = now - dt.timedelta(seconds=20 + (n - i))
        ctx += rnd.randint(1500, 9000)
        rec = {'type': 'assistant', 'timestamp': iso(t), 'sessionId': sid, 'cwd': 'C:\\demo\\project-a',
               'effort': rnd.choice(['medium', 'high', 'xhigh']),
               'message': {'id': 'msg_%s_%s%03d' % (sid[:6], 's' if is_sub else 'm', i), 'model': model,
                           'usage': usage(model, ttl, ctx)}}
        lines.append(json.dumps(rec))
    with open(path, 'w', encoding='utf-8', newline='\n') as f:
        f.write('\n'.join(lines) + '\n')


for prefix, title, model, start, n, gap, ttl, subs in SESSIONS:
    sid = '%s%s-0000-4000-8000-%012d' % (prefix, 'ff', rnd.randint(1, 10 ** 11))
    write_session(os.path.join(proj, sid + '.jsonl'), sid, title, model, start, n, gap, ttl)
    for k in range(subs):
        sub_dir = os.path.join(proj, sid, 'subagents')
        os.makedirs(sub_dir, exist_ok=True)
        write_session(os.path.join(sub_dir, 'agent-%d.jsonl' % (k + 1)), sid, None, 'claude-haiku-5-5',
                      max(30, start - 30), 8, 3.0, 5, is_sub=True)

# A fake account: used by the account chip at the top right of the compact card and by the subscription view
with open(os.path.join(logs, 'accounts.csv'), 'w', encoding='utf-8-sig', newline='') as f:
    f.write('ID,Name,Plan,Start date,Monthly fee,Bound org,Login hint,Note\r\n')
    f.write('A,Demo account,max5,2026-10-01,100,org-demo,d***o@example.com,\r\n')

print('demo data written to', out)

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

args = [a for a in sys.argv[1:] if not a.startswith('--')]
flags = [a for a in sys.argv[1:] if a.startswith('--')]
RICH = '--rich' in flags       # For the layout check: also write a price table (quota_conversion.csv), quota snapshots and a few accounts; file names and words in the tables are taken from the source in src (works for both the Chinese and the English tree)
out = args[0] if args else os.path.join(os.getcwd(), 'demo')
SRC = args[1] if len(args) > 1 else os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'src')
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


def src_text():
    buf = []
    for fn in sorted(os.listdir(SRC)):
        if fn.endswith('.cs'):
            with open(os.path.join(SRC, fn), encoding='utf-8') as f:
                buf.append(f.read())
    return '\n'.join(buf)


def rich():
    """Fake data for the layout check: price table, quota snapshots, a few accounts. Words and file names are read from the source, so it works for both the Chinese and the English tree."""
    import re
    code = src_text()
    m = re.search(r'Path\.Combine\(AppPaths\.DataDir, "([^"]+\.csv)"\), AccountStore\.CurrentKey', code)
    quota_name = m.group(1) if m else 'quota_conversion.csv'
    m = re.search(r'quotaPath = Path\.Combine\(logDir, "([^"]+)"\)', code)
    snap_name = m.group(1) if m else 'quota_snapshots.csv'
    m = re.search(r'accountsPath = Path\.Combine\(logDir, "([^"]+)"\)', code)
    acct_name = m.group(1) if m else 'accounts.csv'
    m = re.search(r'f\.Length < 13 \|\| f\[0\] == "([^"]+)"', code)
    head_word = m.group(1) if m else 'Type'
    m = re.search(r'if \(f\[0\] == "([^"]+)"\)\s*\{\s*t\.PlanDerived', code)
    glob_word = m.group(1) if m else 'Global'
    m = re.search(r't\.Stale && f\[0\] != "([^"]+)"', code)
    model_word = m.group(1) if m else 'Model'
    m = re.search(r'if \(f\[0\] == "([^"]+)"\)\s*\{\s*double d5', code)
    tier_word = m.group(1) if m else 'Tier'
    m = re.search(r'e\.Measured5 = f\[11\] == "([^"]+)"', code)
    meas_word = m.group(1) if m else 'Measured'
    print('rich data: quota=%s snap=%s acct=%s words=%s/%s/%s/%s/%s' % (quota_name, snap_name, acct_name, head_word, glob_word, model_word, tier_word, meas_word))

    key = 'A:max5'
    ts = dt.datetime.now().strftime('%Y-%m-%d %H:%M:%S')
    price = {
        'claude-opus-5-5': (5, 25, 6.25, 10, 0.5, 1.9, 0.30),
        'claude-sonnet-5-5': (3, 15, 3.75, 6, 0.3, 1.2, 0.17),
        'claude-haiku-5-5': (1, 5, 1.25, 2, 0.1, 0.5, 0.07),
    }
    head = 'Type,Name,Input,Output,Write5m,Write1h,Read,K5,Err5,KW,ErrW,State5,StateW,Updated,LongCtx,LongMult,ReadScale,Err,Blocks,Early,Ratio,Basis,Key'
    rows = [head.replace('Type', head_word, 1)]
    rows.append('%s,%s,,,,,,1.2,0.0021,0.17,0.0004,fit,fit,%s,,,1.0,0.0512,12,,0.97,week,%s' % (glob_word, glob_word, ts, key))
    for name, p in price.items():
        rows.append('%s,%s,%s,%.3f,0.0021,%.3f,0.0004,%s,%s,%s,,,,,,,,,%s' % (model_word, name, ','.join(str(x) for x in p[:5]), p[5], p[6], meas_word, meas_word, ts, key))
    for tier, mult in (('low', 0.78), ('medium', 0.9), ('high', 1.0), ('xhigh', 1.22), ('max', 1.45)):
        rows.append('%s,%s,,,,,,%.3f,0.01,%.3f,0.01,%s,%s,%s,,,,,,,,,%s' % (tier_word, tier, mult, mult, meas_word, meas_word, ts, key))
    with open(os.path.join(logs, quota_name), 'w', encoding='utf-8', newline='') as f:
        f.write('\n'.join(rows) + '\n')

    # Quota snapshots: time,5h%,5h reset,weekly%,weekly reset (last 3 hours, one every 10 minutes)
    r5 = (dt.datetime.now() + dt.timedelta(minutes=71)).replace(second=0, microsecond=0)
    rw = (dt.datetime.now() + dt.timedelta(hours=64)).replace(minute=0, second=0, microsecond=0)
    lines = []
    for i in range(18, -1, -1):
        t = dt.datetime.now() - dt.timedelta(minutes=10 * i)
        lines.append('%s,%.1f,%s,%.1f,%s' % (t.strftime('%Y-%m-%d %H:%M:%S'), 58 - i * 1.3, r5.strftime('%Y-%m-%d %H:%M:%S'),
                                           61 - i * 0.2, rw.strftime('%Y-%m-%d %H:%M:%S')))
    with open(os.path.join(logs, snap_name), 'w', encoding='utf-8', newline='') as f:
        f.write('\n'.join(lines) + '\n')

    # Three fake accounts (for the Plan view)
    with open(os.path.join(logs, acct_name), 'w', encoding='utf-8-sig', newline='') as f:
        f.write('ID,Name,Plan,Start date,Monthly fee,Bound org,Login hint,Note\r\n')
        f.write('A,Demo A,max5,2026-10-01,100,org-demo,d***o@example.com,\r\n')
        f.write('B,Demo B,pro,2026-09-12,20,org-b,b***o@example.com,\r\n')
        f.write('C,Demo C,max20,2026-11-03,200,,,\r\n')


if RICH:
    rich()
print('demo data written to', out)

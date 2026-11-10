# -*- coding: utf-8 -*-
"""
Quota conversion: use the percentages in the quota snapshots to work out how many quota percentage points each model consumes per US dollar of usage (at official prices).

Compared with the old analysis.py (which fitted a per-model weight by token count), three things changed here:

1. Usage is converted to dollars at official prices before fitting. Cache-read tokens are 98% of all tokens but cost only 1/50 of output tokens,
   so by token count they would swamp everything; priced in dollars, input / output / cache write / cache read each count at their own rate.
2. The fit uses cumulative amounts, not increments. The snapshot percentages are integers, and rounding error dominates the increments between two snapshots (0, 1, 2);
   fitting the whole curve inside one quota window (one starting point per window) keeps rounding error from accumulating and uses the samples far better.
3. Models without data are neither guessed nor left blank: they shrink toward "official price" (ridge regression) and only move away from it as data accumulates;
   every number carries an error and is marked "measured", "few samples" or "estimated from price".

Accounts and plans (v24):
  - Quota is computed per account and plan: Pro and Max consume several times different percentage points for the same usage, so switching account or plan changes the coefficients.
    The widget records account_timeline.csv (from when on usage belongs to which account and plan); snapshots and usage are split by "account:plan" and each basis gets its own set of coefficients;
  - A new account / plan with no data yet gets an initial value derived from the basis with the most data using the plan multiplier (Pro 1x, Max 5x, Max 20x), marked "estimated from plan multiplier",
    and replaced by a measured value automatically once enough data has accumulated;
  - Without account_timeline.csv (older versions) everything is treated as one basis, as before.

Self-correcting (gets more accurate with use):
  - Every run refits using all snapshots; models and tiers that newly appear are upgraded from "estimated from price" to "measured" once there is enough data;
  - A changed basis (new account / plan / weekly quota reset) is detected automatically: if the latest stretch differs a lot from the earlier ones only the latest is used, and stretches are weighted by recency;
  - Ex-ante self-check: each block of data is predicted using only the snapshots before it and the error is recorded - this is the most honest accuracy, and it is the "estimate error" shown in the widget;
  - Tiers (thinking effort low / medium / high / xhigh / max; Ultracode on counts as ultra; fast mode +fast) each get a correction factor that shrinks toward 1 and only moves away as data grows;
  - Every run appends the key numbers to quota_conversion_history.csv, so you can watch the coefficients and errors converge over time.

Also: readings of 100% are capped (the true value may be higher), so those points are excluded from the fit;
quota window resets (including the weekly quota being cleared) split the data into segments automatically; oversized residuals (e.g. quota used elsewhere with the same account) are down-weighted with Huber weights.

Output: quota_conversion.csv (read by the widget), quota_conversion_report.txt (method / accuracy / per-window prediction comparison)
Usage: python quota_fit.py
"""
import bisect
import calendar
import copy
import csv
import datetime
import io
import math
import os
import re
import sys

# Data folder: passed in via the STILLA_DATA environment variable when called by the widget; use --data <folder> when run by hand; otherwise the script's own folder
BASE = os.environ.get('STILLA_DATA') or os.path.dirname(os.path.abspath(__file__))
if '--data' in sys.argv and sys.argv.index('--data') + 1 < len(sys.argv):
    BASE = sys.argv[sys.argv.index('--data') + 1]
OUT_CSV = os.path.join(BASE, 'quota_conversion.csv')
OUT_REPORT = os.path.join(BASE, 'quota_conversion_report.txt')
OUT_HISTORY = os.path.join(BASE, 'quota_conversion_history.csv')
TIMELINE = os.path.join(BASE, 'account_timeline.csv')
# Official nominal usage multiplier of each plan relative to Pro: once quota is converted to "percentage points per dollar" it is inversely proportional to this. Only used to give a new basis without data an initial estimate
PLAN_MULT = {'pro': 1.0, 'max5': 5.0, 'max20': 20.0}
MULT_REL_SD = 0.3    # Relative uncertainty of values derived from the multiplier

# Official prices (USD per million tokens): input, output, 5-minute cache write, 1-hour cache write, cache read
PRICE = {
    'claude-fable-5-1':  (10, 50, 12.5, 20, 0.25),
    'claude-fable-5':    (10, 50, 12.5, 20, 1.0),
    'claude-opus-5-5':   (4, 20, 5, 8, 0.20),
    'claude-opus-5':     (5, 25, 6.25, 10, 0.50),
    'claude-opus-4-8':   (5, 25, 6.25, 10, 0.50),
    'claude-opus-4-7':   (5, 25, 6.25, 10, 0.50),
    'claude-opus-4-6':   (5, 25, 6.25, 10, 0.50),
    'claude-opus-4-5':   (5, 25, 6.25, 10, 0.50),
    'claude-sonnet-5-5': (2, 10, 2.5, 4, 0.10),
    'claude-sonnet-5':   (2, 10, 2.5, 4, 0.20),
    'claude-sonnet-4-6': (3, 15, 3.75, 6, 0.30),
    'claude-sonnet-4-5': (3, 15, 3.75, 6, 0.30),
    'claude-haiku-5-5':  (0.10, 0.50, 0.125, 0.20, 0.01),
    'claude-haiku-4-5':  (1, 5, 1.25, 2, 0.10),
}

# Long-context surcharge tier: if one request's context (input + cache write + cache read) exceeds the threshold, the whole request is billed at "multiplier x official price"
TIER = {
    'claude-haiku-5-5': (100000, 5.0),
}

TAU = 0.5            # Prior: how far each model's "quota per dollar" may differ from the overall average (relative; 0.5 = usually not more than half off)
QUANT_SD = 0.29      # Standard deviation of the rounding error of integer percentages (uniform distribution); a lower reference bound for the fit residual
HUBER_C = 1.5
MIN_POINTS = 40      # Do not write a result if fewer points than this take part in the fit
FOLD_HOURS = 2.0     # Leave-one-out validation: cut the curve into 2-hour blocks and predict each block's increment with coefficients fitted on the other blocks
MIN_FOLD_DELTA = 3   # Validation only looks at blocks with an increment >= 3 percentage points (smaller blocks drown in rounding error)
LAGS = [-120, -60, 0, 60, 120, 180, 300]
RHOS = [0.25, 0.35, 0.5, 0.65, 0.75, 1.0, 1.5, 2.0]   # Scale factors for the cache-read unit price, used to validate the assumption "cache read is billed at the official price"
EPOCH = datetime.datetime(2020, 1, 1)
USE_TIER = '--no-tier' not in sys.argv
QUICK = '--quick' in sys.argv     # The widget calls this in the background: coefficients only, no comparison or leave-one-out validation, no report
TAU_M = 0.5          # Prior width of the tier correction factors (relative to 1)
PREQ_RECENT = 12     # "Recent error" looks at this many latest blocks
HALF_LIFE_DAYS = 7.0  # Half-life for weighting window curves by recency when the basis has not changed
GAIN = 0.10          # lag / cache-read scaling: if RMSE improves by less than 10% leave it alone (keep official price, no alignment) to avoid overfitting


def parse_time(s):
    return (datetime.datetime.strptime(s.strip(), '%Y-%m-%d %H:%M:%S') - EPOCH).total_seconds()


def fmt_time(t):
    return (EPOCH + datetime.timedelta(seconds=t)).strftime('%m-%d %H:%M')


def base_model(m):
    return re.sub(r'-\d{8}$', '', m or '')


# ---------------------------------------------------------------- Reading data

def load_tags(path):
    """usage_tags.csv: message ID -> (thinking effort, Ultra, fast mode, thinking tokens). Empty if the file does not exist."""
    tags = {}
    if not os.path.exists(path):
        return tags
    with io.open(path, encoding='utf-8-sig', newline='') as f:
        for line in f:
            p = line.rstrip('\r\n').split(',')
            if len(p) < 5 or not p[0] or p[0] == 'Message ID':
                continue
            tags[p[0]] = (p[1], p[2] == '1', p[3] == 'fast', int(p[4]) if p[4].isdigit() else 0)
    return tags


def mode_key(tag):
    """Tier name: ultra (Ultracode on) > thinking effort; fast mode appends +fast; entries with nothing recorded are called "Unlabeled". Same as in the widget."""
    if tag is None:
        return 'Unlabeled'
    effort, ultra, fast, _ = tag
    k = 'ultra' if ultra else (effort or 'Unlabeled')
    return k + '+fast' if fast else k


def _add(out, m, t, base, read, tok):
    d = out.get(m)
    if d is None:
        d = out[m] = {'t': [], 'base': [0.0], 'read': [0.0], 'tok': [0.0]}
    d['t'].append(t)
    d['base'].append(d['base'][-1] + base)
    d['read'].append(d['read'][-1] + read)
    d['tok'].append(d['tok'][-1] + tok)


def load_timeline(path):
    """account_timeline.csv (times are UTC) -> [(local time seconds, account, plan)], sorted by time; empty if the file does not exist."""
    out = []
    if not os.path.exists(path):
        return out
    with io.open(path, encoding='utf-8-sig', newline='') as f:
        for r in csv.reader(f):
            if len(r) < 3 or r[0].startswith('Time'):
                continue
            try:
                u = datetime.datetime.strptime(r[0].strip(), '%Y-%m-%d %H:%M:%S')
            except ValueError:
                continue
            loc = datetime.datetime.fromtimestamp(calendar.timegm(u.timetuple()))
            out.append(((loc - EPOCH).total_seconds(), r[1].strip(), r[2].strip()))
    out.sort()
    return out


def make_key_of(tl):
    """Time -> basis "account:plan". Times before the first record belong to the first record (same as in the widget); without a timeline everything is ''."""
    if not tl:
        return lambda t: ''
    times = [e[0] for e in tl]
    keys = ['%s:%s' % (e[1], e[2]) for e in tl]

    def key_of(t):
        return keys[max(bisect.bisect_right(times, t) - 1, 0)]
    return key_of


def load_messages(path, tags=None, key_of=None):
    """Returns (msgs_by_key, by_mode_by_key). msgs = {model: {t, base, read, tok}}; base = cumulative dollar cost excluding cache reads,
    read = cumulative cache-read cost, tok = cumulative total tokens. by_mode = {tier: same structure}, only present when a tags file exists.
    When key_of is not empty the data is split by basis (account:plan), otherwise everything is under key ''."""
    rows = []
    with io.open(path, encoding='utf-8-sig', newline='') as f:
        for r in csv.DictReader(f):
            m = base_model(r['Model'])
            if m not in PRICE:
                continue
            try:
                t = parse_time(r['Time'])
                i, o = int(r['Input']), int(r['Output'])
                c5, c1, cr = int(r['Cache write 5m']), int(r['Cache write 1h']), int(r['Cache read'])
            except (ValueError, KeyError):
                continue
            rows.append((t, m, i, o, c5, c1, cr, (r.get('Message ID') or '').strip()))
    rows.sort(key=lambda x: x[0])
    out, by_mode = {}, {}
    for t, m, i, o, c5, c1, cr, mid in rows:
        p = PRICE[m]
        mult = 1.0
        if USE_TIER and m in TIER and (i + c5 + c1 + cr) > TIER[m][0]:
            mult = TIER[m][1]
        base = mult * (i * p[0] + o * p[1] + c5 * p[2] + c1 * p[3]) / 1e6
        read = mult * cr * p[4] / 1e6
        tok = (i + o + c5 + c1 + cr) / 1e6
        k = key_of(t) if key_of else ''
        _add(out.setdefault(k, {}), m, t, base, read, tok)
        if tags:
            _add(by_mode.setdefault(k, {}).setdefault(mode_key(tags.get(mid)), {}), m, t, base, read, tok)
    return out, by_mode


def load_snapshots(path):
    out = []
    with io.open(path, encoding='utf-8-sig', newline='') as f:
        for r in csv.DictReader(f):
            def num(k):
                v = (r.get(k) or '').strip()
                return float(v) if v else None
            try:
                t = parse_time(r['Time'])
            except (ValueError, KeyError):
                continue
            out.append({'t': t, 'p5': num('5h used %'), 'r5': (r.get('5h resets at') or '').strip(),
                        'pw': num('Weekly used %'), 'rw': (r.get('Weekly resets at') or '').strip()})
    out.sort(key=lambda s: s['t'])
    return out


def segments(snaps, kind):
    """Consecutive snapshots inside one quota window. Window boundaries, missing readings and falling percentages break a segment; a point showing 100% is capped, so it and everything after it is dropped."""
    pk, rk = ('p5', 'r5') if kind == '5h' else ('pw', 'rw')
    segs, cur = [], []

    def close():
        if len(cur) >= 3:
            segs.append(list(cur))
        del cur[:]

    for s in snaps:
        v, r = s[pk], s[rk]
        if v is None or not r:
            close()
            continue
        if cur and (r != cur[-1][rk] or v < cur[-1][pk]):
            close()
        if v >= 100:
            close()
            continue
        cur.append(s)
    close()
    return segs


# ---------------------------------------------------------------- Linear algebra (small matrices)

def mat_inv(A):
    n = len(A)
    M = [list(A[i]) + [1.0 if i == j else 0.0 for j in range(n)] for i in range(n)]
    for c in range(n):
        piv = max(range(c, n), key=lambda r: abs(M[r][c]))
        if abs(M[piv][c]) < 1e-14:
            M[piv][c] = 1e-14
        M[c], M[piv] = M[piv], M[c]
        d = M[c][c]
        M[c] = [x / d for x in M[c]]
        for r in range(n):
            if r != c and M[r][c] != 0.0:
                f = M[r][c]
                M[r] = [a - f * b for a, b in zip(M[r], M[c])]
    return [row[n:] for row in M]


def median(v):
    s = sorted(v)
    n = len(s)
    return s[n // 2] if n % 2 else (s[n // 2 - 1] + s[n // 2]) / 2.0


# ---------------------------------------------------------------- Building the points for the fit

def cum_vec(msgs, models, t, lag, rho, feat):
    """Cumulative per-model usage up to and including time t. feat = 'cost' (USD) or 'tok' (million tokens). Message times are shifted by lag seconds before comparing."""
    v = []
    for m in models:
        d = msgs.get(m)
        if d is None:
            v.append(0.0)
            continue
        k = bisect.bisect_right(d['t'], t - lag)
        v.append(d['tok'][k] if feat == 'tok' else d['base'][k] + rho * d['read'][k])
    return v


def build_points(msgs, segs, models, lag=0, rho=1.0, feat='cost'):
    """Demeans each segment within the group (one starting point per window); returns points = [dict(y, x, seg, t)] and each segment's (start x, end x, start y, end y)."""
    pts, seginfo = [], []
    for si, seg in enumerate(segs):
        ys = [s['_v'] for s in seg]
        xs = [cum_vec(msgs, models, s['t'], lag, rho, feat) for s in seg]
        my = sum(ys) / len(ys)
        mx = [sum(x[j] for x in xs) / len(xs) for j in range(len(models))]
        for s, y, x in zip(seg, ys, xs):
            pts.append({'y': y - my, 'x': [a - b for a, b in zip(x, mx)], 'seg': si, 't': s['t'],
                        'ry': y, 'rx': x})
        seginfo.append((xs[0], xs[-1], ys[0], ys[-1], seg[0]['t'], seg[-1]['t']))
    return pts, seginfo


def prepare(snaps, kind):
    segs = segments(snaps, kind)
    pk = 'p5' if kind == '5h' else 'pw'
    for seg in segs:
        for s in seg:
            s['_v'] = s[pk]
    return segs


# ---------------------------------------------------------------- Fitting

def fit(pts, nmod, tau=TAU, mask=None):
    """Two steps: 1) a single slope K0 (all models share one "quota per dollar", Huber-robust); 2) per-model ridge regression shrunk toward K0.
    mask: optional boolean list, points that are False take no part (used by leave-one-out)."""
    use = [p for i, p in enumerate(pts) if mask is None or mask[i]]
    if len(use) < 5:
        return None
    s = [sum(p['x']) for p in use]
    y = [p['y'] for p in use]
    w = [1.0] * len(use)
    K0 = 0.0
    for _ in range(12):
        sxx = sum(wi * si * si for wi, si in zip(w, s))
        if sxx <= 1e-12:
            return None
        K0 = sum(wi * si * yi for wi, si, yi in zip(w, s, y)) / sxx
        res = [yi - K0 * si for yi, si in zip(y, s)]
        sc = max(1.4826 * median([abs(r - median(res)) for r in res]), 0.25)
        w = [1.0 if abs(r) <= HUBER_C * sc else HUBER_C * sc / abs(r) for r in res]
    if K0 <= 0:
        return None
    # Step two: one slope per model, shrunk toward K0
    sigma2 = max(sc * sc, QUANT_SD ** 2)
    k = [K0] * nmod
    A = None
    for _ in range(8):
        A = [[0.0] * nmod for _ in range(nmod)]
        b = [0.0] * nmod
        for wi, p, yi in zip(w, use, y):
            for a in range(nmod):
                xa = p['x'][a]
                if xa == 0.0:
                    continue
                b[a] += wi * xa * yi / sigma2
                for c in range(nmod):
                    A[a][c] += wi * xa * p['x'][c] / sigma2
        prior = 1.0 / ((tau * K0) ** 2)
        for a in range(nmod):
            A[a][a] += prior
            b[a] += prior * K0
        Ainv = mat_inv(A)
        k = [sum(Ainv[a][c] * b[c] for c in range(nmod)) for a in range(nmod)]
        res = [yi - sum(ka * xa for ka, xa in zip(k, p['x'])) for p, yi in zip(use, y)]
        sc = max(1.4826 * median([abs(r - median(res)) for r in res]), 0.25)
        sigma2 = max(sc * sc, QUANT_SD ** 2)
        w = [1.0 if abs(r) <= HUBER_C * sc else HUBER_C * sc / abs(r) for r in res]
    # Residual autocorrelation: when neighbouring points in a segment have correlated residuals the effective sample size is smaller than the number of points, so the error must be inflated
    num = den = 0.0
    prev = None
    for p, r in zip(use, res):
        if prev is not None and prev[0] == p['seg']:
            num += prev[1] * r
            den += prev[1] * prev[1]
        prev = (p['seg'], r)
    rho1 = max(0.0, min(0.95, num / den)) if den > 0 else 0.0
    infl = min(25.0, (1 + rho1) / (1 - rho1))
    se = [min(math.sqrt(max(Ainv[a][a], 0.0) * infl), tau * K0) for a in range(nmod)]   # For a model without data the error is just the width of the prior
    sst = sum(yi * yi for yi in y)
    ssr = sum(r * r for r in res)
    # Error of the single slope (with the same inflation factor)
    sxx = sum(wi * si * si for wi, si in zip(w, s))
    se0 = math.sqrt(sigma2 * infl / sxx) if sxx > 0 else float('nan')
    return {'K0': K0, 'se0': se0, 'k': k, 'se': se, 'r2': 1 - ssr / sst if sst > 0 else 0.0,
            'rmse': math.sqrt(ssr / len(use)), 'n': len(use), 'infl': infl, 'rho1': rho1,
            'sigma': math.sqrt(sigma2)}


def pooled_rmse(pts, nmod):
    """Root-mean-square residual with only the single slope (used when scanning lag / rho; no second step)."""
    f = fit(pts, nmod, tau=1e-6)   # tau tiny = all models are pinned to K0
    return (f['rmse'], f['r2'], f['K0']) if f else (float('inf'), 0.0, 0.0)


def fold_eval(pts, nmod, tau):
    """Leave-one-block-out validation: returns [(block start, block end, observed increment, predicted increment)]; the coefficients used for the prediction come from a fit that excludes this block."""
    blocks = {}
    t0s = {}
    for p in pts:
        t0s.setdefault(p['seg'], p['t'])
    for i, p in enumerate(pts):
        key = (p['seg'], int((p['t'] - t0s[p['seg']]) // (FOLD_HOURS * 3600)))
        blocks.setdefault(key, []).append(i)
    out = []
    for key, idx in sorted(blocks.items()):
        if len(idx) < 3:
            continue
        mask = [True] * len(pts)
        for i in idx:
            mask[i] = False
        f = fit(pts, nmod, tau, mask)
        if f is None:
            continue
        a, b = pts[idx[0]], pts[idx[-1]]
        obs = b['ry'] - a['ry']
        pred = sum(k * (xb - xa) for k, xa, xb in zip(f['k'], a['rx'], b['rx']))
        out.append((a['t'], b['t'], obs, pred))
    return out


def fold_summary(folds):
    use = [(o, p) for _, _, o, p in folds if o >= MIN_FOLD_DELTA]
    if not use:
        return float('nan'), float('nan'), 0
    rmse = math.sqrt(sum((o - p) ** 2 for o, p in use) / len(use))
    rel = sum(abs(o - p) / o for o, p in use) / len(use)
    return rmse, rel, len(use)


# ---------------------------------------------------------------- Weekly quota: per-model relative coefficients are taken from the 5-hour result, one slope per window segment

def huber_slope(z, y):
    w = [1.0] * len(z)
    K, sc, r = 0.0, 0.25, []
    for _ in range(12):
        szz = sum(wi * zi * zi for wi, zi in zip(w, z))
        if szz <= 1e-12:
            return None
        K = sum(wi * zi * yi for wi, zi, yi in zip(w, z, y)) / szz
        r = [yi - K * zi for zi, yi in zip(z, y)]
        m = median(r)
        sc = max(1.4826 * median([abs(x - m) for x in r]), 0.25)
        w = [1.0 if abs(x) <= HUBER_C * sc else HUBER_C * sc / abs(x) for x in r]
    return K, sc, w, r


def lag1(r):
    num = sum(a * b for a, b in zip(r, r[1:]))
    den = sum(a * a for a in r[:-1])
    return max(0.0, min(0.95, num / den)) if den > 0 else 0.0


def fit_week(pts, c, nseg):
    segsum = []
    sst = ssr = 0.0
    for si in range(nseg):
        sp = [p for p in pts if p['seg'] == si]
        z = [sum(cm * xm for cm, xm in zip(c, p['x'])) for p in sp]
        y = [p['y'] for p in sp]
        h = huber_slope(z, y)
        if h is None:
            continue
        K, sc, w, r = h
        szz = sum(wi * zi * zi for wi, zi in zip(w, z))
        rho1 = lag1(r)
        infl = min(25.0, (1 + rho1) / (1 - rho1))
        se = math.sqrt(max(sc * sc, QUANT_SD ** 2) * infl / szz)
        sst += sum(v * v for v in y)
        ssr += sum(v * v for v in r)
        segsum.append({'seg': si, 'K': K, 'se': se, 'n': len(sp), 'rho1': rho1, 'infl': infl,
                       'rmse': math.sqrt(sum(v * v for v in r) / len(r)), 't0': sp[0]['t'], 't1': sp[-1]['t'],
                       'y0': sp[0]['ry'], 'y1': sp[-1]['ry']})
    return segsum, (1 - ssr / sst if sst > 0 else 0.0), (math.sqrt(ssr / sum(s['n'] for s in segsum)) if segsum else float('nan'))


def fold_eval_week(pts, zfun):
    """Leave-one-block-out: each block's slope comes from the points of "the same window segment without this block" (weekly segments may have different slopes). zfun(rx) = cumulative usage at a point (price-converted or tokens)."""
    t0s = {}
    for p in pts:
        t0s.setdefault(p['seg'], p['t'])
    blocks = {}
    for i, p in enumerate(pts):
        blocks.setdefault((p['seg'], int((p['t'] - t0s[p['seg']]) // (FOLD_HOURS * 3600))), []).append(i)
    out = []
    for (si, _), idx in sorted(blocks.items()):
        if len(idx) < 3:
            continue
        ids = set(idx)
        rest = [p for i, p in enumerate(pts) if p['seg'] == si and i not in ids]
        if len(rest) < 5:
            continue
        h = huber_slope([zfun(p['x']) for p in rest], [p['y'] for p in rest])
        if h is None:
            continue
        a, b = pts[idx[0]], pts[idx[-1]]
        out.append((a['t'], b['t'], b['ry'] - a['ry'], h[0] * (zfun(b['rx']) - zfun(a['rx']))))
    return out


def make_blocks(pts):
    """Cuts every segment into FOLD_HOURS-hour blocks and returns the point indices of each block (at least 3 points)."""
    t0s = {}
    for p in pts:
        t0s.setdefault(p['seg'], p['t'])
    blocks = {}
    for i, p in enumerate(pts):
        blocks.setdefault((p['seg'], int((p['t'] - t0s[p['seg']]) // (FOLD_HOURS * 3600))), []).append(i)
    return [idx for _, idx in sorted(blocks.items()) if len(idx) >= 3]


def current_K(segsum, now_t):
    """Overall slope of the current basis. If the latest segment differs a lot from the earlier ones (more than 3 standard errors and more than 20% apart) the basis is considered changed and only the latest segment is used;
    otherwise segments are averaged weighted by precision and recency (half-life HALF_LIFE_DAYS days). Returns (K, error, note, whether the basis changed); returns None if there is no sufficiently long curve."""
    ok = [x for x in segsum if x['n'] >= 20 and x['y1'] - x['y0'] >= 5]
    if not ok:
        return None
    cur = ok[-1]
    others = ok[:-1]
    changed = False
    note = ''
    if others:
        ref = sum(x['K'] / (x['se'] ** 2) for x in others) / sum(1 / (x['se'] ** 2) for x in others)
        ratio = cur['K'] / ref
        sig = abs(cur['K'] - ref) / math.sqrt(cur['se'] ** 2 + min(x['se'] for x in others) ** 2)
        if sig > 3 and (ratio < 0.8 or ratio > 1.25):
            changed = True
            note = ('The latest segment\'s slope is x%.2f of the earlier ones (%.1f standard errors apart). The basis may have changed (for example a different account / plan, or the quota was reset); '
                    'the conversion now uses only the latest segment.' % (ratio, sig))
        else:
            note = 'Segment slopes agree (the latest is x%.2f of the earlier ones); averaged weighted by precision and recency.' % ratio
    use = [cur] if changed or not others else ok
    ws = [(1 / (x['se'] ** 2)) * 0.5 ** (max(0.0, now_t - x['t1']) / 86400.0 / HALF_LIFE_DAYS) for x in use]
    K = sum(w * x['K'] for w, x in zip(ws, use)) / sum(ws)
    se = 1.0 / math.sqrt(sum(1 / (x['se'] ** 2) for x in use))
    return K, se, note, changed


def preq_5h(snaps, msgs, models, lag, rho, last_n=None):
    """Ex-ante prediction (5-hour quota): each block is fitted using only the snapshots before it, then its increment is predicted. Returns [(block start, block end, observed increment, predicted increment)]."""
    segs = prepare(snaps, '5h')
    pts, _ = build_points(msgs, segs, models, lag, rho, 'cost')
    blocks = [idx for idx in make_blocks(pts) if pts[idx[-1]]['ry'] - pts[idx[0]]['ry'] >= MIN_FOLD_DELTA]
    if last_n:
        blocks = blocks[-last_n:]
    out = []
    for idx in blocks:
        a, b = pts[idx[0]], pts[idx[-1]]
        tsegs = prepare([x for x in snaps if x['t'] < a['t']], '5h')
        tpts, _ = build_points(msgs, tsegs, models, lag, rho, 'cost')
        f = fit(tpts, len(models), TAU)
        if f is None or f['n'] < MIN_POINTS:
            continue
        pred = sum(k * (xb - xa) for k, xa, xb in zip(f['k'], a['rx'], b['rx']))
        out.append((a['t'], b['t'], b['ry'] - a['ry'], pred))
    return out


def preq_week(snaps, msgs, models, c, lag, rho, last_n=None):
    """Ex-ante prediction (weekly quota): same as above; the slope is fitted from "the same window curve before this block", and blocks with fewer than 8 earlier points are not evaluated."""
    segs = prepare(snaps, 'week')
    pts, _ = build_points(msgs, segs, models, lag, rho, 'cost')
    zf = lambda x: sum(cm * xm for cm, xm in zip(c, x))
    blocks = [idx for idx in make_blocks(pts) if pts[idx[-1]]['ry'] - pts[idx[0]]['ry'] >= MIN_FOLD_DELTA]
    if last_n:
        blocks = blocks[-last_n:]
    out = []
    for idx in blocks:
        a, b = pts[idx[0]], pts[idx[-1]]
        tsegs = prepare([x for x in snaps if x['t'] < a['t']], 'week')
        if not tsegs:
            continue
        tpts, _ = build_points(msgs, tsegs, models, lag, rho, 'cost')
        segsum, _, _ = fit_week(tpts, c, len(tsegs))
        if not segsum:
            continue
        last = segsum[-1]
        if a['t'] - last['t1'] > 3 * 3600 or last['n'] < 8:
            continue
        out.append((a['t'], b['t'], b['ry'] - a['ry'], last['K'] * (zf(b['rx']) - zf(a['rx']))))
    return out


def preq_stats(ev):
    """Summary of the ex-ante predictions: recent (last PREQ_RECENT blocks) mean relative error, number of blocks, actual/predicted ratio; early (first 10 blocks) mean relative error."""
    use = [(o, p) for _, _, o, p in ev if o >= MIN_FOLD_DELTA]
    if not use:
        return None
    rel = lambda xs: sum(abs(o - p) / o for o, p in xs) / len(xs)
    recent = use[-PREQ_RECENT:]
    early = use[:10] if len(use) >= 20 else None
    sp = sum(p for _, p in recent)
    return {'err': rel(recent), 'n': len(recent), 'ratio': (sum(o for o, _ in recent) / sp) if sp > 0 else 0.0,
            'early': rel(early) if early else None, 'last_t': [e for e in ev if e[2] >= MIN_FOLD_DELTA][-1][1], 'total': len(use)}


def fit_modes(snaps, msgs, by_mode, models, k_model, K5, c, lag, rho):
    """Correction factor d for each tier: quota ~ sum over tiers d x sum over models k_model x dollar usage of that model in that tier. d shrinks toward 1 (prior width TAU_M).
    Fitted jointly on the 5-hour curves and the weekly curves (each weekly curve is converted to the 5-hour scale with its own slope - so periods after switching account / plan that only have weekly quota can also be used).
    Not fitted when there is only one tier (or no tags). Returns {tier: (d, error, status, share)}."""
    modes = sorted(by_mode.keys())
    if len(modes) < 2:
        return None
    nm = len(modes)
    pts = []
    raw_first, raw_last = {}, {}

    def add_segments(segs, scales, offset):
        for si, seg in enumerate(segs):
            sc = scales[si]
            if sc is None:
                continue
            ys = [x['_v'] for x in seg]
            zs = []
            for x in seg:
                zs.append([sc * sum(k * v for k, v in zip(k_model, cum_vec(by_mode[mk], models, x['t'], lag, rho, 'cost'))) for mk in modes])
            my = sum(ys) / len(ys)
            mz = [sum(z[j] for z in zs) / len(zs) for j in range(nm)]
            raw_first[offset + si], raw_last[offset + si] = zs[0], zs[-1]
            for x, y, z in zip(seg, ys, zs):
                pts.append({'y': y - my, 'x': [p - q for p, q in zip(z, mz)], 'seg': offset + si, 't': x['t']})

    segs5 = prepare(snaps, '5h')
    add_segments(segs5, [1.0] * len(segs5), 0)
    segsw = prepare(snaps, 'week')
    if segsw:
        wpts, _ = build_points(msgs, segsw, models, lag, rho, 'cost')
        wsum, _, _ = fit_week(wpts, c, len(segsw))
        scales = [None] * len(segsw)
        for x in wsum:
            if x['n'] >= 8 and x['y1'] - x['y0'] >= 2:
                scales[x['seg']] = x['K'] / K5
        segsw = prepare(snaps, 'week')      # After fit_week _v is still the weekly quota; do it once more to be safe
        add_segments(segsw, scales, 1000)
    if len(pts) < MIN_POINTS:
        return None
    y0 = [p['y'] - sum(p['x']) for p in pts]          # Residual when all tier factors are 1
    w = [1.0] * len(pts)
    sigma2 = QUANT_SD ** 2
    delta = [0.0] * nm
    Ainv = None
    res = y0
    for _ in range(8):
        A = [[0.0] * nm for _ in range(nm)]
        b = [0.0] * nm
        for wi, p, yi in zip(w, pts, y0):
            for a in range(nm):
                xa = p['x'][a]
                if xa == 0.0:
                    continue
                b[a] += wi * xa * yi / sigma2
                for c2 in range(nm):
                    A[a][c2] += wi * xa * p['x'][c2] / sigma2
        for a in range(nm):
            A[a][a] += 1.0 / (TAU_M ** 2)
        Ainv = mat_inv(A)
        delta = [sum(Ainv[a][c2] * b[c2] for c2 in range(nm)) for a in range(nm)]
        res = [yi - sum(d * xa for d, xa in zip(delta, p['x'])) for p, yi in zip(pts, y0)]
        sc = max(1.4826 * median([abs(r - median(res)) for r in res]), 0.25)
        sigma2 = max(sc * sc, QUANT_SD ** 2)
        w = [1.0 if abs(r) <= HUBER_C * sc else HUBER_C * sc / abs(r) for r in res]
    num = den = 0.0
    prev = None
    for p, r in zip(pts, res):
        if prev is not None and prev[0] == p['seg']:
            num += prev[1] * r
            den += prev[1] * prev[1]
        prev = (p['seg'], r)
    rho1 = max(0.0, min(0.95, num / den)) if den > 0 else 0.0
    infl = min(25.0, (1 + rho1) / (1 - rho1))
    total = [sum(raw_last[si][j] - raw_first[si][j] for si in raw_first) for j in range(nm)]
    tsum = sum(total) or 1.0
    out = {}
    for j, mk in enumerate(modes):
        share = total[j] / tsum
        # Inflate the error another 2x for safety: messages within one tier are not a random sample, so unit-price deviations come in clumps; a deviation of less than 2.5 (inflated) standard errors from 1 counts as no difference
        se = min(2.0 * math.sqrt(max(Ainv[j][j], 0.0) * infl), TAU_M)
        d = max(0.05, 1.0 + delta[j])
        if share < 0.003:
            out[mk] = (1.0, TAU_M, 'Estimated from price', share)
        elif abs(d - 1.0) < 2.5 * se:
            out[mk] = (1.0, se, 'No significant difference', share)
        else:
            out[mk] = (d, se, 'Measured' if se / d < 0.25 else 'Few samples', share)
    return out


# ---------------------------------------------------------------- Main flow

def analyse5(snaps, msgs, lines, by_mode=None):
    kind = '5h'
    segs = prepare(snaps, kind)
    lines.append('')
    lines.append('=' * 70)
    lines.append('[5-hour quota]')
    if not segs:
        lines.append('No usable window curves.')
        return None
    npts = sum(len(s) for s in segs)
    lines.append('%d window curves with %d snapshot points in total (points showing 100%% and window boundaries removed)' % (len(segs), npts))
    for s in segs:
        lines.append('  %s ~ %s  %g%% -> %g%% (%d points)' % (fmt_time(s[0]['t']), fmt_time(s[-1]['t']), s[0]['_v'], s[-1]['_v'], len(s)))
    models = sorted(msgs.keys())
    nmod = len(models)

    # 1) Cache-read unit-price scaling: validate the assumption "cache read is billed at the official price" (cache reads are 98% of tokens and half of the cost)
    rscan = []
    for rho in RHOS:
        pts, _ = build_points(msgs, segs, models, 0, rho, 'cost')
        rscan.append((rho,) + pooled_rmse(pts, nmod))
    best_rho = min(rscan, key=lambda r: r[1])[0]
    r1 = [r for r in rscan if r[0] == 1.0][0][1]
    rb = [r for r in rscan if r[0] == best_rho][0][1]
    if r1 - rb < GAIN * r1:
        best_rho = 1.0
    lines.append('')
    lines.append('Cache-read unit-price scaling scan (RMS residual, lower is better; 1.0 = official price): ' + '  '.join('×%g:%.3f' % (r[0], r[1]) for r in rscan))
    lines.append('Using cache read x%g (if it improves by less than %d%% the official price is kept)' % (best_rho, GAIN * 100))

    # 2) Time alignment: message time + lag is when the quota was actually deducted
    scan = []
    for lag in LAGS:
        pts, _ = build_points(msgs, segs, models, lag, best_rho, 'cost')
        scan.append((lag,) + pooled_rmse(pts, nmod))
    best_lag = min(scan, key=lambda r: r[1])[0]
    base_rmse = [r for r in scan if r[0] == 0][0][1]
    best_rmse = [r for r in scan if r[0] == best_lag][0][1]
    if base_rmse - best_rmse < GAIN * base_rmse:
        best_lag = 0
    lines.append('Time alignment scan (RMS residual): ' + '  '.join('%+ds:%.3f' % (r[0], r[1]) for r in scan))
    lines.append('Using lag = %+d s (if it improves by less than %d%% no alignment is applied)' % (best_lag, GAIN * 100))

    pts, seginfo = build_points(msgs, segs, models, best_lag, best_rho, 'cost')
    f = fit(pts, nmod, TAU)
    if f is None or f['n'] < MIN_POINTS:
        lines.append('Too few points (%d), no result.' % (0 if f is None else f['n']))
        return None

    # Current basis: per-segment slopes -> if the basis changed use only the latest segment, otherwise weight by precision and recency
    c5 = [k / f['K0'] for k in f['k']]
    segsum5, _, _ = fit_week(pts, c5, len(segs))
    cur5 = current_K(segsum5, snaps[-1]['t'])
    Kcur, Kse, kcur_note = (cur5[0], cur5[1], cur5[2]) if cur5 else (f['K0'], f['se0'], '')

    ft = f_cost_pool = f_tok_pool = tpts = None
    if not QUICK:
        tpts, _ = build_points(msgs, segs, models, best_lag, 1.0, 'tok')
        ft = fit(tpts, nmod, 5.0)
        f_cost_pool = fit(pts, nmod, 1e-6)
        f_tok_pool = fit(tpts, nmod, 1e-6)

    cost_total = [0.0] * nmod
    for xa, xb, ya, yb, ta, tb in seginfo:
        for j in range(nmod):
            cost_total[j] += xb[j] - xa[j]
    all_cost = sum(cost_total) or 1.0

    if not QUICK:
        lines.append('')
        lines.append('Fit result (%d points; residual autocorrelation %.2f, error already inflated by the effective sample size):' % (f['n'], f['rho1']))
        lines.append('  Overall average: %.3f +- %.3f quota percentage points per dollar (1%% ~ %.2f USD of official-price usage)' % (f['K0'], f['se0'], 1.0 / f['K0']))
        if kcur_note:
            lines.append('  Current basis: %.3f +- %.3f. %s' % (Kcur, Kse, kcur_note))
        lines.append('  Goodness of fit (within-group R2): by price %.3f; for comparison by tokens (single coefficient) %.3f, by tokens (one coefficient per model) %.3f'
                     % (f_cost_pool['r2'], f_tok_pool['r2'], ft['r2']))
        lines.append('  RMS residual (percentage points): by price %.3f; for comparison by tokens (one coefficient per model) %.3f; floor from integer rounding about %.2f'
                     % (f['rmse'], ft['rmse'], QUANT_SD))
        # Per-segment slopes: is the basis stable
        lines.append('  Per-segment slopes (each segment fitted with its own coefficient):')
        for si, seg in enumerate(segs):
            sp = [p for p in pts if p['seg'] == si]
            h = huber_slope([sum(p['x']) for p in sp], [p['y'] for p in sp])
            if h:
                lines.append('    %s ~ %s   %.3f   (%g percentage points in this segment)' % (fmt_time(seg[0]['t']), fmt_time(seg[-1]['t']), h[0], seg[-1]['_v'] - seg[0]['_v']))

        folds_c = fold_eval(pts, nmod, TAU)
        folds_t = fold_eval(tpts, nmod, 5.0)
        rc, relc, nc = fold_summary(folds_c)
        rt, relt, nt = fold_summary(folds_t)
        lines.append('')
        lines.append('Leave-one-block-out validation (curve cut into %g-hour blocks, each predicted with coefficients fitted on the other blocks; only blocks with an increment >= %d points, %d blocks):'
                     % (FOLD_HOURS, MIN_FOLD_DELTA, nc))
        lines.append('  By price: RMS prediction error %.2f points, mean relative error %.0f%%' % (rc, relc * 100))
        lines.append('  By tokens (old method): RMS prediction error %.2f points, mean relative error %.0f%%' % (rt, relt * 100))
        lines.append('  Block by block (observed increment / price prediction / token prediction):')
        tmap = dict(((a, b), p) for a, b, o, p in folds_t)
        for a, b, o, p in folds_c:
            if o >= MIN_FOLD_DELTA:
                lines.append('    %s ~ %s   observed %5.1f   by price %5.1f   tokens %5.1f' % (fmt_time(a), fmt_time(b), o, p, tmap.get((a, b), float('nan'))))

    lines.append('')
    lines.append('Models (quota percentage points consumed per dollar; prior shrinkage tau=%g):' % TAU)
    rows = []
    for j, m in enumerate(models):
        share = cost_total[j] / all_cost
        scale = Kcur / f['K0']
        k, se = f['k'][j] * scale, f['se'][j] * scale
        rel = se / k if k > 0 else 9.9
        if share < 0.002:
            status = 'Estimated from price'
        elif rel < 0.25:
            status = 'Measured'
        else:
            status = 'Few samples'
        rows.append((m, k, se, status, share, cost_total[j]))
        lines.append('  %-20s %.3f +- %.3f  vs price x%.2f  usage in the fit %.2f USD (%.1f%%)  %s'
                     % (m, k, se, k / Kcur, cost_total[j], share * 100, status))
    f2 = None if QUICK else fit(pts, nmod, 1.0)
    if f2:
        lines.append('  Sensitivity (tau=1.0, trusting the data more): ' + '  '.join('%s %.3f' % (m.replace('claude-', ''), f2['k'][j])
                                                      for j, m in enumerate(models) if rows[j][4] >= 0.002))
    # Ex-ante self-check
    ev = preq_5h(snaps, msgs, models, best_lag, best_rho, 10 if QUICK else None)
    pq = preq_stats(ev)
    lines.append('')
    if pq:
        lines.append('Ex-ante self-check (each block is fitted using only the snapshots before it, then predicted; %s blocks, increment >= %d points):' % (
            ('latest %d' % len(ev)) if QUICK else 'all %d' % pq['total'], MIN_FOLD_DELTA))
        lines.append('  Recent (last %d blocks) mean relative error %.0f%%, actual / predicted = %.2f' % (pq['n'], pq['err'] * 100, pq['ratio']))
        if pq['early'] is not None:
            lines.append('  Early (first 10 blocks) mean relative error %.0f%% (there was little data then)' % (pq['early'] * 100))
        if not QUICK:
            for a, b, o, p in ev:
                if o >= MIN_FOLD_DELTA:
                    lines.append('    %s ~ %s   observed %5.1f   ex-ante %5.1f   error %+.0f%%' % (fmt_time(a), fmt_time(b), o, p, (p - o) / o * 100))
    else:
        lines.append('Ex-ante self-check: not enough data yet (%d points are needed before predictions can start).' % MIN_POINTS)

    # Tier correction factors
    modes = fit_modes(snaps, msgs, by_mode, models, f['k'], f['K0'], c5, best_lag, best_rho) if by_mode else None
    lines.append('')
    if modes:
        lines.append('Tier correction factors (quota ~ tier factor x model conversion; prior shrinks toward 1, tau=%g; thinking effort low / medium / high / xhigh / max, ultra = Ultracode on, +fast = fast mode):' % TAU_M)
        for mk in sorted(modes, key=lambda x: -modes[x][3]):
            d, se, st, share = modes[mk]
            lines.append('  %-14s x%.2f +- %.2f   share of usage in the fit %.1f%%   %s' % (mk, d, se, share * 100, st))
    elif by_mode:
        lines.append('Tier correction factors: only one tier has usage so far, nothing to compare.')
    else:
        lines.append('Tier correction factors: no tier tags yet (usage_tags.csv is created after the widget has run for a while).')
    return {'f': f, 'models': models, 'rows': rows, 'lag': best_lag, 'rho': best_rho, 'ft': ft,
            'K': Kcur, 'se': Kse, 'pq': pq, 'modes': modes}


def analyse_week(snaps, msgs, lines, res5):
    segs = prepare(snaps, 'week')
    lines.append('')
    lines.append('=' * 70)
    lines.append('[Weekly quota] (per-model relative-price coefficients are taken from the 5-hour result; one overall slope is fitted per weekly window curve)')
    if not segs or res5 is None:
        lines.append('No usable window curves.')
        return None
    models = res5['models']
    nmod = len(models)
    c = [k / res5['f']['K0'] for k in res5['f']['k']]
    lines.append('%d curves:' % len(segs))
    for s in segs:
        lines.append('  %s ~ %s  %g%% -> %g%% (%d points)' % (fmt_time(s[0]['t']), fmt_time(s[-1]['t']), s[0]['_v'], s[-1]['_v'], len(s)))
    pts, _ = build_points(msgs, segs, models, res5['lag'], res5['rho'], 'cost')
    tpts = None if QUICK else build_points(msgs, segs, models, res5['lag'], 1.0, 'tok')[0]
    segsum, r2, rmse = fit_week(pts, c, len(segs))
    if not segsum:
        lines.append('Fit failed.')
        return None
    lines.append('')
    lines.append('Overall slope of each segment (weekly-quota percentage points consumed per dollar):')
    for s in segsum:
        lines.append('  %s ~ %s   %.4f +- %.4f   (%g%% -> %g%%, %d points)' % (fmt_time(s['t0']), fmt_time(s['t1']), s['K'], s['se'], s['y0'], s['y1'], s['n']))
    ok = [s for s in segsum if s['n'] >= 20 and s['y1'] - s['y0'] >= 5]
    if not ok:
        lines.append('No sufficiently long window curve, no weekly-quota conversion.')
        return None
    cur = ok[-1]
    others = [s for s in ok if s is not cur]
    if others:
        ref = sum(s['K'] / (s['se'] ** 2) for s in others) / sum(1 / (s['se'] ** 2) for s in others)
        ratio = cur['K'] / ref
        sig = abs(cur['K'] - ref) / math.sqrt(cur['se'] ** 2 + min(s['se'] for s in others) ** 2)
        if sig > 3 and (ratio < 0.8 or ratio > 1.25):
            lines.append('  -> The latest segment\'s slope is x%.2f of the earlier ones (%.1f standard errors apart). The weekly-quota basis may have changed (for example a different account / plan, or the weekly quota was reset); '
                         'the conversion now uses only the latest segment.' % (ratio, sig))
        else:
            lines.append('  -> Segment slopes agree (the latest is x%.2f of the earlier ones).' % ratio)
    lines.append('  Using the latest segment: %.4f +- %.4f weekly-quota percentage points per dollar (1%% ~ %.1f USD of official-price usage)' % (cur['K'], cur['se'], 1.0 / cur['K']))
    lines.append('  Goodness of fit (within-group R2, each segment with its own slope): %.3f, RMS residual %.3f percentage points' % (r2, rmse))

    if not QUICK:
        folds_c = fold_eval_week(pts, lambda x: sum(cm * xm for cm, xm in zip(c, x)))
        folds_t = fold_eval_week(tpts, lambda x: sum(x))
        rc, relc, nc = fold_summary(folds_c)
        rt, relt, nt = fold_summary(folds_t)
        lines.append('')
        lines.append('Leave-one-block-out validation (same as above, each segment with its own slope; %d blocks with an increment >= %d percentage points):' % (MIN_FOLD_DELTA, nc))
        lines.append('  By price: RMS prediction error %.2f points, mean relative error %.0f%%' % (rc, relc * 100))
        lines.append('  By tokens (old method, single coefficient): RMS prediction error %.2f points, mean relative error %.0f%%' % (rt, relt * 100))
        tmap = dict(((a, b), p) for a, b, o, p in folds_t)
        for a, b, o, p in folds_c:
            if o >= MIN_FOLD_DELTA:
                lines.append('    %s ~ %s   observed %5.1f   by price %5.1f   tokens %5.1f' % (fmt_time(a), fmt_time(b), o, p, tmap.get((a, b), float('nan'))))
    evw = preq_week(snaps, msgs, models, c, res5['lag'], res5['rho'], 10 if QUICK else None)
    pqw = preq_stats(evw)
    lines.append('')
    if pqw:
        lines.append('Ex-ante self-check (weekly quota, each block uses the slope of "the same window curve before it"): recent (last %d blocks) mean relative error %.0f%%, actual / predicted = %.2f'
                     % (pqw['n'], pqw['err'] * 100, pqw['ratio']))
        if not QUICK:
            for a, b, o, p in evw:
                if o >= MIN_FOLD_DELTA:
                    lines.append('    %s ~ %s   observed %5.1f   ex-ante %5.1f   error %+.0f%%' % (fmt_time(a), fmt_time(b), o, p, (p - o) / o * 100))
    else:
        lines.append('Ex-ante self-check (weekly quota): a new window curve has just started, not enough earlier points yet.')
    return {'K': cur['K'], 'se': cur['se'], 'n': sum(s['n'] for s in segsum), 'r2': r2, 'rmse': rmse, 'pq': pqw}


def plan_of(key):
    return key.split(':', 1)[1] if ':' in key else ''


def scale_result(res5, resw, ratio):
    """Estimated from the plan multiplier: scales another basis's result by ratio (percentage points per dollar are proportional to 1 / plan multiplier), and adds 30% of the multiplier itself to the uncertainty."""
    d5 = copy.deepcopy(res5)
    d5['K'] *= ratio
    d5['se'] = d5['K'] * math.sqrt((res5['se'] / res5['K']) ** 2 + MULT_REL_SD ** 2)
    d5['rows'] = [(m, k * ratio, k * ratio * math.sqrt((se / k) ** 2 + MULT_REL_SD ** 2) if k > 0 else se, 'Estimated from plan multiplier', share, cost)
                  for (m, k, se, st, share, cost) in res5['rows']]
    d5['pq'] = None
    d5['modes'] = None
    dw = None
    if resw:
        dw = copy.deepcopy(resw)
        dw['K'] *= ratio
        dw['se'] = dw['K'] * math.sqrt((resw['se'] / resw['K']) ** 2 + MULT_REL_SD ** 2)
        dw['pq'] = None
    return d5, dw


def rows_for(key, res5, resw, now, derived=''):
    """All output rows of one basis (23 columns, the last one is the basis). derived non-empty = this group is estimated and is written into the status column."""
    f5 = res5['f']
    K5, se5 = res5['K'], res5['se']
    fmtn = lambda v: '' if v is None or v == '' else '%.4f' % v
    pq5, pqw = res5.get('pq'), (resw or {}).get('pq')
    pq, pq_label = pq5, '5h'
    if pqw and (not pq5 or pqw['last_t'] > pq5['last_t']):
        pq, pq_label = pqw, 'Week'
    calib = ['', '', '', '', '']
    if pq:
        calib = ['%.4f' % pq['err'], str(pq['n']), '' if pq['early'] is None else '%.4f' % pq['early'], '%.3f' % pq['ratio'], pq_label]
    rows = [['Global', 'Global', '', '', '', '', '', fmtn(K5), fmtn(se5),
             fmtn(resw['K']) if resw else '', fmtn(resw['se']) if resw else '',
             (derived or 'fit points %d R2=%.3f RMS error %.2f cache-read x%g' % (f5['n'], f5['r2'], f5['rmse'], res5['rho'])),
             ('' if derived else (('fit points %d R2=%.3f RMS error %.2f' % (resw['n'], resw['r2'], resw['rmse'])) if resw else '')), now,
             '', '', '%g' % res5['rho']] + calib + [key]]
    by = dict((r[0], r) for r in res5['rows'])
    for m in sorted(PRICE.keys()):
        p = PRICE[m]
        r = by.get(m)
        k5 = r[1] if r else K5
        e5 = r[2] if r else (TAU * K5 if not derived else K5 * math.sqrt(MULT_REL_SD ** 2 + TAU ** 2))
        st = r[3] if r else (derived and 'Estimated from plan multiplier' or 'Estimated from price')
        kw = ew = None
        if resw:
            cm = k5 / K5
            kw = resw['K'] * cm
            ew = kw * math.sqrt((resw['se'] / resw['K']) ** 2 + (e5 / k5) ** 2)
        tier = TIER.get(m) if USE_TIER else None
        rows.append(['Model', m, p[0], p[1], p[2], p[3], p[4], fmtn(k5), fmtn(e5), fmtn(kw), fmtn(ew), st, st, now,
                     tier[0] if tier else '', tier[1] if tier else '', '', '', '', '', '', '', key])
    for mk, (d, se, st, share) in sorted((res5.get('modes') or {}).items()):
        rows.append(['Tier', mk, '', '', '', '', '', fmtn(d), fmtn(se), fmtn(d), fmtn(se), st, st, now,
                     '', '', '', '', '', '', '', '', key])
    return rows, pq, pq_label


def main():
    try:
        sys.stdout.reconfigure(encoding='utf-8')
    except Exception:
        pass
    # These files do not exist yet on the first run (the widget has only just started recording): not an error, compute once there is data
    for need in ('usage_detail_v2.csv', 'quota_snapshots.csv'):
        if not os.path.exists(os.path.join(BASE, need)):
            print('No data yet: %s is missing (the widget generates it automatically after it has been running for a while)' % need)
            return 0
    tags = load_tags(os.path.join(BASE, 'usage_tags.csv'))
    tl = load_timeline(TIMELINE)
    key_of = make_key_of(tl)
    msgs_k, mode_k = load_messages(os.path.join(BASE, 'usage_detail_v2.csv'), tags, key_of)
    snaps = load_snapshots(os.path.join(BASE, 'quota_snapshots.csv'))
    if not snaps or not msgs_k:
        print('No snapshots or usage records')
        return 1
    snaps_k = {}
    for sn in snaps:
        snaps_k.setdefault(key_of(sn['t']), []).append(sn)
    cur_key = key_of(float('inf')) if tl else ''
    keys = sorted(set(snaps_k) & set(msgs_k), key=lambda k: snaps_k[k][-1]['t'])
    lines = ['Quota conversion report (generated %s)' % datetime.datetime.now().strftime('%Y-%m-%d %H:%M'),
             '%d snapshots (%s ~ %s)' % (len(snaps), fmt_time(snaps[0]['t']), fmt_time(snaps[-1]['t'])),
             'Long-context surcharge tier: %s' % ('modelled at official prices (a single haiku-5-5 request with more than 100k tokens of context is billed x5 as a whole)' if USE_TIER else 'not modelled (--no-tier)')]
    if tl:
        lines.append('%d account-timeline entries, bases (account:plan): %s; current basis %s' % (len(tl), ', '.join(sorted(set('%s:%s' % (e[1], e[2]) for e in tl))), cur_key))
        for k in keys:
            lines.append('  basis %-14s %4d snapshots, usage models: %s' % (k, len(snaps_k[k]), ', '.join(sorted(msgs_k[k].keys()))))
    if tags:
        lines.append('%d tier tags (usage_tags.csv)' % len(tags))

    results = {}
    for k in keys:
        kl = ['', '#' * 70, 'Basis %s' % (k or '(no per-account split)')]
        r5 = analyse5(snaps_k[k], msgs_k[k], kl, mode_k.get(k))
        rw = analyse_week(snaps_k[k], msgs_k[k], kl, r5)
        results[k] = (r5, rw)
        lines.extend(kl)

    targets = list(keys)
    if cur_key in targets:
        targets.remove(cur_key)
    if cur_key and cur_key not in set(keys) | set(results):
        pass
    if cur_key or not targets:
        targets.append(cur_key)          # The current basis is written last (the widget uses the last group when it cannot find its own basis)

    now = datetime.datetime.now().strftime('%Y-%m-%d %H:%M:%S')
    all_rows = []
    own = {}
    lines.append('')
    lines.append('=' * 70)
    lines.append('Output bases:')
    for k in targets:
        res = results.get(k)
        if res and res[0] is not None:
            rows, pq, pql = rows_for(k, res[0], res[1], now)
            own[k] = (res[0], res[1], pq, pql)
            lines.append('  %-14s measured (fit points %d)' % (k, res[0]['f']['n']))
            all_rows.extend(rows)
            continue
        # No result of its own: estimate from the basis with the most data using the plan multiplier
        cur_plan = plan_of(k)
        refs = [(r[0]['f']['n'], rk) for rk, r in results.items() if r[0] is not None and rk != k and plan_of(rk) in PLAN_MULT]
        if cur_plan in PLAN_MULT and refs:
            refs.sort(reverse=True)
            rk = refs[0][1]
            ratio = PLAN_MULT[plan_of(rk)] / PLAN_MULT[cur_plan]
            d5, dw = scale_result(results[rk][0], results[rk][1], ratio)
            note = 'estimated from %s using the plan multiplier (x%.3g)' % (rk, ratio)
            rows, _, _ = rows_for(k, d5, dw, now, note)
            lines.append('  %-14s %s' % (k, note))
            all_rows.extend(rows)
        else:
            lines.append('  %-14s not enough data yet, and no estimate from the plan multiplier is possible either (plan not set or no basis to refer to)' % k)
    lines.append('')
    lines.append('Limitations: quota percentages are integers and snapshots come roughly every 6 minutes; models that have never appeared can only be estimated at official prices; ')
    lines.append('quota the same account used elsewhere (web, other computers) is not recorded, which makes the coefficients slightly high; plan multipliers are only the official nominal values and may differ in practice.')
    if not QUICK:
        print('\n'.join(lines))
        with io.open(OUT_REPORT, 'w', encoding='utf-8-sig', newline='') as fh:
            fh.write('\n'.join(lines) + '\n')
    if not all_rows:
        print('\nNo usable result, quota_conversion.csv is not written.')
        return 1

    tmp = OUT_CSV + '.tmp'
    with io.open(tmp, 'w', encoding='utf-8-sig', newline='') as fh:
        w = csv.writer(fh)
        w.writerow(['Type', 'Name', 'Input price', 'Output price', 'Write 5m price', 'Write 1h price', 'Read price', '5h quota % per USD', '5h error',
                    'Weekly quota % per USD', 'Weekly error', '5h status', 'Weekly status', 'Updated at',
                    'Long-context threshold', 'Long-context multiplier', 'Cache-read scale', 'Recent error', 'Error blocks', 'Early error', 'Recent actual/predicted', 'Error basis', 'Account key'])
        w.writerows(all_rows)
    os.replace(tmp, OUT_CSV)
    if cur_key in own:
        r5, rw, pq, pql = own[cur_key]
        append_history(now, cur_key, r5['f'], r5, rw, pq, pql)
    print('\nWritten %s' % OUT_CSV)
    return 0


def append_history(now, ckey, f5, res5, resw, pq, pq_label):
    """Appends the key numbers to quota_conversion_history.csv (nothing is recorded when the number of fit points has not changed) so you can see how coefficients and errors converge as data grows."""
    try:
        last = ''
        if os.path.exists(OUT_HISTORY):
            with io.open(OUT_HISTORY, encoding='utf-8-sig', newline='') as fh:
                rows = list(csv.reader(fh))
            if len(rows) > 1:
                last = rows[-1][1] if len(rows[-1]) > 1 else ''
        key = '%s %d/%d' % (ckey, f5['n'], resw['n'] if resw else 0)
        if key == last:
            return
        haiku = [r for r in res5['rows'] if r[0] == 'claude-haiku-5-5']
        new = not os.path.exists(OUT_HISTORY)
        with io.open(OUT_HISTORY, 'a', encoding='utf-8-sig' if new else 'utf-8', newline='') as fh:
            w = csv.writer(fh)
            if new:
                w.writerow(['Time', 'Basis fit points(5h/week)', '5h quota % per USD', '5h error', 'Cache-read scale', 'haiku relative price', 'Weekly quota % per USD',
                            'Recent error', 'Error blocks', 'Error basis', 'Measured models'])
            measured = len([r for r in res5['rows'] if r[3] == 'Measured'])
            w.writerow([now, key, '%.4f' % res5['K'], '%.4f' % res5['se'], '%g' % res5['rho'],
                        ('%.2f' % (haiku[0][1] / res5['K'])) if haiku else '', ('%.4f' % resw['K']) if resw else '',
                        ('%.4f' % pq['err']) if pq else '', pq['n'] if pq else '', pq_label if pq else '', measured])
    except (IOError, OSError, ValueError):
        pass


if __name__ == '__main__':
    sys.exit(main())

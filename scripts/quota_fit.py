# -*- coding: utf-8 -*-
"""
额度换算：用「额度快照」里的百分比，反推每个模型每花 1 美元（按官方价格折算）会消耗多少额度%。

和旧的 分析.py（按 token 数拟合每个模型的权重）相比，这里改了三件事：

1. 用量按官方价格折算成「美元」再拟合。缓存读取的 token 数占了 98%，但它单价只有输出的 1/50，
   按 token 数算，缓存读取会把一切淹没；按价格折算后，输入 / 输出 / 缓存写入 / 缓存读取各按各的价。
2. 拟合累计量，不拟合增量。快照里的百分比是整数，每两次快照之间的增量（0、1、2）里舍入误差占了大头；
   把同一个额度窗口里的整条曲线一起拟合（每个窗口一个起点），舍入误差不会累积，样本利用率高得多。
3. 没有数据的模型不瞎猜、也不留空：向「按官方价格折算」收缩（岭回归），数据多了才会偏离价格；
   每个数都带误差，并标明是「实测」「样本少」还是「按价格推算」。

账号与计划（v24）：
  · 额度是按账号和计划算的：Pro 和 Max 同样的用量，消耗的百分点差好几倍，换了账号或计划，系数就变了。
    悬浮窗记录的「账号时段.csv」（从什么时候起用量归到哪个账号、什么计划）把快照和用量按「账号:计划」切开，每个口径各拟合一组系数；
  · 新账号 / 新计划刚开始没有数据时，按套餐倍率（Pro 1x、Max 5x、Max 20x）从数据最多的口径推算一个初始值，标成「按套餐倍率推算」，
    攒够数据后自动换成实测；
  · 没有 账号时段.csv（旧版本）就当作一个口径，和以前一样。

自我纠错（越用越准）：
  · 每次运行都用全部快照重新拟合；新出现的模型、新出现的档位，数据够了就从「按价格推算」升级成「实测」；
  · 口径变了（换账号 / 套餐 / 周额度重置）会自动发现：最近一段和之前差很多，就只用最近一段，并按时间新旧加权；
  · 事前预测自检：每一块数据都先只用它之前的快照拟合、再去预测它，记录误差——这是最诚实的准确度，悬浮窗里显示的「估算误差」就是它；
  · 档位（思考强度 low / medium / high / xhigh / max，Ultracode 开着算 ultra，快速模式 +fast）各有一个修正系数，向 1 收缩，数据多了才偏离；
  · 每次运行把关键数字追加到 额度换算_历史.csv，可以看到系数和误差随时间收敛。

另外：额度显示 100% 时是被封顶的（真实值可能更高），这些点不参与拟合；
额度窗口重置（含周额度被清零）会自动分段；偏大的残差（比如同一账号在别处用掉的额度）用 Huber 权重压低影响。

输出：额度换算.csv（悬浮窗读它）、额度换算_报告.txt（方法 / 精度 / 每段窗口的预测对比）
用法：python 额度换算.py
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

# 数据文件夹：悬浮窗调用时通过环境变量 STILLA_DATA 传入；手动运行时可用 --data <文件夹>，都没有就用脚本所在的文件夹
BASE = os.environ.get('STILLA_DATA') or os.path.dirname(os.path.abspath(__file__))
if '--data' in sys.argv and sys.argv.index('--data') + 1 < len(sys.argv):
    BASE = sys.argv[sys.argv.index('--data') + 1]
OUT_CSV = os.path.join(BASE, '额度换算.csv')
OUT_REPORT = os.path.join(BASE, '额度换算_报告.txt')
OUT_HISTORY = os.path.join(BASE, '额度换算_历史.csv')
TIMELINE = os.path.join(BASE, '账号时段.csv')
# 各计划相对 Pro 的官方标称用量倍率：额度换成「每美元消耗的百分点」后，和倍率成反比。只用来给还没有数据的新口径一个初始估计
PLAN_MULT = {'pro': 1.0, 'max5': 5.0, 'max20': 20.0}
MULT_REL_SD = 0.3    # 按倍率推算的相对不确定度

# 官方价格（美元 / 百万 token）：输入、输出、5 分钟缓存写入、1 小时缓存写入、缓存读取
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

# 长上下文加价档：一次请求的上下文（输入 + 缓存写入 + 缓存读取）超过阈值，整条按「倍率 × 官方价」算
TIER = {
    'claude-haiku-5-5': (100000, 5.0),
}

TAU = 0.5            # 先验：各模型的「每美元额度」和全体平均相差多少（相对值，0.5 = 一般不会偏离一半以上）
QUANT_SD = 0.29      # 整数百分比的舍入误差（均匀分布）的标准差，拟合残差的下限参考
HUBER_C = 1.5
MIN_POINTS = 40      # 参与拟合的点少于这个数就不写结果
FOLD_HOURS = 2.0     # 留一法验证：把曲线按 2 小时切块，每块用「其余块」拟合的系数去预测它的增量
MIN_FOLD_DELTA = 3   # 验证只看增量 >= 3 个百分点的块（太小的块被舍入误差淹没）
LAGS = [-120, -60, 0, 60, 120, 180, 300]
RHOS = [0.25, 0.35, 0.5, 0.65, 0.75, 1.0, 1.5, 2.0]   # 缓存读取单价的缩放系数，用来验证「缓存读取按官方价格」这个假设
EPOCH = datetime.datetime(2020, 1, 1)
USE_TIER = '--no-tier' not in sys.argv
QUICK = '--quick' in sys.argv     # 悬浮窗在后台调用：只出系数，不做对照和留一块验证，不写报告
TAU_M = 0.5          # 档位修正系数的先验宽度（相对 1）
PREQ_RECENT = 12     # 「近期误差」看最近这么多块
HALF_LIFE_DAYS = 7.0  # 口径没变时，各段窗口曲线按新旧加权的半衰期
GAIN = 0.10          # lag / 缓存读取缩放：RMSE 改善不到 10% 就不动（保持官方价格、不对齐），避免过拟合


def parse_time(s):
    return (datetime.datetime.strptime(s.strip(), '%Y-%m-%d %H:%M:%S') - EPOCH).total_seconds()


def fmt_time(t):
    return (EPOCH + datetime.timedelta(seconds=t)).strftime('%m-%d %H:%M')


def base_model(m):
    return re.sub(r'-\d{8}$', '', m or '')


# ---------------------------------------------------------------- 读数据

def load_tags(path):
    """用量标注.csv：消息ID → (思考强度, Ultra, 快速模式, 思考token)。没有文件就是空。"""
    tags = {}
    if not os.path.exists(path):
        return tags
    with io.open(path, encoding='utf-8-sig', newline='') as f:
        for line in f:
            p = line.rstrip('\r\n').split(',')
            if len(p) < 5 or not p[0] or p[0] == '消息ID':
                continue
            tags[p[0]] = (p[1], p[2] == '1', p[3] == 'fast', int(p[4]) if p[4].isdigit() else 0)
    return tags


def mode_key(tag):
    """档位名：ultra（开着 Ultracode）> 思考强度；快速模式后面加 +fast；记录里没有的叫「未标注」。和悬浮窗里一致。"""
    if tag is None:
        return '未标注'
    effort, ultra, fast, _ = tag
    k = 'ultra' if ultra else (effort or '未标注')
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
    """账号时段.csv（时间是 UTC）→ [(本地时间秒, 账号, 计划)]，按时间排好；没有文件就是空。"""
    out = []
    if not os.path.exists(path):
        return out
    with io.open(path, encoding='utf-8-sig', newline='') as f:
        for r in csv.reader(f):
            if len(r) < 3 or r[0].startswith('时间'):
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
    """时刻 → 口径「账号:计划」。第一条记录之前的时间归第一条（和悬浮窗里一致）；没有时间线就都是 ''。"""
    if not tl:
        return lambda t: ''
    times = [e[0] for e in tl]
    keys = ['%s:%s' % (e[1], e[2]) for e in tl]

    def key_of(t):
        return keys[max(bisect.bisect_right(times, t) - 1, 0)]
    return key_of


def load_messages(path, tags=None, key_of=None):
    """返回 (msgs_by_key, by_mode_by_key)。msgs = {模型: {t, base, read, tok}}；base = 除缓存读取外的美元费用的累计，
    read = 缓存读取费用的累计，tok = 总 token 的累计。by_mode = {档位: 同样的结构}，有标注文件时才有。
    key_of 不为空时按口径（账号:计划）分开，否则都在键 '' 下。"""
    rows = []
    with io.open(path, encoding='utf-8-sig', newline='') as f:
        for r in csv.DictReader(f):
            m = base_model(r['模型'])
            if m not in PRICE:
                continue
            try:
                t = parse_time(r['时间'])
                i, o = int(r['输入']), int(r['输出'])
                c5, c1, cr = int(r['缓存写入5分钟']), int(r['缓存写入1小时']), int(r['缓存读取'])
            except (ValueError, KeyError):
                continue
            rows.append((t, m, i, o, c5, c1, cr, (r.get('消息ID') or '').strip()))
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
                t = parse_time(r['时间'])
            except (ValueError, KeyError):
                continue
            out.append({'t': t, 'p5': num('5小时已用%'), 'r5': (r.get('5小时重置时间') or '').strip(),
                        'pw': num('每周已用%'), 'rw': (r.get('每周重置时间') or '').strip()})
    out.sort(key=lambda s: s['t'])
    return out


def segments(snaps, kind):
    """同一个额度窗口里连续的快照。窗口边界、读数缺失、百分比回落都会断开；显示 100% 的点被封顶，丢掉它和之后的点。"""
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


# ---------------------------------------------------------------- 线性代数（小矩阵）

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


# ---------------------------------------------------------------- 构造拟合用的点

def cum_vec(msgs, models, t, lag, rho, feat):
    """时刻 t 之前（含）累计的各模型用量。feat = 'cost'（美元）或 'tok'（百万 token）。消息时间按 lag 秒后移后再比较。"""
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
    """每段做组内去均值（每个窗口一个起点），返回 points = [dict(y, x, seg, t)] 和每段的 (起点x, 终点x, 起点y, 终点y)。"""
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


# ---------------------------------------------------------------- 拟合

def fit(pts, nmod, tau=TAU, mask=None):
    """两步：1) 单个斜率 K0（所有模型按同一个「每美元额度」，Huber 稳健）；2) 各模型向 K0 收缩的岭回归。
    mask：可选，布尔列表，False 的点不参与（留一法用）。"""
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
    # 第二步：各模型一个斜率，向 K0 收缩
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
    # 残差自相关：同一段里相邻的点残差相关时，有效样本数比点数少，误差要放大
    num = den = 0.0
    prev = None
    for p, r in zip(use, res):
        if prev is not None and prev[0] == p['seg']:
            num += prev[1] * r
            den += prev[1] * prev[1]
        prev = (p['seg'], r)
    rho1 = max(0.0, min(0.95, num / den)) if den > 0 else 0.0
    infl = min(25.0, (1 + rho1) / (1 - rho1))
    se = [min(math.sqrt(max(Ainv[a][a], 0.0) * infl), tau * K0) for a in range(nmod)]   # 没有数据的模型，误差就是先验的宽度
    sst = sum(yi * yi for yi in y)
    ssr = sum(r * r for r in res)
    # 单斜率的误差（用同样的放大系数）
    sxx = sum(wi * si * si for wi, si in zip(w, s))
    se0 = math.sqrt(sigma2 * infl / sxx) if sxx > 0 else float('nan')
    return {'K0': K0, 'se0': se0, 'k': k, 'se': se, 'r2': 1 - ssr / sst if sst > 0 else 0.0,
            'rmse': math.sqrt(ssr / len(use)), 'n': len(use), 'infl': infl, 'rho1': rho1,
            'sigma': math.sqrt(sigma2)}


def pooled_rmse(pts, nmod):
    """只用单斜率时的残差均方根（扫描 lag / rho 时用，不做第二步）。"""
    f = fit(pts, nmod, tau=1e-6)   # tau 极小 = 所有模型都被钉在 K0 上
    return (f['rmse'], f['r2'], f['K0']) if f else (float('inf'), 0.0, 0.0)


def fold_eval(pts, nmod, tau):
    """留一块验证：返回 [(块起点, 块终点, 观测增量, 预测增量)]，预测用的系数来自「不含这一块」的拟合。"""
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


# ---------------------------------------------------------------- 周额度：各模型的相对系数沿用 5 小时额度的结果，每段窗口一个斜率

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
    """留一块：每块的斜率来自「同一段窗口里不含这一块」的点（周额度各段的斜率可能不同）。zfun(rx) = 点的累计用量（价格折算 或 token）。"""
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
    """把每段曲线按 FOLD_HOURS 小时切块，返回每块的点下标（至少 3 个点）。"""
    t0s = {}
    for p in pts:
        t0s.setdefault(p['seg'], p['t'])
    blocks = {}
    for i, p in enumerate(pts):
        blocks.setdefault((p['seg'], int((p['t'] - t0s[p['seg']]) // (FOLD_HOURS * 3600))), []).append(i)
    return [idx for _, idx in sorted(blocks.items()) if len(idx) >= 3]


def current_K(segsum, now_t):
    """当前口径的整体斜率。最近一段和之前各段差得很多（超过 3 个标准误、且相差 20% 以上）就认为口径变了，只用最近一段；
    否则各段按精度和时间新旧加权平均（半衰期 HALF_LIFE_DAYS 天）。返回 (K, 误差, 说明, 口径变了没有)，没有足够长的曲线返回 None。"""
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
            note = ('最近一段的斜率是之前的 ×%.2f（相差 %.1f 个标准误）。口径可能变了（例如换了账号 / 套餐，或额度被重置）；'
                    '现在的换算只采用最近一段。' % (ratio, sig))
        else:
            note = '各段斜率一致（最近一段是之前的 ×%.2f），按精度和时间新旧加权平均。' % ratio
    use = [cur] if changed or not others else ok
    ws = [(1 / (x['se'] ** 2)) * 0.5 ** (max(0.0, now_t - x['t1']) / 86400.0 / HALF_LIFE_DAYS) for x in use]
    K = sum(w * x['K'] for w, x in zip(ws, use)) / sum(ws)
    se = 1.0 / math.sqrt(sum(1 / (x['se'] ** 2) for x in use))
    return K, se, note, changed


def preq_5h(snaps, msgs, models, lag, rho, last_n=None):
    """事前预测（5 小时额度）：每一块先只用它之前的快照拟合，再预测它的增量。返回 [(块起点, 块终点, 观测增量, 预测增量)]。"""
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
    """事前预测（每周额度）：同上；斜率取「这一块之前、同一段窗口曲线」拟合的，之前的点少于 8 个就不评。"""
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
    """事前预测的汇总：近期（最后 PREQ_RECENT 块）平均相对误差、块数、实际/预测之比；早期（最前面 10 块）平均相对误差。"""
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
    """各档位的修正系数 d：额度 ≈ Σ_档位 d × Σ_模型 k_模型 × 该档位里该模型用量折算的美元数。d 向 1 收缩（先验宽度 TAU_M）。
    用 5 小时曲线和每周曲线一起拟合（每周曲线按各段自己的斜率换算到 5 小时的尺度——换账号 / 套餐之后只有每周额度的时段也能用）。
    只有一个档位（或没有标注）时不拟合。返回 {档位: (d, 误差, 状态, 占比)}。"""
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
        segsw = prepare(snaps, 'week')      # fit_week 之后 _v 还是周额度，再来一遍保险
        add_segments(segsw, scales, 1000)
    if len(pts) < MIN_POINTS:
        return None
    y0 = [p['y'] - sum(p['x']) for p in pts]          # 所有档位系数都是 1 时的残差
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
        # 误差再放大 2 倍留余量：同一档位的消息不是随机抽样，单价偏差会连成片；偏离 1 不到 2.5 个（放大后的）标准误就当作没有差别
        se = min(2.0 * math.sqrt(max(Ainv[j][j], 0.0) * infl), TAU_M)
        d = max(0.05, 1.0 + delta[j])
        if share < 0.003:
            out[mk] = (1.0, TAU_M, '按价格推算', share)
        elif abs(d - 1.0) < 2.5 * se:
            out[mk] = (1.0, se, '无明显差异', share)
        else:
            out[mk] = (d, se, '实测' if se / d < 0.25 else '样本少', share)
    return out


# ---------------------------------------------------------------- 主流程

def analyse5(snaps, msgs, lines, by_mode=None):
    kind = '5h'
    segs = prepare(snaps, kind)
    lines.append('')
    lines.append('=' * 70)
    lines.append('【5 小时额度】')
    if not segs:
        lines.append('没有可用的窗口曲线。')
        return None
    npts = sum(len(s) for s in segs)
    lines.append('窗口曲线 %d 段，共 %d 个快照点（已去掉显示 100%% 的封顶点和窗口边界）' % (len(segs), npts))
    for s in segs:
        lines.append('  %s ~ %s  %g%% → %g%%（%d 点）' % (fmt_time(s[0]['t']), fmt_time(s[-1]['t']), s[0]['_v'], s[-1]['_v'], len(s)))
    models = sorted(msgs.keys())
    nmod = len(models)

    # 1) 缓存读取单价缩放：验证「缓存读取按官方价格」这个假设（缓存读取占了 98% 的 token、一半的费用）
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
    lines.append('缓存读取单价缩放扫描（残差均方根，越小越好；1.0 = 官方价格）：' + '  '.join('×%g:%.3f' % (r[0], r[1]) for r in rscan))
    lines.append('采用缓存读取 ×%g（改善不到 %d%% 就按官方价格）' % (best_rho, GAIN * 100))

    # 2) 时间对齐：消息时间 + lag 才是额度实际扣减的时间
    scan = []
    for lag in LAGS:
        pts, _ = build_points(msgs, segs, models, lag, best_rho, 'cost')
        scan.append((lag,) + pooled_rmse(pts, nmod))
    best_lag = min(scan, key=lambda r: r[1])[0]
    base_rmse = [r for r in scan if r[0] == 0][0][1]
    best_rmse = [r for r in scan if r[0] == best_lag][0][1]
    if base_rmse - best_rmse < GAIN * base_rmse:
        best_lag = 0
    lines.append('时间对齐扫描（残差均方根）：' + '  '.join('%+ds:%.3f' % (r[0], r[1]) for r in scan))
    lines.append('采用 lag = %+d 秒（改善不到 %d%% 就不对齐）' % (best_lag, GAIN * 100))

    pts, seginfo = build_points(msgs, segs, models, best_lag, best_rho, 'cost')
    f = fit(pts, nmod, TAU)
    if f is None or f['n'] < MIN_POINTS:
        lines.append('点数太少（%d），不出结果。' % (0 if f is None else f['n']))
        return None

    # 当前口径：逐段斜率 → 口径变了就只用最近一段，否则按精度和新旧加权
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
        lines.append('拟合结果（%d 个点；残差自相关 %.2f，误差已按有效样本数放大）：' % (f['n'], f['rho1']))
        lines.append('  全体平均：每美元折算消耗 %.3f ± %.3f 个额度百分点（1%% ≈ %.2f 美元的官方价格用量）' % (f['K0'], f['se0'], 1.0 / f['K0']))
        if kcur_note:
            lines.append('  当前口径：%.3f ± %.3f。%s' % (Kcur, Kse, kcur_note))
        lines.append('  拟合优度（组内 R²）：按价格折算 %.3f；对照 按 token 数（单一系数）%.3f，按 token 数（每模型一个系数）%.3f'
                     % (f_cost_pool['r2'], f_tok_pool['r2'], ft['r2']))
        lines.append('  残差均方根（百分点）：按价格折算 %.3f；对照 按 token 数（每模型一个系数）%.3f；整数舍入误差的下限约 %.2f'
                     % (f['rmse'], ft['rmse'], QUANT_SD))
        # 逐段斜率：看口径稳不稳
        lines.append('  逐段斜率（每段单独用一个系数）：')
        for si, seg in enumerate(segs):
            sp = [p for p in pts if p['seg'] == si]
            h = huber_slope([sum(p['x']) for p in sp], [p['y'] for p in sp])
            if h:
                lines.append('    %s ~ %s   %.3f   （该段共 %g 个百分点）' % (fmt_time(seg[0]['t']), fmt_time(seg[-1]['t']), h[0], seg[-1]['_v'] - seg[0]['_v']))

        folds_c = fold_eval(pts, nmod, TAU)
        folds_t = fold_eval(tpts, nmod, 5.0)
        rc, relc, nc = fold_summary(folds_c)
        rt, relt, nt = fold_summary(folds_t)
        lines.append('')
        lines.append('留一块验证（曲线按 %g 小时切块，每块只用其余块拟合的系数去预测它的增量；只看增量 ≥ %d 个百分点的块，共 %d 块）：'
                     % (FOLD_HOURS, MIN_FOLD_DELTA, nc))
        lines.append('  按价格折算：预测误差均方根 %.2f 个百分点，平均相对误差 %.0f%%' % (rc, relc * 100))
        lines.append('  按 token 数（旧方法）：预测误差均方根 %.2f 个百分点，平均相对误差 %.0f%%' % (rt, relt * 100))
        lines.append('  逐块对比（观测增量 / 价格折算预测 / token 预测）：')
        tmap = dict(((a, b), p) for a, b, o, p in folds_t)
        for a, b, o, p in folds_c:
            if o >= MIN_FOLD_DELTA:
                lines.append('    %s ~ %s   观测 %5.1f   价格折算 %5.1f   token %5.1f' % (fmt_time(a), fmt_time(b), o, p, tmap.get((a, b), float('nan'))))

    lines.append('')
    lines.append('各模型（每美元折算消耗的额度百分点；先验收缩 τ=%g）：' % TAU)
    rows = []
    for j, m in enumerate(models):
        share = cost_total[j] / all_cost
        scale = Kcur / f['K0']
        k, se = f['k'][j] * scale, f['se'][j] * scale
        rel = se / k if k > 0 else 9.9
        if share < 0.002:
            status = '按价格推算'
        elif rel < 0.25:
            status = '实测'
        else:
            status = '样本少'
        rows.append((m, k, se, status, share, cost_total[j]))
        lines.append('  %-20s %.3f ± %.3f  相对价格 ×%.2f  参与拟合的用量 %.2f 美元（%.1f%%）  %s'
                     % (m, k, se, k / Kcur, cost_total[j], share * 100, status))
    f2 = None if QUICK else fit(pts, nmod, 1.0)
    if f2:
        lines.append('  敏感性（τ=1.0，更相信数据）：' + '  '.join('%s %.3f' % (m.replace('claude-', ''), f2['k'][j])
                                                      for j, m in enumerate(models) if rows[j][4] >= 0.002))
    # 事前预测自检
    ev = preq_5h(snaps, msgs, models, best_lag, best_rho, 10 if QUICK else None)
    pq = preq_stats(ev)
    lines.append('')
    if pq:
        lines.append('事前预测自检（每一块先只用它之前的快照拟合，再预测它；%s块，增量 ≥ %d 个百分点）：' % (
            ('最近 %d' % len(ev)) if QUICK else '共 %d' % pq['total'], MIN_FOLD_DELTA))
        lines.append('  近期（最后 %d 块）平均相对误差 %.0f%%，实际 / 预测 = %.2f' % (pq['n'], pq['err'] * 100, pq['ratio']))
        if pq['early'] is not None:
            lines.append('  早期（最前面 10 块）平均相对误差 %.0f%%（那时数据少）' % (pq['early'] * 100))
        if not QUICK:
            for a, b, o, p in ev:
                if o >= MIN_FOLD_DELTA:
                    lines.append('    %s ~ %s   观测 %5.1f   事前预测 %5.1f   误差 %+.0f%%' % (fmt_time(a), fmt_time(b), o, p, (p - o) / o * 100))
    else:
        lines.append('事前预测自检：数据还不够（需要先有 %d 个点才能开始预测）。' % MIN_POINTS)

    # 档位修正系数
    modes = fit_modes(snaps, msgs, by_mode, models, f['k'], f['K0'], c5, best_lag, best_rho) if by_mode else None
    lines.append('')
    if modes:
        lines.append('档位修正系数（额度 ≈ 档位系数 × 模型折算；先验向 1 收缩，τ=%g；思考强度 low / medium / high / xhigh / max，ultra = 开着 Ultracode，+fast = 快速模式）：' % TAU_M)
        for mk in sorted(modes, key=lambda x: -modes[x][3]):
            d, se, st, share = modes[mk]
            lines.append('  %-14s ×%.2f ± %.2f   参与拟合的用量占比 %.1f%%   %s' % (mk, d, se, share * 100, st))
    elif by_mode:
        lines.append('档位修正系数：目前只有一个档位的用量，没法比较。')
    else:
        lines.append('档位修正系数：还没有档位标注（悬浮窗 v23 运行一阵子后会生成 用量标注.csv）。')
    return {'f': f, 'models': models, 'rows': rows, 'lag': best_lag, 'rho': best_rho, 'ft': ft,
            'K': Kcur, 'se': Kse, 'pq': pq, 'modes': modes}


def analyse_week(snaps, msgs, lines, res5):
    segs = prepare(snaps, 'week')
    lines.append('')
    lines.append('=' * 70)
    lines.append('【每周额度】（各模型相对价格的系数沿用 5 小时额度的结果；周额度每一段窗口曲线各拟合一个整体斜率）')
    if not segs or res5 is None:
        lines.append('没有可用的窗口曲线。')
        return None
    models = res5['models']
    nmod = len(models)
    c = [k / res5['f']['K0'] for k in res5['f']['k']]
    lines.append('曲线 %d 段：' % len(segs))
    for s in segs:
        lines.append('  %s ~ %s  %g%% → %g%%（%d 点）' % (fmt_time(s[0]['t']), fmt_time(s[-1]['t']), s[0]['_v'], s[-1]['_v'], len(s)))
    pts, _ = build_points(msgs, segs, models, res5['lag'], res5['rho'], 'cost')
    tpts = None if QUICK else build_points(msgs, segs, models, res5['lag'], 1.0, 'tok')[0]
    segsum, r2, rmse = fit_week(pts, c, len(segs))
    if not segsum:
        lines.append('拟合失败。')
        return None
    lines.append('')
    lines.append('每段的整体斜率（每美元折算消耗的周额度百分点）：')
    for s in segsum:
        lines.append('  %s ~ %s   %.4f ± %.4f   （%g%% → %g%%，%d 点）' % (fmt_time(s['t0']), fmt_time(s['t1']), s['K'], s['se'], s['y0'], s['y1'], s['n']))
    ok = [s for s in segsum if s['n'] >= 20 and s['y1'] - s['y0'] >= 5]
    if not ok:
        lines.append('没有足够长的窗口曲线，不出周额度换算。')
        return None
    cur = ok[-1]
    others = [s for s in ok if s is not cur]
    if others:
        ref = sum(s['K'] / (s['se'] ** 2) for s in others) / sum(1 / (s['se'] ** 2) for s in others)
        ratio = cur['K'] / ref
        sig = abs(cur['K'] - ref) / math.sqrt(cur['se'] ** 2 + min(s['se'] for s in others) ** 2)
        if sig > 3 and (ratio < 0.8 or ratio > 1.25):
            lines.append('  → 最近一段的斜率是之前的 ×%.2f（相差 %.1f 个标准误）。周额度的口径可能变了（例如换了账号 / 套餐，或周额度被重置）；'
                         '现在的换算只采用最近一段。' % (ratio, sig))
        else:
            lines.append('  → 各段斜率一致（最近一段是之前的 ×%.2f）。' % ratio)
    lines.append('  采用最近一段：每美元 %.4f ± %.4f 个周额度百分点（1%% ≈ %.1f 美元的官方价格用量）' % (cur['K'], cur['se'], 1.0 / cur['K']))
    lines.append('  拟合优度（组内 R²，每段各用自己的斜率）：%.3f，残差均方根 %.3f 个百分点' % (r2, rmse))

    if not QUICK:
        folds_c = fold_eval_week(pts, lambda x: sum(cm * xm for cm, xm in zip(c, x)))
        folds_t = fold_eval_week(tpts, lambda x: sum(x))
        rc, relc, nc = fold_summary(folds_c)
        rt, relt, nt = fold_summary(folds_t)
        lines.append('')
        lines.append('留一块验证（同上，每段用各自的斜率；增量 ≥ %d 个百分点的块共 %d 块）：' % (MIN_FOLD_DELTA, nc))
        lines.append('  按价格折算：预测误差均方根 %.2f 个百分点，平均相对误差 %.0f%%' % (rc, relc * 100))
        lines.append('  按 token 数（旧方法，单一系数）：预测误差均方根 %.2f 个百分点，平均相对误差 %.0f%%' % (rt, relt * 100))
        tmap = dict(((a, b), p) for a, b, o, p in folds_t)
        for a, b, o, p in folds_c:
            if o >= MIN_FOLD_DELTA:
                lines.append('    %s ~ %s   观测 %5.1f   价格折算 %5.1f   token %5.1f' % (fmt_time(a), fmt_time(b), o, p, tmap.get((a, b), float('nan'))))
    evw = preq_week(snaps, msgs, models, c, res5['lag'], res5['rho'], 10 if QUICK else None)
    pqw = preq_stats(evw)
    lines.append('')
    if pqw:
        lines.append('事前预测自检（每周额度，每块用「它之前、同一段窗口曲线」的斜率）：近期（最后 %d 块）平均相对误差 %.0f%%，实际 / 预测 = %.2f'
                     % (pqw['n'], pqw['err'] * 100, pqw['ratio']))
        if not QUICK:
            for a, b, o, p in evw:
                if o >= MIN_FOLD_DELTA:
                    lines.append('    %s ~ %s   观测 %5.1f   事前预测 %5.1f   误差 %+.0f%%' % (fmt_time(a), fmt_time(b), o, p, (p - o) / o * 100))
    else:
        lines.append('事前预测自检（每周额度）：新一段窗口曲线刚开始，之前的点还不够。')
    return {'K': cur['K'], 'se': cur['se'], 'n': sum(s['n'] for s in segsum), 'r2': r2, 'rmse': rmse, 'pq': pqw}


def plan_of(key):
    return key.split(':', 1)[1] if ':' in key else ''


def scale_result(res5, resw, ratio):
    """按套餐倍率推算：把另一个口径的结果整体乘 ratio（每美元消耗的百分点 ∝ 1 / 套餐倍率），不确定度加上倍率本身的 30%。"""
    d5 = copy.deepcopy(res5)
    d5['K'] *= ratio
    d5['se'] = d5['K'] * math.sqrt((res5['se'] / res5['K']) ** 2 + MULT_REL_SD ** 2)
    d5['rows'] = [(m, k * ratio, k * ratio * math.sqrt((se / k) ** 2 + MULT_REL_SD ** 2) if k > 0 else se, '按套餐倍率推算', share, cost)
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
    """一个口径的全部输出行（23 列，最后一列是口径）。derived 不为空 = 这组是推算出来的，写进状态列。"""
    f5 = res5['f']
    K5, se5 = res5['K'], res5['se']
    fmtn = lambda v: '' if v is None or v == '' else '%.4f' % v
    pq5, pqw = res5.get('pq'), (resw or {}).get('pq')
    pq, pq_label = pq5, '5h'
    if pqw and (not pq5 or pqw['last_t'] > pq5['last_t']):
        pq, pq_label = pqw, '周'
    calib = ['', '', '', '', '']
    if pq:
        calib = ['%.4f' % pq['err'], str(pq['n']), '' if pq['early'] is None else '%.4f' % pq['early'], '%.3f' % pq['ratio'], pq_label]
    rows = [['全局', '全局', '', '', '', '', '', fmtn(K5), fmtn(se5),
             fmtn(resw['K']) if resw else '', fmtn(resw['se']) if resw else '',
             (derived or '拟合点%d R2=%.3f 误差均方根%.2f 缓存读取x%g' % (f5['n'], f5['r2'], f5['rmse'], res5['rho'])),
             ('' if derived else (('拟合点%d R2=%.3f 误差均方根%.2f' % (resw['n'], resw['r2'], resw['rmse'])) if resw else '')), now,
             '', '', '%g' % res5['rho']] + calib + [key]]
    by = dict((r[0], r) for r in res5['rows'])
    for m in sorted(PRICE.keys()):
        p = PRICE[m]
        r = by.get(m)
        k5 = r[1] if r else K5
        e5 = r[2] if r else (TAU * K5 if not derived else K5 * math.sqrt(MULT_REL_SD ** 2 + TAU ** 2))
        st = r[3] if r else (derived and '按套餐倍率推算' or '按价格推算')
        kw = ew = None
        if resw:
            cm = k5 / K5
            kw = resw['K'] * cm
            ew = kw * math.sqrt((resw['se'] / resw['K']) ** 2 + (e5 / k5) ** 2)
        tier = TIER.get(m) if USE_TIER else None
        rows.append(['模型', m, p[0], p[1], p[2], p[3], p[4], fmtn(k5), fmtn(e5), fmtn(kw), fmtn(ew), st, st, now,
                     tier[0] if tier else '', tier[1] if tier else '', '', '', '', '', '', '', key])
    for mk, (d, se, st, share) in sorted((res5.get('modes') or {}).items()):
        rows.append(['档位', mk, '', '', '', '', '', fmtn(d), fmtn(se), fmtn(d), fmtn(se), st, st, now,
                     '', '', '', '', '', '', '', '', key])
    return rows, pq, pq_label


def main():
    try:
        sys.stdout.reconfigure(encoding='utf-8')
    except Exception:
        pass
    # 第一次运行时这些文件还不存在（悬浮窗刚开始记录）：不是错误，等有数据了再算
    for need in ('用量明细_v2.csv', '额度快照.csv'):
        if not os.path.exists(os.path.join(BASE, need)):
            print('还没有数据：缺少 %s（悬浮窗运行一段时间后会自动生成）' % need)
            return 0
    tags = load_tags(os.path.join(BASE, '用量标注.csv'))
    tl = load_timeline(TIMELINE)
    key_of = make_key_of(tl)
    msgs_k, mode_k = load_messages(os.path.join(BASE, '用量明细_v2.csv'), tags, key_of)
    snaps = load_snapshots(os.path.join(BASE, '额度快照.csv'))
    if not snaps or not msgs_k:
        print('没有快照或用量记录')
        return 1
    snaps_k = {}
    for sn in snaps:
        snaps_k.setdefault(key_of(sn['t']), []).append(sn)
    cur_key = key_of(float('inf')) if tl else ''
    keys = sorted(set(snaps_k) & set(msgs_k), key=lambda k: snaps_k[k][-1]['t'])
    lines = ['额度换算报告（%s 生成）' % datetime.datetime.now().strftime('%Y-%m-%d %H:%M'),
             '快照 %d 条（%s ~ %s）' % (len(snaps), fmt_time(snaps[0]['t']), fmt_time(snaps[-1]['t'])),
             '长上下文加价档：%s' % ('已按官方价格建模（haiku-5-5 单次上下文超过 10 万 token，整条 ×5）' if USE_TIER else '未建模（--no-tier）')]
    if tl:
        lines.append('账号时段 %d 条，口径（账号:计划）：%s；当前口径 %s' % (len(tl), '、'.join(sorted(set('%s:%s' % (e[1], e[2]) for e in tl))), cur_key))
        for k in keys:
            lines.append('  口径 %-14s 快照 %4d 条，用量模型：%s' % (k, len(snaps_k[k]), '、'.join(sorted(msgs_k[k].keys()))))
    if tags:
        lines.append('档位标注 %d 条（用量标注.csv）' % len(tags))

    results = {}
    for k in keys:
        kl = ['', '#' * 70, '口径 %s' % (k or '（不分账号）')]
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
        targets.append(cur_key)          # 当前口径写在最后（悬浮窗找不到自己的口径时用最后一组）

    now = datetime.datetime.now().strftime('%Y-%m-%d %H:%M:%S')
    all_rows = []
    own = {}
    lines.append('')
    lines.append('=' * 70)
    lines.append('输出口径：')
    for k in targets:
        res = results.get(k)
        if res and res[0] is not None:
            rows, pq, pql = rows_for(k, res[0], res[1], now)
            own[k] = (res[0], res[1], pq, pql)
            lines.append('  %-14s 实测（拟合点 %d）' % (k, res[0]['f']['n']))
            all_rows.extend(rows)
            continue
        # 没有自己的结果：按套餐倍率从数据最多的口径推算
        cur_plan = plan_of(k)
        refs = [(r[0]['f']['n'], rk) for rk, r in results.items() if r[0] is not None and rk != k and plan_of(rk) in PLAN_MULT]
        if cur_plan in PLAN_MULT and refs:
            refs.sort(reverse=True)
            rk = refs[0][1]
            ratio = PLAN_MULT[plan_of(rk)] / PLAN_MULT[cur_plan]
            d5, dw = scale_result(results[rk][0], results[rk][1], ratio)
            note = '按套餐倍率推算自 %s（×%.3g）' % (rk, ratio)
            rows, _, _ = rows_for(k, d5, dw, now, note)
            lines.append('  %-14s %s' % (k, note))
            all_rows.extend(rows)
        else:
            lines.append('  %-14s 数据还不够，也没法按套餐倍率推算（计划未设置或没有可参照的口径）' % k)
    lines.append('')
    lines.append('局限：额度百分点是整数，快照约每 6 分钟一次；没出现过的模型只能按官方价格推算；')
    lines.append('同一账号在别处（网页、其他电脑）用掉的额度没有记录，会让系数略偏大；套餐倍率只是官方标称值，实际可能有出入。')
    if not QUICK:
        print('\n'.join(lines))
        with io.open(OUT_REPORT, 'w', encoding='utf-8-sig', newline='') as fh:
            fh.write('\n'.join(lines) + '\n')
    if not all_rows:
        print('\n没有可用结果，不写 额度换算.csv。')
        return 1

    tmp = OUT_CSV + '.tmp'
    with io.open(tmp, 'w', encoding='utf-8-sig', newline='') as fh:
        w = csv.writer(fh)
        w.writerow(['类型', '名称', '输入价', '输出价', '写5m价', '写1h价', '读价', '5h每美元额度%', '5h误差',
                    '周每美元额度%', '周误差', '5h状态', '周状态', '更新时间',
                    '长上下文阈值', '长上下文倍率', '缓存读取缩放', '近期误差', '误差块数', '早期误差', '近期实际/预测', '误差口径', '账号键'])
        w.writerows(all_rows)
    os.replace(tmp, OUT_CSV)
    if cur_key in own:
        r5, rw, pq, pql = own[cur_key]
        append_history(now, cur_key, r5['f'], r5, rw, pq, pql)
    print('\n已写入 %s' % OUT_CSV)
    return 0


def append_history(now, ckey, f5, res5, resw, pq, pq_label):
    """把关键数字追加到 额度换算_历史.csv（拟合点数没变就不记），看系数和误差怎么随数据增多而收敛。"""
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
                w.writerow(['时间', '口径 拟合点(5h/周)', '5h每美元额度%', '5h误差', '缓存读取缩放', 'haiku相对价格', '周每美元额度%',
                            '近期误差', '误差块数', '误差口径', '实测模型数'])
            measured = len([r for r in res5['rows'] if r[3] == '实测'])
            w.writerow([now, key, '%.4f' % res5['K'], '%.4f' % res5['se'], '%g' % res5['rho'],
                        ('%.2f' % (haiku[0][1] / res5['K'])) if haiku else '', ('%.4f' % resw['K']) if resw else '',
                        ('%.4f' % pq['err']) if pq else '', pq['n'] if pq else '', pq_label if pq else '', measured])
    except (IOError, OSError, ValueError):
        pass


if __name__ == '__main__':
    sys.exit(main())

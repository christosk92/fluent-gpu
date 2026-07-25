"""Compare scroll CADENCE across paired capture bundles - normally a before/after set from
synthetic-scroll-capture.ps1.

Why this is separate from pack-feel-summary.ps1: the packager answers "what is wrong with this build", one
bundle at a time, and refuses to publish numbers a single capture cannot support. An A/B asks "did the
change move the metric", which is only meaningful when both arms ran the SAME input - something only the
synthetic harness can guarantee.

Two time-based metrics carry the result. Both are time-based rather than distance-based on purpose, so
that INTENDED acceleration (a fling decaying, a wheel chase easing, a direction reversal) cannot be
mistaken for jitter:

  content-move cadence     spacing of consecutive offset writes during continuous motion. This is
                           PRODUCER-side - when the engine wrote a new offset - and is NOT "on-screen
                           cadence"; not every written offset reaches a display refresh.
  presented sample-time    spacing of the sample instants of the frames that actually won a present.

The p05 FLOOR is the single most diagnostic number: sub-millisecond spacing means the loop was
free-running and emitting frames nobody could see.

JOIN QUALITY. Bundles carrying the `ack` column join EXACTLY - each latency row records which published
frame the render seam had acknowledged, so the winner of a present is looked up, not guessed. Older
bundles fall back to "the row before the one that observed a new present", which is an INFERENCE; those
results are labelled `proxy` and must not be reported as the actual presented frame.

Usage:
  python ops/diag/analyze-cadence.py <before-dir>[;<before-dir>...] <after-dir>[;<after-dir>...]
"""
import csv, collections, json, os, statistics as st, sys


def num(s):
    """Blank means MISSING, never 0.0. Several columns are legitimately signed and legitimately zero, so
    conflating the two silently invents data points (it produced a fake -67% observer cost once)."""
    s = s.strip()
    return float(s) if s else None


def intn(s):
    s = s.strip()
    return int(s) if s else None


def pct(v, p):
    if not v:
        return float('nan')
    s = sorted(v)
    return s[min(len(s) - 1, int(p / 100.0 * (len(s) - 1)))]


def load(path, force_proxy=False):
    lat, ow = [], collections.defaultdict(list)
    has_ack_col = False
    with open(os.path.join(path, 'scroll.csv'), newline='') as f:
        rd = csv.reader(f)
        header = next(rd)
        has_ack_col = 'ack' in header
        for r in rd:
            if len(r) < 14:
                continue
            state = int(r[13])
            phase, cold = state & 0xF, (state >> 6) & 1
            if r[2] == 'latency':
                lat.append(dict(
                    t=num(r[0]) or 0.0,
                    pub=int(r[3]) & 0xFFFFFFFF,
                    missed=int(r[5]) & 0xFFFF,
                    attested=(int(r[5]) >> 16) & 0xFFFF,
                    skew=num(r[9]),                 # None when the frame resampled no contact
                    interval=num(r[10]),            # None / 0 when no new present was observed
                    ack=(intn(r[14]) if len(r) > 14 else None),
                    phase=phase, cold=cold))
            elif r[2] == 'offsetWrite':
                ow[phase].append(num(r[0]) or 0.0)

    warm = [x for x in lat if not x['cold']]

    # Producer-side content-move cadence, moving phases only, during continuous motion. A >=20 ms gap is a
    # pause between gesture legs, not a stutter; averaging it in swamps the signal.
    wdt = []
    for ph in (2, 3, 4, 5):
        ts = ow.get(ph, [])
        wdt += [b - a for a, b in zip(ts, ts[1:]) if 0 < b - a < 20]

    # --- presented sample-time deltas -------------------------------------------------------------
    deltas, join = [], 'none'
    acked = [r for r in warm if r['ack']]
    if has_ack_col and acked and not force_proxy:
        join = 'exact'
        # Exact: group rows by the ack they observed. The frame that WON ack A is the row that published A.
        by_pub = {}
        for r in warm:
            by_pub.setdefault(r['pub'], r)
        seen, winners = set(), []
        for r in acked:
            a = r['ack']
            if a in seen:
                continue
            seen.add(a)
            w = by_pub.get(a)
            if w is not None:
                winners.append(w)
        winners.sort(key=lambda x: x['t'])
        for a, b in zip(winners, winners[1:]):
            d = b['t'] - a['t']
            if 0 < d < 50:
                deltas.append(d)
    else:
        join = 'proxy'
        last, prev_obs = None, None
        for i, r in enumerate(warm):
            if not r['interval'] or i == 0:
                continue
            w = warm[i - 1]
            if last is not None and r['t'] - prev_obs < 50:
                d = w['t'] - last['t']
                if 0 < d < 50:
                    deltas.append(d)
            last, prev_obs = w, r['t']

    iv = [r['interval'] for r in warm if r['interval'] and 1.0 < r['interval'] < 50.0]
    skew = [r['skew'] for r in warm if r['skew'] is not None]
    published = sum(1 for r in warm if r['pub'] != 0)
    presents = sum(1 for r in warm if r['interval'])
    span = (warm[-1]['t'] - warm[0]['t']) / 1000.0 if len(warm) > 2 else 0.0

    man = {}
    mp = os.path.join(path, 'manifest.json')
    if os.path.exists(mp):
        with open(mp, encoding='utf-8-sig') as f:
            man = json.load(f)

    return dict(
        wdt=wdt, deltas=deltas, iv=iv, skew=skew, join=join,
        ratio=published / presents if presents else float('nan'),
        fps=len(warm) / span if span else float('nan'),
        rows=len(warm), name=os.path.basename(path), man=man)


def row(label, A, B, fn, better='lower', fmt='{:7.2f}'):
    a, b = [fn(x) for x in A], [fn(x) for x in B]
    ma, mb = st.median(a), st.median(b)
    chg = (mb - ma) / abs(ma) * 100.0 if ma else float('nan')
    good = (chg < 0) if better == 'lower' else (chg > 0)
    print('  {:<38s} {:>24s} | {:>24s} | {:+8.1f}%  {}'.format(
        label, ' '.join(fmt.format(x) for x in a), ' '.join(fmt.format(x) for x in b),
        chg, 'BETTER' if good else 'worse'))


def check_provenance(A, B):
    """Refuse to present an A/B the bundles cannot support. Every one of these has actually happened."""
    problems, notes = [], []
    for side, S in (('before', A), ('after', B)):
        for r in S:
            m = r['man']
            if not m:
                problems.append('{}: {} has no manifest.json'.format(side, r['name']))
                continue
            if not m.get('identified', m.get('gitSha')):
                problems.append('{}: {} is UNIDENTIFIED (no source commit recorded)'.format(side, r['name']))
            if m.get('gitDirty'):
                notes.append('{}: {} was built from a DIRTY tree - not reproducible from its sha'.format(side, r['name']))
    ha = {r['man'].get('exeSha256') for r in A if r['man'].get('exeSha256')}
    hb = {r['man'].get('exeSha256') for r in B if r['man'].get('exeSha256')}
    if ha and hb and ha == hb:
        problems.append('both arms are the SAME BINARY (identical exe sha256) - there is nothing to compare')
    if len(ha) > 1:
        notes.append('before arm mixes {} distinct binaries'.format(len(ha)))
    if len(hb) > 1:
        notes.append('after arm mixes {} distinct binaries'.format(len(hb)))
    sa = {r['man'].get('gitSha') for r in A if r['man'].get('gitSha')}
    sb = {r['man'].get('gitSha') for r in B if r['man'].get('gitSha')}
    if sa and sb and sa == sb and not (ha and hb and ha != hb):
        notes.append('both arms report the same source commit - check that -SourceRoot resolved per-arm')
    joins = {r['join'] for r in A} | {r['join'] for r in B}
    if 'proxy' in joins:
        notes.append("at least one bundle predates the `ack` column: its presented-frame join is a PROXY "
                     "(row adjacency), not the actual presented frame")
    return problems, notes


def main():
    if len(sys.argv) != 3:
        print(__doc__)
        sys.exit(2)
    pa = [p for p in sys.argv[1].split(';') if p]
    pb = [p for p in sys.argv[2].split(';') if p]
    A, B = [load(p) for p in pa], [load(p) for p in pb]

    # Apples to apples: if ANY bundle lacks the ack column, EVERY arm falls back to the proxy join. Comparing an
    # exactly-joined arm against an inferred one would attribute the difference between two JOIN METHODS to the
    # change under test — a fabricated result rather than a weaker one.
    if any(r['join'] == 'proxy' for r in A + B) and any(r['join'] == 'exact' for r in A + B):
        A = [load(p, force_proxy=True) for p in pa]
        B = [load(p, force_proxy=True) for p in pb]
        for r in A + B:
            r['join'] = 'proxy (downgraded: the other arm has no ack column)'

    print('\nSCROLL CADENCE A/B   {} before run(s), {} after run(s)'.format(len(A), len(B)))
    problems, notes = check_provenance(A, B)
    for n in notes:
        print('  WARN  ' + n)
    for p in problems:
        print('  FAIL  ' + p)
    if problems:
        print('\n  Refusing to report a comparison these bundles cannot support.\n')
        sys.exit(1)
    print('  join: before={}  after={}'.format(
        '/'.join(sorted({r['join'] for r in A})), '/'.join(sorted({r['join'] for r in B}))))
    print('=' * 108)
    print('  {:<38s} {:>24s} | {:>24s} |'.format('metric (one column per run)', 'BEFORE', 'AFTER'))

    print('\n  CONTENT-MOVE CADENCE   producer-side: how evenly the engine wrote a new offset')
    row('  floor p05 (ms)  <- free-run tell', A, B, lambda r: pct(r['wdt'], 5), better='higher')
    row('  p50 (ms)', A, B, lambda r: pct(r['wdt'], 50), better='higher')
    row('  SPREAD p05-p95 (ms)', A, B, lambda r: pct(r['wdt'], 95) - pct(r['wdt'], 5))
    row('  stddev (ms)', A, B, lambda r: st.pstdev(r['wdt']))

    print('\n  PRESENTED SAMPLE-TIME DELTAS   what the eye integrates')
    row('  p05 (ms)', A, B, lambda r: pct(r['deltas'], 5), better='higher')
    row('  p95 (ms)', A, B, lambda r: pct(r['deltas'], 95))
    row('  SPREAD p05-p95 (ms)', A, B, lambda r: pct(r['deltas'], 95) - pct(r['deltas'], 5))

    print('\n  PRODUCTION vs PRESENT   the mechanism')
    row('  publishes per present (target 1.00)', A, B, lambda r: r['ratio'], fmt='{:7.3f}')
    row('  UI frames produced per second', A, B, lambda r: r['fps'])

    print('\n  PRESENT CADENCE   must NOT regress')
    row('  present interval p50 (ms)', A, B, lambda r: pct(r['iv'], 50), better='higher')
    row('  present interval spread p05-p95', A, B, lambda r: pct(r['iv'], 95) - pct(r['iv'], 5))

    # Skew: reported WITHIN run, never as "constant" off equal medians. Equal medians across runs are
    # consistent with a tight single mode AND with a bimodal distribution that has a large late tail -
    # which is exactly what the first phase-lock measurement turned out to have (72% locked, 16% one
    # refresh late). Modal concentration is the number that tells them apart.
    print('\n  CLOCK SAMPLING   a CONSTANT offset is invisible; a VARYING one is the stutter')
    for lbl, S in (('BEFORE', A), ('AFTER ', B)):
        for r in S:
            s = r['skew']
            if len(s) < 50:
                print('    {} {:<26s} (only {} scroll-sampled frames)'.format(lbl, r['name'][-26:], len(s)))
                continue
            med = pct(s, 50)
            near1 = 100.0 * sum(1 for x in s if abs(x - med) <= 1.0) / len(s)
            late = 100.0 * sum(1 for x in s if x - med > 6.0) / len(s)
            print('    {} p05={:7.2f} p50={:7.2f} p95={:7.2f} spread={:6.2f} | within 1ms of median {:5.1f}%'
                  ' | >6ms LATE {:5.1f}%  (n={})'.format(lbl, pct(s, 5), med, pct(s, 95),
                                                         pct(s, 95) - pct(s, 5), near1, late, len(s)))

    print('\n  warm latency rows: before {}  after {}'.format(
        [r['rows'] for r in A], [r['rows'] for r in B]))
    print('\n  Synthetic wheel input measures CADENCE only. It carries no feel verdict and does not\n'
          '  exercise the DirectManipulation touchpad path. See ops/diag/AGENT.md.\n')


if __name__ == '__main__':
    main()

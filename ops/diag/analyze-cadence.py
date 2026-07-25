"""Compare scroll CADENCE across paired capture bundles - normally a before/after set from
synthetic-scroll-capture.ps1.

Why this is a separate tool from pack-feel-summary.ps1: the packager answers "what is wrong with this
build", one bundle at a time, and deliberately refuses to publish numbers a single capture cannot support.
An A/B asks a different question - "did the change move the metric" - which is only meaningful when both
arms ran the SAME input, and only the synthetic harness can guarantee that.

The headline is not frame rate. It is how evenly the scroll POSITION advanced on screen, because that is
what the eye integrates. Two metrics carry it, and both are time-based rather than distance-based so that
INTENDED acceleration (a fling decelerating, a wheel chase easing, a direction reversal) does not
contaminate them:

  content-move cadence      spacing of consecutive offset writes during continuous motion
  presented sample-time     spacing of the sample instants of the frames that actually won a present

A loop producing faster than the display shows still presents on a metronomic beat, but WHICH frame wins
each vblank varies - so these spread out while the present interval looks perfect. That spread is the
stutter. The p05 FLOOR is the most diagnostic single number: sub-millisecond spacing means the loop was
free-running and emitting frames nobody could see.

Usage:
  python ops/diag/analyze-cadence.py <before-dir>[;<before-dir>...] <after-dir>[;<after-dir>...]

Per-run values are printed rather than only an average, because a single pair cannot distinguish a real
change from run-to-run variance.
"""
import csv, collections, os, statistics as st, sys


def num(s):
    s = s.strip()
    return float(s) if s else 0.0


def pct(v, p):
    if not v:
        return float('nan')
    s = sorted(v)
    return s[min(len(s) - 1, int(p / 100.0 * (len(s) - 1)))]


def load(path):
    """One capture bundle -> the cadence quantities. Reads scroll.csv only; the console is NOT consulted,
    because its [fps] stream can be truncated by a busy harness and a silently short console would quietly
    turn a ratio into a divide-by-almost-zero."""
    lat, ow = [], collections.defaultdict(list)
    with open(os.path.join(path, 'scroll.csv'), newline='') as f:
        rd = csv.reader(f)
        next(rd)
        for r in rd:
            if len(r) < 14:
                continue
            state = int(r[13])
            phase, cold = state & 0xF, (state >> 6) & 1
            if r[2] == 'latency':
                # i0 = publishSeq low 32 (0 == submit elided), f4 = present interval (>0 == this frame
                # observed a NEW present), f3 = clock-sample skew.
                lat.append((num(r[0]), int(r[3]) & 0xFFFFFFFF, num(r[10]), num(r[9]), phase, cold))
            elif r[2] == 'offsetWrite':
                ow[phase].append(num(r[6]))          # f0 = offset
                ow[(phase, 't')].append(num(r[0]))

    # Content-move cadence over the MOVING phases only, during continuous motion (a >=20 ms gap is a
    # pause between gesture legs, not a stutter, and averaging it in would swamp the signal).
    wdt = []
    for ph in (2, 3, 4, 5):
        ts = ow.get((ph, 't'), [])
        wdt += [b - a for a, b in zip(ts, ts[1:]) if 0 < b - a < 20]

    # Presented sample-time deltas. The frame that WON present N is the last one produced before the
    # frame that observed it; join forward, never on equality (DropOldest is last-writer-wins).
    warm = [x for x in lat if not x[5]]
    deltas, last, prev_obs = [], None, None
    for i, r in enumerate(warm):
        if r[2] <= 0 or i == 0:
            continue
        w = warm[i - 1]
        if last is not None and r[0] - prev_obs < 50:
            d = w[0] - last[0]
            if 0 < d < 50:
                deltas.append(d)
        last, prev_obs = w, r[0]

    published = sum(1 for r in warm if r[1] != 0)
    presents = sum(1 for r in warm if r[2] > 0)
    span = (warm[-1][0] - warm[0][0]) / 1000.0 if len(warm) > 2 else 0.0
    return dict(
        wdt=wdt, deltas=deltas,
        iv=[r[2] for r in warm if 1.0 < r[2] < 50.0],
        skew=[r[3] for r in warm],
        ratio=published / presents if presents else float('nan'),
        fps=len(warm) / span if span else float('nan'),
        rows=len(warm), name=os.path.basename(path))


def row(label, A, B, fn, better='lower', fmt='{:7.2f}'):
    a, b = [fn(x) for x in A], [fn(x) for x in B]
    ma, mb = st.median(a), st.median(b)
    chg = (mb - ma) / abs(ma) * 100.0 if ma else float('nan')
    good = (chg < 0) if better == 'lower' else (chg > 0)
    print('  {:<38s} {:>24s} | {:>24s} | {:+8.1f}%  {}'.format(
        label, ' '.join(fmt.format(x) for x in a), ' '.join(fmt.format(x) for x in b),
        chg, 'BETTER' if good else 'worse'))


def main():
    if len(sys.argv) != 3:
        print(__doc__)
        sys.exit(2)
    A = [load(p) for p in sys.argv[1].split(';') if p]
    B = [load(p) for p in sys.argv[2].split(';') if p]

    print('\nSCROLL CADENCE A/B   {} before run(s), {} after run(s)'.format(len(A), len(B)))
    print('=' * 108)
    print('  {:<38s} {:>24s} | {:>24s} |'.format('metric (one column per run)', 'BEFORE', 'AFTER'))

    print('\n  CONTENT-MOVE CADENCE   how evenly the offset actually advanced')
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

    # Skew is the one metric where "bigger" is not "worse", so it is reported without a verdict column.
    # The eye cannot see a CONSTANT offset between sampling the scroll position and showing it - that is
    # just latency, and a few ms of it is imperceptible. What it sees is the offset CHANGING frame to
    # frame. So the number to read here is the run-to-run and within-run consistency, not the magnitude:
    # a phase-locked loop should report the same value every run, at -(one refresh + ResampleLatencyMs).
    print('\n  CLOCK SAMPLING   a CONSTANT offset is invisible; a VARYING one is the stutter')
    for lbl, S in (('BEFORE', A), ('AFTER ', B)):
        v = [pct(r['skew'], 50) for r in S]
        print('    {} skew p50 per run: {}   run-to-run stddev {:.3f} ms'.format(
            lbl, ' '.join('{:7.2f}'.format(x) for x in v), st.pstdev(v)))

    print('\n  warm latency rows: before {}  after {}'.format(
        [r['rows'] for r in A], [r['rows'] for r in B]))
    print('\n  Synthetic wheel input measures CADENCE only. It carries no feel verdict and does not\n'
          '  exercise the DirectManipulation touchpad path. See ops/diag/AGENT.md.\n')


if __name__ == '__main__':
    main()

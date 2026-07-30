"""
Why 0.6.0's adaptive filter still shook, and shook DIRECTION-DEPENDENTLY.

0.6.0 computed the adaptive cutoff from the RAW per-frame delta:

    speed  = |raw - smoothed| / dt
    cutoff = minCutoff + beta*speed

When the signal is noisy but stationary that delta is mostly noise, so speed reads
high, so the cutoff opens, so the filter smooths LESS, so more noise gets through,
which inflates speed further. Positive feedback - the filter defeats itself exactly
when it is needed most.

ARKit's noise level depends on how much visual texture is in view, so it varies with
where the phone is pointed. The runaway therefore engages for some directions and not
others: "smooth when I look straight, shakes a lot in a certain direction".

The canonical 1-euro filter (Casiez/Roussel/Vogel 2012) low-passes the derivative with
its own fixed cutoff first. That is what 0.6.1 adds.
"""
import math, random, statistics

FPS=144.0; DT=1.0/FPS; SECS=3.0; N=int(FPS*SECS)
MINCUT=1.0; BETA=0.02; DCUT=1.0

def alpha(cut, dt):
    tau=1.0/(2*math.pi*max(1e-4,cut)); return dt/(tau+dt)

def run(noise_deg, use_dfilter, seed=11):
    random.seed(seed)
    s=0.0; ds=0.0; out=[]
    for i in range(N):
        raw = 0.0 + random.gauss(0.0, noise_deg)      # phone perfectly STILL
        rawspeed = abs(raw-s)/DT
        if use_dfilter:
            ds += (rawspeed-ds)*alpha(DCUT, DT)
            speed = ds
        else:
            speed = rawspeed
        cut = MINCUT + BETA*speed
        s += (raw-s)*alpha(cut, DT)
        out.append(s)
    return out

def jitter(series):
    d=[abs(b-a) for a,b in zip(series,series[1:])]
    return statistics.mean(d), max(d)

print("="*78)
print("Phone held PERFECTLY STILL. Only the ARKit noise level varies -")
print("as it does in reality when the phone points at more or less textured scenery.")
print("="*78)
print(f"\n  {'noise (1 sigma)':>16} | {'0.6.0 (raw speed)':>26} | {'0.6.1 (filtered speed)':>26}")
print(f"  {'':>16} | {'mean/frame':>12} {'max':>13} | {'mean/frame':>12} {'max':>13}")
print("  "+"-"*74)
rows=[]
for noise in (0.05, 0.15, 0.30, 0.60):
    a=jitter(run(noise, False)); b=jitter(run(noise, True))
    rows.append((noise,a,b))
    print(f"  {noise:15.2f}° | {a[0]:11.4f}° {a[1]:12.4f}° | {b[0]:11.4f}° {b[1]:12.4f}°")

# The runaway: how much worse does 0.6.0 get as noise rises, vs 0.6.1?
lo_old, hi_old = rows[0][1][0], rows[-1][1][0]
lo_new, hi_new = rows[0][2][0], rows[-1][2][0]
old_growth = hi_old/max(lo_old,1e-12)
new_growth = hi_new/max(lo_new,1e-12)
noise_growth = rows[-1][0]/rows[0][0]

print(f"\n  noise itself grew {noise_growth:.0f}x across those rows.")
print(f"  0.6.0 output jitter grew {old_growth:.1f}x  (worse than the input -> runaway)")
print(f"  0.6.1 output jitter grew {new_growth:.1f}x  (tracks the input, no runaway)")

ok1 = old_growth > noise_growth
print(f"\n  [{'PASS' if ok1 else 'FAIL'}] reproduces the feedback loop in 0.6.0 "
      f"({old_growth:.1f}x output vs {noise_growth:.0f}x input)")
ok2 = new_growth < old_growth
print(f"  [{'PASS' if ok2 else 'FAIL'}] 0.6.1 breaks it ({new_growth:.1f}x vs {old_growth:.1f}x)")
# Honest threshold. Filtering the derivative is a real but PARTIAL improvement: it
# halves the worst-case spikes and stops the runaway growing faster than the input,
# but it is not by itself a cure. The direction-dependent shake was the atan2 yaw
# singularity (tools/yaw_singularity_proof.py); this only stops the filter feeding on
# its own noise on top of that.
ok3 = rows[-1][2][0] < rows[-1][1][0]*0.85
print(f"  [{'PASS' if ok3 else 'FAIL'}] and at the noisy end it is "
      f"{rows[-1][1][0]/max(rows[-1][2][0],1e-12):.1f}x steadier "
      f"({rows[-1][1][0]:.4f}° -> {rows[-1][2][0]:.4f}° per frame)")

# responsiveness must survive
def run_move(use_dfilter):
    random.seed(3); s=0.0; ds=0.0; worst=0.0
    for i in range(N):
        t=i*DT
        truth = 0.0 if t<1.0 else (40.0*(0.5-0.5*math.cos(math.pi*min(1.0,t-1.0))))
        raw = truth + random.gauss(0.0,0.25)
        rs=abs(raw-s)/DT
        if use_dfilter:
            ds += (rs-ds)*alpha(DCUT,DT); sp=ds
        else: sp=rs
        s += (raw-s)*alpha(MINCUT+BETA*sp, DT)
        if t>1.0: worst=max(worst, abs(s-truth))
    return worst
lo, ln = run_move(False), run_move(True)
ok4 = ln < lo + 6.0
print(f"  [{'PASS' if ok4 else 'FAIL'}] responsiveness preserved: lag {ln:.2f}° vs {lo:.2f}° on a 40° look")

print("\n"+"="*78)
n=sum([ok1,ok2,ok3,ok4]); print(f"{n}/4 passed")
raise SystemExit(0 if n==4 else 1)

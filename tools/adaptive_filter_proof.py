"""
Adaptive (1-euro) filter vs the fixed low-pass, on a realistic noisy ARKit stream.

Why the previous fixes could not have solved this: they addressed how the offset was
COUPLED to the camera's frame. But if the offset signal itself carries high-frequency
noise, decoupling it perfectly still delivers that noise to the camera. Parked, a few
tenths of a degree of noise is nearly invisible; driving, it swings the whole scene.

Note on why this went unmeasured for so long: appliedOffsetEuler was logged with F0 -
WHOLE DEGREES - so sub-degree jitter was quantised away entirely, and transformPos/Fwd
used F2. Every earlier measurement in this project sat at or below that noise floor.
0.6.0 raises them to F2/F4.
"""
import math, random, statistics
random.seed(7)

FPS = 144.0
DT = 1.0/FPS
SECS = 4.0
N = int(FPS*SECS)

NOISE_DEG = 0.25      # per-sample ARKit rotational noise (1 sigma), realistic for a handheld phone

def truth(t):
    """Deliberate head movement: still, then a smooth 40 deg look, then still."""
    if t < 1.0:  return 0.0
    if t < 2.0:  return 40.0 * (0.5 - 0.5*math.cos(math.pi*(t-1.0)))   # smooth ease
    return 40.0

def fixed_alpha(smoothing, dt):
    return 1.0 - (1.0-smoothing)**(dt*60.0)

def one_euro_alpha(cutoff, dt):
    tau = 1.0/(2*math.pi*max(1e-4,cutoff))
    return dt/(tau+dt)

def run(mode, smoothing=0.83, mincut=1.0, beta=0.02):
    out=[]; s=truth(0.0)
    for i in range(N):
        t=i*DT
        raw = truth(t) + random.gauss(0.0, NOISE_DEG)
        if mode=="fixed":
            a = fixed_alpha(smoothing, DT)
        else:
            speed = abs(raw - s)/DT                     # deg/s, vs previous smoothed
            a = one_euro_alpha(mincut + beta*speed, DT)
        s += (raw - s)*a
        out.append(s)
    return out

def jitter_at_rest(series):
    """Frame-to-frame wobble over the final still second - what you see when holding still."""
    seg = series[int(FPS*3.0):]
    d=[abs(b-a) for a,b in zip(seg,seg[1:])]
    return statistics.mean(d), max(d)

def lag(series):
    """How far behind truth during the deliberate move."""
    return max(abs(series[i]-truth(i*DT)) for i in range(int(FPS*1.0), int(FPS*2.0)))

fixed = run("fixed")
euro  = run("euro")

fm, fx = jitter_at_rest(fixed)
em, ex = jitter_at_rest(euro)
fl, el = lag(fixed), lag(euro)

print("="*76)
print(f"Noisy ARKit stream ({NOISE_DEG} deg per-sample noise), 144 fps")
print("="*76)
print(f"\n  JITTER while holding still (frame-to-frame):")
print(f"    fixed low-pass (RotationSmoothing 0.83): mean {fm:.4f} deg  max {fx:.4f} deg")
print(f"    adaptive 1-euro (0.6.0)                : mean {em:.4f} deg  max {ex:.4f} deg")
print(f"\n  LAG during a deliberate 40 deg look:")
print(f"    fixed low-pass : {fl:.2f} deg")
print(f"    adaptive 1-euro: {el:.2f} deg")

ok1 = em < fm/4
print(f"\n  [{'PASS' if ok1 else 'FAIL'}] adaptive cuts resting jitter {fm/max(em,1e-9):.0f}x "
      f"({fm:.4f} -> {em:.4f} deg per frame)")
ok2 = el < fl + 3.0
print(f"  [{'PASS' if ok2 else 'FAIL'}] without meaningful added lag "
      f"({el:.2f} deg vs {fl:.2f} deg)")

# And the fixed filter cannot win by turning it down: match the jitter, measure the lag.
best=None
for sm in [x/200 for x in range(1,201)]:
    r=run("fixed", smoothing=sm)
    m,_=jitter_at_rest(r)
    if m<=em:
        best=(sm, lag(r), m); break
if best:
    sm,lg,m = best
    print(f"\n  To match that steadiness a FIXED filter needs smoothing={sm:.3f},")
    print(f"  which costs {lg:.1f} deg of lag (adaptive: {el:.2f} deg) -> {lg/max(el,1e-9):.0f}x worse.")
    ok3 = lg > el*2
else:
    print("\n  No fixed setting matched the adaptive filter's steadiness at all.")
    ok3 = True
print(f"  [{'PASS' if ok3 else 'FAIL'}] the trade-off a fixed filter cannot escape is what 1-euro solves")

print()
print("="*76)
n=sum([ok1,ok2,ok3]); print(f"{n}/3 passed")
raise SystemExit(0 if n==3 else 1)

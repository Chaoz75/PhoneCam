"""
The direction-dependent shake: atan2 yaw extraction near vertical.

yaw = Atan2(fwd.x, fwd.z) is singular as fwd approaches vertical - both components
collapse to zero, so the result is decided by noise alone. 0.6.1 weights the yaw
update by horizontalLen, which IS the confidence: it goes to zero exactly where yaw
stops being defined.
"""
import math, random, statistics

NOISE = 0.004          # per-component noise on a unit forward vector (~0.25 deg)
SAMPLES = 600
FLOOR = 1.0            # use horizontalLen directly: noise ~ 1/hl, so weighting by hl cancels it

def norm(a):
    a %= 360.0
    if a > 180: a -= 360
    if a < -180: a += 360
    return a

def run(pitch_deg, guarded, seed=1):
    random.seed(seed)
    p = math.radians(pitch_deg)
    base = (0.0, -math.sin(p), math.cos(p))
    last = None; out = []
    for _ in range(SAMPLES):
        f = [c + random.gauss(0, NOISE) for c in base]
        hl = math.sqrt(f[0]**2 + f[2]**2)
        if not guarded:
            y = math.degrees(math.atan2(f[0], f[2]))
        else:
            conf = max(0.0, min(1.0, hl/FLOOR))
            if conf > 0.0:
                meas = math.degrees(math.atan2(f[0], f[2]))
                y = last + norm(meas-last)*conf if last is not None else meas
            else:
                y = last if last is not None else 0.0
        last = y
        out.append(y)
    return out

def jitter(series):
    d = [abs(norm(b-a)) for a,b in zip(series, series[1:])]
    return statistics.mean(d), sorted(d)[int(len(d)*0.99)]

print("="*76)
print("Identical input noise at every pitch. Yaw jitter, before vs after.")
print("="*76)
print(f"\n  {'pitch':>7} | {'horizLen':>9} | {'BEFORE mean/p99':>22} | {'AFTER mean/p99':>22}")
print("  "+"-"*70)
worst_before=0; worst_after=0
for pitch in (0,30,60,75,85,88,89):
    hl = math.cos(math.radians(pitch))
    bm,bp = jitter(run(pitch, False))
    am,ap = jitter(run(pitch, True))
    worst_before=max(worst_before,bm); worst_after=max(worst_after,am)
    print(f"  {pitch:6}° | {hl:9.4f} | {bm:9.3f}° / {bp:8.3f}° | {am:9.3f}° / {ap:8.3f}°")

flat_b = jitter(run(0,False))[0]; flat_a = jitter(run(0,True))[0]
print(f"\n  worst-direction / looking-straight ratio:")
print(f"    BEFORE: {worst_before/max(flat_b,1e-9):7.1f}x   <- shakes far more in some directions")
print(f"    AFTER : {worst_after/max(flat_a,1e-9):7.1f}x")

ok1 = worst_before/max(flat_b,1e-9) > 10
print(f"\n  [{'PASS' if ok1 else 'FAIL'}] reproduces the direction dependence "
      f"({worst_before/max(flat_b,1e-9):.0f}x worse in the bad direction)")
ok2 = worst_after/max(flat_a,1e-9) < 3
print(f"  [{'PASS' if ok2 else 'FAIL'}] fix makes jitter direction-independent "
      f"({worst_after/max(flat_a,1e-9):.1f}x)")
ok3 = worst_after < worst_before/5
print(f"  [{'PASS' if ok3 else 'FAIL'}] worst-case jitter cut {worst_before/max(worst_after,1e-9):.0f}x "
      f"({worst_before:.2f}° -> {worst_after:.2f}° per sample)")

# behaviour away from the singularity must be untouched
random.seed(9)
a=run(20,False,seed=9); b=run(20,True,seed=9)
diff=max(abs(norm(x-y)) for x,y in zip(a,b))
ok4 = diff < 0.5
print(f"  [{'PASS' if ok4 else 'FAIL'}] unchanged away from vertical: max difference {diff:.4f}° at 20° pitch")

print("\n"+"="*76)
n=sum([ok1,ok2,ok3,ok4]); print(f"{n}/4 passed")
raise SystemExit(0 if n==4 else 1)

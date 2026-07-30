"""
Why the camera still juddered when the car moved, after 0.5.4/0.5.5.

0.5.4 moved the offset frame onto the CAR's transform. The car is physics-driven, so
its transform advances at the FIXED timestep. Sampling car.forward once per rendered
frame therefore reads a STAIRCASE whenever render rate > physics rate: the heading
holds for a frame or two, then jumps. The tracked offset is rotated by that frame, so
the offset direction inherits the steps and the camera advances unevenly.

Car-motion-dependent by construction: parked, the heading is constant, nothing steps.

Consistent with the live 0.6.1 capture: per-frame camera step stdev was 34% of its
mean, and step-size autocorrelation peaked at lag 2 (+0.325) ABOVE lag 1 (+0.141) -
a two-frame beat, not smooth motion.
"""
import math, statistics

RENDER_HZ, PHYS_HZ, SECS = 144.0, 50.0, 4.0
YAW_RATE = 60.0          # car turning, deg/s
OFFSET = (0.12, 0.0, 0.0)  # phone perfectly still: 12 cm lean
TAU = 0.050              # 0.6.2 smoothing time constant (swept: knee of the curve)

def rot_y(v, deg):
    r = math.radians(deg); c, s = math.cos(r), math.sin(r)
    return (v[0]*c + v[2]*s, v[1], -v[0]*s + v[2]*c)
def vl(a): return math.sqrt(sum(c*c for c in a))
def vs(a,b): return tuple(x-y for x,y in zip(a,b))

def run(smoothed):
    dt = 1.0/RENDER_HZ
    fy = None; out=[]
    for i in range(int(RENDER_HZ*SECS)):
        t = i*dt
        # the car's transform only advances on physics steps -> staircase
        phys_t = math.floor(t*PHYS_HZ)/PHYS_HZ
        raw_yaw = YAW_RATE*phys_t
        if smoothed:
            if fy is None: fy = raw_yaw
            else:
                rate = 1.0-math.exp(-dt/TAU)
                d = (raw_yaw-fy+180)%360-180
                fy += d*rate
            yaw = fy
        else:
            yaw = raw_yaw
        out.append(rot_y(OFFSET, yaw))     # the offset, in world space
    return out

def stats(pts):
    steps=[vl(vs(b,a)) for a,b in zip(pts,pts[1:])]
    m=statistics.mean(steps); sd=statistics.pstdev(steps)
    dev=[x-m for x in steps]; den=sum(d*d for d in dev) or 1e-12
    ac=[sum(dev[i]*dev[i+l] for i in range(len(dev)-l))/den for l in (1,2,3)]
    zeros=sum(1 for s in steps if s < m*0.05)     # frames where the offset didn't move at all
    return m, sd, ac, zeros, len(steps)

print("="*76)
print(f"Phone perfectly still. Car turning {YAW_RATE:.0f} deg/s.")
print(f"Render {RENDER_HZ:.0f} Hz, physics {PHYS_HZ:.0f} Hz "
      f"({RENDER_HZ/PHYS_HZ:.2f} render frames per physics step)")
print("="*76)

for lab, sm in (("RAW car.forward (0.5.4-0.6.1)", False), ("smoothed heading (0.6.2)", True)):
    m, sd, ac, z, n = stats(run(sm))
    print(f"\n  {lab}")
    print(f"    offset step: mean {m*1000:.3f} mm   stdev {sd*1000:.3f} mm   "
          f"stdev/mean = {sd/max(m,1e-12):.1%}")
    print(f"    frames where the offset did not move at all: {z}/{n} ({100*z/n:.0f}%)")
    print(f"    autocorrelation lag1/lag2/lag3: {ac[0]:+.3f} / {ac[1]:+.3f} / {ac[2]:+.3f}")
    if not sm: raw=(m,sd,ac,z,n)
    else: new=(m,sd,ac,z,n)

ok1 = raw[1]/max(raw[0],1e-12) > 0.5
print(f"\n  [{'PASS' if ok1 else 'FAIL'}] staircase reproduced: step size varies by "
      f"{raw[1]/max(raw[0],1e-12):.0%} of the mean, with {100*raw[3]/raw[4]:.0f}% of frames frozen")
# Threshold set from the sweep, not wished for: 0 ms leaves 137%, 25 ms 23.5%, 50 ms
# 14.5%, and it flattens after that while heading lag keeps growing. 14.5% of a
# 0.87 mm step is 0.13 mm - negligible in absolute terms.
ok2 = new[1]/max(new[0],1e-12) < 0.20
print(f"  [{'PASS' if ok2 else 'FAIL'}] smoothing removes it: variation down to "
      f"{new[1]/max(new[0],1e-12):.2%}, frozen frames {100*new[3]/new[4]:.0f}%")
ok3 = abs(new[0]-raw[0])/max(raw[0],1e-12) < 0.05
print(f"  [{'PASS' if ok3 else 'FAIL'}] and the offset still tracks the car "
      f"(mean step {new[0]*1000:.3f} mm vs {raw[0]*1000:.3f} mm - no drift, just even)")

print("\n"+"="*76)
n=sum([ok1,ok2,ok3]); print(f"{n}/3 passed")
raise SystemExit(0 if n==3 else 1)

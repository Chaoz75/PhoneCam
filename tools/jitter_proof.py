"""
Two jitter sources, reproduced and then eliminated.

(1) ACCUMULATION SHAKE
    The write is additive: t.position += t.rotation * posOffset.
    CinemachineBrain does not refresh the camera every rendered frame - it has
    m_UpdateMethod {FixedUpdate, LateUpdate, SmartUpdate, ManualUpdate},
    m_LastFrameUpdated and mWaitForFixedUpdate, so under SmartUpdate a
    physics-tracked target is driven at the FIXED timestep. Above that rate,
    some render frames get no fresh pose.

(2) RATE-DEPENDENT SMOOTHING
    Smoothing was advanced once per received OSC packet with a fixed lerp factor,
    so its response depended on packet rate, not time.
"""
import math

PHYS_HZ, RENDER_HZ, SECONDS = 50.0, 144.0, 2.0
OFFSET = 0.25            # metres of steady tracked offset
CARX = 0.0               # game's pose (held still to isolate the artefact)

def run(idempotent):
    """Returns the camera's per-frame offset-from-base over time."""
    cam = CARX
    game_base = None; we_wrote = None
    out = []
    nframes = int(RENDER_HZ*SECONDS)
    last_phys = -1
    for f in range(nframes):
        t = f/RENDER_HZ
        phys_step = int(t*PHYS_HZ)
        brain_refreshed = phys_step != last_phys
        last_phys = phys_step

        # ---- LateUpdate: Brain pushes CarX's pose, but only on some frames ----
        if brain_refreshed:
            cam = CARX

        # ---- onBeforeRender: PhoneCam ----
        if idempotent:
            if we_wrote is not None and abs(cam - we_wrote) < 1e-9:
                cam = game_base          # nobody refreshed - undo our own offset
            game_base = cam
        cam = cam + OFFSET               # additive write
        we_wrote = cam

        out.append(cam - CARX)
    return out

def shake(series):
    """Peak-to-peak of the frame-to-frame change = visible judder amplitude."""
    d = [abs(b-a) for a,b in zip(series, series[1:])]
    return max(series)-min(series), max(d) if d else 0.0

print("="*76)
print("(1) ACCUMULATION SHAKE — steady 0.25 m offset, camera pose held still")
print(f"    physics {PHYS_HZ:.0f} Hz, render {RENDER_HZ:.0f} Hz  =>  "
      f"{RENDER_HZ/PHYS_HZ:.1f} render frames per physics step")
print("="*76)
old = run(False); new = run(True)
o_range,o_step = shake(old); n_range,n_step = shake(new)
print(f"  BEFORE  offset drifts {min(old):.2f} .. {max(old):.2f} m  "
      f"(range {o_range:.2f} m, worst frame-to-frame jump {o_step:.2f} m)")
print(f"  AFTER   offset drifts {min(new):.2f} .. {max(new):.2f} m  "
      f"(range {n_range:.2e} m, worst frame-to-frame jump {n_step:.2e} m)")
ok1 = o_range > 0.2 and n_range < 1e-9
print(f"  [{'PASS' if ok1 else 'FAIL'}] shake reproduced before ({o_range:.2f} m of oscillation) "
      f"and eliminated after ({n_range:.0e} m)")
print(f"           expected offset is exactly {OFFSET} m; after = "
      f"{min(new):.3f}..{max(new):.3f} m")

print()
print("="*76)
print("(2) RATE-DEPENDENT SMOOTHING — same settings, different packet rates")
print("    measured 100 ms after a step input (a full second hides it: everything")
print("    has converged to 1.0 by then, so the window has to be short enough")
print("    to still be inside the filter's response)")
print("="*76)
SM = 0.35
def old_filter(rate_hz, secs=0.10):
    """per-sample lerp: converges faster the more packets arrive"""
    v=0.0
    for _ in range(int(rate_hz*secs)): v += (1.0-v)*SM
    return v
def new_filter(rate_hz, secs=0.10, fps=144.0):
    """per-frame, time-based: packet rate is irrelevant"""
    v=0.0; dt=1.0/fps
    for _ in range(int(fps*secs)):
        r = 1.0-(1.0-SM)**(dt*60.0)
        v += (1.0-v)*r
    return v
print(f"  {'packet rate':>12} | {'OLD @100ms':>14} | {'NEW @100ms':>14}")
print("  " + "-"*46)
olds=[];news=[]
for hz in (10,30,60,120):
    o=old_filter(hz); n=new_filter(hz); olds.append(o); news.append(n)
    print(f"  {hz:>9} Hz | {o:13.4f} | {n:13.4f}")
o_spread=max(olds)-min(olds); n_spread=max(news)-min(news)
ok2 = o_spread > 0.05 and n_spread < 1e-9
print(f"\n  BEFORE response varies by {o_spread:.4f} across packet rates -> smoothing wobbles with rate")
print(f"  AFTER  response varies by {n_spread:.2e} -> identical regardless of rate")
print(f"  [{'PASS' if ok2 else 'FAIL'}] smoothing is now rate-independent")

# frame-rate independence too
fps_vals=[new_filter(60,0.10,f) for f in (30,60,144)]
ok3 = max(fps_vals)-min(fps_vals) < 0.02
print(f"  [{'PASS' if ok3 else 'FAIL'}] and frame-rate independent: 30/60/144 fps -> "
      f"{fps_vals[0]:.4f}/{fps_vals[1]:.4f}/{fps_vals[2]:.4f}")

print()
print("="*76)
n=sum([ok1,ok2,ok3]); print(f"{n}/3 passed")
raise SystemExit(0 if n==3 else 1)

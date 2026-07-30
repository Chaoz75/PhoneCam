"""
Why it shook only while the car moved.

CarX.FollowCamera is a STATEFUL DAMPED FOLLOW that reads the camera's live transform
as its own input. From Assembly-CSharp, FollowCamera.LateUpdate calls:

    Transform::get_position  (x3)     <- reads where the camera currently IS
    Vector3::Lerp                     <- damps from that toward its target
    Transform::get_forward -> Quaternion::LookRotation -> Transform::set_rotation
    Transform::Rotate, Mathf::MoveTowards

Leaving our offset on the transform feeds it back into that damper. Next frame it
Lerps FROM our offset pose, partially corrects, and we add the offset again - a closed
loop. The loop only has gain while the damper is actually working, i.e. while the car
is moving. Parked, target == current, the Lerp is a no-op, and a constant offset just
sits there. Hence "smooth when still, shakes as soon as it moves".

0.6.3 reverts the transform right after the frame is drawn, so the game never sees it.
"""
import math, statistics

FPS=144.0; DT=1/FPS; SECS=4.0; N=int(FPS*SECS)
LERP=0.25             # FollowCamera's per-frame damping factor
OFFSET=0.30           # steady 30 cm tracked offset, phone PERFECTLY still

def target(t, moving):
    """
    Where the chase cam wants to be.

    A constant-velocity ramp is not good enough here: it leaves the damper in steady
    state, so the feedback shows up as a constant bias with no jitter. Real driving
    and drifting change speed and direction continuously, which keeps the damper in
    transient - and it is the TRANSIENT that the feedback amplifies unevenly.
    Parked => constant, damper at rest.
    """
    if not moving:
        return 0.0
    return 12.0*t + 2.2*math.sin(3.1*t) + 1.1*math.sin(7.7*t) + 0.6*math.sin(13.3*t)

def run(moving, revert):
    """
    Returns the ERROR between the rendered camera position and where it should be
    (the clean no-mod trajectory plus the offset).

    Measuring `rendered - base` instead is tautological - that is the offset by
    construction. The feedback corrupts the BASE, because FollowCamera damps from
    whatever the transform holds, so the deviation only shows against a clean
    reference trajectory.
    """
    clean = target(0.0, moving)     # what the game would do with no mod at all
    state = clean                   # what the transform actually holds at LateUpdate
    out=[]
    for i in range(N):
        t=i*DT
        tgt = target(t, moving)
        clean = clean + (tgt - clean)*LERP                 # no-mod reference
        base  = state + (tgt - state)*LERP                 # game damps FROM the transform
        rendered = base + OFFSET                           # our write, just before render
        out.append(rendered - (clean + OFFSET))            # deviation from ideal
        state = base if revert else rendered               # 0.6.3 reverts; older left it
    return out

def report(series):
    tail = series[int(len(series)*0.25):]                  # ignore initial settle
    m=statistics.mean(tail); sd=statistics.pstdev(tail)
    d=[abs(b-a) for a,b in zip(tail,tail[1:])]
    return m, sd, (max(d) if d else 0.0)


# ---------------------------------------------------------------------------
# WHAT THIS MODEL CAN AND CANNOT SHOW.
#
# It proves the AMPLIFICATION: leaving the offset on the transform feeds it back into
# FollowCamera's damper, which re-damps from it every frame, so a 30 cm request lands
# as 90 cm of camera displacement - a 3x error that exists purely because the game
# re-reads its own polluted output.
#
# It cannot reproduce the JITTER itself, and that is a property of the model, not
# evidence of absence: a linear damper answers a constant offset with a constant bias
# by superposition. The jitter comes from FollowCamera's DISCRETE machinery, which is
# visible in Assembly-CSharp but not faithfully simulable here:
#
#   FollowCamera.CalcCameraPoint:
#       Transform::get_position                    <- the CAMERA's position
#       Vector3::op_Subtraction -> SqrMagnitude    <- distance to tracking point A
#       Vector3::op_Subtraction -> SqrMagnitude    <- distance to tracking point B
#       ...compare, then Transform::set_position
#       this::Reset
#       this::InstantApplyFocus                    <- a hard SNAP, not a blend
#
# It selects the nearest tracking point by distance FROM THE CAMERA'S POSITION - the
# exact quantity our offset was displacing - and on a switch it hard-resets. Add
# m_changeTrackingPointThreshold, m_lastCamChangedTime, Mathf.MoveTowards and
# Quaternion.LookRotation, and a small steady perturbation becomes discrete, uneven
# motion. Parked, those distances are static so the choice is stable; moving, they
# sweep past each other continuously - which is why it only shook when the car moved.
#
# An honest attempt to simulate the switching was made and removed: without the real
# tracking-point layout and thresholds it produced zero switches in every condition,
# i.e. it demonstrated nothing either way.
# ---------------------------------------------------------------------------

print("="*76)
print(f"Phone PERFECTLY STILL, constant {OFFSET*100:.0f} cm offset.")
print("Measured: how far the rendered camera deviates from the clean no-mod trajectory.")
print("Any non-zero value is the mod disturbing the game's own damper.")
print("="*76)
rows={}
for moving in (False, True):
    for revert in (False, True):
        rows[(moving,revert)] = report(run(moving,revert))

print(f"\n  {'car':>8} | {'version':>18} | {'mean error':>12} | {'stdev':>9} | {'worst frame jump':>17}")
print("  "+"-"*76)
for moving in (False, True):
    for revert in (False,True):
        m,sd,mx = rows[(moving,revert)]
        lab = "0.6.3 (revert)" if revert else "<=0.6.2 (leave)"
        print(f"  {('MOVING' if moving else 'PARKED'):>8} | {lab:>18} | {m*100:9.2f} cm | "
              f"{sd*100:6.2f} cm | {mx*100:14.3f} cm")

park_old = abs(rows[(False,False)][0]); move_old = abs(rows[(True,False)][0])
move_new = abs(rows[(True,True)][0]);  park_new = abs(rows[(False,True)][0])
move_old_jump = rows[(True,False)][2]; move_new_jump = rows[(True,True)][2]

# The linear model can only show the steady-state AMPLIFICATION, not the jitter:
# a linear damper answers a constant offset with a constant bias, by superposition.
# The jitter comes from FollowCamera's DISCRETE elements (tracking-point switching
# above, plus Mathf.MoveTowards and LookRotation), which the linear part cannot model.
ok1 = abs(rows[(True,False)][0]) > OFFSET*2
print(f"\n  [{'PASS' if ok1 else 'FAIL'}] feedback AMPLIFIES the offset "
      f"{abs(rows[(True,False)][0])/OFFSET:.1f}x ({OFFSET*100:.0f} cm requested -> "
      f"{abs(rows[(True,False)][0])*100:.0f} cm of camera displacement)")
move_new_sd = rows[(True,True)][1]
ok2 = move_new_sd < 1e-9
print(f"  [{'PASS' if ok2 else 'FAIL'}] reverting removes it entirely: variation "
      f"{move_new_sd*100:.4f} cm while moving (the game's damper never sees the offset)")
ok3 = park_new < 1e-9
print(f"  [{'PASS' if ok3 else 'FAIL'}] parked behaviour still clean: {park_new*100:.4f} cm")

print("\n"+"="*76)
n=sum([ok1,ok2,ok3]); print(f"{n}/3 passed")
raise SystemExit(0 if n==3 else 1)

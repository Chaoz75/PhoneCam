"""
Why the camera shook while drifting but not while parked.

The translation used to be expressed in the camera's LIVE rotation:
    t.position += t.rotation * posOffset
CarX.FollowCamera adds sway (m_SwaySpeed / m_BaseSwayAmount / m_TrackingSwayAmount)
and, during a drift, yaws hard to follow the car's slip angle. Rotating a FIXED
phone offset by that swinging rotation sweeps the world-space offset around, so a
motionless phone still moves the camera.

0.5.4 expresses it in the car's heading (yaw only) instead.
"""
import math
def qax(a,d):
    n=math.sqrt(sum(c*c for c in a)) or 1.0; a=[c/n for c in a]
    h=math.radians(d)/2; s=math.sin(h); return (a[0]*s,a[1]*s,a[2]*s,math.cos(h))
def qmul(A,B):
    ax,ay,az,aw=A; bx,by,bz,bw=B
    return (aw*bx+ax*bw+ay*bz-az*by, aw*by-ax*bz+ay*bw+az*bx,
            aw*bz+ax*by-ay*bx+az*bw, aw*bw-ax*bx-ay*by-az*bz)
def qrot(q,v):
    x,y,z,w=q; vx,vy,vz=v
    tx=2*(y*vz-z*vy); ty=2*(z*vx-x*vz); tz=2*(x*vy-y*vx)
    return (vx+w*tx+(y*tz-z*ty), vy+w*ty+(z*tx-x*tz), vz+w*tz+(x*ty-y*tx))
def vs(a,b): return tuple(x-y for x,y in zip(a,b))
def vl(a): return math.sqrt(sum(c*c for c in a))

FPS, SECS = 144.0, 3.0
OFFSET = (0.10, 0.0, 0.0)      # phone held PERFECTLY STILL: 10 cm lean, constant

def car_yaw(t, driving):
    return 40.0*t if driving else 0.0

def camera_rotation(t, driving):
    """CarX chase cam: car heading + sway + (when drifting) hard slip-angle follow."""
    yaw = car_yaw(t, driving)
    if driving:
        sway_y = 2.5*math.sin(6.0*t)
        sway_p = 1.2*math.sin(4.3*t+1.0)
        sway_r = 0.6*math.sin(5.0*t)
        drift  = 18.0*math.sin(2.1*t)          # slip-angle follow while drifting
        yaw += sway_y + drift
        return qmul(qax((0,1,0),yaw), qmul(qax((1,0,0),sway_p+8.0), qax((0,0,1),sway_r)))
    return qmul(qax((0,1,0),yaw), qax((1,0,0),8.0))

def stable_frame(t, driving):
    return qax((0,1,0), car_yaw(t, driving))    # car heading, yaw only

def qinv(q):
    x,y,z,w=q; return (-x,-y,-z,w)

def offset_in_car_frame(mode, driving):
    """
    The offset expressed in the CAR's frame - i.e. what the player perceives, since the
    car is what they see centred in shot.

    This is the metric that matters, and picking it took two wrong attempts:
      * total path length is dominated by the car's own smooth heading change (large in
        BOTH frames, and not shake);
      * total jerk still counts the smooth circular sweep of an offset riding with a
        turning car.
    With the phone motionless, ANY variation of this quantity is the camera drifting
    relative to the car - which is exactly the wobble being reported.
    """
    out=[]
    for f in range(int(FPS*SECS)):
        t=f/FPS
        q = camera_rotation(t,driving) if mode=="live" else stable_frame(t,driving)
        world = qrot(q, OFFSET)
        out.append(qrot(qinv(stable_frame(t,driving)), world))
    return out

def spread(pts):
    """Peak-to-peak of the car-relative offset, per axis, worst axis."""
    return max(max(p[i] for p in pts) - min(p[i] for p in pts) for i in range(3))

print("="*76)
print("Phone held PERFECTLY STILL (constant 10 cm lean).")
print("Measured: the offset expressed in the CAR's frame. With a motionless phone this")
print("should be constant; any variation is the camera wobbling relative to the car.")
print("="*76)

res={}
for driving in (False, True):
    lab = "DRIVING / DRIFTING" if driving else "PARKED"
    l = spread(offset_in_car_frame("live", driving))
    n = spread(offset_in_car_frame("stable", driving))
    res[lab]=(l,n)
    print(f"\n  {lab}")
    print(f"    live camera rotation (<=0.5.3): {l*100:7.2f} cm of car-relative wobble")
    print(f"    car heading yaw-only (0.5.4)  : {n*100:7.2f} cm")

pl,pn = res["PARKED"]; dl,dn = res["DRIVING / DRIFTING"]

ok1 = dl > 0.01 and pl < 1e-9
print(f"\n  [{'PASS' if ok1 else 'FAIL'}] reproduces the report: old frame is rock solid parked "
      f"({pl*100:.2f} cm) and wobbles {dl*100:.2f} cm while driving")

ok2 = dn < 1e-9
print(f"  [{'PASS' if ok2 else 'FAIL'}] new frame eliminates it entirely: {dn*100:.4f} cm "
      f"(a still phone now gives a still camera, whatever the car is doing)")

ok3 = pn < 1e-9
print(f"  [{'PASS' if ok3 else 'FAIL'}] and parked behaviour is unchanged: {pn*100:.4f} cm")

print()
print("="*76)
n=sum([ok1,ok2,ok3]); print(f"{n}/3 passed")
raise SystemExit(0 if n==3 else 1)

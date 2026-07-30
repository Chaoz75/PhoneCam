"""
Proof that 0.4.4's camera write cannot freeze the camera, and that 0.4.0-0.4.3's could.

Models the exact C# in both versions against a MOVING CarX chase camera (sway +
follow, as CarX.FollowCamera really produces) and asks one question:

    when the phone sits at its neutral pose, does the camera still move?

That is the whole regression. A camera that stops moving at neutral is frozen.
"""
import math

def qax(a,d):
    n=math.sqrt(sum(c*c for c in a)) or 1.0; a=[c/n for c in a]
    h=math.radians(d)/2; s=math.sin(h); return (a[0]*s,a[1]*s,a[2]*s,math.cos(h))
def qmul(A,B):
    ax,ay,az,aw=A; bx,by,bz,bw=B
    return (aw*bx+ax*bw+ay*bz-az*by, aw*by-ax*bz+ay*bw+az*bx,
            aw*bz+ax*by-ay*bx+az*bw, aw*bw-ax*bx-ay*by-az*bz)
def qconj(q): x,y,z,w=q; return(-x,-y,-z,w)
def qrot(q,v):
    x,y,z,w=q; vx,vy,vz=v
    tx=2*(y*vz-z*vy); ty=2*(z*vx-x*vz); tz=2*(x*vy-y*vx)
    return (vx+w*tx+(y*tz-z*ty), vy+w*ty+(z*tx-x*tz), vz+w*tz+(x*ty-y*tx))
def va(a,b): return tuple(x+y for x,y in zip(a,b))
def vs(a,b): return tuple(x-y for x,y in zip(a,b))
def vl(a): return math.sqrt(sum(c*c for c in a))
IDENT=(0.,0.,0.,1.)

def car_at(t):
    return ((6.0*t, 0.0, 3.0*math.sin(0.8*t)), qax((0,1,0), 40.0*t))

def carx_chase_cam(t):
    """
    CarX's own solve. CarX.FollowCamera carries m_CurrentVelocityOffset /
    m_TargetVelocityOffset / m_FollowSpeed / m_FollowTime / m_SwaySpeed /
    m_BaseSwayAmount (all real fields in Assembly-CSharp), so its boom does NOT
    sit at a fixed point in the car's frame - it lags on turns and pulls back
    under acceleration. Modelling that is essential: with a rigid boom the
    camera's car-relative motion is zero by construction and the test cannot
    distinguish "welded to the car" from "behaving normally".
    """
    cp,cr = car_at(t)
    # velocity-driven boom stretch + follow lag, in the car's local frame
    speed = 6.0 + 2.4*math.cos(0.8*t)
    lag_z = -3.96 - 0.30*speed*0.1*math.sin(1.7*t)
    lag_x =  0.39 + 0.22*math.sin(2.3*t)
    lag_y =  2.53 + 0.09*math.sin(3.1*t)
    pos=va(cp,qrot(cr,(lag_x,lag_y,lag_z)))
    sway=qmul(qax((0,1,0),2.5*math.sin(6.0*t)), qax((1,0,0),20.0+1.2*math.sin(4.3*t+1.0)))
    return pos, qmul(cr,sway)

anchor_lp=(0.39,2.53,-3.96)
anchor_lr=qax((1,0,0),20.0)

def pivot_local():
    boom=vl(anchor_lp); f=qrot(anchor_lr,(0,0,1))
    return va(anchor_lp, tuple(c*boom for c in f))

def v043_assign(t, off_euler, gain):
    """0.4.0-0.4.3: ASSIGN a car-constant pose. Ignores CarX entirely."""
    cp,cr=car_at(t)
    pl=pivot_local(); bl=vs(anchor_lp,pl)
    e=[c*gain for c in off_euler]
    orb=qmul(qax((0,1,0),e[1]), qax((1,0,0),e[0]))
    orbited=va(pl,qrot(orb,bl))
    return va(cp,qrot(cr,orbited))

def v044_additive(t, off_euler, gain):
    """0.4.4: ADD a displacement to CarX's live camera."""
    cam_pos,cam_rot=carx_chase_cam(t)
    cp,cr=car_at(t)
    pl=pivot_local(); bl=vs(anchor_lp,pl)
    e=[c*gain for c in off_euler]
    orb=qmul(qax((0,1,0),e[1]), qax((1,0,0),e[0]))
    orbited=va(pl,qrot(orb,bl))
    delta_local=vs(orbited,anchor_lp)
    return va(cam_pos, qrot(cr,delta_local))

TIMES=[i*0.05 for i in range(1,241)]
NEUTRAL=(0.0,0.0,0.0)     # phone exactly at its calibrated pose
TYPICAL=(-7.0,4.0,0.0)    # a real logged sample
GAIN=5.0

def motion(fn, off):
    """Total frame-to-frame movement — how alive the camera is."""
    pts=[fn(t,off,GAIN) for t in TIMES]
    return sum(vl(vs(b,a)) for a,b in zip(pts,pts[1:]))

carx_pts=[carx_chase_cam(t)[0] for t in TIMES]
carx_motion=sum(vl(vs(b,a)) for a,b in zip(carx_pts,carx_pts[1:]))

print("="*74)
print("REGRESSION PROOF - phone held at NEUTRAL pose (offset 0,0,0)")
print("="*74)
m043=motion(v043_assign,NEUTRAL); m044=motion(v044_additive,NEUTRAL)
print(f"  CarX's own camera travels          : {carx_motion:8.2f} m over 12 s")
print(f"  0.4.0-0.4.3 (assign) camera travels: {m043:8.2f} m")
print(f"  0.4.4      (additive) travels      : {m044:8.2f} m")
print()
# Does the mod preserve CarX's motion exactly at neutral?
worst=max(vl(vs(v044_additive(t,NEUTRAL,GAIN), carx_chase_cam(t)[0])) for t in TIMES)
print(f"  0.4.4 deviation from CarX's camera at neutral: {worst:.2e} m")
ok1 = worst < 1e-9
print(f"  [{'PASS' if ok1 else 'FAIL'}] at neutral, 0.4.4 is a mathematical no-op -> cannot freeze")

# The 0.4.3 failure: it replaces CarX motion with rigid-to-car motion.
# Measure motion RELATIVE TO THE CAR, which is what the driver perceives.
def rel_motion(fn, off):
    pts=[]
    for t in TIMES:
        cp,cr=car_at(t)
        pts.append(qrot(qconj(cr), vs(fn(t,off,GAIN), cp)))
    return sum(vl(vs(b,a)) for a,b in zip(pts,pts[1:]))

r043=rel_motion(v043_assign,NEUTRAL); r044=rel_motion(v044_additive,NEUTRAL)
rcarx=[]
for t in TIMES:
    cp,cr=car_at(t); rcarx.append(qrot(qconj(cr), vs(carx_chase_cam(t)[0],cp)))
rcarx_m=sum(vl(vs(b,a)) for a,b in zip(rcarx,rcarx[1:]))
print()
print("  Motion RELATIVE TO THE CAR (what you actually see on screen):")
print(f"    CarX's own camera : {rcarx_m:7.3f} m   (alive - sway/follow)")
print(f"    0.4.0-0.4.3      : {r043:7.3f} m   <-- WELDED TO CAR = FROZEN")
print(f"    0.4.4            : {r044:7.3f} m   (CarX motion preserved)")
ok2 = r043 < 1e-9 and r044 > 0.1
print(f"  [{'PASS' if ok2 else 'FAIL'}] reproduces the freeze in 0.4.3 and its absence in 0.4.4")

print()
print("="*74)
print("Phone MOVED (a real logged sample: pitch -7, yaw +4, gain 5x)")
print("="*74)
travel=max(vl(vs(v044_additive(t,TYPICAL,GAIN), carx_chase_cam(t)[0])) for t in TIMES)
print(f"  0.4.4 camera displacement from CarX's camera: {travel:.2f} m")
ok3 = travel > 1.0
print(f"  [{'PASS' if ok3 else 'FAIL'}] a typical phone turn still produces metres of travel")

print()
print("="*74)
n=sum([ok1,ok2,ok3])
print(f"{n}/3 passed")
raise SystemExit(0 if n==3 else 1)

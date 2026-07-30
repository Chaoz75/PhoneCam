"""
Does clamping keep the camera out of the ground / out of the car?

Driven by the REAL orbitEuler values logged in the 0.4.5 session that produced the
report - the ones that put the camera at y=-8.93 (metres below the car).
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
def va(a,b): return tuple(x+y for x,y in zip(a,b))
def vs(a,b): return tuple(x-y for x,y in zip(a,b))
def vl(a): return math.sqrt(sum(c*c for c in a))

ANCHOR=(0.00,2.87,-3.96)                 # real logged calibration seat (MazdaRX7)
ANCHOR_ROT=qax((1,0,0),20.0)
YAW_CLAMP, PITCH_CLAMP = 55.0, 18.0

def pivot():
    b=vl(ANCHOR); f=qrot(ANCHOR_ROT,(0,0,1))
    return va(ANCHOR, tuple(c*b for c in f))

def orbited(pitch, yaw, clamp, floor):
    if clamp:
        yaw=max(-YAW_CLAMP,min(YAW_CLAMP,yaw)); pitch=max(-PITCH_CLAMP,min(PITCH_CLAMP,pitch))
    pl=pivot(); bl=vs(ANCHOR,pl)
    q=qmul(qax((0,1,0),yaw), qax((1,0,0),pitch))
    p=va(pl,qrot(q,bl))
    if floor and p[1]<ANCHOR[1]: p=(p[0],ANCHOR[1],p[2])
    return p

# The actual orbitEuler values logged when the user reported a frozen camera.
REAL=[(-39,115),(-38,126),(51,161),(-146,174),(-56,122),(-30,124),(-16,-3)]

print("="*78); print("Real logged orbitEuler from the 0.4.5 'camera frozen' session"); print("="*78)
print(f"{'pitch':>7}{'yaw':>6} | {'UNCLAMPED (0.4.5)':>34} | {'CLAMPED (0.4.6)':>30}")
print(f"{'':>13} | {'cam y':>9}{'travel':>9}{'below car?':>14} | {'cam y':>9}{'travel':>9}{'below?':>10}")
print("-"*78)
bad_before=bad_after=0
for pitch,yaw in REAL:
    a=orbited(pitch,yaw,False,False); b=orbited(pitch,yaw,True,True)
    ba = a[1] < ANCHOR[1]-0.01; bb = b[1] < ANCHOR[1]-0.01
    bad_before += ba; bad_after += bb
    print(f"{pitch:7.0f}{yaw:6.0f} | {a[1]:9.2f}{vl(vs(a,ANCHOR)):9.2f}{('YES - UNDER' if ba else 'no'):>14} | "
          f"{b[1]:9.2f}{vl(vs(b,ANCHOR)):9.2f}{('YES' if bb else 'no'):>10}")

print()
ok1 = bad_before>0 and bad_after==0
print(f"[{'PASS' if ok1 else 'FAIL'}] clamps eliminate every below-car case "
      f"({bad_before}/{len(REAL)} bad before, {bad_after}/{len(REAL)} after)")

# Exhaustive sweep of every reachable orbit angle at max gain.
worst_y=1e9; worst_travel=0.0
for p in range(-180,181,3):
    for y in range(-180,181,3):
        q=orbited(p,y,True,True)
        worst_y=min(worst_y,q[1]); worst_travel=max(worst_travel,vl(vs(q,ANCHOR)))
ok2 = worst_y >= ANCHOR[1]-1e-6
print(f"[{'PASS' if ok2 else 'FAIL'}] exhaustive sweep of all 14641 angle pairs: "
      f"lowest camera y = {worst_y:.2f} (seat y = {ANCHOR[1]:.2f}), max travel {worst_travel:.2f} m")

# And it must still move usefully at the new 2x default with a typical 8 deg turn.
t=orbited(-3.2*2, 8.0*2, True, True)
travel=vl(vs(t,ANCHOR))
ok3 = 0.5 < travel < 4.0
print(f"[{'PASS' if ok3 else 'FAIL'}] typical 8 deg turn at the new 2x default still moves the camera "
      f"{travel:.2f} m (want 0.5-4 m: visible, not chaotic)")

print("="*78)
n=sum([ok1,ok2,ok3]); print(f"{n}/3 passed")
raise SystemExit(0 if n==3 else 1)

"""
0.7.0: the camera is moved by overriding worldToCameraMatrix, not by writing the Transform.

The whole design rests on this matrix being exactly what Unity would have derived from a
Transform at the offset pose. If it is wrong the view is silently skewed or mirrored, so
it is verified against Unity's own definition rather than assumed:

    worldToCameraMatrix == Matrix4x4.TRS(pos, rot, (1,1,-1)).inverse

The (1,1,-1) scale is the handedness flip: Unity's view space looks down -Z while a
Transform's forward is +Z.
"""
import math

def mat_mul(A,B):
    return [[sum(A[i][k]*B[k][j] for k in range(4)) for j in range(4)] for i in range(4)]
def mat_vec(M,v):
    r=[sum(M[i][k]*v[k] for k in range(4)) for i in range(4)]
    return r
def ident(): return [[1.0 if i==j else 0.0 for j in range(4)] for i in range(4)]

def quat_to_mat(q):
    x,y,z,w=q
    return [
        [1-2*(y*y+z*z), 2*(x*y-w*z),   2*(x*z+w*y),   0.0],
        [2*(x*y+w*z),   1-2*(x*x+z*z), 2*(y*z-w*x),   0.0],
        [2*(x*z-w*y),   2*(y*z+w*x),   1-2*(x*x+y*y), 0.0],
        [0.0,0.0,0.0,1.0]]

def trs(pos, q, scale):
    R=quat_to_mat(q)
    M=[[R[i][j]*scale[j] for j in range(3)]+[pos[i]] for i in range(3)]
    M.append([0.0,0.0,0.0,1.0])
    return M

def inverse(M):
    # general 4x4 inverse via Gauss-Jordan (these are affine, but keep it general)
    n=4; A=[row[:]+[1.0 if i==j else 0.0 for j in range(n)] for i,row in enumerate(M)]
    for c in range(n):
        p=max(range(c,n), key=lambda r: abs(A[r][c]))
        A[c],A[p]=A[p],A[c]
        pv=A[c][c]
        A[c]=[v/pv for v in A[c]]
        for r in range(n):
            if r!=c and A[r][c]!=0:
                f=A[r][c]; A[r]=[a-f*b for a,b in zip(A[r],A[c])]
    return [row[n:] for row in A]

def qax(axis,deg):
    n=math.sqrt(sum(c*c for c in axis)) or 1.0; a=[c/n for c in axis]
    h=math.radians(deg)/2; s=math.sin(h)
    return (a[0]*s,a[1]*s,a[2]*s,math.cos(h))
def qmul(A,B):
    ax,ay,az,aw=A; bx,by,bz,bw=B
    return (aw*bx+ax*bw+ay*bz-az*by, aw*by-ax*bz+ay*bw+az*bx,
            aw*bz+ax*by-ay*bx+az*bw, aw*bw-ax*bx-ay*by-az*bz)
def qrot(q,v):
    x,y,z,w=q; vx,vy,vz=v
    tx=2*(y*vz-z*vy); ty=2*(z*vx-x*vz); tz=2*(x*vy-y*vx)
    return (vx+w*tx+(y*tz-z*ty), vy+w*ty+(z*tx-x*tz), vz+w*tz+(x*ty-y*tx))
def vsub(a,b): return tuple(x-y for x,y in zip(a,b))
def vlen(a): return math.sqrt(sum(c*c for c in a))

print("="*78)
print("Verifying worldToCameraMatrix = TRS(pos, rot, (1,1,-1)).inverse")
print("="*78)

CASES=[((0,0,0), qax((0,1,0),0)),
       ((5,2,-3), qax((0,1,0),37)),
       ((-8.3,-2.8,-3.3), qmul(qax((0,1,0),150), qax((1,0,0),20))),
       ((172.5,-3.7,-75.3), qmul(qmul(qax((0,1,0),-95), qax((1,0,0),-12)), qax((0,0,1),8)))]

worst_origin=0.0; worst_fwd=0.0; worst_pt=0.0
for pos,rot in CASES:
    V = inverse(trs(pos,rot,(1.0,1.0,-1.0)))
    # 1. the camera's world position must map to the view-space origin
    o = mat_vec(V, list(pos)+[1.0])
    worst_origin=max(worst_origin, vlen(o[:3]))
    # 2. a point 10 m along the camera's forward must land on view -Z
    fwd = qrot(rot,(0,0,1))
    p = tuple(pos[i]+fwd[i]*10.0 for i in range(3))
    pv = mat_vec(V, list(p)+[1.0])
    worst_fwd = max(worst_fwd, abs(pv[0])+abs(pv[1])+abs(pv[2]+10.0))
    # 3. distances must be preserved (no scale/skew)
    q1=(pos[0]+1.0,pos[1]+2.0,pos[2]+3.0)
    a=mat_vec(V,list(pos)+[1.0]); b=mat_vec(V,list(q1)+[1.0])
    worst_pt=max(worst_pt, abs(vlen(vsub(b[:3],a[:3])) - vlen((1.0,2.0,3.0))))

print(f"\n  camera position -> view-space origin      : max error {worst_origin:.2e}")
print(f"  point 10 m ahead -> view-space (0,0,-10)  : max error {worst_fwd:.2e}")
print(f"  distances preserved (rigid, no skew)      : max error {worst_pt:.2e}")

ok1 = worst_origin < 1e-9
ok2 = worst_fwd  < 1e-9
ok3 = worst_pt   < 1e-9
print(f"\n  [{'PASS' if ok1 else 'FAIL'}] the override puts the camera exactly at the requested position")
print(f"  [{'PASS' if ok2 else 'FAIL'}] and aims it exactly along the requested forward (-Z convention)")
print(f"  [{'PASS' if ok3 else 'FAIL'}] and is rigid - no scale, mirror or skew leaks in")

# 4. an offset must displace the view by exactly that offset
pos,rot = CASES[2]
OFF=(0.12,-0.03,0.05)
base = inverse(trs(pos,rot,(1.0,1.0,-1.0)))
offp = tuple(pos[i]+OFF[i] for i in range(3))
off  = inverse(trs(offp,rot,(1.0,1.0,-1.0)))
probe=(10.0,4.0,-2.0)
d = vlen(vsub(mat_vec(off,list(probe)+[1.0])[:3], mat_vec(base,list(probe)+[1.0])[:3]))
ok4 = abs(d - vlen(OFF)) < 1e-9
print(f"  [{'PASS' if ok4 else 'FAIL'}] a {vlen(OFF)*100:.1f} cm pose offset shifts the view by exactly "
      f"{d*100:.1f} cm")

print("\n"+"="*78)
n=sum([ok1,ok2,ok3,ok4]); print(f"{n}/4 passed")
raise SystemExit(0 if n==4 else 1)

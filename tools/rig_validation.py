"""
Numerical validation of PhoneCam 0.4.0's car-anchored camera rig.

This reproduces the exact arithmetic the C# does (Unity quaternion/Transform
semantics, unit scale) so the *algorithm* can be checked without the game:

    Unity                              here
    ------------------------------     -------------------------------
    Quaternion * Vector3               qrot(q, v)
    Quaternion * Quaternion            qmul(a, b)
    Quaternion.Inverse(q)              qconj(q)          (unit quats)
    t.TransformPoint(p)                t.pos + qrot(t.rot, p)
    t.InverseTransformPoint(p)         qrot(qconj(t.rot), p - t.pos)

What's under test (HeadTrackMod.OnCameraPreCull, rig branch):

    basePosition = car.TransformPoint(anchorLocalPosition)
    baseRotation = car.rotation * anchorLocalRotation
    cam.position = basePosition + baseRotation * posOffset
    cam.rotation = baseRotation * rotOffset

versus the pre-0.4.0 additive branch:

    cam.position += cam.rotation * posOffset
    cam.rotation  = cam.rotation * rotOffset

and versus 0.3.25's reverted world-space anchor:

    cam.position = anchorWorldPos + anchorWorldRot * posOffset
    cam.rotation = anchorWorldRot * rotOffset
"""

import math

# ---------------------------------------------------------------- quaternions
# (x, y, z, w), same component order as UnityEngine.Quaternion.


def qmul(a, b):
    ax, ay, az, aw = a
    bx, by, bz, bw = b
    return (
        aw * bx + ax * bw + ay * bz - az * by,
        aw * by - ax * bz + ay * bw + az * bx,
        aw * bz + ax * by - ay * bx + az * bw,
        aw * bw - ax * bx - ay * by - az * bz,
    )


def qconj(q):
    x, y, z, w = q
    return (-x, -y, -z, w)


def qrot(q, v):
    """Rotate vector v by unit quaternion q (Unity's Quaternion * Vector3)."""
    x, y, z, w = q
    vx, vy, vz = v
    # t = 2 * cross(q.xyz, v)
    tx = 2.0 * (y * vz - z * vy)
    ty = 2.0 * (z * vx - x * vz)
    tz = 2.0 * (x * vy - y * vx)
    # v + w*t + cross(q.xyz, t)
    return (
        vx + w * tx + (y * tz - z * ty),
        vy + w * ty + (z * tx - x * tz),
        vz + w * tz + (x * ty - y * tx),
    )


def qaxisangle(axis, deg):
    ax, ay, az = axis
    n = math.sqrt(ax * ax + ay * ay + az * az) or 1.0
    ax, ay, az = ax / n, ay / n, az / n
    h = math.radians(deg) * 0.5
    s = math.sin(h)
    return (ax * s, ay * s, az * s, math.cos(h))


def qeuler(pitch, yaw, roll):
    """Unity's Quaternion.Euler(x=pitch, y=yaw, z=roll): applies Z, then X, then Y."""
    qy = qaxisangle((0, 1, 0), yaw)
    qx = qaxisangle((1, 0, 0), pitch)
    qz = qaxisangle((0, 0, 1), roll)
    return qmul(qmul(qy, qx), qz)


IDENT = (0.0, 0.0, 0.0, 1.0)


def vadd(a, b):
    return (a[0] + b[0], a[1] + b[1], a[2] + b[2])


def vsub(a, b):
    return (a[0] - b[0], a[1] - b[1], a[2] - b[2])


def vlen(a):
    return math.sqrt(a[0] ** 2 + a[1] ** 2 + a[2] ** 2)


def angle_between(qa, qb):
    """
    Angle between two unit quaternions, in degrees.

    Deliberately NOT the acos(dot) form Unity's Quaternion.Angle uses: acos is
    ill-conditioned as its argument approaches 1, so for two nearly-identical
    rotations it reports ~1e-6 deg of pure measurement noise and hides whether
    the real error is 1e-6 or 1e-16. The atan2(|sin|, cos) form below stays
    accurate all the way down to zero, so a near-zero reading here means the
    rotations really are identical rather than merely close.
    """
    d = sum(x * y for x, y in zip(qa, qb))
    rel = qmul(qconj(qa), qb) if d >= 0 else qmul(qconj(qa), tuple(-c for c in qb))
    vx, vy, vz, w = rel
    return math.degrees(2.0 * math.atan2(math.sqrt(vx * vx + vy * vy + vz * vz), abs(w)))


def mat_to_quat(m):
    """
    Rotation matrix (basis vectors as COLUMNS, m[row][col]) -> quaternion.

    Full four-branch Shepperd's method. The naive single-branch trace formula is
    only valid while trace > 0; past ~120 degrees of rotation the trace goes
    negative and that formula divides by a vanishing (or imaginary) term. An
    earlier revision of this harness fell back to identity in that case, which
    silently produced up to 180 degrees of error for exactly the wide orbit
    angles being tested - a harness bug that looked like an algorithm bug.
    Branching on the largest diagonal element keeps the divisor well away from
    zero for every possible rotation.
    """
    m00, m01, m02 = m[0]
    m10, m11, m12 = m[1]
    m20, m21, m22 = m[2]
    tr = m00 + m11 + m22

    if tr > 0.0:
        s = math.sqrt(tr + 1.0) * 2.0
        return ((m21 - m12) / s, (m02 - m20) / s, (m10 - m01) / s, 0.25 * s)
    if m00 > m11 and m00 > m22:
        s = math.sqrt(1.0 + m00 - m11 - m22) * 2.0
        return (0.25 * s, (m01 + m10) / s, (m02 + m20) / s, (m21 - m12) / s)
    if m11 > m22:
        s = math.sqrt(1.0 + m11 - m00 - m22) * 2.0
        return ((m01 + m10) / s, 0.25 * s, (m12 + m21) / s, (m02 - m20) / s)
    s = math.sqrt(1.0 + m22 - m00 - m11) * 2.0
    return ((m02 + m20) / s, (m12 + m21) / s, 0.25 * s, (m10 - m01) / s)


# ------------------------------------------------------------------ transform
class Xform:
    __slots__ = ("pos", "rot")

    def __init__(self, pos=(0, 0, 0), rot=IDENT):
        self.pos = pos
        self.rot = rot

    def transform_point(self, p):
        return vadd(self.pos, qrot(self.rot, p))

    def inverse_transform_point(self, p):
        return qrot(qconj(self.rot), vsub(p, self.pos))


# ------------------------------------------------------------ the three modes
def rig(car, anchor_local_pos, anchor_local_rot, pos_offset, rot_offset):
    """0.4.0 car-anchored rig."""
    base_pos = car.transform_point(anchor_local_pos)
    base_rot = qmul(car.rot, anchor_local_rot)
    return (vadd(base_pos, qrot(base_rot, pos_offset)), qmul(base_rot, rot_offset))


def additive(cam, pos_offset, rot_offset):
    """<=0.3.31 additive perturbation of CarX's own solve."""
    return (vadd(cam.pos, qrot(cam.rot, pos_offset)), qmul(cam.rot, rot_offset))


def world_anchor(anchor_pos, anchor_rot, pos_offset, rot_offset):
    """0.3.25's reverted fixed-world-space anchor."""
    return (vadd(anchor_pos, qrot(anchor_rot, pos_offset)), qmul(anchor_rot, rot_offset))


# ------------------------------------------------------- simulated game motion
def car_at(t):
    """Car driving a curve while yawing - stands in for real gameplay."""
    yaw = 40.0 * t
    return Xform(pos=(6.0 * t, 0.0, 3.0 * math.sin(0.8 * t)), rot=qeuler(0, yaw, 0))


def carx_chase_cam(car, t):
    """
    Approximation of CarX.FollowCamera's output: a boom behind the car, plus the
    sway/damping the real component carries (m_SwaySpeed / m_BaseSwayAmount /
    m_TrackingSwayAmount, seen in Assembly-CSharp). Exact values don't matter -
    what matters is that the baseline MOVES on its own every frame.
    """
    boom_local = (0.0, 1.6, -5.0)
    sway_yaw = 2.5 * math.sin(6.0 * t)
    sway_pitch = 1.2 * math.sin(4.3 * t + 1.0)
    pos = car.transform_point(boom_local)
    rot = qmul(car.rot, qeuler(sway_pitch + 8.0, sway_yaw, 0.6 * math.sin(5.0 * t)))
    return Xform(pos=pos, rot=rot)


# ------------------------------------------------------------------- reporting
PASS, FAIL = "PASS", "FAIL"
results = []


def check(name, ok, detail):
    results.append((PASS if ok else FAIL, name, detail))
    print(f"[{PASS if ok else FAIL}] {name}\n        {detail}")


print("=" * 78)
print("PhoneCam 0.4.0 - car-anchored rig validation")
print("=" * 78)

# Calibrate at t=0 exactly as Calibrate() does.
t0 = 0.0
car0 = car_at(t0)
cam0 = carx_chase_cam(car0, t0)
anchor_local_pos = car0.inverse_transform_point(cam0.pos)
anchor_local_rot = qmul(qconj(car0.rot), cam0.rot)
anchor_world_pos, anchor_world_rot = cam0.pos, cam0.rot

print(f"\ncalibration: car at {tuple(round(v,3) for v in car0.pos)}, "
      f"camera at {tuple(round(v,3) for v in cam0.pos)}")
print(f"anchorLocalPosition = {tuple(round(v,3) for v in anchor_local_pos)}\n")

TIMES = [i * 0.05 for i in range(1, 241)]  # 12 s

# --- T1: with zero tracking input the rig must ride with the car exactly ------
worst_rig = worst_world = 0.0
for t in TIMES:
    car = car_at(t)
    expected = car.transform_point(anchor_local_pos)  # where it *should* be
    p_rig, _ = rig(car, anchor_local_pos, anchor_local_rot, (0, 0, 0), IDENT)
    p_world, _ = world_anchor(anchor_world_pos, anchor_world_rot, (0, 0, 0), IDENT)
    worst_rig = max(worst_rig, vlen(vsub(p_rig, expected)))
    worst_world = max(worst_world, vlen(vsub(p_world, expected)))

check(
    "T1  rig follows the car with zero tracking input",
    worst_rig < 1e-9,
    f"max deviation from the car-carried pose: rig={worst_rig:.2e} m "
    f"(0.3.25 world anchor, for contrast: {worst_world:.1f} m adrift)",
)

# --- T2: translation is 1:1 in the anchor frame -------------------------------
worst_err = 0.0
for t in TIMES:
    car = car_at(t)
    for probe in [(1, 0, 0), (0, 1, 0), (0, 0, 1), (0.35, -0.2, 0.9)]:
        p_zero, _ = rig(car, anchor_local_pos, anchor_local_rot, (0, 0, 0), IDENT)
        p_off, _ = rig(car, anchor_local_pos, anchor_local_rot, probe, IDENT)
        # displacement, expressed back in the anchor's own frame
        base_rot = qmul(car.rot, anchor_local_rot)
        local_disp = qrot(qconj(base_rot), vsub(p_off, p_zero))
        worst_err = max(worst_err, vlen(vsub(local_disp, probe)))

check(
    "T2  translation is exactly 1:1 in the anchor frame",
    worst_err < 1e-9,
    f"max |applied - requested| across 4 probe vectors x {len(TIMES)} frames: {worst_err:.2e} m",
)

# --- T3: rotation is exactly the requested delta ------------------------------
worst_rot_err = 0.0
for t in TIMES:
    car = car_at(t)
    for e in [(0, 30, 0), (-15, 0, 0), (0, 0, 20), (12, -25, 5)]:
        rq = qeuler(*[e[0], e[1], e[2]])
        _, r_zero = rig(car, anchor_local_pos, anchor_local_rot, (0, 0, 0), IDENT)
        _, r_off = rig(car, anchor_local_pos, anchor_local_rot, (0, 0, 0), rq)
        delta = qmul(qconj(r_zero), r_off)  # what actually got applied
        worst_rot_err = max(worst_rot_err, angle_between(delta, rq))

check(
    "T3  rotation applies exactly the requested delta",
    worst_rot_err < 1e-6,
    f"max angular error across 4 probe rotations x {len(TIMES)} frames: {worst_rot_err:.2e} deg",
)

# --- T4: no drift / no accumulation under constant input ----------------------
const_pos = (0.12, -0.05, 0.20)
const_rot = qeuler(-6, 18, 2)
poses = []
for t in TIMES:
    car = car_at(t)
    p, r = rig(car, anchor_local_pos, anchor_local_rot, const_pos, const_rot)
    poses.append((car, p, r))

worst_drift = 0.0
for car, p, r in poses:
    base_pos = car.transform_point(anchor_local_pos)
    base_rot = qmul(car.rot, anchor_local_rot)
    local_p = qrot(qconj(base_rot), vsub(p, base_pos))
    worst_drift = max(worst_drift, vlen(vsub(local_p, const_pos)))

check(
    "T4  constant phone pose -> constant camera pose (no accumulation)",
    worst_drift < 1e-9,
    f"max car-relative wander over {len(TIMES)} consecutive frames: {worst_drift:.2e} m",
)

# --- T5: stability vs the old additive path -----------------------------------
# Hold the phone perfectly still. Measure how much the camera still jitters.
rig_jitter = []
add_jitter = []
prev_rig = prev_add = None
for t in TIMES:
    car = car_at(t)
    cam = carx_chase_cam(car, t)
    base_rot_now = qmul(car.rot, anchor_local_rot)

    p_rig, r_rig = rig(car, anchor_local_pos, anchor_local_rot, const_pos, const_rot)
    p_add, r_add = additive(cam, const_pos, const_rot)

    # measure each mode's pose in the car's frame, so the car's own motion cancels
    lr = qrot(qconj(base_rot_now), vsub(p_rig, car.transform_point(anchor_local_pos)))
    la = qrot(qconj(base_rot_now), vsub(p_add, car.transform_point(anchor_local_pos)))
    if prev_rig is not None:
        rig_jitter.append(vlen(vsub(lr, prev_rig)))
        add_jitter.append(vlen(vsub(la, prev_add)))
    prev_rig, prev_add = lr, la

rj, aj = max(rig_jitter), max(add_jitter)
check(
    "T5  rig is stable where the additive path is not (phone held still)",
    rj < 1e-9 and aj > rj,
    f"max frame-to-frame car-relative jitter: rig={rj:.2e} m, additive={aj:.4f} m "
    f"({'inf' if rj == 0 else f'{aj/rj:.0f}x'} worse)",
)

# --- T6: additive rotation does not translate the camera ----------------------
# The core "world moves but the camera doesn't" complaint, quantified.
t = 3.0
car = car_at(t)
cam = carx_chase_cam(car, t)
look = qeuler(0, 30, 0)  # a 30 deg look to the side

p_add, _ = additive(cam, (0, 0, 0), look)
add_travel = vlen(vsub(p_add, cam.pos))

p_rig_a, _ = rig(car, anchor_local_pos, anchor_local_rot, (0, 0, 0), IDENT)
p_rig_b, _ = rig(car, anchor_local_pos, anchor_local_rot, (0.5, 0, 0), IDENT)
rig_travel = vlen(vsub(p_rig_b, p_rig_a))

check(
    "T6  additive rotation moves the camera 0 m (root cause of the report)",
    add_travel < 1e-9 and abs(rig_travel - 0.5) < 1e-9,
    f"30 deg look under additive => camera travels {add_travel:.2e} m (view sweeps, camera "
    f"stays put); rig 0.5 m lean => camera travels {rig_travel:.3f} m",
)

# ------------------------------------------------------------------- summary
print("\n" + "=" * 78)
failed = [r for r in results if r[0] == FAIL]
for status, name, _ in results:
    print(f"  {status}  {name}")
print("=" * 78)
print(f"{len(results) - len(failed)}/{len(results)} checks passed")
if failed:
    raise SystemExit(1)


# ============================================================================
# 0.4.2 ORBIT MODE
# Measured from a real 0.4.1 session (108 samples): the phone's ARKit stream
# carries 359 deg of yaw range but only 0.094 m of positional range. So the
# only usable signal is rotation. These checks establish what each mode can
# actually do with rotation-only input.
# ============================================================================
print("\n" + "=" * 78)
print("0.4.2 orbit mode - rotation-only input")
print("=" * 78 + "\n")


def orbit_pivot_local(anchor_local_pos, anchor_local_rot):
    boom = vlen(anchor_local_pos)
    if boom < 0.01:
        return (0.0, 0.0, 0.0)
    fwd = qrot(anchor_local_rot, (0.0, 0.0, 1.0))
    return vadd(anchor_local_pos, (fwd[0] * boom, fwd[1] * boom, fwd[2] * boom))


def orbit(car, anchor_local_pos, anchor_local_rot, offset_euler, pos_offset=(0, 0, 0)):
    """0.4.2 orbit mode, mirroring the C# exactly."""
    pitch, yaw, roll = offset_euler
    pivot_local = orbit_pivot_local(anchor_local_pos, anchor_local_rot)
    boom_local = vsub(anchor_local_pos, pivot_local)

    q = qmul(qaxisangle((0, 1, 0), yaw), qaxisangle((1, 0, 0), pitch))
    orbited_local = vadd(pivot_local, qrot(q, boom_local))

    orbited_world = car.transform_point(orbited_local)
    pivot_world = car.transform_point(pivot_local)

    aim = vsub(pivot_world, orbited_world)
    car_up = qrot(car.rot, (0.0, 1.0, 0.0))
    # Quaternion.LookRotation(aim, car.up)
    f = [c / (vlen(aim) or 1.0) for c in aim]
    r = (car_up[1] * f[2] - car_up[2] * f[1],
         car_up[2] * f[0] - car_up[0] * f[2],
         car_up[0] * f[1] - car_up[1] * f[0])
    rl = vlen(r) or 1.0
    r = [c / rl for c in r]
    u = (f[1] * r[2] - f[2] * r[1], f[2] * r[0] - f[0] * r[2], f[0] * r[1] - f[1] * r[0])
    m = [[r[0], u[0], f[0]], [r[1], u[1], f[1]], [r[2], u[2], f[2]]]
    rot = mat_to_quat(m)
    rot = qmul(rot, qaxisangle((0, 0, 1), roll))
    return (vadd(orbited_world, qrot(rot, pos_offset)), rot, pivot_world)


# Real values straight out of the live log.
anchor_lp = (0.01, 2.84, -3.88)          # car-local camera pose at F9, MazdaRX7
anchor_lr = qeuler(20.0, 0.0, 0.0)       # chase cam aimed forward and down
car = car_at(2.0)

# --- T7: rotation-only input must actually move the camera --------------------
travel_by_mode = {}
look = (0.0, 30.0, 0.0)  # 30 deg of phone yaw, no translation at all

p_base = car.transform_point(anchor_lp)
p_rig, _ = rig(car, anchor_lp, anchor_lr, (0, 0, 0), qeuler(*look))
travel_by_mode["rig 1:1 (0.4.0)"] = vlen(vsub(p_rig, p_base))

cam_now = carx_chase_cam(car, 2.0)
p_add, _ = additive(cam_now, (0, 0, 0), qeuler(*look))
travel_by_mode["additive (<=0.3.31)"] = vlen(vsub(p_add, cam_now.pos))

p_orb, _, pivot_w = orbit(car, anchor_lp, anchor_lr, look)
travel_by_mode["orbit (0.4.2)"] = vlen(vsub(p_orb, p_base))

for mode, d in travel_by_mode.items():
    print(f"        {mode:24} camera travel for a 30 deg phone turn = {d:6.3f} m")

check(
    "T7  rotation-only input produces real camera travel in orbit mode",
    travel_by_mode["orbit (0.4.2)"] > 1.0
    and travel_by_mode["rig 1:1 (0.4.0)"] < 1e-9
    and travel_by_mode["additive (<=0.3.31)"] < 1e-9,
    f"orbit={travel_by_mode['orbit (0.4.2)']:.3f} m vs 0.000 m for both older modes "
    f"- this is why the camera never appeared to move",
)

# --- T8: orbit keeps the car framed -------------------------------------------
worst_aim = 0.0
worst_radius_err = 0.0
r0 = vlen(vsub(car.transform_point(anchor_lp), car.transform_point(orbit_pivot_local(anchor_lp, anchor_lr))))
for yaw in range(-180, 181, 5):
    for pitch in (-25.0, 0.0, 25.0):
        p, r, pw = orbit(car, anchor_lp, anchor_lr, (pitch, float(yaw), 0.0))
        fwd = qrot(r, (0.0, 0.0, 1.0))
        to_pivot = vsub(pw, p)
        n = vlen(to_pivot) or 1.0
        cosang = sum(a * b / n for a, b in zip(to_pivot, fwd))
        worst_aim = max(worst_aim, math.degrees(math.acos(max(-1.0, min(1.0, cosang)))))
        worst_radius_err = max(worst_radius_err, abs(vlen(to_pivot) - r0))

check(
    "T8  orbit keeps the framed point centred at a constant radius",
    worst_aim < 1e-3 and worst_radius_err < 1e-9,
    f"across 111 orbit positions: max aim error {worst_aim:.2e} deg, "
    f"max radius drift {worst_radius_err:.2e} m (radius {r0:.2f} m)",
)

# --- T9: orbit still follows the car ------------------------------------------
worst_follow = 0.0
for t in TIMES:
    c = car_at(t)
    p, _, pw = orbit(c, anchor_lp, anchor_lr, (5.0, 40.0, 0.0))
    # the orbited point must stay fixed in the car's own frame
    local = c.inverse_transform_point(p)
    expected = vadd(orbit_pivot_local(anchor_lp, anchor_lr),
                    qrot(qmul(qaxisangle((0, 1, 0), 40.0), qaxisangle((1, 0, 0), 5.0)),
                         vsub(anchor_lp, orbit_pivot_local(anchor_lp, anchor_lr))))
    worst_follow = max(worst_follow, vlen(vsub(local, expected)))

check(
    "T9  orbit rides with the car (car-local, not world-locked)",
    worst_follow < 1e-9,
    f"max car-relative deviation over {len(TIMES)} frames: {worst_follow:.2e} m",
)

print("\n" + "=" * 78)
failed2 = [r for r in results if r[0] == FAIL]
for status, name, _ in results:
    print(f"  {status}  {name}")
print("=" * 78)
print(f"{len(results) - len(failed2)}/{len(results)} checks passed")
if failed2:
    raise SystemExit(1)

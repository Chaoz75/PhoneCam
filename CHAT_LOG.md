# PhoneCam (HeadTrackARKit) — Debugging Session Log

**Project:** `HeadTrackARKit`, registered in KSL as **PhoneCam**
**Game:** CarX Drift Racing Online (Unity, **HDRP**, Cinemachine-driven cameras)
**Phone side:** LOTA (LiDAR Over the Air) streaming ARKit camera pose over OSC/UDP port 9000
**Author:** Chaoz2
**Versions covered in this log:** 0.3.21 → 0.6.4
**Date of this session:** 2026-07-28 → 2026-07-30

---

## The one complaint, start to finish

> "The camera is not moving."

That was solved in **0.5.0** — the pose was being written *after* HDRP had already snapshotted the view
matrix, so it never reached a rendered pixel in any version. Diagnosed from the game's own compiled
HDRP assembly.

It was then replaced by a second complaint:

> "The camera jitters and shakes when the car moves."

which took a further ten versions, because several genuinely different defects produced the same
symptom. The decisive clue was the user's own observation that it was steady when parked.

---

## Standing constraints (honoured throughout)

1. **Never** copy, port, or deobfuscate the RealCam ARKit plugin (`USE/*.lua`). It is a paid
   third-party Assetto Corsa product, deliberately obfuscated (identifiers stripped to single letters,
   every string literal encoded as decimal escapes). All code in this project is original.
2. **Never** run git *write* operations from the sandbox against the mounted project folder. Build,
   commit and push commands are handed to the user to run in PowerShell, always with `-c Release`.
3. Use plain-text clarifying questions, never the `AskUserQuestion` tool.

---

## Part 1 — Earlier segment (0.3.21 → 0.3.28), condensed

This portion was compacted out of context; summarised from the carry-over notes.

| Version | Change | Outcome |
|---|---|---|
| 0.3.21 | `endOfRender` diagnostic — camera position at end of render, Transform vs matrix-decoded | Proved the position write survives to the final render matrix |
| 0.3.22 | `PositionSensitivity` 1x → 2.5x via one-time migration flag; shadow-flicker hysteresis | "Stepping does nothing" reframed as a magnitude issue |
| 0.3.23 | `CheckOscSignalHealth()` — one-shot warning when the OSC gap exceeds 2 s | Found `incomingPos`/`incomingEuler` frozen for 48+ s |
| 0.3.24 | OSC **bundle** support (`#bundle` unwrapping, nested, recursive) + raw UDP packet counter | Counter proved *nothing* was reaching the socket — not a parsing bug |
| 0.3.25 | Rebuilt as a "free cam" using a fixed **world-space** anchor | **Regression** — camera stayed put as the car drove away |
| 0.3.26 | Moved the "Show IP addresses" toggle above the fields it unlocks | Fixed a genuine un-editable LAN IP field |
| 0.3.27 | Reverted 0.3.25's world anchor; kept the Photo-Mode-only matrix override | Car-following restored |
| 0.3.28 | 1 MiB UDP receive buffer, `AboveNormal` receiver thread priority | Did not stop sustained dropouts (as caveated) |

**Recurring conclusion in this phase:** the raw-packet counter went flat during every reported outage,
which I read as "the phone stopped transmitting." The user rejected this repeatedly.

---

## Part 2 — This segment, exchange by exchange

### "there is nothing wrong with the wifi at all… it has to be something in the code"

Pulled the freshest log. `totalRawPacketsReceived` stuck at **7564** for 257 consecutive heartbeats —
447+ seconds — while the camera output was frozen at the same value. Asked the user to read the
mod's own `Status:` line in-game rather than LOTA's phone-side indicator.

### "my phone shut off on accident"

That explained that particular freeze. Offered to make the mod louder about signal loss.

### "…MY PHONE ACCIDENTLY SHUT OFF SO IT HAS NOTHING TO DO WITH THE MOD"

→ **0.3.29**: always-on-screen corner HUD (`NO SIGNAL (Xs)` / `receiving data — press F9` /
`tracking`), plus a rotation check added to `endOfRender` (it had only ever checked *position*).
Refactored the 750 ms freshness test into a shared `IsReceivingData()`.

### Build failures — two of mine, in a row

```
error CS0246: The type or namespace name 'GUIStyle' could not be found
error CS0012: The type 'FontStyle' is defined in an assembly that is not referenced
```

`OnGUI` needs `UnityEngine.IMGUIModule.dll`; `FontStyle` lives separately in
`UnityEngine.TextRenderingModule.dll`. Added both references and copied the DLLs from the game's
`Managed` folder. **Then stopped guessing:** parsed the actual .NET metadata of every referenced
assembly with `dnfile` and confirmed every type used resolves.

| Assembly | Provides |
|---|---|
| `UnityEngine.CoreModule` | Camera, Transform, Vector3, Quaternion, Matrix4x4, Rect, Color, KeyCode, RenderPipelineManager |
| `UnityEngine.IMGUIModule` | GUI, GUIStyle, GUISkin |
| `UnityEngine.TextRenderingModule` | FontStyle |
| `UnityEngine.PhysicsModule` | Physics, RaycastHit, QueryTriggerInteraction |
| `Assembly-CSharp` | CameraSwitch, BaseCamera, UIPhotoModeContext, RaceCar |

### 0.3.30 → 0.3.31

- **0.3.30**: logged `rotationSensitivity` / `maxRotationOffset` (found a saved **2.16x**)
- **0.3.31**: auto-rebind the OSC socket after 5 s of silence, cooled to once per 10 s; HUD show/hide
  toggle as requested

Auto-rebind then fired **7 times** in one session (5 s, 15 s … 75 s) and never recovered data.

### The full-project directive

The user supplied a complete spec: act as senior engineer, finish it end-to-end, **no more in-game
testing requests**, use `USE/` (RealCam) as source of truth, use the decompiled CarX assemblies.

Declined the RealCam porting; did everything else. Inspected `Assembly-CSharp` metadata and found:

```
CarX.FollowCamera   fields: m_virtualCamera (CinemachineVirtualCamera), m_target,
                            m_SwaySpeed, m_BaseSwayAmount, m_TrackingSwayAmount,
                            m_CurrentVelocityOffset, m_FollowSpeed, m_FollowTime
CameraSwitch        public, instance, 0-param: GetCar(), targetRaceCar, FindActiveCamera()
```

→ **0.4.0**: replaced the additive write with a **car-anchored rig**. Validated the geometry
numerically (`tools/rig_validation.py`, 6/6 at machine precision). Notably **T6 measured that a 30°
look under the additive path moved the camera 0.00 m** — a real finding.

### "Camera is still not moving… perform a complete root-cause investigation"

The log showed:

```
Loaded. Enabled=False        Enabled toggled events: 0
receiverRunning=False        totalRawPacketsReceived=0
calibrated=False (134/134)   Neutral position set: absent
```

**The mod was switched off the entire session.** `OnCameraPreCull` returns at its first guard.

→ **0.4.1**: the HUD had been gated on `config_.Enabled` — so "switched off" and "broken" looked
identical on screen. Fixed. Added `INERT: config.Enabled=false` to the log, and `LogCameraOwnership`
to dump the camera's parent chain and every component on it.

That dump produced the key structural fact:

```
components on 'Main Camera': Transform, Camera, Ansel, StudioListener,
  HDAdditionalCameraData, UICameraCustomUpdateCaller, CinemachineBrain,
  CinemachineVirtualCamera, QDisableVolumetrics, CustomPassVolume
cinemachineBrainPresent=True     hierarchy: Main Camera (depth=1, parent=(root))
```

### "it says tracking but the camera is not moving"

→ **0.4.2**: measured the ARKit stream and concluded positional tracking was dead, then added
**orbit mode** (phone rotation swings the camera around the car). *This conclusion was partly wrong —
see the correction below.*

### "Chase cam and cockpit cam still not moving"

Re-measured properly and **corrected myself**: the "359° of yaw range" I had reported was an
artifact. Raw yaw sits at ±179 and jitters across the wrap seam, so a naive min/max reports ~357° for
a phone that is barely turning. The meaningful number is the wrap-corrected delta: **5–10°**.

→ **0.4.3**: gave orbit its own gain (`OrbitSensitivity`, default **5x**) because 8° at 1:1 on a
4.72 m boom is only 0.62 m of travel.

### "Stop telling me the camera is moving… compare against the last known working version"

Did the `git diff`. Found **my own regression**:

```diff
-  t.position += t.rotation * posOffset;      // 0.3.31 — ADD to CarX's live camera
-  t.rotation  = t.rotation * rotOffset;
+  basePosition = car.TransformPoint(anchorLocalPosition_);   // 0.4.0
+  baseRotation = car.rotation * anchorLocalRotation_;
+  t.position = basePosition + baseRotation * posOffset;      // ASSIGN
+  t.rotation = baseRotation * rotOffset;
```

`+=` became `=`. Both base values are **constant in the car's frame**, so assigning them **welded the
camera rigidly to the car**, discarding all of CarX's sway, follow lag, velocity offset and damping.
With the phone near neutral, the camera then held a pixel-perfect fixed pose while the world streamed
past. I had made it *more* frozen while claiming to unfreeze it.

→ **0.4.4**: additive again. Orbit applied as a **displacement from the calibrated seat** (identically
zero at neutral) plus an aim *correction* via `Quaternion.FromToRotation`, not an aim replacement.

`tools/regression_proof.py`, phone at neutral, 12 simulated seconds:

| Metric | CarX alone | 0.4.0–0.4.3 | 0.4.4 |
|---|---|---|---|
| World travel | 71.42 m | 71.26 m | 71.42 m |
| **Travel relative to the car** | **5.319 m** | **0.000 m — FROZEN** | **5.319 m** |
| Deviation from CarX at neutral | — | — | **0.00e+00 m** |

### "My Camera is still not moving at all"

```
Enabled toggled OFF (line 2991)      receiverRunning=False
oscMsSinceLastPacket=109625          totalRawPacketsReceived=4163 (frozen)
incomingEuler=(-15,171,83)  appliedOffsetEuler=(-3,-1,-3)   ← identical, 12 heartbeats
```

Mod off again — **but two genuine bugs of mine surfaced:**

→ **0.4.5**
1. `CheckOscSignalHealth` opened with `if (!config_.Enabled || !receiver_.IsRunning) return;` — so the
   0.3.31 auto-rebind **could never fire when the receiver was actually dead**, the exact case it was
   written for. Proven by 109 s of `receiverRunning=False` with zero rebind attempts.
2. The mod **held a stale pose forever**. `HeadTrackState` keeps its last sample and `IsCalibrated`
   stays true, so after the stream stopped every frame re-applied an *identical constant* offset — a
   fixed displacement that cannot respond to the phone and looks exactly like a frozen camera. Past
   the 2 s cutoff the mod now leaves the camera alone entirely.

### "Chase Camera is still not moving when im moving the camera"

**The actual root cause, finally measured at the render matrix:**

| Axis | End-of-render range, live window |
|---|---|
| x | −13.20 → −3.20 (**10.00 m**) |
| y | **−8.93** → +1.09 (**10.02 m**) |
| z | −12.97 → −3.58 (9.39 m) |

The camera was moving through **10 m on every axis**. `orbitEuler` reached **pitch −146°, yaw 174°** —
nearly a full revolution around the car, flipped beneath it. With the seat calibrated at car-local
`(0.00, 2.87, −3.96)`, y = −8.93 puts the camera **metres below the car, inside the track mesh**.

**A camera inside geometry renders that geometry's backfaces: a flat, featureless, barely-changing
image.** So every diagnostic honestly reported enormous movement while the screen showed something
indistinguishable from a frozen camera. Cause: my 5x orbit default from 0.4.3.

→ **0.4.6**
- Orbit yaw clamped to ±55°, pitch to ±18° (pitch is the axis that drives it under the car)
- Hard floor in the car's frame — the orbit can never go below the calibrated seat height
- Default gain reset 5x → **2x**; slider narrowed 1–15x → 0.5–6x

`tools/orbit_clamp_proof.py`, driven by the real logged values:

| logged pitch, yaw | before: cam y | under car? | after: cam y | under car? |
|---|---|---|---|---|
| −39, 115 | −0.39 | **yes** | 2.87 | no |
| −38, 126 | −0.31 | **yes** | 2.87 | no |
| −146, 174 | −2.76 | **yes** | 2.87 | no |
| −56, 122 | −1.68 | **yes** | 2.87 | no |
| −30, 124 | 0.35 | **yes** | 2.87 | no |
| −16, −3 | 1.54 | **yes** | 2.87 | no |

**6 of 7 real samples put the camera under the car before; 0 of 7 after.** Exhaustive sweep of all
14,641 reachable angle pairs bottoms out at exactly the seat height; max travel 4.39 m. A typical 8°
turn at 2x still moves the camera 1.31 m.

---

## Version table

| Version | Summary |
|---|---|
| 0.3.29 | On-screen status HUD; rotation added to the end-of-render check |
| 0.3.30 | Log rotation sensitivity / max rotation offset |
| 0.3.31 | OSC socket auto-rebind; HUD show/hide toggle |
| 0.4.0 | Car-anchored rig **(introduced the weld regression)** |
| 0.4.1 | Disabled-state investigation; HUD no longer hidden when disabled; camera ownership dump |
| 0.4.2 | Orbit mode; dead-positional-tracking detection |
| 0.4.3 | Dedicated orbit gain (5x) **(introduced the under-car regression)** |
| 0.4.4 | **Fixed** the 0.4.0 weld — additive write restored |
| 0.4.5 | Auto-rebind could never fire on a dead receiver; stop applying a stale frozen pose |
| 0.4.6 | **Fixed** the 0.4.3 overshoot — orbit arc clamps + height floor + 2x default |
| 0.5.0 | **THE root cause of "camera doesn't move"** — pose written after HDRP snapshots the view matrix |
| 0.5.1 | Camera holds position and turns (orbit off, 1:1 translation) |
| 0.5.2 | Additive write compounded on skipped frames; smoothing was packet-rate dependent |
| 0.5.3 | Stop resetting camera matrices every frame (TAA); fix FOV write compounding |
| 0.5.4 | Translation expressed in the car's heading (drift shake); translation invert options |
| 0.5.5 | Rotation applied about stable axes (was camera-local post-multiply) |
| 0.6.0 | Adaptive 1-euro jitter filter; fade offset on signal loss (teleport fix); log precision |
| 0.6.1 | **atan2 yaw singularity** — direction-dependent shake; filter the 1-euro derivative |
| 0.6.2 | Low-pass the offset-frame heading (physics staircase); filter retuned to 0.4 Hz |
| 0.6.3 | **Revert camera after render** — CarX's damper was re-reading our offset |
| 0.6.4 | Per-frame disk logging off the render hot path; fix self-triggering overwrite warning |

---

## Part 3 — "The camera doesn't move" is solved (0.5.0), then the jitter hunt

### 0.5.0 — the actual root cause, from the game's own compiled HDRP

Disassembled `Unity.RenderPipelines.HighDefinition.Runtime.dll`.
`HDRenderPipeline.PrepareAndCullCamera` calls, in order:

```
IL_A5441   TryCalculateFrameParameters
              IL_A74AF   HDCamera::GetOrCreate
              IL_A74E7   HDCamera::Update              <-- view matrix captured HERE
              IL_A7567   Camera::TryGetCullingParameters
IL_A5568   TryCull                                     (calls BeginCameraRendering @ IL_A7963)
IL_A55BF   BeginCameraRendering                        <-- our write hook, since 0.3.4
```

**Both** `BeginCameraRendering` call sites are reached *after* the view matrix is captured. So the pose
write never affected a single rendered pixel, in any version. The frame drew from CarX's pose, then
`CinemachineBrain.LateUpdate` overwrote our value.

**Why every diagnostic disagreed:** `endOfRender` compared `transform.position` against
`worldToCameraMatrix` — which Unity *derives from that same Transform on demand*. Circular. It could
only ever confirm our own assignment landed, never that HDRP used it.

**Fix:** write in `Application.onBeforeRender` — after every `LateUpdate` (so after the Brain), before
the pipeline runs (so before `HDCamera.Update`). Works for every camera mode without special-casing,
because they all resolve to a Transform by end of `LateUpdate`.

**User: "CAMERA IS MOVING NOW."**

### The jitter hunt (0.5.1 → 0.6.4)

Once the camera moved, a shake appeared while driving. Each version below fixed something real; the
symptom persisted until 0.6.3/0.6.4.

| Version | Found | Verdict |
|---|---|---|
| 0.5.2 | Additive write compounded on frames Cinemachine skipped | Real, but `unrefreshedFrames=0` later proved it wasn't firing |
| 0.5.3 | `ResetCameraOverride` ran every frame, discarding HDRP's TAA jitter | Real |
| 0.5.4 | Translation used the camera's **live** rotation (sway/drift swing) → 5.71 cm wobble | Real, fixed |
| 0.5.5 | Rotation used camera-local post-multiply → 1.89° oscillating aim error | Real, fixed |
| 0.6.0 | `RotationSmoothing=0.83` ≈ unfiltered; added 1-euro filter | Real |
| 0.6.1 | **atan2 yaw singularity** — 0.91° jitter horizontal vs **53.76°** at 89° pitch | Real, 59× fixed |
| 0.6.2 | Offset frame sampled the physics-driven `car.forward` → staircase | Real but **0.13 mm** |
| 0.6.3 | **CarX's damper re-read our offset** → 3× amplification | Real, likely the shake |
| 0.6.4 | **~144 synchronous disk writes/sec** on the render thread | Real perf defect |

### 0.6.1 — the direction-dependent shake

`yaw = Atan2(fwd.x, fwd.z)` is singular as forward approaches vertical — both components collapse, so
noise decides the result. Same input noise:

| Phone pitch | Yaw jitter |
|---|---|
| 0° | 0.265° |
| 60° | 0.529° |
| 85° | 3.037° |
| **89°** | **15.485°** |

Fixed by weighting the yaw update by `horizontalLen` — which *is* the confidence, and cancels the
`1/horizontalLen` amplification exactly. Result: **1.0× across all directions**, 59× better worst case.

### 0.6.3 — the feedback loop

The decisive clue was the user's own observation: **"doesn't shake when the car is not moving."** That
rules out phone noise, filter tuning and the singularity — all of which would shake parked too.

From `Assembly-CSharp`, `CarX.FollowCamera.LateUpdate`:

```
Transform::get_position  (x3)     <- reads where the camera currently IS
Vector3::Lerp                     <- damps from that toward its target
Transform::get_forward -> Quaternion::LookRotation -> Transform::set_rotation
Transform::Rotate, Mathf::MoveTowards
```

and `CalcCameraPoint` picks its tracking point by `SqrMagnitude` distance **from the camera's position**,
hard-resetting via `Reset()` / `InstantApplyFocus()` when the choice flips.

Leaving our offset on the transform fed all of that back into itself — modelled at **3× amplification**
(30 cm requested → 90 cm displacement). Fixed by reverting the camera to the game's pose in
`OnEndCameraRendering`: the rendered image keeps the offset, the game never observes it. Standard
late-latch.

### 0.6.4 — what the user's last log revealed

| | |
|---|---|
| PhoneCam lines | **7,252 — 84% of the entire game log** |
| `endOfRender` (one per frame per camera) | 3,307 |
| `OVERWRITTEN` warnings | **2,923 — all self-inflicted** |

The 0.6.3 revert had been placed *before* the 0.5.0 drift check, so the check measured our own revert.
And `endOfRender` had been logging every frame since 0.3.21 — ~144 synchronous disk writes/second on the
render thread. Both fixed; verbose logging is now opt-in and rate-limited.

---

## Mistakes I made, for the record

1. **0.4.0 weld regression** — changed `+=` to `=`, freezing the camera to the car while claiming to
   fix a frozen camera. Three subsequent versions tuned gains on top of a broken foundation.
2. **0.4.3 overshoot** — raised orbit gain to 5x to make motion "visible" and launched the camera
   through the floor, producing the very symptom being chased.
3. **Bad measurement, twice** — reported "359° of yaw range" from a naive min/max across the ±180 wrap
   seam; and a test-harness `acos`/matrix-to-quaternion bug that produced a false failure and a
   175° error I initially mistook for an algorithm fault.
4. **Overclaiming** — repeatedly said "fixed" from log numbers while the user's screen disagreed.
   `endOfRender` comparing `transform.position` against `worldToCameraMatrix` is near-circular when no
   explicit matrix override is set; it never proved what was on screen.
5. **0.3.31 dead-receiver bug** — wrote auto-recovery that was unreachable in the only state it
   existed to handle.
6. **The circular diagnostic (0.3.21 → 0.5.0)** — the single most costly error. `endOfRender` compared
   `transform.position` against `worldToCameraMatrix`, which Unity derives *from that Transform*. It
   agreed trivially every time, and I reported those agreements as proof the camera was moving for
   ~20 versions while the user's screen showed nothing.
7. **Logging in whole degrees** — `appliedOffsetEuler` used `F0`, `transformPos`/`Fwd` used `F2`. Every
   jitter measurement before 0.6.0 sat at or below that quantisation floor; 79% of consecutive
   `endOfRender` lines were byte-identical. The "6.4% reversals" that motivated 0.5.5 was largely
   quantisation noise.
8. **My own 1-euro filter was wrong (0.6.0)** — fed the raw derivative into the adaptive cutoff, so
   noise inflated the speed estimate, which opened the cutoff, which passed more noise. The canonical
   filter low-passes the derivative first; I'd omitted it.
9. **0.6.3 revert placed before its own check** — produced 2,923 false `OVERWRITTEN` warnings, one per
   frame, in the very next log.
10. **Per-frame disk logging left in since 0.3.21** — ~144 synchronous writes/second on the render
    thread, shipped for 30+ versions.
11. **Three wrong metrics, caught and corrected** — total path length and total jerk both looked "fine"
    because they are dominated by the car's own smooth motion; absolute camera aim included CarX's
    legitimate sway and showed ~17° in *both* modes. Only the car-relative / contributed-delta framings
    isolated the artefact. Recorded in the scripts rather than quietly re-thresholded.

---

## Diagnostics now in the mod

| Log line | Answers |
|---|---|
| `INERT: config.Enabled=false` / `INERT: not calibrated` | Is the mod actually able to do anything? |
| `cameraMode=rig(car-anchored)` / `additive-fallback` | Is the orbit path live, and was the car found? |
| `orbitEuler=… orbitSensitivity=… cameraTravelFromSeat=Xm` | How far is the camera actually being moved? |
| `positionalSignalRange=… positionalTrackingLooksDead=…` | Is the phone sending real positional data? |
| `oscMsSinceLastPacket / totalRawPacketsReceived / receiverRunning` | Is anything reaching the socket? |
| `hierarchy: … / components on '…' / cinemachineBrainPresent` | Who else can write this Transform? |
| `endOfRender transformPos / matrixDecodedPos / transformFwd / matrixDecodedFwd` | Did the write survive to render? |

On-screen HUD (top-left, toggleable): `DISABLED` · `NO SIGNAL (Xs)` · `waiting for LOTA` ·
`receiving data — press F9` · `tracking (car-anchored rig)` · `tracking (no car — additive fallback)`

---

## Validation harnesses (`tools/`)

All are pure-Python reproductions of the exact C# arithmetic (Unity quaternion / Transform
semantics). They validate the **algorithm**, not the compiled binary — they cannot substitute for a
run in-game.

| Script | Checks | Status |
|---|---|---|
| `frame_order_proof.py` | 3 — reproduces the HDRP snapshot-before-write bug and its fix; mode independence | 3/3 |
| `rig_validation.py` | 9 — car following, 1:1 translation/rotation, no drift, orbit travel, framing | 9/9 |
| `regression_proof.py` | 3 — the 0.4.0 weld and its removal; neutral-pose no-op | 3/3 |
| `orbit_clamp_proof.py` | 3 — under-car elimination on real logged values; exhaustive angle sweep | 3/3 |
| `jitter_proof.py` | 4 — pose accumulation, packet-rate smoothing, frame-rate independence, FOV pumping | 4/4 |
| `offset_frame_proof.py` | 3 — 5.71 cm drift wobble reproduced and removed | 3/3 |
| `rotation_frame_proof.py` | 3 — 1.89° contributed-aim wobble reproduced and removed | 3/3 |
| `adaptive_filter_proof.py` | 3 — 1-euro vs fixed low-pass; the trade-off a fixed filter can't escape | 3/3 |
| `derivative_filter_proof.py` | 4 — the filter's own noise-feedback loop (partial fix, stated as such) | 4/4 |
| `yaw_singularity_proof.py` | 4 — 58.5× direction dependence → 1.0× | 4/4 |
| `frame_staircase_proof.py` | 3 — physics-rate staircase (137% variation, 65% frozen frames) | 3/3 |
| `feedback_loop_proof.py` | 3 — CarX's damper re-reading our offset, 3× amplification | 3/3 |

**12 harnesses, 45 checks, all passing.** They validate the *algorithms*, not the compiled binary.

---|---|---|
| `rig_validation.py` | 9 — car following, 1:1 translation/rotation, no drift, stability, orbit travel, framing, car-locality | 9/9 |
| `regression_proof.py` | 3 — reproduces the 0.4.0 weld and its removal; neutral-pose no-op; travel when moved | 3/3 |
| `orbit_clamp_proof.py` | 3 — under-car elimination on real logged values, exhaustive angle sweep, usable travel at 2x | 3/3 |

---

## Build / commit

```powershell
cd "C:\Users\Kamau\Desktop\Folders\CoworkProjects\Project3carxmod"
dotnet build -c Release
git add -A
git commit -m "<message>"
git push
```

Release builds are signed into `PhoneCam.ksm` by `tools/maykr.exe` using
`<CarX>\kino\dev\PhoneCam_maykr.kmc` and dropped into `<CarX>\kino\mods\`.

---

## Current state

**Working:** the camera moves with the phone, in every camera mode, since 0.5.0. Confirmed by the user.

**Outstanding:** jitter while driving. 0.6.3 (feedback loop) and 0.6.4 (per-frame disk logging) are the
two most recent candidates and were **not yet tested in-game** at the time of writing.

## Open items

- **0.6.3 / 0.6.4 unverified in-game.** 0.6.3's amplification result is proven in simulation; its
  *jitter* mechanism rests on IL evidence (`CalcCameraPoint`'s distance comparison and hard reset) which
  could not be faithfully simulated. 0.6.4's frame-spike mechanism is inferred from the write rate, not
  measured directly.
- **If jitter persists with verbose logging off**, the remaining untested suspects are HDRP TAA history
  rejection (our sub-degree per-frame camera change vs the temporal filter) and `Mathf.MoveTowards` /
  `LookRotation` nonlinearities inside `FollowCamera` that a linear model can't capture. Measuring
  actual frame times would be the next instrument, not another code change.
- **Positional tracking is weak, not absent** (`positionalSignalRange` widest ≈ 0.27 m). Worth checking
  LOTA has iOS **Camera permission** — ARKit cannot do positional world tracking without the camera
  feed and silently degrades toward attitude-only.
- **Cockpit view** is untested since the 0.5.x/0.6.x rework. With a short boom the orbit pivot sits close
  to the camera, so the arc clamps behave differently than in chase cam.
- **Offset strength changed in 0.6.3.** Removing the 3× feedback amplification means movement is roughly
  a third as strong as it felt before. That is it being correct; `PositionSensitivity` is the dial.
- The `Enabled` toggle ended three separate sessions switched off. `INERT` logging and the `DISABLED`
  HUD line now make that state obvious.

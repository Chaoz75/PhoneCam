# HeadTrackARKit — iPhone head tracking for CarX Drift Racing Online

Uses an iPhone's ARKit camera tracking (streamed over the network by the free **LOTA** app)
to drive a live head-tracking offset on top of CarX's own in-game camera — similar in spirit
to TrackIR. This is an original implementation built for this project; it does not use or
depend on the `RealCam ARKit` plugin found in `ModAssetto/` (that file is a separate, paid
Assetto Corsa–only plugin and was never opened, run, or decompiled — it's unrelated to this
mod other than "phone streams ARKit data over OSC" being the same general idea).

## Changelog

**0.3.19** - Fixed a real regression: shadows disappearing and severe motion blur (even while
completely stationary) the moment the mod was enabled, reported from an online session.
Root cause: `ApplyCameraOverride` - which manually reassigns
`Camera.worldToCameraMatrix`/`projectionMatrix`/`cullingMatrix` every frame to fight Kino's Custom
Camera mode possibly freezing those matrices in Photo Mode - was running unconditionally on every
frame the mod was Enabled, regardless of whether there was any actual head-tracking offset or zoom
to apply. The old comment argued this was a visual no-op since the resulting matrix *values* are
mathematically identical to Unity's defaults when nothing else is customizing them - true for the
values, but not for the act of assigning them: setting `Camera.projectionMatrix` at all switches
the camera into a "custom projection" mode that disables the render pipeline's own per-frame TAA
jitter and temporal motion-vector bookkeeping, so reassigning a clean matrix every frame forever
looks to the motion-vector pass like the camera is constantly moving - exactly "motion blur while
standing still." The same override's `cullingMatrix` feeds cascaded shadow culling, and a custom
culling matrix that doesn't precisely match what the shadow pass expects can cull shadow-casters
out of the shadow map entirely - "shadows disappear."

Fixed by only calling `ApplyCameraOverride` on frames where there's a real zoom or head-tracking
offset large enough to matter (a tiny epsilon check on both), and calling `ResetCameraOverride`
otherwise - handing the camera fully back to Unity's normal automatic matrix derivation whenever
this mod isn't actually changing anything about it. This should restore normal shadows/TAA any
time you're centered/not actively leaning or zooming, and only re-engage the override during
active head movement or a zoom adjustment.

**0.3.18** - The v0.3.17 log confirmed (again) that the offset math and the Transform write are
both correct: right after calibration, `cameraWorldPosAfterWrite` moved by exactly the magnitude of
`appliedPosOffset` (rotated into world space by the camera's own facing, as expected from
`t.position += t.rotation * posOffset`), then later jumped by hundreds of meters the moment the
joystick connected and the car started driving around the parking/practice area - while
`appliedPosOffset` stayed under ~0.3m the whole time. So the reported "no movement" is the same
scale problem as before: a real head-tracking offset of a few tenths of a meter is invisible next
to the car's own motion once you're actually driving.

Rather than argue the point again, **`PositionSensitivity` is temporarily forced up to 8x**
(`ApplyDefaultsIfUnset`, was 1x) and its in-game slider range widened from 0-3 to 0-15 to match, so
the effect is unmistakable at a glance even against a moving scene. This intentionally overrides
whatever value was saved from a previous session, every load, until it's reverted - it's a
diagnostic aid, not a permanent tuning change. Once you can positively confirm the camera is moving
with your real steps (ideally while the car is stationary, or in Photo Mode where the camera holds
still on its own), say so and this will be dropped back to a normal 1x default that only fills in a
truly-unset value, same as everything else in that method.

**0.3.17** - Two things this round, both aimed at "I step left, nothing happens":

1. **Every single real-game log this project has produced** - all the way back - repeats
   `Unable to save config 'PhoneCam.ksc': System.NullReferenceException` constantly, on every
   toggle and every slider drag. That means settings tuned mid-session (raising Max position
   offset, Position sensitivity, the invert toggles, etc.) have likely never actually survived a
   game restart - `ApplyDefaultsIfUnset` explicitly defaults every numeric setting, but
   `LocalIpOverride`/`PhoneIpFilter` (the only two `string` properties) were never defaulted away
   from a literal `null`, and a null string reaching whatever `(string, string, string)` method
   KSL's save path uses internally (visible right in the log's stack trace) is a very plausible
   source of that NullReferenceException. Both now default to `""` on load. Not provably *the*
   cause (KSL's actual save code is obfuscated, can't be read directly) but it's the one concrete
   gap that existed and costs nothing to close.
2. **Added a ground-truth diagnostic**: the heartbeat log now also prints
   `cameraWorldPosAfterWrite` - the camera's actual world position read back immediately after
   this mod writes to its Transform, every frame. The v0.3.16 log's math checked out
   (`appliedPosOffset` genuinely swung across a >0.5m range while stepping side to side, matching
   real movement scaled by sensitivity) - but "the numbers are right" and "the screen shows it"
   are two different claims, and there was no way to tell from the old log whether something
   *else* (CarX/Kino's own camera-follow logic) was overwriting the Transform again before the
   frame actually rendered. Comparing `cameraWorldPosAfterWrite` frame-to-frame in the next test
   settles that directly, independent of whatever offset math produced it.

Also worth isolating on the next test: try this in **Photo Mode** specifically rather than while
actively driving. In free-roam/race, CarX's own camera is already moving and rotating with the
car, so a real half-meter shift is easy to miss against that - while the same movement is very
obvious on the dashboard needles, which sit only inches from the lens (parallax makes near
objects sweep a much bigger angle for the same camera movement than distant ones). Photo Mode's
camera holds still on its own, which removes that confound entirely.

**0.3.16** - Confirmed v0.3.15's rotation fix worked (log shows `Loading [PhoneCam 0.3.15 ...]`,
`incomingEuler`/`appliedOffsetEuler` no longer showing the pitch/yaw coupling). This round's ask:
make sure real-world stepping (e.g. two steps to the right) is actually detected and applied
correctly, not just rotation. Position had the *same* root-cause bug as the rotation issue:
`HeadTrackState.GetPositionOffset()` rotated the incoming position delta by the full calibration
orientation's inverse (`baseRotationInverse_` - every axis: yaw, pitch, *and* roll). Since this rig
calibrates with the phone rolled ~90 degrees, rotating a horizontal step vector through that much
roll swaps it onto the *vertical* axis of the result - so a real step to the side was arriving at
the camera mostly as an up/down offset instead of left/right, easy to mistake for "stepping does
nothing" if it happened to get eaten by clamping, or just look wrong. **Fixed the same way as the
rotation bug**: position delta is now rotated by a *yaw-only* calibration frame
(`baseYawOnlyInverse_`, built from `ComputeWorldYawPitchRoll`'s yaw component only) instead of the
full 3D orientation. Yaw-only rotation can't tilt world-vertical into horizontal or vice versa, so
real up/down steps stay on Y and real left/right/forward/back steps stay on the horizontal plane,
regardless of the phone's mounting roll. Calibration-time yaw still decides what "forward" means
for the session (so leaning/stepping forward always pushes the camera forward even if you turn
your head afterward) - only the roll/pitch contamination is removed.

To verify: press F9 to calibrate, then physically take a couple of real steps to the side and
check the heartbeat log's `incomingPos`/`appliedPosOffset` lines - the offset's dominant axis
should now match the direction you actually moved (x for left/right, y for up/down, z for
forward/back), not a mix.

**0.3.15** - Same "look left = up, look right = down" symptom kept coming back even after 0.3.13
removed the pitch/yaw swap, and the 0.3.14 log finally explained why: this rig's incoming roll
sits around 85-98 degrees *constantly* - the phone is physically held/mounted rolled about 90
degrees from "upright." `HeadTrackState.GetRotationOffsetEuler` extracted yaw/pitch from a delta
quaternion (`baseRotationInverse_ * smoothedRotation_`), which measures rotation in the *phone's
own calibrated-local frame* - correct in general, but if that frame is itself rolled ~90 degrees
from true world-vertical, a genuine yaw turn (rotating around the true vertical axis) lines up with
the phone's local *pitch* axis instead, so a clean, correctly-computed atan2 pitch value legitimately
comes out huge (confirmed directly: `appliedOffsetEuler.x=-89` after roughly a 90-degree turn). Not
a decomposition bug this time - the delta math was doing exactly what it's designed to do, just in
the wrong reference frame for a rig with real mounting roll. **Fixed by measuring yaw/pitch against
true world axes instead**: both the live orientation and the calibration baseline get their own
world-referenced (yaw, pitch, roll) - see `HeadTrackState.ComputeWorldYawPitchRoll` - and the offset
is a plain per-axis angle subtraction, wrapped to -180..180. Since both sides of that subtraction are
always measured against the same fixed world axes, whatever constant roll the phone happens to be
held at cancels out cleanly, regardless of its exact value. If you'd toggled "Invert up/down" or
"Invert left/right" while chasing this, reset both to off before retesting - they were compensating
for the old bug and may fight the real fix.

Also: the in-car dashboard gauge needles ("digidash," "digiboost," etc., confirmed via the now-
removed diagnostic scan to NOT be parented under the camera) visibly swinging while looking around
may well have been a *symptom* of this same bug rather than a separate issue - wild, incorrect pitch
swings from the coupling above would send the camera whipping past the dashboard in exactly the way
that'd look like "the gauges are moving." Worth confirming after this fix before treating it as
still-open.

**0.3.14** - Three changes:
- **Removed the Gauge HUD workaround entirely** (the "Hide gauges while PhoneCam is enabled"
  toggle and the "Log dashboard gauge diagnostics" button, plus all the supporting code and
  config). It was solving the wrong problem in the first place (MultiHUD's 2D HUD, not the 3D
  dashboard needles) and is no longer wanted in the menu.
- **Added position-offset diagnostics**, mirroring the rotation ones already in place - a report
  that stepping/leaning left-right in real life (translation, not looking around) wasn't visibly
  moving the camera couldn't be confirmed either way from the existing log, since only rotation
  was being logged. The periodic heartbeat now also prints the raw incoming position, the actual
  per-axis offset `GetPositionOffset()` produced, and the current `MaxPositionOffset`/
  `PositionSensitivity` - next test session's log will show directly whether position samples are
  arriving and, if so, whether they're just being clamped down to nothing.
- **0.3.13's actual build never made it into the game.** `dotnet build` (no `-c Release`) builds
  the Debug configuration by default, and the `RunMaykr` step that signs and drops `PhoneCam.ksm`
  into `kino\mods\` only runs `Condition="'$(Configuration)' == 'Release'"` - so the 0.3.12 `.ksm`
  kept loading even after a "successful" build. Confirmed directly: the next KSL log still showed
  `Loading [PhoneCam 0.3.12 ...]`, and with the swap-removal fix un-deployed, the same
  `appliedOffsetEuler.x=180`-during-yaw-turn signature from before reappeared exactly as
  predicted. No code changed here - just a reminder that `-c Release` is required for a real
  build/deploy, not just `dotnet build`.

Also requested this round, not yet implemented - needs design decisions first (see chat): an
attach/detach toggle so the camera can decouple from the car entirely (car drives off on its own,
camera free-floats from wherever it was and keeps taking head-tracking input) versus reattaching
to follow the car again.

**0.3.13** - The atan2 rewrite (0.3.12) fixed the axis-bleed bug, but real testing showed the camera
could *still* flip upside down after a couple of full 360 turns. The 0.3.12 diagnostic log gave a
direct, concrete answer: `appliedOffsetEuler` hit `x=234` (pitch) during ordinary left/right
turning. A pitch (rotation around the camera's right/horizontal axis) of that size isn't a glitch -
it's a literal upside-down camera, and no amount of clean input data changes that; pitch simply
can't spin through 360 degrees without flipping, only yaw (rotation around up) can. The actual
cause: `HeadTrackMod.FixLookDirection`'s unconditional pitch/yaw swap (added in 0.3.9) was feeding
the large, unbounded "turning left/right" value straight into the applied rotation's pitch slot.
That swap was validated back in 0.3.9 against the *old*, decomposition-unstable rotation extraction
- it's likely the original "turning left/right moved the camera up/down" symptom it fixed was
itself partly a side effect of that same instability, not a true, permanent axis mismatch. Now that
extraction is clean (0.3.12's `atan2` rewrite), the swap is obsolete and was actively causing the
new flip. **Fixed by removing the swap** - raw pitch and yaw now route straight through to the
matching camera axis, so unlimited left/right turning stays on the flip-safe yaw axis no matter how
many times you spin around. `InvertPitch`/`InvertYaw` are unchanged and still available if either
direction ever reads backwards on your specific rig.

Also confirmed via Q&A: the existing 1:1 position-tracking (`Max position offset`, 0.3.10) already
applies in every mode including Photo Mode - moving the phone left/right/forward/back moves the
camera the same amount, up to the configured range. No separate "WASD-style" movement feature was
needed; this is what was being asked for.

**0.3.12** - 0.3.11's rotation fix (removing the pitch/yaw clamp) wasn't the whole story - a real
log from testing it caught the actual bug: looking behind you (yaw approaching +-180 degrees) sent
the camera to "weird angles" rather than continuing to turn smoothly. Root cause: standard XYZ
Euler-angle decomposition (`Quaternion.eulerAngles`) isn't stable at every orientation, and that
instability sits right on top of "looking behind you" - a normal, frequent direction, not a rare
edge case. The log proved it directly: a near-pure yaw turn came back with a huge, spurious roll
component (`appliedOffsetEuler` jumping to `roll=284` with no actual roll motion happening) -
rotation was bleeding between axes. Fixed by extracting yaw and pitch directly from the delta
rotation's forward vector via `atan2` instead of decomposing a full Euler triple - `atan2` has no
such instability anywhere across the full yaw range, and only degenerates when looking exactly
straight up or down (a genuinely rare case for a driving camera). `HeadTrackState.GetRotationOffset`
was restructured into `GetRotationOffsetEuler`, returning the raw (pitch, yaw, roll) numbers
directly so `HeadTrackMod.FixLookDirection`'s pitch/yaw swap operates on them before they're ever
assembled into a Quaternion - reassembling and then reading `.eulerAngles` back out a second time
(what the 0.3.11 version did) would have reintroduced the exact same bleed.

Also this build: the gauge-hide workaround (0.3.10/0.3.11) turned out to be solving a different
problem than reported - it correctly hides MultiHUD's 2D speedometer HUD, but the actual complaint
is the 3D dashboard gauge needles (visible in the cockpit) spinning/wobbling, a separate object
this mod has no visibility into yet. Added a new **"Log dashboard gauge diagnostics"** button in
the settings panel (next to the existing HUD-hide toggle) - press it the moment the wobble is
visible in-game, then send the resulting KSL log. It scans for a much wider set of dashboard-
related object names and logs each match's full hierarchy path plus whether a Camera component
sits anywhere in its ancestor chain, which should reveal whether the needle is parented under the
camera rig (a likely explanation) or something else entirely.

**0.3.11** - Two fixes from testing 0.3.10 against a real KSL log:
- **Gauge-hide workaround (0.3.10) never actually found anything to hide.** The log confirmed it:
  zero "Found N gauge object(s)" lines across a full session, even after MultiHUD itself logged
  finding its own speedometer at `KeepAlive(Clone)/UGUI/Root/Contexts/UIRaceFreerideMode/
  UIRaceSpeedometer/Speedometer`. That `KeepAlive(Clone)` name is a strong tell it's a
  `DontDestroyOnLoad` object, and the log also showed CarX loading several scenes "Additive"
  (`nfs_studio`, `Race`, `vp_parking`) - neither DontDestroyOnLoad objects nor additively-loaded
  scenes are covered by `SceneManager.GetActiveScene().GetRootGameObjects()`, which is what
  0.3.10's search used, so it was structurally guaranteed to find nothing regardless of naming.
  Switched to `Resources.FindObjectsOfTypeAll<GameObject>()` (filtered to `go.scene.IsValid()`),
  which walks every loaded GameObject in every scene plus DontDestroyOnLoad in one pass. This is
  also what was causing the reported gauge "flickering" when moving the camera - with nothing ever
  found, the gauges were never actually hidden, just reacting to every camera change as before.
- **Full 360-degree rotation - pitch and yaw are now unclamped.** `Max rotation offset` (previously
  10-120 degrees) was clamping every euler axis, including pitch/yaw, which meant looking far
  enough left/right or up/down would just stop rather than keep turning with you. Turning your
  whole body around (not just your neck) should be able to spin the camera continuously with no
  limit. Fixed in `HeadTrackState.GetRotationOffset()` by only clamping roll (tilting your head
  sideways) - `Quaternion.Euler` builds rotations from periodic sin/cos, so leaving pitch/yaw
  unclamped doesn't introduce any discontinuity or wrap-around glitch, even well past +-180
  degrees. The safety-clamp slider is relabeled "Max roll offset" to match its new, narrower scope.

**0.3.10** - Two changes based on the latest real-game feedback (a populated diagnostic log this
time, plus explicit priority calls):
- **Position tracking now supports room-scale, "walk around the car" movement, not just a small
  seat-lean.** The actual bug behind "I move 3 steps left in real life and the camera doesn't go
  with me": `Max position offset` defaulted to 0.5m and topped out at 1.5m on the slider, so any
  real movement past that was silently clamped down to almost nothing - the tracking itself was
  always working, it just had no room to move. Default is now 3.0m and the slider goes up to 10m.
  This applies via a one-time migration (`PositionRangeUpgraded`) so it takes effect even on
  installs that already have a saved 0.5m value from before - after that one bump, tuning the
  slider yourself always sticks. Applies everywhere (driving included), same as before - no
  mode-specific gating was added.
- **Gauge HUD "acting weird" - stopped fighting it, hid it instead.** Two direct fixes (0.3.7's
  Transform-decoupling, 0.3.8's revert-plus-matrix-override) didn't stop MultiHUD's gauge Canvas
  from visibly reacting to head movement/zoom, because that reaction is by design for a
  Screen-Space-Camera Canvas reading the real camera Transform/FOV - not a bug in this mod's own
  camera handling to begin with. Rather than keep fighting a Canvas this mod doesn't own, 0.3.10
  finds MultiHUD's gauge object(s) at runtime by name (`speedometer`/`tachometer`/`rpmgauge`,
  case-insensitive) and simply deactivates them while PhoneCam is enabled, reactivating them the
  instant it's turned off. On by default (`HideGaugesWhileTracking`, one-time-migrated the same
  way as the position range above) - a "Gauge HUD workaround" toggle and status label (how many
  objects were found) are in the settings panel if you'd rather leave the gauges alone.

**0.3.9** - Two fixes from the latest real-game feedback:
- **Zoom is now smoothed.** Scroll wheel / +/- keys used to snap the FOV offset instantly to its
  new value; now it eases toward that target every frame (frame-rate independent). A new "Zoom
  smoothing" slider controls how fast or slow that transition is (lower = smoother/slower,
  higher = snappier) - separate from "Zoom sensitivity," which still controls how many degrees
  each scroll notch/keypress adds.
- **Pitch/yaw swap - fixed directly instead of another mount-orientation guess.** The "Phone
  mount orientation" cycle button (0.3.7/0.3.8) never resolved the reported "look left moves the
  camera up, look right moves it down" bug, so it's removed entirely. In its place: the final
  head-tracking rotation offset's pitch and yaw are now unconditionally swapped right before
  being applied to the camera - a direct, transparent fix instead of a conjugation-based
  correction that wasn't working. Two small toggles, "Invert up/down look" and "Invert
  left/right look," are there as a one-click fix if either direction still ends up backwards
  after the swap.

**0.3.8** - Real-game test of 0.3.7 surfaced two problems with that build's approach, both
addressed here:
- **Zoom silently did nothing to the actual view, even though the gauge HUD visibly reacted to
  it.** That split is the tell: `fieldOfView` was being changed correctly (so anything reading
  that property directly, like the gauge Canvas's own scaling, responded), but Kino's Custom
  Camera mode almost certainly freezes `Camera.projectionMatrix` too - the same class of gotcha
  as the `worldToCameraMatrix` freeze found in 0.3.6, just for projection instead of view. Fixed
  by rebuilding `projectionMatrix` from `fieldOfView` explicitly and reassigning it every frame,
  the same way the view matrix already was.
- **The gauge HUD kept "acting weird" when looking around and zooming, even after 0.3.7 stopped
  writing the offset onto the camera's real Transform specifically to fix that.** Since it kept
  happening either way, that theory was wrong. The gauge HUD (from the separate MultiHUD mod) is
  a "UGUI" Canvas - almost certainly Unity's Screen-Space-Camera render mode, which is *designed*
  to read the camera's real Transform/fieldOfView directly so it stays glued to the screen.
  0.3.7's decoupling made the UI and the 3D world disagree with each other (UI tracking the stale
  Transform/FOV, 3D world rendered from a separately-computed pose) instead of fixing anything.
  0.3.8 goes back to writing the offset onto the real Transform/FOV (keeps every such system in
  sync automatically) while still rebuilding the view/projection matrices explicitly afterward, so
  Kino's Custom Camera mode still can't silently ignore the change either way.
- **Pitch/yaw axis swap still not fixed** - cycling the "Phone mount orientation" setting didn't
  resolve it. Rather than guess a third time, this build adds a diagnostic log line (every ~2s,
  alongside the existing camera heartbeat) printing the raw incoming ARKit euler angles, the
  post-correction Unity-space euler angles, and the current mount-roll setting - the next log
  capture (moving the phone through a pure look-left/right, then a pure look-up/down) should show
  exactly which axis LOTA's data is actually changing for each movement, turning this into a
  data-driven fix instead of another guess.

**0.3.7** - Confirmed via real-game log: 0.3.6's view-matrix override works, including in Kino's
Custom Camera mode - the head-tracked camera moves correctly now. Four follow-up fixes from that
same test session:
- **Pitch/yaw axis fix.** Tilting the phone up/down (and moving it up/down) was rotating/moving
  the camera left/right instead of up/down. Added a "Phone mount orientation" setting (cycles
  0/90/180/270 via a button in the menu) that compensates for how the phone happens to be
  physically mounted relative to the orientation LOTA's raw ARKit axes assume - applied
  identically to the incoming position and rotation data via proper rotation (quaternion
  conjugation, not a naive component swap) so it stays correct for compound head movements too.
  Defaults to 0 (unchanged behavior) - cycle it in-game until up/down and left/right map correctly.
- **Dashboard gauges no longer move with the head-tracked view.** The previous approach wrote the
  offset directly onto the camera's real `Transform`, which meant anything parented under the
  camera (like the gauge HUD) inherited the same rotation. 0.3.7 computes the offset pose in local
  variables instead and only ever touches the render's view/culling matrices - the camera's actual
  Transform is never modified, so nothing parented to it moves.
- **Camera now snaps back to normal when the mod is disabled (or before calibration).** Since the
  real Transform is no longer touched at all, there's nothing left to "undo" - `OnCameraPreCull`
  now also explicitly calls `ResetWorldToCameraMatrix()`/`ResetCullingMatrix()` in both cases so
  the view reverts to the game's own default immediately instead of staying stuck at the last
  head-tracked pose.
- **Privacy: IP addresses are now masked by default.** The last-sender IP, this PC's LAN IP, and
  the phone IP filter are shown digit-masked (e.g. `•••.•••.•.••`) unless a new "Show IP
  addresses" toggle (off by default) is switched on - keeps the settings panel safe to show on
  stream or in screenshots without exposing home network details.

**0.3.6** - Real-game test of 0.3.5 confirmed the handler runs, resolves the correct on-screen
camera, and applies last every frame - but in Kino's own "Custom Camera" mode (the mouse/keyboard
free-look mode) the view still didn't move at all. Best-evidenced explanation: that camera system
likely sets `Camera.worldToCameraMatrix` explicitly, which makes Unity ignore further Transform
changes for rendering purposes until `ResetWorldToCameraMatrix()` is called - a known Unity
behavior for exactly this kind of camera rig. This build rebuilds and re-assigns the view matrix
directly from the (now offset) Transform every frame, unconditionally, right after applying the
offset. This is mathematically identical to Unity's own default when nothing else is customizing
the matrix (so it should be a no-op in normal driving/replay/photo modes), and wins outright when
something else is. Not yet confirmed in a real play session - this is the best next guess given
`kino.dll` can't be inspected directly, not a confirmed root cause.

**0.3.5** - The SRP hook (0.3.4) got the callback firing again, and the diagnostic log confirmed
`GetActiveCamera()` was correctly resolving to the real render camera - but the offset still had
no visible effect on the camera view (a dashboard element moved, the outside view didn't). Root
cause: multicast events like `RenderPipelineManager.beginCameraRendering` call every subscriber in
registration order, so if CarX/Kino's own camera-follow logic subscribes to the same hook and runs
*after* this mod's handler, it silently overwrites whatever this mod just set, every frame. Fixed
two ways: (1) `LateUpdate()` now unsubscribes and immediately re-subscribes both camera hooks every
single frame, which moves this mod's handler to the end of the invocation list each time - it
always applies its offset last. (2) Targeting was simplified to stop trying to guess which
CarX/Kino system currently "owns" the camera; it now applies to whatever camera is actually
rendering to the screen that frame (skipping only offscreen cameras like reflection probes, and
orthographic ones like 2D HUD overlays), which is the direct path to a true override that works
identically in and out of Photo Mode rather than needing separate logic per mode.

**0.3.4** - Found and fixed the real cause of "receiving data + calibrated, but the camera never
moves": this game runs a Scriptable Render Pipeline (URP/HDRP), and `Camera.onPreCull` - the hook
this mod relied on since 0.1.0 - is a documented no-op under any SRP, full stop, regardless of
camera targeting. 0.3.2's fully-unconditional diagnostic logging came back with zero `[diag]`
lines across several play sessions with Enabled confirmed on, which is what confirmed it (rather
than a targeting miss). Fix: also subscribe to `RenderPipelineManager.beginCameraRendering`, the
SRP equivalent hook with the same timing guarantee. Both hooks stay subscribed; whichever one the
project's actual pipeline calls is the one that does the work.

**0.3.3** - Adds an in-game updater. New "Update" section at the top of the PhoneCam menu:
**Check for Update** queries this repo's latest GitHub Release, and if it's newer than the
installed version, **Download & Install** fetches the `PhoneCam.ksm` asset straight into
`kino\mods\` - no more manually visiting GitHub and copying the file over. Also wired up (as a
parallel, no-code-needed path): KSL's own built-in Automatic updater, same mechanism your other
mods already use. Important caveat either way: a game restart is still required to actually load
the new code - see "Publishing an update" above for why, and for the release-publishing steps
this depends on.

**0.3.2** - Diagnostic build, round 2. The 0.3.1 log capture showed *zero* `[diag]` lines despite
a full play session (car loaded, camera modes engaged, calibration re-run several times) - that's
ambiguous, since 0.3.1 only logged diagnostics while the Enabled toggle was on. This build removes
that gate so `LogCameraDiagnostics` always runs off `Camera.onPreCull`, regardless of Enabled, so
the next log will show definitively whether `onPreCull` ever fires at all for Kino's custom camera.
Also added: a log line whenever the Enabled toggle is flipped from the settings panel, and the
startup log now states the Enabled state it loaded with, so a log dump gives a clear timeline of
what was actually on/off during a test session.

**0.3.1** - Diagnostic build. Tracking still didn't move the camera in testing even after 0.3.0's
`CameraSwitch` fix, and the KSL log showed Kino's own camera system (`kino.dll`) is active
("Custom camera set to True") - a separate system from anything in `Assembly-CSharp.dll`.
`kino.dll` has no readable .NET metadata (confirmed obfuscated), so it can't be inspected the
same way. This build adds logging instead of another guess: every distinct camera Unity
actually renders through gets logged once (see `LogCameraDiagnostics` in `HeadTrackMod.cs`),
plus a heartbeat every ~2s showing what `GetActiveCamera()` currently resolves to. The next
`kino/output.log` capture should show exactly which camera is real, so the fix can target it
directly instead of guessing again.

**0.3.0**
- Fixed: tracking could fail to move the camera in modes other than Photo Mode too. CarX turns
  out to manage cockpit/follow/rear/static/replay/photo cameras through its own public
  `CameraSwitch` singleton (confirmed via `Assembly-CSharp.dll`) rather than relying on Unity's
  `MainCamera` tag consistently. `GetActiveCamera()` now asks `CameraSwitch.instance.FindActiveCamera()`
  first (works for every mode CarX itself tracks), falls back to the Photo-Mode-specific fix
  from 0.2.0, then to `Camera.main` as a last resort. See "Known limitations" for the full
  writeup.
- Added: the PC LAN IP field is now editable (persisted) in case auto-detect picks the wrong
  network adapter - "Refresh IP" goes back to auto-detect.
- Added: an optional "Phone's IP" field - when set, the mod only accepts OSC packets from that
  exact address, ignoring anything else hitting the port.

**0.2.0**
- Fixed: head tracking did nothing while in Photo Mode - it uses its own dedicated camera,
  separate from `Camera.main` (see "Known limitations" below for the full explanation).
- Added: the mod's settings panel now shows this PC's LAN IP (with a Refresh button) and the
  last OSC sender's IP, so pointing LOTA at the right address is easier and verifiable.
- Added: a "Calibrated: yes/no" status label so it's unambiguous whether F9 has been pressed.

**0.1.0** - Initial release: head tracking, zoom/FOV control, cockpit clipping guard,
Photo-Mode-gated "How to use" help panel.

## How it works

1. **LOTA** (free, official App Store app, no subscription) runs on your iPhone and streams
   your phone's ARKit camera pose over OSC/UDP: `/lota/camera/position` (x, y, z) and
   `/lota/camera/rotation` (quaternion x, y, z, w), about 30 times a second.
2. This mod opens a UDP socket in CarX, parses those OSC packets itself (no external OSC
   library — see `src/Osc/`), and converts the pose from ARKit's coordinate convention
   (right-handed, camera looks down -Z) into Unity's (left-handed, +Z forward) — see
   `src/Tracking/ArKitConversion.cs`.
3. Press a hotkey (default **F9**) to record your current head position as "neutral." Every
   frame after that, the mod computes how far your head has moved/rotated from that neutral
   pose, smooths and scales it, and adds it as an offset directly to the active camera's
   transform via `Camera.onPreCull` — after CarX's own camera logic has already run for that
   frame, so it works no matter which camera mode (cockpit, bumper, chase) CarX is using.
4. Two extra camera features, built the same "apply after the game" way so they compose with
   whatever CarX is already doing rather than fighting it:
   - **Zoom / FOV control** — mouse wheel (or `+`/`-`, reset on `F10`) nudges the camera's
     field of view in or out, independent of head tracking.
   - **Cockpit clipping guard** (off by default) — raycasts along the head-offset direction
     each frame and clamps the offset short of anything it hits, so leaning in doesn't push
     the camera through the dashboard or seat. Needs its layer mask tuned against CarX's
     actual cockpit collision geometry — see "Known limitations" below.

## Requirements

- An iPhone that supports ARKit (LiDAR is not required for camera pose tracking itself —
  that's standard ARKit visual-inertial tracking; LiDAR mainly helps LOTA's other features).
- [LOTA — LiDAR Over the Air](https://apps.apple.com/app/id6760984302) installed on the phone.
- CarX Drift Racing Online with [KSL](https://github.com/trbflxr/ksl) installed (the current
  community mod loader for this game — see their install guide).
- Windows PC with the .NET SDK (or Visual Studio / Rider) to build this project. Building
  has to happen on your own machine — it needs `KSL.API.dll` and
  `UnityEngine.CoreModule.dll` copied out of your actual CarX install.

## Build

Verified directly against a real install at
`X:\SteamLibrary\steamapps\common\CarX Drift Racing Online\` — the actual game folder is
named `Drift Racing Online_Data`, not `CarXDriftRacingOnline_Data` as generic docs assume.
Adjust the path below to match your own install.

1. Copy from `<CarX install>\Drift Racing Online_Data\Managed\`:
   - `UnityEngine.CoreModule.dll`
   - `UnityEngine.PhysicsModule.dll` (used by the cockpit clipping guard's raycasts)
   - `UnityEngine.InputLegacyModule.dll` (used by `UnityEngine.Input` for scroll-wheel zoom)
   - `UnityEngine.UnityWebRequestModule.dll` (used by the in-game update checker)
   - `UnityEngine.JSONSerializeModule.dll` (used to parse GitHub's release response)
   - `Assembly-CSharp.dll` (the game's own code - used for the `UIPhotoModeContext` Photo Mode check, see below)

   into this project's `libs\` folder (create it if it doesn't exist).
2. **`KSL.API.dll` was not present in that `Managed\` folder** on the install I checked, even
   with KSL already installed and run at least once (its logs existed). The loader's own core
   assembly lives at `<CarX install>\kino\mods\kino.dll`, but a quick inspection couldn't
   confirm plain-text type names in it (possibly obfuscated), so I can't be sure it's a safe
   compile-time reference. Get a clean one from the official
   [KSL SDK](https://github.com/trbflxr/ksl_sdk) instead — that's what it's for — and copy its
   `KSL.API.dll` into `libs\` too.
3. `dotnet build -c Release` (or open `HeadTrackARKit.csproj` in Visual Studio/Rider and build
   the Release configuration).
4. The mod is registered with KSL under the name **PhoneCam** (that's what shows up in KSL's
   Control Panel and in-game, even though the project/repo is still called HeadTrackARKit).
   A Release build automatically signs and drops a distributable `PhoneCam.ksm` straight into
   `<CarX install>\kino\mods\` via a `PostBuildEvent` in the `.csproj` that calls KSL's own
   `maykr` tool (`tools\maykr.exe`) with the build key generated for this mod,
   `<CarX install>\kino\dev\PhoneCam_maykr.kmc`. Don't rename that `.kmc` file — it's tied to
   the "PhoneCam" registration. This step is skipped automatically (no error) if `tools\maykr.exe`
   isn't present, e.g. on a machine that's just checking the code out to read it.
5. That's it — no manual copy step needed anymore. `kino\mods\` already held several other raw
   `.dll` mods and `.ksm` packages before this one, confirming KSL loads straight from there.

## Publishing an update (so the in-game updater / KSL's own updater can find it)

The mod's signing key (`PhoneCam_maykr.kmc`) lives only on your PC and should never be uploaded
anywhere, so GitHub can't build/sign releases itself — the signed `.ksm` always has to be built
locally first, same as above. Once you have it:

1. Build normally (`dotnet build -c Release`) — this drops a freshly-signed `PhoneCam.ksm` into
   `<CarX install>\kino\mods\`.
2. On GitHub, go to `https://github.com/Chaoz75/PhoneCam` → **Releases** → **Draft a new
   release**.
3. **Tag**: match the version you just bumped in `HeadTrackMod.cs` (both the `KSLMeta` attribute
   and the `CurrentVersion` const), prefixed with `v` — e.g. `v0.3.3`.
4. **Title**: whatever's clear to you, e.g. `v0.3.3`.
5. Drag in the built `PhoneCam.ksm` (from `<CarX install>\kino\mods\PhoneCam.ksm`) as a release
   asset. The filename must stay exactly `PhoneCam.ksm` — that's what both updaters look for.
6. Click **Publish release**.

That's the only manual step per version — from there:
- **In-game updater**: open the PhoneCam menu → **Check for Update** → **Download & Install**
  fetches that release's `PhoneCam.ksm` straight into `kino\mods\`, no browser needed. A restart
  is still required to actually load the new code (see note below).
- **KSL's own updater**: if PhoneCam's Updater type is set to **Automatic** in KSL's Control
  Panel (linked to this GitHub repo), it self-updates the same way automatically at the next
  game launch, with no button-clicking needed at all.

**Neither of these can apply an update without a restart.** Once a mod's compiled code is loaded
into the running game process, there's no supported way in Mono/.NET (or Unity generally) to
unload and reload just that one assembly — every other KSL/BepInEx mod you have installed has
this same limitation. What both updaters *do* remove is the manual "find the file on GitHub,
download it, copy it into `kino\mods`" busywork — the new version is just sitting there ready
the next time you happen to restart anyway.

## Using it

1. In CarX, open the mod's settings (via KSL's mod menu) first and note **"This PC's LAN IP"**
   shown there. It's auto-detected but editable — if it picked the wrong network adapter (e.g.
   a VPN's virtual adapter instead of your real Wi-Fi), just type the correct one in; **Refresh
   IP** re-runs auto-detection. There's also an optional **"Phone's IP"** field — leave it blank
   to accept data from any device, or fill in your phone's IP to have the mod ignore anything
   else that happens to hit the port.
2. On your iPhone, open LOTA. Stay on the main camera page (any mode except **Motion** —
   camera-pose OSC is deliberately suppressed in Motion mode). Tap the status bar pill to
   open **Transmission Settings**, enable **OSC**, and type in the IP from step 1 and the port
   shown in the mod's settings (default `9000`, changeable there). Make sure the phone and PC
   are on the same Wi-Fi network.
3. Tap the shutter with **STREAM** selected to start streaming.
4. Back in CarX's mod settings, confirm **"Status: receiving data"** — the panel also shows
   **"Last sender IP"** once a packet arrives, so you can double check it's actually your phone
   and not some other device on the network hitting that port.
5. Sit in your normal driving position, holding the phone facing you (or mounted), then press
   **F9** to set neutral. The panel shows **"Calibrated: yes/no"** so it's obvious whether this
   step has actually happened — nothing moves until it says yes. Re-press F9 any time you shift
   position.
6. Tune position/rotation sensitivity, smoothing, and the safety clamps (max offset) from the
   in-game settings panel to taste.

### Controls (all rebindable via KSL's own hotkey settings)

| Action | Default key |
| --- | --- |
| Set neutral head position | `F9` |
| Zoom in / out | Mouse wheel, or `=` / `-` |
| Reset zoom | `F10` |

### Cockpit clipping guard

Off by default. Turn it on in the mod's settings panel once you've confirmed head tracking
itself feels right — it raycasts from the camera toward the direction you're leaning and stops
the offset short of whatever it hits. The layer mask defaults to "everything" (`-1`) so it's
obviously doing something the moment you enable it; narrow it down to just the cockpit/interior
collision layer once you can see what it's catching in-game (e.g. it may initially also block
against the car's own exterior shell or track geometry depending on how CarX's colliders are
laid out — that's exactly the kind of detail only visible with the real game running).

## "How to use" help panel (Photo Mode only)

The mod's KSL menu shows a "How to use" button only while CarX's **Photo Mode** is the
active screen. Outside Photo Mode it shows a label telling you to enter Photo Mode instead.

This is driven by `UIPhotoModeContext.isActive` - confirmed by directly inspecting
`Assembly-CSharp.dll`: the class and its `isActive` property (inherited from a `BaseContext`
base class) are both public, so no reflection into private game internals was needed. One
caveat: it's unverified whether `UIPhotoModeContext`'s instance exists in the scene before
you've entered Photo Mode at least once in the current session - if the button doesn't show
up the first time, going into and back out of Photo Mode once should make it available from
then on.

## Project layout

```
HeadTrackARKit.csproj      Build file (net472, references KSL.API.dll + UnityEngine.CoreModule.dll)
src/
  HeadTrackMod.cs           KSL BaseMod entry point: config, UI, hotkey, Camera.onPreCull hook
  HeadTrackConfig.cs        Persisted settings (Kino.Config interface)
  Osc/
    OscParser.cs            Dependency-free OSC 1.0 message parser
    OscUdpReceiver.cs        Background UDP listener -> thread-safe queue drained on main thread
  Tracking/
    ArKitConversion.cs      ARKit (RH, -Z fwd) -> Unity (LH, +Z fwd) pose conversion
    HeadTrackState.cs       Calibration, smoothing, sensitivity, clamping, delta computation
libs/                       You put KSL.API.dll / UnityEngine.CoreModule.dll here (not shipped)
```

## Known limitations / next steps

- **CarX manages cameras through its own `CameraSwitch` system, not Unity's `MainCamera` tag.**
  Confirmed by directly inspecting `Assembly-CSharp.dll`'s metadata:
  - `UIPhotoModeContext` holds a private `m_camera` field (type `UnityEngine.Camera`, driven
    internally by a `CinemachineVirtualCamera`) that's a separate object from whatever's tagged
    `MainCamera`.
  - More broadly, `CameraSwitch` (a public singleton, `CameraSwitch.instance`) is CarX's own
    manager for every camera mode - its `ECameraType` enum lists Race, Follow, Replay, and
    PhotoSession, and its public `FindActiveCamera()` method returns whichever
    `CarX.BaseCamera`-derived controller (`CockpitCamera`, `FollowCamera`, `RearCamera`,
    `StaticCamera`, etc.) is currently active. Since `BaseCamera` extends `MonoBehaviour`, the
    real `Camera` component sits on the same GameObject.

  `HeadTrackMod.GetActiveCamera()` resolves the target camera in three layers:
  `CameraSwitch.instance.FindActiveCamera()` first (covers every mode CarX itself tracks, no
  reflection needed since everything involved is public), then the Photo-Mode-specific
  reflection fix as a fallback, then `Camera.main` as a last resort. This covers cockpit,
  follow/hood, rear, static, replay, and photo modes.
- **Found the real reason tracking still didn't move the camera in 0.3.0-0.3.3, despite correct
  camera targeting: this game runs a Scriptable Render Pipeline (URP or HDRP), not Unity's
  legacy Built-in Render Pipeline.** `Camera.onPreCull` - the hook this mod applied its offset
  through - only ever fires under the legacy pipeline; it's a documented no-op under any SRP,
  regardless of which camera is targeted or whether it's enabled. The KSL log confirmed this
  indirectly (`Enabled volume override` / `Enabled sky override` - Volume Framework terms,
  SRP-only), and directly: 0.3.2's diagnostic logging was made fully unconditional and still
  produced zero `[diag]` lines across multiple full play sessions with Enabled confirmed on -
  the callback simply never fired, no matter what.

  Fixed in 0.3.4 by also subscribing to `RenderPipelineManager.beginCameraRendering` - the SRP
  equivalent hook, same per-camera/right-before-render timing guarantee as `onPreCull`. Both
  subscriptions are kept active; only the one matching the project's actual pipeline will ever
  call back, so this doesn't need to guess which one applies.
- **Even with the SRP hook firing and resolving the correct camera, the offset had no visible
  effect on the camera view (0.3.4 real-game test).** Cause: multicast events invoke subscribers
  in registration order - if CarX/Kino's own camera logic also subscribes to
  `beginCameraRendering`/`onPreCull` and runs *after* this mod's handler in a given frame, it
  overwrites whatever this mod just set, silently, every frame. Fixed in 0.3.5 by having
  `LateUpdate()` unsubscribe-then-resubscribe both hooks every single frame, which always moves
  this mod's handler to the end of the invocation list - it now applies its offset last, no
  matter what else is also touching the camera that frame. Targeting was also simplified at the
  same time: rather than asking CarX/Kino which system currently "owns" the camera, it now applies
  to whichever camera is actually rendering on-screen that frame (skipping only offscreen cameras
  like reflection probes and orthographic ones like 2D HUD overlays) - the goal being one code
  path that behaves identically whether you're in Photo Mode or not, rather than special-casing
  each mode. Not yet confirmed against Photo Mode specifically in a real play session (the last
  log capture never entered it) - that's the next thing to verify.
- **Still no visible movement, specifically confirmed in Kino's own "Custom Camera" mode (the
  mouse/keyboard-controlled free-look mode), even with 0.3.5's last-write-every-frame fix in
  place.** Since the diagnostic log rules out both a targeting miss and a losing-the-race-to-write
  problem, the remaining likely cause is that this camera system sets
  `Camera.worldToCameraMatrix` explicitly - once that's done, Unity renders using that matrix and
  ignores the Transform for that camera until `ResetWorldToCameraMatrix()` is called, regardless
  of how late something else writes to `transform.position`/`rotation`. 0.3.6 rebuilds and
  re-assigns the view matrix directly every frame instead of relying on the Transform alone. This
  is inferred, not confirmed - `kino.dll` still can't be inspected directly - so it needs a real
  retest, specifically in that Custom Camera mode, to know if it worked.
- **Confirmed working (0.3.7): the 0.3.6 view-matrix override does move the camera in Kino's
  Custom Camera mode**, per a real play-session log. Same test surfaced three follow-up issues,
  all addressed in 0.3.7 - see the changelog entry above for each: a pitch/yaw axis swap (now a
  cycle-able "phone mount orientation" setting), the dashboard gauge HUD moving along with the
  head-tracked view (now fixed by never writing to the camera's real Transform, only its render
  matrices), and the camera not reverting when disabled (now explicitly reset).
- **Phone mount orientation cycling (0.3.7/0.3.8) is gone as of 0.3.9 - replaced with a direct
  fix.** It never resolved the reported "look left moves the camera up / look right moves it
  down" bug across two real-game tests, so rather than keep guessing at a conjugation-based
  correction, 0.3.9 unconditionally swaps the final rotation offset's pitch and yaw right before
  applying it to the camera (see `HeadTrackMod.FixLookDirection`), with "Invert up/down look" and
  "Invert left/right look" toggles as a one-click correction if either direction is still
  backwards after the swap. The axis-mapping diagnostic log line is still there (now printing the
  incoming euler vs. the final post-swap offset) in case another round of tuning is needed.
- **Kino's Custom Camera mode appears to freeze `Camera.projectionMatrix` independent of
  `fieldOfView`, in addition to freezing `worldToCameraMatrix` (0.3.6).** Real-game evidence: zoom
  visibly changed the gauge HUD's own scaling (which reads `fieldOfView` directly) but never the
  actual rendered view. Fixed in 0.3.8 by rebuilding and reassigning `projectionMatrix` from
  `fieldOfView` explicitly every frame, mirroring the existing `worldToCameraMatrix` fix.
- **0.3.7's "stop writing to the camera's real Transform" change was based on an incorrect
  theory and was reverted in 0.3.8.** The gauge HUD kept reacting even without any Transform
  writes, which ruled out "the HUD is parented under the camera" as the cause. The HUD (MultiHUD,
  a separate mod) is a UGUI Canvas, almost certainly Screen-Space-Camera mode, which needs the
  camera's real Transform/FOV to stay in sync by design - decoupling from it made things worse,
  not better. 0.3.8 writes to the real Transform/FOV again (keeping every such system in sync)
  while still separately rebuilding the view/projection matrices to guarantee Kino's Custom
  Camera mode can't ignore the change.
- **Gauge HUD desync: the hide-instead-of-fix approach (0.3.10) was the right call, but its object
  search was broken - fixed in 0.3.11.** MultiHUD's gauge Canvas is a separate mod's
  Screen-Space-Camera UI, which is *designed* to read the real camera Transform/FOV every frame -
  any camera movement or FOV change is always visible to it, the same way it would be to any other
  Screen-Space-Camera UI in the scene, so hiding it outright rather than fighting the sync is still
  the plan. But 0.3.10's search (`SceneManager.GetActiveScene().GetRootGameObjects()`) never found
  the object at all - it's parented under what's almost certainly a `DontDestroyOnLoad` root
  (`KeepAlive(Clone)`), and CarX also loads several scenes additively, neither of which that API
  covers. 0.3.11 switches to `Resources.FindObjectsOfTypeAll<GameObject>()`, which finds it
  regardless of which scene (or DontDestroyOnLoad) it lives in - see `HeadTrackMod.
  RefreshGaugeObjects`/`SetGaugesHidden`. Trade-off unchanged: gauges aren't visible at all while
  tracking is on, which is why it's a toggle (`HideGaugesWhileTracking`), not a forced behavior.
- **Rotation was capped well short of a full turn (0.3.11), then glitched when looking behind you
  (0.3.12's atan2 fix), then flipped upside down after a couple of full spins - resolved in
  0.3.13.** Three layers, each real-game tested: (1) 0.3.11 removed the pitch/yaw clamp that was
  stopping rotation partway through a turn. (2) 0.3.12 caught a second bug - near yaw=+-180
  degrees, XYZ Euler decomposition is inherently unstable and was bleeding rotation between axes
  (a pure yaw turn producing a spurious large roll value) - fixed with `atan2` extraction from the
  delta's forward vector instead (see `HeadTrackState.GetRotationOffsetEuler`). (3) 0.3.13 found a
  third, root-cause bug: `HeadTrackMod.FixLookDirection`'s pitch/yaw swap (added 0.3.9, validated
  against the old unstable extraction) was routing the large, unbounded "turning left/right" value
  into the applied rotation's *pitch* slot - and a large pitch is a literal upside-down camera,
  regardless of how clean the input is. Removed the swap; pitch/yaw now route straight through, so
  unlimited turning stays on the flip-safe yaw axis through any number of full spins.
- **The "gauges react to camera movement" report turned out to be about the 3D dashboard gauge
  cluster (the physical needles/dials visible in the cockpit), not MultiHUD's 2D speedometer HUD -
  still open, needs more diagnosis.** 0.3.10/0.3.11 correctly found and can now hide MultiHUD's UI
  overlay (`Speedometer`/`SpeedometerBoundings`/`UIRaceSpeedometer`, confirmed via log), but that
  was solving the wrong problem - the user clarified afterward they meant the in-car dashboard
  gauges moving, a different object entirely that this mod hasn't investigated yet.
- **Position tracking range: the "can't walk around" report was a clamp, not a broken pipeline.**
  `HeadTrackState.GetPositionOffset()` was already computing a correct, calibration-relative
  translation delta every frame - `Max position offset` (0.5m default, 1.5m slider ceiling) was
  just clamping any real movement larger than a small seat-lean down to a few centimeters, which
  is indistinguishable from "not working" in practice. 0.3.10 raises the default to 3.0m and the
  slider ceiling to 10m (with a one-time migration so existing saved configs pick up the new
  default too) - no changes were needed to the actual tracking/calibration math itself.
- **`Unable to save config 'PhoneCam.ksc': NullReferenceException`** appears repeatedly in the
  logs, correlated with rapid Enabled-toggle/F9/zoom-reset actions. The stack trace is entirely
  inside KSL's own obfuscated internals, not this mod's code, so there's nothing to fix on this
  end - flagging it as a known, non-fatal noise source rather than chasing it further.
- The zoom offset and head-tracking offset both add on top of whatever CarX outputs that
  frame (including its own shake/FOV effects) rather than replacing it — this is deliberate,
  but means extreme zoom + a game effect that also changes FOV heavily could stack oddly.
- **Cockpit clipping guard is unverified against real geometry.** The raycast logic itself is
  straightforward Unity `Physics.Raycast`, but which layer(s) CarX's cockpit/interior
  collision lives on isn't something confirmed against the real game yet. Leave it off until
  you've tried it, then narrow the layer mask down from "everything."

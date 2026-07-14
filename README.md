# HeadTrackARKit — iPhone head tracking for CarX Drift Racing Online

Uses an iPhone's ARKit camera tracking (streamed over the network by the free **LOTA** app)
to drive a live head-tracking offset on top of CarX's own in-game camera — similar in spirit
to TrackIR. This is an original implementation built for this project; it does not use or
depend on the `RealCam ARKit` plugin found in `ModAssetto/` (that file is a separate, paid
Assetto Corsa–only plugin and was never opened, run, or decompiled — it's unrelated to this
mod other than "phone streams ARKit data over OSC" being the same general idea).

## Changelog

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
- The zoom offset and head-tracking offset both add on top of whatever CarX outputs that
  frame (including its own shake/FOV effects) rather than replacing it — this is deliberate,
  but means extreme zoom + a game effect that also changes FOV heavily could stack oddly.
- **Cockpit clipping guard is unverified against real geometry.** The raycast logic itself is
  straightforward Unity `Physics.Raycast`, but which layer(s) CarX's cockpit/interior
  collision lives on isn't something confirmed against the real game yet. Leave it off until
  you've tried it, then narrow the layer mask down from "everything."

# HeadTrackARKit — iPhone head tracking for CarX Drift Racing Online

Uses an iPhone's ARKit camera tracking (streamed over the network by the free **LOTA** app)
to drive a live head-tracking offset on top of CarX's own in-game camera — similar in spirit
to TrackIR. This is an original implementation built for this project; it does not use or
depend on the `RealCam ARKit` plugin found in `ModAssetto/` (that file is a separate, paid
Assetto Corsa–only plugin and was never opened, run, or decompiled — it's unrelated to this
mod other than "phone streams ARKit data over OSC" being the same general idea).

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
- Windows PC with the .NET SDK (or Visual Studio / Rider) to build this project. **Building
  has to happen on your own machine** — it needs `KSL.API.dll` and
  `UnityEngine.CoreModule.dll` copied out of your actual CarX install, which obviously isn't
  available in this sandboxed environment. Everything in this repo has been written and
  logic-checked here, but not yet compiled or run against the real game.

## Build

Verified directly against a real install at
`X:\SteamLibrary\steamapps\common\CarX Drift Racing Online\` — the actual game folder is
named `Drift Racing Online_Data`, not `CarXDriftRacingOnline_Data` as generic docs assume.
Adjust the path below to match your own install.

1. Copy from `<CarX install>\Drift Racing Online_Data\Managed\`:
   - `UnityEngine.CoreModule.dll`
   - `UnityEngine.PhysicsModule.dll` (used by the cockpit clipping guard's raycasts)
   - `UnityEngine.InputLegacyModule.dll` (used by `UnityEngine.Input` for scroll-wheel zoom)
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

## Using it

1. On your iPhone, open LOTA. Stay on the main camera page (any mode except **Motion** —
   camera-pose OSC is deliberately suppressed in Motion mode). Tap the status bar pill to
   open **Transmission Settings**, enable **OSC**, set the destination IP to your PC's LAN
   address and the port to match this mod's (default `9000`, changeable in the mod's
   settings panel in-game). Make sure the phone and PC are on the same Wi-Fi network.
2. Tap the shutter with **STREAM** selected to start streaming.
3. In CarX, open the mod's settings (via KSL's mod menu) and confirm "Status: receiving
   data."
4. Sit in your normal driving position, holding the phone facing you (or mounted), then press
   **F9** to set neutral. Re-press it any time you shift position.
5. Tune position/rotation sensitivity, smoothing, and the safety clamps (max offset) from the
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

- **Not yet compiled against the real game.** The OSC wire-format parsing was verified
  independently (byte-for-byte logic check, see chat history), but `HeadTrackMod.cs` itself
  has not been built or run inside CarX yet, since that requires Windows + your actual game
  files. If you hit build errors after adding the real `KSL.API.dll`, they're likely small
  API-shape mismatches (KSL's exact method signatures were sourced from their public docs,
  not from a downloaded SDK) — send me the compiler errors and I'll fix them.
- **`Camera.onPreCull` targets `Camera.main`**, i.e. whatever camera is tagged `MainCamera`
  in the scene — Unity's default for a game's primary camera. If CarX's in-car camera isn't
  tagged that way, the hook won't find it. If you share your CarX install folder (or just
  `Assembly-CSharp.dll` from its `Managed` folder), I can inspect the actual camera
  controller and wire this up precisely instead of relying on the tag convention.
- The zoom offset and head-tracking offset both add on top of whatever CarX outputs that
  frame (including its own shake/FOV effects) rather than replacing it — this is deliberate,
  but means extreme zoom + a game effect that also changes FOV heavily could stack oddly.
  Worth a look once you can test in-game.
- **Cockpit clipping guard is unverified against real geometry.** The raycast logic itself is
  straightforward Unity `Physics.Raycast`, but which layer(s) CarX's cockpit/interior
  collision lives on isn't something I could determine without the game running. Leave it
  off until you've tried it, then narrow the layer mask down from "everything."

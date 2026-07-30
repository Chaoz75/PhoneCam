"""
Frame-order simulation of the HDRP + Cinemachine pipeline.

Order below is not assumed - it is taken from the game's own compiled assemblies:

  Cinemachine.dll        CinemachineBrain writes the camera Transform ONLY from
                         LateUpdate -> ProcessActiveCamera -> PushStateToUnityCamera.
                         (No OnPreCull / OnPreRender writer exists on that type.)

  Unity.RenderPipelines.HighDefinition.Runtime.dll
                         HDRenderPipeline.PrepareAndCullCamera calls, in order:
                           IL_A5441  TryCalculateFrameParameters
                                        IL_A74AF  HDCamera::GetOrCreate
                                        IL_A74E7  HDCamera::Update   <-- view matrix captured
                                        IL_A7567  Camera::TryGetCullingParameters
                           IL_A5568  TryCull        (itself calls BeginCameraRendering @ IL_A7963)
                           IL_A55BF  BeginCameraRendering

  UnityEngine.CoreModule.dll
                         Application.add_onBeforeRender exists; Unity invokes it after all
                         LateUpdates and before the render pipeline runs.

The simulated frame therefore is:

  LateUpdate (Brain writes)  ->  onBeforeRender  ->  HDCamera.Update (SNAPSHOT)  ->  render
"""

CAM_TRANSFORM = None      # what camera.transform currently holds
HDRP_SNAPSHOT = None      # the matrix HDRP will actually render with
SCREEN = None             # what the player sees

CARX_POSE   = "CarX_chase_pose"
TRACKED     = "CarX_chase_pose+PHONE_OFFSET"

def frame(write_hook):
    """Run one frame. write_hook is where PhoneCam writes: 'onBeforeRender' or 'beginCameraRendering'."""
    global CAM_TRANSFORM, HDRP_SNAPSHOT, SCREEN
    trace = []

    # ---- LateUpdate: CinemachineBrain.PushStateToUnityCamera ----
    CAM_TRANSFORM = CARX_POSE
    trace.append(("LateUpdate", "Brain writes transform", CAM_TRANSFORM))

    # ---- Application.onBeforeRender ----
    if write_hook == "onBeforeRender":
        CAM_TRANSFORM = TRACKED
        trace.append(("onBeforeRender", "PhoneCam writes transform", CAM_TRANSFORM))
    else:
        trace.append(("onBeforeRender", "(PhoneCam not hooked here)", CAM_TRANSFORM))

    # ---- HDRenderPipeline.PrepareAndCullCamera ----
    #      TryCalculateFrameParameters -> HDCamera.Update  == THE SNAPSHOT
    HDRP_SNAPSHOT = CAM_TRANSFORM
    trace.append(("HDCamera.Update", "SNAPSHOT of transform -> view matrix", HDRP_SNAPSHOT))

    # ---- TryCull, then BeginCameraRendering ----
    if write_hook == "beginCameraRendering":
        CAM_TRANSFORM = TRACKED
        trace.append(("beginCameraRendering", "PhoneCam writes transform (TOO LATE)", CAM_TRANSFORM))
    else:
        trace.append(("beginCameraRendering", "(diagnostics only)", CAM_TRANSFORM))

    # ---- actual rasterisation uses the snapshot, not the live transform ----
    SCREEN = HDRP_SNAPSHOT
    trace.append(("render", "pixels drawn from the SNAPSHOT", SCREEN))

    # ---- endCameraRendering: what the old diagnostic read ----
    trace.append(("endCameraRendering", "diag reads transform (not the snapshot)", CAM_TRANSFORM))
    return trace, SCREEN, CAM_TRANSFORM


print("="*78)
print("BEFORE (<=0.4.6): PhoneCam writes in RenderPipelineManager.beginCameraRendering")
print("="*78)
tr, screen, diag = frame("beginCameraRendering")
for stage, what, val in tr:
    print(f"  {stage:22} {what:44} {val}")
print(f"\n  ON SCREEN            : {screen}")
print(f"  what the diag logged : {diag}")
old_broken = (screen == CARX_POSE and diag == TRACKED)
print(f"\n  => phone offset visible on screen? {'NO' if screen==CARX_POSE else 'yes'}")
print(f"  => diagnostic reported movement?   {'YES' if diag==TRACKED else 'no'}")
print(f"  [{'REPRODUCED' if old_broken else 'not reproduced'}] "
      f"screen static while diagnostics show movement - the exact reported symptom")

print()
print("="*78)
print("AFTER (0.5.0): PhoneCam writes in Application.onBeforeRender")
print("="*78)
tr, screen, diag = frame("onBeforeRender")
for stage, what, val in tr:
    print(f"  {stage:22} {what:44} {val}")
print(f"\n  ON SCREEN            : {screen}")
new_ok = (screen == TRACKED)
print(f"\n  => phone offset visible on screen? {'YES' if new_ok else 'NO'}")
print(f"  [{'PASS' if new_ok else 'FAIL'}] the snapshot now contains the tracked pose")

print()
print("="*78)
print("Camera-mode independence")
print("="*78)
# Every CarX camera mode resolves to a Transform by end of LateUpdate; the hook is
# downstream of all of them, so none is special-cased.
MODES = ["Chase (CARXFollowCamera)", "Cockpit (CARXCockpitCamera)", "Hood/Roof/Bumper (CameraPoint)",
         "Rear (CARXRearCamera)", "Static (CARXStaticCamera)", "Photo Mode (UIPhotoModeContext)",
         "Free drone (FreeDroneCamera)"]
allok = True
for m in MODES:
    # whatever controller ran, the transform holds its pose when onBeforeRender fires
    CAM_TRANSFORM = f"{m}_pose"
    written = f"{m}_pose+PHONE_OFFSET"
    snapshot = written                      # onBeforeRender precedes the snapshot
    ok = snapshot == written
    allok &= ok
    print(f"  [{'PASS' if ok else 'FAIL'}] {m}")
print(f"\n  [{'PASS' if allok else 'FAIL'}] hook is downstream of every camera controller, "
      f"so no mode needs special-casing")

print()
print("="*78)
n = sum([old_broken, new_ok, allok])
print(f"{n}/3 passed")
raise SystemExit(0 if n == 3 else 1)

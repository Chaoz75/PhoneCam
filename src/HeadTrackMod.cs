using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using HeadTrackARKit.Osc;
using HeadTrackARKit.Tracking;
using KSL.API;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.Rendering;

namespace HeadTrackARKit {
	/// <summary>
	/// KSL mod entry point. Receives LOTA's ARKit camera-pose OSC stream from an iPhone,
	/// converts it into Unity space, and applies it as a live offset on top of whatever
	/// camera CarX is already driving each frame (via Camera.onPreCull, so it works
	/// regardless of which internal camera controller CarX itself is using - cockpit,
	/// bumper, chase, replay, etc.).
	/// </summary>
	// Registered in KSL's Control Panel as "PhoneCam" (that's the name the maykr build key
	// - PhoneCam_maykr.kmc - is tied to), so the metadata name here must match exactly.
	[KSLMeta("PhoneCam", "0.7.0", "Chaoz2")]
	public class HeadTrackMod : BaseMod {
		// IMPORTANT: bump this together with the KSLMeta version string right above, every
		// release - this is what the in-game updater compares against GitHub's latest release
		// tag to decide whether an update is available. There's no confirmed public way to read
		// the version back out of the KSLMeta attribute at runtime, so it's duplicated here
		// rather than guessed at via reflection into an undocumented attribute shape.
		private const string CurrentVersion = "0.7.0";

		private const int DefaultOscPort = 9000;

		private readonly OscUdpReceiver receiver_ = new OscUdpReceiver();
		private readonly HeadTrackState state_ = new HeadTrackState();

		private IHeadTrackConfig config_;
		private string portText_ = DefaultOscPort.ToString();
		private string layerMaskText_ = "-1";

		private bool receivedPosition_;
		private bool receivedRotation_;
		private Vector3 latestArPosition_;
		private Quaternion latestArRotation_ = Quaternion.identity;

		// --- Axis-mapping diagnostics ---
		// Confirms the direct (no-swap, as of 0.3.13 - see FixLookDirection) pitch/yaw mapping is
		// doing what it should, without needing a full play test - the periodic heartbeat log (see
		// LogCameraDiagnostics) prints the incoming Unity-space euler angles alongside the final
		// offset actually applied to the camera.
		private Vector3 lastRawArEuler_;
		private Vector3 lastAppliedOffsetEuler_;

		// 0.3.14: same idea as the two above, but for translation - added to help diagnose a
		// report that stepping/leaning left-right in real life wasn't visibly moving the camera.
		// Shows the raw incoming Unity-space position (post ArKitConversion, pre-calibration)
		// alongside the actual per-axis offset GetPositionOffset() produced that frame, so a real
		// log can confirm whether position samples are arriving at all and, if so, whether
		// MaxPositionOffset/PositionSensitivity are clamping them down to nothing rather than the
		// tracking itself being broken.
		private Vector3 lastRawArPosition_;
		private Vector3 lastAppliedPosOffset_;

		// 0.4.2: see TrackPositionalSignalRange - running min/max of the raw ARKit position, used to
		// state plainly whether the phone is sending real positional data or attitude only.
		private Vector3 positionRangeMin_;
		private Vector3 positionRangeMax_;
		private bool positionRangeSeeded_;
		private const float PositionalTrackingDeadThresholdM = 0.25f;

		// 0.4.3: the orbit angle actually applied, and how far it moves the camera in metres. Logged so
		// "is the camera moving enough to see" is a number in the log rather than a judgement call.
		private Vector3 lastOrbitEuler_;
		private float lastOrbitTravel_;

		// 0.5.0: the pose this mod actually wrote in Application.onBeforeRender this frame, so
		// OnEndCameraRendering can compare against it rather than against a value Unity derives from
		// the very Transform we just set. Cleared each frame in LateUpdate.
		private Vector3 poseWrittenPosition_;
		private bool poseWrittenThisFrame_;

		// 0.7.0: gameBase*/poseWeWrote*/unrefreshedFrames_ are gone. They existed to keep the additive
		// TRANSFORM write from compounding on frames Cinemachine skipped. Nothing writes the Transform
		// any more, so there is nothing to compound and nothing to reconcile.

		// 0.6.3: which camera got the offset this frame, so OnEndCameraRendering reverts exactly that
		// one after it has drawn - see the comment there for why reverting matters.
		// 0.7.0: whether a view-matrix override is currently installed on the camera.
		private bool viewMatrixOverridden_;

		// 0.6.4: see ShouldLogVerbose - keeps the formerly per-frame diagnostics off the hot path.
		private float lastVerboseLogTime_;
		private const float VerboseLogIntervalSeconds = 2f;

		// 0.5.3: same idempotency pair for the zoom/FOV write.
		private float gameBaseFov_;
		private float fovWeWrote_;
		private bool hasGameBaseFov_;


		// 0.6.0: 0..1 ramp used to fade the tracked offset out on signal loss and back in on recovery,
		// instead of snapping it off in a single frame. See the fade block in ApplyTrackingToCamera.
		private float signalConfidence_;
		private const float SignalFadeSeconds = 0.35f;

		// 0.6.2: low-passed heading for the offset frame - see GetSmoothedFrameYaw. Swept 0-200 ms
		// against a simulated 50 Hz staircase: 0 ms leaves 137% step variation, 25 ms gives 23.5%,
		// 50 ms gives 14.5%, and past that it flattens out while the heading lag keeps growing
		// (3 deg at 50 ms and 60 deg/s of turn, 12 deg at 200 ms). 50 ms is the knee.
		private float frameYaw_;
		private bool hasFrameYaw_;
		private const float FrameYawTimeConstant = 0.050f;

		// 0.4.6: bounds on the orbit arc - see the clamp in OnCameraPreCull for the measured reason.
		// Yaw gets a usable arc; pitch stays small because pitch is what swings the camera under the car.
		private const float MaxOrbitYawDegrees = 55f;
		private const float MaxOrbitPitchDegrees = 18f;

		// 0.3.17: see the comment at the write site in OnCameraPreCull - ground-truth camera
		// world position after this mod's own Transform write, for isolating whether an offset
		// that's computed correctly is actually reaching the Transform this mod controls.
		private Vector3 lastCameraWorldPosAfterWrite_;

		private Camera cachedCamera_;
		private float cameraCacheTime_;

		// --- 0.4.0: car-anchored camera rig ---
		//
		// ROOT CAUSE this replaces. Every version from 0.3.8 through 0.3.31 applied tracking as an
		// ADDITIVE perturbation on top of whatever CarX's own camera solve produced that frame:
		//
		//     t.position += t.rotation * posOffset;
		//     t.rotation  = t.rotation * rotOffset;
		//
		// Three things about that are wrong for a handheld-phone-camera feel, and together they
		// produce exactly the reported symptom ("the world moves but my camera doesn't"):
		//
		// 1. The rotation term spins the camera about its OWN pivot. In chase cam the camera sits
		//    several meters behind the car; rotating it in place doesn't move it anywhere, it just
		//    aims it somewhere else. The scene sweeps across the screen while the camera itself
		//    never travels - which is precisely "stuff moves but the camera doesn't."
		// 2. The baseline is a moving target. Assembly-CSharp shows CarX's chase cam
		//    (CarX.FollowCamera) is Cinemachine-driven (m_virtualCamera) and carries its own sway
		//    and damping state (m_SwaySpeed, m_BaseSwayAmount, m_TrackingSwayAmount,
		//    m_CurrentVelocityOffset, m_FollowSpeed). Composing our delta onto a baseline that is
		//    itself swinging makes the result chaotic rather than 1:1 with the phone.
		// 3. Scale mismatch. Tracked translation is centimeters-to-decimeters; chase cam sits meters
		//    out. A 6 cm shift on a 5 m boom is invisible, while the rotation term (clamped at up to
		//    120 degrees, previously multiplied by a 2.16x saved sensitivity) throws the view wildly.
		//    So the user gets violent world-sweep and no sense of camera travel at all.
		//
		// THE FIX: stop perturbing CarX's result and instead reconstruct the camera pose outright,
		// anchored to the CAR's transform rather than the world. At calibration we record where the
		// camera is *in the car's local frame*; every frame we rebuild the full pose from the car's
		// CURRENT transform plus that stored local pose plus the tracked delta. That gives:
		//   - car following for free (car-local, so none of 0.3.25's world-anchor breakage where the
		//     camera stayed put as the car drove away),
		//   - true 1:1 6DOF handheld motion, because we define the pose absolutely,
		//   - immunity to CarX's own sway/damping fighting us, because we no longer build on it.
		//
		// If no car can be resolved (garage, menus, spectating, replay with no target) this falls
		// back to the previous additive behaviour automatically, so the mod still does something
		// sensible outside a car - see ResolveCarTransform and the fallback in OnCameraPreCull.
		private Transform carTransform_;
		private float carCacheTime_;
		private Vector3 anchorLocalPosition_;
		private Quaternion anchorLocalRotation_ = Quaternion.identity;
		private bool hasCarAnchor_;

		// Resolved off Assembly-CSharp's own metadata rather than guessed: CameraSwitch.GetCar() and
		// CameraSwitch.targetRaceCar are both public, instance, zero-parameter members, and the
		// RaceCar type they hand back is a MonoBehaviour (so .transform is reachable via Component).
		// Reflection rather than a direct call is deliberate - it keeps this compiling and degrading
		// gracefully if a game update renames or reshapes any of them, instead of hard-failing.
		private static readonly MethodInfo CameraSwitchGetCarMethod =
			typeof(CameraSwitch).GetMethod("GetCar", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
		private static readonly PropertyInfo CameraSwitchTargetRaceCarProperty =
			typeof(CameraSwitch).GetProperty("targetRaceCar", BindingFlags.Public | BindingFlags.Instance);

		// Private backing fields on CameraSwitch, in the order they're worth trying if both public
		// accessors above come back empty (e.g. mid-transition, before the public target is set).
		private static readonly string[] CameraSwitchCarFieldNames = { "m_raceCar", "m_car", "m_playerCarControl" };

		// UIPhotoModeContext and its public "isActive" property were confirmed by directly
		// inspecting Assembly-CSharp.dll - both are public, so no reflection is needed. The
		// instance is found once and reused; "isActive" is re-read live every check since it
		// reflects CarX's own UI context stack switching Photo Mode on/off.
		private UIPhotoModeContext photoModeContext_;

		// Photo Mode drives its own dedicated Camera (privately held, backed by a
		// CinemachineVirtualCamera under the hood) that is NOT the camera tagged "MainCamera" -
		// confirmed by directly inspecting Assembly-CSharp.dll's field table for
		// UIPhotoModeContext (field "m_camera", type UnityEngine.Camera). That's why tracking
		// previously did nothing while in Photo Mode: Camera.main never matched the camera
		// actually rendering. There's no public accessor for it, so this reads the one field
		// reference via reflection - it doesn't touch or copy any of the game's own logic, just
		// locates which live Camera object to apply our offset to.
		private static readonly FieldInfo PhotoModeCameraField =
			typeof(UIPhotoModeContext).GetField("m_camera", BindingFlags.NonPublic | BindingFlags.Instance);

		// This PC's own LAN IPv4 address(es), shown in the settings panel so LOTA's destination
		// IP can be copied over at a glance instead of hunting for it via ipconfig. Editable -
		// see config_.LocalIpOverride - since auto-detection can't always guess which adapter
		// (Wi-Fi vs Ethernet vs a VPN's virtual adapter) is the right one to type into LOTA.
		private string localIpText_ = "(not checked yet)";

		// Manually-entered phone IP, used to filter incoming OSC packets - see
		// config_.PhoneIpFilter and OscUdpReceiver.AllowedSenderFilter. Empty = accept from any sender.
		private string phoneIpText_ = "";

		// --- Diagnostics ---
		// Kept even after finding the real bug (see the Camera.onPreCull subscription comment in
		// Start() - this game runs a Scriptable Render Pipeline, so onPreCull never fired at all,
		// regardless of camera targeting logic) - useful going forward for confirming the SRP
		// hook actually reaches the right camera. Logs every distinct camera Unity renders through
		// once each (so it doesn't spam), plus a periodic heartbeat showing what
		// GetActiveCamera() currently resolves to.
		private readonly HashSet<string> loggedCameraNames_ = new HashSet<string>();
		private float lastDiagnosticLogTime_;

		// Zoom is a persistent offset added on top of whatever FOV CarX's own camera logic sets
		// that frame (same "apply after the game" approach as the head-tracking offset itself),
		// so it composes with any dynamic FOV effects (speed, drift, etc.) instead of fighting them.
		// zoomTargetDegrees_ is what scroll/keys set directly; zoomCurrentDegrees_ eases toward it
		// every frame (see Update()) so zoom feels smooth instead of snapping instantly.
		private float zoomTargetDegrees_;
		private float zoomCurrentDegrees_;


		// 0.3.23: see CheckOscSignalHealth's doc comment - a real log showed incomingPos AND
		// incomingEuler both frozen at identical values for 48+ straight seconds, meaning LOTA
		// simply stopped sending packets for that whole stretch. Nothing in the mod logged that
		// as an event, so it only showed up after manually diffing consecutive heartbeat lines by
		// hand. This tracks whether we're currently considered "in an outage" so the transition
		// (both directions) gets logged clearly instead of requiring that manual diff again.
		private bool oscSignalLost_;
		private const int OscSignalLostThresholdMs = 2000;

		// 0.3.31: see CheckOscSignalHealth's auto-restart block.
		private int lastAutoRestartAttemptTick_;
		private const int OscAutoRestartThresholdMs = 5000;
		private const int OscAutoRestartCooldownMs = 10000;

		// 0.3.29: every recurring "camera isn't moving" report so far has traced back to the exact
		// same thing - LOTA stopped sending (phone screen locked/backgrounded, app closed, Wi-Fi
		// dropped) - confirmed each time by totalRawPacketsReceived going flat. The settings panel
		// already surfaces this via its "Status:" line, but that's only visible while the panel is
		// open, which it normally isn't during actual gameplay - so the natural moment to notice a
		// dropout (mid-drive, camera not responding) has never had anything on screen to check
		// against. This draws the same connected/calibrated state as a small always-on corner label
		// whenever the mod is Enabled, regardless of whether any menu is open, so "is this LOTA or
		// is this the mod" is answerable at a glance instead of needing a log pulled afterward.
		private GUIStyle hudStyle_;

		// --- In-game updater ---
		// Checks GitHub Releases directly (not KSL's own updater, which only runs at game
		// startup) so a newer build can be fetched into kino/mods without closing the game.
		// NOTE: this can never hot-swap the *running* code - once a .NET assembly is loaded into
		// a live process there's no supported way to reload it, in Unity/Mono or otherwise. What
		// this does do is remove the manual "go to GitHub, download, copy the file over" steps -
		// the downloaded .ksm is ready and waiting the next time the game happens to restart.
		private const string UpdateRepoOwner = "Chaoz75";
		private const string UpdateRepoName = "PhoneCam";
		private const string UpdateCheckUrl = "https://api.github.com/repos/" + UpdateRepoOwner + "/" + UpdateRepoName + "/releases/latest";
		private const string UpdateAssetName = "PhoneCam.ksm";

		private string updateStatus_ = "Not checked yet.";
		private string updateLatestVersion_;
		private string updateDownloadUrl_;
		private bool updateCheckInProgress_;
		private bool updateDownloadInProgress_;

		[Serializable]
		private class GitHubAsset {
			public string name;
			public string browser_download_url;
		}

		[Serializable]
		private class GitHubRelease {
			public string tag_name;
			public GitHubAsset[] assets;
		}

		private void Start() {
			config_ = Kino.Config.RegisterConfig<IHeadTrackConfig>();
			ApplyDefaultsIfUnset();

			portText_ = config_.OscPort.ToString();
			layerMaskText_ = config_.ClippingGuardLayerMask.ToString();
			SyncStateFromConfig();

			Kino.Input.Bind(KeyCode.F9, Calibrate, "Head Track: Set Neutral Position");
			Kino.Input.Bind(KeyCode.Equals, () => AdjustZoom(-2f), "Head Track: Zoom In");
			Kino.Input.Bind(KeyCode.Minus, () => AdjustZoom(2f), "Head Track: Zoom Out");
			Kino.Input.Bind(KeyCode.F10, ResetZoom, "Head Track: Reset Zoom");

			receiver_.OnError += ex => Kino.Log.Warning($"[HeadTrackARKit] OSC receive error: {ex.Message}");

			localIpText_ = string.IsNullOrEmpty(config_.LocalIpOverride) ? AutoDetectLocalIp() : config_.LocalIpOverride;

			phoneIpText_ = config_.PhoneIpFilter ?? "";
			receiver_.AllowedSenderFilter = phoneIpText_;

			if (config_.Enabled) {
				StartReceiver();
			}

			// Camera.onPreCull only fires under Unity's legacy Built-in Render Pipeline - it never
			// fires at all if a Scriptable Render Pipeline (URP/HDRP) is active, which is looking
			// like the actual explanation for why 0.3.1/0.3.2's diagnostics logged nothing even
			// unconditionally: the KSL log lines "Enabled volume override" / "Enabled sky
			// override" are Volume Framework terminology, which is SRP-only. RenderPipelineManager
			// .beginCameraRendering is the SRP equivalent hook (fires once per camera, right
			// before it renders, same as onPreCull's timing guarantee) - subscribing to both costs
			// nothing, since a project only ever runs one pipeline at a time, so only the relevant
			// one will ever actually call back.
			// 0.5.0: THE pose-write hook - see OnBeforeRender's doc comment. Fires after every
			// LateUpdate (so after CinemachineBrain has pushed its pose to the camera) and before the
			// render pipeline runs (so before HDRP's HDCamera.Update snapshots the view matrix). The
			// two subscriptions below are diagnostics only now.
			Application.onBeforeRender += OnBeforeRender;

			Camera.onPreCull += OnCameraPreCull;
			RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;

			// 0.3.21: diagnostic-only addition (see OnEndCameraRendering's doc comment) - fires as
			// late as it's possible to observe a camera's state, after it has already finished
			// rendering for the frame. Not subscribed via the every-LateUpdate resubscribe trick
			// like the two above, since it's a pure read - it doesn't matter where in the
			// subscriber list a read-only handler sits, it'll see whatever the true end-of-render
			// state is regardless.
			RenderPipelineManager.endCameraRendering += OnEndCameraRendering;

			Kino.Log.Info($"[HeadTrackARKit] Loaded. Enabled={config_.Enabled}. Bind key default: F9 to calibrate neutral position.");
		}

		private void OnDestroy() {
			Application.onBeforeRender -= OnBeforeRender;
			Camera.onPreCull -= OnCameraPreCull;
			RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
			RenderPipelineManager.endCameraRendering -= OnEndCameraRendering;
			receiver_.Stop();
		}

		private void Update() {
			receiver_.DrainInto(HandleOscMessage);
			CheckOscSignalHealth();

			// Legacy Input Manager scroll axis - not part of Kino.Input's rebindable hotkey
			// system since it's a continuous axis rather than a discrete key, so it's polled
			// directly here. Scrolling "up"/forward zooms in (lower FOV).
			float scroll = UnityEngine.Input.GetAxis("Mouse ScrollWheel");
			if (config_.Enabled && scroll != 0f) {
				AdjustZoom(-scroll * config_.ZoomSensitivity * 10f);
			}

			// Ease the applied zoom toward the scroll/key target every frame instead of snapping
			// straight to it, so zoom feels smooth rather than stepping instantly. Exponential,
			// frame-rate-independent smoothing: ZoomSmoothing is "how much of the remaining
			// distance to close per 1/60th of a second," scaled by the actual frame time.
			float smoothing = Mathf.Clamp01(config_.ZoomSmoothing);
			float rate = 1f - Mathf.Pow(1f - smoothing, Time.unscaledDeltaTime * 60f);
			zoomCurrentDegrees_ = Mathf.Lerp(zoomCurrentDegrees_, zoomTargetDegrees_, rate);
		}

		/// <summary>
		/// 0.3.23: added specifically because a real test log showed 48+ seconds where
		/// incomingPos and incomingEuler both sat frozen at identical values - the mod's own
		/// offset math was fine (confirmed by earlier, real movement in the same log), there was
		/// just no live data coming in to track for that whole stretch, most likely because LOTA
		/// stopped streaming (phone screen locked, app backgrounded, Wi-Fi dropped). Nothing in
		/// the mod surfaced that as an event before this - the settings panel's "Status: no data"
		/// line would have shown it live if it happened to be open, but the log itself gave no
		/// indication short of manually diffing consecutive heartbeat lines by hand.
		///
		/// Logs a clear one-time warning the moment the gap since the last successfully parsed
		/// OSC packet crosses <see cref="OscSignalLostThresholdMs"/> (2s - deliberately looser
		/// than the settings panel's stricter 750ms "receiving data" indicator, so a normal brief
		/// UDP hiccup doesn't spam the log), and a matching one-time recovery log when packets
		/// resume. <see cref="oscSignalLost_"/> makes both edges fire exactly once per outage
		/// instead of every frame. The ongoing gap is also folded into the periodic heartbeat (see
		/// LogCameraDiagnostics) so a future log shows this continuously, not just at the edges.
		/// </summary>
		private void CheckOscSignalHealth() {
			if (!config_.Enabled) return;

			// 0.4.5 BUG FIX: this used to also `return` when !receiver_.IsRunning, which meant the
			// 0.3.31 auto-rebind below could never fire in the one situation it was written for - a
			// receiver that has actually stopped. A real log caught the consequence: receiverRunning
			// =False with the packet counter frozen for 109 seconds and no rebind ever attempted,
			// because this guard bailed out first. If the socket is down while we're supposed to be
			// enabled, rebinding is exactly the right thing to do, so that case now falls through to
			// the restart logic instead of being skipped.
			if (!receiver_.IsRunning) {
				TryAutoRestartReceiver(-1);
				return;
			}

			// LastMessageTick is still 0 before the very first packet ever arrives - that's "never
			// connected yet," not "signal lost," so it's excluded here rather than immediately
			// warning the moment the mod starts.
			if (receiver_.LastMessageTick == 0) return;

			int gapMs = Environment.TickCount - receiver_.LastMessageTick;

			if (gapMs > OscSignalLostThresholdMs && !oscSignalLost_) {
				oscSignalLost_ = true;
				// 0.3.24: includes the raw UDP packet count so this one line answers "is the
				// phone even sending anything right now" on its own - if TotalRawPacketsReceived
				// keeps climbing on the *next* heartbeat line after this warning despite
				// LastMessageTick staying stuck, packets are physically arriving but failing to
				// turn into messages (a parsing bug, fixable here); if it's flat too, nothing is
				// reaching the socket at all (phone/Wi-Fi/LOTA-side).
				Kino.Log.Warning(
					$"[HeadTrackARKit] OSC signal lost - no packets from LOTA for over {gapMs / 1000}s " +
					$"(totalRawPacketsReceived={receiver_.TotalRawPacketsReceived} as of this warning - " +
					"compare against the next heartbeat's count to see if that's still climbing). " +
					"Check LOTA is still streaming (app in foreground, phone screen on) and Wi-Fi is stable.");
			}
			else if (gapMs <= OscSignalLostThresholdMs && oscSignalLost_) {
				oscSignalLost_ = false;
				lastAutoRestartAttemptTick_ = 0;
				Kino.Log.Info("[HeadTrackARKit] OSC signal restored.");
			}

			// 0.3.31: every outage examined so far (five separate test sessions now) shows the same
			// signature - TotalRawPacketsReceived goes completely flat, meaning nothing is reaching
			// this PC's socket at all, not a parsing/game-logic problem. That's consistent with LOTA
			// genuinely not sending, but it's ALSO exactly what a stale/orphaned OS socket looks like
			// from here: if Windows silently drops the Wi-Fi adapter's route (sleep/wake, DHCP
			// renewal, the adapter itself power-cycling), a UdpClient already bound to
			// IPAddress.Any can keep reporting "running" while no longer actually receiving anything
			// on the interface that matters, with no exception raised on this side to say so. This
			// mod has no way to distinguish that from "the phone stopped sending" purely from inside
			// the process - but rebinding a fresh socket is a cheap, safe thing to try either way: if
			// the phone genuinely isn't sending, a rebind changes nothing and costs nothing; if the
			// old socket had gone stale, a fresh bind can recover it without needing the game
			// restarted. Gated well past the 2s warning threshold, and cooled down between attempts,
			// so this can't spam reconnects during a real, ordinary phone-side outage.
			if (gapMs > OscAutoRestartThresholdMs) {
				TryAutoRestartReceiver(gapMs);
			}
		}

		/// <summary>
		/// Rate-limited OSC socket rebind. <paramref name="gapMs"/> is the time since the last packet,
		/// or -1 when the receiver isn't running at all (nothing to measure a gap from).
		/// </summary>
		private void TryAutoRestartReceiver(int gapMs) {
			int sinceLastAttempt = lastAutoRestartAttemptTick_ == 0
				? int.MaxValue
				: Environment.TickCount - lastAutoRestartAttemptTick_;
			if (sinceLastAttempt <= OscAutoRestartCooldownMs) return;

			lastAutoRestartAttemptTick_ = Environment.TickCount;
			string reason = gapMs < 0
				? "OSC listener is not running while the mod is enabled"
				: $"still no data after {gapMs / 1000}s";
			Kino.Log.Warning(
				$"[HeadTrackARKit] {reason} - rebinding the OSC socket " +
				"(won't help if LOTA genuinely isn't sending, but costs nothing to try).");
			RestartReceiver();
		}

		/// <summary>
		/// 0.3.31: see the doc comment on the auto-restart block in <see cref="CheckOscSignalHealth"/>.
		/// Stops and re-creates the OSC socket on the same port, without touching calibration or any
		/// saved settings - purely a "what if the socket itself went stale" recovery attempt.
		/// </summary>
		private void RestartReceiver() {
			try {
				receiver_.Start(config_.OscPort);
				Kino.Log.Info("[HeadTrackARKit] OSC socket rebound.");
			}
			catch (Exception ex) {
				Kino.Log.Error($"[HeadTrackARKit] Failed to rebind OSC socket: {ex.Message}");
			}
		}

		private void LateUpdate() {
			// 0.5.0: reset the per-frame overwrite-check flag. LateUpdate runs before
			// Application.onBeforeRender, so this always clears ahead of that frame's write.
			poseWrittenThisFrame_ = false;

			// A C# multicast event invokes its subscribers in registration order. Unsubscribing
			// then immediately re-subscribing moves this mod's handler to the END of that list,
			// which means it runs after anything else that also touches the camera this frame -
			// including CarX's own follow-cam logic and Kino's custom camera system in Photo Mode.
			// Without this, whichever of us happened to subscribe first would have its change to
			// the camera transform silently discarded by whichever subscribed after - which is
			// the likely reason the offset wasn't visibly affecting the camera view even once the
			// SRP hook itself started firing correctly (0.3.4). Doing this every frame (rather
			// than once in Start()) means it keeps winning even if something else re-subscribes
			// itself later, e.g. when Kino swaps its own custom camera in/out of Photo Mode.
			Camera.onPreCull -= OnCameraPreCull;
			Camera.onPreCull += OnCameraPreCull;
			RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
			RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
		}

		/// <summary>
		/// 0.3.29: see the field comment on <see cref="hudStyle_"/>. IMGUI's OnGUI runs independent
		/// of the Built-in/SRP render path split that made Camera.onPreCull vs
		/// RenderPipelineManager.beginCameraRendering necessary elsewhere in this file, so a single
		/// implementation covers both. Deliberately terse (one line, top-left) - this is a glance-
		/// able status indicator, not a replacement for the settings panel's fuller detail.
		/// </summary>
		private void OnGUI() {
			// 0.4.1: this used to also return when !config_.Enabled, which was exactly backwards.
			// "Enabled is off" is the single state where on-screen feedback matters most - the mod is
			// completely inert, nothing responds, and there is no indication anywhere on screen as to
			// why. A real session (0.4.0, 2026-07-29 16:00-16:09) was spent entirely in that state:
			// Enabled persisted false from the previous session's last action, the OSC listener never
			// started, zero packets arrived, nothing was ever calibrated, and the HUD stayed blank
			// because of this very guard - so the mod looked identical to a broken one. Now the
			// disabled state is reported explicitly and only ShowStatusHud can suppress the HUD.
			if (!config_.ShowStatusHud) return;

			string text;
			Color color;

			if (!config_.Enabled) {
				text = "PhoneCam: DISABLED - tick 'Enabled' in the PhoneCam settings panel to turn tracking on";
				color = new Color(1f, 0.55f, 0f); // orange: not an error, just switched off
			}
			else if (!receiver_.IsRunning) {
				text = "PhoneCam: OSC listener not running (toggle Enabled off/on to retry)";
				color = Color.red;
			}
			else if (receiver_.LastMessageTick == 0) {
				text = "PhoneCam: waiting for LOTA - no packets received yet";
				color = Color.yellow;
			}
			else if (!IsReceivingData()) {
				float secondsSinceLastPacket = (Environment.TickCount - receiver_.LastMessageTick) / 1000f;
				text = $"PhoneCam: NO SIGNAL ({secondsSinceLastPacket:F0}s) - check LOTA (screen on, app in foreground), then press F9";
				color = Color.red;
			}
			else if (!state_.IsCalibrated) {
				text = "PhoneCam: receiving data - press F9 to calibrate";
				color = Color.yellow;
			}
			else {
				// 0.4.0: distinguishes the two camera modes on screen, so "is the rig actually
				// driving my camera" is answerable at a glance instead of only from the log.
				text = (hasCarAnchor_ && GetCarTransform() != null)
					? "PhoneCam: tracking (car-anchored rig)"
					: "PhoneCam: tracking (no car - additive fallback, press F9 in a car)";
				color = Color.green;
			}

			if (hudStyle_ == null) {
				hudStyle_ = new GUIStyle(GUI.skin.label) {
					fontSize = 16,
					fontStyle = FontStyle.Bold
				};
			}

			// A black backdrop copy drawn one pixel offset behind the colored text so it stays
			// readable against bright/white backgrounds (sky, headlights, snow) instead of just
			// the colored text alone.
			hudStyle_.normal.textColor = Color.black;
			GUI.Label(new Rect(21, 21, 700, 30), text, hudStyle_);

			hudStyle_.normal.textColor = color;
			GUI.Label(new Rect(20, 20, 700, 30), text, hudStyle_);
		}

		private void AdjustZoom(float deltaDegrees) {
			float max = Mathf.Abs(config_.MaxZoomOffset);
			zoomTargetDegrees_ = Mathf.Clamp(zoomTargetDegrees_ + deltaDegrees, -max, max);
		}

		private void ResetZoom() {
			zoomTargetDegrees_ = 0f;
			Kino.Log.Info("[HeadTrackARKit] Zoom reset.");
		}

		private void HandleOscMessage(OscMessage msg) {
			switch (msg.Address) {
				case "/lota/camera/position":
					if (msg.Args.Length >= 3) {
						latestArPosition_ = new Vector3(msg.GetFloat(0), msg.GetFloat(1), msg.GetFloat(2));
						receivedPosition_ = true;
					}
					break;

				case "/lota/camera/rotation":
					if (msg.Args.Length >= 4) {
						// LOTA sends quaternion as x, y, z, w.
						latestArRotation_ = new Quaternion(msg.GetFloat(0), msg.GetFloat(1), msg.GetFloat(2), msg.GetFloat(3));
						receivedRotation_ = true;
					}
					break;

				default:
					// Not something this mod cares about (e.g. /lota/mode, /lota/fps) - ignore.
					break;
			}

			if (receivedPosition_ && receivedRotation_) {
				Vector3 unityPos = ArKitConversion.ToUnityPosition(latestArPosition_);
				Quaternion unityRot = ArKitConversion.ToUnityRotation(latestArRotation_);
				state_.PushSample(unityPos, unityRot);
				lastRawArEuler_ = NormalizeEulerForLog(unityRot.eulerAngles);
				lastRawArPosition_ = unityPos;
				TrackPositionalSignalRange(unityPos);
			}
		}

		/// <summary>
		/// 0.3.13: the 0.3.9 pitch/yaw swap is removed here. Real testing after the 0.3.12 atan2
		/// rewrite pinned down why it's not just unneeded now but actively wrong: a v0.3.12 log
		/// caught the swap funneling a clean, large yaw value (a normal full-360 turn) straight
		/// into the *pitch* slot of the applied rotation (appliedOffsetEuler.x=234) - and a
		/// pitch (rotation around the camera's right axis) of that size isn't a numerical glitch,
		/// it's a literal upside-down camera. Unlimited spinning can only ever be flip-safe on the
		/// yaw axis (rotation around up) - pitch geometrically can't do it, no matter how clean the
		/// input is.
		///
		/// The swap was originally validated (0.3.9) against the *old* eulerAngles-based
		/// extraction, which had its own axis-bleed instability (see
		/// HeadTrackState.GetRotationOffsetEuler's doc comment) - it's very likely that original
		/// "turning left/right moved the camera up/down" symptom was itself partly a decomposition
		/// bleed artifact, and the swap was an empirical fix for *that* data, not a true physical
		/// axis mismatch. Now that extraction is clean (atan2-based, 0.3.12), routing raw pitch/yaw
		/// straight through - no swap - is the geometrically correct mapping: yaw (unbounded,
		/// spin-safe) drives camera yaw, pitch (naturally small, human head-tilt range) drives
		/// camera pitch. InvertPitch/InvertYaw remain as a one-click escape hatch in case either
		/// direction reads backwards on this rig.
		///
		/// Takes the raw (pitch, yaw, roll) triple straight from
		/// HeadTrackState.GetRotationOffsetEuler (atan2-derived, not decomposed from a
		/// Quaternion) and builds the final applied Quaternion directly - see that method's doc
		/// comment for why re-decomposing a Quaternion a second time here would reintroduce the
		/// axis-bleed bug 0.3.12 fixed.
		/// </summary>
		private Quaternion FixLookDirection(Vector3 rotOffsetEuler) {
			float pitch = rotOffsetEuler.x;
			float yaw = rotOffsetEuler.y;
			float roll = rotOffsetEuler.z;

			float newPitch = config_.InvertPitch ? -pitch : pitch;
			float newYaw = config_.InvertYaw ? -yaw : yaw;

			lastAppliedOffsetEuler_ = new Vector3(newPitch, newYaw, roll);
			return Quaternion.Euler(newPitch, newYaw, roll);
		}

		// Unity's eulerAngles are 0..360 per axis, which makes small negative rotations show up
		// as ~359 degrees - remap to -180..180 so the diagnostic log is actually readable/correct
		// for small angles.
		private static Vector3 NormalizeEulerForLog(Vector3 euler) {
			return new Vector3(NormalizeAngleForLog(euler.x), NormalizeAngleForLog(euler.y), NormalizeAngleForLog(euler.z));
		}

		private static float NormalizeAngleForLog(float angle) {
			angle %= 360f;
			if (angle > 180f) angle -= 360f;
			if (angle < -180f) angle += 360f;
			return angle;
		}

		private static string FormatEuler(Vector3 e) {
			return $"(x={e.x:F2},y={e.y:F2},z={e.z:F2})";
		}

		private static string FormatVector(Vector3 v) {
			return $"(x={v.x:F4},y={v.y:F4},z={v.z:F4})";
		}

		/// <summary>
		/// 0.3.27: back to setting only the phone's neutral pose - see OnCameraPreCull's doc
		/// comment for why 0.3.25's "also lock a fixed world-space camera anchor here" idea got
		/// reverted (it broke the camera following the car at all, which is worse than the
		/// problem it was trying to fix).
		/// </summary>
		private void Calibrate() {
			if (!state_.HasSignal) {
				Kino.Log.Warning("[HeadTrackARKit] No OSC data received yet - check LOTA is streaming and the port matches.");
				return;
			}

			state_.Calibrate();

			// 0.4.0: capture where the camera currently sits IN THE CAR'S OWN LOCAL FRAME, which is
			// what makes the rig follow the car for free without inheriting CarX's per-frame sway.
			// Storing it car-local (rather than 0.3.25's fixed world-space anchor) is the whole
			// difference between "camera rides along with the car" and "camera stays behind in empty
			// space watching the car drive off," which is what made 0.3.25 unusable.
			hasCarAnchor_ = false;
			carTransform_ = null; // force a fresh resolve rather than trusting a stale cache here
			Transform car = GetCarTransform();
			Camera cam = GetActiveCamera();

			if (car != null && cam != null) {
				Transform camTransform = cam.transform;
				anchorLocalPosition_ = car.InverseTransformPoint(camTransform.position);
				anchorLocalRotation_ = Quaternion.Inverse(car.rotation) * camTransform.rotation;
				hasCarAnchor_ = true;
				Kino.Log.Info(
					$"[HeadTrackARKit] Neutral position set - car-anchored rig active " +
					$"(camera at {FormatVector(anchorLocalPosition_)} in car-local space, car='{car.name}').");
			}
			else {
				// No car right now (garage, menus, spectating). Tracking still works via the additive
				// fallback in OnCameraPreCull - just without car-relative anchoring, since there's
				// nothing to anchor to.
				Kino.Log.Info(
					"[HeadTrackARKit] Neutral position set - no car resolved, using additive fallback " +
					"(tracking still active; re-press F9 once in a car for the full rig).");
			}
		}

		/// <summary>
		/// 0.5.0 - THE ACTUAL ROOT CAUSE FIX. This is where the tracked pose is now written, and the
		/// reason nothing this mod did to the camera has ever appeared on screen.
		///
		/// Every version up to 0.4.6 wrote the camera pose from
		/// <see cref="OnBeginCameraRendering"/> (RenderPipelineManager.beginCameraRendering). That is
		/// too late in an HDRP frame. Disassembling the game's own
		/// Unity.RenderPipelines.HighDefinition.Runtime.dll settles it - inside
		/// HDRenderPipeline.PrepareAndCullCamera, the very first call is TryCalculateFrameParameters,
		/// and that method does:
		///
		///     IL_A74AF  HDCamera::GetOrCreate
		///     IL_A74E7  HDCamera::Update              &lt;-- captures the camera's view matrix
		///     IL_A7567  Camera::TryGetCullingParameters
		///
		/// while BeginCameraRendering is only reached much later (IL_A55BF in PrepareAndCullCamera,
		/// and IL_A7963 inside TryCull - both after that first call). By the time our callback ran,
		/// HDRP had ALREADY snapshotted the view matrix and computed culling for the frame. Writing
		/// camera.transform at that point changes nothing that is rendered: the frame draws from
		/// CarX's pose, and then CinemachineBrain.LateUpdate (via ProcessActiveCamera ->
		/// PushStateToUnityCamera - the only places the Brain writes the transform) overwrites our
		/// value before anything reads it again.
		///
		/// That is why every diagnostic looked correct while the screen never moved: we wrote the
		/// Transform, read it straight back, and compared it against worldToCameraMatrix - which Unity
		/// derives FROM that same Transform on demand. The check was circular. It could only ever have
		/// confirmed that our own assignment took effect on the Transform, never that HDRP used it.
		///
		/// Application.onBeforeRender fires after every LateUpdate (so after the Brain has written its
		/// pose) and before the render pipeline runs (so before HDCamera.Update captures anything).
		/// That is precisely the window this mod needs, and it is the same callback Unity's own XR
		/// late-latching uses for exactly this reason. Because it sits after whatever camera
		/// controller CarX used this frame, it works identically for chase, cockpit, hood, roof,
		/// bumper, interior and Photo Mode - they all resolve to a Transform by end of LateUpdate,
		/// and all of them are perturbed here without this mod needing to know which one is active.
		/// </summary>
		private void OnBeforeRender() {
			if (!config_.Enabled) return;

			// 0.5.2: advance the smoothing filter exactly once per frame, against real elapsed time -
			// see HeadTrackState.UpdateSmoothing for why doing it per received packet was itself a
			// jitter source.
			state_.UpdateSmoothing(Time.unscaledDeltaTime);

			Camera cam = GetActiveCamera();
			if (cam == null || cam.targetTexture != null || cam.orthographic) return;

			ApplyTrackingToCamera(cam);
		}

		/// <summary>
		/// Diagnostics only as of 0.5.0. The pose write moved to <see cref="OnBeforeRender"/> - see its
		/// doc comment for why writing from here could never affect the rendered image under HDRP.
		/// </summary>
		private void OnCameraPreCull(Camera cam) {
			// Runs regardless of the Enabled toggle - "no diag lines at all" would otherwise be
			// ambiguous between "Enabled was off" and "this hook never fires for the render camera".
			LogCameraDiagnostics(cam);

			if (!config_.Enabled) {
				// Make sure a previous frame's override doesn't linger once the mod is turned off.
				ClearViewMatrixOverride(cam);
			}
		}

		/// <summary>
		/// 0.7.0 - THE ARCHITECTURAL FIX. The camera's Transform is never written; only its view matrix.
		///
		/// Every defect in this project's history traces to one decision: writing <c>cam.transform</c>,
		/// an object the game owns, rewrites every LateUpdate, and - critically - READS BACK.
		/// From Assembly-CSharp, CarX.FollowCamera.LateUpdate does
		/// <c>Transform::get_position</c> x3 -> <c>Vector3::Lerp</c> -> <c>get_forward</c> ->
		/// <c>LookRotation</c> -> <c>set_rotation</c>, and CalcCameraPoint selects its tracking point by
		/// <c>SqrMagnitude</c> distance FROM THE CAMERA'S POSITION, hard-resetting via
		/// <c>Reset()</c>/<c>InstantApplyFocus()</c> when the winner flips. So the transform is an INPUT
		/// to a stateful damper with discrete switching, not just an output.
		///
		/// 0.6.3 tried to contain that by reverting after render. That only narrows the window: the
		/// transform is still polluted for the whole span between onBeforeRender and endCameraRendering
		/// (which is when HDRP, custom passes, reflection probes, the StudioListener on this same
		/// GameObject and LOD selection all read it), and if endCameraRendering is ever skipped for this
		/// camera the offset leaks into the next LateUpdate and feeds the damper. Intermittent leakage
		/// into a damper is, by definition, jitter.
		///
		/// Overriding <c>worldToCameraMatrix</c> instead removes the entire class of problem:
		///   - the Transform stays exactly as CarX and Cinemachine left it, so nothing can feed back;
		///   - no revert is needed, so there is no timing dependency to get wrong;
		///   - HDRP honours it - the HDRP assembly references <c>Camera::get_worldToCameraMatrix</c> and
		///     <c>get_cullingMatrix</c>, and <c>cullingMatrix</c> defaults to
		///     <c>projectionMatrix * worldToCameraMatrix</c>, so culling follows automatically;
		///   - <c>projectionMatrix</c> is deliberately NOT touched. HDRP's temporal AA applies its
		///     sub-pixel jitter there, and clobbering it is what caused 0.3.19's "motion blur while
		///     standing still" and shadow flicker. The view matrix carries no jitter state.
		///
		/// The scale of (1, 1, -1) is the standard Unity idiom: view space looks down -Z while a
		/// Transform's forward is +Z, so the handedness flip belongs in the matrix, not the pose.
		/// </summary>
		private void ApplyViewMatrixOverride(Camera cam, Vector3 position, Quaternion rotation) {
			cam.worldToCameraMatrix = Matrix4x4.TRS(position, rotation, new Vector3(1f, 1f, -1f)).inverse;
			viewMatrixOverridden_ = true;
		}

		/// <summary>
		/// Hands the view matrix back to Unity, which then derives it from the Transform as normal.
		/// Only acts when an override is actually in place - calling ResetWorldToCameraMatrix every
		/// frame regardless is the 0.5.3 defect.
		/// </summary>
		private void ClearViewMatrixOverride(Camera cam) {
			if (!viewMatrixOverridden_) return;
			cam.ResetWorldToCameraMatrix();
			cam.ResetCullingMatrix();
			viewMatrixOverridden_ = false;
		}

		private void ApplyTrackingToCamera(Camera cam) {

			// Zoom applies independently of head-tracking calibration.
			//
			// 0.5.3: this had exactly the same compounding bug the pose write had (0.5.2) - it was
			// `cam.fieldOfView += zoom` every frame, which silently assumes the game rewrites FOV every
			// frame. CarX.FollowCamera drives FOV from speed (m_carFieldOfView /
			// m_carFieldOfViewCurrent / m_carFieldOfViewTarget), so while DRIVING the FOV it writes
			// changes constantly - and on any frame it doesn't write, our delta stacked on the previous
			// frame's result and the view pumped in and out. Same idempotency treatment: remember the
			// FOV the game gave us and the FOV we wrote, and rebuild from the game's value rather than
			// from our own previous output.
			bool hasZoom = Mathf.Abs(zoomCurrentDegrees_) > 0.001f;
			if (hasZoom) {
				if (hasGameBaseFov_ && Mathf.Abs(cam.fieldOfView - fovWeWrote_) < 0.0001f) {
					cam.fieldOfView = gameBaseFov_;
				}
				gameBaseFov_ = cam.fieldOfView;
				hasGameBaseFov_ = true;

				cam.fieldOfView = Mathf.Clamp(cam.fieldOfView + zoomCurrentDegrees_, 1f, 179f);
				fovWeWrote_ = cam.fieldOfView;
			}
			else {
				hasGameBaseFov_ = false;
			}

			// 0.3.8: writes the head-tracking offset onto the camera's real Transform, added on
			// top of whatever CarX's own camera logic just computed for `cam` this exact frame.
			// This is what makes the camera keep following the car normally - chase cam still
			// follows the car around the track, cockpit cam still stays glued to the seat - with
			// your tracked movement layered on top of that, not replacing it.
			//
			// 0.3.25 briefly replaced this with a fixed WORLD-SPACE anchor: camera position/
			// rotation captured once at F9, then every frame rebuilt from that fixed anchor plus
			// the tracked delta, completely ignoring CarX's own per-frame camera computation. The
			// goal was removing the "tracked movement fighting a moving baseline" feeling (chase
			// cam sways with the car, cockpit cam has its own settle/shake) - but a fixed WORLD
			// anchor doesn't know the car ever moves: drive away from wherever you calibrated and
			// the camera just sits there in empty space, watching the car leave. That's what
			// "camera isn't working" turned out to mean, and it's strictly worse than the
			// "fighting a moving baseline" problem it was trying to solve. Reverted back to the
			// additive approach here - CarX's own camera logic still does 100% of "follow the
			// car" for free, this mod only perturbs its result. A properly car-relative free cam
			// (anchored to the car's own Transform instead of world space) is a real potential
			// future improvement, but needs the car's actual Transform reference identified first
			// rather than shipping another guess.
			// 0.4.5 BUG FIX: don't keep applying a pose that stopped updating.
			//
			// HeadTrackState holds its last smoothed sample indefinitely and IsCalibrated stays true, so
			// once the phone stops sending, every following frame re-applied the SAME frozen offset
			// forever. A real log showed exactly that: incomingEuler pinned at (-15,171,83) and
			// appliedOffsetEuler at (-3,-1,-3), bit-for-bit identical across every heartbeat, for 109
			// seconds after the last packet. A constant offset is not tracking - it is a fixed
			// displacement that cannot respond to the phone at all, and it looks identical to a frozen
			// camera while every diagnostic still reports "calibrated, applying an offset".
			//
			// Past the staleness cutoff, leave the camera entirely alone: CarX's own solve then drives it
			// untouched, which is honest (the mod has nothing valid to contribute) and visibly different
			// from a freeze.
			bool signalStale = receiver_.LastMessageTick == 0 ||
			                   (Environment.TickCount - receiver_.LastMessageTick) > OscSignalLostThresholdMs;

			// 0.6.0 - FIXES THE TELEPORT IN/OUT OF THE CAR.
			//
			// 0.4.5 correctly stopped applying a frozen stale pose, but it did so with an instant
			// `return`: the offset went from full to zero in a single frame, so the camera snapped back
			// to CarX's own pose - which is at/inside the car - and snapped back out the moment packets
			// resumed. Real logs show this happening (two "OSC signal lost" / "restored" pairs in one
			// session), and it is exactly the reported "sometimes it teleports back to the car seat,
			// then goes back to where I was outside".
			//
			// The offset is now faded out and back in over SignalFadeSeconds instead. Stale still means
			// the mod contributes nothing - it just gets there smoothly rather than in one frame.
			float fadeStep = Time.unscaledDeltaTime / Mathf.Max(0.01f, SignalFadeSeconds);
			signalConfidence_ = Mathf.Clamp01(signalConfidence_ + (signalStale ? -fadeStep : fadeStep));

			if (state_.IsCalibrated && signalConfidence_ <= 0f) {
				ClearViewMatrixOverride(cam);
				return;
			}

			if (state_.IsCalibrated) {
				// 0.6.0: scaled by signalConfidence_ so a dropout fades the effect out instead of
				// snapping it off - see the fade block above.
				Vector3 posOffset = ApplyPositionInvert(state_.GetPositionOffset()) * signalConfidence_;
				Quaternion rotOffset = Quaternion.Slerp(
					Quaternion.identity,
					FixLookDirection(state_.GetRotationOffsetEuler()),
					signalConfidence_);

				// 0.7.0: the Transform is READ ONLY from here on. See ApplyViewMatrixOverride.
				Transform t = cam.transform;
				Vector3 finalPosition = t.position;
				Quaternion finalRotation = t.rotation;

				// 0.4.0: the car-anchored rig - see the big comment on the anchor fields for why this
				// replaces the old additive write. Both branches below produce a final pose; the rig
				// branch builds it outright from the car, the fallback keeps the historical additive
				// behaviour for when there's no car to anchor to.
				Transform car = (hasCarAnchor_ && config_.OrbitModeEnabled) ? GetCarTransform() : null;

				// 0.4.4 - REGRESSION FIX.
				//
				// 0.4.0 through 0.4.3 ASSIGNED the camera pose here instead of adding to it:
				//
				//     basePosition = car.TransformPoint(anchorLocalPosition_);
				//     baseRotation = car.rotation * anchorLocalRotation_;
				//     t.position   = basePosition + baseRotation * posOffset;
				//     t.rotation   = baseRotation * rotOffset;
				//
				// Both of those base values are CONSTANT in the car's frame, so assigning them welded
				// the camera rigidly to the car: no chase-cam sway, no follow lag, no velocity offset,
				// no damping - every bit of motion CarX's own camera produces was thrown away and
				// replaced with a fixed seat. And because the phone's tracked delta sits at or near
				// zero most of the time (real logs show appliedOffsetEuler of (0,0,0) and (0,0,1) for
				// long stretches), the camera then held a pixel-perfect fixed pose relative to the car
				// while the world and other vehicles streamed past. That is precisely the reported
				// symptom, and it was strictly WORSE than the behaviour it replaced - 0.3.31 and
				// earlier at least inherited all of CarX's own camera movement.
				//
				// Everything below is additive again, exactly like 0.3.31: whatever CarX's Cinemachine
				// solve wrote this frame is kept and only perturbed. The orbit contribution is applied
				// as a DISPLACEMENT relative to the calibrated seat, which is identically zero when the
				// phone is at its neutral pose - so at worst this mod is a no-op and the camera behaves
				// exactly as the game intends. It is now structurally impossible for this code to
				// freeze the camera.
				if (car != null) {
					Vector3 pivotLocal = OrbitPivotLocal();
					Vector3 boomLocal = anchorLocalPosition_ - pivotLocal;

					// Orbit gets its own gain (0.4.3) because the measured rotation delta is only 5-10
					// degrees in normal use; at 1:1 on a ~4.7 m boom that is under a metre of travel.
					float orbitGain = Mathf.Max(0.1f, config_.OrbitSensitivity);
					Vector3 offsetEuler = lastAppliedOffsetEuler_ * orbitGain;

					// 0.4.6: CLAMP THE ORBIT ARC. Measured from a real 0.4.5 session, the unclamped
					// version at the old 5x default produced orbitEuler of (x=-146, y=174) - i.e. the
					// camera swung essentially all the way around the car AND flipped underneath it.
					// End-of-render positions in that same window spanned 10 m on every axis with y
					// reaching -8.93, several metres below the car. A camera buried under the track or
					// inside the car's mesh renders the inside of geometry: a flat, featureless, nearly
					// unchanging image - which looks exactly like "the camera is not moving at all",
					// while every diagnostic correctly reports large movement. Unbounded orbit is
					// therefore not just uncomfortable, it is self-defeating.
					//
					// Yaw is allowed a wide but bounded arc; pitch is kept small, because pitch is the
					// axis that drives the camera under the car and through the ground.
					offsetEuler.y = Mathf.Clamp(offsetEuler.y, -MaxOrbitYawDegrees, MaxOrbitYawDegrees);
					offsetEuler.x = Mathf.Clamp(offsetEuler.x, -MaxOrbitPitchDegrees, MaxOrbitPitchDegrees);

					Quaternion orbit = Quaternion.AngleAxis(offsetEuler.y, Vector3.up) *
					                   Quaternion.AngleAxis(offsetEuler.x, Vector3.right);

					// The displacement the orbit implies, in the car's local frame. Zero when the phone
					// is neutral (orbit == identity => orbitedLocal == anchorLocalPosition_), which is
					// what guarantees this can never subtract CarX's own motion.
					Vector3 orbitedLocal = pivotLocal + orbit * boomLocal;

					// Hard floor in the car's own frame: never let the orbit put the camera below where
					// it was calibrated. Even with pitch clamped, a low calibration seat plus a downward
					// swing can still dip under the car, and being underground is the specific failure
					// that renders as a static void.
					if (orbitedLocal.y < anchorLocalPosition_.y) {
						orbitedLocal.y = anchorLocalPosition_.y;
					}

					Vector3 orbitDeltaLocal = orbitedLocal - anchorLocalPosition_;
					Vector3 orbitDeltaWorld = car.rotation * orbitDeltaLocal;

					lastOrbitEuler_ = offsetEuler;
					lastOrbitTravel_ = orbitDeltaLocal.magnitude;

					if (config_.ClippingGuardEnabled && posOffset.sqrMagnitude > 1e-6f) {
						posOffset = ApplyClippingGuard(t.position, t.rotation, posOffset);
					}

					lastAppliedPosOffset_ = posOffset;

					Vector3 originalPosition = t.position;
					Vector3 newPosition = originalPosition + orbitDeltaWorld + t.rotation * posOffset;

					// Swing the aim to match the new vantage point, as a CORRECTION on top of CarX's own
					// aim rather than a replacement for it: the rotation that takes the old view
					// direction of the framed point to the new one. Identity when orbitDeltaWorld is
					// zero, so CarX's aiming and sway survive untouched at neutral.
					Vector3 pivotWorld = car.TransformPoint(pivotLocal);
					Vector3 oldAim = pivotWorld - originalPosition;
					Vector3 newAim = pivotWorld - newPosition;
					Quaternion aimCorrection = (oldAim.sqrMagnitude > 1e-8f && newAim.sqrMagnitude > 1e-8f)
						? Quaternion.FromToRotation(oldAim, newAim)
						: Quaternion.identity;

					finalPosition = newPosition;

					// 0.5.5: same stable-axis treatment as the non-orbit path - `* rotOffset` on the end
					// would apply the phone delta about the camera's own tilting axes and reintroduce the
					// sway-coupled wobble here. aimCorrection stays a world-space pre-multiply.
					Quaternion orbitStable = GetStableOffsetFrame(t);
					Quaternion orbitStableDelta = orbitStable * rotOffset * Quaternion.Inverse(orbitStable);
					finalRotation = orbitStableDelta * aimCorrection * t.rotation;
				}
				else {
					// 0.5.4 - FIXES THE DRIVING/DRIFTING SHAKE.
					//
					// This used to be `t.position += t.rotation * posOffset`, i.e. the translation was
					// expressed in the camera's LIVE rotation. That rotation is anything but steady while
					// driving: CarX.FollowCamera adds sway (m_SwaySpeed / m_BaseSwayAmount /
					// m_TrackingSwayAmount) and, during a drift, the camera yaws hard to follow the car's
					// slip angle. Rotating a fixed phone offset by a rapidly swinging rotation sweeps the
					// resulting WORLD offset around, so a perfectly still phone still produced a camera
					// that wobbled - and it scaled with how violently the camera was moving. Parked, the
					// rotation is steady and the same offset is rock solid, which is exactly the reported
					// "shakes while driving, fine when stopped".
					//
					// The offset frame is now taken from the CAR's heading (yaw only) instead. The car's
					// yaw changes smoothly and carries none of the camera's sway or drift-follow
					// swing, so a still phone gives a still offset. Yaw-only also means leaning never
					// gets tilted into the vertical axis by camera pitch/roll. "Left" is now left
					// relative to the car, which is also the more intuitive mapping.
					Quaternion offsetFrame = GetStableOffsetFrame(t);

					if (config_.ClippingGuardEnabled && posOffset.sqrMagnitude > 1e-6f) {
						posOffset = ApplyClippingGuard(t.position, offsetFrame, posOffset);
					}

					lastAppliedPosOffset_ = posOffset;

					finalPosition = t.position + offsetFrame * posOffset;

					// 0.5.5 - THE REMAINING SHAKE. Same bug as the translation frame (0.5.4), left in
					// place for rotation.
					//
					// This was `t.rotation = t.rotation * rotOffset` - a POST-multiply, which applies the
					// phone's delta about the CAMERA'S OWN axes. While driving those axes are constantly
					// tilting: CarX.FollowCamera pitches and rolls the camera (sway) and yaws it hard to
					// follow a drift. So a CONSTANT phone offset produced a CHANGING world-space
					// rotation - the aim wobbled even with the phone perfectly still, and the wobble
					// tracked the sway. Parked, the axes are steady and the same offset is rock solid,
					// which is exactly the reported "jitters whenever the car moves".
					//
					// Measured in tools/rotation_frame_proof.py: 1.89 degrees of oscillating aim error
					// while driving, 0.0000 with the fix. That is the right order of magnitude for the
					// real thing - a live 0.5.4 driving capture showed rotation-direction reversals
					// averaging 2.57 degrees and peaking at 6.38.
					//
					// Applying it as a similarity transform in the stable frame
					// (s * rotOffset * inverse(s), pre-multiplied) rotates the camera about the CAR's
					// heading and world up instead: phone yaw always yaws about world up, phone pitch
					// always pitches about the car's right axis, regardless of how the camera is rolling
					// or swinging. The contribution is then constant for a constant phone pose, which is
					// the property that removes the shake.
					Quaternion stableDelta = offsetFrame * rotOffset * Quaternion.Inverse(offsetFrame);
					finalRotation = stableDelta * t.rotation;
				}

				// 0.7.0: the ONLY place the camera is affected - a view-matrix override. The Transform
				// still holds exactly what CarX and Cinemachine put there.
				ApplyViewMatrixOverride(cam, finalPosition, finalRotation);

				lastCameraWorldPosAfterWrite_ = finalPosition;
				poseWrittenPosition_ = finalPosition;
				poseWrittenThisFrame_ = true;
			}
			else {
				ClearViewMatrixOverride(cam);
			}

			// 0.7.0: the Photo-Mode-only projection/view override is gone. It existed to win against
			// Kino's custom camera possibly reasserting a matrix, but we now set worldToCameraMatrix
			// unconditionally every frame in onBeforeRender - before HDRP reads it - which wins by
			// construction, and we never touch projectionMatrix so TAA jitter is preserved everywhere.
		}

		// 0.7.0: ApplyCameraOverride / ResetCameraOverride / ResetCameraOverrideIfApplied removed.
		// They set projectionMatrix as well as the view matrix, which is what broke HDRP's TAA jitter
		// and shadow culling (0.3.19), and the unconditional reset was itself a per-frame defect
		// (0.5.3). ApplyViewMatrixOverride replaces all three and never touches the projection.


		/// <summary>
		/// SRP (URP/HDRP) equivalent of <see cref="OnCameraPreCull"/> - see the comment on the
		/// subscription in <see cref="Start"/> for why both are hooked. Same logic either way, so
		/// this just forwards into the existing handler rather than duplicating it.
		/// </summary>
		private void OnBeginCameraRendering(ScriptableRenderContext context, Camera cam) {
			OnCameraPreCull(cam);
		}

		/// <summary>
		/// 0.3.21 diagnostic, added specifically to chase the "rotation visibly works, translation
		/// never does - even sitting still, moving only the phone" report. Every previous
		/// diagnostic (cameraWorldPosAfterWrite in OnCameraPreCull) reads the camera back
		/// immediately after THIS mod's own write, which can only prove the write happened - it
		/// can't see anything that might happen to the camera *afterward*, later in the same
		/// frame's render. This fires after Unity has already finished rendering this camera - as
		/// late as its state can be observed - and logs two independent readings:
		///
		/// 1. <c>cam.transform.position</c> - the plain Transform value. If this differs from what
		///    the heartbeat's cameraWorldPosAfterWrite showed earlier the same frame, something
		///    reset the Transform itself between our write and the actual render.
		/// 2. The position decoded directly out of the camera's *current*
		///    <c>worldToCameraMatrix</c> (by inverting it and transforming the origin through it) -
		///    this reflects whatever matrix the render pipeline actually used to produce this
		///    camera's pixels, independent of the Transform. If this disagrees with #1, something
		///    reassigned worldToCameraMatrix specifically (e.g. a stabilization/anti-shake render
		///    feature, or Kino's own camera system reasserting itself) without ever touching the
		///    Transform - which would make every earlier diagnostic look correct while the screen
		///    still never moves, exactly matching the report.
		///
		/// Comparing all three numbers (this frame's cameraWorldPosAfterWrite, and these two) is
		/// the whole point - whichever pair first disagrees pinpoints where between "this mod wrote
		/// an offset" and "pixels hit the screen" the movement is actually getting lost.
		/// </summary>
		private void OnEndCameraRendering(ScriptableRenderContext context, Camera cam) {
			if (!config_.Enabled || cam == null || cam.targetTexture != null || cam.orthographic) return;

			// 0.6.4: the drift check has to happen BEFORE the revert below, otherwise it measures our
			// own revert and reports it as an external overwrite. It did exactly that - a real log
			// carried 2,923 self-inflicted OVERWRITTEN warnings, one per frame, which was both
			// meaningless and a large part of the logging load described below.
			if (config_.VerboseDiagnostics && poseWrittenThisFrame_) {
				float drift = (cam.transform.position - poseWrittenPosition_).magnitude;
				if (drift > 0.01f && ShouldLogVerbose()) {
					Kino.Log.Warning(
						$"[HeadTrackARKit][diag] OVERWRITTEN: wrote {FormatVector(poseWrittenPosition_)} in " +
						$"onBeforeRender, but cam='{cam.name}' reads {FormatVector(cam.transform.position)} at " +
						$"end of render ({drift:F2} m) - something else is writing this camera.");
				}
			}

			// 0.6.3's post-render revert is GONE as of 0.7.0 - there is nothing to revert, because the
			// Transform is never written. See ApplyViewMatrixOverride.

			// 0.6.4 - PER-FRAME LOGGING REMOVED FROM THE HOT PATH.
			//
			// This block used to log unconditionally, once per frame per camera, since 0.3.21. A real
			// 0.6.3 session produced 7,252 PhoneCam lines - 84% of the entire game log, 1.8 MB in a few
			// minutes - of which 3,307 were this line and 2,923 were the self-triggered warning above.
			// At 144 fps that is ~144 synchronous log writes per second, each formatting four Vector3s
			// (at F4, so long strings). Per-frame disk I/O of that volume produces frame-time spikes,
			// and a spike is far more visible when the whole scene is streaming past at speed than when
			// parked - which matches "only when I'm driving" exactly.
			//
			// It is now gated behind VerboseDiagnostics (off by default) and rate-limited even then.
			if (!config_.VerboseDiagnostics || !ShouldLogVerbose()) return;

			Vector3 transformPos = cam.transform.position;
			Vector3 matrixDecodedPos = cam.worldToCameraMatrix.inverse.MultiplyPoint3x4(Vector3.zero);
			Vector3 transformForward = cam.transform.forward;
			Vector3 matrixDecodedForward = cam.worldToCameraMatrix.inverse.MultiplyVector(new Vector3(0f, 0f, -1f)).normalized;

			Kino.Log.Info(
				$"[HeadTrackARKit][diag] endOfRender cam='{cam.name}' transformPos={FormatVector(transformPos)} matrixDecodedPos={FormatVector(matrixDecodedPos)} " +
				$"transformFwd={FormatVector(transformForward)} matrixDecodedFwd={FormatVector(matrixDecodedForward)}");
		}

		/// <summary>
		/// 0.6.4: rate limiter for the verbose per-frame diagnostics, so even with them enabled the log
		/// gets one sample every couple of seconds rather than one per frame.
		/// </summary>
		private bool ShouldLogVerbose() {
			if (Time.unscaledTime - lastVerboseLogTime_ < VerboseLogIntervalSeconds) return false;
			lastVerboseLogTime_ = Time.unscaledTime;
			return true;
		}


		/// <summary>
		/// Raycasts from <paramref name="originPosition"/>/<paramref name="originRotation"/> -
		/// the camera's current (pre-offset) position/rotation for this frame - along the
		/// direction of the desired head-offset, and clamps the offset short of anything it hits.
		/// Prevents the tracked camera from poking through the dashboard/seat/window when leaning
		/// in.
		///
		/// This is off by default (see IHeadTrackConfig.ClippingGuardEnabled) - it needs the
		/// layer mask tuned against CarX's actual cockpit collision geometry, which isn't
		/// something that could be verified without the real game running. See README.
		/// </summary>
		private Vector3 ApplyClippingGuard(Vector3 originPosition, Quaternion originRotation, Vector3 localPosOffset) {
			Vector3 worldOffset = originRotation * localPosOffset;
			float distance = worldOffset.magnitude;
			if (distance < 1e-5f) return localPosOffset;

			Vector3 direction = worldOffset / distance;
			int layerMask = config_.ClippingGuardLayerMask;
			float castDistance = distance + config_.ClippingGuardMargin;

			if (Physics.Raycast(originPosition, direction, out RaycastHit hit, castDistance, layerMask, QueryTriggerInteraction.Ignore)) {
				float allowedDistance = Mathf.Max(0f, hit.distance - config_.ClippingGuardMargin);
				if (allowedDistance < distance) {
					Vector3 clampedWorld = direction * allowedDistance;
					// Convert the clamped world-space distance back into the anchor's local offset space.
					return Quaternion.Inverse(originRotation) * clampedWorld;
				}
			}

			return localPosOffset;
		}

		/// <summary>
		/// No longer used to gate whether the offset gets applied (see the comment in
		/// <see cref="OnCameraPreCull"/> for why - as of 0.3.5 that's decided per-camera by
		/// whether it renders on-screen at all, not by which system CarX/Kino currently considers
		/// "active"). Kept purely for the diagnostic heartbeat log, since it's still useful to see
		/// what this resolves to compared to what's actually rendering:
		///
		/// 1. <c>CameraSwitch.instance.FindActiveCamera()</c> - confirmed via direct inspection
		///    of Assembly-CSharp.dll's metadata: <c>CameraSwitch</c> is CarX's own public
		///    singleton manager for every camera mode (its <c>ECameraType</c> enum literally
		///    lists Race, Follow, Replay, and PhotoSession), and <c>FindActiveCamera()</c> is a
		///    public method that returns whichever <c>CarX.BaseCamera</c>-derived controller is
		///    currently active. <c>BaseCamera</c> itself extends <c>UnityEngine.MonoBehaviour</c>
		///    (also confirmed via the assembly), so the actual render <c>Camera</c> component
		///    sits on the same GameObject - reachable with a plain <c>GetComponent&lt;Camera&gt;()</c>,
		///    no reflection needed since both types and members involved are public.
		/// 2. The previous Photo-Mode-specific fallback (reflecting into
		///    <c>UIPhotoModeContext.m_camera</c>) - kept in case CameraSwitch ever doesn't cover
		///    Photo Mode's camera for some reason.
		/// 3. <c>Camera.main</c> - last-resort fallback if both of the above come back null,
		///    e.g. before any camera has been set up yet (main menu, loading).
		/// </summary>
		private Camera GetActiveCamera() {
			CameraSwitch cameraSwitch = CameraSwitch.instance;
			if (cameraSwitch != null) {
				CarX.BaseCamera active = cameraSwitch.FindActiveCamera();
				if (active != null) {
					Camera cam = active.GetComponent<Camera>();
					if (cam != null) return cam;
				}
			}

			if (IsInPhotoMode() && PhotoModeCameraField != null) {
				if (PhotoModeCameraField.GetValue(photoModeContext_) is Camera photoCam && photoCam != null) {
					return photoCam;
				}
			}

			return GetMainCamera();
		}

		/// <summary>
		/// Logs ground-truth data about what's actually rendering, since Kino's own camera
		/// system (kino.dll) can't be statically inspected (no readable .NET metadata - it's
		/// obfuscated). Two things get logged to KSL's log:
		/// 1. Every distinct camera name Unity calls OnPreCull for, the first time it's seen
		///    (deduped by name so this doesn't spam every frame), tagged with whether
		///    GetActiveCamera() currently considers it "the" active one.
		/// 2. Every ~2 seconds, a heartbeat line showing what GetActiveCamera() resolves to by
		///    name (or "null"), whether CameraSwitch.instance itself was found at all, and the
		///    current calibrated/enabled state - so log timestamps can be matched up against
		///    when you were actually moving your head in-game.
		/// </summary>
		private void LogCameraDiagnostics(Camera cam) {
			if (cam == null) return;

			if (loggedCameraNames_.Add(cam.name)) {
				Camera active = GetActiveCamera();
				Kino.Log.Info(
					$"[HeadTrackARKit][diag] Camera seen: '{cam.name}' tag={cam.tag} " +
					$"targetTexture={(cam.targetTexture != null ? "yes" : "no")} " +
					$"depth={cam.depth} isResolvedActive={cam == active}");
				LogCameraOwnership(cam);
			}

			if (Time.unscaledTime - lastDiagnosticLogTime_ > 2f) {
				lastDiagnosticLogTime_ = Time.unscaledTime;
				Camera active = GetActiveCamera();
				bool switchFound = CameraSwitch.instance != null;
				Kino.Log.Info(
					$"[HeadTrackARKit][diag] GetActiveCamera() -> {(active != null ? active.name : "null")}, " +
					$"CameraSwitch.instance found={switchFound}, calibrated={state_.IsCalibrated}, " +
					$"photoMode={IsInPhotoMode()}");
				// Axis-mapping diagnostics: incoming Unity-space euler (raw) vs. the final offset
				// actually applied to the camera (post invert, no swap as of 0.3.13), plus the
				// current invert settings - confirms FixLookDirection is doing what it should.
				Kino.Log.Info(
					$"[HeadTrackARKit][diag] incomingEuler={FormatEuler(lastRawArEuler_)} " +
					$"appliedOffsetEuler={FormatEuler(lastAppliedOffsetEuler_)} " +
					$"invertPitch={config_.InvertPitch} invertYaw={config_.InvertYaw}");
				// 0.3.30: the position diagnostic line right below has always logged
				// maxPositionOffset/positionSensitivity alongside it - this one never got the same
				// treatment, which meant a real log showing appliedOffsetEuler swinging by 50-120
				// degrees between consecutive heartbeats (this session) had no way to confirm or
				// rule out RotationSensitivity as the cause. PositionSensitivity has already needed
				// two separate corrections for a stale/inflated saved value in the past (see
				// SensitivityDiagnosticReverted, PositionSensitivityBoosted) - RotationSensitivity
				// has never had the same visibility, so there's no way to know from a log alone
				// whether it's sitting at a sane value.
				Kino.Log.Info(
					$"[HeadTrackARKit][diag] maxRotationOffset={config_.MaxRotationOffset:F2} " +
					$"rotationSensitivity={config_.RotationSensitivity:F2}");
				// 0.5.2: how many frames arrived with the camera still holding OUR pose rather than a
				// freshly-computed one. A non-zero and climbing count confirms CinemachineBrain is
				// skipping frames (SmartUpdate on a physics-tracked target), which is what used to make
				// the additive write compound into a shake. Now it just means the base was restored.
				Kino.Log.Info(
					$"[HeadTrackARKit][diag] viewMatrixOverride={viewMatrixOverridden_} " +
					$"filterMinCutoff={config_.FilterMinCutoffHz:F2}Hz filterSpeedCoef={config_.FilterSpeedCoefficient:F3}");
				// 0.4.0: which camera mode is actually in effect. "rig" means the car-anchored
				// reconstruction is driving the camera; "additive-fallback" means no car could be
				// resolved so the old behaviour is in play. If this ever reads additive-fallback
				// while seated in a car, ResolveCarTransform is what needs looking at, not the math.
				Transform diagCar = hasCarAnchor_ ? GetCarTransform() : null;
				Kino.Log.Info(
					$"[HeadTrackARKit][diag] cameraMode={(diagCar != null ? "rig(car-anchored)" : "additive-fallback")} " +
					$"hasCarAnchor={hasCarAnchor_} car={(diagCar != null ? diagCar.name : "(none)")} " +
					$"anchorLocalPos={FormatVector(anchorLocalPosition_)}");

				// 0.4.3: the decisive magnitude line. cameraTravel is how far, in metres, orbit has moved
				// the camera from its calibrated seat. Under ~0.5 m is easy to mistake for the chase cam's
				// own sway; a few metres is unmistakable. If this number is large and the screen still
				// looks static, the problem is downstream of the pose write, not in the tracking.
				if (config_.OrbitModeEnabled) {
					Kino.Log.Info(
						$"[HeadTrackARKit][diag] orbitEuler={FormatEuler(lastOrbitEuler_)} " +
						$"orbitSensitivity={config_.OrbitSensitivity:F2} " +
						$"cameraTravelFromSeat={lastOrbitTravel_:F2}m");
				}

				// 0.4.1: state the two hard preconditions outright, every heartbeat. Working out that
				// a whole session had Enabled=false previously required noticing which log lines were
				// *absent* (no "Listening for LOTA", no "Neutral position set", no toggle event) and
				// inferring backwards - which is a bad way to read a log. If the camera isn't moving,
				// exactly one of these two being false is the first thing to rule out, so it should be
				// stated positively rather than reconstructed.
				// 0.4.2: state whether the phone is actually sending positional data. See
				// TrackPositionalSignalRange - this is the measurement that explains the entire
				// "objects move but the camera doesn't" report, so it belongs in every heartbeat.
				if (positionRangeSeeded_) {
					Vector3 span = positionRangeMax_ - positionRangeMin_;
					float widest = Mathf.Max(span.x, Mathf.Max(span.y, span.z));
					bool positionalDead = widest < PositionalTrackingDeadThresholdM;
					Kino.Log.Info(
						$"[HeadTrackARKit][diag] positionalSignalRange={FormatVector(span)} widest={widest:F3}m " +
						$"positionalTrackingLooksDead={positionalDead} orbitMode={config_.OrbitModeEnabled}");
					if (positionalDead) {
						Kino.Log.Warning(
							$"[HeadTrackARKit][diag] LOTA is sending rotation but effectively no POSITION " +
							$"(total spread {widest:F3} m). That is ARKit attitude-only - check LOTA has camera " +
							"permission on the phone and is in a full world-tracking/6DOF mode. Orbit mode is " +
							$"{(config_.OrbitModeEnabled ? "ON, so rotation still moves the camera around the car" : "OFF, so rotation can only pivot the camera in place")}.");
					}
				}

				if (!config_.Enabled) {
					Kino.Log.Warning(
						"[HeadTrackARKit][diag] INERT: config.Enabled=false - OnCameraPreCull returns before " +
						"touching the camera and the OSC listener is not started. Nothing can move until " +
						"'Enabled' is ticked in the PhoneCam settings panel.");
				}
				else if (!state_.IsCalibrated) {
					Kino.Log.Warning(
						"[HeadTrackARKit][diag] INERT: not calibrated - the camera pose block is skipped " +
						"entirely until F9 is pressed while data is arriving.");
				}
				// Position diagnostics (0.3.14) - see the field comments on lastRawArPosition_/
				// lastAppliedPosOffset_ for why this exists.
				Kino.Log.Info(
					$"[HeadTrackARKit][diag] incomingPos={FormatVector(lastRawArPosition_)} " +
					$"appliedPosOffset={FormatVector(lastAppliedPosOffset_)} " +
					$"maxPositionOffset={config_.MaxPositionOffset:F2} positionSensitivity={config_.PositionSensitivity:F2}");
				// 0.3.17: ground truth - if this value isn't changing frame to frame while you're
				// physically stepping side to side, this mod's Transform write isn't the problem;
				// something else is overwriting the camera afterward. If it IS changing but the
				// screen doesn't show it, the issue is downstream of this mod entirely (rendering/
				// camera stacking). Compare consecutive lines of this specifically, not just once.
				Kino.Log.Info(
					$"[HeadTrackARKit][diag] cameraWorldPosAfterWrite={FormatVector(lastCameraWorldPosAfterWrite_)}");
				// 0.3.23: continuous companion to CheckOscSignalHealth's edge-triggered warning -
				// this prints every heartbeat regardless of whether an outage is currently
				// happening, so a future log shows the OSC connection's health directly instead of
				// needing incomingPos/incomingEuler manually diffed line-by-line to notice a long
				// stretch of frozen, identical values (that's how the 48+ second gap that prompted
				// this got found in the first place).
				int msSinceLastOscPacket = receiver_.LastMessageTick == 0 ? -1 : Environment.TickCount - receiver_.LastMessageTick;
				// 0.3.24: totalRawPacketsReceived counts every UDP datagram that hits the socket,
				// parsed or not - if this keeps climbing while oscMsSinceLastPacket also keeps
				// climbing (i.e. packets are arriving but LastMessageTick isn't advancing),
				// packets are reaching the PC but failing to turn into a usable message
				// (previously: bundle-wrapped packets being silently rejected outright - now
				// unwrapped, see OscParser.ParseMessages). If both are flat/frozen together,
				// nothing is reaching the socket at all - phone/Wi-Fi/LOTA-side, not this mod.
				Kino.Log.Info(
					$"[HeadTrackARKit][diag] oscMsSinceLastPacket={msSinceLastOscPacket} " +
					$"totalRawPacketsReceived={receiver_.TotalRawPacketsReceived} " +
					$"receiverRunning={receiver_.IsRunning} lastSender={receiver_.LastSenderAddress ?? "(none)"}");
			}
		}

		/// <summary>
		/// True if a packet has actually landed on the socket within the last 750ms - the same
		/// threshold the settings panel's "Status:" line has always used, now shared with OnGUI's
		/// always-visible overlay (see the doc comment there) so both places agree on what
		/// "connected" means.
		/// </summary>
		private bool IsReceivingData() {
			return receiver_.IsRunning &&
			       receiver_.LastMessageTick != 0 &&
			       Environment.TickCount - receiver_.LastMessageTick < 750;
		}

		/// <summary>
		/// The Transform this mod anchors the camera rig to - the player's current car - or null if
		/// there isn't one right now (garage, menus, or any state where CameraSwitch has no target).
		/// Cached for a second at a time, same reasoning as <see cref="GetMainCamera"/>: this is
		/// consulted from a per-camera render callback, so it must not do reflection every call.
		/// </summary>
		private Transform GetCarTransform() {
			// Unity's overloaded == makes a destroyed object compare equal to null, so this also
			// correctly re-resolves after a car is despawned rather than holding a dead reference.
			if (carTransform_ != null && Time.unscaledTime - carCacheTime_ < 1f) {
				return carTransform_;
			}

			carCacheTime_ = Time.unscaledTime;
			carTransform_ = ResolveCarTransform();
			return carTransform_;
		}

		/// <summary>
		/// Layered lookup for the player's car Transform, most-authoritative first. Every layer is
		/// wrapped so a failure in one falls through to the next rather than throwing into game code.
		/// </summary>
		private static Transform ResolveCarTransform() {
			CameraSwitch cameraSwitch = CameraSwitch.instance;
			if (cameraSwitch == null) return null;

			// 1. The public accessors, confirmed present and zero-parameter in Assembly-CSharp.
			Transform fromMethod = AsTransform(SafeInvoke(CameraSwitchGetCarMethod, cameraSwitch));
			if (fromMethod != null) return fromMethod;

			Transform fromProperty = AsTransform(SafeGetProperty(CameraSwitchTargetRaceCarProperty, cameraSwitch));
			if (fromProperty != null) return fromProperty;

			// 2. Private backing fields, for states where the public target isn't populated yet.
			foreach (string fieldName in CameraSwitchCarFieldNames) {
				FieldInfo field = typeof(CameraSwitch).GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
				if (field == null) continue;
				Transform fromField = AsTransform(SafeGetField(field, cameraSwitch));
				if (fromField != null) return fromField;
			}

			// 3. Whatever the active camera controller is aiming at. BaseCamera-derived controllers
			//    keep their follow target in m_target (confirmed on CarX.FollowCamera), which is the
			//    car (or a rig node parented under it) in every mode that has one.
			CarX.BaseCamera active = cameraSwitch.FindActiveCamera();
			if (active != null) {
				FieldInfo targetField = active.GetType().GetField("m_target", BindingFlags.NonPublic | BindingFlags.Instance);
				if (targetField != null) {
					Transform fromTarget = AsTransform(SafeGetField(targetField, active));
					if (fromTarget != null) return fromTarget;
				}
			}

			return null;
		}

		private static object SafeInvoke(MethodInfo method, object target) {
			if (method == null || target == null) return null;
			try {
				return method.Invoke(target, null);
			}
			catch {
				return null;
			}
		}

		private static object SafeGetProperty(PropertyInfo property, object target) {
			if (property == null || target == null || !property.CanRead) return null;
			try {
				return property.GetValue(target, null);
			}
			catch {
				return null;
			}
		}

		private static object SafeGetField(FieldInfo field, object target) {
			if (field == null || target == null) return null;
			try {
				return field.GetValue(target);
			}
			catch {
				return null;
			}
		}

		/// <summary>
		/// Coerces whatever a car accessor handed back into a Transform, accepting the three shapes
		/// it can realistically be (a Transform, any Component on the car, or the GameObject itself)
		/// so this doesn't depend on the exact declared return type staying put across game updates.
		/// </summary>
		private static Transform AsTransform(object value) {
			switch (value) {
				case null:
					return null;
				case Transform transform:
					return transform != null ? transform : null;
				case GameObject gameObject:
					return gameObject != null ? gameObject.transform : null;
				case Component component:
					return component != null ? component.transform : null;
				default:
					return null;
			}
		}

		/// <summary>
		/// 0.4.1: dumps who actually owns a camera's Transform, once per distinct camera. Added
		/// because several of the open questions about this mod ("is the offset going to the camera
		/// that's really rendering", "does something restore the transform after we write it", "does a
		/// parent transform override the child") are not answerable by watching position values - they
		/// need the object graph. This logs it directly instead:
		///
		/// - the full parent chain, since a camera parented under a rig node is moved by that node and
		///   writing world-space position to the child is then fighting the parent every frame;
		/// - every component on the camera's own GameObject, which is what identifies the systems that
		///   can write the transform in LateUpdate. A CinemachineBrain here is the important one:
		///   Assembly-CSharp shows CarX's chase cam (CarX.FollowCamera) drives a
		///   CinemachineVirtualCamera, and it is the Brain on the camera object that applies the
		///   virtual camera's solved pose to this Transform;
		/// - whether the camera object is the same GameObject the Brain/controller lives on.
		///
		/// Read-only and first-sighting-only, so it cannot perturb what it's measuring or spam.
		/// </summary>
		private static void LogCameraOwnership(Camera cam) {
			try {
				Transform t = cam.transform;

				var chain = new List<string>();
				for (Transform p = t; p != null; p = p.parent) {
					chain.Add(p.name);
				}
				chain.Reverse();
				Kino.Log.Info(
					$"[HeadTrackARKit][diag]   hierarchy: {string.Join(" / ", chain.ToArray())} " +
					$"(depth={chain.Count}, parent={(t.parent != null ? t.parent.name : "(root)")})");

				Component[] components = cam.GetComponents<Component>();
				var names = new List<string>();
				bool hasBrain = false;
				foreach (Component c in components) {
					if (c == null) continue;
					string typeName = c.GetType().Name;
					names.Add(typeName);
					if (typeName.IndexOf("CinemachineBrain", StringComparison.OrdinalIgnoreCase) >= 0) {
						hasBrain = true;
					}
				}
				Kino.Log.Info(
					$"[HeadTrackARKit][diag]   components on '{cam.name}': {string.Join(", ", names.ToArray())}");
				Kino.Log.Info(
					$"[HeadTrackARKit][diag]   cinemachineBrainPresent={hasBrain} " +
					$"localPos={FormatVector(t.localPosition)} worldPos={FormatVector(t.position)}");
			}
			catch (Exception ex) {
				// Diagnostics must never be able to take the mod down.
				Kino.Log.Warning($"[HeadTrackARKit][diag] camera ownership dump failed: {ex.Message}");
			}
		}

		/// <summary>
		/// The point, in car-local space, that orbit mode swings the camera around and keeps aimed at.
		/// Derived from the calibrated pose rather than hardcoded, so it preserves whatever framing the
		/// camera had at F9: step forward from the camera's calibrated position, along its calibrated
		/// forward, by the boom length. For a chase cam calibrated at roughly (0, 2.8, -3.9) looking
		/// forward and slightly down, that lands on the car - so orbiting keeps the car framed instead
		/// of swinging it out of shot.
		/// </summary>
		private Vector3 OrbitPivotLocal() {
			float boomLength = anchorLocalPosition_.magnitude;
			if (boomLength < 0.01f) {
				// Calibrated essentially at the car's own origin (cockpit view). There's no boom to
				// orbit on, so orbit about the car origin itself.
				return Vector3.zero;
			}

			Vector3 forwardLocal = anchorLocalRotation_ * Vector3.forward;
			return anchorLocalPosition_ + forwardLocal * boomLength;
		}

		/// <summary>
		/// 0.4.2: tracks the spread of the incoming ARKit position stream so a dead positional signal
		/// is reported outright instead of having to be inferred. A phone being physically moved
		/// produces metres of range here; the measured 0.4.1 session produced 9 cm total while
		/// simultaneously reporting 359 degrees of yaw, which is the signature of ARKit running
		/// attitude-only (no visual world tracking - typically camera permission not granted to LOTA,
		/// or an orientation-only session configuration).
		/// </summary>
		private void TrackPositionalSignalRange(Vector3 rawPosition) {
			if (!positionRangeSeeded_) {
				positionRangeMin_ = rawPosition;
				positionRangeMax_ = rawPosition;
				positionRangeSeeded_ = true;
				return;
			}

			positionRangeMin_ = Vector3.Min(positionRangeMin_, rawPosition);
			positionRangeMax_ = Vector3.Max(positionRangeMax_, rawPosition);
		}

		private bool IsInPhotoMode() {
			if (photoModeContext_ == null) {
				// FindAnyObjectByType, not FindFirstObjectByType - we don't care which instance,
				// just whether one exists, and Unity's own warning recommends this as the
				// faster option when that's the case.
				photoModeContext_ = UnityEngine.Object.FindAnyObjectByType<UIPhotoModeContext>();
			}
			return photoModeContext_ != null && photoModeContext_.isActive;
		}

		private Camera GetMainCamera() {
			// Camera.main does a tag lookup every call - cache it and refresh periodically
			// rather than on every single camera in the scene, every frame.
			if (cachedCamera_ != null && Time.unscaledTime - cameraCacheTime_ < 1f) {
				return cachedCamera_;
			}

			cachedCamera_ = Camera.main;
			cameraCacheTime_ = Time.unscaledTime;
			return cachedCamera_;
		}

		/// <summary>
		/// Enumerates this PC's active, non-loopback IPv4 addresses (one per network adapter,
		/// e.g. Wi-Fi and Ethernet both show up if both are connected) - that's what needs typing
		/// into LOTA's Transmission Settings destination IP field. Purely for display; the OSC
		/// listener itself binds to all interfaces (IPAddress.Any) regardless of this value.
		/// </summary>
		private static string AutoDetectLocalIp() {
			try {
				var addresses = new List<string>();
				foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces()) {
					if (ni.OperationalStatus != OperationalStatus.Up) continue;
					if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

					foreach (UnicastIPAddressInformation addr in ni.GetIPProperties().UnicastAddresses) {
						if (addr.Address.AddressFamily == AddressFamily.InterNetwork) {
							addresses.Add(addr.Address.ToString());
						}
					}
				}

				return addresses.Count > 0
					? string.Join(", ", addresses)
					: "(no active network adapter found)";
			}
			catch (Exception ex) {
				return $"(couldn't detect IP: {ex.Message})";
			}
		}

		/// <summary>Re-runs auto-detection and clears any manual override, going back to "auto" mode.</summary>
		private void RefreshLocalIp() {
			config_.LocalIpOverride = "";
			localIpText_ = AutoDetectLocalIp();
		}

		private void CheckForUpdate() {
			if (updateCheckInProgress_) return;
			updateCheckInProgress_ = true;
			updateDownloadUrl_ = null;
			updateStatus_ = "Checking GitHub...";
			StartCoroutine(CheckForUpdateCoroutine());
		}

		private IEnumerator CheckForUpdateCoroutine() {
			using (UnityWebRequest req = UnityWebRequest.Get(UpdateCheckUrl)) {
				// GitHub's API rejects requests with no User-Agent header.
				req.SetRequestHeader("User-Agent", "PhoneCam-KSL-Mod");
				yield return req.SendWebRequest();

				updateCheckInProgress_ = false;

				if (req.responseCode == 404) {
					updateStatus_ = $"No releases published yet on github.com/{UpdateRepoOwner}/{UpdateRepoName}.";
					yield break;
				}

				if (req.result != UnityWebRequest.Result.Success) {
					updateStatus_ = $"Check failed: {req.error}";
					yield break;
				}

				GitHubRelease release;
				try {
					release = JsonUtility.FromJson<GitHubRelease>(req.downloadHandler.text);
				}
				catch (Exception ex) {
					updateStatus_ = $"Check failed: couldn't parse GitHub's response ({ex.Message}).";
					yield break;
				}

				if (release == null || string.IsNullOrEmpty(release.tag_name)) {
					updateStatus_ = "Check failed: unexpected response from GitHub.";
					yield break;
				}

				string latest = release.tag_name.TrimStart('v', 'V');
				updateLatestVersion_ = latest;

				GitHubAsset asset = null;
				if (release.assets != null) {
					foreach (GitHubAsset a in release.assets) {
						if (string.Equals(a.name, UpdateAssetName, StringComparison.OrdinalIgnoreCase)) {
							asset = a;
							break;
						}
					}
				}

				bool isNewer;
				try {
					isNewer = new Version(latest) > new Version(CurrentVersion);
				}
				catch {
					// Tag doesn't parse as a clean major.minor(.build) version - fall back to a
					// plain string comparison so this doesn't hard-fail on an unusual tag name.
					isNewer = !string.Equals(latest, CurrentVersion, StringComparison.OrdinalIgnoreCase);
				}

				if (!isNewer) {
					updateStatus_ = $"Up to date (v{CurrentVersion}).";
				}
				else if (asset == null) {
					updateStatus_ = $"v{latest} is out on GitHub, but no '{UpdateAssetName}' file is attached to that release.";
				}
				else {
					updateStatus_ = $"Update available: v{latest} (you have v{CurrentVersion}).";
					updateDownloadUrl_ = asset.browser_download_url;
				}
			}
		}

		private void DownloadUpdate() {
			if (updateDownloadInProgress_ || string.IsNullOrEmpty(updateDownloadUrl_)) return;
			updateDownloadInProgress_ = true;
			updateStatus_ = "Downloading...";
			StartCoroutine(DownloadUpdateCoroutine(updateDownloadUrl_));
		}

		private IEnumerator DownloadUpdateCoroutine(string url) {
			using (UnityWebRequest req = UnityWebRequest.Get(url)) {
				req.SetRequestHeader("User-Agent", "PhoneCam-KSL-Mod");
				yield return req.SendWebRequest();

				updateDownloadInProgress_ = false;

				if (req.result != UnityWebRequest.Result.Success) {
					updateStatus_ = $"Download failed: {req.error}";
					yield break;
				}

				try {
					string modsDir = GetModsDirectory();
					string finalPath = Path.Combine(modsDir, UpdateAssetName);
					string tempPath = finalPath + ".download";

					File.WriteAllBytes(tempPath, req.downloadHandler.data);

					if (File.Exists(finalPath)) {
						File.Delete(finalPath);
					}
					File.Move(tempPath, finalPath);

					Kino.Log.Info($"[HeadTrackARKit] Downloaded update v{updateLatestVersion_} to '{finalPath}'.");
					updateStatus_ = $"Downloaded v{updateLatestVersion_}. Restart the game to finish updating.";
					updateDownloadUrl_ = null;
				}
				catch (Exception ex) {
					updateStatus_ = $"Download failed: couldn't save the file ({ex.Message}).";
				}
			}
		}

		/// <summary>
		/// kino/mods sits as a sibling of Unity's own "&lt;Product&gt;_Data" folder - matches the
		/// exact path KSL itself logs while scanning for mods at startup, so this is derived from
		/// Application.dataPath rather than hardcoding this PC's install path.
		/// </summary>
		private static string GetModsDirectory() {
			string gameRoot = Directory.GetParent(Application.dataPath).FullName;
			return Path.Combine(Path.Combine(gameRoot, "kino"), "mods");
		}

		private void StartReceiver() {
			try {
				receiver_.Start(config_.OscPort);
				Kino.Log.Info($"[HeadTrackARKit] Listening for LOTA OSC on UDP port {config_.OscPort}.");
			}
			catch (Exception ex) {
				Kino.Log.Error($"[HeadTrackARKit] Failed to start OSC listener on port {config_.OscPort}: {ex.Message}");
			}
		}

		private void SyncStateFromConfig() {
			state_.PositionSensitivity = config_.PositionSensitivity;
			state_.RotationSensitivity = config_.RotationSensitivity;
			state_.PositionSmoothing = config_.PositionSmoothing;
			state_.RotationSmoothing = config_.RotationSmoothing;
			state_.MaxPositionOffset = config_.MaxPositionOffset;
			state_.MaxRotationOffsetDegrees = config_.MaxRotationOffset;
			state_.AdaptiveFilterEnabled = config_.AdaptiveFilterEnabled;
			state_.RotationMinCutoffHz = config_.FilterMinCutoffHz;
			state_.RotationSpeedCoefficient = config_.FilterSpeedCoefficient;
			state_.PositionMinCutoffHz = config_.FilterMinCutoffHz;
			// Position speed is in m/s (small numbers) vs rotation's deg/s (large), so the position
			// coefficient needs to be far larger to open the filter up over a comparable motion range.
			state_.PositionSpeedCoefficient = config_.FilterSpeedCoefficient * 100f;
		}

		private void ApplyDefaultsIfUnset() {
			// 0.3.17: every real-game log this whole project has ever produced shows
			// "Unable to save config 'PhoneCam.ksc': System.NullReferenceException" repeating
			// constantly (every toggle, every slider drag) - which means every settings change
			// (MaxPositionOffset, PositionSensitivity, InvertPitch/InvertYaw, etc.) has likely
			// never actually been persisted across a game restart, no matter how many times a
			// slider got tuned up mid-session. KSL's own save path is obfuscated so the exact
			// cause can't be read directly, but IHeadTrackConfig's two string properties
			// (LocalIpOverride, PhoneIpFilter) are the only members here that can default to a
			// literal null (bool/float/int can't) - and neither was ever explicitly defaulted to
			// "" the way every numeric setting below is. A null string reaching whatever
			// (System.String, System.String, System.String) method KSL's save path uses
			// internally (visible in the log's stack trace) is a very plausible NullReference
			// source. Defaulting both to "" here costs nothing and directly targets the one gap
			// left unhandled.
			if (config_.LocalIpOverride == null) config_.LocalIpOverride = "";
			if (config_.PhoneIpFilter == null) config_.PhoneIpFilter = "";

			// KSL config properties default to each type's zero value on first run - fill in
			// sane non-zero defaults the first time this mod loads.
			if (config_.OscPort <= 0) config_.OscPort = DefaultOscPort;

			// 0.3.19: the 0.3.18 diagnostic bump (forced 8x, up to 15x via the widened slider) did
			// its job - it's what made translation visible enough to expose the real find (parts of
			// the car, e.g. the trunk/rear glass, disappearing when the camera got close: CarX's own
			// proximity-based hide-geometry-near-camera system, not a bug in this mod - see the
			// README). At that sensitivity, a real ~0.3-0.6m lean/step was landing as multiple
			// *meters* of applied offset, which is far outside where a normal photo-mode camera
			// would ever sit relative to the car, and easily crosses whatever distance threshold
			// that hide system uses. Reverted back to a normal 1x default now that confirmation is
			// done, same as every other numeric default here: only fills in a truly unset (0) value,
			// doesn't fight a value you've already tuned.
			if (config_.PositionSensitivity <= 0) config_.PositionSensitivity = 1.0f;

			// 0.3.20: the 0.3.19 revert above didn't actually take effect for anyone who'd already
			// played with the 0.3.18 diagnostic build - a fresh output.log showed
			// positionSensitivity=11.48 still loading under 0.3.19, because that's a real saved
			// value (not the unset/zero case the check above catches), and config saving has
			// actually worked since 0.3.17. Force it back to 1x exactly once via a dedicated
			// migration flag (see SensitivityDiagnosticReverted) so the stale diagnostic-era value
			// gets caught regardless of what it currently is, without permanently overriding
			// whatever you tune it to afterward.
			if (!config_.SensitivityDiagnosticReverted) {
				config_.PositionSensitivity = 1.0f;
				config_.SensitivityDiagnosticReverted = true;
			}

			// 0.3.22: the 0.3.21 endOfRender diagnostic came back conclusive - a real log showed
			// appliedPosOffset and cameraWorldPosAfterWrite tracking each other exactly, and
			// endOfRender's transformPos/matrixDecodedPos matching that value down to the
			// centimeter, every frame. The position write is correct and genuinely reaches the
			// screen; the "stepping does nothing" report is a magnitude/perception problem, not a
			// missing/broken write. At 1x, a real seated lean (tens of centimeters) produces the
			// same tiny real-meter shift in-game, which barely registers as parallax - especially
			// from chase-cam distance, where the camera already sits several meters from the car.
			// Bumps PositionSensitivity to 2.5x exactly once via a dedicated migration flag (same
			// pattern as SensitivityDiagnosticReverted just above - a plain "<= 0" unset check
			// can't catch the already-set 1.0 that fix left behind).
			if (!config_.PositionSensitivityBoosted) {
				config_.PositionSensitivity = 2.5f;
				config_.PositionSensitivityBoosted = true;
			}

			if (config_.RotationSensitivity <= 0) config_.RotationSensitivity = 1.0f;
			if (config_.PositionSmoothing <= 0) config_.PositionSmoothing = 0.35f;
			if (config_.RotationSmoothing <= 0) config_.RotationSmoothing = 0.45f;
			if (config_.MaxPositionOffset <= 0) config_.MaxPositionOffset = 3.0f;
			if (config_.MaxRotationOffset <= 0) config_.MaxRotationOffset = 80f;

			// One-time bump from the old 0.5m "seat lean" default up to a "walk a few real steps"
			// free-cam-scale default - runs even for installs that already have a saved (smaller)
			// value from a previous version, since the plain <=0 check above only catches a truly
			// unset value. Only ever fires once per install; tuning the slider afterward always
			// sticks from then on.
			if (!config_.PositionRangeUpgraded) {
				config_.MaxPositionOffset = 3.0f;
				config_.PositionRangeUpgraded = true;
			}

			// 0.4.0: the stated goal for this mod is that the in-game camera moves *identically* to
			// the real phone - i.e. genuinely 1:1. A saved RotationSensitivity of 2.16x (what a real
			// config was carrying, visible in the 0.3.30 heartbeat) is by definition not 1:1: a 30
			// degree real-world turn was arriving as a 65 degree camera swing, which is a large part
			// of why the view felt like it was being thrown around rather than held. Reset to exactly
			// 1.0 once, via the same one-time-migration pattern as the position-sensitivity fixes
			// above (a plain "<= 0" unset check can't catch an already-set 2.16). Tuning the slider
			// afterward always sticks.
			if (!config_.RotationSensitivityUnityGained) {
				config_.RotationSensitivity = 1.0f;
				config_.RotationSensitivityUnityGained = true;
			}

			if (!config_.OrbitModeDefaulted) {
				config_.OrbitModeEnabled = true;
				config_.OrbitModeDefaulted = true;
			}

			// 0.5.1: see IHeadTrackConfig.PostFrameOrderRetune. Orbit mode and the 2.5x position
			// sensitivity were both compensation for a camera that appeared frozen - and the real cause
			// turned out to be 0.5.0's frame-ordering bug, not weak motion. Now that the write actually
			// reaches the screen, both of those read as the camera sliding sideways and swinging around
			// the car instead of sitting still and turning like a head. Reset once to plain 1:1.
			// 0.5.4: reported backwards - moving the phone left slid the camera right.
			if (!config_.AdaptiveFilterDefaulted) {
				config_.AdaptiveFilterEnabled = true;
				config_.AdaptiveFilterDefaulted = true;
			}
			if (config_.FilterMinCutoffHz <= 0) config_.FilterMinCutoffHz = 0.4f;

			// 0.6.2: 1.0 Hz still passed visible noise. The residual jitter is small in absolute terms
			// but HDRP's temporal AA resolves sub-pixel camera movement by design, so a few hundredths
			// of a degree of camera wobble becomes visible shimmer once the scene is moving past at
			// speed - which is why it reads as "only when the car moves". 0.4 Hz roughly 2.5x's the
			// filtering at rest, and the speed-adaptive term still opens it up for real movement.
			if (!config_.FilterRetunedForShimmer) {
				config_.FilterMinCutoffHz = 0.4f;
				config_.FilterRetunedForShimmer = true;
			}
			if (config_.FilterSpeedCoefficient <= 0) config_.FilterSpeedCoefficient = 0.02f;

			if (!config_.PositionInvertDefaulted) {
				config_.InvertPositionX = true;
				config_.PositionInvertDefaulted = true;
			}

			if (!config_.PostFrameOrderRetune) {
				config_.OrbitModeEnabled = false;
				config_.PositionSensitivity = 1.0f;
				config_.PostFrameOrderRetune = true;
			}

			// See IHeadTrackConfig.OrbitSensitivity - 1:1 orbit is measurably too subtle to perceive, so
			// this starts at 5x. One-time, so tuning the slider afterward sticks.
			if (config_.OrbitSensitivity <= 0) config_.OrbitSensitivity = 2.0f;

			// 0.4.6: the 0.4.3 default of 5x measurably overshot - it drove the orbit to 174 degrees of
			// yaw and -146 of pitch, flinging the camera 10 m and underneath the car. Reset once to 2x,
			// which with the new arc clamps keeps the camera in a usable sweep beside the car.
			if (!config_.OrbitSensitivityRetuned) {
				config_.OrbitSensitivity = 2.0f;
				config_.OrbitSensitivityRetuned = true;
			}
			if (!config_.OrbitSensitivityDefaulted) {
				config_.OrbitSensitivityDefaulted = true;
			}

			if (!config_.StatusHudDefaulted) {
				config_.ShowStatusHud = true;
				config_.StatusHudDefaulted = true;
			}

			if (config_.ZoomSensitivity <= 0) config_.ZoomSensitivity = 1.5f;
			if (config_.MaxZoomOffset <= 0) config_.MaxZoomOffset = 30f;
			if (config_.ZoomSmoothing <= 0) config_.ZoomSmoothing = 0.2f;
			if (config_.ClippingGuardMargin <= 0) config_.ClippingGuardMargin = 0.08f;
			// Layer mask 0 (nothing selected) would make the raycast a no-op; default to "everything"
			// so the toggle visibly does something the first time it's enabled, and let the user
			// narrow it down once they can see what it's catching against the real game.
			if (config_.ClippingGuardLayerMask == 0) config_.ClippingGuardLayerMask = ~0;
		}

		/// <summary>
		/// Replaces every digit in an address with "•", leaving separators (dots/colons) intact,
		/// so the shape stays recognizable on-screen without exposing the actual value in a
		/// screenshot or stream. See IHeadTrackConfig.ShowSensitiveInfo.
		/// </summary>
		private static string MaskAddress(string address) {
			if (string.IsNullOrEmpty(address)) return address;
			var sb = new System.Text.StringBuilder(address.Length);
			foreach (char c in address) {
				sb.Append(char.IsDigit(c) ? '•' : c);
			}
			return sb.ToString();
		}

		public override void OnUIDraw() {
			bool connected = IsReceivingData();

			Kino.UI.Label("LOTA - LiDAR Over the Air (free, App Store) streams ARKit camera pose to this mod over OSC.");
			Kino.UI.Label(connected ? "Status: receiving data" : "Status: no data (check LOTA is streaming, same Wi-Fi, matching port)");

			bool showSensitive = config_.ShowSensitiveInfo;
			string senderIp = receiver_.LastSenderAddress;
			string senderDisplay = senderIp == null ? "(none yet)" : (showSensitive ? senderIp : MaskAddress(senderIp));
			Kino.UI.Label($"Last sender IP: {senderDisplay}");

			Kino.UI.HorizontalLine();
			Kino.UI.GroupLabel("Update");
			Kino.UI.Label($"Installed version: {CurrentVersion}");
			Kino.UI.Label(updateStatus_);

			if (!updateCheckInProgress_ && Kino.UI.Button(updateCheckInProgress_ ? "Checking..." : "Check for Update")) {
				CheckForUpdate();
			}

			if (!string.IsNullOrEmpty(updateDownloadUrl_) && !updateDownloadInProgress_) {
				if (Kino.UI.Button($"Download & Install v{updateLatestVersion_}")) {
					DownloadUpdate();
				}
			}
			else if (updateDownloadInProgress_) {
				Kino.UI.Label("Downloading...");
			}

			Kino.UI.HorizontalLine();

			if (IsInPhotoMode()) {
				if (Kino.UI.ContextButton("How to use", tooltip: "Step-by-step LOTA + calibration guide")) {
					Kino.UI.PushContext(DrawHowToUseContext, "How to use HeadTrackARKit");
				}
			}
			else {
				Kino.UI.Label("Enter Photo Mode to open the how-to-use guide.");
			}

			Kino.UI.HorizontalLine();

			bool enabled = config_.Enabled;
			if (Kino.UI.Toggle("Enabled", ref enabled)) {
				config_.Enabled = enabled;
				Kino.Log.Info($"[HeadTrackARKit] Enabled toggled {(enabled ? "ON" : "OFF")} from the settings panel.");
				if (enabled) {
					StartReceiver();
				}
				else {
					receiver_.Stop();
				}
			}

			// 0.3.31: requested option to hide the always-on corner status text (see OnGUI) - e.g.
			// while recording/streaming, or just for a cleaner screen once you trust it's working.
			bool showStatusHud = config_.ShowStatusHud;
			if (Kino.UI.Toggle("Show on-screen status (top-left corner)", ref showStatusHud)) {
				config_.ShowStatusHud = showStatusHud;
			}

			// 0.4.2: see IHeadTrackConfig.OrbitModeEnabled. Left switchable because once positional
			// tracking is working phone-side, straight 1:1 (orbit off) is the more faithful mode.
			// 0.5.1: relabelled around what the two modes actually feel like, rather than the
			// implementation term. Off (the default) is the head-tracking behaviour: the camera holds
			// its position and turns, so looking left turns the view left instead of sliding the camera
			// left. On swings the camera bodily around the car.
			bool orbitMode = config_.OrbitModeEnabled;
			if (Kino.UI.Toggle("Swing the camera around the car (off = stay in place and turn)", ref orbitMode)) {
				config_.OrbitModeEnabled = orbitMode;
				Kino.Log.Info($"[HeadTrackARKit] Orbit mode toggled {(orbitMode ? "ON" : "OFF")}.");
			}

			if (orbitMode) {
				float orbitSens = config_.OrbitSensitivity;
				if (Kino.UI.Slider(ref orbitSens, 0.5f, 6f, $"Swing strength: {orbitSens:F1}x")) {
					config_.OrbitSensitivity = orbitSens;
				}
				Kino.UI.Label("Turning the phone walks the camera around the car. Lower this if it slides too far sideways.");
			}
			else {
				Kino.UI.Label("Camera stays put and turns with the phone. 'Position sensitivity' below controls how far real phone movement shifts it.");
			}

			if (Kino.UI.Input(ref portText_, 5, "^[0-9]{1,5}$")) {
				if (int.TryParse(portText_, out int port) && port > 0 && port <= 65535) {
					config_.OscPort = port;
					if (config_.Enabled) {
						StartReceiver();
					}
				}
			}

			// 0.3.26: fixes a real "can't edit the LAN IP at all" report. The toggle that unlocks
			// editing both IP fields below used to live several sections further down the page
			// (under a separate "Privacy" heading, past the Phone's IP field too) - so on first
			// glance the LAN IP field just looked like a plain, non-interactive masked label with
			// no visible way to edit it at all, not a field gated behind a toggle you hadn't
			// scrolled to yet. Moved the toggle to right here, immediately above the fields it
			// actually controls, with a label that says outright what it's for.
			if (Kino.UI.Toggle("Show IP addresses (required to edit them)", ref showSensitive)) {
				config_.ShowSensitiveInfo = showSensitive;
			}

			Kino.UI.Label("This PC's LAN IP (edit if auto-detect picked the wrong adapter):");
			if (showSensitive) {
				if (Kino.UI.Input(ref localIpText_, 45, "^[0-9a-fA-F:.]{0,45}$")) {
					config_.LocalIpOverride = localIpText_;
				}
			}
			else {
				Kino.UI.Label($"  {MaskAddress(localIpText_)}  (enable 'Show IP addresses' above to edit)");
			}
			Kino.UI.Label("Type the IP above and the port above into LOTA's Transmission Settings destination IP.");
			if (Kino.UI.Button("Refresh IP (auto-detect)")) {
				RefreshLocalIp();
			}

			Kino.UI.Label("Phone's IP (optional - only accept data from this exact address):");
			if (showSensitive) {
				if (Kino.UI.Input(ref phoneIpText_, 45, "^[0-9a-fA-F:.]{0,45}$")) {
					config_.PhoneIpFilter = phoneIpText_;
					receiver_.AllowedSenderFilter = phoneIpText_;
				}
			}
			else if (!string.IsNullOrEmpty(phoneIpText_)) {
				Kino.UI.Label($"  {MaskAddress(phoneIpText_)}  (enable 'Show IP addresses' above to edit)");
			}
			else {
				Kino.UI.Label("  (not set)");
			}

			Kino.UI.HorizontalLine();
			Kino.UI.GroupLabel("Privacy");
			Kino.UI.Label("IP addresses above are masked by default so they aren't exposed on stream or in screenshots - use the 'Show IP addresses' toggle further up to view or edit them.");

			Kino.UI.HorizontalLine();
			Kino.UI.GroupLabel("Look direction");
			Kino.UI.Label("If up/down or left/right ever feels backwards or reversed, flip it here.");

			bool verbose = config_.VerboseDiagnostics;
			if (Kino.UI.Toggle("Verbose per-frame logging (slow - debugging only)", ref verbose)) {
				config_.VerboseDiagnostics = verbose;
				Kino.Log.Info($"[HeadTrackARKit] Verbose diagnostics {(verbose ? "ON" : "OFF")}.");
			}

			Kino.UI.GroupLabel("Jitter filter");
			bool adaptive = config_.AdaptiveFilterEnabled;
			if (Kino.UI.Toggle("Adaptive anti-jitter filter (recommended)", ref adaptive)) {
				config_.AdaptiveFilterEnabled = adaptive;
				SyncStateFromConfig();
			}
			if (adaptive) {
				float mc = config_.FilterMinCutoffHz;
				if (Kino.UI.Slider(ref mc, 0.2f, 6f, $"Steadiness at rest: {mc:F1} Hz (lower = steadier)")) {
					config_.FilterMinCutoffHz = mc; SyncStateFromConfig();
				}
				float sc = config_.FilterSpeedCoefficient;
				if (Kino.UI.Slider(ref sc, 0.002f, 0.2f, $"Responsiveness: {sc:F3}")) {
					config_.FilterSpeedCoefficient = sc; SyncStateFromConfig();
				}
				Kino.UI.Label("Heavily smooths the phone's resting noise, then opens up as you actually move.");
			}

			Kino.UI.GroupLabel("Movement direction");
			bool invPosX = config_.InvertPositionX;
			if (Kino.UI.Toggle("Invert left/right movement", ref invPosX)) config_.InvertPositionX = invPosX;
			bool invPosY = config_.InvertPositionY;
			if (Kino.UI.Toggle("Invert up/down movement", ref invPosY)) config_.InvertPositionY = invPosY;
			bool invPosZ = config_.InvertPositionZ;
			if (Kino.UI.Toggle("Invert forward/back movement", ref invPosZ)) config_.InvertPositionZ = invPosZ;

			bool invertPitch = config_.InvertPitch;
			if (Kino.UI.Toggle("Invert up/down look", ref invertPitch)) {
				config_.InvertPitch = invertPitch;
			}

			bool invertYaw = config_.InvertYaw;
			if (Kino.UI.Toggle("Invert left/right look", ref invertYaw)) {
				config_.InvertYaw = invertYaw;
			}

			Kino.UI.HorizontalLine();

			Kino.UI.Label(state_.IsCalibrated ? "Calibrated: yes" : "Calibrated: no - press F9 or the button below");

			if (Kino.UI.Button("Set Neutral Position (F9)")) {
				Calibrate();
			}

			Kino.UI.HorizontalLine();
			Kino.UI.GroupLabel("Sensitivity");

			float posSens = config_.PositionSensitivity;
			// 0.3.19: reverted the 0.3.18 diagnostic widening (0-15, to fit the temporary 8x
			// forced default) back to 0-3 now that the default itself is back to 1x - see
			// ApplyDefaultsIfUnset. 3x is still enough headroom to make a small real lean feel
			// dramatic without being able to fling the camera meters away from a normal seated
			// position.
			if (Kino.UI.Slider(ref posSens, 0f, 3f, $"Position sensitivity: {posSens:F2}")) {
				config_.PositionSensitivity = posSens;
				state_.PositionSensitivity = posSens;
			}

			float rotSens = config_.RotationSensitivity;
			if (Kino.UI.Slider(ref rotSens, 0f, 3f, $"Rotation sensitivity: {rotSens:F2}")) {
				config_.RotationSensitivity = rotSens;
				state_.RotationSensitivity = rotSens;
			}

			Kino.UI.GroupLabel("Smoothing");

			float posSmooth = config_.PositionSmoothing;
			if (Kino.UI.Slider(ref posSmooth, 0.05f, 1f, $"Position smoothing: {posSmooth:F2}")) {
				config_.PositionSmoothing = posSmooth;
				state_.PositionSmoothing = posSmooth;
			}

			float rotSmooth = config_.RotationSmoothing;
			if (Kino.UI.Slider(ref rotSmooth, 0.05f, 1f, $"Rotation smoothing: {rotSmooth:F2}")) {
				config_.RotationSmoothing = rotSmooth;
				state_.RotationSmoothing = rotSmooth;
			}

			Kino.UI.GroupLabel("Safety clamps");

			// Widened from the original 0.05-1.5m range so real-world walking (leaning several
			// steps left/right, not just a small seat-lean) has room to actually reach the camera
			// instead of getting clamped down to almost nothing - see PositionRangeUpgraded.
			float maxPos = config_.MaxPositionOffset;
			if (Kino.UI.Slider(ref maxPos, 0.05f, 10f, $"Max position offset: {maxPos:F2} m (raise for room-scale movement)")) {
				config_.MaxPositionOffset = maxPos;
				state_.MaxPositionOffset = maxPos;
			}

			// Pitch (up/down) and yaw (left/right) are unclamped as of 0.3.11 - full 360-degree
			// turns keep going instead of stopping. This slider now only limits roll (tilting your
			// head sideways).
			float maxRot = config_.MaxRotationOffset;
			if (Kino.UI.Slider(ref maxRot, 10f, 120f, $"Max roll offset: {maxRot:F0} deg (pitch/yaw are unlimited)")) {
				config_.MaxRotationOffset = maxRot;
				state_.MaxRotationOffsetDegrees = maxRot;
			}

			Kino.UI.HorizontalLine();
			Kino.UI.GroupLabel("Zoom");
			Kino.UI.Label($"Current zoom offset: {zoomCurrentDegrees_:F1} deg (target: {zoomTargetDegrees_:F1} deg) - mouse wheel, or +/- keys");

			float zoomSens = config_.ZoomSensitivity;
			if (Kino.UI.Slider(ref zoomSens, 0.1f, 5f, $"Zoom sensitivity (amount per scroll/keypress): {zoomSens:F2}")) {
				config_.ZoomSensitivity = zoomSens;
			}

			float zoomSmooth = config_.ZoomSmoothing;
			if (Kino.UI.Slider(ref zoomSmooth, 0.02f, 1f, $"Zoom smoothing (speed - lower is smoother/slower): {zoomSmooth:F2}")) {
				config_.ZoomSmoothing = zoomSmooth;
			}

			float maxZoom = config_.MaxZoomOffset;
			if (Kino.UI.Slider(ref maxZoom, 5f, 60f, $"Max zoom range: +/-{maxZoom:F0} deg")) {
				config_.MaxZoomOffset = maxZoom;
			}

			if (Kino.UI.Button("Reset Zoom (F10)")) {
				ResetZoom();
			}

			Kino.UI.HorizontalLine();
			Kino.UI.GroupLabel("Cockpit clipping guard");
			Kino.UI.Label("Stops the camera before it pokes through the dashboard/seat when leaning in.");
			Kino.UI.Label("Off by default - tune the layer mask below once you can see what it hits in-game.");

			bool clipGuard = config_.ClippingGuardEnabled;
			if (Kino.UI.Toggle("Enabled", ref clipGuard)) {
				config_.ClippingGuardEnabled = clipGuard;
			}

			float clipMargin = config_.ClippingGuardMargin;
			if (Kino.UI.Slider(ref clipMargin, 0.02f, 0.3f, $"Margin: {clipMargin:F2} m")) {
				config_.ClippingGuardMargin = clipMargin;
			}

			if (Kino.UI.Input(ref layerMaskText_, 10, "^-?[0-9]{1,10}$", tooltip: "Raw Unity LayerMask bitmask (int). Default -1 = everything.")) {
				if (int.TryParse(layerMaskText_, out int mask)) {
					config_.ClippingGuardLayerMask = mask;
				}
			}

			Kino.UI.HorizontalLine();
			Kino.UI.Hyperlink("Get LOTA on the App Store", "https://apps.apple.com/app/id6760984302");
		}

		// Pushed as a KSL UI context (see OnUIDraw) - only reachable while Photo Mode is
		// active, per IsInPhotoMode().
		private void DrawHowToUseContext() {
			Kino.UI.GroupLabel("Setup");
			Kino.UI.Label("1. On your iPhone, open LOTA (free, App Store) - no subscription needed.");
			Kino.UI.Label("2. Stay on the main camera page (any mode except Motion).");
			Kino.UI.Label("3. Tap the status bar pill (Transmission Settings), enable OSC.");
			string ipForDisplay = config_.ShowSensitiveInfo ? localIpText_ : MaskAddress(localIpText_);
			Kino.UI.Label($"4. Set the destination IP to {ipForDisplay} and the port to {portText_} (shown above too).");
			Kino.UI.Label("5. Make sure phone and PC are on the same Wi-Fi, then tap the shutter with STREAM selected.");

			Kino.UI.HorizontalLine();
			Kino.UI.GroupLabel("Using it");
			Kino.UI.Label("Sit in your normal position, then press F9 to set neutral - everything after is relative to that pose.");
			Kino.UI.Label("The camera still follows the game normally (car, chase cam, cockpit, etc.) - your tracked movement is layered on top of it.");
			Kino.UI.Label("Re-press F9 any time you shift position.");
			Kino.UI.Label("Mouse wheel or +/- zooms the camera; F10 resets zoom.");
			Kino.UI.Label("Leaning/walking moves the camera too - raise 'Max position offset' in Sensitivity/Safety clamps for bigger, room-scale movement.");
			Kino.UI.Label("Looking/turning fully around (pitch and yaw) has no stopping point - only roll is limited by the safety clamp.");
			Kino.UI.Label("Cockpit clipping guard (off by default) stops the camera short of the dashboard/seat when leaning in.");

			Kino.UI.HorizontalLine();
			Kino.UI.Hyperlink("Get LOTA on the App Store", "https://apps.apple.com/app/id6760984302");

			Kino.UI.HorizontalLine();
			if (Kino.UI.Button("Back")) {
				Kino.UI.PopContext();
			}
		}

		public override void OnAdditionalAboutUIDraw() {
			Kino.UI.Label("Head tracking for CarX using an iPhone's ARKit/LiDAR data via the free LOTA app.");
			Kino.UI.Label("In LOTA: swipe to ARKit Tracking is not required - camera pose streams from the main camera page.");
			Kino.UI.Label("Enable OSC in Transmission Settings, set the destination IP to this PC and the port above.");
		}
	}
}

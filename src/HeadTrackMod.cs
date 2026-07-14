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
	[KSLMeta("PhoneCam", "0.3.13", "Chaoz2")]
	public class HeadTrackMod : BaseMod {
		// IMPORTANT: bump this together with the KSLMeta version string right above, every
		// release - this is what the in-game updater compares against GitHub's latest release
		// tag to decide whether an update is available. There's no confirmed public way to read
		// the version back out of the KSLMeta attribute at runtime, so it's duplicated here
		// rather than guessed at via reflection into an undocumented attribute shape.
		private const string CurrentVersion = "0.3.13";

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

		private Camera cachedCamera_;
		private float cameraCacheTime_;

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

		// --- MultiHUD gauge conflict workaround ---
		// MultiHUD's gauge Canvas (confirmed "UGUI") reads the camera's real Transform/FOV
		// directly - two direct fixes (0.3.8's revert-to-Transform + matrix override) never
		// stopped it from visibly reacting to head movement/zoom, because that reaction is by
		// design for a Screen-Space-Camera Canvas, not a bug in this mod's own camera handling.
		// Rather than keep fighting a Canvas this mod doesn't own, these objects are located by
		// name at runtime and simply deactivated while config_.Enabled is true, then reactivated
		// the moment it's turned off - see UpdateGaugeVisibility.
		private static readonly string[] GaugeNameKeywords = { "speedometer", "tachometer", "rpmgauge" };
		private readonly List<GameObject> gaugeObjects_ = new List<GameObject>();
		private bool gaugesHidden_;
		private float lastGaugeSearchTime_;

		// --- Dashboard gauge-needle diagnostics ---
		// The hide-workaround above (found via GaugeNameKeywords) turned out to be solving a
		// different problem than reported: those are MultiHUD's own 2D speedometer HUD objects,
		// confirmed by MultiHUD's own log ("Found CarX speedometer at: .../UIRaceSpeedometer/
		// Speedometer") - hiding them stopped THAT from reacting, but the actual complaint is the
		// physical 3D dashboard gauge needles (visible in the cockpit) spinning/wobbling, only
		// while PhoneCam is enabled - a completely different object this mod has no visibility
		// into yet. Rather than guess at another keyword list blind, this is an on-demand scan
		// (triggered by a button in the settings panel, so it can be run at the exact moment the
		// wobble is visible) that casts a much wider net and logs each match's full hierarchy path
		// plus whether a Camera component sits anywhere in its ancestor chain - the single most
		// useful fact for telling apart "this needle is part of the car's interior model" from
		// "this needle is parented under the camera rig," which would explain a lot.
		private static readonly string[] DashboardDiagnosticKeywords = {
			"speedo", "tach", "rpm", "gauge", "needle", "dash", "cluster", "odometer"
		};

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
			Camera.onPreCull += OnCameraPreCull;
			RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;

			Kino.Log.Info($"[HeadTrackARKit] Loaded. Enabled={config_.Enabled}. Bind key default: F9 to calibrate neutral position.");
		}

		private void OnDestroy() {
			Camera.onPreCull -= OnCameraPreCull;
			RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
			receiver_.Stop();
		}

		private void Update() {
			receiver_.DrainInto(HandleOscMessage);

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

			UpdateGaugeVisibility();
		}

		private void LateUpdate() {
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

		private void AdjustZoom(float deltaDegrees) {
			float max = Mathf.Abs(config_.MaxZoomOffset);
			zoomTargetDegrees_ = Mathf.Clamp(zoomTargetDegrees_ + deltaDegrees, -max, max);
		}

		private void ResetZoom() {
			zoomTargetDegrees_ = 0f;
			Kino.Log.Info("[HeadTrackARKit] Zoom reset.");
		}

		/// <summary>
		/// Hides (or restores) MultiHUD's gauge object(s) depending on whether PhoneCam is
		/// currently enabled - see the field comments on gaugeObjects_/GaugeNameKeywords for why
		/// this exists instead of another attempt to keep the gauges in sync with the camera.
		/// Runs every frame from Update(), but the actual scene search only happens periodically
		/// (see lastGaugeSearchTime_) and only while nothing has been found yet.
		/// </summary>
		private void UpdateGaugeVisibility() {
			if (config_.HideGaugesWhileTracking && config_.Enabled) {
				if (Time.unscaledTime - lastGaugeSearchTime_ > 2f) {
					lastGaugeSearchTime_ = Time.unscaledTime;
					gaugeObjects_.RemoveAll(go => go == null);
					if (gaugeObjects_.Count == 0) {
						RefreshGaugeObjects();
					}
				}
				SetGaugesHidden(true);
			}
			else {
				SetGaugesHidden(false);
			}
		}

		/// <summary>
		/// Searches every GameObject currently loaded in memory (not just the "active" scene) for
		/// anything whose name contains one of GaugeNameKeywords, including inactive objects.
		///
		/// The first attempt at this (0.3.10) walked SceneManager.GetActiveScene().GetRootGameObjects()
		/// and found nothing, ever - confirmed by a real KSL log showing zero "Found N gauge
		/// object(s)" lines across a full test session, even after MultiHUD itself logged finding
		/// the speedometer at runtime. Root cause: MultiHUD's own log named the object's path
		/// "KeepAlive(Clone)/UGUI/...", and CarX separately logs loading several scenes
		/// "Additive" (nfs_studio, Race, vp_parking, etc.) - "KeepAlive(Clone)" is almost certainly
		/// a DontDestroyOnLoad object, and DontDestroyOnLoad objects (along with anything in an
		/// additively-loaded scene) are NOT returned by GetActiveScene().GetRootGameObjects(),
		/// which only covers whichever single scene Unity currently considers "active." Switched to
		/// Resources.FindObjectsOfTypeAll, which walks every loaded GameObject regardless of which
		/// scene (or DontDestroyOnLoad) it lives in - filtered to go.scene.IsValid() so it only
		/// matches real scene instances, not unrelated prefab/asset references that happen to be
		/// loaded in memory.
		/// </summary>
		private void RefreshGaugeObjects() {
			GameObject[] all = Resources.FindObjectsOfTypeAll<GameObject>();
			foreach (GameObject go in all) {
				if (!go.scene.IsValid()) continue;

				string lower = go.name.ToLowerInvariant();
				foreach (string keyword in GaugeNameKeywords) {
					if (lower.Contains(keyword)) {
						gaugeObjects_.Add(go);
						break;
					}
				}
			}

			if (gaugeObjects_.Count > 0) {
				var names = new List<string>();
				foreach (GameObject go in gaugeObjects_) names.Add(go.name);
				Kino.Log.Info($"[HeadTrackARKit] Found {gaugeObjects_.Count} gauge object(s): {string.Join(", ", names)}");
			}
		}

		/// <summary>
		/// On-demand diagnostic scan for the dashboard-needle wobble report - see the field comment
		/// on DashboardDiagnosticKeywords for why this exists as a separate, wider search from the
		/// HUD-hide one above. Logs every match's full hierarchy path (root-to-leaf) and whether a
		/// Camera component sits anywhere in its ancestor chain - meant to be run right when the
		/// wobble is visible in-game, so the resulting log capture is directly correlated with the
		/// actual symptom instead of being a generic startup dump.
		/// </summary>
		private void LogDashboardGaugeDiagnostics() {
			GameObject[] all = Resources.FindObjectsOfTypeAll<GameObject>();
			int matchCount = 0;

			Kino.Log.Info("[HeadTrackARKit][dash-diag] Scan starting...");

			foreach (GameObject go in all) {
				if (!go.scene.IsValid()) continue;

				string lower = go.name.ToLowerInvariant();
				bool matches = false;
				foreach (string keyword in DashboardDiagnosticKeywords) {
					if (lower.Contains(keyword)) {
						matches = true;
						break;
					}
				}
				if (!matches) continue;

				matchCount++;
				if (matchCount > 60) {
					Kino.Log.Info("[HeadTrackARKit][dash-diag] Stopping early at 60 matches to avoid flooding the log.");
					break;
				}

				bool underCamera = false;
				var pathParts = new List<string> { go.name };
				Transform t = go.transform.parent;
				while (t != null) {
					pathParts.Insert(0, t.name);
					if (t.GetComponent<Camera>() != null) underCamera = true;
					t = t.parent;
				}

				Kino.Log.Info($"[HeadTrackARKit][dash-diag] '{string.Join("/", pathParts)}' underCamera={underCamera}");
			}

			Kino.Log.Info($"[HeadTrackARKit][dash-diag] Scan complete - {matchCount} candidate object(s) matched.");
		}

		/// <summary>
		/// Applies the hidden/shown state to every currently-known gauge object. Only logs on an
		/// actual state change (not every frame), and re-applies to any newly-found objects even if
		/// the overall state didn't just change, so an object found mid-session while already
		/// hidden still gets hidden immediately instead of waiting for the next toggle.
		/// </summary>
		private void SetGaugesHidden(bool hidden) {
			bool changed = hidden != gaugesHidden_;
			gaugesHidden_ = hidden;

			foreach (GameObject go in gaugeObjects_) {
				if (go != null && go.activeSelf == hidden) {
					go.SetActive(!hidden);
				}
			}

			if (changed && gaugeObjects_.Count > 0) {
				Kino.Log.Info($"[HeadTrackARKit] {(hidden ? "Hiding" : "Restoring")} {gaugeObjects_.Count} gauge object(s) (MultiHUD conflict workaround).");
			}
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
			return $"(x={e.x:F0},y={e.y:F0},z={e.z:F0})";
		}

		private void Calibrate() {
			if (!state_.HasSignal) {
				Kino.Log.Warning("[HeadTrackARKit] No OSC data received yet - check LOTA is streaming and the port matches.");
				return;
			}

			state_.Calibrate();
			Kino.Log.Info("[HeadTrackARKit] Neutral position set.");
		}

		private void OnCameraPreCull(Camera cam) {
			// Runs regardless of the Enabled toggle now - the previous diagnostic build (0.3.1)
			// gated this on config_.Enabled and came back with zero diag lines even after a full
			// play session, which was ambiguous: either Enabled was actually off, or onPreCull
			// never fires for whatever camera Kino's custom camera system uses. Logging
			// unconditionally settles which one it is.
			LogCameraDiagnostics(cam);

			if (!config_.Enabled) {
				// Make sure a previous frame's override doesn't linger once the mod is turned
				// off - without this, the view would stay stuck at its last head-tracked pose
				// instead of snapping back to whatever the game's own camera logic drives it to.
				ResetCameraOverride(cam);
				return;
			}

			// 0.3.4's real fix (SRP hook) proved the plumbing works: GetActiveCamera() correctly
			// resolved to the actual render camera the whole session. But the goal now is a
			// free-cam-style override that works in *every* mode CarX/Kino can be in, including
			// Photo Mode, without needing to special-case each one - so instead of trusting
			// CameraSwitch/UIPhotoModeContext to say which camera is "the" camera, just apply to
			// whichever camera Unity is actually about to render to the screen. `cam` here already
			// is exactly that, every time this fires - the only cameras to skip are ones that
			// obviously aren't the player's view: anything rendering to an offscreen buffer
			// (targetTexture != null - reflection probes, minimap, icon generation, etc.) or an
			// orthographic camera (2D HUD/UI overlays, if this game has any as separate cameras).
			if (cam.targetTexture != null || cam.orthographic) return;

			// Zoom applies independently of head-tracking calibration.
			if (Mathf.Abs(zoomCurrentDegrees_) > 0.001f) {
				cam.fieldOfView = Mathf.Clamp(cam.fieldOfView + zoomCurrentDegrees_, 1f, 179f);
			}

			// 0.3.8: back to writing the head-tracking offset onto the camera's real Transform.
			// 0.3.7 stopped doing this on the theory that the gauge HUD was moving because it was
			// parented under the camera's Transform - but a real-game test showed the gauges kept
			// reacting even without touching the Transform at all, which rules that theory out.
			// The gauge HUD (MultiHUD, a separate mod) is a "UGUI" Canvas - almost certainly using
			// Unity's Screen-Space-Camera render mode, which is *designed* to read the camera's
			// real Transform and fieldOfView directly to stay glued to the screen. Decoupling from
			// the Transform (0.3.7) made the UI and the 3D world disagree with each other instead
			// of fixing anything: the UI kept tracking the stale Transform/FOV while the 3D world
			// rendered from a separately-computed pose, which is what "acting weird" actually was.
			// Writing to the real Transform/FOV keeps every such system in sync automatically,
			// exactly like it did before 0.3.7.
			if (state_.IsCalibrated) {
				Vector3 posOffset = state_.GetPositionOffset();
				Quaternion rotOffset = FixLookDirection(state_.GetRotationOffsetEuler());

				Transform t = cam.transform;

				if (config_.ClippingGuardEnabled && posOffset.sqrMagnitude > 1e-6f) {
					posOffset = ApplyClippingGuard(t, posOffset);
				}

				t.position += t.rotation * posOffset;
				t.rotation = t.rotation * rotOffset;
			}

			// Rebuild and reassign the view AND projection matrices explicitly, every frame this
			// mod is enabled - regardless of calibration, so zoom alone (before F9 is ever
			// pressed) reaches the render too. Two separate Unity gotchas this works around:
			// (1) Kino's Custom Camera mode sets Camera.worldToCameraMatrix explicitly (0.3.6),
			// which makes Unity stop deriving the view from the Transform - rebuilding it from the
			// (now-offset) Transform every frame wins regardless. (2) The zoom-not-working report
			// points at the same thing happening to Camera.projectionMatrix: fieldOfView changes
			// were clearly reaching *something* (the gauge Canvas visibly reacted to them), but
			// never the actual rendered view - consistent with Kino also freezing the projection
			// matrix independent of fieldOfView. Rebuilding it from fieldOfView explicitly, every
			// frame, guarantees the render matches whatever FOV was just set. Both are
			// mathematically identical to Unity's own defaults when nothing else is customizing
			// them, so this is a no-op visually anywhere Kino isn't fighting it.
			ApplyCameraOverride(cam);
		}

		/// <summary>
		/// Rebuilds and reassigns Camera.worldToCameraMatrix, Camera.projectionMatrix, and the
		/// matching cullingMatrix from the camera's current Transform/fieldOfView. See the comment
		/// at the call site in <see cref="OnCameraPreCull"/> for why both matrices need to be set
		/// explicitly rather than relying on Unity to derive them normally.
		/// </summary>
		private static void ApplyCameraOverride(Camera cam) {
			Matrix4x4 view = cam.transform.worldToLocalMatrix;
			// Unity's camera space looks down local -Z, while a plain worldToLocalMatrix follows
			// the transform's own +Z-forward convention - flipping the Z row is the standard way
			// to reconcile the two when building a view matrix manually.
			view.SetRow(2, -view.GetRow(2));

			Matrix4x4 proj = Matrix4x4.Perspective(cam.fieldOfView, cam.aspect, cam.nearClipPlane, cam.farClipPlane);

			cam.worldToCameraMatrix = view;
			cam.projectionMatrix = proj;
			cam.cullingMatrix = proj * view;
		}

		/// <summary>Reverts a camera to the game's own default view/projection/culling behavior.</summary>
		private static void ResetCameraOverride(Camera cam) {
			cam.ResetWorldToCameraMatrix();
			cam.ResetProjectionMatrix();
			cam.ResetCullingMatrix();
		}

		/// <summary>
		/// SRP (URP/HDRP) equivalent of <see cref="OnCameraPreCull"/> - see the comment on the
		/// subscription in <see cref="Start"/> for why both are hooked. Same logic either way, so
		/// this just forwards into the existing handler rather than duplicating it.
		/// </summary>
		private void OnBeginCameraRendering(ScriptableRenderContext context, Camera cam) {
			OnCameraPreCull(cam);
		}

		/// <summary>
		/// Raycasts from the camera's current (pre-offset) position along the direction of the
		/// desired head-offset, and clamps the offset short of anything it hits. Prevents the
		/// tracked camera from poking through the dashboard/seat/window when leaning in.
		///
		/// This is off by default (see IHeadTrackConfig.ClippingGuardEnabled) - it needs the
		/// layer mask tuned against CarX's actual cockpit collision geometry, which isn't
		/// something that could be verified without the real game running. See README.
		/// </summary>
		private Vector3 ApplyClippingGuard(Transform cameraTransform, Vector3 localPosOffset) {
			Vector3 worldOffset = cameraTransform.rotation * localPosOffset;
			float distance = worldOffset.magnitude;
			if (distance < 1e-5f) return localPosOffset;

			Vector3 direction = worldOffset / distance;
			int layerMask = config_.ClippingGuardLayerMask;
			float castDistance = distance + config_.ClippingGuardMargin;

			if (Physics.Raycast(cameraTransform.position, direction, out RaycastHit hit, castDistance, layerMask, QueryTriggerInteraction.Ignore)) {
				float allowedDistance = Mathf.Max(0f, hit.distance - config_.ClippingGuardMargin);
				if (allowedDistance < distance) {
					Vector3 clampedWorld = direction * allowedDistance;
					// Convert the clamped world-space distance back into the camera's local offset space.
					return Quaternion.Inverse(cameraTransform.rotation) * clampedWorld;
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
			}
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
		}

		private void ApplyDefaultsIfUnset() {
			// KSL config properties default to each type's zero value on first run - fill in
			// sane non-zero defaults the first time this mod loads.
			if (config_.OscPort <= 0) config_.OscPort = DefaultOscPort;
			if (config_.PositionSensitivity <= 0) config_.PositionSensitivity = 1.0f;
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

			// Same one-time-bump pattern for the new gauge-hide workaround - defaults it to ON
			// (the chosen fix for the recurring gauge-desync reports) without a plain bool default
			// silently reverting a user's own later choice to turn it back off.
			if (!config_.GaugeWorkaroundDefaultsApplied) {
				config_.HideGaugesWhileTracking = true;
				config_.GaugeWorkaroundDefaultsApplied = true;
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
			bool connected = receiver_.IsRunning &&
			                  receiver_.LastMessageTick != 0 &&
			                  Environment.TickCount - receiver_.LastMessageTick < 750;

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

			if (Kino.UI.Input(ref portText_, 5, "^[0-9]{1,5}$")) {
				if (int.TryParse(portText_, out int port) && port > 0 && port <= 65535) {
					config_.OscPort = port;
					if (config_.Enabled) {
						StartReceiver();
					}
				}
			}

			Kino.UI.Label("This PC's LAN IP (edit if auto-detect picked the wrong adapter):");
			if (showSensitive) {
				if (Kino.UI.Input(ref localIpText_, 45, "^[0-9a-fA-F:.]{0,45}$")) {
					config_.LocalIpOverride = localIpText_;
				}
			}
			else {
				Kino.UI.Label($"  {MaskAddress(localIpText_)}  (enable 'Show IP addresses' below to view/edit)");
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
				Kino.UI.Label($"  {MaskAddress(phoneIpText_)}  (enable 'Show IP addresses' below to view/edit)");
			}
			else {
				Kino.UI.Label("  (not set)");
			}

			Kino.UI.HorizontalLine();
			Kino.UI.GroupLabel("Privacy");
			Kino.UI.Label("Off by default - masks IP addresses above so they aren't exposed on stream or in screenshots.");
			if (Kino.UI.Toggle("Show IP addresses", ref showSensitive)) {
				config_.ShowSensitiveInfo = showSensitive;
			}

			Kino.UI.HorizontalLine();
			Kino.UI.GroupLabel("Look direction");
			Kino.UI.Label("If up/down or left/right ever feels backwards or reversed, flip it here.");

			bool invertPitch = config_.InvertPitch;
			if (Kino.UI.Toggle("Invert up/down look", ref invertPitch)) {
				config_.InvertPitch = invertPitch;
			}

			bool invertYaw = config_.InvertYaw;
			if (Kino.UI.Toggle("Invert left/right look", ref invertYaw)) {
				config_.InvertYaw = invertYaw;
			}

			Kino.UI.HorizontalLine();
			Kino.UI.GroupLabel("Gauge HUD workaround");
			Kino.UI.Label("MultiHUD's speedometer reads the camera directly, so it reacts to every head " +
				"movement/zoom the same way the 3D view does - two direct fixes didn't stop that from " +
				"looking wrong. This hides it entirely while PhoneCam is enabled instead, and restores " +
				"it the moment PhoneCam is turned off.");

			bool hideGauges = config_.HideGaugesWhileTracking;
			if (Kino.UI.Toggle("Hide gauges while PhoneCam is enabled", ref hideGauges)) {
				config_.HideGaugesWhileTracking = hideGauges;
				if (!hideGauges) SetGaugesHidden(false);
			}

			Kino.UI.Label(gaugeObjects_.Count > 0
				? $"Found {gaugeObjects_.Count} gauge object(s) to hide."
				: "No gauge object found yet - only searches while this is on and PhoneCam is enabled, so get into a session with the HUD visible first.");

			Kino.UI.Label("If the 3D dashboard gauge needles (not the 2D HUD above) spin/wobble while " +
				"looking around, that's a different, not-yet-identified object - press this the moment " +
				"you see it happening, then send the KSL log:");
			if (Kino.UI.Button("Log dashboard gauge diagnostics")) {
				LogDashboardGaugeDiagnostics();
			}

			Kino.UI.HorizontalLine();

			Kino.UI.Label(state_.IsCalibrated ? "Calibrated: yes" : "Calibrated: no - press F9 or the button below");

			if (Kino.UI.Button("Set Neutral Position (F9)")) {
				Calibrate();
			}

			Kino.UI.HorizontalLine();
			Kino.UI.GroupLabel("Sensitivity");

			float posSens = config_.PositionSensitivity;
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
			Kino.UI.Label("Re-press F9 any time you shift position.");
			Kino.UI.Label("Mouse wheel or +/- zooms the camera; F10 resets zoom.");
			Kino.UI.Label("Leaning/walking moves the camera too - raise 'Max position offset' in Sensitivity/Safety clamps for bigger, room-scale movement.");
			Kino.UI.Label("Looking/turning fully around (pitch and yaw) has no stopping point - only roll is limited by the safety clamp.");
			Kino.UI.Label("Cockpit clipping guard (off by default) stops the camera short of the dashboard/seat when leaning in.");
			Kino.UI.Label("Gauges hide automatically while PhoneCam is enabled (toggle in 'Gauge HUD workaround') since MultiHUD's speedometer reacts to every head movement/zoom otherwise.");

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

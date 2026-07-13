using System;
using HeadTrackARKit.Osc;
using HeadTrackARKit.Tracking;
using KSL.API;
using UnityEngine;

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
	[KSLMeta("PhoneCam", "0.1.0", "Chaoz2")]
	public class HeadTrackMod : BaseMod {
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

		private Camera cachedCamera_;
		private float cameraCacheTime_;

		// UIPhotoModeContext and its public "isActive" property were confirmed by directly
		// inspecting Assembly-CSharp.dll - both are public, so no reflection is needed. The
		// instance is found once and reused; "isActive" is re-read live every check since it
		// reflects CarX's own UI context stack switching Photo Mode on/off.
		private UIPhotoModeContext photoModeContext_;

		// Zoom is a persistent offset added on top of whatever FOV CarX's own camera logic sets
		// that frame (same "apply after the game" approach as the head-tracking offset itself),
		// so it composes with any dynamic FOV effects (speed, drift, etc.) instead of fighting them.
		private float zoomOffsetDegrees_;

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

			if (config_.Enabled) {
				StartReceiver();
			}

			Camera.onPreCull += OnCameraPreCull;

			Kino.Log.Info("[HeadTrackARKit] Loaded. Bind key default: F9 to calibrate neutral position.");
		}

		private void OnDestroy() {
			Camera.onPreCull -= OnCameraPreCull;
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
		}

		private void AdjustZoom(float deltaDegrees) {
			float max = Mathf.Abs(config_.MaxZoomOffset);
			zoomOffsetDegrees_ = Mathf.Clamp(zoomOffsetDegrees_ + deltaDegrees, -max, max);
		}

		private void ResetZoom() {
			zoomOffsetDegrees_ = 0f;
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
			}
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
			if (!config_.Enabled) return;
			if (cam != GetMainCamera()) return;

			// Zoom applies independently of head-tracking calibration.
			if (zoomOffsetDegrees_ != 0f) {
				cam.fieldOfView = Mathf.Clamp(cam.fieldOfView + zoomOffsetDegrees_, 1f, 179f);
			}

			if (!state_.IsCalibrated) return;

			Vector3 posOffset = state_.GetPositionOffset();
			Quaternion rotOffset = state_.GetRotationOffset();

			Transform t = cam.transform;

			if (config_.ClippingGuardEnabled && posOffset.sqrMagnitude > 1e-6f) {
				posOffset = ApplyClippingGuard(t, posOffset);
			}

			t.position += t.rotation * posOffset;
			t.rotation = t.rotation * rotOffset;
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
			if (config_.MaxPositionOffset <= 0) config_.MaxPositionOffset = 0.5f;
			if (config_.MaxRotationOffset <= 0) config_.MaxRotationOffset = 80f;
			if (config_.ZoomSensitivity <= 0) config_.ZoomSensitivity = 1.5f;
			if (config_.MaxZoomOffset <= 0) config_.MaxZoomOffset = 30f;
			if (config_.ClippingGuardMargin <= 0) config_.ClippingGuardMargin = 0.08f;
			// Layer mask 0 (nothing selected) would make the raycast a no-op; default to "everything"
			// so the toggle visibly does something the first time it's enabled, and let the user
			// narrow it down once they can see what it's catching against the real game.
			if (config_.ClippingGuardLayerMask == 0) config_.ClippingGuardLayerMask = ~0;
		}

		public override void OnUIDraw() {
			bool connected = receiver_.IsRunning &&
			                  receiver_.LastMessageTick != 0 &&
			                  Environment.TickCount - receiver_.LastMessageTick < 750;

			Kino.UI.Label("LOTA - LiDAR Over the Air (free, App Store) streams ARKit camera pose to this mod over OSC.");
			Kino.UI.Label(connected ? "Status: receiving data" : "Status: no data (check LOTA is streaming, same Wi-Fi, matching port)");

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

			float maxPos = config_.MaxPositionOffset;
			if (Kino.UI.Slider(ref maxPos, 0.05f, 1.5f, $"Max position offset: {maxPos:F2} m")) {
				config_.MaxPositionOffset = maxPos;
				state_.MaxPositionOffset = maxPos;
			}

			float maxRot = config_.MaxRotationOffset;
			if (Kino.UI.Slider(ref maxRot, 10f, 120f, $"Max rotation offset: {maxRot:F0} deg")) {
				config_.MaxRotationOffset = maxRot;
				state_.MaxRotationOffsetDegrees = maxRot;
			}

			Kino.UI.HorizontalLine();
			Kino.UI.GroupLabel("Zoom");
			Kino.UI.Label($"Current zoom offset: {zoomOffsetDegrees_:F1} deg (mouse wheel, or +/- keys)");

			float zoomSens = config_.ZoomSensitivity;
			if (Kino.UI.Slider(ref zoomSens, 0.1f, 5f, $"Zoom sensitivity: {zoomSens:F2}")) {
				config_.ZoomSensitivity = zoomSens;
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
			Kino.UI.Label("4. Set the destination IP to this PC's LAN address, port to match this mod (see Enabled/port above).");
			Kino.UI.Label("5. Make sure phone and PC are on the same Wi-Fi, then tap the shutter with STREAM selected.");

			Kino.UI.HorizontalLine();
			Kino.UI.GroupLabel("Using it");
			Kino.UI.Label("Sit in your normal position, then press F9 to set neutral - everything after is relative to that pose.");
			Kino.UI.Label("Re-press F9 any time you shift position.");
			Kino.UI.Label("Mouse wheel or +/- zooms the camera; F10 resets zoom.");
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

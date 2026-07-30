using UnityEngine;

namespace HeadTrackARKit.Tracking {
	/// <summary>
	/// Holds the live tracking state: latest smoothed pose, the calibrated "neutral" baseline,
	/// and produces the final camera-space offset to apply on top of the game's own camera
	/// each frame. Pure C#/UnityEngine math - no BaseMod/Kino dependency, so it can be reused
	/// or unit tested independently of the KSL plugin host.
	/// </summary>
	public sealed class HeadTrackState {
		/// <summary>Position smoothing factor per frame, 0..1. Higher = snappier, lower = smoother/laggier.</summary>
		public float PositionSmoothing = 0.35f;

		/// <summary>Rotation smoothing factor per frame, 0..1. Only used when the adaptive filter is off.</summary>
		public float RotationSmoothing = 0.45f;

		/// <summary>Use the adaptive (1-euro) filter instead of the fixed-strength low-pass.</summary>
		public bool AdaptiveFilterEnabled = true;

		/// <summary>
		/// Cutoff in Hz when the phone is essentially still. Lower = steadier at rest. 1 Hz gives very
		/// heavy smoothing of ARKit's resting noise while remaining well above any real head movement.
		/// </summary>
		public float RotationMinCutoffHz = 1.0f;

		/// <summary>
		/// How fast the cutoff opens up with angular speed (per deg/s). Higher = more responsive to
		/// deliberate movement, less noise rejection during it (noise matters far less while moving).
		/// </summary>
		public float RotationSpeedCoefficient = 0.02f;

		/// <summary>Cutoff in Hz for position when essentially still.</summary>
		public float PositionMinCutoffHz = 1.0f;

		/// <summary>How fast the position cutoff opens up with linear speed (per m/s).</summary>
		public float PositionSpeedCoefficient = 2.0f;

		/// <summary>Multiplies the calibrated position delta before it's applied to the camera (meters -> meters).</summary>
		public float PositionSensitivity = 1.0f;

		/// <summary>Multiplies the calibrated rotation delta before it's applied to the camera (degrees -> degrees).</summary>
		public float RotationSensitivity = 1.0f;

		/// <summary>Clamp on translation offset magnitude, in meters, applied per axis after sensitivity scaling.</summary>
		public float MaxPositionOffset = 0.5f;

		/// <summary>Clamp on rotation offset, in degrees, applied per euler axis after sensitivity scaling.</summary>
		public float MaxRotationOffsetDegrees = 80f;

		private bool hasRawSample_;
		private Vector3 rawPosition_;
		private Quaternion rawRotation_ = Quaternion.identity;

		private bool hasSmoothedSample_;
		private Vector3 smoothedPosition_;
		private Quaternion smoothedRotation_ = Quaternion.identity;

		private bool isCalibrated_;
		private Vector3 basePosition_;

		// 0.3.15: world-referenced calibration baseline for rotation - see GetRotationOffsetEuler's
		// doc comment for why yaw/pitch are measured this way instead of via a full-orientation delta.
		private float baseYaw_;
		private float basePitch_;
		private float baseRoll_;

		// 0.3.16: yaw-only calibration frame for translation - see GetPositionOffset's doc comment
		// for why this replaced the old full-orientation baseRotationInverse_.
		private Quaternion baseYawOnlyInverse_ = Quaternion.identity;

		public bool IsCalibrated => isCalibrated_;
		public bool HasSignal => hasRawSample_;

		/// <summary>
		/// Feed a freshly-converted (already ARKit->Unity converted) pose sample.
		///
		/// 0.5.2: this used to also advance the smoothing, once per received sample, with a fixed lerp
		/// factor. That made the effective smoothing depend on the PACKET RATE rather than on time: at a
		/// high send rate the filter converged almost instantly (no smoothing at all), at a low or
		/// uneven rate it lagged, and when several packets arrived within one rendered frame the filter
		/// advanced several times for that single frame. A phone stream whose rate wobbles therefore
		/// produced a camera whose smoothing wobbled with it - a jitter source of its own. Sampling is
		/// now decoupled: this stores the newest raw pose only, and <see cref="UpdateSmoothing"/>
		/// advances the filter exactly once per frame against real elapsed time.
		/// </summary>
		public void PushSample(Vector3 unityPosition, Quaternion unityRotation) {
			rawPosition_ = unityPosition;
			rawRotation_ = unityRotation;
			hasRawSample_ = true;

			if (!hasSmoothedSample_) {
				smoothedPosition_ = unityPosition;
				smoothedRotation_ = unityRotation;
				hasSmoothedSample_ = true;
			}
		}

		/// <summary>
		/// Advances the smoothing filter toward the newest raw sample. Call once per frame with the
		/// frame's unscaled delta time.
		///
		/// Exponential, frame-rate independent: PositionSmoothing/RotationSmoothing are "fraction of the
		/// remaining error closed per 1/60 s", scaled by the real frame time - the same formulation the
		/// zoom easing already used. Identical response at 30, 60 or 144 fps, and unaffected by how many
		/// OSC packets happen to land in a given frame.
		/// </summary>
		public void UpdateSmoothing(float deltaTime) {
			if (!hasRawSample_ || !hasSmoothedSample_) return;
			if (deltaTime <= 0f) return;

			if (AdaptiveFilterEnabled) {
				UpdateAdaptiveSmoothing(deltaTime);
				return;
			}

			float posRate = 1f - Mathf.Pow(1f - Mathf.Clamp01(PositionSmoothing), deltaTime * 60f);
			float rotRate = 1f - Mathf.Pow(1f - Mathf.Clamp01(RotationSmoothing), deltaTime * 60f);

			smoothedPosition_ = Vector3.Lerp(smoothedPosition_, rawPosition_, posRate);
			smoothedRotation_ = Quaternion.Slerp(smoothedRotation_, rawRotation_, rotRate);
		}

		/// <summary>
		/// 0.6.0 - adaptive (1-euro style) filtering. This is the fix for the driving shake.
		///
		/// A fixed-strength low-pass cannot solve tracker jitter: set it strong enough to kill the
		/// noise and deliberate head movement lags badly; set it weak enough to feel responsive - which
		/// is where this mod's saved values sat (RotationSmoothing 0.83 closes ~48% of the error every
		/// frame, i.e. effectively unfiltered) - and ARKit's high-frequency noise goes straight to the
		/// camera. Parked, a few tenths of a degree of that noise is nearly invisible against a static
		/// scene. Driving, the same few tenths swing the whole world across the screen, which is why
		/// the shake appeared only while moving.
		///
		/// The 1-euro filter resolves that trade-off by making the cutoff frequency track the signal's
		/// own speed: nearly still -> very low cutoff -> heavy smoothing -> rock steady; moving fast ->
		/// high cutoff -> light smoothing -> no perceptible lag. It is the standard solution for
		/// exactly this problem (noisy 6DOF pose streams) and needs no per-user tuning.
		///
		///   cutoff = MinCutoffHz + SpeedCoefficient * speed
		///   tau    = 1 / (2*pi*cutoff)
		///   alpha  = dt / (tau + dt)
		///
		/// Speed is measured against the previous SMOOTHED value, so the filter reacts to real motion
		/// rather than to noise spikes.
		/// </summary>
		private void UpdateAdaptiveSmoothing(float deltaTime) {
			// --- rotation ---
			float angleDelta = Quaternion.Angle(smoothedRotation_, rawRotation_);
			float angularSpeed = angleDelta / deltaTime;                      // deg/s
			float rotCutoff = RotationMinCutoffHz + RotationSpeedCoefficient * angularSpeed;
			smoothedRotation_ = Quaternion.Slerp(smoothedRotation_, rawRotation_, Alpha(rotCutoff, deltaTime));

			// --- position ---
			float linearSpeed = (rawPosition_ - smoothedPosition_).magnitude / deltaTime;  // m/s
			float posCutoff = PositionMinCutoffHz + PositionSpeedCoefficient * linearSpeed;
			smoothedPosition_ = Vector3.Lerp(smoothedPosition_, rawPosition_, Alpha(posCutoff, deltaTime));
		}

		private static float Alpha(float cutoffHz, float deltaTime) {
			float tau = 1f / (2f * Mathf.PI * Mathf.Max(0.0001f, cutoffHz));
			return Mathf.Clamp01(deltaTime / (tau + deltaTime));
		}

		/// <summary>Set the current smoothed pose as the "looking at the screen normally" baseline.</summary>
		public void Calibrate() {
			if (!hasSmoothedSample_) return;

			basePosition_ = smoothedPosition_;
			ComputeWorldYawPitchRoll(smoothedRotation_, out baseYaw_, out basePitch_, out baseRoll_);
			baseYawOnlyInverse_ = Quaternion.Inverse(Quaternion.Euler(0f, baseYaw_, 0f));
			isCalibrated_ = true;
		}

		public void ClearCalibration() {
			isCalibrated_ = false;
		}

		/// <summary>
		/// Delta position, in a calibration-time frame, sensitivity-scaled and clamped. Meant to be
		/// rotated into the game camera's current facing before being added to its world position
		/// (see HeadTrackMod.OnCameraPreCull).
		///
		/// ARKit's position stream is real-world, gravity-aligned meters (see ArKitConversion -
		/// only Z gets mirrored for handedness; X/Y pass straight through), so a real step to the
		/// side is genuinely a horizontal-plane vector and a real step up/down is genuinely a
		/// vertical one, regardless of how the phone happens to be held. Through 0.3.15 this delta
		/// was rotated by the *full* calibration orientation's inverse (every axis: yaw, pitch, AND
		/// roll) - the same quantity that turned out to cause the look-direction bug (see
		/// GetRotationOffsetEuler's doc comment): this rig sits with roughly 90 degrees of roll at
		/// calibration time. Rotating a horizontal step vector through that much roll swaps it onto
		/// the *vertical* axis of the result, so "step right" was arriving at HeadTrackMod as
		/// mostly an up/down offset instead of a left/right one - easy to mistake for "stepping
		/// doesn't do anything" if the vertical clamp happened to eat it, or just confusing if it
		/// visibly moved the camera the wrong way.
		///
		/// 0.3.16 fixes this the same way as the rotation bug: only the *yaw* component of the
		/// calibration orientation is used to build the frame (<see cref="baseYawOnlyInverse_"/>),
		/// not the full 3D orientation. Yaw-only rotation can't tilt the world's vertical axis into
		/// a horizontal one or vice versa, so real up/down steps stay on Y and real left/right/
		/// forward/back steps stay on the horizontal plane, no matter how much roll the phone is
		/// physically held at. Calibration-time yaw is still what "forward" means for the rest of
		/// the session (so "lean forward" always pushes the camera forward even if you turn your
		/// head afterward) - only the roll/pitch contamination is removed.
		/// </summary>
		public Vector3 GetPositionOffset() {
			if (!isCalibrated_ || !hasSmoothedSample_) return Vector3.zero;

			Vector3 delta = baseYawOnlyInverse_ * (smoothedPosition_ - basePosition_);
			delta *= PositionSensitivity;

			return new Vector3(
				Mathf.Clamp(delta.x, -MaxPositionOffset, MaxPositionOffset),
				Mathf.Clamp(delta.y, -MaxPositionOffset, MaxPositionOffset),
				Mathf.Clamp(delta.z, -MaxPositionOffset, MaxPositionOffset));
		}

		/// <summary>
		/// Delta rotation relative to the calibrated baseline, sensitivity-scaled, returned as a
		/// raw (pitch, yaw, roll) triple rather than a Quaternion.
		///
		/// 0.3.11 removed the pitch/yaw clamp but kept computing this via
		/// <c>delta.eulerAngles</c> - real-game testing then showed the actual bug: standard XYZ
		/// Euler decomposition isn't stable everywhere, and yaw approaching +-180 degrees (i.e.
		/// looking behind you) sits right in an unstable region for it, bleeding rotation between
		/// axes. 0.3.12 fixed that by extracting yaw/pitch via atan2 from a *delta* quaternion
		/// (<c>baseRotationInverse_ * smoothedRotation_</c>) instead of decomposing eulerAngles.
		///
		/// That still wasn't enough: a v0.3.14 log caught the camera pitching hard on ordinary
		/// left/right turning (appliedOffsetEuler.x reaching -89 after roughly a 90-degree turn),
		/// and the same log's incomingEuler showed why - this rig's roll (z) sits around 85-98
		/// degrees *all the time*, meaning the phone is physically held/mounted rolled about 90
		/// degrees from "upright." A delta quaternion re-expresses rotation in the *phone's own
		/// calibrated-local frame* - and if that frame is itself rolled ~90 degrees from true
		/// world-vertical, a real yaw motion (turning around the true vertical axis) ends up
		/// aligned with the phone's local *pitch* axis instead, so atan2 faithfully reports a big
		/// pitch even though the user only turned left/right. This isn't a decomposition-instability
		/// bug like 0.3.12's - the delta math was working exactly as designed, it's just the wrong
		/// reference frame for a rig with a non-trivial mounting roll.
		///
		/// Fixed in 0.3.15 by measuring yaw/pitch against true world axes instead of the phone's
		/// own (possibly rolled) calibrated frame: both the current orientation and the calibration
		/// baseline get their own world-referenced (yaw, pitch, roll) via
		/// <see cref="ComputeWorldYawPitchRoll"/>, and the *offsets* are plain angle subtractions
		/// (yaw-yaw, pitch-pitch, roll-roll), each wrapped to -180..180. Because both quantities are
		/// always measured against the same fixed world axes, whatever constant roll the phone
		/// happens to be held at cancels out of the subtraction cleanly - a real yaw turn stays yaw
		/// regardless of mounting angle.
		/// </summary>
		public Vector3 GetRotationOffsetEuler() {
			if (!isCalibrated_ || !hasSmoothedSample_) return Vector3.zero;

			ComputeWorldYawPitchRoll(smoothedRotation_, out float yaw, out float pitch, out float roll);

			Vector3 euler = new Vector3(
				NormalizeAngle(pitch - basePitch_),
				NormalizeAngle(yaw - baseYaw_),
				NormalizeAngle(roll - baseRoll_)) * RotationSensitivity;
			euler.z = Mathf.Clamp(euler.z, -MaxRotationOffsetDegrees, MaxRotationOffsetDegrees);

			return euler;
		}

		/// <summary>
		/// World-referenced (yaw, pitch, roll) for an absolute orientation quaternion - yaw is
		/// rotation around true world-up (Vector3.up), pitch is elevation above/below the world-
		/// horizontal plane, and roll is the twist remaining around the forward axis once yaw/pitch
		/// are accounted for. Using world axes (rather than a calibrated-local delta) means these
		/// three numbers mean the same physical thing - "compass heading," "look up/down,"
		/// "tilt sideways" - no matter what roll the phone itself is held/mounted at; see
		/// GetRotationOffsetEuler's doc comment for why that distinction matters. Degenerates only
		/// when looking exactly straight up or down (fwd parallel to Vector3.up), same as the
		/// atan2/LookRotation approach it's built from.
		/// </summary>
		private static void ComputeWorldYawPitchRoll(Quaternion q, out float yaw, out float pitch, out float roll) {
			Vector3 fwd = q * Vector3.forward;

			yaw = Mathf.Atan2(fwd.x, fwd.z) * Mathf.Rad2Deg;
			float horizontalLen = Mathf.Sqrt(fwd.x * fwd.x + fwd.z * fwd.z);
			pitch = Mathf.Atan2(-fwd.y, horizontalLen) * Mathf.Rad2Deg;

			// zeroRoll points the same direction (same forward vector) with zero roll relative to
			// world-up by construction (Quaternion.LookRotation always picks the least-rolled
			// orientation for a given forward+up pair). Whatever's left after removing it is a pure
			// rotation around the shared forward axis - i.e. just the roll, cleanly.
			Quaternion zeroRoll = Quaternion.LookRotation(fwd, Vector3.up);
			roll = NormalizeAngle((Quaternion.Inverse(zeroRoll) * q).eulerAngles.z);
		}

		// Unity's eulerAngles are 0..360 per axis, which makes small negative rotations show up
		// as ~359 degrees. Remap each axis into -180..180 so clamping/sensitivity math behaves.
		private static Vector3 NormalizeEuler(Vector3 euler) {
			return new Vector3(NormalizeAngle(euler.x), NormalizeAngle(euler.y), NormalizeAngle(euler.z));
		}

		private static float NormalizeAngle(float angle) {
			angle %= 360f;
			if (angle > 180f) angle -= 360f;
			if (angle < -180f) angle += 360f;
			return angle;
		}
	}
}

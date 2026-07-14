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

		/// <summary>Rotation smoothing factor per frame, 0..1.</summary>
		public float RotationSmoothing = 0.45f;

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
		private Quaternion baseRotationInverse_ = Quaternion.identity;

		// 0.3.15: world-referenced calibration baseline for rotation - see GetRotationOffsetEuler's
		// doc comment for why yaw/pitch are measured this way instead of via baseRotationInverse_.
		private float baseYaw_;
		private float basePitch_;
		private float baseRoll_;

		public bool IsCalibrated => isCalibrated_;
		public bool HasSignal => hasRawSample_;

		/// <summary>Feed a freshly-converted (already ARKit->Unity converted) pose sample.</summary>
		public void PushSample(Vector3 unityPosition, Quaternion unityRotation) {
			rawPosition_ = unityPosition;
			rawRotation_ = unityRotation;
			hasRawSample_ = true;

			if (!hasSmoothedSample_) {
				smoothedPosition_ = unityPosition;
				smoothedRotation_ = unityRotation;
				hasSmoothedSample_ = true;
				return;
			}

			smoothedPosition_ = Vector3.Lerp(smoothedPosition_, unityPosition, Mathf.Clamp01(PositionSmoothing));
			smoothedRotation_ = Quaternion.Slerp(smoothedRotation_, unityRotation, Mathf.Clamp01(RotationSmoothing));
		}

		/// <summary>Set the current smoothed pose as the "looking at the screen normally" baseline.</summary>
		public void Calibrate() {
			if (!hasSmoothedSample_) return;

			basePosition_ = smoothedPosition_;
			baseRotationInverse_ = Quaternion.Inverse(smoothedRotation_);
			ComputeWorldYawPitchRoll(smoothedRotation_, out baseYaw_, out basePitch_, out baseRoll_);
			isCalibrated_ = true;
		}

		public void ClearCalibration() {
			isCalibrated_ = false;
		}

		/// <summary>
		/// Delta position, in the *head's own local frame at calibration time*, sensitivity-scaled
		/// and clamped. Meant to be rotated into the game camera's current facing before being added
		/// to its world position (see HeadTrackMod.ApplyToCamera).
		/// </summary>
		public Vector3 GetPositionOffset() {
			if (!isCalibrated_ || !hasSmoothedSample_) return Vector3.zero;

			Vector3 delta = baseRotationInverse_ * (smoothedPosition_ - basePosition_);
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

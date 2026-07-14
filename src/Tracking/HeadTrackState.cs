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
		/// Delta rotation relative to the calibrated baseline, sensitivity-scaled. Pitch (x) and
		/// yaw (y) are intentionally left unclamped as of 0.3.11 - MaxRotationOffsetDegrees no
		/// longer applies to them at all - so physically turning your whole body all the way around
		/// keeps rotating the camera continuously instead of stopping partway. This is safe:
		/// Quaternion.Euler builds its rotation from sin/cos of the given angles, which are
		/// periodic, so there's no discontinuity or error even as these values grow past +-180 (a
		/// real 370-degree turn and a 10-degree turn produce the same, correct final orientation).
		/// Roll (z) still clamps to MaxRotationOffsetDegrees - unlimited roll would let the camera
		/// corkscrew, which nothing in a real head/body movement would naturally produce.
		/// </summary>
		public Quaternion GetRotationOffset() {
			if (!isCalibrated_ || !hasSmoothedSample_) return Quaternion.identity;

			Quaternion delta = baseRotationInverse_ * smoothedRotation_;

			Vector3 euler = NormalizeEuler(delta.eulerAngles);
			euler *= RotationSensitivity;
			euler.z = Mathf.Clamp(euler.z, -MaxRotationOffsetDegrees, MaxRotationOffsetDegrees);

			return Quaternion.Euler(euler);
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

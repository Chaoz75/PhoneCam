using UnityEngine;

namespace HeadTrackARKit.Tracking {
	/// <summary>
	/// ARKit reports camera pose in a right-handed coordinate system where the camera looks
	/// down its local -Z axis (same convention as OpenGL / most AR/CV libraries). Unity is
	/// left-handed with the camera looking down local +Z. This class converts LOTA's raw
	/// ARKit pose (as sent over OSC) into Unity world-space position/rotation.
	///
	/// This is the same technique used by Apple's own official ARKit-Unity plugin
	/// (UnityARMatrixOps.GetPosition / GetQuaternion): mirror every basis vector's Z
	/// component to flip handedness, then rebuild the rotation with LookRotation so the
	/// result is expressed in Unity's local +Z-forward camera convention.
	/// </summary>
	public static class ArKitConversion {
		public static Vector3 ToUnityPosition(Vector3 arPosition) {
			return new Vector3(arPosition.x, arPosition.y, -arPosition.z);
		}

		public static Quaternion ToUnityRotation(Quaternion arRotation) {
			float x = arRotation.x, y = arRotation.y, z = arRotation.z, w = arRotation.w;

			// 'up' and 'back' basis columns extracted from the ARKit quaternion's rotation matrix.
			Vector3 up = new Vector3(
				2f * (x * y - w * z),
				1f - 2f * (x * x + z * z),
				2f * (y * z + w * x));

			Vector3 back = new Vector3(
				2f * (x * z + w * y),
				2f * (y * z - w * x),
				1f - 2f * (x * x + y * y));

			// Mirror into Unity's world space (negate Z of each basis vector).
			Vector3 upUnity = new Vector3(up.x, up.y, -up.z);
			Vector3 forwardUnity = new Vector3(back.x, back.y, -back.z);

			if (forwardUnity.sqrMagnitude < 1e-8f) {
				return Quaternion.identity;
			}

			return Quaternion.LookRotation(forwardUnity, upUnity);
		}
	}
}

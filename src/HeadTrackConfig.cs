namespace HeadTrackARKit {
	/// <summary>
	/// Persisted mod settings, registered with Kino.Config. All members use types from KSL's
	/// natively supported list (bool/float/int/string) so no custom TypeParser is required.
	/// </summary>
	public interface IHeadTrackConfig {
		/// <summary>Master on/off switch for applying the camera offset.</summary>
		bool Enabled { get; set; }

		/// <summary>UDP port to listen on for LOTA's OSC stream. Must match the port set in LOTA's Transmission Settings.</summary>
		int OscPort { get; set; }

		/// <summary>Multiplier applied to head translation (leaning) before it reaches the camera.</summary>
		float PositionSensitivity { get; set; }

		/// <summary>Multiplier applied to head rotation (looking around) before it reaches the camera.</summary>
		float RotationSensitivity { get; set; }

		/// <summary>0..1 smoothing factor for incoming position samples. Lower = smoother but laggier.</summary>
		float PositionSmoothing { get; set; }

		/// <summary>0..1 smoothing factor for incoming rotation samples. Lower = smoother but laggier.</summary>
		float RotationSmoothing { get; set; }

		/// <summary>Max translation offset, in meters, applied per axis after sensitivity scaling.</summary>
		float MaxPositionOffset { get; set; }

		/// <summary>Max rotation offset, in degrees, applied per euler axis after sensitivity scaling.</summary>
		float MaxRotationOffset { get; set; }

		// --- Zoom / FOV control ---

		/// <summary>Degrees of FOV change per mouse scroll notch.</summary>
		float ZoomSensitivity { get; set; }

		/// <summary>How far the zoom offset can pull the FOV in (negative) or out (positive), in degrees.</summary>
		float MaxZoomOffset { get; set; }

		/// <summary>
		/// 0..1 - how quickly the applied zoom eases toward the scroll-wheel/key target each
		/// frame. Higher = snappier, lower = smoother/slower. Frame-rate independent.
		/// </summary>
		float ZoomSmoothing { get; set; }

		// --- Cockpit clipping guard ---

		/// <summary>Master toggle. Off by default - needs the layer mask tuned against the real game first (see README).</summary>
		bool ClippingGuardEnabled { get; set; }

		/// <summary>Unity layer mask (as an int bitmask) the clipping raycast tests against.</summary>
		int ClippingGuardLayerMask { get; set; }

		/// <summary>Distance, in meters, kept between the camera and whatever the clipping raycast hits.</summary>
		float ClippingGuardMargin { get; set; }

		// --- Manual network overrides ---

		/// <summary>
		/// Manually-entered override for the displayed "this PC's LAN IP" text. Empty means show
		/// the auto-detected IP instead. Purely informational - the OSC listener always binds to
		/// all local interfaces regardless of this value, so this only affects what's shown/typed
		/// into LOTA, not how the mod actually listens.
		/// </summary>
		string LocalIpOverride { get; set; }

		/// <summary>
		/// If set, only OSC packets whose sender IP matches this exact string are accepted -
		/// anything else hitting the port is silently ignored. Empty (default) accepts from any
		/// sender, same as before this setting existed.
		/// </summary>
		string PhoneIpFilter { get; set; }

		// --- Look direction fix ---
		// Replaces the old MountRollDegrees cycle-based correction (removed in 0.3.9) - multiple
		// real-game tests confirmed a clean, consistent pitch/yaw swap (turning your head
		// left/right moved the in-game camera up/down, and vice versa) that cycling never
		// resolved. HeadTrackMod.FixLookDirection now swaps the final offset's pitch/yaw
		// unconditionally instead, with these two as a quick escape hatch if either direction
		// still ends up backwards.

		/// <summary>Flips the up/down look direction after the pitch/yaw swap.</summary>
		bool InvertPitch { get; set; }

		/// <summary>Flips the left/right look direction after the pitch/yaw swap.</summary>
		bool InvertYaw { get; set; }

		// --- Privacy ---

		/// <summary>
		/// When false (the default), IP addresses shown in the settings panel (last sender IP,
		/// this PC's LAN IP, phone IP filter) are masked out so they aren't exposed on stream or
		/// in screenshots. Toggle on temporarily to read/edit the real values.
		/// </summary>
		bool ShowSensitiveInfo { get; set; }
	}
}

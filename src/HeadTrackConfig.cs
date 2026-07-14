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

		// --- Orientation correction ---

		/// <summary>
		/// Compensates for how the phone is physically mounted relative to what LOTA's raw
		/// ARKit axes assume - one of 0/90/180/270. If tilting the phone up/down moves the
		/// camera left/right instead of up/down (a known symptom when the phone ends up
		/// mounted rotated relative to LOTA's expected orientation), cycle this until up/down
		/// and left/right map correctly. Applied identically to both the incoming position and
		/// rotation data, since both come from the same physical mount.
		/// </summary>
		int MountRollDegrees { get; set; }

		// --- Privacy ---

		/// <summary>
		/// When false (the default), IP addresses shown in the settings panel (last sender IP,
		/// this PC's LAN IP, phone IP filter) are masked out so they aren't exposed on stream or
		/// in screenshots. Toggle on temporarily to read/edit the real values.
		/// </summary>
		bool ShowSensitiveInfo { get; set; }
	}
}

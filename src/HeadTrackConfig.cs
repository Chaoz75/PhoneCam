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

		/// <summary>
		/// One-time migration flag - the very first time this loads on 0.3.10+, MaxPositionOffset
		/// gets bumped from the old 0.5m "seat lean" default up to a "walk a few real steps"
		/// free-cam-scale default, even for installs that already have a saved (smaller) value from
		/// before. After that one bump this is never touched again, so tuning the slider afterward
		/// always sticks.
		/// </summary>
		bool PositionRangeUpgraded { get; set; }

		/// <summary>
		/// Max roll offset, in degrees, applied after sensitivity scaling. As of 0.3.11 this only
		/// clamps roll (tilting your head sideways) - pitch (up/down) and yaw (left/right) are left
		/// unclamped so a full real-world 360-degree turn keeps rotating the camera continuously
		/// instead of stopping partway. See HeadTrackState.GetRotationOffset.
		/// </summary>
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
		// Replaces the old MountRollDegrees cycle-based correction (removed in 0.3.9). 0.3.9 also
		// added an unconditional pitch/yaw swap here, validated at the time against the old
		// eulerAngles-based rotation extraction - but a v0.3.12 log caught that swap funneling a
		// large, clean yaw value (a normal full-360 turn) into the applied rotation's *pitch* slot,
		// which is what was flipping the camera upside down on full spins. 0.3.13 removed the swap
		// now that extraction is clean (atan2-based, see HeadTrackState.GetRotationOffsetEuler):
		// raw pitch/yaw route straight through, unbounded yaw stays flip-safe, and these two remain
		// as a quick escape hatch if either direction ever reads backwards on this rig.

		/// <summary>Flips the up/down look direction.</summary>
		bool InvertPitch { get; set; }

		/// <summary>Flips the left/right look direction.</summary>
		bool InvertYaw { get; set; }

		// --- Gauge HUD (MultiHUD) conflict workaround ---
		// Two direct fixes (0.3.8's Transform-mutation revert + matrix override, and just living
		// with it) didn't stop MultiHUD's gauge Canvas from visibly reacting to every head
		// movement/zoom - it reads the camera's real Transform/FOV directly by design (Screen-Space
		// -Camera rendering), so anything that moves the camera moves it too. Rather than keep
		// fighting a Canvas this mod doesn't own, 0.3.10 just hides it outright while PhoneCam is
		// enabled and restores it the moment PhoneCam is turned off.

		/// <summary>
		/// When true (default from 0.3.10 on), MultiHUD's gauge object(s) - found at runtime by
		/// name, see HeadTrackMod.RefreshGaugeObjects - are deactivated while config_.Enabled is
		/// true, and reactivated the moment it's turned off. Turn off to leave the gauges alone
		/// (they'll go back to reacting to head movement/zoom as before).
		/// </summary>
		bool HideGaugesWhileTracking { get; set; }

		/// <summary>One-time migration flag mirroring PositionRangeUpgraded - see that property.</summary>
		bool GaugeWorkaroundDefaultsApplied { get; set; }

		// --- Privacy ---

		/// <summary>
		/// When false (the default), IP addresses shown in the settings panel (last sender IP,
		/// this PC's LAN IP, phone IP filter) are masked out so they aren't exposed on stream or
		/// in screenshots. Toggle on temporarily to read/edit the real values.
		/// </summary>
		bool ShowSensitiveInfo { get; set; }
	}
}

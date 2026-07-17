using System;
using System.Collections.Generic;
using System.Text;

namespace HeadTrackARKit.Osc {
	/// <summary>
	/// A single parsed OSC 1.0 message: an address pattern plus its typed arguments.
	/// </summary>
	public readonly struct OscMessage {
		public readonly string Address;
		public readonly object[] Args;

		public OscMessage(string address, object[] args) {
			Address = address;
			Args = args;
		}

		public float GetFloat(int index, float fallback = 0f) {
			if (Args == null || index < 0 || index >= Args.Length) return fallback;
			return Args[index] switch {
				float f => f,
				int i => i,
				double d => (float)d,
				_ => fallback
			};
		}
	}

	/// <summary>
	/// Minimal dependency-free OSC 1.0 packet parser. Handles the subset of the spec LOTA
	/// actually uses on the wire: single messages (no bundles), with 'f' (float32 BE),
	/// 'i' (int32 BE), and 's' (padded string) argument types. Blobs ('b') are skipped safely.
	///
	/// Wire format recap (OSC 1.0):
	///   [address pattern]   null-terminated ASCII, padded with extra NULs to a 4-byte boundary
	///   [type tag string]   starts with ',', e.g. ",fff", null-terminated, padded to 4 bytes
	///   [arguments]         encoded back-to-back per the type tag, each type padded per its own rules
	/// No dependency on Unity types so this class can be compiled and unit tested standalone.
	/// </summary>
	public static class OscParser {
		private static readonly byte[] BundleTag = Encoding.ASCII.GetBytes("#bundle\0");

		/// <summary>
		/// Parses a raw UDP datagram into zero or more OSC messages, appending them to
		/// <paramref name="results"/>. This is the entry point <see cref="OscUdpReceiver"/> uses
		/// (instead of calling <see cref="TryParseMessage"/> directly), since a single UDP
		/// datagram can legally contain more than one message if the sender wraps them in an
		/// OSC 1.0 bundle.
		///
		/// 0.3.24: added because of a real, repeated dropout pattern - position AND rotation data
		/// both going completely silent for extended stretches mid-session (sometimes recovering
		/// on their own later, sometimes not) - which is consistent with LOTA switching to
		/// bundle-wrapped output under some condition (e.g. batching position+rotation into one
		/// packet together). Every earlier version of this parser explicitly rejected anything
		/// starting with "#" (the bundle marker - see the check still in
		/// <see cref="TryParseMessage"/>, kept there as a defensive leaf-level guard) rather than
		/// unwrapping it, so if that theory's right, every packet during those stretches was
		/// being silently and completely dropped - which looks exactly like "no data at all" on
		/// every diagnostic this mod has, without actually being a phone/Wi-Fi/ARKit-tracking
		/// problem.
		///
		/// Handles nested bundles (a bundle element can itself be another bundle, per the OSC 1.0
		/// spec) via recursion, and is defensive the same way TryParseMessage is: malformed or
		/// truncated bundle framing just stops parsing further elements rather than throwing,
		/// keeping whatever valid messages were already found up to that point.
		/// </summary>
		public static void ParseMessages(byte[] buffer, int length, List<OscMessage> results) {
			try {
				ParseMessagesInternal(buffer, length, results);
			}
			catch {
				// Defensive, same reasoning as TryParseMessage's own catch-all - never let a
				// corrupt/truncated datagram throw out of the parser. Whatever was already added
				// to `results` before the failure point is kept rather than discarded.
			}
		}

		private static void ParseMessagesInternal(byte[] buffer, int length, List<OscMessage> results) {
			if (buffer == null || length <= 0) return;

			if (length >= 16 && IsBundleTag(buffer)) {
				// "#bundle\0" (8 bytes) + an 8-byte NTP timetag (ignored - for a live head-
				// tracking stream only the newest sample matters, delivery-order scheduling
				// isn't meaningful here) + zero or more (int32 size, element) pairs, each
				// element itself either a plain message or another nested bundle.
				// 8 bytes for "#bundle\0" + 8 bytes for the timetag = 16 bytes consumed before
				// the first (size, element) pair.
				int offset = 16;

				while (offset + 4 <= length) {
					if (!ReadInt32(buffer, length, ref offset, out int elementSize)) return;
					if (elementSize < 0 || offset + elementSize > length) return;

					byte[] element = new byte[elementSize];
					Array.Copy(buffer, offset, element, 0, elementSize);
					ParseMessagesInternal(element, elementSize, results);

					offset += elementSize;
				}
				return;
			}

			if (TryParseMessage(buffer, length, out OscMessage msg)) {
				results.Add(msg);
			}
		}

		private static bool IsBundleTag(byte[] buffer) {
			for (int i = 0; i < BundleTag.Length; i++) {
				if (buffer[i] != BundleTag[i]) return false;
			}
			return true;
		}

		/// <summary>
		/// Attempt to parse a single OSC message from a raw UDP datagram (or a single unwrapped
		/// bundle element - see <see cref="ParseMessages"/>).
		/// Returns false (without throwing) on any malformed input - packets from the network
		/// should never be trusted enough to let a parse exception bubble up into game code.
		/// </summary>
		public static bool TryParseMessage(byte[] buffer, int length, out OscMessage message) {
			message = default;

			try {
				if (buffer == null || length <= 0) return false;

				// Bundles start with "#bundle" - not used by LOTA's camera/motion streams, skip.
				if (length >= 8 && buffer[0] == (byte)'#') return false;

				int offset = 0;

				if (!ReadPaddedString(buffer, length, ref offset, out string address)) return false;
				if (address.Length == 0 || address[0] != '/') return false;

				if (offset >= length) {
					// No type tag string -> address-only message, valid but argument-less.
					message = new OscMessage(address, Array.Empty<object>());
					return true;
				}

				if (!ReadPaddedString(buffer, length, ref offset, out string typeTags)) return false;
				if (typeTags.Length == 0 || typeTags[0] != ',') {
					// Some very old senders omit the type tag string; treat as no-args.
					message = new OscMessage(address, Array.Empty<object>());
					return true;
				}

				var args = new List<object>(typeTags.Length - 1);
				for (int i = 1; i < typeTags.Length; i++) {
					char tag = typeTags[i];
					switch (tag) {
						case 'f': {
							if (!ReadFloat32(buffer, length, ref offset, out float f)) return false;
							args.Add(f);
							break;
						}
						case 'i': {
							if (!ReadInt32(buffer, length, ref offset, out int iv)) return false;
							args.Add(iv);
							break;
						}
						case 's': {
							if (!ReadPaddedString(buffer, length, ref offset, out string s)) return false;
							args.Add(s);
							break;
						}
						case 'b': {
							if (!SkipBlob(buffer, length, ref offset)) return false;
							break;
						}
						case 'T':
							args.Add(true);
							break;
						case 'F':
							args.Add(false);
							break;
						case 'N':
							args.Add(null);
							break;
						default:
							// Unknown/unsupported tag type (OSC has many: 'h','d','t','S','c','r','m','I', etc.)
							// We don't know its size, so we can't safely keep decoding the rest of the packet.
							return false;
					}
				}

				message = new OscMessage(address, args.ToArray());
				return true;
			}
			catch {
				// Defensive: never let a corrupt/truncated UDP datagram throw out of the parser.
				return false;
			}
		}

		private static bool ReadPaddedString(byte[] buffer, int length, ref int offset, out string value) {
			value = null;
			int start = offset;
			int i = start;
			while (i < length && buffer[i] != 0) i++;
			if (i >= length) return false; // no NUL terminator found before end of packet

			value = Encoding.ASCII.GetString(buffer, start, i - start);

			// Advance past the NUL and pad to the next 4-byte boundary.
			int consumed = (i - start) + 1;
			int padded = (consumed + 3) & ~3;
			offset = start + padded;
			return offset <= length;
		}

		private static bool ReadFloat32(byte[] buffer, int length, ref int offset, out float value) {
			value = 0f;
			if (offset + 4 > length) return false;
			// OSC floats are big-endian on the wire; BitConverter reads using the platform's
			// endianness (little-endian on Windows/x86-64), so reverse the 4 bytes first.
			// (Avoiding Span<byte> here deliberately - net472 doesn't have it without pulling
			// in the System.Memory package, and a plain array is simplest for 4 bytes at ~30Hz.)
			byte[] be = { buffer[offset + 3], buffer[offset + 2], buffer[offset + 1], buffer[offset] };
			value = BitConverter.ToSingle(be, 0);
			offset += 4;
			return true;
		}

		private static bool ReadInt32(byte[] buffer, int length, ref int offset, out int value) {
			value = 0;
			if (offset + 4 > length) return false;
			value = (buffer[offset] << 24) | (buffer[offset + 1] << 16) | (buffer[offset + 2] << 8) | buffer[offset + 3];
			offset += 4;
			return true;
		}

		private static bool SkipBlob(byte[] buffer, int length, ref int offset) {
			if (!ReadInt32(buffer, length, ref offset, out int blobLen)) return false;
			if (blobLen < 0) return false;
			int padded = (blobLen + 3) & ~3;
			if (offset + padded > length) return false;
			offset += padded;
			return true;
		}
	}
}

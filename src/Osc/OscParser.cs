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
		/// <summary>
		/// Attempt to parse a single OSC message from a raw UDP datagram.
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

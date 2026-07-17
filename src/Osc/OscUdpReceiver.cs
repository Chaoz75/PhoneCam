using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace HeadTrackARKit.Osc {
	/// <summary>
	/// Listens for OSC-over-UDP packets on a background thread and hands parsed messages off
	/// through a thread-safe queue. IMPORTANT: Unity's API is not thread-safe, so nothing in
	/// this class should touch UnityEngine types directly. Call <see cref="DrainInto"/> from
	/// a Unity Update()/LateUpdate() on the main thread to consume messages.
	/// </summary>
	public sealed class OscUdpReceiver : IDisposable {
		private readonly ConcurrentQueue<OscMessage> queue_ = new ConcurrentQueue<OscMessage>();
		private UdpClient client_;
		private Thread thread_;
		private volatile bool running_;

		public int Port { get; private set; }
		public bool IsRunning => running_;

		/// <summary>Timestamp (Environment.TickCount) of the last successfully parsed packet, or 0 if none yet.</summary>
		public volatile int LastMessageTick;

		// 0.3.24: tracks every UDP datagram that actually hits the socket, regardless of whether
		// it went on to parse into a usable message (wrong sender, malformed, or - previously -
		// bundle-wrapped and silently rejected outright). Compared against LastMessageTick, this
		// is what lets a future log tell "packets are arriving but not turning into messages"
		// (a real, fixable bug in this mod's own parsing) apart from "nothing is arriving at the
		// socket at all" (phone/Wi-Fi/LOTA-side, outside this mod's control) - previously there
		// was no way to distinguish the two from the log alone.
		private long totalRawPacketsReceived_;

		/// <summary>Count of every UDP datagram received on this socket so far, parsed or not.</summary>
		public long TotalRawPacketsReceived => Interlocked.Read(ref totalRawPacketsReceived_);

		// Reference assignment is atomic in .NET and `volatile` gives the visibility guarantee
		// needed for the background receive thread to hand this off to the main thread without
		// a lock - same reasoning as LastMessageTick above.
		private volatile string lastSenderAddress_;

		/// <summary>IP address (no port) the most recent successfully-parsed packet came from, or null if none yet.</summary>
		public string LastSenderAddress => lastSenderAddress_;

		/// <summary>
		/// If set (non-null, non-empty), only packets whose sender IP matches this string exactly
		/// are accepted - everything else hitting the port is silently ignored. Null/empty (the
		/// default) accepts from any sender. Settable from the main thread at any time; read by
		/// the background receive thread, so this is volatile like the fields above.
		/// </summary>
		public volatile string AllowedSenderFilter;

		public event Action<Exception> OnError;

		public void Start(int port) {
			Stop();

			Port = port;
			client_ = new UdpClient(AddressFamily.InterNetwork);
			client_.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
			client_.Client.Bind(new IPEndPoint(IPAddress.Any, port));
			client_.Client.ReceiveTimeout = 1000;

			running_ = true;
			thread_ = new Thread(ReceiveLoop) {
				IsBackground = true,
				Name = "HeadTrackARKit-OscReceiver"
			};
			thread_.Start();
		}

		public void Stop() {
			running_ = false;

			try {
				client_?.Close();
			}
			catch {
				/* ignored - shutting down */
			}

			if (thread_ != null && thread_.IsAlive) {
				thread_.Join(1500);
			}

			client_ = null;
			thread_ = null;
		}

		private void ReceiveLoop() {
			var remote = new IPEndPoint(IPAddress.Any, 0);
			// Reused across iterations rather than allocated fresh each datagram - cleared at
			// the top of every loop instead. A live head-tracking stream calls this many times
			// a second, so avoiding a per-packet allocation here is cheap insurance.
			var messages = new List<OscMessage>(4);

			while (running_) {
				byte[] data;
				try {
					data = client_.Receive(ref remote);
				}
				catch (SocketException) {
					// Timeout or socket closed during shutdown - both expected, just loop/exit.
					continue;
				}
				catch (ObjectDisposedException) {
					break;
				}
				catch (Exception ex) {
					OnError?.Invoke(ex);
					continue;
				}

				// Counts every datagram that reaches the socket, before the sender filter or
				// parsing gets a say - see the field comment on TotalRawPacketsReceived.
				Interlocked.Increment(ref totalRawPacketsReceived_);

				string filter = AllowedSenderFilter;
				if (!string.IsNullOrEmpty(filter) &&
				    !string.Equals(remote.Address.ToString(), filter, StringComparison.OrdinalIgnoreCase)) {
					// Doesn't match the configured phone IP - ignore silently, same as noise from
					// any other device that happens to hit this port.
					continue;
				}

				// 0.3.24: was TryParseMessage(data, data.Length, out OscMessage msg) - a single
				// UDP datagram can legally contain more than one OSC message if the sender wraps
				// them in a bundle, which TryParseMessage alone can't unwrap (see ParseMessages's
				// doc comment for why this matters here specifically).
				messages.Clear();
				OscParser.ParseMessages(data, data.Length, messages);
				if (messages.Count > 0) {
					LastMessageTick = Environment.TickCount;
					lastSenderAddress_ = remote.Address.ToString();
					foreach (OscMessage msg in messages) {
						queue_.Enqueue(msg);
					}
				}
			}
		}

		/// <summary>
		/// Drain all currently queued messages. Call once per frame from the main thread.
		/// Bounded by maxMessages so a burst can't stall a frame.
		/// </summary>
		public int DrainInto(Action<OscMessage> handler, int maxMessages = 64) {
			int count = 0;
			while (count < maxMessages && queue_.TryDequeue(out OscMessage msg)) {
				handler(msg);
				count++;
			}
			return count;
		}

		public void Dispose() {
			Stop();
		}
	}
}

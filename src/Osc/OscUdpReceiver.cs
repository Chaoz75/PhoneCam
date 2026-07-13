using System;
using System.Collections.Concurrent;
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

				if (OscParser.TryParseMessage(data, data.Length, out OscMessage msg)) {
					LastMessageTick = Environment.TickCount;
					queue_.Enqueue(msg);
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

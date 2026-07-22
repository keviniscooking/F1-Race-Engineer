using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using F1Game.UDP;
using F1Game.UDP.Packets;

namespace F1RaceEngineer.Telemetry
{
    /// <summary>
    /// Listens for UDP telemetry packets broadcast by F1 25 / F1 25: 2026 Season Pack.
    /// Confirmed against the actual compiled F1Game.UDP 26.0.0 API via reflection dump
    /// (types live under F1Game.UDP.Packets / .Enums / .Data, not the root namespace).
    /// </summary>
    public class UdpListenerService
    {
        private UdpClient? _client;
        private CancellationTokenSource? _cts;

        public bool IsRunning { get; private set; }

        /// <summary>Fired for every telemetry packet received, regardless of type.</summary>
        public event Action<UnionPacket>? PacketReceived;

        /// <summary>Fired on any unhandled exception inside the receive loop, so the UI can surface it.</summary>
        public event Action<Exception>? ErrorOccurred;

        /// <summary>Fired once the socket is successfully bound and listening.</summary>
        public event Action? Started;

        /// <summary>Fired once listening has stopped.</summary>
        public event Action? Stopped;

        public void Start(int port)
        {
            if (IsRunning) return;

            // Built locally and only published to the fields once BOTH succeeded. Binding is
            // the step that realistically fails (another telemetry tool already holds the
            // port), and it used to leave the token source created on the line above stranded
            // and unreachable - one leaked per retry, and retrying a busy port is exactly what
            // the Connect button invites.
            var cts = new CancellationTokenSource();
            UdpClient client;
            try
            {
                client = new UdpClient(port);
            }
            catch
            {
                cts.Dispose();
                throw; // surfaced to the caller, which shows the bind failure in the top bar
            }

            _cts = cts;
            _client = client;
            IsRunning = true;
            Started?.Invoke();

            _ = Task.Run(() => ReceiveLoopAsync(cts.Token));
        }

        public void Stop()
        {
            // Resources are released unconditionally, but Stopped only fires if there was
            // actually something running - so calling this defensively (e.g. on window close
            // when never connected) can't emit a spurious "disconnected" to the UI.
            bool wasRunning = IsRunning;
            IsRunning = false;
            _cts?.Cancel();
            _client?.Close();
            _client?.Dispose();
            _client = null;
            // Disposed AND nulled, not just cancelled: Start() replaces this field, so a
            // Disconnect/Connect cycle used to abandon a live CancellationTokenSource every
            // time. Each one holds an unreleased kernel wait handle, so repeated reconnects
            // (the top bar's button invites exactly that) leaked one per cycle.
            _cts?.Dispose();
            _cts = null;
            if (wasRunning) Stopped?.Invoke();
        }

        private async Task ReceiveLoopAsync(CancellationToken token)
        {
            if (_client == null) return;

            while (!token.IsCancellationRequested)
            {
                try
                {
                    UdpReceiveResult result = await _client.ReceiveAsync(token);
                    HandlePacket(result.Buffer);
                }
                catch (OperationCanceledException)
                {
                    break; // expected on Stop()
                }
                catch (ObjectDisposedException)
                {
                    break; // expected on Stop()
                }
                catch (Exception ex)
                {
                    ErrorOccurred?.Invoke(ex);
                }
            }
        }

        private void HandlePacket(byte[] buffer)
        {
            try
            {
                UnionPacket packet = PacketReader.ToPacket(buffer);
                PacketReceived?.Invoke(packet);
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(ex);
            }
        }
    }
}

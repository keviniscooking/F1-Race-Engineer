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

            _cts = new CancellationTokenSource();
            _client = new UdpClient(port);
            IsRunning = true;
            Started?.Invoke();

            _ = Task.Run(() => ReceiveLoopAsync(_cts.Token));
        }

        public void Stop()
        {
            IsRunning = false;
            _cts?.Cancel();
            _client?.Close();
            _client?.Dispose();
            _client = null;
            Stopped?.Invoke();
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

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace HapticSeat.Telemetry.Il2;

/// <summary>
/// Escucha el UDP de IL-2 (por defecto 5678, donde startup.cfg emite en broadcast a 127.255.255.255).
/// El socket se abre con ReuseAddress para convivir con HFS, SimHub u otras apps en el mismo puerto.
/// </summary>
public sealed class Il2Source(int port = Il2Source.DefaultPort) : ITelemetrySource
{
    public const int DefaultPort = 5678;

    /// <summary>Sin paquetes durante este tiempo se considera desconectado.</summary>
    private const int TimeoutMs = 500;

    /// <summary>Tope de eventos sin consumir (si nadie los lee, se descartan los viejos).</summary>
    private const int MaxPendingEvents = 512;

    private readonly Il2Decoder _decoder = new();
    private readonly FlightState _state = new();
    private readonly List<FlightEvent> _events = new(64);
    private readonly Lock _lock = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private Socket? _socket;
    private Thread? _thread;
    private volatile bool _running;
    private long _lastPacketMs = -TimeoutMs;
    private long _packets;

    public string Name => $"IL-2 (UDP {port})";

    public long PacketCount => Interlocked.Read(ref _packets);

    public void Start()
    {
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
        {
            ExclusiveAddressUse = false,
            ReceiveTimeout = 200,
        };
        _socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _socket.Bind(new IPEndPoint(IPAddress.Any, port));

        _running = true;
        _thread = new Thread(ReceiveLoop) { IsBackground = true, Name = "IL-2 UDP" };
        _thread.Start();
    }

    private void ReceiveLoop()
    {
        var buffer = new byte[2048];
        while (_running)
        {
            int n;
            try
            {
                n = _socket!.Receive(buffer);
            }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.TimedOut or SocketError.ConnectionReset)
            {
                continue;
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
                return; // socket cerrado en Dispose
            }

            lock (_lock)
            {
                if (!_decoder.Decode(buffer.AsSpan(0, n), _state, _events)) continue;
                _lastPacketMs = _clock.ElapsedMilliseconds;
                _packets++;
                if (_events.Count > MaxPendingEvents) _events.RemoveRange(0, _events.Count - MaxPendingEvents);
            }
        }
    }

    public void Snapshot(FlightState target)
    {
        lock (_lock)
        {
            _state.Connected = _clock.ElapsedMilliseconds - _lastPacketMs < TimeoutMs;
            _state.CopyTo(target);
        }
    }

    public void DrainEvents(List<FlightEvent> into)
    {
        lock (_lock)
        {
            into.AddRange(_events);
            _events.Clear();
        }
    }

    public bool TryTakeNotice(out string notice)
    {
        lock (_lock)
        {
            return _decoder.Notices.TryDequeue(out notice!);
        }
    }

    public void Dispose()
    {
        _running = false;
        _socket?.Dispose();
        _thread?.Join(500);
    }
}

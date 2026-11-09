using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace HapticSeat.Telemetry.Dcs;

/// <summary>
/// Escucha lo que envía dcs/HapticSeatExport.lua. Está en pausa si deja de avanzar el tiempo de la misión
/// (menú de ESC) o de llegar paquetes; un rato sin ninguno cuenta como desconectado.
/// </summary>
public sealed class DcsSource(int port = DcsSource.DefaultPort) : ITelemetrySource
{
    public const int DefaultPort = 5679;

    private const int PausedMs = 500;
    private const int DisconnectedMs = 3000;
    private const int MaxPendingEvents = 512;

    private readonly DcsDecoder _decoder = new();
    private readonly FlightState _state = new() { Source = "DCS" };
    private readonly List<FlightEvent> _events = new(64);
    private readonly Lock _lock = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private Socket? _socket;
    private Thread? _thread;
    private volatile bool _running;
    private long _lastPacketMs = -DisconnectedMs;
    private long _lastAdvanceMs = -PausedMs;
    private double _lastModelTime = double.NaN;
    private long _packets;

    public string Name => $"DCS (UDP {port})";

    public long PacketCount => Interlocked.Read(ref _packets);

    public void Start()
    {
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { ReceiveTimeout = 200 };
        _socket.Bind(new IPEndPoint(IPAddress.Loopback, port));
        _running = true;
        _thread = new Thread(ReceiveLoop) { IsBackground = true, Name = "DCS UDP" };
        _thread.Start();
    }

    private void ReceiveLoop()
    {
        var buffer = new byte[1024];
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
                var now = _clock.ElapsedMilliseconds;
                _lastPacketMs = now;
                if (_decoder.ModelTime != _lastModelTime)
                {
                    _lastModelTime = _decoder.ModelTime;
                    _lastAdvanceMs = now;
                }
                _packets++;
                if (_events.Count > MaxPendingEvents) _events.RemoveRange(0, _events.Count - MaxPendingEvents);
            }
        }
    }

    public void Snapshot(FlightState target)
    {
        lock (_lock)
        {
            var now = _clock.ElapsedMilliseconds;
            _state.Connected = now - _lastPacketMs < DisconnectedMs;
            _state.Paused = now - _lastPacketMs >= PausedMs || now - _lastAdvanceMs >= PausedMs;
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
        notice = "";
        return false;
    }

    public void Dispose()
    {
        _running = false;
        _socket?.Dispose();
        _thread?.Join(500);
    }
}

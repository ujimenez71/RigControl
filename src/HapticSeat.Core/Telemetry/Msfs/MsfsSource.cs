using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace HapticSeat.Telemetry.Msfs;

/// <summary>
/// Telemetría de MSFS 2024 por SimConnect (DLL nativa del SDK, como en RigControl). Reintenta cada 3 s,
/// así que da igual cuál se abra antes. Se considera en pausa si el simulador lo dice o si dejan de
/// llegar datos, porque MSFS no los envía mientras está parado. No tiene eventos: no hay armas.
/// </summary>
public sealed class MsfsSource : ITelemetrySource
{
    private const int RetryMs = 3000;
    private const int PingMs = 5000;
    private const int WaitingFlightPingMs = 1000;
    private const int StaleMs = 500;

    private const uint DefinitionFlight = 1, DefinitionTitle = 2;
    private const uint RequestFlight = 1, RequestTitle = 2, RequestSimState = 3;
    private const uint EventSim = 1, EventPause = 2;

    // Enumeraciones de SimConnect.h
    private const int TypeFloat64 = 4, TypeString256 = 9;
    private const uint PeriodSimFrame = 3, PeriodSecond = 4, FlagChanged = 1;

    // SIMCONNECT_RECV (#pragma pack 1): dwSize, dwVersion, dwID y después cada mensaje
    private const int IdOffset = 8, RequestOffset = 12, EventIdOffset = 16, EventDataOffset = 20, StateIntegerOffset = 16, DataOffset = 40;
    private const int MessageException = 1, MessageQuit = 3, MessageEvent = 4, MessageData = 8, MessageSystemState = 15;

    private readonly FlightState _state = new() { Source = "MSFS" };
    private readonly Lock _lock = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly double[] _sample = new double[MsfsVars.Count];
    private readonly ConcurrentQueue<string> _notices = new();
    private readonly Dictionary<uint, string> _sentNames = [];

    private Thread? _thread;
    private volatile bool _running;
    private IntPtr _handle;
    private bool _connected, _simRunning, _paused;
    private long _lastDataMs = -StaleMs;
    private long _packets;

    public string Name => "MSFS (SimConnect)";

    public long PacketCount => Interlocked.Read(ref _packets);

    public void Start()
    {
        _running = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = "MSFS SimConnect" };
        _thread.Start();
    }

    public void Snapshot(FlightState target)
    {
        lock (_lock)
        {
            _state.Connected = _connected;
            _state.Paused = !_simRunning || _paused || _clock.ElapsedMilliseconds - _lastDataMs >= StaleMs;
            _state.CopyTo(target);
        }
    }

    public void DrainEvents(List<FlightEvent> into) { }

    public bool TryTakeNotice(out string notice) => _notices.TryDequeue(out notice!);

    public void Dispose()
    {
        _running = false;
        _thread?.Join(1000);
    }

    // ------------------------------------------------------ hilo de SimConnect

    private void Loop()
    {
        long nextAttempt = 0, nextPing = 0;
        try
        {
            while (_running)
            {
                var now = _clock.ElapsedMilliseconds;
                if (_handle == IntPtr.Zero && now >= nextAttempt)
                {
                    nextAttempt = now + RetryMs;
                    TryOpen();
                    nextPing = now + WaitingFlightPingMs;
                }

                while (_handle != IntPtr.Zero && SimConnect_GetNextDispatch(_handle, out var data, out var size) >= 0 && data != IntPtr.Zero)
                    Handle(data, (int)size);

                // Si MSFS se cae no avisa con QUIT: solo se nota porque enviar empieza a fallar.
                // De paso, refresca si hay un vuelo en marcha.
                if (_handle != IntPtr.Zero && now >= nextPing)
                {
                    // Nada más conectar, MSFS puede decir que no hay vuelo aunque lo haya: insistir hasta que lo confirme
                    bool running;
                    lock (_lock) running = _simRunning;
                    nextPing = now + (running ? PingMs : WaitingFlightPingMs);
                    if (SimConnect_RequestSystemState(_handle, RequestSimState, "Sim") < 0)
                    {
                        _notices.Enqueue("MSFS: se ha perdido la conexión.");
                        Disconnect();
                    }
                }

                Thread.Sleep(5);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
        {
            _notices.Enqueue($"MSFS: no se puede cargar SimConnect.dll, no habrá telemetría de MSFS ({ex.Message}).");
        }
        finally
        {
            Disconnect();
        }
    }

    private void TryOpen()
    {
        if (SimConnect_Open(out var handle, "HapticSeat", IntPtr.Zero, 0, IntPtr.Zero, 0) < 0) return; // MSFS no está abierto
        _handle = handle;
        _sentNames.Clear();

        foreach (var (name, unit) in MsfsVars.Datums)
        {
            SimConnect_AddToDataDefinition(_handle, DefinitionFlight, name, unit, TypeFloat64, 0, uint.MaxValue);
            RememberPacket(name);
        }
        SimConnect_AddToDataDefinition(_handle, DefinitionTitle, "TITLE", null, TypeString256, 0, uint.MaxValue);
        RememberPacket("TITLE");

        SimConnect_SubscribeToSystemEvent(_handle, EventSim, "Sim");
        SimConnect_SubscribeToSystemEvent(_handle, EventPause, "Pause");
        SimConnect_RequestSystemState(_handle, RequestSimState, "Sim");
        SimConnect_RequestDataOnSimObject(_handle, RequestFlight, DefinitionFlight, 0, PeriodSimFrame, 0, 0, 0, 0);
        SimConnect_RequestDataOnSimObject(_handle, RequestTitle, DefinitionTitle, 0, PeriodSecond, FlagChanged, 0, 0, 0);

        lock (_lock) _connected = true;
        _notices.Enqueue("MSFS: conectado.");
    }

    private void RememberPacket(string name)
    {
        if (SimConnect_GetLastSentPacketID(_handle, out var packet) >= 0) _sentNames[packet] = name;
    }

    private void Disconnect()
    {
        if (_handle != IntPtr.Zero)
        {
            SimConnect_Close(_handle);
            _handle = IntPtr.Zero;
        }
        lock (_lock)
        {
            _connected = _simRunning = _paused = false;
            _state.Aircraft = "";
            _state.ResetAircraft();
        }
    }

    /// <summary>Procesa un mensaje de SimConnect. Interno para probarlo con mensajes construidos a mano.</summary>
    internal void Handle(IntPtr p, int size)
    {
        switch (Marshal.ReadInt32(p, IdOffset))
        {
            case MessageException:
                var sendId = (uint)Marshal.ReadInt32(p, 16);
                _notices.Enqueue($"MSFS: excepción {Marshal.ReadInt32(p, 12)} con {_sentNames.GetValueOrDefault(sendId, $"el paquete {sendId}")}.");
                break;

            case MessageQuit:
                _notices.Enqueue("MSFS: el simulador se ha cerrado.");
                Disconnect();
                break;

            case MessageEvent:
                var eventId = (uint)Marshal.ReadInt32(p, EventIdOffset);
                var on = Marshal.ReadInt32(p, EventDataOffset) != 0;
                lock (_lock)
                {
                    if (eventId == EventSim) _simRunning = on;
                    else if (eventId == EventPause) _paused = on;
                }
                break;

            case MessageSystemState:
                if ((uint)Marshal.ReadInt32(p, RequestOffset) == RequestSimState)
                    lock (_lock) _simRunning = Marshal.ReadInt32(p, StateIntegerOffset) != 0;
                break;

            case MessageData:
                HandleData(p, size);
                break;
        }
    }

    private void HandleData(IntPtr p, int size)
    {
        var request = (uint)Marshal.ReadInt32(p, RequestOffset);
        if (request == RequestTitle)
        {
            var title = Marshal.PtrToStringAnsi(p + DataOffset) ?? "";
            lock (_lock) _state.Aircraft = title;
        }
        else if (request == RequestFlight && size >= DataOffset + MsfsVars.Count * sizeof(double))
        {
            Marshal.Copy(p + DataOffset, _sample, 0, MsfsVars.Count);
            lock (_lock)
            {
                MsfsVars.Apply(_sample, _state);
                _lastDataMs = _clock.ElapsedMilliseconds;
                _packets++;
            }
        }
    }

    // --------------------------------------------------------- P/Invoke

    private const string Dll = "SimConnect.dll";

    [DllImport(Dll)] private static extern int SimConnect_Open(out IntPtr phSimConnect, [MarshalAs(UnmanagedType.LPStr)] string szName, IntPtr hWnd, uint userEventWin32, IntPtr hEventHandle, uint configIndex);
    [DllImport(Dll)] private static extern int SimConnect_Close(IntPtr hSimConnect);
    [DllImport(Dll)] private static extern int SimConnect_GetNextDispatch(IntPtr hSimConnect, out IntPtr ppData, out uint pcbData);
    [DllImport(Dll)] private static extern int SimConnect_GetLastSentPacketID(IntPtr hSimConnect, out uint pdwError);
    [DllImport(Dll)] private static extern int SimConnect_AddToDataDefinition(IntPtr hSimConnect, uint defineId, [MarshalAs(UnmanagedType.LPStr)] string datumName, [MarshalAs(UnmanagedType.LPStr)] string? unitsName, int datumType, float epsilon, uint datumId);
    [DllImport(Dll)] private static extern int SimConnect_RequestDataOnSimObject(IntPtr hSimConnect, uint requestId, uint defineId, uint objectId, uint period, uint flags, uint origin, uint interval, uint limit);
    [DllImport(Dll)] private static extern int SimConnect_SubscribeToSystemEvent(IntPtr hSimConnect, uint eventId, [MarshalAs(UnmanagedType.LPStr)] string systemEventName);
    [DllImport(Dll)] private static extern int SimConnect_RequestSystemState(IntPtr hSimConnect, uint requestId, [MarshalAs(UnmanagedType.LPStr)] string szState);
}

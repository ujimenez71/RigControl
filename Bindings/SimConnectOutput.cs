using System.Runtime.InteropServices;

namespace RigProbe.Bindings;

/// <summary>
/// Salida a MSFS 2024 por SimConnect, llamando directamente a la SimConnect.dll nativa del SDK
/// (la DLL managed es para .NET Framework). Todo ocurre en el hilo del bucle: Tick conecta,
/// reintenta si el simulador no está y procesa los mensajes pendientes; nunca bloquea.
///
///  - K:  se registra con MapClientEventToSimEvent al cargar el perfil y se envía con TransmitClientEvent_EX1.
///  - L:  una definición de datos por variable ("L:Nombre", unidades "Number") y SetDataOnSimObject.
///  - B:  los InputEvents dependen del avión: se enumeran al conectar y al cargar avión, y se
///        envían con SetInputEvent por su hash.
/// Los errores del simulador llegan después, como excepción con el id del paquete: se guardan los
/// últimos envíos para poder decir qué acción falló.
/// </summary>
public sealed class SimConnectOutput : ISimOutput, IDisposable
{
    private const int RetryMs = 3000;

    // Identificadores propios (cualquier valor nuestro sirve; solo tienen que ser únicos).
    private const uint EventAircraftLoaded = 1;
    private const uint EventSimStart = 2;
    private const uint FirstClientEvent = 100;
    private const uint RequestAircraft = 1;
    private const uint RequestInputEvents = 2;

    private IntPtr _handle;
    private bool _available = true;
    private long _nextAttempt;

    private readonly Dictionary<string, uint> _eventIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, uint> _lvarDefs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ulong> _inputEvents = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ulong> _inputEventsLoading = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<uint, string> _registrations = [];

    /// <summary>K: y L: que el simulador rechazó al registrarlos: no se vuelven a enviar.</summary>
    private readonly HashSet<string> _rejected = new(StringComparer.OrdinalIgnoreCase);
    private uint _nextEventId = FirstClientEvent;
    private uint _nextDefinition = 1;
    private CompiledProfile? _profile;

    // Últimos envíos, para atribuir las excepciones (sin asignar memoria al enviar).
    private const int SentRing = 64;
    private readonly uint[] _sentIds = new uint[SentRing];
    private readonly ActionSpec?[] _sentActions = new ActionSpec?[SentRing];
    private int _sentNext;

    public bool IsConnected => _handle != IntPtr.Zero;

    /// <summary>Ruta del avión cargado (aircraft.cfg) tal como la da el simulador, o null.</summary>
    public string? Aircraft { get; private set; }

    /// <summary>Se dispara al conectar y cada vez que cambia el avión (para el cambio automático de perfil).</summary>
    public event Action<string>? AircraftChanged;

    /// <summary>Se dispara cuando termina de llegar la lista de InputEvents (B:) del avión.</summary>
    public event Action<IReadOnlyDictionary<string, ulong>>? InputEventsReady;

    public IReadOnlyDictionary<string, ulong> InputEvents => _inputEvents;

    /// <summary>Registra los K: y L: del perfil (ahora si hay conexión, o al conectar).</summary>
    public void Prepare(CompiledProfile profile)
    {
        _profile = profile;
        if (IsConnected) RegisterProfile();
        CheckInputEvents();
    }

    public void Tick(long timeMs)
    {
        if (!_available) return;

        if (!IsConnected)
        {
            if (timeMs < _nextAttempt) return;
            _nextAttempt = timeMs + RetryMs;
            TryOpen();
            if (!IsConnected) return;
        }

        // GetNextDispatch devuelve S_OK mientras haya mensajes y E_FAIL cuando no quedan.
        while (IsConnected && SimConnect_GetNextDispatch(_handle, out var data, out _) >= 0 && data != IntPtr.Zero)
            Handle(data);
    }

    public bool Send(ActionSpec action)
    {
        if (!IsConnected) return false;

        int hr;
        switch (action.Kind)
        {
            case ActionKind.SimEvent:
                if (!_eventIds.TryGetValue(action.Name, out var id)) return false;
                var data = action.Value < 0 ? unchecked((uint)(int)action.Value) : (uint)action.Value;
                hr = SimConnect_TransmitClientEvent_EX1(_handle, 0 /* avión del usuario */, id,
                    1 /* SIMCONNECT_GROUP_PRIORITY_HIGHEST */, 0x10 /* GROUPID_IS_PRIORITY */, data, 0, 0, 0, 0);
                break;

            case ActionKind.LVar:
                if (!_lvarDefs.TryGetValue(action.Name, out var def)) return false;
                var value = action.Value;
                hr = SimConnect_SetDataOnSimObject(_handle, def, 0, 0, 0, sizeof(double), ref value);
                break;

            case ActionKind.InputEvent:
                if (!_inputEvents.TryGetValue(action.Name, out var hash))
                {
                    Console.WriteLine($"         B:{action.Name} no existe en este avión (ver \"sim eventos\").");
                    return false;
                }
                var v = action.Value;
                hr = SimConnect_SetInputEvent(_handle, hash, sizeof(double), ref v);
                break;

            default:
                return false;
        }

        if (hr < 0)
        {
            Disconnect($"error 0x{hr:X8} al enviar");
            return false;
        }

        if (SimConnect_GetLastSentPacketID(_handle, out var packet) >= 0)
        {
            _sentIds[_sentNext] = packet;
            _sentActions[_sentNext] = action;
            _sentNext = (_sentNext + 1) % SentRing;
        }
        return true;
    }

    public void Dispose() => Disconnect(null);

    // ------------------------------------------------------------ conexión

    private void TryOpen()
    {
        try
        {
            var hr = SimConnect_Open(out var handle, "RigControl", IntPtr.Zero, 0, IntPtr.Zero, 0);
            if (hr < 0) return; // el simulador no está abierto: se reintenta en RetryMs
            _handle = handle;
        }
        catch (DllNotFoundException)
        {
            _available = false;
            Console.WriteLine("SimConnect: no se encuentra SimConnect.dll junto al ejecutable. " +
                              "Instala el SDK de MSFS 2024 y recompila. Las acciones K:/B:/L: no se enviarán.");
            return;
        }

        _eventIds.Clear();
        _lvarDefs.Clear();
        _registrations.Clear();
        _rejected.Clear();
        _inputEvents.Clear();
        _nextEventId = FirstClientEvent;
        _nextDefinition = 1;

        SimConnect_SubscribeToSystemEvent(_handle, EventAircraftLoaded, "AircraftLoaded");
        SimConnect_SubscribeToSystemEvent(_handle, EventSimStart, "SimStart");
        SimConnect_RequestSystemState(_handle, RequestAircraft, "AircraftLoaded");
        if (_profile is not null) RegisterProfile();
    }

    private void Disconnect(string? reason)
    {
        if (_handle == IntPtr.Zero) return;
        SimConnect_Close(_handle);
        _handle = IntPtr.Zero;
        Aircraft = null;
        if (reason is not null) Console.WriteLine($"SimConnect: desconectado ({reason}). Reintentando cada {RetryMs / 1000} s.");
    }

    private void RegisterProfile()
    {
        foreach (var b in _profile!.Bindings)
        {
            var a = b.Output;
            if (_rejected.Contains($"{(a.Kind == ActionKind.SimEvent ? "K" : "L")}:{a.Name}")) continue;

            if (a.Kind == ActionKind.SimEvent && !_eventIds.ContainsKey(a.Name))
            {
                var id = _nextEventId++;
                SimConnect_MapClientEventToSimEvent(_handle, id, a.Name);
                Remember($"K:{a.Name}");
                _eventIds[a.Name] = id;
            }
            else if (a.Kind == ActionKind.LVar && !_lvarDefs.ContainsKey(a.Name))
            {
                var def = _nextDefinition++;
                SimConnect_AddToDataDefinition(_handle, def, $"L:{a.Name}", "Number", 4 /* FLOAT64 */, 0, uint.MaxValue);
                Remember($"L:{a.Name}");
                _lvarDefs[a.Name] = def;
            }
        }
    }

    private void Remember(string what)
    {
        if (SimConnect_GetLastSentPacketID(_handle, out var packet) >= 0) _registrations[packet] = what;
    }

    /// <summary>Avisa de los B: del perfil que no existen en el avión cargado.</summary>
    private void CheckInputEvents()
    {
        if (_profile is null || _inputEvents.Count == 0) return;
        var missing = _profile.Bindings
            .Where(b => b.Output.Kind == ActionKind.InputEvent && !_inputEvents.ContainsKey(b.Output.Name))
            .Select(b => $"B:{b.Output.Name}").Distinct().ToList();
        if (missing.Count > 0)
            Console.WriteLine($"SimConnect: este avión no tiene {string.Join(", ", missing)} (ver \"sim eventos\").");
    }

    // ------------------------------------------------------------ mensajes

    // Desplazamientos de los SIMCONNECT_RECV_* (#pragma pack 1). Cabecera: dwSize, dwVersion, dwID.
    private const int IdOffset = 8;

    private void Handle(IntPtr p)
    {
        switch (Marshal.ReadInt32(p, IdOffset))
        {
            case 1: // EXCEPTION: dwException, dwSendID, dwIndex
                ReportException((uint)Marshal.ReadInt32(p, 12), (uint)Marshal.ReadInt32(p, 16));
                break;

            case 2: // OPEN: szApplicationName[256], versión mayor/menor, build mayor/menor...
                var app = Marshal.PtrToStringAnsi(p + 12) ?? "?";
                Console.WriteLine($"SimConnect: conectado a {app} {Marshal.ReadInt32(p, 268)}.{Marshal.ReadInt32(p, 272)}" +
                                  $" (build {Marshal.ReadInt32(p, 276)}.{Marshal.ReadInt32(p, 280)}).");
                break;

            case 3: // QUIT
                Disconnect("el simulador se ha cerrado");
                break;

            case 4: // EVENT: uGroupID, uEventID, dwData
                if ((uint)Marshal.ReadInt32(p, 16) == EventSimStart && Aircraft is null)
                    SimConnect_RequestSystemState(_handle, RequestAircraft, "AircraftLoaded");
                break;

            case 6: // EVENT_FILENAME: RECV_EVENT + szFileName[260]
                if ((uint)Marshal.ReadInt32(p, 16) == EventAircraftLoaded)
                    SetAircraft(Marshal.PtrToStringAnsi(p + 24));
                break;

            case 15: // SYSTEM_STATE: dwRequestID, dwInteger, fFloat, szString[260]
                if ((uint)Marshal.ReadInt32(p, 12) == RequestAircraft)
                    SetAircraft(Marshal.PtrToStringAnsi(p + 24));
                break;

            case 34: // ENUMERATE_INPUT_EVENTS: LIST_TEMPLATE (dwRequestID, dwArraySize, dwEntryNumber, dwOutOf) + descriptores
                ReadInputEvents(p);
                break;
        }
    }

    /// <summary>
    /// MSFS da la ruta unas veces relativa ("SimObjects\Airplanes\...") y otras absoluta
    /// ("c:\users\...\simobjects\airplanes\..."): se normaliza a partir de "simobjects\" en minúsculas.
    /// </summary>
    public static string NormalizeAircraft(string path)
    {
        var p = path.Replace('/', '\\').ToLowerInvariant();
        var i = p.IndexOf("simobjects\\", StringComparison.Ordinal);
        return i >= 0 ? p[i..] : p;
    }

    private void SetAircraft(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return;
        var path = NormalizeAircraft(raw);
        if (path == Aircraft) return;
        Aircraft = path;
        Console.WriteLine($"SimConnect: avión cargado: {path}");
        _inputEvents.Clear();
        SimConnect_EnumerateInputEvents(_handle, RequestInputEvents);
        AircraftChanged?.Invoke(path);
    }

    /// <summary>SIMCONNECT_INPUT_EVENT_DESCRIPTOR: Name[64], UINT64 Hash, DWORD eType = 76 bytes.</summary>
    private void ReadInputEvents(IntPtr p)
    {
        var count = Marshal.ReadInt32(p, 16);
        var entry = Marshal.ReadInt32(p, 20);
        var outOf = Marshal.ReadInt32(p, 24);
        if (entry == 0) _inputEventsLoading.Clear();

        for (var i = 0; i < count; i++)
        {
            var item = p + 28 + i * 76;
            var name = Marshal.PtrToStringAnsi(item);
            if (!string.IsNullOrEmpty(name)) _inputEventsLoading[name] = (ulong)Marshal.ReadInt64(item, 64);
        }

        if (entry < outOf - 1) return;
        _inputEvents.Clear();
        foreach (var (name, hash) in _inputEventsLoading) _inputEvents[name] = hash;
        Console.WriteLine($"SimConnect: {_inputEvents.Count} InputEvent(s) B: disponibles en este avión.");
        CheckInputEvents();
        InputEventsReady?.Invoke(_inputEvents);
    }

    private void ReportException(uint code, uint sendId)
    {
        var what = _registrations.GetValueOrDefault(sendId);
        if (what is not null)
        {
            // Falló al registrarlo: se quita para que Send no lo envíe, y no se vuelve a registrar.
            _rejected.Add(what);
            if (what.StartsWith("K:")) _eventIds.Remove(what[2..]);
            else if (what.StartsWith("L:")) _lvarDefs.Remove(what[2..]);
        }
        else
            for (var i = 0; i < SentRing; i++)
                if (_sentIds[i] == sendId && _sentActions[i] is { } a)
                    what = $"{a.Kind switch { ActionKind.SimEvent => "K", ActionKind.LVar => "L", _ => "B" }}:{a.Name}";

        var reason = code switch
        {
            1 => "error del simulador",
            7 => "nombre no reconocido (¿está bien escrito?); no se enviará",
            20 => "error en los datos",
            28 => "definición de datos no válida",
            43 => "el avión no aceptó el InputEvent",
            _ => $"excepción {code}",
        };
        Console.WriteLine($"SimConnect: {what ?? $"paquete {sendId}"}: {reason}.");
    }

    // --------------------------------------------------------- P/Invoke

    private const string Dll = "SimConnect.dll";

    [DllImport(Dll)] private static extern int SimConnect_Open(out IntPtr phSimConnect, [MarshalAs(UnmanagedType.LPStr)] string szName, IntPtr hWnd, uint userEventWin32, IntPtr hEventHandle, uint configIndex);
    [DllImport(Dll)] private static extern int SimConnect_Close(IntPtr hSimConnect);
    [DllImport(Dll)] private static extern int SimConnect_GetNextDispatch(IntPtr hSimConnect, out IntPtr ppData, out uint pcbData);
    [DllImport(Dll)] private static extern int SimConnect_GetLastSentPacketID(IntPtr hSimConnect, out uint pdwError);
    [DllImport(Dll)] private static extern int SimConnect_MapClientEventToSimEvent(IntPtr hSimConnect, uint eventId, [MarshalAs(UnmanagedType.LPStr)] string eventName);
    [DllImport(Dll)] private static extern int SimConnect_TransmitClientEvent_EX1(IntPtr hSimConnect, uint objectId, uint eventId, uint groupId, uint flags, uint data0, uint data1, uint data2, uint data3, uint data4);
    [DllImport(Dll)] private static extern int SimConnect_AddToDataDefinition(IntPtr hSimConnect, uint defineId, [MarshalAs(UnmanagedType.LPStr)] string datumName, [MarshalAs(UnmanagedType.LPStr)] string unitsName, int datumType, float epsilon, uint datumId);
    [DllImport(Dll)] private static extern int SimConnect_SetDataOnSimObject(IntPtr hSimConnect, uint defineId, uint objectId, uint flags, uint arrayCount, uint cbUnitSize, ref double pDataSet);
    [DllImport(Dll)] private static extern int SimConnect_SubscribeToSystemEvent(IntPtr hSimConnect, uint eventId, [MarshalAs(UnmanagedType.LPStr)] string systemEventName);
    [DllImport(Dll)] private static extern int SimConnect_RequestSystemState(IntPtr hSimConnect, uint requestId, [MarshalAs(UnmanagedType.LPStr)] string szState);
    [DllImport(Dll)] private static extern int SimConnect_EnumerateInputEvents(IntPtr hSimConnect, uint requestId);
    [DllImport(Dll)] private static extern int SimConnect_SetInputEvent(IntPtr hSimConnect, ulong hash, uint cbUnitSize, ref double value);
}

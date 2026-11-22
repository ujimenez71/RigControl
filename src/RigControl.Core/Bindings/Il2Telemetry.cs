using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace RigControl.Core.Bindings;

/// <summary>Qué da cada variable "I:" de IL-2.</summary>
public enum Il2VarKind : byte { Rpm, Manifold, Gear, OnGround, Ias, IasKt, IasMph, Agl, AglFt, Flaps, Airbrake, Engines, Paused }

/// <summary>Variable "I:NOMBRE[:n]" ya interpretada (Index base 0; -1 = sin índice).</summary>
public readonly record struct Il2Var(Il2VarKind Kind, int Index);

/// <summary>
/// Telemetría de IL-2 Great Battles (bloque telemetrydevice de data\startup.cfg: UDP 5678 en broadcast a
/// 127.255.255.255). Solo lectura: IL-2 no acepta órdenes de fuera, las acciones en IL-2 son teclas.
/// Da el avión (evento al empezar la misión) y unos pocos indicadores, como variables "I:" para luces,
/// condiciones "state" y la pantalla del ICP. Formato sin documentación oficial (IDs de la ingeniería inversa
/// de la comunidad, IL2Shaker; el mismo que decodifica HapticSeat). Todo en el hilo del bucle: Tick lee lo
/// que haya sin bloquear. El socket se abre con ReuseAddress: convive con HapticSeat, SimHub, etc.
///
///   Estado 0x54000101: id u32, tamaño u16, tick u32, nIndicadores u8, [id u16, nFloats u8, float × n] ×,
///                      nEventos u8, [tipo u16, nBytes u8, bytes] ×.  Sin indicadores = pausa o menús.
///   Indicadores: 0 RPM por motor, 1 presión de admisión por motor, 4 tren por rueda (0 arriba .. 1 abajo),
///                5 carga por rueda, 6 IAS (m/s), 10 altura sobre el terreno (m), 11 flaps, 12 aerofreno (0..1).
///   Evento 0 = nombre del avión.
/// </summary>
public sealed class Il2Telemetry : IDisposable
{
    public const int DefaultPort = 5678;
    public const uint StateId = 0x54000101;
    public const int ActiveMs = 2000;
    /// <summary>Sin datos durante más de esto se descarta todo (como DCS-BIOS).</summary>
    public const int StaleMs = 30_000;
    private const int RetryMs = 5000;
    private const int Max = 8;

    /// <summary>Variables "I:" conocidas: nombre, si admite índice ":n" y qué es (para errores, editor e IA).</summary>
    public static readonly (string Name, Il2VarKind Kind, bool Indexed, string Help)[] Vars =
    [
        ("GEAR", Il2VarKind.Gear, true, "tren: 0 recogido .. 1 abajo (sin índice, la rueda menos bajada; GEAR:1, :2... cada rueda)"),
        ("ON_GROUND", Il2VarKind.OnGround, false, "1 con peso sobre alguna rueda"),
        ("FLAPS", Il2VarKind.Flaps, false, "flaps, 0..1"),
        ("AIRBRAKE", Il2VarKind.Airbrake, false, "aerofreno, 0..1"),
        ("RPM", Il2VarKind.Rpm, true, "RPM del motor (sin índice, el 1; RPM:2 el segundo)"),
        ("MANIFOLD", Il2VarKind.Manifold, true, "presión de admisión del motor (valor tal cual lo da IL-2, sin verificar las unidades)"),
        ("IAS", Il2VarKind.Ias, false, "velocidad indicada, km/h"),
        ("IAS_KT", Il2VarKind.IasKt, false, "velocidad indicada, nudos"),
        ("IAS_MPH", Il2VarKind.IasMph, false, "velocidad indicada, mph"),
        ("AGL", Il2VarKind.Agl, false, "altura sobre el terreno, m"),
        ("AGL_FT", Il2VarKind.AglFt, false, "altura sobre el terreno, pies"),
        ("ENGINES", Il2VarKind.Engines, false, "número de motores"),
        ("PAUSED", Il2VarKind.Paused, false, "1 en pausa o en los menús"),
    ];

    /// <summary>"I:GEAR:2" -> (Gear, 1). Sin "I:" delante también vale.</summary>
    public static bool TryParseVar(string text, out Il2Var v, out string? error)
    {
        v = default;
        error = null;
        var t = text.Trim();
        if (t.StartsWith("I:", StringComparison.OrdinalIgnoreCase)) t = t[2..];
        var colon = t.IndexOf(':');
        var name = (colon < 0 ? t : t[..colon]).Trim().ToUpperInvariant();
        var known = Array.FindIndex(Vars, x => x.Name == name);
        if (known < 0)
        {
            error = $"\"I:{t}\": variable de IL-2 desconocida; valen {string.Join(", ", Vars.Select(x => x.Name))}.";
            return false;
        }
        var index = -1;
        if (colon >= 0)
        {
            if (!Vars[known].Indexed)
            {
                error = $"\"I:{t}\": {name} no lleva número (solo {string.Join(", ", Vars.Where(x => x.Indexed).Select(x => x.Name))}).";
                return false;
            }
            if (!int.TryParse(t[(colon + 1)..], out var n) || n < 1 || n > Max)
            {
                error = $"\"I:{t}\": el número va de 1 a {Max}.";
                return false;
            }
            index = n - 1;
        }
        v = new Il2Var(Vars[known].Kind, index);
        return true;
    }

    private readonly int _port;
    private readonly byte[] _packet = new byte[4096];
    private Socket? _socket;
    private long _now;
    private long _nextOpen;
    private long _lastPacket = long.MinValue / 2;
    private bool _lost;
    private bool _announcedNoName;

    // Estado (NaN = aún no ha llegado).
    private readonly double[] _rpm = new double[Max];
    private readonly double[] _manifold = new double[Max];
    private readonly double[] _gear = new double[Max];
    private readonly double[] _load = new double[Max];
    private int _engines, _wheels;
    private double _ias = double.NaN, _agl = double.NaN, _flaps = double.NaN, _airbrake = double.NaN;
    private bool _paused;

    private CompiledProfile? _profile;
    private Il2Var?[] _watched = [];

    /// <param name="port">Configurable para las pruebas.</param>
    public Il2Telemetry(int port = DefaultPort)
    {
        _port = port;
        ResetState();
    }

    /// <summary>Llegan datos de IL-2 (hubo un paquete hace menos de 2 s).</summary>
    public bool IsActive => _now - _lastPacket < ActiveMs;

    /// <summary>Hay un estado utilizable: datos de hace menos de StaleMs.</summary>
    public bool HasState => _now - _lastPacket < StaleMs;

    /// <summary>En pausa o en los menús (llegan paquetes sin indicadores).</summary>
    public bool Paused => _paused;

    /// <summary>Nombre del avión tal como lo da IL-2 (p. ej. "Bf 109 G-6"), o null.</summary>
    public string? Aircraft { get; private set; }

    /// <summary>
    /// "il2:" + nombre en minúsculas al empezar una misión o cambiar de avión; "il2:" a secas si llegan datos sin
    /// nombre (RigControl arrancó con la misión ya empezada: IL-2 solo lo manda al empezar).
    /// </summary>
    public event Action<string>? AircraftChanged;

    /// <summary>Prepara las variables I: del perfil (luces, condiciones e ICP).</summary>
    public void Prepare(CompiledProfile profile)
    {
        _profile = profile;
        var vars = profile.WatchedVars;
        _watched = new Il2Var?[vars.Length];
        for (var i = 0; i < vars.Length; i++)
            if (vars[i].IsIl2 && TryParseVar(vars[i].Name, out var v, out _)) _watched[i] = v;
    }

    /// <summary>Pone el valor de cada variable I: del perfil (NaN sin datos o pasado StaleMs).</summary>
    public void Fill(double[] values)
    {
        var n = Math.Min(values.Length, _watched.Length);
        for (var i = 0; i < n; i++)
            if (_watched[i] is { } v) values[i] = HasState ? Read(v) : double.NaN;
    }

    /// <summary>Valor actual de una variable (NaN si aún no ha llegado).</summary>
    public double Read(Il2Var v)
    {
        double At(double[] a, int count) => v.Index < count ? a[Math.Max(v.Index, 0)] : double.NaN;
        return v.Kind switch
        {
            Il2VarKind.Rpm => _engines == 0 ? double.NaN : At(_rpm, _engines),
            Il2VarKind.Manifold => _engines == 0 ? double.NaN : At(_manifold, _engines),
            Il2VarKind.Gear when v.Index < 0 => MinGear(),
            Il2VarKind.Gear => At(_gear, _wheels),
            Il2VarKind.OnGround => OnGround(),
            Il2VarKind.Ias => _ias * 3.6,
            Il2VarKind.IasKt => _ias * 3.6 / 1.852,
            Il2VarKind.IasMph => _ias * 3.6 / 1.609344,
            Il2VarKind.Agl => _agl,
            Il2VarKind.AglFt => _agl / 0.3048,
            Il2VarKind.Flaps => _flaps,
            Il2VarKind.Airbrake => _airbrake,
            Il2VarKind.Engines => _engines == 0 ? double.NaN : _engines,
            Il2VarKind.Paused => _paused ? 1 : 0,
            _ => double.NaN,
        };
    }

    // Sin asignar memoria: se llaman en cada vuelta del bucle.
    private double MinGear()
    {
        if (_wheels == 0) return double.NaN;
        var min = double.MaxValue;
        for (var j = 0; j < _wheels; j++) min = Math.Min(min, _gear[j]);
        return min;
    }

    private double OnGround()
    {
        if (_wheels == 0 || double.IsNaN(_load[0])) return double.NaN;
        for (var j = 0; j < _wheels; j++) if (_load[j] > 0) return 1;
        return 0;
    }

    public void Tick(long timeMs)
    {
        _now = timeMs;
        if (_socket is null)
        {
            if (timeMs < _nextOpen) return;
            _nextOpen = timeMs + RetryMs;
            if (!TryOpen()) return;
        }

        try
        {
            while (_socket!.Available > 0)
            {
                var n = _socket.Receive(_packet);
                if (n <= 0) break;
                if (!Decode(_packet.AsSpan(0, n))) continue;
                if (_lost)
                {
                    _lost = false;
                    Log.Info($"IL-2: vuelven los datos tras {(timeMs - _lastPacket) / 1000.0:0.0} s.");
                }
                _lastPacket = timeMs;
            }
        }
        catch (SocketException ex)
        {
            Log.Warning($"IL-2: error al recibir ({ex.Message}); se reabre.");
            Close();
        }

        if (HasState && Aircraft is null && !_announcedNoName)
        {
            _announcedNoName = true;
            Log.Info("IL-2: llegan datos, pero sin el nombre del avión (IL-2 lo manda al empezar la misión).");
            AircraftChanged?.Invoke("il2:");
        }
        if (!IsActive && !_lost && _lastPacket > long.MinValue / 2)
        {
            _lost = true;
            Log.Info($"IL-2: sin datos (¿cerrado o fuera de misión?); se mantiene el último estado {StaleMs / 1000} s.");
        }
        if (!HasState && (Aircraft is not null || _announcedNoName))
        {
            Aircraft = null;
            _announcedNoName = false;
            ResetState();
            Log.Info($"IL-2: {StaleMs / 1000} s sin datos: se descarta el estado.");
        }
    }

    public void Dispose() => Close();

    // ------------------------------------------------------------ red

    private bool TryOpen()
    {
        try
        {
            var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { ExclusiveAddressUse = false, Blocking = false };
            s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            s.Bind(new IPEndPoint(IPAddress.Any, _port));
            _socket = s;
            return true;
        }
        catch (SocketException ex)
        {
            Log.Warning($"IL-2: no se puede escuchar en el puerto UDP {_port} ({ex.Message}); reintento en {RetryMs / 1000} s.");
            return false;
        }
    }

    private void Close()
    {
        _socket?.Dispose();
        _socket = null;
    }

    // ------------------------------------------------------------ paquetes

    private void ResetState()
    {
        Array.Fill(_rpm, double.NaN);
        Array.Fill(_manifold, double.NaN);
        Array.Fill(_gear, double.NaN);
        Array.Fill(_load, double.NaN);
        _engines = _wheels = 0;
        _ias = _agl = _flaps = _airbrake = double.NaN;
        _paused = false;
    }

    /// <summary>Devuelve false si no es un paquete de estado de IL-2 (los de movimiento se ignoran) o está truncado.</summary>
    private bool Decode(ReadOnlySpan<byte> p)
    {
        if (p.Length < 11 || BinaryPrimitives.ReadUInt32LittleEndian(p) != StateId) return false;
        int indicators = p[10];
        var o = 11;
        _paused = indicators == 0;

        for (var i = 0; i < indicators; i++)
        {
            if (o + 3 > p.Length) return false;
            int id = BinaryPrimitives.ReadUInt16LittleEndian(p[o..]);
            int n = p[o + 2];
            o += 3;
            if (o + n * 4 > p.Length) return false;
            Indicator(id, p.Slice(o, n * 4), n);
            o += n * 4;
        }

        if (o >= p.Length) return true;
        int events = p[o++];
        for (var i = 0; i < events; i++)
        {
            if (o + 3 > p.Length) return false;
            int type = BinaryPrimitives.ReadUInt16LittleEndian(p[o..]);
            int n = p[o + 2];
            o += 3;
            if (o + n > p.Length) return false;
            if (type == 0 && n >= 1) AircraftName(p.Slice(o, n));
            o += n;
        }
        return true;
    }

    private void Indicator(int id, ReadOnlySpan<byte> v, int n)
    {
        static double F(ReadOnlySpan<byte> s, int i) => BinaryPrimitives.ReadSingleLittleEndian(s[(i * 4)..]);
        switch (id)
        {
            case 0: _engines = Math.Min(n, Max); for (var j = 0; j < _engines; j++) _rpm[j] = F(v, j); break;
            case 1: for (var j = 0; j < Math.Min(n, Max); j++) _manifold[j] = F(v, j); break;
            case 4: _wheels = Math.Min(n, Max); for (var j = 0; j < _wheels; j++) _gear[j] = F(v, j); break;
            case 5: for (var j = 0; j < Math.Min(n, Max); j++) _load[j] = F(v, j); break;
            case 6 when n >= 1: _ias = F(v, 0); break;
            case 10 when n >= 1: _agl = F(v, 0); break;
            case 11 when n >= 1: _flaps = F(v, 0); break;
            case 12 when n >= 1: _airbrake = F(v, 0); break;
        }
    }

    private void AircraftName(ReadOnlySpan<byte> e)
    {
        var len = Math.Min(e[0], e.Length - 1);
        var name = Encoding.ASCII.GetString(e.Slice(1, len)).TrimEnd('\0').Trim();
        if (name.Length == 0 || name == Aircraft) return;
        Aircraft = name;
        ResetState(); // otro avión: lo de antes no vale
        Log.Info($"IL-2: avión: {name}");
        AircraftChanged?.Invoke("il2:" + name.ToLowerInvariant());
    }
}

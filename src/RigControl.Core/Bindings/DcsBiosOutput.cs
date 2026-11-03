using System.Net;
using System.Net.Sockets;
using System.Text;

namespace RigControl.Core.Bindings;

/// <summary>
/// Conexión con DCS a través de DCS-BIOS (https://github.com/DCS-Skunkworks/dcs-bios). No toca DCS:
/// solo escucha lo que DCS-BIOS emite y le manda órdenes de texto, igual que cualquier panel.
///
///  - Estado: DCS-BIOS emite por UDP multicast 239.255.50.10:5010 un flujo binario. Cada paquete:
///    [55 55 55 55 = inicio de fotograma] y bloques [dirección u16][longitud u16][datos]. Se mantiene
///    una imagen de 64 KB de toda la cabina. El nombre del avión está en 0x0000 (24 caracteres).
///  - Órdenes: texto "CONTROL VALOR\n" por UDP a 127.0.0.1:7778 (p. ej. "UFC_1 1", "HDG_KNOB INC").
///
/// Todo en el hilo del bucle: Tick lee lo que haya en el socket sin bloquear. Sin asignaciones en
/// Tick/Send salvo cuando cambia el avión.
/// </summary>
public sealed class DcsBiosOutput : IDisposable
{
    public static readonly IPAddress MulticastGroup = IPAddress.Parse("239.255.50.10");
    public const int ExportPort = 5010;
    public const int CommandPort = 7778;
    private const int AircraftNameLength = 24;
    private const int ActiveMs = 2000;
    private const int TapMs = 50;
    private const int RetryMs = 5000;

    private readonly IPEndPoint _commandEndpoint;
    private readonly byte[] _image = new byte[65536];
    private readonly byte[] _packet = new byte[65536];
    private readonly byte[] _lastName = new byte[AircraftNameLength];
    private Socket? _listen;
    private Socket? _send;
    private long _lastPacket = long.MinValue / 2;
    private long _now;
    private long _nextOpen;
    private bool _warned;

    private const int MaxPending = 32;
    private readonly byte[]?[] _pendingRelease = new byte[MaxPending][];
    private readonly long[] _pendingAt = new long[MaxPending];

    /// <param name="exportPort">Puertos configurables solo para pruebas.</param>
    /// <param name="catalog">Controles de un avión (por defecto, DcsBiosCatalog); configurable para pruebas.</param>
    public DcsBiosOutput(int exportPort = ExportPort, int commandPort = CommandPort,
                         Func<string, IReadOnlyList<DcsControl>>? catalog = null)
    {
        _exportPort = exportPort;
        _commandEndpoint = new IPEndPoint(IPAddress.Loopback, commandPort);
        _catalog = catalog ?? DcsBiosCatalog.ForAircraft;
    }

    // ------------------------------------------------------------ variables vigiladas (D:)

    private readonly Func<string, IReadOnlyList<DcsControl>> _catalog;
    private CompiledProfile? _profile;
    /// <summary>Por variable vigilada: control de DCS-BIOS con salida entera, o null (no es D: o no se encontró).</summary>
    private DcsControl?[] _watched = [];
    private string? _resolvedFor;

    /// <summary>
    /// Prepara las variables D: del perfil (luces y condiciones): busca cada control en las
    /// definiciones de DCS-BIOS del avión cargado o, si aún no hay, del primero "dcs:" del perfil.
    /// Se vuelve a hacer al cambiar de avión.
    /// </summary>
    public void Prepare(CompiledProfile profile)
    {
        _profile = profile;
        _resolvedFor = null;
        _watched = new DcsControl?[profile.WatchedVars.Length];
        ResolveWatched();
    }

    private void ResolveWatched()
    {
        if (_profile is null || !_profile.WatchedVars.Any(v => v.IsDcs)) return;
        var aircraft = Aircraft ?? _profile.Aircraft.FirstOrDefault(a => a.StartsWith("dcs:", StringComparison.Ordinal));
        if (aircraft is null || aircraft == _resolvedFor) return;
        _resolvedFor = aircraft;

        var controls = _catalog(aircraft);
        var missing = new List<string>();
        var vars = _profile.WatchedVars;
        for (var i = 0; i < vars.Length; i++)
        {
            if (!vars[i].IsDcs) continue;
            var name = vars[i].Name[2..];
            var c = controls.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            _watched[i] = c is { Address: not null } ? c : null;
            if (_watched[i] is null) missing.Add(c is null ? vars[i].Name : $"{vars[i].Name} (sin valor numérico)");
        }
        if (controls.Count == 0)
            Log.Warning($"DCS-BIOS: no hay definiciones de controles para {aircraft}; las condiciones D: no tendrán valor.");
        else if (missing.Count > 0)
            Log.Warning($"DCS-BIOS: {aircraft} no tiene {string.Join(", ", missing)}; esas condiciones no se cumplirán.");
    }

    /// <summary>
    /// Escribe en <paramref name="values"/> (índices de WatchedVars) el valor actual de cada variable D:.
    /// Sin datos de DCS-BIOS, NaN. No toca las demás. Sin asignaciones.
    /// </summary>
    public void Fill(double[] values)
    {
        var vars = _profile?.WatchedVars;
        if (vars is null) return;
        var n = Math.Min(values.Length, _watched.Length);
        for (var i = 0; i < n; i++)
        {
            if (!vars[i].IsDcs) continue;
            values[i] = IsActive && _watched[i] is { } c && c.Read(_image) is { } v ? v : double.NaN;
        }
    }

    private readonly int _exportPort;

    /// <summary>Llegan datos de DCS-BIOS (hubo un paquete hace menos de 2 s).</summary>
    public bool IsActive => _now - _lastPacket < ActiveMs;

    /// <summary>Nombre del avión según DCS-BIOS (p. ej. "F-15ESE"), o null.</summary>
    public string? Aircraft { get; private set; }

    /// <summary>Al cambiar de avión: "dcs:" + nombre en minúsculas (para el cambio automático de perfil).</summary>
    public event Action<string>? AircraftChanged;

    /// <summary>Imagen de la cabina (direcciones de DCS-BIOS), para leer estados.</summary>
    public ReadOnlySpan<byte> Image => _image;

    public void Tick(long timeMs)
    {
        _now = timeMs;
        if (_listen is null && !TryOpen(timeMs)) return;

        // Leer todo lo que haya, sin bloquear.
        try
        {
            while (_listen!.Available > 0)
            {
                var n = _listen.Receive(_packet);
                if (n <= 0) break;
                Apply(_packet.AsSpan(0, n));
                _lastPacket = timeMs;
            }
        }
        catch (SocketException ex)
        {
            Log.Warning($"DCS-BIOS: error al recibir ({ex.Message}); se reabre.");
            CloseSockets();
        }

        SettleAircraft();

        if (!IsActive && Aircraft is not null)
        {
            Aircraft = null;
            _candidate = null;
            _candidateAt = -1;
            Array.Clear(_lastName);
            Log.Info("DCS-BIOS: sin datos (¿DCS cerrado o fuera de misión?).");
        }

        for (var i = 0; i < MaxPending; i++)
        {
            var msg = _pendingRelease[i];
            if (msg is null || timeMs < _pendingAt[i]) continue;
            _pendingRelease[i] = null;
            SendRaw(msg);
        }
    }

    /// <summary>Envía una acción D: (Trigger o INICIO). Pulsador sin valor: 1 ahora y 0 a los 50 ms.</summary>
    public bool Send(ActionSpec action, bool tap)
    {
        if (action.DcsMessage is null) return false;
        if (!SendRaw(action.DcsMessage)) return false;
        if (tap && action.DcsRelease is { } release) ScheduleRelease(release);
        return true;
    }

    /// <summary>FIN de un hold con pulsador: suelta (envía 0).</summary>
    public bool Release(ActionSpec action) => action.DcsRelease is { } r && SendRaw(r);

    public void Dispose() => CloseSockets();

    // ------------------------------------------------------------ protocolo

    /// <summary>Aplica un paquete del flujo de exportación a la imagen de la cabina.</summary>
    public void Apply(ReadOnlySpan<byte> p)
    {
        var i = 0;
        var nameTouched = false;
        while (i + 4 <= p.Length)
        {
            // Inicio de fotograma: 55 55 55 55 (puede aparecer al principio de un paquete).
            if (p[i] == 0x55 && p[i + 1] == 0x55 && p[i + 2] == 0x55 && p[i + 3] == 0x55)
            {
                i += 4;
                continue;
            }

            var address = p[i] | p[i + 1] << 8;
            var length = p[i + 2] | p[i + 3] << 8;
            i += 4;
            if (length == 0 || i + length > p.Length || address + length > _image.Length) break; // paquete raro: se descarta el resto
            p.Slice(i, length).CopyTo(_image.AsSpan(address));
            i += length;

            if (address < AircraftNameLength) nameTouched = true;
        }
        // Una vez por paquete (el nombre puede llegar en varios trozos dentro del mismo paquete).
        if (nameTouched) CheckAircraft();
    }

    private void CheckAircraft()
    {
        var name = _image.AsSpan(0, AircraftNameLength);
        if (name.SequenceEqual(_lastName)) return;
        name.CopyTo(_lastName);

        // Se compara el texto, no los bytes: el relleno tras el nombre cambia sin que cambie el avión.
        // El nombre puede llegar a trozos en paquetes distintos ("AH" y luego "AH-64D_BLK_II"):
        // solo se da por bueno cuando lleva NameStableMs sin cambiar (ver Tick).
        var end = name.IndexOf((byte)0);
        var text = Encoding.ASCII.GetString(end < 0 ? name : name[..end]).Trim();
        _candidate = text.Length == 0 ? null : text;
        _candidateAt = _now;
    }

    private const int NameStableMs = 300;
    private string? _candidate;
    private long _candidateAt = -1;

    /// <summary>Anuncia el avión cuando el nombre lleva NameStableMs sin cambiar.</summary>
    private void SettleAircraft()
    {
        if (_candidateAt < 0 || _now - _candidateAt < NameStableMs) return;
        _candidateAt = -1;
        if (_candidate == Aircraft) return;
        Aircraft = _candidate;
        if (Aircraft is null) return;

        Log.Info($"DCS-BIOS: avión cargado: {Aircraft}");
        ResolveWatched();
        AircraftChanged?.Invoke("dcs:" + Aircraft.ToLowerInvariant());
    }

    // ------------------------------------------------------------ sockets

    private bool TryOpen(long timeMs)
    {
        if (timeMs < _nextOpen) return false;
        _nextOpen = timeMs + RetryMs;
        try
        {
            // Compartido: otros programas (paneles, SimAppPro...) pueden escuchar el mismo grupo.
            var listen = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            listen.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            listen.Bind(new IPEndPoint(IPAddress.Any, _exportPort));
            listen.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership,
                new MulticastOption(MulticastGroup, IPAddress.Any));
            listen.Blocking = false;
            _listen = listen;
            _send = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            _warned = false;
            return true;
        }
        catch (SocketException ex)
        {
            if (!_warned) Log.Warning($"DCS-BIOS: no se puede escuchar en {MulticastGroup}:{_exportPort} ({ex.Message}); se reintenta.");
            _warned = true;
            CloseSockets();
            return false;
        }
    }

    private bool SendRaw(byte[] message)
    {
        try
        {
            // El envío no depende de la escucha: si aún no se ha abierto (o falló), se abre aquí.
            _send ??= new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            _send.SendTo(message, _commandEndpoint);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private void ScheduleRelease(byte[] message)
    {
        for (var i = 0; i < MaxPending; i++)
        {
            if (_pendingRelease[i] is not null) continue;
            _pendingRelease[i] = message;
            _pendingAt[i] = _now + TapMs;
            return;
        }
        SendRaw(message); // cola llena: mejor un toque corto que un botón enganchado
    }

    private void CloseSockets()
    {
        // Soltar lo que estuviera pendiente de soltar antes de cerrar.
        for (var i = 0; i < MaxPending; i++)
        {
            if (_pendingRelease[i] is { } msg) SendRaw(msg);
            _pendingRelease[i] = null;
        }
        _listen?.Dispose();
        _send?.Dispose();
        _listen = null;
        _send = null;
    }
}

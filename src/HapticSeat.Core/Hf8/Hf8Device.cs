using System.Diagnostics;
using System.IO.Ports;
using System.Text;
using Microsoft.Win32;

namespace HapticSeat.Hf8;

/// <summary>Respuesta a "P": escala del dial rojo (0..255) y si está en modo Audio.</summary>
public readonly record struct Hf8Status(int IntensityScale, bool AudioMode);

/// <summary>El puerto existe pero lo tiene otro programa (normalmente HFS).</summary>
public sealed class Hf8BusyException(string message) : IOException(message);

public enum Hf8OpenError
{
    None,

    /// <summary>No hay ningún HF8 enchufado.</summary>
    NotFound,

    /// <summary>Otro programa tiene el puerto (normalmente HFS).</summary>
    Busy,

    Other,
}

/// <summary>
/// Driver del HF8 en modo PC. Protocolo serie ASCII (115200 8N1.5, fin de línea \r):
///   P\r                      -> E&lt;0..255&gt;,A&lt;0|1&gt;\r
///   M&lt;motor&gt;&lt;0..255&gt;\r   -> fija la intensidad de un motor, p. ej. M3128\r
/// Un hilo propio envía a ~100 Hz solo los motores que cambian. Seguridad: si nadie llama a
/// <see cref="SetMotors"/> en <see cref="WatchdogMs"/>, apaga todo; Dispose siempre deja todo a 0.
/// HFS abre el puerto en exclusiva: tiene que estar cerrado.
/// </summary>
public sealed class Hf8Device : IDisposable
{
    private const string UsbId = @"VID_04D8&PID_EE96";
    private const int FrameMs = 10;

    /// <summary>Sin órdenes nuevas durante este tiempo, los motores se apagan.</summary>
    public const int WatchdogMs = 250;

    /// <summary>Reenvía el estado completo cada cierto tiempo por si se perdió algún comando.</summary>
    private const int RefreshMs = 500;

    private readonly SerialPort _port;
    private readonly Lock _lock = new();
    private readonly byte[] _target = new byte[Hf8Motors.Count];
    private readonly byte[] _sent = new byte[Hf8Motors.Count];
    private readonly byte[] _command = new byte[8];
    private readonly Thread _writer;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _lastUpdateMs;
    private volatile bool _running = true;
    private volatile bool _faulted;

    public string PortName => _port.PortName;

    /// <summary>Ha fallado la escritura (cojín desenchufado): hay que cerrarlo y volver a abrir.</summary>
    public bool Faulted => _faulted;

    private Hf8Device(SerialPort port)
    {
        _port = port;
        _writer = new Thread(WriterLoop) { IsBackground = true, Name = "HF8 salida", Priority = ThreadPriority.AboveNormal };
        _writer.Start();
    }

    /// <summary>
    /// Busca el COM del HF8 en el registro (Enum\USB\VID_04D8&amp;PID_EE96) y lo cruza con los
    /// puertos presentes, porque el registro también guarda dispositivos desconectados.
    /// </summary>
    public static string? FindPort()
    {
        var present = SerialPort.GetPortNames();
        using var usb = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\USB\{UsbId}");
        if (usb is null) return null;

        foreach (var instance in usb.GetSubKeyNames())
        {
            using var parameters = usb.OpenSubKey($@"{instance}\Device Parameters");
            if (parameters?.GetValue("PortName") is string name && present.Contains(name, StringComparer.OrdinalIgnoreCase))
                return name;
        }
        return null;
    }

    /// <summary>Abre el HF8. Lanza si no está conectado o si el puerto está ocupado (HFS abierto).</summary>
    public static Hf8Device Open(string? portName = null)
    {
        portName ??= FindPort() ?? throw new IOException("HF8 no detectado por USB (VID_04D8 PID_EE96).");

        var port = new SerialPort(portName, 115200, Parity.None, 8, StopBits.OnePointFive)
        {
            Handshake = Handshake.None,
            DtrEnable = true,
            NewLine = "\r",
            ReadTimeout = 500,
            WriteTimeout = 50,
            Encoding = Encoding.ASCII,
        };

        try
        {
            port.Open();
        }
        catch (UnauthorizedAccessException)
        {
            port.Dispose();
            throw new Hf8BusyException($"{portName} está ocupado. ¿Está HFS abierto? Ciérralo y reintenta.");
        }

        Thread.Sleep(200); // el firmware necesita un momento tras abrir
        port.DiscardInBuffer();
        return new Hf8Device(port);
    }

    /// <summary>Como <see cref="Open"/>, pero sin excepciones: para reintentar en bucle desde la interfaz.</summary>
    public static Hf8Device? TryOpen(out Hf8OpenError error)
    {
        var portName = FindPort();
        if (portName is null)
        {
            error = Hf8OpenError.NotFound;
            return null;
        }

        try
        {
            error = Hf8OpenError.None;
            return Open(portName);
        }
        catch (Hf8BusyException)
        {
            error = Hf8OpenError.Busy;
            return null;
        }
        catch (IOException)
        {
            error = Hf8OpenError.Other;
            return null;
        }
    }

    /// <summary>Envía "P" y lee la respuesta "E255,A0".</summary>
    public Hf8Status QueryStatus()
    {
        lock (_lock)
        {
            _port.DiscardInBuffer();
            _port.Write("P\r");
            var line = _port.ReadLine().Trim();
            var parts = line.Split(',');
            if (parts.Length != 2 || !parts[0].StartsWith('E') || !parts[1].StartsWith('A')
                || !int.TryParse(parts[0].AsSpan(1), out var scale) || !int.TryParse(parts[1].AsSpan(1), out var audio))
                throw new InvalidDataException($"Respuesta inesperada del HF8: \"{line}\"");
            return new Hf8Status(scale, audio != 0);
        }
    }

    /// <summary>Fija la intensidad (0..255) de los 8 motores. Hay que llamarlo de forma continua (watchdog).</summary>
    public void SetMotors(ReadOnlySpan<byte> levels)
    {
        if (levels.Length != Hf8Motors.Count)
            throw new ArgumentException($"Se esperan {Hf8Motors.Count} niveles.", nameof(levels));

        lock (_lock)
        {
            levels.CopyTo(_target);
            _lastUpdateMs = _clock.ElapsedMilliseconds;
        }
    }

    public void AllOff() => SetMotors(stackalloc byte[Hf8Motors.Count]);

    private void WriterLoop()
    {
        long nextRefresh = 0;
        while (_running)
        {
            var now = _clock.ElapsedMilliseconds;
            try
            {
                lock (_lock)
                {
                    if (now - _lastUpdateMs > WatchdogMs)
                        Array.Clear(_target);

                    var refresh = now >= nextRefresh;
                    for (var m = 0; m < Hf8Motors.Count; m++)
                    {
                        if (!refresh && _target[m] == _sent[m]) continue;
                        WriteMotor(m, _target[m]);
                        _sent[m] = _target[m];
                    }
                    if (refresh) nextRefresh = now + RefreshMs;
                }
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException)
            {
                // Desenchufado o puerto perdido: quien lo use debe cerrarlo y reabrirlo
                _faulted = true;
                Thread.Sleep(500);
            }

            Thread.Sleep(FrameMs);
        }
    }

    /// <summary>Escribe "M&lt;m&gt;&lt;v&gt;\r" sin asignar memoria. Llamar con _lock tomado.</summary>
    private void WriteMotor(int motor, byte value)
    {
        var n = 0;
        _command[n++] = (byte)'M';
        _command[n++] = (byte)('0' + motor);
        if (value >= 100) _command[n++] = (byte)('0' + value / 100);
        if (value >= 10) _command[n++] = (byte)('0' + value / 10 % 10);
        _command[n++] = (byte)('0' + value % 10);
        _command[n++] = (byte)'\r';
        _port.Write(_command, 0, n);
    }

    public void Dispose()
    {
        if (!_running) return;
        _running = false;
        _writer.Join(500);

        lock (_lock)
        {
            try
            {
                for (var m = 0; m < Hf8Motors.Count; m++) WriteMotor(m, 0);
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException)
            {
                Console.WriteLine($"HF8: no se pudo apagar los motores al cerrar ({ex.Message}).");
            }
            _port.Dispose();
        }
    }
}

using HidSharp;

namespace RigControl.Core.WinWing;

/// <summary>
/// Pantalla (DED) del ICP ViperAce, 200x65 píxeles de 1 bit. Protocolo tomado de DedSharp
/// (github.com/broosa/DedSharp, licencia MIT), mismo output report de 64 B que los LEDs:
///   F0 &lt;op&gt; &lt;secuencia&gt; &lt;longitud&gt; &lt;hasta 60 B&gt;   (op 02 = paquete inicial, 00 = datos)
/// Los datos son órdenes seguidas, partidas en trozos de 60 B:
///   &lt;PID BF06, 4 B&gt; &lt;orden, 4 B&gt; &lt;marca de tiempo FFFF, 4 B&gt; 00 &lt;longitud, 4 B&gt; &lt;datos&gt;
/// 0x102 = escribir la memoria de la pantalla (4 ceros + 65 filas de 25 B, bit bajo = píxel de la izquierda),
/// 0x103 = refrescar (1 B a 0). Todo en little endian. Solo se dibuja: nada de firmware.
/// </summary>
/// <summary>Pantalla del ICP vista como texto (interfaz para poder probar sin el dispositivo).</summary>
public interface IIcpScreen : IDisposable
{
    /// <summary>Muestra IcpFont.Columns x IcpFont.Rows celdas (por filas) con su marca de invertido.</summary>
    void Show(ReadOnlySpan<char> cells, ReadOnlySpan<bool> inverted);
}

public sealed class IcpDisplay : IIcpScreen
{
    public const int ProductId = 0xBF06;
    public const int Width = 200;
    public const int Height = 65;
    private const int RowBytes = Width / 8;
    private const int PayloadLength = 60;
    private const uint CmdWriteMemory = 0x102;
    private const uint CmdRefresh = 0x103;

    private readonly HidStream _stream;
    private readonly byte[] _report = new byte[WinWingPanel.LongReportLength];
    private readonly byte[] _pixels = new byte[Height * RowBytes];
    private byte _sequence = 1;

    private IcpDisplay(HidStream stream) => _stream = stream;

    /// <summary>Abre el ICP (modo compartido) y manda el paquete inicial; null si no está.</summary>
    public static IcpDisplay? TryOpen()
    {
        var device = DeviceList.Local.GetHidDevices(WinWingPanel.VendorId, ProductId)
            .FirstOrDefault(d => { try { return d.GetMaxOutputReportLength() == WinWingPanel.LongReportLength; } catch { return false; } });
        if (device is null || !device.TryOpen(out var stream)) return null;
        var display = new IcpDisplay(stream);
        display.WritePacket(0x02, 0, [0]);
        return display;
    }

    public void Clear() => Array.Clear(_pixels);

    public void SetPixel(int x, int y, bool on = true)
    {
        if ((uint)x >= Width || (uint)y >= Height) return;
        ref var b = ref _pixels[y * RowBytes + x / 8];
        var mask = (byte)(1 << (x % 8));
        b = on ? (byte)(b | mask) : (byte)(b & ~mask);
    }

    /// <summary>
    /// Escribe texto con la fuente de la DED en la celda (columna 0-24, fila 0-4); lo que no cabe se corta.
    /// Invertido = fondo encendido y letras apagadas (los campos seleccionados de la DED).
    /// </summary>
    public void DrawText(int column, int row, ReadOnlySpan<char> text, bool inverted = false)
    {
        for (var i = 0; i < text.Length && column + i < IcpFont.Columns; i++)
        {
            var glyph = IcpFont.Glyph(text[i]);
            int x0 = (column + i) * IcpFont.CharWidth, y0 = row * IcpFont.CharHeight;
            for (var y = 0; y < IcpFont.CharHeight; y++)
            for (var x = 0; x < IcpFont.CharWidth; x++)
                SetPixel(x0 + x, y0 + y, ((glyph[y] >> x & 1) != 0) != inverted);
        }
    }

    public void Show(ReadOnlySpan<char> cells, ReadOnlySpan<bool> inverted)
    {
        Clear();
        Span<char> one = stackalloc char[1];
        for (var i = 0; i < cells.Length && i < IcpFont.Columns * IcpFont.Rows; i++)
        {
            if (cells[i] == ' ' && !inverted[i]) continue;
            one[0] = cells[i];
            DrawText(i % IcpFont.Columns, i / IcpFont.Columns, one, inverted[i]);
        }
        Flush();
    }

    /// <summary>Manda la imagen a la pantalla (memoria + refresco).</summary>
    public void Flush()
    {
        var memory = new byte[4 + _pixels.Length];
        _pixels.CopyTo(memory, 4);
        var data = new List<byte>(1700);
        AddCommand(data, CmdWriteMemory, memory);
        AddCommand(data, CmdRefresh, [0]);
        var bytes = data.ToArray();
        for (var i = 0; i < bytes.Length; i += PayloadLength)
            WritePacket(0x00, _sequence++, bytes.AsSpan(i, Math.Min(PayloadLength, bytes.Length - i)));
    }

    private static void AddCommand(List<byte> data, uint command, byte[] payload)
    {
        data.AddRange(BitConverter.GetBytes((uint)ProductId));
        data.AddRange(BitConverter.GetBytes(command));
        data.AddRange(BitConverter.GetBytes(0xFFFFu));
        data.Add(0);
        data.AddRange(BitConverter.GetBytes(payload.Length));
        data.AddRange(payload);
    }

    private void WritePacket(byte op, byte sequence, ReadOnlySpan<byte> payload)
    {
        Array.Clear(_report);
        _report[0] = 0xF0;
        _report[1] = op;
        _report[2] = sequence;
        _report[3] = (byte)payload.Length;
        payload.CopyTo(_report.AsSpan(4));
        _stream.Write(_report);
    }

    public void Dispose() => _stream.Dispose();
}

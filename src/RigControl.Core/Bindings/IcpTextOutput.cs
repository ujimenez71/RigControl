using RigControl.Core.WinWing;

namespace RigControl.Core.Bindings;

/// <summary>
/// Escribe en la pantalla del ICP las líneas "icp" del perfil (ver IcpTemplate). Si el perfil no tiene, no toca la
/// pantalla (SimAppPro la usa con el F-16). Dibuja como mucho cada RenderMs y solo escribe cuando cambia el texto.
/// Al cambiar a un perfil sin "icp", o al salir, la deja en blanco una vez. El ICP se abre al hacer falta y, si no
/// está, se reintenta cada RetryMs.
/// </summary>
/// <summary>Lo que hay en la pantalla: IcpFont.Columns x IcpFont.Rows celdas por filas y su marca de invertido.</summary>
public sealed record IcpSnapshot(char[] Cells, bool[] Inverted);

public sealed class IcpTextOutput : IDisposable
{
    public const int RenderMs = 100;
    private const int RetryMs = 5000;
    private const int Cells = IcpFont.Columns * IcpFont.Rows;

    private readonly Func<IIcpScreen?> _open;
    private IIcpScreen? _screen;
    private long _nextOpen;
    private long _nextRender;
    private bool _warned;

    private IcpLine[] _lines = [];
    private readonly char[] _cells = new char[Cells];
    private readonly bool[] _inverted = new bool[Cells];
    private readonly char[] _shownCells = new char[Cells];
    private readonly bool[] _shownInverted = new bool[Cells];
    private bool _shown;        // lo que hay en _shown* está en la pantalla
    private bool _clearPending; // hay que dejarla en blanco (el perfil ya no tiene "icp")

    /// <param name="open">Abre la pantalla (por defecto la real, IcpDisplay.TryOpen).</param>
    public IcpTextOutput(Func<IIcpScreen?>? open = null) => _open = open ?? (() => IcpDisplay.TryOpen());

    /// <summary>El perfil activo tiene líneas para el ICP.</summary>
    public bool Active => _lines.Length > 0;

    /// <summary>Texto que hay ahora en la pantalla, una cadena por línea (lista nueva solo al cambiar).</summary>
    public IReadOnlyList<string> Text { get; private set; } = [];

    /// <summary>La pantalla escrita ahora (objeto nuevo solo al cambiar); null si aún no se ha escrito con este perfil.</summary>
    public IcpSnapshot? Shown { get; private set; }

    public void SetProfile(CompiledProfile profile)
    {
        if (_lines.Length > 0 && profile.Icp.Length == 0 && _shown) _clearPending = true;
        _lines = profile.Icp;
        _shown = false; // perfil nuevo: se vuelve a escribir aunque coincida
        _nextRender = 0;
        Text = [];
        Shown = null;
        if (_lines.Length > 0) Log.Info($"Pantalla del ICP: {_lines.Length} línea(s).");
    }

    public void Update(in IcpContext ctx, long timeMs)
    {
        if (_lines.Length == 0 && !_clearPending) return;
        if (timeMs < _nextRender) return;
        _nextRender = timeMs + RenderMs;

        Array.Fill(_cells, ' ');
        Array.Clear(_inverted);
        for (var r = 0; r < _lines.Length && r < IcpFont.Rows; r++)
            IcpTemplate.Render(_lines[r], ctx, _cells.AsSpan(r * IcpFont.Columns, IcpFont.Columns),
                               _inverted.AsSpan(r * IcpFont.Columns, IcpFont.Columns));

        if (_shown && _cells.AsSpan().SequenceEqual(_shownCells) && _inverted.AsSpan().SequenceEqual(_shownInverted)) return;
        var screen = Screen(timeMs);
        if (screen is null) return;
        try
        {
            screen.Show(_cells, _inverted);
        }
        catch (Exception ex)
        {
            Log.Warning($"Pantalla del ICP: error al escribir ({ex.Message}); se reintenta.");
            try { screen.Dispose(); } catch { }
            _screen = null;
            _nextOpen = timeMs + RetryMs;
            _shown = false;
            return;
        }
        _cells.CopyTo(_shownCells, 0);
        _inverted.CopyTo(_shownInverted, 0);
        _shown = true;
        _clearPending = false;
        Shown = new IcpSnapshot([.. _cells], [.. _inverted]);
        Text = [.. Enumerable.Range(0, Math.Min(_lines.Length, IcpFont.Rows))
                             .Select(r => new string(_cells, r * IcpFont.Columns, IcpFont.Columns).TrimEnd())];
    }

    private IIcpScreen? Screen(long timeMs)
    {
        if (_screen is not null) return _screen;
        if (timeMs < _nextOpen) return null;
        _nextOpen = timeMs + RetryMs;
        _screen = _open();
        if (_screen is null)
        {
            if (!_warned) Log.Warning("Pantalla del ICP: no se encuentra el ICP (se reintenta cada 5 s).");
            _warned = true;
        }
        else _warned = false;
        return _screen;
    }

    public void Dispose()
    {
        // Al salir se deja en blanco si se escribió algo.
        if (_screen is not null && (_shown || _clearPending))
        {
            Array.Fill(_cells, ' ');
            Array.Clear(_inverted);
            try { _screen.Show(_cells, _inverted); } catch { }
        }
        _screen?.Dispose();
        _screen = null;
    }
}

using RigControl.Core.WinWing;

namespace RigControl.Core.Bindings;

/// <summary>
/// Enciende los LEDs de los WinWing según las reglas "lights" del perfil ("pto2:GEAR_NOSE": "D:..."). Solo toca
/// los LEDs que nombra el perfil (el resto sigue siendo de SimAppPro) y solo escribe cuando cambian. Al cambiar a
/// un perfil que ya no nombra un LED, o al salir, lo suelta una vez: apagado, o la retroiluminación a 255.
/// Cada dispositivo se abre al hacer falta y, si no está, se reintenta cada RetryMs.
/// </summary>
public sealed class WinWingLightsOutput : IDisposable
{
    private const int RetryMs = 5000;
    private const byte On = 255;

    private readonly Func<int, IWinWingLeds?> _open;
    private readonly Dictionary<int, IWinWingLeds> _panels = [];
    private readonly Dictionary<int, long> _nextOpen = [];
    private readonly HashSet<int> _warned = [];

    private LightRule[] _rules = [];
    private bool[] _lit = [];      // lo que piden las reglas ahora
    private bool?[] _written = []; // lo último escrito (null = sin escribir)
    private readonly List<WinWingLight> _release = []; // LEDs que hay que soltar (ya no los nombra el perfil)

    /// <param name="open">Abre un dispositivo por PID (por defecto el real, WinWingPanel.TryOpen).</param>
    public WinWingLightsOutput(Func<int, IWinWingLeds?>? open = null) => _open = open ?? (pid => WinWingPanel.TryOpen(pid));

    /// <summary>Hay luces de WinWing en el perfil activo.</summary>
    public bool Active => _rules.Length > 0;

    /// <summary>Nombres de las luces encendidas ahora ("pto2:GEAR_NOSE"...). Se crea una lista nueva solo al cambiar.</summary>
    public IReadOnlyList<string> Lit { get; private set; } = [];

    public void SetProfile(CompiledProfile profile)
    {
        var rules = profile.Lights.Where(r => r.WinWing is not null).ToArray();
        // Lo que se deja de controlar se suelta (si alguna vez se escribió).
        for (var i = 0; i < _rules.Length; i++)
            if (_written[i] is not null && !rules.Any(r => r.WinWing == _rules[i].WinWing))
                _release.Add(_rules[i].WinWing!);
        _rules = rules;
        _lit = new bool[rules.Length];
        _written = new bool?[rules.Length];
        Lit = [];
        if (rules.Length > 0)
            Log.Info($"Luces de WinWing: {rules.Length} regla(s) ({string.Join(", ", rules.Select(r => r.Name))}).");
    }

    /// <param name="values">Valores de CompiledProfile.WatchedVars.</param>
    /// <param name="simConnected">Sin simulador, todas apagadas.</param>
    public void Update(double[] values, bool simConnected, long timeMs)
    {
        var changed = false;
        for (var i = 0; i < _rules.Length; i++)
        {
            var on = simConnected && values.Length > 0 && _rules[i].Evaluate(values);
            if (on != _lit[i]) { _lit[i] = on; changed = true; }
        }
        if (changed) Lit = [.. _rules.Where((_, i) => _lit[i]).Select(r => r.Name)];

        for (var i = _release.Count - 1; i >= 0; i--)
        {
            var l = _release[i];
            if (Write(l, l.ReleaseValue, timeMs)) _release.RemoveAt(i);
        }

        for (var i = 0; i < _rules.Length; i++)
        {
            if (_written[i] == _lit[i]) continue;
            var light = _rules[i].WinWing!;
            if (Write(light, _lit[i] ? On : (byte)0, timeMs)) _written[i] = _lit[i];
        }
    }

    private bool Write(WinWingLight light, byte value, long timeMs)
    {
        var panel = Panel(light.ProductId, timeMs);
        if (panel is null) return false;
        try
        {
            panel.Set(light.Led, value);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning($"Luces de WinWing: error al escribir en {light.Name} ({ex.Message}); se reintenta.");
            try { panel.Dispose(); } catch { }
            _panels.Remove(light.ProductId);
            _nextOpen[light.ProductId] = timeMs + RetryMs;
            for (var i = 0; i < _rules.Length; i++)
                if (_rules[i].WinWing!.ProductId == light.ProductId) _written[i] = null; // al volver, se reescribe todo
            return false;
        }
    }

    private IWinWingLeds? Panel(int pid, long timeMs)
    {
        if (_panels.TryGetValue(pid, out var panel)) return panel;
        if (_nextOpen.TryGetValue(pid, out var next) && timeMs < next) return null;
        _nextOpen[pid] = timeMs + RetryMs;
        panel = _open(pid);
        if (panel is null)
        {
            if (_warned.Add(pid)) Log.Warning($"Luces de WinWing: no se encuentra el dispositivo {pid:X4} (se reintenta cada 5 s).");
            return null;
        }
        _warned.Remove(pid);
        _panels[pid] = panel;
        return panel;
    }

    public void Dispose()
    {
        // Al salir se sueltan todos los LEDs que se hayan tocado.
        for (var i = 0; i < _rules.Length; i++)
            if (_written[i] is not null) _release.Add(_rules[i].WinWing!);
        foreach (var l in _release)
            if (_panels.TryGetValue(l.ProductId, out var p))
                try { p.Set(l.Led, l.ReleaseValue); } catch { }
        _release.Clear();
        foreach (var p in _panels.Values) p.Dispose();
        _panels.Clear();
    }
}

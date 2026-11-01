namespace RigControl.Core.Bindings;

/// <summary>
/// Enciende las luces del Bravo según las reglas "lights" del perfil y los valores que llegan del
/// simulador. Solo escribe cuando cambia alguna luz. Si el perfil activo no define luces, no toca el
/// Bravo (así no pelea con otros programas que lo controlen); si antes había luces, las apaga una vez.
/// Sin asignaciones en Update salvo al (re)abrir el dispositivo, cada RetryMs como mucho.
/// </summary>
public sealed class BravoLightsOutput : IDisposable
{
    private const int RetryMs = 5000;

    private BravoLights? _bravo;
    private LightRule[] _rules = [];
    private BravoLed _written;
    private bool _dirty;
    private long _nextOpen;
    private bool _warnedMissing;

    /// <summary>Luces encendidas ahora mismo (lo último escrito o que se escribiría si hubiera Bravo).</summary>
    public BravoLed Current { get; private set; }

    /// <summary>Hay reglas de luces en el perfil activo.</summary>
    public bool Active => _rules.Length > 0;

    public void SetProfile(CompiledProfile profile)
    {
        _rules = profile.Lights;
        _dirty = true;
        if (_rules.Length > 0)
            Log.Info($"Luces del Bravo: {_rules.Length} regla(s) ({string.Join(", ", _rules.Select(r => r.Name))}).");
    }

    /// <param name="values">Valores de CompiledProfile.WatchedVars (SimConnectOutput.Values).</param>
    /// <param name="simConnected">Sin simulador, todas apagadas.</param>
    public void Update(double[] values, bool simConnected, long timeMs)
    {
        var mask = BravoLed.None;
        if (simConnected && values.Length > 0)
            foreach (var r in _rules)
                if (r.Evaluate(values)) mask |= r.Led;
        Current = mask;

        if (_rules.Length == 0)
        {
            // Perfil sin luces: apagar lo que hubiéramos encendido y soltar el Bravo.
            if (_bravo is not null)
            {
                _bravo.Dispose(); // apaga todo
                _bravo = null;
                _written = BravoLed.None;
            }
            return;
        }

        if (_bravo is null)
        {
            if (timeMs < _nextOpen) return;
            _nextOpen = timeMs + RetryMs;
            _bravo = BravoLights.TryOpen();
            if (_bravo is null)
            {
                if (!_warnedMissing) Log.Warning("Luces del Bravo: no se encuentra el Bravo (se reintenta cada 5 s).");
                _warnedMissing = true;
                return;
            }
            _warnedMissing = false;
            _dirty = true;
        }

        if (!_dirty && mask == _written) return;
        try
        {
            _bravo.Set(mask);
            _written = mask;
            _dirty = false;
        }
        catch (Exception ex)
        {
            Log.Warning($"Luces del Bravo: error al escribir ({ex.Message}); se reintenta.");
            try { _bravo.Dispose(); } catch { }
            _bravo = null;
            _nextOpen = timeMs + RetryMs;
        }
    }

    public void Dispose()
    {
        _bravo?.Dispose(); // apaga todas al salir
        _bravo = null;
    }
}

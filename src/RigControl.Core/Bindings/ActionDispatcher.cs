namespace RigControl.Core.Bindings;

/// <summary>Destino de las acciones K:/B:/L: (SimConnect). Send devuelve false si no se pudo enviar.</summary>
public interface ISimOutput
{
    bool IsConnected { get; }
    bool Send(ActionSpec action);

    /// <summary>Registra en el simulador lo que usa el perfil (eventos, variables).</summary>
    void Prepare(CompiledProfile profile);

    /// <summary>Conecta/reintenta y procesa mensajes. Se llama en cada ciclo; no debe bloquear.</summary>
    void Tick(long timeMs);
}

/// <summary>
/// Reparte cada acción a su salida y la registra:
///  - key:  toque en Trigger; en un hold, tecla pulsada en INICIO y soltada en FIN.
///  - K:/B:/L:  se envían en Trigger e INICIO.
///  - D: (DCS-BIOS)  pulsador: toque (1 y 0 a los 50 ms) en Trigger, 1 en INICIO y 0 en FIN;
///    con valor: se envía en Trigger e INICIO.
///  - Con "repeatMs", un hold con K:/B:/L:/D: se reenvía cada tantos ms hasta el FIN (sin registrar
///    cada repetición).
///  - sin prefijo: solo se registra.
/// Con dryRun no se envía nada. Sin asignaciones en OnAction/Tick salvo el texto del registro.
/// </summary>
public sealed class ActionDispatcher(KeyboardOutput? keyboard, ISimOutput? sim, bool dryRun, DcsBiosOutput? dcs = null)
    : IActionSink, IDisposable
{
    private const int MaxRepeats = 16;
    private readonly Binding?[] _repeating = new Binding?[MaxRepeats];
    private readonly long[] _nextRepeat = new long[MaxRepeats];

    public void OnAction(Binding binding, ActionPhase phase, long timeMs)
    {
        var a = binding.Output;
        var result = "";

        if (a.Kind == ActionKind.Key && a.Keys is { } keys)
        {
            if (dryRun || keyboard is null) result = "(simulado)";
            else if (phase == ActionPhase.Trigger) keyboard.Tap(keys, timeMs);
            else if (phase == ActionPhase.Start) keyboard.Down(keys);
            else keyboard.Up(keys);
        }
        else if (a.IsExternal)
        {
            if (binding.RepeatMs > 0)
            {
                if (phase == ActionPhase.Start) StartRepeat(binding, timeMs);
                else if (phase == ActionPhase.End) StopRepeat(binding);
            }

            if (dryRun)
            {
                if (phase != ActionPhase.End || a.DcsRelease is not null)
                    result = binding.RepeatMs > 0 && phase == ActionPhase.Start ? $"(simulado, cada {binding.RepeatMs} ms)" : "(simulado)";
            }
            else if (a.Kind == ActionKind.DcsBios)
            {
                if (dcs is null || !dcs.IsActive) result = "(sin datos de DCS-BIOS)";
                var ok = phase switch
                {
                    ActionPhase.Trigger => dcs?.Send(a, tap: true) ?? false,
                    ActionPhase.Start => dcs?.Send(a, tap: false) ?? false,
                    _ => a.DcsRelease is null || (dcs?.Release(a) ?? false),
                };
                if (!ok && result.Length == 0) result = "(no se pudo enviar a DCS-BIOS)";
            }
            else if (phase != ActionPhase.End)
            {
                if (sim is null || !sim.IsConnected) result = "(sin conexión con el simulador)";
                else if (!sim.Send(a)) result = "(el simulador no lo aceptó)";
            }

            if (result.Length == 0 && binding.RepeatMs > 0 && phase == ActionPhase.Start)
                result = $"(repitiendo cada {binding.RepeatMs} ms)";
        }

        ConsoleActionSink.Print(binding, phase, result);
    }

    public void Tick(long timeMs)
    {
        keyboard?.Tick(timeMs);
        sim?.Tick(timeMs); // también en --dry: hace falta para saber qué avión hay ("run auto")
        dcs?.Tick(timeMs);

        for (var i = 0; i < MaxRepeats; i++)
        {
            var b = _repeating[i];
            if (b is null || timeMs < _nextRepeat[i]) continue;
            _nextRepeat[i] += b.RepeatMs;
            if (_nextRepeat[i] <= timeMs) _nextRepeat[i] = timeMs + b.RepeatMs; // el bucle se retrasó: no ráfagas
            if (dryRun) continue;
            if (b.Output.Kind == ActionKind.DcsBios) dcs?.Send(b.Output, tap: false);
            else if (sim is { IsConnected: true }) sim.Send(b.Output);
        }
    }

    public void ProfileLoaded(CompiledProfile profile)
    {
        // EndHolds ya ha cerrado los hold del perfil anterior; esto es solo por seguridad.
        Array.Clear(_repeating);
        sim?.Prepare(profile);
    }

    public void Dispose() => keyboard?.ReleaseAll();

    private void StartRepeat(Binding b, long timeMs)
    {
        for (var i = 0; i < MaxRepeats; i++)
        {
            if (_repeating[i] is not null) continue;
            _repeating[i] = b;
            _nextRepeat[i] = timeMs + b.RepeatMs;
            return;
        }
    }

    private void StopRepeat(Binding b)
    {
        for (var i = 0; i < MaxRepeats; i++)
            if (ReferenceEquals(_repeating[i], b)) _repeating[i] = null;
    }

    /// <summary>Cuántas repeticiones hay activas (para pruebas).</summary>
    public int ActiveRepeats => _repeating.Count(r => r is not null);
}

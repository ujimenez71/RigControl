using RigControl.Core.Bindings;

namespace RigControl.Core.Ai;

/// <summary>Avión cargado ahora y, en MSFS, sus controles B: (para dárselos a la IA).</summary>
/// <param name="Sim">"dcs", "msfs" o null si no hay ninguno.</param>
/// <param name="Aircraft">Como en "aircraft" del perfil: "dcs:AH-64D_BLK_II" o la carpeta del avión de MSFS.</param>
public sealed record SimSnapshot(string? Sim, string? Aircraft, IReadOnlyCollection<string>? InputEvents)
{
    /// <summary>
    /// Escucha DCS-BIOS y SimConnect hasta <paramref name="timeoutMs"/> (bloquea el hilo: llamarlo
    /// fuera del de la interfaz). Para en cuanto DCS da el avión o MSFS da la lista de B:.
    /// </summary>
    public static SimSnapshot Read(int timeoutMs = 6000, CancellationToken cancel = default)
    {
        using var sim = new SimConnectOutput();
        using var dcs = new DcsBiosOutput();
        IReadOnlyDictionary<string, ulong>? events = null;
        sim.InputEventsReady += e => events = e;

        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < timeoutMs && !cancel.IsCancellationRequested)
        {
            sim.Tick(clock.ElapsedMilliseconds);
            dcs.Tick(clock.ElapsedMilliseconds);
            if (dcs.IsActive && dcs.Aircraft is not null) return new("dcs", "dcs:" + dcs.Aircraft, null);
            if (events is not null) return new("msfs", AircraftFolder(sim.Aircraft), [.. events.Keys]);
            Thread.Sleep(50);
        }
        return sim.IsConnected ? new("msfs", AircraftFolder(sim.Aircraft), null) : new(null, null, null);
    }

    /// <summary>"simobjects\airplanes\asobo_c172sp_g1000\..." -> "asobo_c172sp_g1000" (lo que va en "aircraft"; igual que el editor).</summary>
    public static string? AircraftFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var parts = SimConnectOutput.NormalizeAircraft(path).Split('\\',StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 3 ? parts[2] : parts.LastOrDefault();
    }
}

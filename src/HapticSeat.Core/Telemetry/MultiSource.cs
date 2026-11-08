using System.Net.Sockets;

namespace HapticSeat.Telemetry;

/// <summary>
/// Atiende varios simuladores a la vez y usa el que está volando: primero el que está activo, si no
/// el que está conectado, y a igualdad el primero de la lista. Así la app no necesita elegir simulador.
/// </summary>
public sealed class MultiSource(params ITelemetrySource[] sources) : ITelemetrySource
{
    private readonly FlightState _scratch = new();
    private readonly Lock _lock = new();

    public string Name => string.Join(" + ", sources.Select(s => s.Name));

    public long PacketCount => sources.Sum(s => s.PacketCount);

    /// <summary>Arranca todos aunque alguno falle; el primer error se relanza al final para poder mostrarlo.</summary>
    public void Start()
    {
        SocketException? first = null;
        foreach (var source in sources)
        {
            try
            {
                source.Start();
            }
            catch (SocketException ex)
            {
                first ??= ex;
            }
        }
        if (first is not null) throw first;
    }

    public void Snapshot(FlightState target)
    {
        lock (_lock)
        {
            var best = -1;
            foreach (var source in sources)
            {
                source.Snapshot(_scratch);
                var score = _scratch.Active ? 2 : _scratch.Connected ? 1 : 0;
                if (score <= best) continue;
                best = score;
                _scratch.CopyTo(target);
            }
        }
    }

    public void DrainEvents(List<FlightEvent> into)
    {
        foreach (var source in sources) source.DrainEvents(into);
    }

    public bool TryTakeNotice(out string notice)
    {
        foreach (var source in sources)
            if (source.TryTakeNotice(out notice)) return true;
        notice = "";
        return false;
    }

    public void Dispose()
    {
        foreach (var source in sources) source.Dispose();
    }
}

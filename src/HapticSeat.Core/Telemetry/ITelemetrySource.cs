namespace HapticSeat.Telemetry;

/// <summary>Lector de telemetría de un simulador. Recibe en su propio hilo; los métodos son seguros entre hilos.</summary>
public interface ITelemetrySource : IDisposable
{
    string Name { get; }

    long PacketCount { get; }

    void Start();

    /// <summary>Copia el último estado conocido (sin asignar memoria).</summary>
    void Snapshot(FlightState target);

    /// <summary>Mueve a <paramref name="into"/> los eventos recibidos desde la última llamada.</summary>
    void DrainEvents(List<FlightEvent> into);

    /// <summary>IDs de indicadores/eventos desconocidos vistos por primera vez (para depurar).</summary>
    bool TryTakeNotice(out string notice);
}

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using HapticSeat.Effects;
using HapticSeat.Hf8;
using HapticSeat.Telemetry;

namespace HapticSeat;

/// <summary>
/// Bucle principal a ~100 Hz: telemetría → efectos → HF8. Muestra una línea de estado con los datos
/// de vuelo y una barra por motor. Sin cojín (hf8 null) sirve de monitor de telemetría.
/// </summary>
public static class EffectRunner
{
    private const int FrameMs = 10;
    private const int StatusMs = 200;
    private static readonly char[] Bars = [' ', '▁', '▂', '▃', '▄', '▅', '▆', '▇', '█'];

    public static void Run(ITelemetrySource source, Hf8Device? hf8, EffectEngine engine, byte[] minLevels, CancellationToken cancel)
    {
        var state = new FlightState();
        var events = new List<FlightEvent>(64);
        Span<float> motors = stackalloc float[Hf8Motors.Count];
        Span<byte> levels = stackalloc byte[Hf8Motors.Count];
        var status = new StringBuilder(160);
        var counts = new int[Enum.GetValues<FlightEventType>().Length];

        var clock = Stopwatch.StartNew();
        var last = clock.Elapsed.TotalSeconds;
        long nextStatus = 0;
        var aircraft = "";

        Console.WriteLine($"Escuchando {source.Name}{(hf8 is null ? " (sin cojín)" : $", HF8 en {hf8.PortName}")}. Ctrl+C para salir.");
        while (!cancel.IsCancellationRequested)
        {
            var now = clock.Elapsed.TotalSeconds;
            var dt = (float)Math.Clamp(now - last, 0.001, 0.1);
            last = now;

            source.Snapshot(state);
            events.Clear();
            source.DrainEvents(events);
            engine.Update(state, CollectionsMarshal.AsSpan(events), dt, motors);
            EffectEngine.ToLevels(motors, levels, minLevels);
            hf8?.SetMotors(levels);

            foreach (var e in events) counts[(int)e.Type]++;

            if (clock.ElapsedMilliseconds >= nextStatus)
            {
                nextStatus = clock.ElapsedMilliseconds + StatusMs;
                while (source.TryTakeNotice(out var notice)) WriteLine(notice);
                if (state.Aircraft != aircraft)
                {
                    aircraft = state.Aircraft;
                    WriteLine($"Avión: {aircraft} ({state.EngineCount} motor(es), {state.WheelCount} rueda(s))");
                }
                WriteEvents(counts);
                Status(status, state, motors, source.PacketCount);
            }

            Thread.Sleep(FrameMs);
        }
        Console.WriteLine();
    }

    /// <summary>Resume los eventos de los últimos 200 ms en una línea (los disparos van a ráfagas).</summary>
    private static void WriteEvents(int[] counts)
    {
        var sb = new StringBuilder();
        for (var t = 0; t < counts.Length; t++)
        {
            if (counts[t] == 0) continue;
            sb.Append($"{(FlightEventType)t}×{counts[t]} ");
            counts[t] = 0;
        }
        if (sb.Length > 0) WriteLine($"Eventos: {sb}");
    }

    private static void Status(StringBuilder sb, FlightState s, ReadOnlySpan<float> motors, long packets)
    {
        sb.Clear();
        if (!s.Connected)
            sb.Append($"Esperando telemetría… (paquetes: {packets})");
        else if (s.Paused)
            sb.Append("En pausa / menús");
        else
        {
            sb.Append($"IAS {s.IasMs * 3.6f,4:0} km/h  G {s.VerticalG,4:0.0}  AGL {s.AglM,5:0} m  RPM {(s.EngineCount > 0 ? s.EngineRpm[0] : 0),4:0}");
            if (s.Rotorcraft) sb.Append($"  rotor {s.RotorSpeed * 100,3:0}%");
            sb.Append($"  buffet {s.BuffetAmplitude:0.00}@{s.BuffetFrequency:0}Hz  ruedas ");
            for (var i = 0; i < s.WheelCount; i++) sb.Append($"{s.WheelLoad[i]:0.0} ");
        }

        sb.Append(" |");
        foreach (var v in motors) sb.Append(Bars[(int)Math.Round(v * (Bars.Length - 1))]);
        sb.Append('|');

        var line = sb.ToString();
        if (Console.IsOutputRedirected)
        {
            Console.WriteLine(line); // sin consola no se puede reescribir la línea
            return;
        }
        var width = Math.Max(1, Console.WindowWidth - 1);
        Console.Write('\r' + (line.Length > width ? line[..width] : line.PadRight(width)));
    }

    private static void WriteLine(string text)
    {
        var width = Console.IsOutputRedirected ? 0 : Math.Max(0, Console.WindowWidth - 1);
        Console.WriteLine('\r' + text.PadRight(width));
    }
}

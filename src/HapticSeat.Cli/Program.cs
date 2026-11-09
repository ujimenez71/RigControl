using System.Diagnostics;
using HapticSeat.Effects;
using HapticSeat.Hf8;
using HapticSeat.Profiles;
using HapticSeat.Telemetry;
using HapticSeat.Telemetry.Dcs;
using HapticSeat.Telemetry.Il2;
using HapticSeat.Telemetry.Msfs;

namespace HapticSeat;

/// <summary>
///   HapticSeat estado                      -> puerto, escala del dial y modo (PC/Audio)
///   HapticSeat test [pct]                  -> barrido motor a motor (40% por defecto)
///   HapticSeat motor &lt;n|todos&gt; &lt;pct&gt; [ms] -> mantiene uno o todos los motores (1000 ms por defecto)
///   HapticSeat umbral [n]                  -> rampa lenta en un motor para encontrar dónde empieza a notarse
///   HapticSeat il2 [opciones]              -> efectos con la telemetría de IL-2
///       --sin-cojin    solo monitor de telemetría (no abre el HF8)
///       --ganancia N   ganancia general en % (100 por defecto)
///       --minimo N     fuerza el mismo nivel mínimo 0-255 en todos los motores
///                      (por defecto, la calibración guardada por la app en %APPDATA%\HapticSeat\settings.json)
///       --puerto N     puerto UDP (5678 por defecto)
///   HapticSeat msfs [opciones]             -> igual con MSFS 2024 por SimConnect (--puerto no aplica)
///   HapticSeat dcs [opciones]              -> igual con DCS (dcs/HapticSeatExport.lua, puerto 5679)
/// Ctrl+C detiene; los motores siempre quedan a 0 al salir.
/// </summary>
internal static class Program
{
    private static readonly CancellationTokenSource Cancel = new();

    private static int Main(string[] args)
    {
        var mode = args.FirstOrDefault()?.ToLowerInvariant() ?? "estado";
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true; // salir por el camino normal para que Dispose apague los motores
            Cancel.Cancel();
        };

        if (mode == "il2") return Telemetry(args[1..], port => new Il2Source(port));
        if (mode == "msfs") return Telemetry(args[1..], _ => new MsfsSource());
        if (mode == "dcs")
        {
            // El puerto por defecto de "Telemetry" es el de IL-2: sin --puerto, el de DCS
            var explicitPort = args.Contains("--puerto");
            return Telemetry(args[1..], port => new DcsSource(explicitPort ? port : DcsSource.DefaultPort));
        }

        Hf8Device hf8;
        try
        {
            hf8 = Hf8Device.Open();
        }
        catch (IOException ex)
        {
            Console.WriteLine(ex.Message);
            return 1;
        }

        using (hf8)
        {
            switch (mode)
            {
                case "estado":
                    return Status(hf8);
                case "test":
                    return Sweep(hf8, args.Length > 1 ? args[1] : "40");
                case "motor" when args.Length > 2:
                    return Hold(hf8, args[1], args[2], args.Length > 3 ? args[3] : "1000");
                case "umbral":
                    return Threshold(hf8, args.Length > 1 ? args[1] : "2");
                default:
                    Console.WriteLine("Uso: HapticSeat estado | test [pct] | motor <0-7|todos> <pct> [ms] | umbral [motor]");
                    Console.WriteLine("     HapticSeat il2|msfs|dcs [--sin-cojin] [--ganancia %] [--minimo 0-255] [--puerto N]");
                    return 1;
            }
        }
    }

    // ------------------------------------------------------ telemetría (il2, msfs)

    private static int Telemetry(string[] options, Func<int, ITelemetrySource> createSource)
    {
        var withSeat = true;
        var gain = 100;
        var minLevel = -1; // -1 = el medido en cada motor
        var port = Il2Source.DefaultPort;

        for (var i = 0; i < options.Length; i++)
        {
            if (options[i] == "--sin-cojin")
            {
                withSeat = false;
                continue;
            }

            var ok = options[i] switch
            {
                "--ganancia" => ++i < options.Length && int.TryParse(options[i], out gain) && gain is >= 0 and <= 200,
                "--minimo" => ++i < options.Length && int.TryParse(options[i], out minLevel) && minLevel is >= 0 and < 255,
                "--puerto" => ++i < options.Length && int.TryParse(options[i], out port) && port is > 0 and < 65536,
                _ => false,
            };
            if (!ok)
            {
                Console.WriteLine($"Opción no válida: {string.Join(' ', options[..Math.Min(i + 1, options.Length)])}");
                return 1;
            }
        }

        Hf8Device? hf8 = null;
        if (withSeat)
        {
            try
            {
                hf8 = Hf8Device.Open();
            }
            catch (IOException ex)
            {
                Console.WriteLine(ex.Message);
                return 1;
            }
        }

        using (hf8)
        using (var source = createSource(port))
        {
            try
            {
                source.Start();
            }
            catch (System.Net.Sockets.SocketException ex)
            {
                Console.WriteLine($"No se pudo escuchar el puerto UDP {port}: {ex.Message}");
                return 1;
            }

            var engine = EffectEngine.CreateDefault();
            engine.MasterGain = gain / 100f;
            // Por defecto, la calibración guardada por la app (settings.json)
            var minLevels = minLevel < 0
                ? new ProfileStore().LoadSettings().MinLevelBytes()
                : Enumerable.Repeat((byte)minLevel, Hf8Motors.Count).ToArray();
            EffectRunner.Run(source, hf8, engine, minLevels, Cancel.Token);
        }
        return 0;
    }

    // ------------------------------------------------------ pruebas del cojín

    private static int Status(Hf8Device hf8)
    {
        var status = hf8.QueryStatus();
        Console.WriteLine($"HF8 en {hf8.PortName}");
        Console.WriteLine($"  Dial de intensidad: {status.IntensityScale}/255 ({status.IntensityScale * 100 / 255}%)");
        Console.WriteLine($"  Modo: {(status.AudioMode ? "Audio (no acepta órdenes por USB: mantén el dial rojo 10 s)" : "PC")}");
        return 0;
    }

    private static int Sweep(Hf8Device hf8, string pctText)
    {
        if (!TryParsePercent(pctText, out var level)) return 1;

        Span<byte> levels = stackalloc byte[Hf8Motors.Count];
        for (var m = 0; m < Hf8Motors.Count && !Cancel.IsCancellationRequested; m++)
        {
            Console.WriteLine($"M{m} ({Hf8Motors.Names[m]})");
            levels.Clear();
            levels[m] = level;
            Drive(hf8, levels, 1000);
            levels.Clear();
            Drive(hf8, levels, 500);
        }
        return 0;
    }

    private static int Hold(Hf8Device hf8, string motorText, string pctText, string msText)
    {
        if (!TryParsePercent(pctText, out var level)) return 1;
        if (!int.TryParse(msText, out var ms) || ms <= 0)
        {
            Console.WriteLine($"Duración no válida: {msText}");
            return 1;
        }

        Span<byte> levels = stackalloc byte[Hf8Motors.Count];
        if (motorText.Equals("todos", StringComparison.OrdinalIgnoreCase))
            levels.Fill(level);
        else if (int.TryParse(motorText, out var m) && m is >= 0 and < Hf8Motors.Count)
            levels[m] = level;
        else
        {
            Console.WriteLine($"Motor no válido: {motorText} (0-7 o \"todos\")");
            return 1;
        }

        Drive(hf8, levels, ms);
        return 0;
    }

    /// <summary>
    /// Sube un motor de 5 en 5 hasta 80 para ver dónde empieza a notarse. Pulsos de 1 s separados por
    /// 0,5 s de silencio, para poder contarlos sentado sin mirar la pantalla (pulso n = nivel 5·n).
    /// </summary>
    private static int Threshold(Hf8Device hf8, string motorText)
    {
        if (!int.TryParse(motorText, out var m) || m is < 0 or >= Hf8Motors.Count)
        {
            Console.WriteLine($"Motor no válido: {motorText} (0-7)");
            return 1;
        }

        Console.WriteLine($"Rampa en M{m} ({Hf8Motors.Names[m]}). Cuenta los pulsos: el primero que notes × 5 = nivel mínimo.");
        Console.WriteLine("Empieza en 3 s. Ctrl+C para parar.");
        Thread.Sleep(3000);
        Span<byte> levels = stackalloc byte[Hf8Motors.Count];
        for (var level = 5; level <= 80 && !Cancel.IsCancellationRequested; level += 5)
        {
            Console.WriteLine($"  pulso {level / 5,2}: nivel {level,3} ({level * 100 / 255}%)");
            levels[m] = (byte)level;
            Drive(hf8, levels, 1000);
            levels.Clear();
            Drive(hf8, levels, 500);
        }
        return 0;
    }

    /// <summary>Mantiene unos niveles durante un tiempo, refrescando el watchdog del driver.</summary>
    private static void Drive(Hf8Device hf8, ReadOnlySpan<byte> levels, int ms)
    {
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < ms && !Cancel.IsCancellationRequested)
        {
            hf8.SetMotors(levels);
            Thread.Sleep(20);
        }
        hf8.AllOff();
    }

    private static bool TryParsePercent(string text, out byte level)
    {
        level = 0;
        if (!int.TryParse(text, out var pct) || pct is < 0 or > 100)
        {
            Console.WriteLine($"Porcentaje no válido: {text} (0-100)");
            return false;
        }
        level = (byte)Math.Round(pct * 255 / 100.0);
        return true;
    }
}

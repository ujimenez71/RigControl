using System.Globalization;

namespace RigControl.Core.Graphics;

/// <summary>Un cambio recomendado: ajuste, de qué nivel a cuál y por qué.</summary>
public sealed record Recommendation(string Key, string Label, string From, string To, string Reason);

/// <summary>Una prueba del optimizador con su resultado.</summary>
public sealed record OptimizerTest(string Description, BenchResult Result, bool Holds);

/// <summary>Lo que sale de optimizar: refresco detectado, pruebas, cambios recomendados, consejos y el perfil propuesto.</summary>
public sealed record OptimizeReport(int RefreshHz, bool BaselineHolds, FrameStats Baseline, List<OptimizerTest> Tests,
                                    List<Recommendation> Changes, List<string> Notes, GraphicsProfile Profile);

/// <summary>
/// Optimizador para VR. En el visor el ritmo lo marca su refresco: si DCS llega, cada fotograma tarda lo mismo
/// (13,9 ms a 72 Hz) haya margen o no, así que el margen no se puede leer de una medición: hay que probar.
/// 1.er arranque: la configuración actual + cada ajuste un nivel arriba (si llega al refresco, en orden de
/// calidad ganada por coste) o abajo (si no llega, en orden de FPS ganados por calidad perdida), uno a uno.
/// 2.º arranque (si hace falta): la combinación de lo que ha valido y combinaciones más cortas por si no cabe todo.
/// Resultado: perfil nuevo con los cambios (no se aplica: lo revisa el usuario). Solo ajustes que DCS cambia sin
/// reiniciar; texturas y DLSS se dejan como consejo.
/// </summary>
public sealed class VrOptimizer(DcsBenchmark bench, HeadsetInfo headset)
{
    /// <summary>Pruebas individuales como mucho (cada una ~1 min en VR).</summary>
    public int MaxCandidates { get; init; } = 6;

    /// <summary>Segundos por escena en las pruebas (más cortas que una medición normal; las medias se repiten a 10 s).</summary>
    public int SceneSeconds { get; init; } = 10;

    /// <summary>"Llega" = como mucho un 2 % de fotogramas fuera de tiempo y el 1 % bajo a un 90 % del refresco.</summary>
    public static bool Holds(FrameStats s, int hz) => s.MissedPercent <= 2.0 && s.P1LowFps >= hz * 0.9;

    /// <summary>Ritmo fijo a la mitad del refresco (mediana a < 3 % de 2000/hz y casi sin fotogramas más rápidos).</summary>
    public static bool LockedAtHalf(FrameStats s, int hz) =>
        Math.Abs(s.MedianMs - 2000.0 / hz) / (2000.0 / hz) < 0.03 && s.AvgFps < hz * 0.55;

    /// <summary>Un nivel arriba (holds) o abajo de cada ajuste que se puede probar, en el orden en que conviene probarlos.</summary>
    public static List<(DcsSetting Setting, int From, int To, double Score)> Candidates(IReadOnlyDictionary<string, string> current, bool up)
    {
        List<(DcsSetting, int, int, double)> list = [];
        foreach (var s in DcsSettingsCatalog.All.Where(s => !s.Restart))
        {
            var i = s.IndexOf(current.TryGetValue(s.Key, out var v) ? v : null);
            if (i < 0) continue;
            var j = up ? i + 1 : i - 1;
            if (j < 0 || j >= s.Levels.Length) continue;
            var dq = Math.Abs(s.Levels[j].Quality - s.Levels[i].Quality);
            var dg = Math.Abs(s.Levels[j].Gpu - s.Levels[i].Gpu);
            // Subir: calidad por coste. Bajar: FPS ganados por calidad perdida.
            var score = up ? dq / Math.Max(dg, 0.005) : dg / Math.Max(dq, 0.5);
            list.Add((s, i, j, score));
        }
        return [.. list.OrderByDescending(c => c.Item4)];
    }

    public OptimizeReport Run(IProgress<string>? progress = null, CancellationToken cancel = default)
    {
        // ---- 1.er arranque: actual + cada cambio por separado.
        var current = new GraphicsManager(Path.Combine(bench.SavedGamesDir, "Config"), null, _ => false).Current("dcs");
        progress?.Report("Arranque 1: midiendo tu configuración y probando cada ajuste por separado.");
        // Sin saber aún si llega, se prueban las dos direcciones más prometedoras (la mitad de cada una).
        var ups = Candidates(current, up: true).Take(MaxCandidates / 2 + 1).ToList();
        var downs = Candidates(current, up: false).Take(MaxCandidates / 2).ToList();
        var keys = ups.Concat(downs).Select(c => c.Setting.Key).Distinct().ToList();
        Dictionary<string, string> Base() => keys.ToDictionary(k => k, k => current[k]);
        List<BenchTrial> trials = [new("actual", Base())];
        foreach (var c in ups) trials.Add(new($"+{c.Setting.Key}", With(Base(), c.Setting, c.To)));
        foreach (var c in downs) trials.Add(new($"-{c.Setting.Key}", With(Base(), c.Setting, c.To)));

        var r1 = bench.MeasureTrials(null, trials, vr: true, headset.RefreshHz, progress, cancel, sceneSeconds: SceneSeconds);
        var hz = r1[0].RefreshHz;
        var baseline = r1[0].Total;
        var holds = Holds(baseline, hz);
        progress?.Report($"Tu configuración a {hz} Hz: {baseline.AvgFps:0.0} FPS, {baseline.MissedPercent:0.0} % fuera de tiempo → " +
                         (holds ? "llega." : "NO llega."));

        List<OptimizerTest> tests = [new("Configuración actual", r1[0], holds)];
        List<(DcsSetting Setting, int From, int To, double Score)> passed = [];
        for (var i = 0; i < ups.Count; i++)
        {
            var c = ups[i];
            var ok = Holds(r1[1 + i].Total, hz);
            tests.Add(new($"{c.Setting.Label}: {c.Setting.Levels[c.From].Name} → {c.Setting.Levels[c.To].Name}", r1[1 + i], ok));
            if (holds && ok) passed.Add(c);
        }
        for (var i = 0; i < downs.Count; i++)
        {
            var c = downs[i];
            var r = r1[1 + ups.Count + i];
            var ok = Holds(r.Total, hz);
            tests.Add(new($"{c.Setting.Label}: {c.Setting.Levels[c.From].Name} → {c.Setting.Levels[c.To].Name}", r, ok));
            // Sin llegar: vale si llega o si mejora claramente los fotogramas fuera de tiempo.
            if (!holds && (ok || r.Total.MissedPercent < baseline.MissedPercent * 0.7)) passed.Add(c);
        }

        // Todas las pruebas clavadas a la mitad del refresco = Smart Smoothing ha fijado el ritmo y no deja ver
        // diferencias (17/11/2026: 45 FPS exactos en las 8 pruebas a 90 Hz). No se concluye nada.
        var locked = !holds && tests.All(t => LockedAtHalf(t.Result.Total, hz));
        if (locked)
        {
            passed.Clear();
            progress?.Report($"Todas las pruebas quedaron fijas a {hz / 2} FPS (la mitad): Smart Smoothing no deja ver diferencias.");
        }

        // ---- 2.º arranque: combinaciones (las de subir, de más a menos; las de bajar, de menos a más).
        List<(DcsSetting Setting, int From, int To, double Score)> chosen = [];
        if (passed.Count == 1)
            chosen = passed;   // ya medido por separado
        else if (passed.Count > 0)
        {
            var combos = holds
                ? Enumerable.Range(1, passed.Count).Reverse().Take(4).Select(n => passed.Take(n).ToList()).ToList()
                : Enumerable.Range(1, passed.Count).Take(4).Select(n => passed.Take(n).ToList()).ToList();
            progress?.Report($"Arranque 2: probando {combos.Count} combinación(es) de los cambios que han valido.");
            var comboTrials = combos.Select((combo, i) =>
            {
                var set = Base();
                foreach (var c in combo) set = With(set, c.Setting, c.To);
                return new BenchTrial($"combo{i}", set);
            }).ToList();
            var r2 = bench.MeasureTrials(null, comboTrials, vr: true, headset.RefreshHz, progress, cancel, sceneSeconds: SceneSeconds);
            for (var i = 0; i < combos.Count; i++)
            {
                var ok = Holds(r2[i].Total, hz);
                tests.Add(new("Juntos: " + string.Join(", ", combos[i].Select(c => $"{c.Setting.Label} {c.Setting.Levels[c.To].Name}")), r2[i], ok));
                if (ok && chosen.Count == 0) chosen = combos[i];   // subir: la más grande que llega; bajar: la más pequeña
            }
            if (chosen.Count == 0 && !holds) chosen = combos[^1];  // no llega con ninguna: al menos la que más mejora
        }

        // ---- Recomendaciones, consejos y perfil.
        List<Recommendation> changes = [.. chosen.Select(c => new Recommendation(c.Setting.Key, c.Setting.Label,
            c.Setting.Levels[c.From].Name, c.Setting.Levels[c.To].Name,
            holds ? $"Cabe a {hz} Hz y se ve mejor: {c.Setting.Why.ToLowerInvariant()}."
                  : $"Libera GPU con poca pérdida visual ({c.Setting.Why.ToLowerInvariant()})."))];
        var notes = Notes(current, hz, holds, ups.Count, passed.Count, chosen, tests);
        if (locked)
            notes.Insert(0, $"NO CONCLUYENTE: todas las pruebas quedaron fijas a {hz / 2} FPS, la mitad de {hz} Hz. Smart Smoothing de Pimax " +
                            "bloquea el ritmo cuando DCS no llega y así todas las configuraciones salen iguales. Desactívalo en Pimax Play " +
                            "mientras optimizas (vuelve a activarlo después) y repite.");
        var settings = new Dictionary<string, string>(current, StringComparer.Ordinal);
        foreach (var c in chosen) settings[c.Setting.Key] = c.Setting.Levels[c.To].Value;
        var profile = new GraphicsProfile
        {
            Name = $"Óptimo VR {hz} Hz ({DateTime.Now:dd-MM HH.mm})",
            Sim = "dcs",
            Vr = true,
            Notes = (locked ? "NO CONCLUYENTE: Smart Smoothing fijó todas las pruebas a la mitad del refresco; repetir sin él. "
                   : changes.Count == 0 ? (holds ? "Sin cambios: ninguna subida probada cabe; tu configuración es la buena para este refresco. "
                                                 : "Sin cambios: NO llega al refresco y ningún ajuste probado mejora lo bastante (mira los consejos). ")
                                        : "Cambios: " + string.Join("; ", changes.Select(c => $"{c.Label} {c.From} → {c.To}")) + ". ") +
                    $"Optimizado en VR a {hz} Hz con la misión de prueba.",
            Settings = settings,
        };
        return new OptimizeReport(hz, holds, baseline, tests, changes, notes, profile);
    }

    private List<string> Notes(Dictionary<string, string> current, int hz, bool holds, int triedUp, int passed,
                               List<(DcsSetting Setting, int From, int To, double Score)> chosen, List<OptimizerTest> tests)
    {
        List<string> notes = [];
        var pimax = PimaxInfo.LastSession(logDir: bench.PimaxLogDir);
        if (holds && triedUp > 0 && passed == triedUp && hz < 120)
            notes.Add($"Todas las subidas probadas caben a {hz} Hz: hay margen. Si prefieres fluidez, prueba {(hz == 72 ? 90 : 120)} Hz en Pimax Play y vuelve a optimizar.");
        if (holds && passed == 0 && triedUp > 0)
            notes.Add($"Ninguna subida cabe a {hz} Hz: estás en el límite; tu configuración es la buena para este refresco.");
        if (!holds && !tests.Skip(1).Any(t => t.Holds))
            notes.Add($"Ni bajando un ajuste se llega a {hz} Hz: baja el modo de DLSS (p. ej. Quality → Balanced), la resolución en Pimax Play o el refresco.");
        if (pimax is { SmartSmoothing: true })
            notes.Add("Smart Smoothing está activo en Pimax Play: si DCS no llega al refresco, el visor lo fija a la mitad y reproyecta (se nota en movimientos rápidos).");
        if (pimax is { GpuOver80Percent: > 30 })
            notes.Add($"En la última sesión la GPU pasó el {pimax.GpuOver80Percent:0} % del tiempo por encima del 80 % (dato de Pimax): vas justo de GPU.");
        foreach (var s in DcsSettingsCatalog.All.Where(s => s.Restart))
        {
            var i = s.IndexOf(current.TryGetValue(s.Key, out var v) ? v : null);
            if (i >= 0 && i < s.Levels.Length - 1)
                notes.Add($"{s.Label} en {s.Levels[i].Name}: no se prueba aquí (DCS necesita reiniciar). Para comprobarlo, guarda un perfil con {s.Levels[^1].Name} y mídelo.");
        }
        if (current.TryGetValue("graphics.Upscaling", out var up) && LuaOptionsFile.Display(up) == "DLSS")
            notes.Add($"DLSS en modo {DlssName(current)}: tampoco se prueba sin reiniciar. Para comparar con otro modo, guarda un perfil con ese modo y mídelo.");
        return notes;
    }

    private static string DlssName(Dictionary<string, string> s) =>
        s.TryGetValue("graphics.DLSS_PerfQuality", out var q) ? LuaOptionsFile.Display(q) switch
        {
            "0" => "Ultra Performance", "1" => "Quality", "2" => "Balanced", "3" => "Performance", var x => x,
        } : "?";

    private static Dictionary<string, string> With(Dictionary<string, string> set, DcsSetting s, int level)
    {
        var r = new Dictionary<string, string>(set, StringComparer.Ordinal) { [s.Key] = s.Levels[level].Value };
        return r;
    }

    public static string Describe(OptimizeReport r)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Refresco del visor: {r.RefreshHz} Hz. Tu configuración: {r.Baseline.AvgFps.ToString("0.0", CultureInfo.CurrentCulture)} FPS, " +
                      $"{r.Baseline.MissedPercent:0.0} % fuera de tiempo → {(r.BaselineHolds ? "llega" : "no llega")}.");
        sb.AppendLine(r.Changes.Count == 0 ? "Recomendación: dejarla como está." : "Recomendación:");
        foreach (var c in r.Changes) sb.AppendLine($"  • {c.Label}: {c.From} → {c.To}. {c.Reason}");
        foreach (var n in r.Notes) sb.AppendLine($"  ℹ {n}");
        return sb.ToString().TrimEnd();
    }
}

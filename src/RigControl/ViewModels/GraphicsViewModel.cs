using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using RigControl.Core.Graphics;

namespace RigControl.ViewModels;

/// <summary>Un perfil en la lista de la pantalla Gráficos.</summary>
public sealed class GraphicsProfileItem(GraphicsProfile model, int differences)
{
    public GraphicsProfile Model { get; } = model;
    public string Name => Model.Name;
    public string Detail => $"{Model.SimName} · {(Model.Vr ? "VR" : "monitor")} · {Model.Settings.Count} ajustes" +
                            (differences < 0 ? "" : differences == 0 ? " · aplicado" : $" · {differences} distintos");
}

/// <summary>Una medición en la tabla.</summary>
public sealed class MeasurementRow(BenchResult r)
{
    public string When => r.When.ToString("dd/MM HH:mm");
    public string Mode => r.Vr ? $"VR {r.RefreshHz} Hz" : "monitor";
    public string Profile => r.Profile;
    public string Avg => r.Total.AvgFps.ToString("0.0");
    public string Low => r.Total.P1LowFps.ToString("0.0");
    public string Missed => r.Vr ? $"{r.Total.MissedPercent:0.0} %" : "—";
    public int Stutters => r.Total.Stutters;
    public string PerScene => string.Join(" · ", r.Scenes.Select(s => $"{s.Name} {s.Stats.AvgFps:0}" + (r.Vr ? $" ({s.Stats.MissedPercent:0} %)" : "")));
}

/// <summary>
/// Pantalla Gráficos: perfiles de ajustes gráficos de DCS y MSFS 2024 (graficos.json), revisión para VR de lo
/// que hay ahora y de cada perfil, diferencias con lo actual, y aplicar (simulador cerrado, con copia .bak).
/// </summary>
public sealed class GraphicsViewModel : ObservableObject, IDisposable
{
    private readonly GraphicsManager _manager = new();
    private GraphicsFile _file = new();
    private bool _fileOk = true;
    private GraphicsProfileItem? _selected;
    private string _message = "";
    private string _newName = "";
    private string _newSim = "DCS World";
    private bool _newVr = true;
    private bool _busy;

    public ObservableCollection<GraphicsProfileItem> Profiles { get; } = [];
    public ObservableCollection<GraphicsChange> Changes { get; } = [];
    public ObservableCollection<GraphicsAdvice> ProfileAdvice { get; } = [];
    public ObservableCollection<string> CurrentReview { get; } = [];

    // ------------------------------------------------------------ medición de DCS
    public const string CurrentSettings = "(ajustes actuales)";
    private string _measureChoice = CurrentSettings;
    private bool _measureVr = true;
    private bool _measuring;
    private CancellationTokenSource? _measureCts;

    public ObservableCollection<string> MeasureOptions { get; } = [CurrentSettings];
    public ObservableCollection<string> MeasureLog { get; } = [];
    public ObservableCollection<MeasurementRow> Measurements { get; } = [];

    public string MeasureChoice { get => _measureChoice; set { if (value is not null && Set(ref _measureChoice, value)) Raise(nameof(MeasureIndex)); } }

    /// <summary>Para el desplegable (por posición: no depende del orden en que WPF activa los enlaces).</summary>
    public int MeasureIndex
    {
        get => Math.Max(0, MeasureOptions.IndexOf(_measureChoice));
        set { if (value >= 0 && value < MeasureOptions.Count) MeasureChoice = MeasureOptions[value]; }
    }
    public bool MeasureVr { get => _measureVr; set => Set(ref _measureVr, value); }

    public bool IsMeasuring
    {
        get => _measuring;
        private set
        {
            if (!Set(ref _measuring, value)) return;
            Raise(nameof(NotMeasuring));
            Raise(nameof(CanMeasure));
            Raise(nameof(CanApply));
            Raise(nameof(CanCapture));
        }
    }

    public bool NotMeasuring => !_measuring;
    public bool CanMeasure => !_measuring && !_busy;

    public string MeasureSummary => $"DCS: {DcsBenchmark.FindDcsExe() ?? "(no se encuentra DCS.exe)"}";

    /// <summary>
    /// Una medición con DcsBenchmark en otro hilo; el progreso sale en MeasureLog. DCS abierto, VR que no arranca,
    /// sin resultado... = mensaje; en todos los casos la configuración de DCS queda como estaba.
    /// </summary>
    public async Task MeasureAsync()
    {
        if (!CanMeasure) return;
        var exe = DcsBenchmark.FindDcsExe();
        var saved = DcsBenchmark.FindSavedGames();
        if (exe is null || saved is null)
        {
            Message = $"No se encuentra DCS.exe ({exe ?? "añade un paso con DCS.exe en el Hangar"}) o su carpeta de Saved Games.";
            return;
        }
        var profile = _measureChoice == CurrentSettings ? null : _file.Find(_measureChoice);
        var vr = _measureVr;
        var refresh = _file.Headset.RefreshHz;
        IsMeasuring = true;
        MeasureLog.Clear();
        _measureCts = new CancellationTokenSource();
        var token = _measureCts.Token;
        var progress = new Progress<string>(m => MeasureLog.Add($"{DateTime.Now:HH:mm:ss}  {m}"));
        ((IProgress<string>)progress).Report($"Midiendo {(profile is null ? "los ajustes actuales" : $"«{profile.Name}»")} {(vr ? $"en VR ({refresh} Hz)" : "en el monitor")}.");
        try
        {
            var r = await Task.Run(() => new DcsBenchmark(saved, exe).Measure(profile, vr, refresh, progress, token), token);
            MeasureLog.Add($"{DateTime.Now:HH:mm:ss}  Terminado: {r.Total.AvgFps:0.0} FPS de media" +
                           (vr ? $", {r.Total.MissedPercent:0.0} % fuera de tiempo." : "."));
            Message = "Medición guardada. Tu configuración de DCS está como antes.";
        }
        catch (OperationCanceledException)
        {
            MeasureLog.Add($"{DateTime.Now:HH:mm:ss}  Cancelado: DCS cerrado y configuración restaurada.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or TimeoutException or IOException
                                       or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            MeasureLog.Add($"{DateTime.Now:HH:mm:ss}  {ex.Message}");
            Message = "La medición no se pudo hacer; tu configuración de DCS está como antes.";
        }
        finally
        {
            _measureCts.Dispose();
            _measureCts = null;
            IsMeasuring = false;
            LoadMeasurements();
            Reload(keep: _selected?.Name);
        }
    }

    public void CancelMeasure() => _measureCts?.Cancel();

    private string _optimizeSummary = "";

    /// <summary>Resultado del último optimizar (recomendaciones y consejos).</summary>
    public string OptimizeSummary { get => _optimizeSummary; private set { if (Set(ref _optimizeSummary, value)) Raise(nameof(HasOptimizeSummary)); } }
    public bool HasOptimizeSummary => _optimizeSummary.Length > 0;

    /// <summary>
    /// Optimizar en VR: uno o dos arranques de DCS con pruebas (VrOptimizer). El perfil recomendado se guarda en
    /// graficos.json SIN aplicarlo; se elige en la lista para revisarlo y aplicarlo.
    /// </summary>
    public async Task OptimizeAsync()
    {
        if (!CanMeasure) return;
        var exe = DcsBenchmark.FindDcsExe();
        var saved = DcsBenchmark.FindSavedGames();
        if (exe is null || saved is null)
        {
            Message = $"No se encuentra DCS.exe ({exe ?? "añade un paso con DCS.exe en el Hangar"}) o su carpeta de Saved Games.";
            return;
        }
        IsMeasuring = true;
        MeasureLog.Clear();
        OptimizeSummary = "";
        _measureCts = new CancellationTokenSource();
        var token = _measureCts.Token;
        var progress = new Progress<string>(m => MeasureLog.Add($"{DateTime.Now:HH:mm:ss}  {m}"));
        try
        {
            var headset = _file.Headset;
            var report = await Task.Run(() => new VrOptimizer(new DcsBenchmark(saved, exe), headset).Run(progress, token), token);
            foreach (var t in report.Tests)
                MeasureLog.Add($"    {(t.Holds ? "✔" : "✘")} {t.Description}: {t.Result.Total.AvgFps:0.0} FPS, {t.Result.Total.MissedPercent:0.0} % fuera de tiempo");
            if (report.Profile is { } profile)
            {
                _file.Profiles.Add(profile);
                Save();
                OptimizeSummary = VrOptimizer.Describe(report) + $"\nGuardado como perfil «{profile.Name}» (sin aplicar): elígelo en la lista, revisa la pestaña Perfil y pulsa Aplicar.";
                Reload(keep: profile.Name);
            }
            else
                OptimizeSummary = VrOptimizer.Describe(report) + "\nSin cambios que recomendar: no se guarda ningún perfil.";
            Message = "Optimización terminada. Tu configuración de DCS está como antes.";
        }
        catch (OperationCanceledException)
        {
            MeasureLog.Add($"{DateTime.Now:HH:mm:ss}  Cancelado: DCS cerrado y configuración restaurada.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or TimeoutException or IOException
                                       or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            MeasureLog.Add($"{DateTime.Now:HH:mm:ss}  {ex.Message}");
            Message = "La optimización no se pudo terminar; tu configuración de DCS está como antes.";
        }
        finally
        {
            _measureCts.Dispose();
            _measureCts = null;
            IsMeasuring = false;
        }
    }

    private void LoadMeasurements()
    {
        Measurements.Clear();
        foreach (var r in DcsBenchmark.LoadResults()) Measurements.Add(new MeasurementRow(r));
    }

    private void RefreshMeasureOptions()
    {
        var names = new List<string> { CurrentSettings };
        names.AddRange(_file.Profiles.Where(p => p.Sim == "dcs").Select(p => p.Name).Order(StringComparer.CurrentCultureIgnoreCase));
        if (names.SequenceEqual(MeasureOptions)) return;
        var keep = _measureChoice;
        // Sin vaciar la lista: la primera opción se queda (si no, el desplegable se queda en blanco).
        while (MeasureOptions.Count > 1) MeasureOptions.RemoveAt(MeasureOptions.Count - 1);
        foreach (var n in names.Skip(1)) MeasureOptions.Add(n);
        _measureChoice = names.Contains(keep) ? keep : CurrentSettings;
        Raise(nameof(MeasureChoice));
        Raise(nameof(MeasureIndex));
    }

    /// <summary>Al cerrar la app a mitad de una medición: cancela (cierra DCS y restaura).</summary>
    public void Dispose() => _measureCts?.Cancel();

    public IReadOnlyList<string> SimNames { get; } = ["DCS World", "MSFS 2024"];
    public IReadOnlyList<int> RefreshRates { get; } = [72, 90, 120];

    public string FilePath => GraphicsFile.DefaultPath;
    public string PathsText => $"DCS: {_manager.DcsConfigDir ?? "(no encontrado)"}\nMSFS: {_manager.MsfsUserCfgPath ?? "(no encontrado)"}";

    public string HeadsetName => _file.Headset.Name;

    public int RefreshHz
    {
        get => _file.Headset.RefreshHz;
        set
        {
            if (_file.Headset.RefreshHz == value || !_fileOk) return;
            _file.Headset.RefreshHz = value;
            Save();
            Raise(nameof(RefreshHz));
            Reload(keep: _selected?.Name);
        }
    }

    public string Message { get => _message; private set => Set(ref _message, value); }
    public string NewName { get => _newName; set { if (Set(ref _newName, value)) Raise(nameof(CanCapture)); } }
    public string NewSim { get => _newSim; set => Set(ref _newSim, value); }
    public bool NewVr { get => _newVr; set => Set(ref _newVr, value); }

    public bool Busy
    {
        get => _busy;
        private set { if (Set(ref _busy, value)) { Raise(nameof(CanCapture)); Raise(nameof(CanApply)); Raise(nameof(CanMeasure)); } }
    }

    public bool CanCapture => _fileOk && !_busy && !_measuring && _newName.Trim().Length > 0;
    public bool CanApply => _fileOk && !_busy && !_measuring && _selected is not null && Changes.Count > 0;
    public bool HasSelection => _selected is not null;
    public bool NoSelection => _selected is null;
    public bool NoChanges => _selected is not null && Changes.Count == 0;

    public GraphicsProfileItem? Selected
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value)) return;
            ShowSelected();
        }
    }

    public string SelectedNotes => _selected?.Model.Notes ?? "";

    public void SetVisible(bool visible)
    {
        if (visible) Reload(keep: _selected?.Name);
    }

    public void Reload(string? keep = null)
    {
        try
        {
            _file = GraphicsFile.Load();
            _fileOk = true;
        }
        catch (JsonException ex)
        {
            _fileOk = false;
            _file = new GraphicsFile();
            Message = $"graficos.json está roto ({ex.Message}). Corrígelo a mano: no se sobrescribe.";
        }
        Raise(nameof(HeadsetName));
        Raise(nameof(RefreshHz));
        Raise(nameof(PathsText));
        Raise(nameof(CanCapture));

        Profiles.Clear();
        foreach (var p in _file.Profiles.OrderBy(p => p.Sim).ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            int diff;
            try { diff = _manager.Diff(p).Count; }
            catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or IOException) { diff = -1; }
            Profiles.Add(new GraphicsProfileItem(p, diff));
        }
        _selected = null;
        Selected = Profiles.FirstOrDefault(p => p.Name.Equals(keep, StringComparison.OrdinalIgnoreCase)) ?? Profiles.FirstOrDefault();
        if (_selected is null) ShowSelected();
        ReviewCurrent();
        RefreshMeasureOptions();
        if (!_measuring) LoadMeasurements();
    }

    /// <summary>Revisión VR de lo que hay ahora en cada simulador.</summary>
    private void ReviewCurrent()
    {
        CurrentReview.Clear();
        foreach (var (sim, name) in new[] { ("dcs", "DCS"), ("msfs", "MSFS") })
        {
            try
            {
                var advice = VrAdvisor.Check(sim, _manager.Current(sim), _file.Headset);
                if (advice.Count == 0) CurrentReview.Add($"✔  {name}: nada que señalar para VR.");
                foreach (var a in advice) CurrentReview.Add($"{(a.Level == AdviceLevel.Aviso ? "⚠" : "ℹ")}  {name}: {a.Text}");
            }
            catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or IOException)
            {
                CurrentReview.Add($"—  {name}: {ex.Message}");
            }
        }
    }

    private void ShowSelected()
    {
        Changes.Clear();
        ProfileAdvice.Clear();
        if (_selected is { } item)
        {
            try
            {
                foreach (var c in _manager.Diff(item.Model)) Changes.Add(c);
            }
            catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or IOException)
            {
                Message = ex.Message;
            }
            if (item.Model.Vr)
                foreach (var a in VrAdvisor.Check(item.Model.Sim, item.Model.Settings, _file.Headset)) ProfileAdvice.Add(a);
        }
        Raise(nameof(SelectedNotes));
        Raise(nameof(HasSelection));
        Raise(nameof(NoSelection));
        Raise(nameof(NoChanges));
        Raise(nameof(CanApply));
    }

    /// <summary>Guarda los ajustes actuales del simulador elegido como perfil (si ya existe con ese nombre, lo sustituye).</summary>
    public void Capture()
    {
        if (!CanCapture) return;
        var sim = _newSim == "MSFS 2024" ? "msfs" : "dcs";
        try
        {
            var name = _newName.Trim();
            var profile = _manager.Capture(sim, name, _newVr);
            _file.Profiles.RemoveAll(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            _file.Profiles.Add(profile);
            Save();
            NewName = "";
            Message = $"Guardado «{name}»: {profile.Settings.Count} ajustes de {profile.SimName}{(profile.Vr ? " (VR)" : "")}.";
            Reload(keep: name);
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or IOException)
        {
            Message = ex.Message;
        }
    }

    public bool Exists(string name) => _file.Profiles.Any(p => p.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));

    public async Task ApplyAsync()
    {
        if (!CanApply || _selected is not { } item) return;
        Busy = true;
        Message = $"Aplicando «{item.Name}»…";
        try
        {
            var changes = await Task.Run(() => _manager.Apply(item.Model));
            Message = $"«{item.Name}» aplicado: {changes.Count} cambio(s). Copia del archivo anterior al lado (.rigcontrol-….bak).";
            Reload(keep: item.Name);
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            Message = ex.Message;
        }
        finally
        {
            Busy = false;
        }
    }

    public void DeleteSelected()
    {
        if (_selected is not { } item || !_fileOk) return;
        _file.Profiles.Remove(item.Model);
        Save();
        Message = $"Borrado «{item.Name}».";
        Reload();
    }

    private void Save()
    {
        if (!_fileOk) return;
        try
        {
            _file.Save();
        }
        catch (IOException ex)
        {
            Message = $"No se pudo guardar graficos.json: {ex.Message}";
        }
    }
}

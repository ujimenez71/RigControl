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

/// <summary>
/// Pantalla Gráficos: perfiles de ajustes gráficos de DCS y MSFS 2024 (graficos.json), revisión para VR de lo
/// que hay ahora y de cada perfil, diferencias con lo actual, y aplicar (simulador cerrado, con copia .bak).
/// </summary>
public sealed class GraphicsViewModel : ObservableObject
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
        private set { if (Set(ref _busy, value)) { Raise(nameof(CanCapture)); Raise(nameof(CanApply)); } }
    }

    public bool CanCapture => _fileOk && !_busy && _newName.Trim().Length > 0;
    public bool CanApply => _fileOk && !_busy && _selected is not null && Changes.Count > 0;
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

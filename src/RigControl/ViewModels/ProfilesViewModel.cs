using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Data;
using System.Windows.Threading;
using RigControl.Core;
using RigControl.Core.Bindings;

namespace RigControl.ViewModels;

/// <summary>
/// Pantalla Perfiles: lista los perfiles de la carpeta y muestra de cada uno sus bindings, luces,
/// dispositivos y lo que dice el Conflict Guard; se actualiza sola al guardar (vigila la carpeta
/// mientras está visible). "Editar" abre el editor gráfico (ProfileEditorViewModel).
/// </summary>
public sealed class ProfilesViewModel : ObservableObject, IDisposable
{
    private readonly DispatcherTimer _timer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
    private FileWatch? _watch;
    private ProfileReport? _selected;
    private string _filter = "";
    private string _summary = "";
    private ICollectionView? _bindingsView;

    public ProfilesViewModel()
    {
        // Lista agrupada por simulador (MSFS, DCS, general) y por nombre de archivo dentro de cada uno.
        ProfilesView = CollectionViewSource.GetDefaultView(Profiles);
        ProfilesView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ProfileReport.SimName)));
        ProfilesView.SortDescriptions.Add(new SortDescription(nameof(ProfileReport.SimOrder), ListSortDirection.Ascending));
        ProfilesView.SortDescriptions.Add(new SortDescription(nameof(ProfileReport.FileName), ListSortDirection.Ascending));
        _timer.Tick += (_, _) =>
        {
            if (_watch?.Due(0) == true) Refresh();
        };
    }

    public string ProfilesDir => ProfileFile.ProfilesDir;

    // ------------------------------------------------------------ editor

    private IntPtr _window;
    private ProfileEditorViewModel? _editor;

    /// <summary>La ventana, para abrir los mandos (DirectInput) al capturar entradas.</summary>
    public void Initialize(IntPtr window) => _window = window;

    /// <summary>Editor abierto (null = vista normal de solo lectura).</summary>
    public ProfileEditorViewModel? Editor
    {
        get => _editor;
        private set
        {
            if (!Set(ref _editor, value)) return;
            Raise(nameof(IsEditing));
            Raise(nameof(IsViewing));
            Raise(nameof(ShowDetail));
        }
    }

    public bool IsEditing => _editor is not null;
    public bool IsViewing => _editor is null;
    public bool ShowDetail => _editor is null && _selected is not null;

    /// <summary>Abre el perfil seleccionado en el editor. Devuelve un mensaje si no se puede.</summary>
    public string? BeginEdit()
    {
        if (_selected is null) return null;
        ProfileFile file;
        try
        {
            file = ProfileFile.Load(_selected.Path);
        }
        catch (Exception ex)
        {
            return $"No se puede abrir en el editor porque el JSON no es válido ({ex.Message}). Corrígelo con \"Abrir en el editor\" (de texto).";
        }
        var editor = new ProfileEditorViewModel(_selected.Path, file, _window);
        editor.Closed += _ =>
        {
            editor.Dispose();
            Editor = null;
            Refresh();
        };
        Editor = editor;
        return null;
    }

    /// <summary>¿Tiene el JSON comentarios // que se perderían al guardar desde el editor?</summary>
    public bool SelectedHasComments()
    {
        if (_selected is null || !File.Exists(_selected.Path)) return false;
        return File.ReadLines(_selected.Path).Any(l => l.TrimStart().StartsWith("//", StringComparison.Ordinal));
    }

    /// <summary>Crea "nuevo-perfil.json" (o -2, -3...) vacío y lo abre en el editor.</summary>
    public void NewProfile()
    {
        Directory.CreateDirectory(ProfilesDir);
        var path = Path.Combine(ProfilesDir, "nuevo-perfil.json");
        for (var n = 2; File.Exists(path); n++) path = Path.Combine(ProfilesDir, $"nuevo-perfil-{n}.json");
        new ProfileFile { Name = "Nuevo perfil", Sim = "msfs" }.Save(path);
        Refresh();
        Selected = Profiles.FirstOrDefault(p => p.Path == path);
        BeginEdit();
    }

    /// <summary>Borra el perfil seleccionado (queda una copia en .json.bak).</summary>
    public void DeleteSelected()
    {
        if (_selected is null) return;
        File.Copy(_selected.Path, _selected.Path + ".bak", overwrite: true);
        File.Delete(_selected.Path);
        Refresh();
    }

    public ObservableCollection<ProfileReport> Profiles { get; } = [];
    public ICollectionView ProfilesView { get; }

    public ProfileReport? Selected
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value)) return;
            BindingsView = value is null ? null : CollectionViewSource.GetDefaultView(value.Bindings);
            if (BindingsView is not null) BindingsView.Filter = FilterBinding;
            Raise(nameof(HasSelection));
            Raise(nameof(ShowDetail));
            Raise(nameof(AircraftText));
            Raise(nameof(ErrorsText));
            Raise(nameof(HasErrors));
            Raise(nameof(FindingsHeader));
            Raise(nameof(BindingsHeader));
            Raise(nameof(LightsHeader));
            Raise(nameof(DevicesHeader));
            Raise(nameof(HasNotes));
        }
    }

    public bool HasSelection => _selected is not null;

    public ICollectionView? BindingsView { get => _bindingsView; private set => Set(ref _bindingsView, value); }

    /// <summary>Filtra la tabla de bindings por disparador, condición o acción.</summary>
    public string Filter
    {
        get => _filter;
        set
        {
            if (Set(ref _filter, value)) BindingsView?.Refresh();
        }
    }

    public string Summary { get => _summary; private set => Set(ref _summary, value); }

    public bool HasNotes => !string.IsNullOrWhiteSpace(_selected?.Notes);

    public string AircraftText => _selected is null ? ""
        : $"{_selected.SimName} · " + (_selected.Aircraft.Length == 0 ? "sin \"aircraft\": solo se usa eligiéndolo a mano"
        : $"se activa con: {string.Join(", ", _selected.Aircraft)}");

    public bool HasErrors => _selected?.Errors.Count > 0;
    public string ErrorsText => _selected is null ? "" : string.Join("\n", _selected.Errors);

    public string BindingsHeader => $"Bindings ({_selected?.Bindings.Count ?? 0})";
    public string LightsHeader => $"Luces del Bravo ({_selected?.Lights.Count ?? 0})";
    public string DevicesHeader => $"Dispositivos ({_selected?.Devices.Count ?? 0})";
    public string FindingsHeader => _selected is null ? "Conflict Guard"
        : $"Conflict Guard ({_selected.Findings.Count})";

    /// <summary>Se llama al mostrar u ocultar la pantalla: solo vigila la carpeta mientras se ve.</summary>
    public void SetVisible(bool visible)
    {
        if (visible)
        {
            Directory.CreateDirectory(ProfilesDir);
            _watch ??= new FileWatch(Path.Combine(ProfilesDir, "x.json"), "*.json");
            _timer.Start();
            Refresh();
        }
        else
        {
            _timer.Stop();
            _watch?.Dispose();
            _watch = null;
        }
    }

    /// <summary>Vuelve a leer todos los perfiles, conservando el seleccionado.</summary>
    public void Refresh()
    {
        var keep = _selected?.Path;
        var catalog = DeviceCatalog.Load();
        var connected = ConnectedKeys();

        Profiles.Clear();
        foreach (var path in Directory.EnumerateFiles(ProfilesDir, "*.json").Order(StringComparer.OrdinalIgnoreCase))
            Profiles.Add(ProfileInspector.Inspect(path, catalog, connected));

        Selected = Profiles.FirstOrDefault(p => p.Path == keep) ?? ProfilesView.Cast<ProfileReport>().FirstOrDefault();
        var bad = Profiles.Count(p => !p.Ok);
        Summary = $"{Profiles.Count} perfil(es)" + (bad > 0 ? $", {bad} con errores (run los ignora)" : "");
    }

    /// <summary>Abre el perfil seleccionado con el programa asociado a .json (o el Bloc de notas).</summary>
    public void OpenSelected()
    {
        if (_selected is null) return;
        try
        {
            Process.Start(new ProcessStartInfo(_selected.Path) { UseShellExecute = true });
        }
        catch (Exception)
        {
            Process.Start("notepad.exe", $"\"{_selected.Path}\"");
        }
    }

    public void OpenFolder() =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{ProfilesDir}\"") { UseShellExecute = true });

    private bool FilterBinding(object o)
    {
        if (string.IsNullOrWhiteSpace(_filter) || o is not BindingReport b) return true;
        var f = _filter.Trim();
        return b.Trigger.Contains(f, StringComparison.OrdinalIgnoreCase)
            || b.InputText.Contains(f, StringComparison.OrdinalIgnoreCase)
            || b.GestureText.Contains(f, StringComparison.OrdinalIgnoreCase)
            || b.Action.Contains(f, StringComparison.OrdinalIgnoreCase)
            || b.Description.Contains(f, StringComparison.OrdinalIgnoreCase)
            || b.Condition.Contains(f, StringComparison.OrdinalIgnoreCase)
            || b.Gesture.Contains(f, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Claves VID:PID de lo conectado ahora. Si DirectInput falla, null (= "no se sabe").</summary>
    private static HashSet<string>? ConnectedKeys()
    {
        try
        {
            using var di = Vortice.DirectInput.DInput.DirectInput8Create();
            return DeviceScanner.Scan(di).Select(d => d.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public void Dispose()
    {
        SetVisible(false);
        _editor?.Dispose();
    }
}

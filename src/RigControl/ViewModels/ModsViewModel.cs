using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Threading;
using RigControl.Core.Mods;

namespace RigControl.ViewModels;

/// <summary>Un mod de la biblioteca elegida, con su casilla de activo, "siempre" y "en el conjunto".</summary>
public sealed class ModItem(ModsViewModel owner, ModEntry entry) : ObservableObject
{
    private ModEntry _entry = entry;
    private bool _always;
    private bool _inSet;

    public string Name => _entry.Name;
    public ModState State => _entry.State;
    public string? Detail => _entry.Detail;

    public string StateText => _entry.State switch
    {
        ModState.Active => "Activo",
        ModState.Inactive => "Inactivo",
        ModState.Foreign => _entry.Detail is null ? "Suelto (sin importar)" : "De otro programa",
        ModState.Conflict => "Conflicto",
        _ => "Enlace roto",
    };

    public bool IsProblem => _entry.State is ModState.Conflict or ModState.Broken;

    /// <summary>Solo los de la biblioteca se activan y desactivan; con el simulador abierto, nada.</summary>
    public bool CanToggle => _entry.State is ModState.Active or ModState.Inactive && !owner.IsLocked;
    public bool InLibrary => _entry.State is ModState.Active or ModState.Inactive or ModState.Conflict;

    public bool IsActive
    {
        get => _entry.State == ModState.Active;
        set { if (value != IsActive) owner.Toggle(this, value); }
    }

    public bool Always
    {
        get => _always;
        set { if (Set(ref _always, value)) owner.SetAlways(this, value); }
    }

    public bool InSet
    {
        get => _inSet;
        set { if (Set(ref _inSet, value)) owner.SetInSet(this, value); }
    }

    /// <summary>Sin avisar al dueño (al cargar).</summary>
    internal void Init(bool always, bool inSet) { _always = always; _inSet = inSet; }

    internal void Update(ModEntry entry, bool always, bool inSet)
    {
        _entry = entry;
        _always = always;
        _inSet = inSet;
        foreach (var p in new[] { nameof(State), nameof(Detail), nameof(StateText), nameof(IsProblem), nameof(CanToggle), nameof(InLibrary),
                                  nameof(IsActive), nameof(Always), nameof(InSet) })
            Raise(p);
    }
}

/// <summary>
/// Pantalla Mods: bibliotecas de mods.json, sus mods (activar = unión en el destino) y conjuntos.
/// Los cambios de mods.json se guardan al momento; si el archivo está roto se muestra el error y no
/// se toca. Con el simulador abierto no se cambia ningún mod de su biblioteca.
/// </summary>
public sealed class ModsViewModel : ObservableObject
{
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly ModManager _manager = new();
    private ModsFile _file = new();
    private bool _fileOk = true;
    private DateTime _fileTime;
    private ModLibrary? _selectedLibrary;
    private ModSet? _selectedSet;
    private bool _isLocked;
    private bool _isBusy;
    private string _message = "";
    private string _newSetName = "";

    public ModsViewModel()
    {
        _refreshTimer.Tick += (_, _) => { ReloadIfChangedOnDisk(); Refresh(); };
        Load();
    }

    public ObservableCollection<ModLibrary> Libraries { get; } = [];
    public ObservableCollection<ModSet> Sets { get; } = [];
    public ObservableCollection<ModItem> Mods { get; } = [];

    public ModLibrary? SelectedLibrary
    {
        get => _selectedLibrary;
        set
        {
            if (!Set(ref _selectedLibrary, value)) return;
            Mods.Clear();
            Refresh();
            RaiseLibrary();
        }
    }

    public ModSet? SelectedSet
    {
        get => _selectedSet;
        set
        {
            if (!Set(ref _selectedSet, value)) return;
            Raise(nameof(HasSet));
            Raise(nameof(InSetHeader));
            Refresh();
        }
    }

    public bool HasLibrary => _selectedLibrary is not null;
    public bool HasSet => _selectedSet is not null;
    public bool IsLocked { get => _isLocked; private set { if (Set(ref _isLocked, value)) { Raise(nameof(CanChange)); Raise(nameof(LockText)); } } }
    public bool IsBusy { get => _isBusy; private set { if (Set(ref _isBusy, value)) Raise(nameof(CanChange)); } }
    public bool CanChange => _fileOk && !_isLocked && !_isBusy;
    public bool CanEdit => _fileOk && !_isBusy;
    public string LockText => _isLocked && _selectedLibrary is { } lib ? $"{lib.Process} está abierto: ciérralo para cambiar los mods de {lib.Name}." : "";
    public string Message { get => _message; private set => Set(ref _message, value); }
    public string FilePath => ModsFile.DefaultPath;
    public string NewSetName { get => _newSetName; set => Set(ref _newSetName, value); }

    public string LibraryText => _selectedLibrary is { } lib
        ? $"Biblioteca: {ModManager.Expand(lib.Library)}\nDestino: {ModManager.Expand(lib.Target)}"
        : "";

    public string InSetHeader => _selectedSet is { } set ? $"En «{set.Name}»" : "En el conjunto";

    public int LooseCount => Mods.Count(m => m.State == ModState.Foreign && m.Detail is null);
    public string ImportText => $"Importar los sueltos ({LooseCount})";
    public string Summary => $"{Mods.Count(m => m.IsActive)} activos de {Mods.Count(m => m.InLibrary)} en la biblioteca" +
                             (LooseCount > 0 ? $"; {LooseCount} sueltos sin importar" : "");

    public void SetVisible(bool visible)
    {
        if (visible)
        {
            ReloadIfChangedOnDisk();
            Refresh();
            _refreshTimer.Start();
        }
        else _refreshTimer.Stop();
    }

    /// <summary>Vuelve a leer las carpetas y el estado del simulador.</summary>
    public void Refresh()
    {
        if (_selectedLibrary is not { } lib) return;
        IReadOnlyList<ModEntry> entries;
        try
        {
            IsLocked = _manager.IsLocked(lib);
            entries = _manager.Scan(lib);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Message = $"No se pudo leer {lib.Name}: {ex.Message}";
            return;
        }
        var setMods = SetModsOf(lib, create: false);
        var byName = Mods.ToDictionary(m => m.Name, StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<string>(entries.Select(e => e.Name), StringComparer.OrdinalIgnoreCase);
        for (var i = Mods.Count - 1; i >= 0; i--)
            if (!names.Contains(Mods[i].Name)) Mods.RemoveAt(i);
        for (var i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            var always = lib.Always.Contains(e.Name, StringComparer.OrdinalIgnoreCase);
            var inSet = setMods?.Contains(e.Name, StringComparer.OrdinalIgnoreCase) == true;
            if (byName.TryGetValue(e.Name, out var item))
            {
                item.Update(e, always, inSet);
                var at = Mods.IndexOf(item);
                if (at != i) Mods.Move(at, i);
            }
            else
            {
                item = new ModItem(this, e);
                item.Init(always, inSet);
                Mods.Insert(i, item);
            }
        }
        Raise(nameof(LooseCount));
        Raise(nameof(ImportText));
        Raise(nameof(Summary));
    }

    internal void Toggle(ModItem item, bool active)
    {
        if (_selectedLibrary is not { } lib) return;
        try
        {
            if (active) _manager.Enable(lib, item.Name);
            else _manager.Disable(lib, item.Name);
            Message = $"{item.Name}: {(active ? "activado" : "desactivado")}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Message = $"{item.Name}: no se pudo {(active ? "activar" : "desactivar")}: {ex.Message}";
        }
        Refresh();
    }

    internal void SetAlways(ModItem item, bool always)
    {
        if (_selectedLibrary is not { } lib) return;
        lib.Always.RemoveAll(n => n.Equals(item.Name, StringComparison.OrdinalIgnoreCase));
        if (always) lib.Always.Add(item.Name);
        lib.Always.Sort(StringComparer.OrdinalIgnoreCase);
        Save();
    }

    internal void SetInSet(ModItem item, bool inSet)
    {
        if (_selectedLibrary is not { } lib || _selectedSet is null) return;
        var mods = SetModsOf(lib, create: true)!;
        mods.RemoveAll(n => n.Equals(item.Name, StringComparison.OrdinalIgnoreCase));
        if (inSet) mods.Add(item.Name);
        mods.Sort(StringComparer.OrdinalIgnoreCase);
        Save();
    }

    /// <summary>Mueve a la biblioteca todo lo suelto del destino (sigue activo). Misma unidad: es renombrar.</summary>
    public async void ImportLoose()
    {
        if (_selectedLibrary is not { } lib || !CanChange) return;
        var names = Mods.Where(m => m.State == ModState.Foreign && m.Detail is null).Select(m => m.Name).ToList();
        if (names.Count == 0) return;
        IsBusy = true;
        Message = $"Importando {names.Count} mods…";
        var failures = await Task.Run(() =>
        {
            var errors = new List<string>();
            foreach (var name in names)
            {
                try { _manager.Import(lib, name); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    errors.Add($"{name}: {ex.Message}");
                }
            }
            return errors;
        });
        IsBusy = false;
        Message = failures.Count == 0
            ? $"Importados {names.Count} mods en {ModManager.Expand(lib.Library)}. Siguen activos."
            : $"Importados {names.Count - failures.Count} de {names.Count}. No se pudo: {string.Join("; ", failures)}";
        Refresh();
    }

    public void ApplySelectedSet()
    {
        if (_selectedSet is not { } set || !_fileOk) return;
        var changes = _manager.ApplySet(_file, set);
        var errors = changes.Where(c => c.Error is not null).ToList();
        Message = changes.Count == 0
            ? $"«{set.Name}»: ya estaba así."
            : $"«{set.Name}» aplicado: {changes.Count - errors.Count} cambio(s)" +
              (errors.Count == 0 ? "." : $". Problemas: {string.Join("; ", errors.Select(e => e.ToString()))}");
        Refresh();
    }

    /// <summary>Nuevo conjunto con lo que está activo ahora en todas las bibliotecas (sin los "siempre").</summary>
    public void CreateSetFromActive()
    {
        if (!_fileOk) return;
        var name = NewSetName.Trim();
        if (name.Length == 0) { Message = "Escribe un nombre para el conjunto."; return; }
        if (_file.FindSet(name) is not null) { Message = $"Ya hay un conjunto «{name}»."; return; }
        var set = new ModSet { Name = name, Mods = _manager.Snapshot(_file.Libraries) };
        foreach (var empty in set.Mods.Where(kv => kv.Value.Count == 0).Select(kv => kv.Key).ToList()) set.Mods.Remove(empty);
        if (_selectedLibrary is { } lib && !set.Mods.ContainsKey(lib.Name)) set.Mods[lib.Name] = [];
        _file.Sets.Add(set);
        Sets.Add(set);
        NewSetName = "";
        Save();
        SelectedSet = set;
        Message = $"Conjunto «{name}» creado con lo activo ahora. Marca o desmarca la columna «En «{name}»» para cambiarlo.";
    }

    public void RemoveSelectedSet()
    {
        if (_selectedSet is not { } set || !_fileOk) return;
        _file.Sets.Remove(set);
        Sets.Remove(set);
        SelectedSet = null;
        Save();
        Message = $"Conjunto «{set.Name}» borrado (los mods no se tocan).";
    }

    /// <summary>Lista de mods del conjunto elegido para esta biblioteca; null si el conjunto no la incluye.</summary>
    private List<string>? SetModsOf(ModLibrary lib, bool create)
    {
        if (_selectedSet is not { } set) return null;
        var key = set.Mods.Keys.FirstOrDefault(k => k.Equals(lib.Name, StringComparison.OrdinalIgnoreCase));
        if (key is not null) return set.Mods[key];
        if (!create) return null;
        return set.Mods[lib.Name] = [];
    }

    private void RaiseLibrary()
    {
        Raise(nameof(HasLibrary));
        Raise(nameof(LibraryText));
        Raise(nameof(LockText));
    }

    private void Save()
    {
        if (!_fileOk) return;
        try
        {
            _file.Save();
            _fileTime = File.GetLastWriteTimeUtc(ModsFile.DefaultPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Message = $"No se pudo guardar mods.json: {ex.Message}";
        }
    }

    private void Load()
    {
        var libName = _selectedLibrary?.Name;
        var setName = _selectedSet?.Name;
        try
        {
            _file = ModsFile.Load();
            _fileOk = true;
            _fileTime = File.Exists(ModsFile.DefaultPath) ? File.GetLastWriteTimeUtc(ModsFile.DefaultPath) : default;
        }
        catch (Exception ex)
        {
            _file = new ModsFile();
            _fileOk = false;
            _fileTime = File.Exists(ModsFile.DefaultPath) ? File.GetLastWriteTimeUtc(ModsFile.DefaultPath) : default;
            Message = $"mods.json tiene un error; corrígelo a mano (no se sobrescribe): {ex.Message}";
        }
        _selectedLibrary = null;
        _selectedSet = null;
        Libraries.Clear();
        Sets.Clear();
        Mods.Clear();
        foreach (var l in _file.Libraries) Libraries.Add(l);
        foreach (var s in _file.Sets) Sets.Add(s);
        SelectedSet = Sets.FirstOrDefault(s => s.Name == setName);
        SelectedLibrary = Libraries.FirstOrDefault(l => l.Name == libName) ?? Libraries.FirstOrDefault();
        Raise(nameof(CanChange));
        Raise(nameof(CanEdit));
        Raise(nameof(HasSet));
        Raise(nameof(InSetHeader));
    }

    private void ReloadIfChangedOnDisk()
    {
        if (IsBusy) return;
        var time = File.Exists(ModsFile.DefaultPath) ? File.GetLastWriteTimeUtc(ModsFile.DefaultPath) : default;
        if (time != _fileTime) Load();
    }
}

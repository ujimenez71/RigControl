using System.Collections.ObjectModel;
using System.IO;
using RigControl.Core.Snapshots;

namespace RigControl.ViewModels;

/// <summary>Una fila de la lista de instantáneas.</summary>
public sealed class SnapshotItem(SnapshotInfo info)
{
    public SnapshotInfo Info { get; } = info;
    public string When => Info.Created.ToString("dd/MM/yyyy HH:mm:ss");
    public string Kind => Info.Auto ? "Automática" : "Manual";
    public string Name => Info.Name;
    public int Files => Info.FileCount;
    public string Size => Info.Bytes < 1024 * 1024 ? $"{Info.Bytes / 1024.0:N0} KB" : $"{Info.Bytes / 1024.0 / 1024.0:N1} MB";
}

/// <summary>
/// Pantalla Instantáneas: por grupo (DCS, MSFS 2024, RigControl), crear, comparar con lo actual,
/// restaurar (con el programa cerrado; antes se guarda lo actual) y borrar.
/// </summary>
public sealed class SnapshotsViewModel : ObservableObject
{
    private SnapshotsFile _file = new();
    private SnapshotStore _store = new();
    private SnapshotGroup? _selectedGroup;
    private SnapshotItem? _selectedSnapshot;
    private string _newName = "";
    private string _message = "";
    private string _groupText = "";
    private bool _isLocked;
    private bool _isBusy;
    private bool _fileOk = true;

    public SnapshotsViewModel() => Load();

    public ObservableCollection<SnapshotGroup> Groups { get; } = [];
    public ObservableCollection<SnapshotItem> Snapshots { get; } = [];
    public ObservableCollection<string> Differences { get; } = [];

    public SnapshotGroup? SelectedGroup
    {
        get => _selectedGroup;
        set
        {
            if (!Set(ref _selectedGroup, value)) return;
            Raise(nameof(HasGroup));
            Refresh();
        }
    }

    public SnapshotItem? SelectedSnapshot
    {
        get => _selectedSnapshot;
        set
        {
            if (!Set(ref _selectedSnapshot, value)) return;
            Differences.Clear();
            Raise(nameof(HasSnapshot));
            Raise(nameof(CanRestore));
        }
    }

    public bool HasGroup => _selectedGroup is not null;
    public bool HasSnapshot => _selectedSnapshot is not null;
    public bool IsBusy { get => _isBusy; private set { if (Set(ref _isBusy, value)) { Raise(nameof(CanWork)); Raise(nameof(CanRestore)); } } }
    public bool IsLocked { get => _isLocked; private set { if (Set(ref _isLocked, value)) { Raise(nameof(CanRestore)); Raise(nameof(LockText)); } } }
    public bool CanWork => _fileOk && !_isBusy;
    public bool CanRestore => CanWork && !_isLocked && _selectedSnapshot is not null;
    public string LockText => _isLocked && _selectedGroup is { } g ? $"{g.Process} está abierto: se pueden crear instantáneas, pero no restaurar." : "";
    public string GroupText { get => _groupText; private set => Set(ref _groupText, value); }
    public string NewName { get => _newName; set => Set(ref _newName, value); }
    public string Message { get => _message; private set => Set(ref _message, value); }
    public string StoreDir => _store.StoreDir;
    public string FilePath => SnapshotsFile.DefaultPath;

    public void SetVisible(bool visible)
    {
        if (!visible) return;
        Load();
    }

    public void Refresh()
    {
        Snapshots.Clear();
        SelectedSnapshot = null;
        if (_selectedGroup is not { } g) { GroupText = ""; return; }
        try
        {
            IsLocked = _store.IsLocked(g);
            var files = SnapshotStore.Collect(g);
            GroupText = $"{files.Count} archivos ahora en {SnapshotStore.ExpandRoot(g)}" +
                        $"\nIncluye: {string.Join(", ", g.Include)}{(g.Exclude.Count > 0 ? $"   ·   Excluye: {string.Join(", ", g.Exclude)}" : "")}";
            foreach (var s in _store.List(g.Name)) Snapshots.Add(new SnapshotItem(s));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Message = $"No se pudo leer {g.Name}: {ex.Message}";
        }
    }

    public async void Create(bool all)
    {
        if (!CanWork) return;
        var groups = all ? _file.Groups.ToList() : _selectedGroup is { } g ? [g] : [];
        if (groups.Count == 0) return;
        var name = NewName.Trim();
        IsBusy = true;
        var lines = await Task.Run(() => groups.Select(group =>
        {
            try
            {
                var s = _store.Create(group, name, auto: false)!;
                return $"{group.Name}: {s.FileCount} archivos";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                return $"{group.Name}: no se pudo ({ex.Message})";
            }
        }).ToList());
        IsBusy = false;
        NewName = "";
        Message = $"Instantánea creada. {string.Join("; ", lines)}.";
        Refresh();
    }

    public async void Compare()
    {
        if (_selectedGroup is not { } g || _selectedSnapshot is not { } s || !CanWork) return;
        IsBusy = true;
        var diff = await Task.Run(() => _store.Compare(g, s.Info));
        IsBusy = false;
        Differences.Clear();
        foreach (var (rel, state) in diff) Differences.Add($"{state}:  {rel}");
        Message = diff.Count == 0 ? "La instantánea es igual que lo que hay ahora." : $"{diff.Count} diferencia(s) con lo que hay ahora.";
    }

    public async void Restore()
    {
        if (_selectedGroup is not { } g || _selectedSnapshot is not { } s || !CanRestore) return;
        IsBusy = true;
        try
        {
            var r = await Task.Run(() => _store.Restore(g, s.Info));
            Message = $"Restaurados {r.Files} archivos de {g.Name} ({s.When})." +
                      (r.Before is { } b ? $" Lo que había antes está en la automática «{b.Name}»." : " Lo de antes ya estaba en la última instantánea.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException)
        {
            Message = $"No se pudo restaurar: {ex.Message}";
        }
        IsBusy = false;
        Refresh();
    }

    public void Delete()
    {
        if (_selectedSnapshot is not { } s || !CanWork) return;
        try
        {
            _store.Delete(s.Info);
            Message = $"Borrada la instantánea del {s.When}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Message = $"No se pudo borrar: {ex.Message}";
        }
        Refresh();
    }

    public string FolderOfSelectedGroup() =>
        _selectedGroup is { } g ? Path.Combine(_store.StoreDir, g.Name) : _store.StoreDir;

    private void Load()
    {
        var name = _selectedGroup?.Name;
        try
        {
            _file = SnapshotsFile.Load();
            _fileOk = true;
        }
        catch (Exception ex)
        {
            _file = new SnapshotsFile();
            _fileOk = false;
            Message = $"snapshots.json tiene un error; corrígelo a mano: {ex.Message}";
        }
        _store = new SnapshotStore(keepAuto: _file.KeepAuto);
        _selectedGroup = null;
        Groups.Clear();
        foreach (var g in _file.Groups) Groups.Add(g);
        SelectedGroup = Groups.FirstOrDefault(g => g.Name == name) ?? Groups.FirstOrDefault();
        Raise(nameof(CanWork));
        Raise(nameof(CanRestore));
    }
}

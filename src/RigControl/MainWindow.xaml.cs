using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using RigControl.ViewModels;

namespace RigControl;

public partial class MainWindow : Window
{
    private readonly MonitorViewModel _monitor = new();
    private readonly RunViewModel _run = new();
    private readonly ProfilesViewModel _profiles = new();
    private readonly HangarViewModel _hangar = new();
    private readonly ModsViewModel _mods = new();
    private readonly SnapshotsViewModel _snapshots = new();

    public MainWindow()
    {
        InitializeComponent();
        MonitorPage.DataContext = _monitor;
        RunPage.DataContext = _run;
        ProfilesPage.DataContext = _profiles;
        HangarPage.DataContext = _hangar;
        ModsPage.DataContext = _mods;
        SnapshotsPage.DataContext = _snapshots;
        _hangar.StartRunRequested += _run.StartAuto;

        // DirectInput necesita la ventana ya creada para el nivel cooperativo (compartido, en segundo plano).
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            _monitor.Start(hwnd);
            _run.Initialize(hwnd);
            _profiles.Initialize(hwnd);
        };

        // Al cerrar: parar la ejecución (suelta teclas y dispositivos) y cerrar el monitor.
        Closed += (_, _) =>
        {
            _hangar.Dispose();
            _run.Dispose();
            _profiles.Dispose();
            _monitor.Dispose();
        };
    }

    private void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MonitorPage is null || RunPage is null || ProfilesPage is null || HangarPage is null || ModsPage is null || SnapshotsPage is null) return; // durante InitializeComponent
        var page = Nav.SelectedIndex;
        MonitorPage.Visibility = page == 0 ? Visibility.Visible : Visibility.Collapsed;
        RunPage.Visibility = page == 1 ? Visibility.Visible : Visibility.Collapsed;
        ProfilesPage.Visibility = page == 2 ? Visibility.Visible : Visibility.Collapsed;
        HangarPage.Visibility = page == 3 ? Visibility.Visible : Visibility.Collapsed;
        ModsPage.Visibility = page == 4 ? Visibility.Visible : Visibility.Collapsed;
        SnapshotsPage.Visibility = page == 5 ? Visibility.Visible : Visibility.Collapsed;
        _snapshots.SetVisible(page == 5);
        _monitor.SetVisible(page == 0);
        _profiles.SetVisible(page == 2);
        _hangar.SetVisible(page == 3);
        _mods.SetVisible(page == 4);
    }
}

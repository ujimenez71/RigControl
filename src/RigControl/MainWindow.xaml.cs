using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using RigControl.ViewModels;

namespace RigControl;

public partial class MainWindow : Window
{
    private readonly MonitorViewModel _monitor = new();
    private readonly RunViewModel _run = new();

    public MainWindow()
    {
        InitializeComponent();
        MonitorPage.DataContext = _monitor;
        RunPage.DataContext = _run;

        // DirectInput necesita la ventana ya creada para el nivel cooperativo (compartido, en segundo plano).
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            _monitor.Start(hwnd);
            _run.Initialize(hwnd);
        };

        // Al cerrar: parar la ejecución (suelta teclas y dispositivos) y cerrar el monitor.
        Closed += (_, _) =>
        {
            _run.Dispose();
            _monitor.Dispose();
        };
    }

    private void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MonitorPage is null || RunPage is null) return; // durante InitializeComponent
        var page = Nav.SelectedIndex;
        MonitorPage.Visibility = page == 0 ? Visibility.Visible : Visibility.Collapsed;
        RunPage.Visibility = page == 1 ? Visibility.Visible : Visibility.Collapsed;
        _monitor.SetVisible(page == 0);
    }
}

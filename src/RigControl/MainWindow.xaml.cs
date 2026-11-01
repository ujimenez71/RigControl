using System.Windows;
using System.Windows.Interop;
using RigControl.ViewModels;

namespace RigControl;

public partial class MainWindow : Window
{
    private readonly MonitorViewModel _monitor = new();

    public MainWindow()
    {
        InitializeComponent();
        Monitor.DataContext = _monitor;

        // DirectInput necesita la ventana ya creada para el nivel cooperativo (compartido, en segundo plano).
        SourceInitialized += (_, _) => _monitor.Start(new WindowInteropHelper(this).Handle);
        Closed += (_, _) => _monitor.Dispose();
    }
}

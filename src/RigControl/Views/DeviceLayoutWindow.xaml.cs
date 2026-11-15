using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Microsoft.Win32;
using RigControl.ViewModels;

namespace RigControl.Views;

/// <summary>Ventana "Fotos de los mandos": foto de cada mando y posición de sus entradas (para la hoja de mandos).</summary>
public partial class DeviceLayoutWindow : Window
{
    public DeviceLayoutWindow(string? deviceKey = null)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => DataContext = new DeviceLayoutViewModel(new WindowInteropHelper(this).Handle, deviceKey);
        Closed += (_, _) => (DataContext as DeviceLayoutViewModel)?.Dispose();
    }

    private DeviceLayoutViewModel Vm => (DeviceLayoutViewModel)DataContext;

    private void LoadImage_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Foto del mando",
            Filter = "Imágenes (*.jpg;*.jpeg;*.png;*.bmp)|*.jpg;*.jpeg;*.png;*.bmp|Todos los archivos (*.*)|*.*",
        };
        if (dialog.ShowDialog(this) == true) Vm.ImportImage(dialog.FileName);
    }

    private void Capture_Click(object sender, RoutedEventArgs e) => Vm.StartCapture();

    private void Photo_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var p = e.GetPosition(PhotoArea);
        Vm.PlaceAt(p.X, p.Y);
    }

    private void Marker_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is LayoutMarker m) Vm.SelectMarker(m.Part);
        e.Handled = true; // no colocar encima
    }

    private void Marker_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is LayoutMarker m) Vm.RemoveMarker(m.Part);
        e.Handled = true;
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(Vm.FolderPath);
        Process.Start(new ProcessStartInfo(Vm.FolderPath) { UseShellExecute = true });
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}

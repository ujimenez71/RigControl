using System.Windows;
using System.Windows.Controls;
using RigControl.ViewModels;

namespace RigControl.Views;

public partial class MonitorView : UserControl
{
    public MonitorView() => InitializeComponent();

    private MonitorViewModel Vm => (MonitorViewModel)DataContext;

    private void Reopen_Click(object sender, RoutedEventArgs e) => Vm.Reopen();

    private void ClearLog_Click(object sender, RoutedEventArgs e) => Vm.ClearLog();

    /// <summary>Abre «Fotos de los mandos» en el mando elegido; al cerrarla, vuelve a cargar su foto.</summary>
    private void Photos_Click(object sender, RoutedEventArgs e)
    {
        if (Vm.Selected is not { } device) return;
        new DeviceLayoutWindow(device.Key) { Owner = Window.GetWindow(this) }.ShowDialog();
        Vm.ReloadPhoto();
    }
}

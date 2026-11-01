using System.Windows;
using System.Windows.Controls;
using RigControl.ViewModels;

namespace RigControl.Views;

public partial class RunView : UserControl
{
    public RunView() => InitializeComponent();

    private RunViewModel Vm => (RunViewModel)DataContext;

    private void StartStop_Click(object sender, RoutedEventArgs e) => Vm.StartStop();

    private void ClearLog_Click(object sender, RoutedEventArgs e) => Vm.ClearLog();

    /// <summary>Al desplegar, vuelve a leer la carpeta por si hay perfiles nuevos.</summary>
    private void Targets_DropDownOpened(object? sender, EventArgs e) => Vm.RefreshTargets();
}

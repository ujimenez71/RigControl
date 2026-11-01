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
}

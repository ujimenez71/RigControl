using System.Windows;
using System.Windows.Controls;
using RigControl.ViewModels;

namespace RigControl.Views;

public partial class ProfilesView : UserControl
{
    public ProfilesView() => InitializeComponent();

    private ProfilesViewModel Vm => (ProfilesViewModel)DataContext;

    private void Refresh_Click(object sender, RoutedEventArgs e) => Vm.Refresh();

    private void OpenFolder_Click(object sender, RoutedEventArgs e) => Vm.OpenFolder();

    private void OpenSelected_Click(object sender, RoutedEventArgs e) => Vm.OpenSelected();
}

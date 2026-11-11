using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using RigControl.ViewModels;

namespace RigControl.Views;

public partial class HangarView : UserControl
{
    public HangarView() => InitializeComponent();

    private HangarViewModel Vm => (HangarViewModel)DataContext;

    private void Launch_Click(object sender, RoutedEventArgs e) => Vm.LaunchOrCancel();
    private void AddHangar_Click(object sender, RoutedEventArgs e) => Vm.AddHangar();
    private void DuplicateHangar_Click(object sender, RoutedEventArgs e) => Vm.DuplicateHangar();
    private void AddStep_Click(object sender, RoutedEventArgs e) => Vm.AddStep();
    private void RemoveStep_Click(object sender, RoutedEventArgs e) => Vm.RemoveStep();
    private void MoveUp_Click(object sender, RoutedEventArgs e) => Vm.MoveStep(-1);
    private void MoveDown_Click(object sender, RoutedEventArgs e) => Vm.MoveStep(1);
    private void CaptureWindow_Click(object sender, RoutedEventArgs e) => Vm.CaptureWindow();

    private void RemoveHangar_Click(object sender, RoutedEventArgs e)
    {
        if (Vm.SelectedHangar is not { } hangar) return;
        var answer = MessageBox.Show(Window.GetWindow(this), $"¿Borrar el hangar «{hangar.Name}»? Sus programas no se desinstalan ni se cierran.",
                                     "Borrar hangar", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Yes) Vm.RemoveHangar();
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Elegir programa", Filter = "Programas (*.exe)|*.exe|Todos los archivos (*.*)|*.*" };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) Vm.SetSelectedProgram(dialog.FileName);
    }
}

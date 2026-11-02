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

    private void New_Click(object sender, RoutedEventArgs e) => Vm.NewProfile();

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (Vm.SelectedHasComments() && MessageBox.Show(Window.GetWindow(this)!,
                "Este perfil tiene comentarios // en el JSON. Si guardas desde el editor, se perderán " +
                "(lo importante puede ir en Notas y en la descripción de cada binding). Queda una copia .json.bak.\n\n¿Abrir el editor?",
                "RigControl", MessageBoxButton.YesNo, MessageBoxImage.Information) != MessageBoxResult.Yes)
            return;
        if (Vm.BeginEdit() is { } error)
            MessageBox.Show(Window.GetWindow(this)!, error, "RigControl", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (Vm.Selected is not { } p) return;
        if (MessageBox.Show(Window.GetWindow(this)!, $"¿Borrar el perfil \"{p.Name}\" ({p.FileName})?\n\nQueda una copia en {p.FileName}.bak.",
                "RigControl", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
            Vm.DeleteSelected();
    }
}

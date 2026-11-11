using System.Windows;
using System.Windows.Controls;
using RigControl.Core.Mods;
using RigControl.ViewModels;

namespace RigControl.Views;

public partial class ModsView : UserControl
{
    public ModsView() => InitializeComponent();

    private ModsViewModel Vm => (ModsViewModel)DataContext;

    private void ApplySet_Click(object sender, RoutedEventArgs e) => Vm.ApplySelectedSet();
    private void CreateSet_Click(object sender, RoutedEventArgs e) => Vm.CreateSetFromActive();

    private void RemoveSet_Click(object sender, RoutedEventArgs e)
    {
        if (Vm.SelectedSet is not { } set) return;
        var answer = MessageBox.Show(Window.GetWindow(this), $"¿Borrar el conjunto «{set.Name}»? Los mods no se tocan.",
                                     "Borrar conjunto", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Yes) Vm.RemoveSelectedSet();
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        if (Vm.SelectedLibrary is not { } lib || Vm.LooseCount == 0) return;
        var answer = MessageBox.Show(Window.GetWindow(this),
            $"Se moverán {Vm.LooseCount} carpetas de\n{ModManager.Expand(lib.Target)}\na la biblioteca\n{ModManager.Expand(lib.Library)}\n\n" +
            "En su sitio quedará un enlace, así que siguen activas y el simulador las ve igual. " +
            "Si la biblioteca está en la misma unidad, solo se renombran (es instantáneo).\n\n¿Seguir?",
            "Importar mods", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Yes) Vm.ImportLoose();
    }
}

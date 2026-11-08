using System.Windows;
using HapticSeat.App.ViewModels;

namespace HapticSeat.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    /// <summary>Solo las elecciones hechas a mano en la lista se recuerdan para el próximo arranque.</summary>
    private void ProfileBox_DropDownClosed(object? sender, EventArgs e) => (DataContext as MainViewModel)?.ProfileChosenByUser();
}

using Avalonia.Controls;
using Proteus.App.ViewModels;

namespace Proteus.App.Views;

public partial class MatchStyleWindow : Window
{
    public MatchStyleWindow()
    {
        InitializeComponent();
    }

    public MatchStyleWindow(MatchStyleViewModel vm) : this()
    {
        DataContext = vm;
        vm.Close = result => Close(result);
    }
}

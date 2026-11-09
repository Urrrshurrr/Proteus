using Avalonia.Controls;
using Proteus.App.ViewModels;

namespace Proteus.App.Views;

public partial class MatchFolderWindow : Window
{
    public MatchFolderWindow()
    {
        InitializeComponent();
    }

    public MatchFolderWindow(MatchFolderViewModel vm) : this()
    {
        DataContext = vm;
        vm.Close = result => Close(result);
    }
}

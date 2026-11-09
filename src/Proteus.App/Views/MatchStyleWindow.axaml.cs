using Avalonia.Controls;
using Avalonia.Interactivity;
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

    /// <summary>Inserts the clicked field into the pattern where the cursor is.</summary>
    private void OnFieldClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string token } || DataContext is not MatchStyleViewModel vm) return;
        var text = vm.Template ?? string.Empty;
        int at = Math.Clamp(TemplateBox.CaretIndex, 0, text.Length);
        if (TemplateBox.SelectionStart != TemplateBox.SelectionEnd)
        {
            int s = Math.Min(TemplateBox.SelectionStart, TemplateBox.SelectionEnd), en = Math.Max(TemplateBox.SelectionStart, TemplateBox.SelectionEnd);
            s = Math.Clamp(s, 0, text.Length); en = Math.Clamp(en, 0, text.Length);
            text = text.Remove(s, en - s);
            at = s;
        }
        vm.Template = text.Insert(at, token);
        TemplateBox.Focus();
        TemplateBox.CaretIndex = at + token.Length;
    }
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace Proteus.App.Controls;

/// <summary>
/// Shows one side of a before/after name pair, highlighting the characters that differ.
/// Side = New highlights inserted characters; Side = Old highlights removed ones.
/// </summary>
public class DiffTextBlock : TextBlock
{
    public enum DiffSide { Old, New }

    public static readonly StyledProperty<string?> OldTextProperty =
        AvaloniaProperty.Register<DiffTextBlock, string?>(nameof(OldText));

    public static readonly StyledProperty<string?> NewTextProperty =
        AvaloniaProperty.Register<DiffTextBlock, string?>(nameof(NewText));

    public static readonly StyledProperty<DiffSide> SideProperty =
        AvaloniaProperty.Register<DiffTextBlock, DiffSide>(nameof(Side), DiffSide.New);

    public static readonly StyledProperty<bool> HighlightProperty =
        AvaloniaProperty.Register<DiffTextBlock, bool>(nameof(Highlight), true);

    public static readonly StyledProperty<IBrush?> MarkBackgroundProperty =
        AvaloniaProperty.Register<DiffTextBlock, IBrush?>(nameof(MarkBackground));

    public static readonly StyledProperty<IBrush?> MarkForegroundProperty =
        AvaloniaProperty.Register<DiffTextBlock, IBrush?>(nameof(MarkForeground));

    public string? OldText { get => GetValue(OldTextProperty); set => SetValue(OldTextProperty, value); }
    public string? NewText { get => GetValue(NewTextProperty); set => SetValue(NewTextProperty, value); }
    public DiffSide Side { get => GetValue(SideProperty); set => SetValue(SideProperty, value); }
    public bool Highlight { get => GetValue(HighlightProperty); set => SetValue(HighlightProperty, value); }
    public IBrush? MarkBackground { get => GetValue(MarkBackgroundProperty); set => SetValue(MarkBackgroundProperty, value); }
    public IBrush? MarkForeground { get => GetValue(MarkForegroundProperty); set => SetValue(MarkForegroundProperty, value); }

    protected override Type StyleKeyOverride => typeof(TextBlock);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == OldTextProperty || change.Property == NewTextProperty || change.Property == SideProperty
            || change.Property == HighlightProperty || change.Property == MarkBackgroundProperty
            || change.Property == MarkForegroundProperty)
            Rebuild();
    }

    private void Rebuild()
    {
        var oldText = OldText ?? string.Empty;
        var newText = NewText ?? string.Empty;
        var shown = Side == DiffSide.New ? newText : oldText;
        var inlines = new InlineCollection();

        if (!Highlight || oldText == newText || shown.Length == 0)
        {
            inlines.Add(new Run(shown));
            Inlines = inlines;
            return;
        }

        var marks = Side == DiffSide.New ? Diff.Marks(oldText, newText).inNew : Diff.Marks(oldText, newText).inOld;
        int i = 0;
        while (i < shown.Length)
        {
            bool m = marks[i];
            int j = i;
            while (j < shown.Length && marks[j] == m) j++;
            var run = new Run(shown[i..j]);
            if (m)
            {
                run.Background = MarkBackground;
                if (MarkForeground != null) run.Foreground = MarkForeground;
                if (Side == DiffSide.Old) run.TextDecorations = Avalonia.Media.TextDecorations.Strikethrough;
            }
            inlines.Add(run);
            i = j;
        }
        Inlines = inlines;
    }
}

/// <summary>Character-level LCS diff; good enough for file names (at most a few hundred chars).</summary>
public static class Diff
{
    public static (bool[] inOld, bool[] inNew) Marks(string a, string b)
    {
        // Trim common prefix and suffix first; most renames only touch one end.
        int pre = 0;
        while (pre < a.Length && pre < b.Length && a[pre] == b[pre]) pre++;
        int suf = 0;
        while (suf < a.Length - pre && suf < b.Length - pre && a[a.Length - 1 - suf] == b[b.Length - 1 - suf]) suf++;

        var markA = new bool[a.Length];
        var markB = new bool[b.Length];
        int n = a.Length - pre - suf, m = b.Length - pre - suf;
        if (n == 0 || m == 0 || (long)n * m > 250_000)
        {
            for (int k = 0; k < n; k++) markA[pre + k] = true;
            for (int k = 0; k < m; k++) markB[pre + k] = true;
            return (markA, markB);
        }

        var dp = new int[n + 1, m + 1];
        for (int x = n - 1; x >= 0; x--)
        for (int y = m - 1; y >= 0; y--)
            dp[x, y] = a[pre + x] == b[pre + y] ? dp[x + 1, y + 1] + 1 : Math.Max(dp[x + 1, y], dp[x, y + 1]);

        int ia = 0, ib = 0;
        while (ia < n && ib < m)
        {
            if (a[pre + ia] == b[pre + ib]) { ia++; ib++; }
            else if (dp[ia + 1, ib] >= dp[ia, ib + 1]) markA[pre + ia++] = true;
            else markB[pre + ib++] = true;
        }
        while (ia < n) markA[pre + ia++] = true;
        while (ib < m) markB[pre + ib++] = true;
        return (markA, markB);
    }
}

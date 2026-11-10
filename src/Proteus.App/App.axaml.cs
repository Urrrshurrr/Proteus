using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Proteus.App.ViewModels;
using Proteus.App.Views;
using Proteus.Core;

namespace Proteus.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // An error in a button handler or binding is logged and shown, instead of closing the app.
        // Files are only ever changed by a batch, which checks every step, so carrying on is safe;
        // the preview is rebuilt from scratch so the list matches the files on disk again.
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            e.Handled = true;
            if (MainViewModel.Current is { } vm)
            {
                vm.ReportError("Something went wrong. The preview has been refreshed; if anything still looks wrong, restart Proteus", e.Exception);
                try { vm.SchedulePreview(); }
                catch (Exception ex) { AppLog.Write("Refreshing after an error failed", ex); }
            }
            else AppLog.Write("Unhandled UI error", e.Exception);
        };

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var vm = new MainViewModel();
            desktop.MainWindow = new MainWindow { DataContext = vm };
            if (desktop.Args is { Length: > 0 } args) vm.AddPaths(args);
        }
        base.OnFrameworkInitializationCompleted();
    }
}

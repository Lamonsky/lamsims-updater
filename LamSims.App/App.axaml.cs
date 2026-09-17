using System.Linq;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace LamSims.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var catalogArgument = desktop.Args?.FirstOrDefault();
            desktop.MainWindow = new MainWindow(Composition.Build(catalogArgument));
        }

        base.OnFrameworkInitializationCompleted();
    }
}

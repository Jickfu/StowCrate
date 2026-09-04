using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using StowCrate.App.ViewModels;
using StowCrate.App.Views;
using StowCrate.App.Services;
using Microsoft.Extensions.DependencyInjection;

namespace StowCrate.App;

public partial class App : Avalonia.Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var services = new ServiceCollection();
            services.AddSingleton<IRelocationWorkspace, RelocationWorkspace>();
            services.AddTransient<MainViewModel>();
            var provider = services.BuildServiceProvider();
            desktop.Exit += (_, _) => provider.Dispose();
            desktop.MainWindow = new MainWindow
            {
                DataContext = provider.GetRequiredService<MainViewModel>(),
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}

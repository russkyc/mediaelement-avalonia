using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using LibVLCSharp.Shared;

namespace Sample;

public partial class App : Application
{
    public static LibVLC LibVlc;
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        Core.Initialize();
        
        LibVlc = new LibVLC("--avcodec-threads=4");
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var vm = new MainViewModel();
            desktop.MainWindow = new MainWindow()
            {
                DataContext = vm
            };
            var window = new MainWindow()
            {
                DataContext = vm
            };
            window.Show();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
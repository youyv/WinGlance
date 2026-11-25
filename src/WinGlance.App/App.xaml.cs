using Microsoft.UI.Xaml;
using WinGlance.App.Services;

namespace WinGlance.App;

public partial class App : Application
{
    private Window? _window;
    private SingleInstance? _instance;
    public App()
    {
        InitializeComponent();
        DebugSettings.XamlResourceReferenceFailed += (_, e) => Log("resource", e.Message);
        DebugSettings.BindingFailed += (_, e) => Log("binding", e.Message);
        UnhandledException += (_, e) =>
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinGlance");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "app-error.log"), $"{DateTimeOffset.Now:O}\n{e.Message}\n{e.Exception}\n" + string.Join("\n", e.Exception.Data.Keys.Cast<object>().Select(key => $"{key}: {e.Exception.Data[key]}")));
        };
    }
    private static void Log(string category, string message)
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinGlance");
        Directory.CreateDirectory(directory);
        File.AppendAllText(Path.Combine(directory, "xaml-diagnostics.log"), $"{DateTimeOffset.Now:O} {category}: {message}\n");
    }
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _instance = SingleInstance.Acquire();
        if (_instance is null) { Exit(); return; }
        var main = new MainWindow();
        _window = main;
        _instance.Listen(() => main.DispatcherQueue.TryEnqueue(main.ActivateFromAnotherInstance));
        _window.Closed += (_, _) => { _instance?.Dispose(); _instance = null; };
        main.ShowInitial(Environment.GetCommandLineArgs().Skip(1));
    }
}

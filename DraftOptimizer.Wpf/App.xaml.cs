using System;
using System.IO;
using System.Net.Http;
using System.Windows;

namespace DraftOptimizer;

public partial class App : Application
{
    internal PortraitImageLoader Portraits { get; } = new(
        new HttpClient { Timeout = TimeSpan.FromSeconds(20) },
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DraftOptimizer", "cache", "portraits"));

    protected override void OnExit(ExitEventArgs e)
    {
        Portraits.Dispose();
        base.OnExit(e);
    }

    public App()
    {
        DispatcherUnhandledException += (_, e) => DraftOptimizer.Core.ErrorLog.Write("Error no controlado de la interfaz", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception error)
                DraftOptimizer.Core.ErrorLog.Write("Error no controlado del proceso", error);
        };
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
            DraftOptimizer.Core.ErrorLog.Write("Error no observado de tarea", e.Exception);
    }
}
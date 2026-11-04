using System;
using System.Threading.Tasks;
using System.Windows;
using USM.Core.Services;
using USM.Core.Services.Diagnostics;
using USM.ViewModels;

namespace USM;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Child-process mode: run the LemonLoader APK patcher (no UI) and exit.
        if (e.Args.Length > 0 && string.Equals(e.Args[0], USM.Core.Services.Quest.LemonPatcherBridge.CommandLineSwitch, StringComparison.Ordinal))
        {
            int exitCode = USM.Core.Services.Quest.LemonPatcherBridge.Run(e.Args[1..]);
            Console.Out.Flush();
            Shutdown(exitCode);
            return;
        }

        // Logging only: these hooks do not mark anything handled or change existing behaviour.
        DispatcherUnhandledException += (_, args) =>
            DiagnosticLog.Failure("Unhandled UI exception", args.Exception.Message, args.Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
            DiagnosticLog.Failure("Unobserved task exception", args.Exception.Message, args.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception unhandled)
                DiagnosticLog.Failure("Unhandled exception", unhandled.Message, unhandled, "Terminating=" + args.IsTerminating);
            else
                DiagnosticLog.Failure("Unhandled exception", "Non-exception object thrown.", null, "Terminating=" + args.IsTerminating);
        };
        DiagnosticLog.Info("App", "USM started.");

        base.OnStartup(e);

        var window = new MainWindow();
        MainWindow = window;
        window.Show();

        // Prepare audio tools at startup once. The executable remains cached in
        // LocalAppData, so subsequent starts and conversions do not download it.
        if (!AudioPipeline.IsFfmpegAvailable)
            _ = PrepareFfmpegAsync(window);
    }

    private static async Task PrepareFfmpegAsync(MainWindow window)
    {
        if (window.DataContext is MainViewModel viewModel)
            viewModel.Notify("Downloading audio tools (FFmpeg) for first use...");

        try
        {
            await AudioPipeline.EnsureFfmpegInstalledAsync();
            if (window.DataContext is MainViewModel vm)
                vm.Notify("Audio tools are ready.");
        }
        catch (Exception ex)
        {
            DiagnosticLog.Failure("FFmpeg download", ex.Message, ex);
            if (window.DataContext is MainViewModel vm)
                vm.Notify("FFmpeg could not be downloaded. USM will retry when audio conversion is used.");

            MessageBox.Show(
                $"USM could not download FFmpeg. Audio conversion will retry when used.\n\n{ex.Message}",
                "Audio tools unavailable",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }
}

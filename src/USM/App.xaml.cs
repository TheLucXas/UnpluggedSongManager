using System;
using System.Threading.Tasks;
using System.Windows;
using USM.Core.Services;
using USM.ViewModels;

namespace USM;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
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

using System.Configuration;
using System.Data;
using Velopack;
using Velopack.Sources;

namespace PilotEars;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : System.Windows.Application
{
    // GitHub release channel that Velopack uses for update checks.
    private const string UpdateUrl = "https://github.com/MANFahrer-GF/PilotEars";

    // Set once a background download finishes and is ready to apply on restart.
    // MainWindow watches this and shows the visible "Update ready" pill.
    public static UpdateManager? PendingUpdateManager;
    public static UpdateInfo? PendingUpdateInfo;
    public static event System.Action? UpdateReady;

    public App()
    {
        // VelopackApp.Build().Run() handles install/uninstall/update events
        // (when this exe is launched by the installer with special command-line
        // flags). For normal launches this is a no-op.
        VelopackApp.Build().Run();

        // Fire-and-forget update check on startup. Downloads in the background
        // and notifies MainWindow via UpdateReady so user sees a visible pill.
        _ = TryCheckForUpdatesAsync();
    }

    private static async System.Threading.Tasks.Task TryCheckForUpdatesAsync()
    {
        try
        {
            var mgr = new UpdateManager(new GithubSource(UpdateUrl, accessToken: null, prerelease: false));
            if (!mgr.IsInstalled) return;  // running from dev / portable — skip
            var info = await mgr.CheckForUpdatesAsync();
            if (info is null) return;
            await mgr.DownloadUpdatesAsync(info);

            // Stash so MainWindow can apply on user click.
            PendingUpdateManager = mgr;
            PendingUpdateInfo = info;

            // Hop to UI thread before raising — MainWindow handler touches WPF controls.
            if (Current?.Dispatcher is { } d)
                await d.InvokeAsync(() => UpdateReady?.Invoke());
        }
        catch
        {
            // Update check is best-effort — never block the app.
        }
    }
}

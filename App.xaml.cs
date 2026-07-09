using System;
using System.Threading.Tasks;
using System.Windows;
using Velopack;
using Velopack.Sources;

namespace F1RaceEngineer
{
    public partial class App : Application
    {
        [STAThread]
        private static void Main(string[] args)
        {
            // Must run first, before any window/UI code - Velopack briefly re-launches
            // the exe with special arguments during install/update/uninstall, and this
            // intercepts those invocations before anything else happens.
            VelopackApp.Build().Run();

            var app = new App();
            app.InitializeComponent();
            app.Run();
        }

        protected override async void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            if (await ApplyPendingUpdateAndRestartIfAvailable())
                return; // process is restarting - this instance shouldn't open a window

            new MainWindow().Show();
        }

        /// <summary>
        /// Silent auto-update: checks once at launch, downloads and applies immediately
        /// if a newer version is available (one quick restart), otherwise falls through
        /// to a normal launch. Returns true if a restart was initiated.
        ///
        /// IsInstalled guards against running this outside an actual Velopack-installed
        /// copy - e.g. launching the exe directly from bin/Debug or bin/Release during
        /// development, which is how this app is routinely run/verified and must keep
        /// working unchanged.
        ///
        /// Wrapped in try/catch: a failed update check (no internet, GitHub unreachable,
        /// etc.) must never prevent the app from opening - worst case, this launch just
        /// skips the check and tries again next time.
        /// </summary>
        private static async Task<bool> ApplyPendingUpdateAndRestartIfAvailable()
        {
            try
            {
                var mgr = new UpdateManager(new GithubSource(
                    "https://github.com/keviniscooking/F1-Race-Engineer", null, false));

                if (!mgr.IsInstalled) return false;

                var newVersion = await mgr.CheckForUpdatesAsync();
                if (newVersion == null) return false;

                await mgr.DownloadUpdatesAsync(newVersion);
                mgr.ApplyUpdatesAndRestart(newVersion);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}

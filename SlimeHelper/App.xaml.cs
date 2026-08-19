using System.Configuration;
using System.Data;
using System.Diagnostics;
using System.Windows;

namespace SlimeHelper
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            string[] args = Environment.GetCommandLineArgs();

            if (args.Length > 1)
            {
                if (int.TryParse(args[1], out int parentPid))
                {
                    try
                    {
                        Process parentProcess = Process.GetProcessById(parentPid);

                        parentProcess.EnableRaisingEvents = true;

                        parentProcess.Exited += (s, evt) =>
                        {
                            this.Dispatcher.Invoke(() =>
                            {
                                Shutdown();
                            });
                        };
                    }
                    catch (Exception)
                    {
                        Shutdown();
                    }
                }
            }
        }
    }

}

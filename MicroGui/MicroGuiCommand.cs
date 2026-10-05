using System;
using System.Linq;
using System.Threading;
using System.Windows;
using OpenTap;
using OpenTap.Cli;

namespace MicroGui
{
    [Display("microgui", "Open the MicroGui operator window.")]
    public class MicroGuiCommand : ICliAction
    {
        [UnnamedCommandLineArgument("plan", Required = false)]
        public string PlanPath { get; set; }

        public int Execute(CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
                return 1;

            // The GUI owns live logs while it runs; restore the console for failures and later tap commands.
            var consoleListener = Log.GetListeners().OfType<ConsoleTraceListener>().FirstOrDefault();
            if (consoleListener != null)
                Log.RemoveListener(consoleListener);

            Exception failure = null;
            try
            {
                var thread = new Thread(() =>
                {
                    try
                    {
                        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
                        var window = new MainWindow(PlanPath);
                        using (cancellationToken.Register(() =>
                                   window.Dispatcher.BeginInvoke(new Action(window.RequestShutdown))))
                        {
                            app.Run(window);
                            if (window.HadError)
                                throw new InvalidOperationException("MicroGui exited after a plan error.");
                        }
                    }
                    catch (Exception ex)
                    {
                        failure = ex;
                    }
                });
                thread.SetApartmentState(ApartmentState.STA);
                thread.Start();
                thread.Join();
            }
            finally
            {
                if (consoleListener != null)
                    Log.AddListener(consoleListener);
            }

            if (failure != null)
            {
                Log.CreateSource("MicroGui").Error("MicroGui failed: {0}", failure);
                return 1;
            }

            return 0;
        }
    }
}
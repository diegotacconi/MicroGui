using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using OpenTap;
using OpenTap.Cli;
using OpenTap.Diagnostic;

namespace MicroGui
{
    [Display("microgui", "Open the MicroGui operator window.")]
    public class MicroGuiCommand : ICliAction
    {
        [UnnamedCommandLineArgument("plan", Required = false)]
        public string PlanPath { get; set; }

        public int Execute(CancellationToken cancellationToken)
        {
            FilterConsoleLogging();
            if (cancellationToken.IsCancellationRequested)
                return 1;

            Exception failure = null;
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

            if (failure != null)
            {
                Log.CreateSource("MicroGui").Error("MicroGui failed: {0}", failure);
                return 1;
            }

            return 0;
        }

        private static void FilterConsoleLogging()
        {
            foreach (var listener in Log.GetListeners().OfType<ConsoleTraceListener>().ToArray())
            {
                Log.RemoveListener(listener);
                Log.AddListener(new ErrorOnlyConsoleListener(listener));
            }
        }

        private sealed class ErrorOnlyConsoleListener : ILogListener
        {
            private readonly ConsoleTraceListener _listener;

            public ErrorOnlyConsoleListener(ConsoleTraceListener listener)
            {
                _listener = listener;
            }

            public void EventsLogged(IEnumerable<Event> events)
            {
                _listener.TraceEvents(events.Where(logEvent =>
                    logEvent.EventType == (int)LogEventType.Error));
            }

            public void Flush()
            {
                _listener.Flush();
            }
        }
    }
}
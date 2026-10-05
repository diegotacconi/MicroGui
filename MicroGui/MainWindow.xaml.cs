using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;
using OpenTap;

namespace MicroGui
{
    public partial class MainWindow : Window
    {
        private readonly TestPlanController _controller;
        private readonly Stopwatch _runStopwatch = new Stopwatch();
        private readonly DispatcherTimer _runTimer;
        private readonly DispatcherTimer _activityDelayTimer;
        private MicroGuiState _displayState = MicroGuiState.Idle;
        private bool _runActive;
        private bool _stopRequested;
        private string _runOutcome;
        private string _lastAttemptedPath;
        private bool _closeRequested;
        private bool _listenersStopped;

        public bool HadError { get; private set; }

        public MainWindow(string initialPath)
        {
            InitializeComponent();
            _runTimer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(50)
            };
            _runTimer.Tick += (sender, args) =>
            {
                if (_runActive)
                    RefreshStateText();
                else
                    _runTimer.Stop();
            };
            // The activity ring appears only once a run has lasted longer than this delay.
            _activityDelayTimer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(150)
            };
            _activityDelayTimer.Tick += (sender, args) =>
            {
                _activityDelayTimer.Stop();
                if (_runActive)
                    ShowActivityRing();
            };
            _controller = new TestPlanController();
            _controller.StateChanged += OnStateChanged;
            PlanPathBox.Text = initialPath ?? string.Empty;
            UpdateControls();
            Loaded += (sender, args) =>
            {
                if (!string.IsNullOrWhiteSpace(PlanPathBox.Text))
                    LoadPlan(PlanPathBox.Text);
            };
        }

        protected override void OnContentRendered(EventArgs e)
        {
            base.OnContentRendered(e);

            // Lock the content-fitted height so the window only resizes horizontally.
            MinHeight = ActualHeight;
            MaxHeight = ActualHeight;
        }

        private void Browse_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Filter = "OpenTAP plans (*.TapPlan)|*.TapPlan",
                DefaultExt = ".TapPlan",
                CheckFileExists = true
            };
            if (dialog.ShowDialog(this) == true)
            {
                // With no plan loaded, show the chosen path first so a failed load leaves it there to fix.
                // With a plan loaded, leave the path box alone so a failed load keeps the current plan.
                if (!_controller.HasPlan)
                    PlanPathBox.Text = dialog.FileName;
                LoadPlan(dialog.FileName);
            }
        }

        private void PlanPathBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            _lastAttemptedPath = null;
            if (_controller == null)
                return;

            if (!_controller.HasPlan)
            {
                // Editing the path dismisses a previous load failure.
                _controller.ClearLoadFailure();
                return;
            }

            if (string.Equals(PlanPathBox.Text, _controller.LoadedPath, StringComparison.Ordinal))
                return;

            _controller.UnloadPlan();
            VerdictText.Text = "Verdict: -";
            UpdateControls();
        }

        private void PlanPathBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter)
                return;
            e.Handled = true;
            // Enter is an explicit request, so retry even a path that already failed.
            LoadTypedPath(retry: true);
        }

        private void PlanPathBox_LostFocus(object sender, RoutedEventArgs e)
        {
            LoadTypedPath(retry: false);
        }

        private void LoadTypedPath(bool retry)
        {
            var path = PlanPathBox.Text;
            if (_controller.IsRunning || _closeRequested || _controller.HasPlan ||
                string.IsNullOrWhiteSpace(path) ||
                (!retry && string.Equals(path, _lastAttemptedPath, StringComparison.Ordinal)))
                return;
            LoadPlan(path);
        }

        private bool LoadPlan(string path)
        {
            _lastAttemptedPath = path;
            try
            {
                _controller.LoadPlan(path);
                PlanPathBox.Text = _controller.LoadedPath;
                _lastAttemptedPath = _controller.LoadedPath;
                HadError = false;
                VerdictText.Text = "Verdict: -";
                UpdateControls();
                return true;
            }
            catch (Exception ex)
            {
                // The failure is reported through the LoadFailed state in the status area.
                HadError = true;
                UpdateControls();
                Log.CreateSource("MicroGui").Error("Unable to load test plan '{0}': {1}", path, ex);
                return false;
            }
        }

        internal static string FormatLoadError(string path, Exception ex)
        {
            var details = new System.Text.StringBuilder(ex.Message);
            for (var inner = ex.InnerException; inner != null; inner = inner.InnerException)
            {
                if (!string.IsNullOrWhiteSpace(inner.Message) && !details.ToString().Contains(inner.Message))
                    details.AppendLine().Append(inner.Message);
            }

            return "The test plan could not be loaded:" + Environment.NewLine + path +
                   Environment.NewLine + Environment.NewLine + details;
        }

        private async void Start_Click(object sender, RoutedEventArgs e)
        {
            if (_controller.IsRunning || _closeRequested || !_controller.HasPlan)
                return;

            HadError = false;
            VerdictText.Text = "Verdict: -";
            _runOutcome = null;
            _stopRequested = false;
            _runActive = true;
            _runStopwatch.Restart();
            _runTimer.Start();
            _activityDelayTimer.Start();

            try
            {
                var run = _controller.StartAsync();
                UpdateControls();
                var verdict = await run;
                VerdictText.Text = "Verdict: " + verdict;
                _runOutcome = _stopRequested || verdict == Verdict.Aborted
                    ? "Aborted after " + FormatSeconds(_runStopwatch.Elapsed)
                    : "Completed in " + FormatSeconds(_runStopwatch.Elapsed);
            }
            catch (Exception ex)
            {
                HadError = true;
                _runOutcome = "Failed after " + FormatSeconds(_runStopwatch.Elapsed);
                FinishRunTiming();
                if (!_closeRequested)
                    MessageBox.Show(this, ex.Message, "Test plan execution failed",
                        MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                FinishRunTiming();
                UpdateControls();
                if (_closeRequested)
                    Close();
            }
        }

        private void FinishRunTiming()
        {
            if (!_runActive)
                return;
            _runStopwatch.Stop();
            _runTimer.Stop();
            _activityDelayTimer.Stop();
            _runActive = false;
            // The controller has already left Running/Stopping even if its dispatched notification is still queued.
            _displayState = _controller.State;
            RefreshStateText();
            HideActivityRing();
        }

        private static string FormatSeconds(TimeSpan elapsed)
        {
            return elapsed.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture) + " s";
        }

        private void Stop_Click(object sender, RoutedEventArgs e)
        {
            _controller.RequestStop();
            UpdateControls();
        }

        private void OnStateChanged(MicroGuiState state)
        {
            if (Dispatcher.CheckAccess())
                ApplyState(state);
            else
                Dispatcher.BeginInvoke(new Action(() => ApplyState(state)));
        }

        private void ApplyState(MicroGuiState state)
        {
            _displayState = state;
            if (state == MicroGuiState.Stopping)
                _stopRequested = true;
            else if (state != MicroGuiState.Running && state != MicroGuiState.Ready)
                _runOutcome = null;

            RefreshStateText();
            UpdateControls();
        }

        private void RefreshStateText()
        {
            var state = _displayState;
            if (state == MicroGuiState.LoadFailed && _controller.LoadError != null)
            {
                var details = FormatLoadError(_controller.FailedLoadPath, _controller.LoadError);
                if (_controller.HasPlan)
                    details += Environment.NewLine + Environment.NewLine +
                               "The previous test plan is still loaded:" + Environment.NewLine + _controller.LoadedPath;

                StateText.Text = "Load failed";
                StateText.Foreground = Brushes.Firebrick;
                StateText.ToolTip = details;
                AutomationProperties.SetHelpText(StateText, details);
                return;
            }

            StateText.ClearValue(TextBlock.ForegroundProperty);
            StateText.ClearValue(ToolTipProperty);
            StateText.ClearValue(AutomationProperties.HelpTextProperty);

            if (_runActive && (state == MicroGuiState.Running || state == MicroGuiState.Stopping ||
                               state == MicroGuiState.Ready))
            {
                // A Ready notification can arrive before the awaited run result; keep timing until it does.
                var elapsed = FormatSeconds(_runStopwatch.Elapsed);
                StateText.Text = _stopRequested ? "Stopping after " + elapsed : elapsed;
                return;
            }

            switch (state)
            {
                case MicroGuiState.Idle:
                    StateText.Text = "Idle";
                    break;
                case MicroGuiState.Loading:
                    StateText.Text = "Loading...";
                    break;
                case MicroGuiState.Ready:
                    StateText.Text = _runOutcome ?? "Ready";
                    break;
                default:
                    StateText.Text = state.ToString();
                    break;
            }
        }

        // Shown by the run's delay timer and hidden when the run finishes; state notifications
        // (including Running -> Stopping) do not restart the delay.
        private void ShowActivityRing()
        {
            if (ActivityRing.Visibility == Visibility.Visible)
                return;

            ActivityRing.Visibility = Visibility.Visible;
            var spin = new DoubleAnimation(0, 360, new Duration(TimeSpan.FromSeconds(1)))
            {
                RepeatBehavior = RepeatBehavior.Forever
            };
            ActivityRingRotation.BeginAnimation(RotateTransform.AngleProperty, spin);
        }

        private void HideActivityRing()
        {
            ActivityRingRotation.BeginAnimation(RotateTransform.AngleProperty, null);
            ActivityRing.Visibility = Visibility.Hidden;
        }

        private void UpdateControls()
        {
            if (StartButton == null)
                return;

            StartButton.IsEnabled = _controller.HasPlan && !_controller.IsRunning && !_closeRequested;
            BrowseButton.IsEnabled = !_controller.IsRunning && !_closeRequested;
            PlanPathBox.IsEnabled = !_controller.IsRunning && !_closeRequested;
            StopButton.IsEnabled = _controller.IsRunning && _controller.State == MicroGuiState.Running;
        }

        public void RequestShutdown()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(RequestShutdown));
                return;
            }

            Close();
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (_controller.IsRunning)
            {
                _closeRequested = true;
                _controller.RequestStop();
                UpdateControls();
                e.Cancel = true;
            }
            else if (!_listenersStopped)
            {
                _runTimer.Stop();
                _activityDelayTimer.Stop();
                _controller.Dispose();
                _listenersStopped = true;
            }

            base.OnClosing(e);
        }
    }
}
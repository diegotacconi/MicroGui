using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using OpenTap;

namespace MicroGui
{
    public partial class MainWindow : Window
    {
        private readonly TestPlanController _controller;
        private string _lastAttemptedPath;
        private bool _closeRequested;
        private bool _listenersStopped;

        public bool HadError { get; private set; }

        public MainWindow(string initialPath)
        {
            InitializeComponent();
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
                PlanPathBox.Text = dialog.FileName;
                LoadPlan(dialog.FileName);
            }
        }

        private void PlanPathBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            _lastAttemptedPath = null;
            if (_controller == null || !_controller.HasPlan ||
                string.Equals(PlanPathBox.Text, _controller.LoadedPath, StringComparison.Ordinal))
                return;

            _controller.UnloadPlan();
            StateText.Text = "Idle";
            VerdictText.Text = "Verdict: -";
            UpdateControls();
        }

        private void PlanPathBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter)
                return;
            e.Handled = true;
            LoadTypedPath();
        }

        private void PlanPathBox_LostFocus(object sender, RoutedEventArgs e)
        {
            LoadTypedPath();
        }

        private void LoadTypedPath()
        {
            var path = PlanPathBox.Text;
            if (_controller.IsRunning || _closeRequested || _controller.HasPlan ||
                string.IsNullOrWhiteSpace(path) ||
                string.Equals(path, _lastAttemptedPath, StringComparison.Ordinal))
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
                HadError = true;
                UpdateControls();
                Log.CreateSource("MicroGui").Error("Unable to load test plan: {0}", ex);
                MessageBox.Show(this, ex.Message, "Unable to load test plan",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        private async void Start_Click(object sender, RoutedEventArgs e)
        {
            if (_controller.IsRunning || _closeRequested || !_controller.HasPlan)
                return;

            HadError = false;
            VerdictText.Text = "Verdict: -";

            try
            {
                var run = _controller.StartAsync();
                UpdateControls();
                var verdict = await run;
                VerdictText.Text = _controller.State == MicroGuiState.Stopped
                    ? "Verdict: STOPPED"
                    : "Verdict: " + verdict;
            }
            catch (Exception ex)
            {
                HadError = true;
                if (!_closeRequested)
                    MessageBox.Show(this, ex.Message, "Test plan execution failed",
                        MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                UpdateControls();
                if (_closeRequested)
                    Close();
            }
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
            StateText.Text = state.ToString();
            UpdateControls();
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
                _controller.Dispose();
                _listenersStopped = true;
            }

            base.OnClosing(e);
        }
    }
}
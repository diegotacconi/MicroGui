using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using OpenTap;

namespace MicroGui
{
    // Test plan lifecycle only; the run outcome is reported separately as an OpenTAP Verdict.
    internal enum MicroGuiState
    {
        Idle,       // No test plan loaded.
        Loading,    // A test plan is being loaded.
        LoadFailed, // The last load attempt failed; any previously loaded plan is kept.
        Ready,      // A test plan is loaded and can be run.
        Running,    // The test plan is executing.
        Stopping    // Stop was requested; waiting for the run to end.
    }

    internal sealed class TestPlanController : IDisposable
    {
        private readonly object _gate = new object();
        private TestPlan _plan;
        private CancellationTokenSource _runCancellation;
        private bool _isRunning;
        private bool _disposed;
        public event Action<MicroGuiState> StateChanged;
        public MicroGuiState State { get; private set; } = MicroGuiState.Idle;
        public string LoadedPath { get; private set; }

        // Set while State is LoadFailed.
        public string FailedLoadPath { get; private set; }
        public Exception LoadError { get; private set; }

        public bool IsRunning
        {
            get
            {
                lock (_gate)
                {
                    return _isRunning;
                }
            }
        }

        public bool HasPlan => _plan != null;

        public void LoadPlan(string path)
        {
            if (IsRunning)
                throw new InvalidOperationException("A test plan cannot be loaded while a run is active.");

            SetState(MicroGuiState.Loading);
            try
            {
                var fullPath = Path.GetFullPath(path);
                if (!File.Exists(fullPath))
                    throw new FileNotFoundException("Test plan not found.", fullPath);
                if (!string.Equals(Path.GetExtension(fullPath), ".TapPlan", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("Select a .TapPlan file.", nameof(path));

                var plan = TestPlan.Load(fullPath);
                if (plan == null)
                    throw new InvalidDataException("OpenTAP did not load a test plan.");

                _plan = plan;
                LoadedPath = fullPath;
                SetState(MicroGuiState.Ready);
            }
            catch (Exception ex)
            {
                // A failed load leaves any previously loaded plan in place.
                FailedLoadPath = TryGetFullPath(path);
                LoadError = ex;
                SetState(MicroGuiState.LoadFailed);
                throw;
            }
        }

        public void ClearLoadFailure()
        {
            if (State == MicroGuiState.LoadFailed && !IsRunning)
                SetState(_plan != null ? MicroGuiState.Ready : MicroGuiState.Idle);
        }

        private static string TryGetFullPath(string path)
        {
            try
            {
                return Path.GetFullPath(path);
            }
            catch (Exception)
            {
                return path;
            }
        }

        public void UnloadPlan()
        {
            if (IsRunning)
                return;

            _plan = null;
            LoadedPath = null;
            SetState(MicroGuiState.Idle);
        }

        public async Task<Verdict> StartAsync()
        {
            if (_plan == null)
                throw new InvalidOperationException("Load a valid .TapPlan before starting execution.");

            CancellationToken token;
            lock (_gate)
            {
                if (_disposed)
                    throw new ObjectDisposedException(nameof(TestPlanController));
                if (_isRunning)
                    throw new InvalidOperationException("A test plan is already running.");

                _isRunning = true;
                _runCancellation = new CancellationTokenSource();
                token = _runCancellation.Token;
            }

            SetState(MicroGuiState.Running);
            try
            {
                _plan.PrintTestPlanRunSummary = true;
                var run = await _plan.ExecuteAsync(token)
                    .ConfigureAwait(false);
                return run.Verdict;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // OpenTAP normally returns an Aborted run on cancellation.
                return Verdict.Aborted;
            }
            finally
            {
                lock (_gate)
                {
                    _isRunning = false;
                    _runCancellation.Dispose();
                    _runCancellation = null;
                }
                // The plan stays loaded after any run outcome, so it is ready to run again.
                SetState(MicroGuiState.Ready);
            }
        }

        public bool RequestStop()
        {
            lock (_gate)
            {
                if (!_isRunning || _runCancellation == null || _runCancellation.IsCancellationRequested)
                    return false;

                SetState(MicroGuiState.Stopping);
                _runCancellation.Cancel();
                return true;
            }
        }

        private void SetState(MicroGuiState state)
        {
            if (state != MicroGuiState.LoadFailed)
            {
                FailedLoadPath = null;
                LoadError = null;
            }
            State = state;
            StateChanged?.Invoke(state);
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_isRunning)
                    throw new InvalidOperationException("Stop the active test plan before disposing the controller.");

                if (_disposed)
                    return;

                _disposed = true;
                _plan = null;
            }
        }
    }
}
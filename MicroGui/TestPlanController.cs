using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using OpenTap;

namespace MicroGui
{
    internal enum TestPlanState
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
        public event Action<TestPlanState> StateChanged;
        public TestPlanState State { get; private set; } = TestPlanState.Idle;
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

            SetState(TestPlanState.Loading);
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
                SetState(TestPlanState.Ready);
            }
            catch (Exception ex)
            {
                // A failed load leaves any previously loaded plan in place.
                FailedLoadPath = TryGetFullPath(path);
                LoadError = ex;
                SetState(TestPlanState.LoadFailed);
                throw;
            }
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
            SetState(TestPlanState.Idle);
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

            SetState(TestPlanState.Running);
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
                SetState(TestPlanState.Ready);
            }
        }

        public bool RequestStop()
        {
            lock (_gate)
            {
                if (!_isRunning || _runCancellation == null || _runCancellation.IsCancellationRequested)
                    return false;

                SetState(TestPlanState.Stopping);
                _runCancellation.Cancel();
                return true;
            }
        }

        private void SetState(TestPlanState state)
        {
            if (state != TestPlanState.LoadFailed)
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

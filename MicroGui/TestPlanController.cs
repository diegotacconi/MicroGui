using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using OpenTap;

namespace MicroGui
{
    internal enum MicroGuiState
    {
        Idle,
        Loading,
        Ready,
        Running,
        Stopping,
        Completed,
        Passed,
        Failed,
        Stopped,
        Error
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
        public bool IsRunning
        {
            get
            {
                lock (_gate)
                    return _isRunning;
            }
        }

        public bool HasPlan => _plan != null;

        public TestPlanController()
        {

        }

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
            catch
            {
                _plan = null;
                LoadedPath = null;
                SetState(MicroGuiState.Error);
                throw;
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

                SetState(token.IsCancellationRequested
                    ? MicroGuiState.Stopped
                    : GetStateForVerdict(run.Verdict));
                return run.Verdict;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                SetState(MicroGuiState.Stopped);
                return Verdict.Inconclusive;
            }
            catch
            {
                SetState(MicroGuiState.Error);
                throw;
            }
            finally
            {
                lock (_gate)
                {
                    _isRunning = false;
                    _runCancellation.Dispose();
                    _runCancellation = null;
                }
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

        private static MicroGuiState GetStateForVerdict(Verdict verdict)
        {
            if (verdict == Verdict.Pass)
                return MicroGuiState.Passed;

            if (verdict == Verdict.Fail)
                return MicroGuiState.Failed;

            return MicroGuiState.Completed;
        }

        private void SetState(MicroGuiState state)
        {
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

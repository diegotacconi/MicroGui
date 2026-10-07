using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
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

    // Not a user-selectable plugin; injected per run to observe when OpenTAP has finished timing it.
    [Browsable(false)]
    internal sealed class RunCompletedListener : ResultListener
    {
        private readonly Action _completed;

        public RunCompletedListener(Action completed)
        {
            _completed = completed;
            Name = "MicroGui run timer";
        }

        // Called on this listener's own result worker after TestPlanRun.Duration has been set,
        // and before resources are closed and ExecuteAsync returns.
        public override void OnTestPlanRunCompleted(TestPlanRun planRun, Stream logStream)
        {
            _completed();
        }
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

        // Duration reported by OpenTAP (TestPlanRun.Duration) for the last run; null if no run result was returned.
        public TimeSpan? LastRunDuration { get; private set; }

        // Stopwatch timestamps bracketing the interval OpenTAP measures as TestPlanRun.Duration; 0 = not yet observed.
        private long _runStartTimestamp;
        private long _runEndTimestamp;

        /// <summary>
        /// Elapsed time of the current or last run, measured over the same interval OpenTAP uses for
        /// <see cref="TestRun.Duration"/>. Null until OpenTAP has started the run. Stops advancing once
        /// OpenTAP has finished timing the run, even while resources are still closing.
        /// </summary>
        public TimeSpan? GetAlignedElapsed()
        {
            var start = Interlocked.Read(ref _runStartTimestamp);
            if (start == 0)
                return null;
            var end = Interlocked.Read(ref _runEndTimestamp);
            return TimestampsToTimeSpan(start, end != 0 ? end : Stopwatch.GetTimestamp());
        }

        internal static TimeSpan TimestampsToTimeSpan(long start, long end)
        {
            var ticks = Math.Max(0, end - start);
            return TimeSpan.FromTicks((long)(ticks * ((double)TimeSpan.TicksPerSecond / Stopwatch.Frequency)));
        }

        private void OnPlanPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            // OpenTAP raises IsRunning on the plan thread right after creating the TestPlanRun and
            // immediately before starting the stopwatch that produces TestPlanRun.Duration.
            if (e.PropertyName == nameof(TestPlan.IsRunning) && sender is TestPlan plan && plan.IsRunning)
                Interlocked.CompareExchange(ref _runStartTimestamp, Stopwatch.GetTimestamp(), 0);
        }

        private void MarkRunTimingEnded()
        {
            Interlocked.CompareExchange(ref _runEndTimestamp, Stopwatch.GetTimestamp(), 0);
        }

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
                LastRunDuration = null;
                Interlocked.Exchange(ref _runStartTimestamp, 0);
                Interlocked.Exchange(ref _runEndTimestamp, 0);
            }

            var plan = _plan;
            SetState(TestPlanState.Running);
            plan.PropertyChanged += OnPlanPropertyChanged;
            try
            {
                plan.PrintTestPlanRunSummary = true;
                // Same listeners as ExecuteAsync(token), plus one that marks when OpenTAP stops timing the run.
                var listeners = ResultSettings.Current.Cast<IResultListener>()
                    .Concat(new IResultListener[] { new RunCompletedListener(MarkRunTimingEnded) })
                    .ToList();
                var run = await plan.ExecuteAsync(listeners, null, null, token)
                    .ConfigureAwait(false);
                MarkRunTimingEnded();
                LastRunDuration = run.Duration;
                return run.Verdict;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // OpenTAP normally returns an Aborted run on cancellation.
                return Verdict.Aborted;
            }
            finally
            {
                plan.PropertyChanged -= OnPlanPropertyChanged;
                MarkRunTimingEnded();
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

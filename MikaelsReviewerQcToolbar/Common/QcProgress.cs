using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace MikaelsReviewerQcToolbar.Common
{
    /// <summary>
    /// Worker-facing progress/cancellation for one unit of work (one check on one layer). The
    /// checks call Begin/Tick as they go; the UI polls Read() on a timer, so the worker never
    /// touches the UI. Tick also checks for cancellation, so Cancel takes effect on the next record.
    /// </summary>
    internal sealed class QcProgress
    {
        private readonly object _lock = new object();
        private string _phase = "Starting";
        private long _total;
        private long _done;
        private long _oid = -1;
        private double _start;
        private double _span = 1;

        public CancellationToken Token { get; }

        /// <summary>Number of records in the current unit (the layer's feature count); set by the caller before the check runs.</summary>
        public long UnitRecords { get; set; }

        public QcProgress(CancellationToken token = default) { Token = token; }

        /// <summary>
        /// Starts a phase of the unit. start/span place it within the unit's 0..1 range, so a unit
        /// made of several phases (read, merge, validate...) advances the bar smoothly.
        /// </summary>
        public void Begin(string phase, long total, double start = 0, double span = 1)
        {
            lock (_lock)
            {
                _phase = phase;
                _total = total;
                _start = start;
                _span = span;
                _done = 0;
                _oid = -1;
            }
            Token.ThrowIfCancellationRequested();
        }

        public void BeginRecords(string phase, double start = 0, double span = 1) => Begin(phase, UnitRecords, start, span);

        /// <summary>A phase that is one long operation with no record counter (e.g. merging polygons).</summary>
        public void Working(string phase, double start, double span) => Begin(phase, 0, start, span);

        public void Tick(long oid = -1)
        {
            Interlocked.Increment(ref _done);
            if (oid >= 0) Interlocked.Exchange(ref _oid, oid);
            Token.ThrowIfCancellationRequested();
        }

        public void Check() => Token.ThrowIfCancellationRequested();

        public ProgressSnapshot Read()
        {
            lock (_lock)
            {
                long done = Interlocked.Read(ref _done);
                double inPhase = _total > 0 ? Math.Min(1.0, (double)done / _total) : 0;
                return new ProgressSnapshot(_phase, done, _total, Interlocked.Read(ref _oid), Math.Min(1.0, _start + _span * inPhase));
            }
        }
    }

    internal readonly struct ProgressSnapshot
    {
        public ProgressSnapshot(string phase, long done, long total, long oid, double fraction)
        {
            Phase = phase; Done = done; Total = total; Oid = oid; Fraction = fraction;
        }
        public string Phase { get; }
        public long Done { get; }
        public long Total { get; }
        public long Oid { get; }
        /// <summary>Progress through the current unit, 0..1.</summary>
        public double Fraction { get; }
    }

    internal class QcRunView
    {
        public double OverallFraction { get; set; }
        public string StepText { get; set; }
        public string LayerText { get; set; }
        public string PhaseText { get; set; }
        public string RecordText { get; set; }
        public TimeSpan Elapsed { get; set; }
        public TimeSpan? Remaining { get; set; }
        public bool Finished { get; set; }
    }

    /// <summary>
    /// Overall progress of a run made of planned units (step x layer). Each unit has a weight
    /// (records x a per-step cost factor) so the overall bar and the remaining-time estimate are
    /// proportional to expected work rather than just counting units. The estimate is recomputed
    /// only every EstimateIntervalSeconds so it doesn't jitter. Created and read on the UI thread;
    /// only the nested QcProgress is touched by the worker.
    /// </summary>
    internal sealed class QcRunProgress
    {
        public const int EstimateIntervalSeconds = 3;

        public class Unit
        {
            public string Step { get; set; }
            public int StepNumber { get; set; }
            public string Layer { get; set; }
            public long Records { get; set; }
            public double Weight { get; set; }
        }

        private readonly List<Unit> _units;
        private readonly int _stepCount;
        private readonly DateTime _startedUtc = DateTime.UtcNow;
        private int _index = -1;
        private double _completedWeight;
        private DateTime _lastEstimateUtc = DateTime.MinValue;
        private TimeSpan? _lastRemaining;
        private bool _finished;
        private bool _unitDone;
        private TimeSpan _finalElapsed;

        public QcProgress Current { get; }

        public QcRunProgress(IEnumerable<Unit> units, int stepCount, CancellationToken token)
        {
            _units = units.ToList();
            _stepCount = stepCount;
            Current = new QcProgress(token);
        }

        public static QcRunProgress ForSingle(string toolName, string layerName, long records, CancellationToken token)
        {
            var run = new QcRunProgress(new[]
            {
                new Unit { Step = toolName, StepNumber = 1, Layer = layerName, Records = records, Weight = 1 }
            }, 1, token);
            return run;
        }

        public void BeginUnit(string step, string layer)
        {
            // Units are planned in execution order; match forward from the current position.
            int next = _units.FindIndex(Math.Max(0, _index + 1), u => u.Step == step && u.Layer == layer);
            if (next < 0) return;
            _index = next;
            _unitDone = false;
            Current.UnitRecords = _units[_index].Records;
            Current.Begin("Starting", 0);
        }

        public void EndUnit()
        {
            if (_index < 0 || _index >= _units.Count || _unitDone) return;
            _completedWeight += _units[_index].Weight;
            _unitDone = true;
        }

        public void Finish()
        {
            _finished = true;
            _finalElapsed = DateTime.UtcNow - _startedUtc;
        }

        public QcRunView Snapshot()
        {
            var now = DateTime.UtcNow;
            var elapsed = _finished ? _finalElapsed : now - _startedUtc;
            double totalWeight = Math.Max(_units.Sum(u => u.Weight), 1e-9);

            var view = new QcRunView { Elapsed = elapsed, Finished = _finished };

            if (_index < 0 || _index >= _units.Count)
            {
                view.OverallFraction = _finished ? 1 : 0;
                view.StepText = "Preparing...";
                view.LayerText = "";
                view.PhaseText = "";
                view.RecordText = "";
                return view;
            }

            var unit = _units[_index];
            var snap = Current.Read();

            double unitFraction = _unitDone ? 1 : snap.Fraction;
            double done = _completedWeight + (_unitDone ? 0 : unit.Weight * unitFraction);
            double fraction = _finished ? 1 : Math.Min(1.0, done / totalWeight);
            view.OverallFraction = fraction;

            int layersInStep = _units.Count(u => u.Step == unit.Step);
            int layerNumber = _units.Take(_index + 1).Count(u => u.Step == unit.Step);
            view.StepText = $"Step {unit.StepNumber} of {_stepCount}: {unit.Step}";
            view.LayerText = $"Layer {layerNumber} of {layersInStep}: {unit.Layer}";
            view.PhaseText = _finished ? "Finished" : snap.Phase;
            view.RecordText = snap.Total > 0
                ? $"Record {Math.Min(snap.Done, snap.Total):N0} of {snap.Total:N0}" + (snap.Oid >= 0 ? $"  (OID {snap.Oid})" : "")
                : "(this phase is one long operation and has no record counter)";

            if (!_finished && (now - _lastEstimateUtc).TotalSeconds >= EstimateIntervalSeconds)
            {
                _lastEstimateUtc = now;
                _lastRemaining = fraction >= 0.01 && elapsed.TotalSeconds >= 8
                    ? TimeSpan.FromSeconds(elapsed.TotalSeconds * (1 - fraction) / fraction)
                    : (TimeSpan?)null;
            }
            view.Remaining = _finished ? TimeSpan.Zero : _lastRemaining;
            return view;
        }
    }
}

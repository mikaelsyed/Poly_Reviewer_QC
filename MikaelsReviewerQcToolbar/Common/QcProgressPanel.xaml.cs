using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace MikaelsReviewerQcToolbar.Common
{
    /// <summary>
    /// Shows a QcRunProgress: overall progress bar, current step / layer / phase, the record being
    /// processed, elapsed time and the estimated time remaining. It polls on a one-second timer
    /// (the worker never touches the UI); the remaining-time estimate itself is only recomputed
    /// every QcRunProgress.EstimateIntervalSeconds so it doesn't jitter.
    /// </summary>
    public partial class QcProgressPanel : UserControl
    {
        private readonly DispatcherTimer _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        private QcRunProgress _run;

        public QcProgressPanel()
        {
            InitializeComponent();
            Visibility = Visibility.Collapsed;
            _timer.Tick += (s, e) => Render(null);
        }

        internal void Start(QcRunProgress run)
        {
            _run = run;
            Visibility = Visibility.Visible;
            Render(null);
            _timer.Start();
        }

        /// <summary>Stops updating and leaves a final snapshot on screen with the given outcome ("Finished", "Cancelled"...).</summary>
        internal void Stop(string outcome)
        {
            _timer.Stop();
            if (_run == null) return;
            _run.Finish();
            Render(outcome);
        }

        private void Render(string outcome)
        {
            if (_run == null) return;
            var view = _run.Snapshot();

            double percent = Math.Round(view.OverallFraction * 100, 0);
            if (outcome == "Cancelled") percent = Math.Min(percent, 99);
            Bar.Value = percent;
            PercentText.Text = $"{percent:0}%";

            StepText.Text = view.StepText;
            LayerText.Text = view.LayerText;
            PhaseText.Text = outcome ?? view.PhaseText;
            RecordText.Text = outcome == null ? view.RecordText : "";

            string remaining = outcome != null
                ? ""
                : view.Remaining.HasValue
                    ? $"   |   Remaining about {Format(view.Remaining.Value)}   (estimate refreshes every {QcRunProgress.EstimateIntervalSeconds} s)"
                    : $"   |   Remaining: calculating... (estimate refreshes every {QcRunProgress.EstimateIntervalSeconds} s once enough work is done)";
            TimeText.Text = $"Elapsed {Format(view.Elapsed)}{remaining}";
        }

        private static string Format(TimeSpan t) =>
            t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes:00}:{t.Seconds:00}";
    }
}

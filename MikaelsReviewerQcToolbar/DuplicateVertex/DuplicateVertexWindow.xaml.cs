using ArcGIS.Desktop.Catalog;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using MikaelsReviewerQcToolbar.Common;
using System;
using System.Threading;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;

namespace MikaelsReviewerQcToolbar.DuplicateVertex
{
    public partial class DuplicateVertexWindow : ArcGIS.Desktop.Framework.Controls.ProWindow
    {
        private readonly System.Windows.Forms.TextBox _logTextBox;

        public DuplicateVertexWindow()
        {
            InitializeComponent();
            _logTextBox = QcUiHelpers.CreateLogTextBox();
            LogHost.Child = _logTextBox;

            UseDefaultGdbCheck.Checked += (s, e) => ApplyDefaultGdb(true);
            UseDefaultGdbCheck.Unchecked += (s, e) => ApplyDefaultGdb(false);

            LayerCombo.SelectionChanged += async (s, e) =>
            {
                var layer = (LayerCombo.SelectedItem as QcUiHelpers.LayerItem)?.Layer;
                ToleranceLabel.Text = $"Tolerance ({await QcUiHelpers.GetDistanceUnitHintAsync(layer)}):";
            };
            Loaded += DuplicateVertexWindow_Loaded;
        }

        private void ApplyDefaultGdb(bool useDefault)
        {
            VertexBrowseButton.IsEnabled = !useDefault;
            if (useDefault)
            {
                var gdbPath = ArcGIS.Desktop.Core.Project.Current?.DefaultGeodatabasePath;
                VertexOutputTextBox.Text = string.IsNullOrEmpty(gdbPath) ? "" : Path.Combine(gdbPath, "VertexToleranceVertices");
            }
            else
            {
                VertexOutputTextBox.Text = "";
            }
        }

        private void SetLog(IEnumerable<string> lines) => _logTextBox.Text = string.Join(Environment.NewLine, lines);

        private async void DuplicateVertexWindow_Loaded(object sender, RoutedEventArgs e)
        {
            var layers = await QcUiHelpers.GetPolylineOrPolygonLayerItemsAsync();
            LayerCombo.ItemsSource = layers;
            if (layers.Count > 0) LayerCombo.SelectedIndex = 0;
            else SetLog(new[] { "No polyline or polygon layers found in the active map." });

            UseDefaultGdbCheck.IsChecked = true;
        }

        private void VertexBrowseButton_Click(object sender, RoutedEventArgs e)
        {
            var saveDialog = new SaveItemDialog { Title = "Vertex Points Output", OverwritePrompt = true };
            if (saveDialog.ShowDialog() == true)
                VertexOutputTextBox.Text = saveDialog.FilePath;
        }

        private void BrowseButton_Click(object sender, RoutedEventArgs e)
        {
            var saveDialog = new SaveItemDialog { Title = "Flagged Features Output", OverwritePrompt = true };
            if (saveDialog.ShowDialog() == true)
                OutputPathTextBox.Text = saveDialog.FilePath;
        }

        private async void RunButton_Click(object sender, RoutedEventArgs e)
        {
            const string title = "Vertex Tolerance Check";
            var layer = (LayerCombo.SelectedItem as QcUiHelpers.LayerItem)?.Layer;
            if (layer == null)
            {
                MessageBox.Show("Please select an input layer.", title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!double.TryParse(ToleranceTextBox.Text?.Trim(), out double tolerance) || tolerance <= 0)
            {
                MessageBox.Show("Tolerance must be a positive number.", title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!double.TryParse(MarginTextBox.Text?.Trim(), out double margin) || margin < 0 || margin >= tolerance)
            {
                MessageBox.Show("The at-tolerance margin must be zero or more, and smaller than the tolerance.", title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            bool reportBelow = BelowCheck.IsChecked == true;
            bool reportAt = AtCheck.IsChecked == true;
            if (!reportBelow && !reportAt)
            {
                MessageBox.Show("Tick at least one of: report vertices below the tolerance, report vertices at the tolerance.", title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string vertexPath = string.IsNullOrWhiteSpace(VertexOutputTextBox.Text) ? null : VertexOutputTextBox.Text.Trim();
            string flaggedPath = string.IsNullOrWhiteSpace(OutputPathTextBox.Text) ? null : OutputPathTextBox.Text.Trim();

            RunButton.IsEnabled = false;
            CloseButton.IsEnabled = false;
            SetLog(new[] { "Running..." });
            var cts = new CancellationTokenSource();
            _cts = cts;
            CancelButton.Content = "Cancel";
            CancelButton.IsEnabled = true;
            CancelButton.Visibility = Visibility.Visible;
            QcRunProgress run = null;

            try
            {
                long records = await QcRunUi.CountAsync(layer);
                run = QcRunProgress.ForSingle("Vertex Tolerance Check", layer.Name, records, cts.Token);
                ProgressPanel.Start(run);
                run.BeginUnit("Vertex Tolerance Check", layer.Name);
                var messages = new List<string>();
                var (scan, sr) = await QueuedTask.Run(() =>
                {
                    var found = DuplicateVertexChecker.Find(f => layer.Search(f), tolerance, margin, reportBelow, reportAt, run.Current);
                    QcOutputHelper.SelectOids(layer, found.Oids.ToList());
                    using var fc = layer.GetFeatureClass();
                    return (found, fc.GetDefinition().GetSpatialReference());
                });

                int below = scan.Hits.Count(h => h.Status == VertexStatus.Below);
                int at = scan.Hits.Count - below;
                messages.Add($"{scan.Oids.Count} feature(s) flagged on {layer.Name}: {below} vertex(es) below the tolerance, {at} at the tolerance.");
                if (scan.Hits.Count == 0)
                {
                    messages.Add(scan.ClosestDistance < double.MaxValue
                        ? $"Closest pair of vertices within 10x the tolerance is {scan.ClosestDistance:G4} apart (feature OID {scan.ClosestOid})."
                        : "No pair of vertices is within 10x the tolerance of each other.");
                }

                await QcOutputHelper.CopyToOutputAsync(layer, scan.Oids.Count, flaggedPath, messages);

                if (vertexPath != null)
                {
                    var points = DuplicateVertexChecker.ToFeatures(scan.Hits);
                    var fields = DuplicateVertexChecker.OutputFields;
                    await QcFeatureClassWriter.WriteFeaturesAsync(sr, "POINT", points, fields, vertexPath, messages,
                        "No vertices to report; vertex points output not created.", "vertex point(s)");
                }

                SetLog(messages);
            }
            catch (OperationCanceledException)
            {
                SetLog(new[] { "Cancelled." });
            }
            catch (Exception ex)
            {
                SetLog(new[] { "Error: " + ex.Message });
            }
            finally
            {
                run?.EndUnit();
                ProgressPanel.Stop(cts.IsCancellationRequested ? "Cancelled" : "Finished");
                CancelButton.Visibility = Visibility.Collapsed;
                _cts = null;
                cts.Dispose();
                RunButton.IsEnabled = true;
                CloseButton.IsEnabled = true;
            }
        }

        private CancellationTokenSource _cts;

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
            CancelButton.IsEnabled = false;
            CancelButton.Content = "Cancelling...";
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
    }
}

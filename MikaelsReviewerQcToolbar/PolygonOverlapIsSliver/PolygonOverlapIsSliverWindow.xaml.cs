using ArcGIS.Desktop.Catalog;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using MikaelsReviewerQcToolbar.Common;
using System;
using System.Threading;
using System.Collections.Generic;
using System.Windows;

namespace MikaelsReviewerQcToolbar.PolygonOverlapIsSliver
{
    public partial class PolygonOverlapIsSliverWindow : ArcGIS.Desktop.Framework.Controls.ProWindow
    {
        private readonly System.Windows.Forms.TextBox _logTextBox;

        public PolygonOverlapIsSliverWindow()
        {
            InitializeComponent();
            _logTextBox = QcUiHelpers.CreateLogTextBox();
            LogHost.Child = _logTextBox;
            QcUiHelpers.ConfigureDefaultGdbToggle(UseDefaultGdbCheck, OutputPathTextBox, BrowseButton, "OverlapSliverOutput");
            Loaded += PolygonOverlapIsSliverWindow_Loaded;
        }

        private void SetLog(IEnumerable<string> lines) => _logTextBox.Text = string.Join(Environment.NewLine, lines);

        private async void PolygonOverlapIsSliverWindow_Loaded(object sender, RoutedEventArgs e)
        {
            var layers = await QcUiHelpers.GetPolygonLayerItemsAsync();
            LayerACombo.ItemsSource = layers;
            LayerBCombo.ItemsSource = layers;
            if (layers.Count > 0)
            {
                LayerACombo.SelectedIndex = 0;
                LayerBCombo.SelectedIndex = layers.Count > 1 ? 1 : 0;
            }
            else
            {
                SetLog(new[] { "No polygon layers found in the active map." });
            }

            var gdbPath = ArcGIS.Desktop.Core.Project.Current?.DefaultGeodatabasePath;
            if (!string.IsNullOrEmpty(gdbPath))
                OutputPathTextBox.Text = System.IO.Path.Combine(gdbPath, "OverlapSliverOutput");
        }

        private void BrowseButton_Click(object sender, RoutedEventArgs e)
        {
            var saveDialog = new SaveItemDialog { Title = "Output Feature Class", OverwritePrompt = true };
            if (saveDialog.ShowDialog() == true)
                OutputPathTextBox.Text = saveDialog.FilePath;
        }

        private async void RunButton_Click(object sender, RoutedEventArgs e)
        {
            var layerA = (LayerACombo.SelectedItem as QcUiHelpers.LayerItem)?.Layer;
            var layerB = (LayerBCombo.SelectedItem as QcUiHelpers.LayerItem)?.Layer;
            if (layerA == null || layerB == null)
            {
                MessageBox.Show("Please select both polygon layers.", "Polygon Overlap is Sliver", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!double.TryParse(ThinnessTextBox.Text?.Trim(), out double thinness) || thinness <= 0 || thinness > 1)
            {
                MessageBox.Show("Thinness Ratio Threshold must be a number between 0 and 1.", "Polygon Overlap is Sliver", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            double? maxArea = null;
            var maxAreaText = MaxAreaTextBox.Text?.Trim();
            if (!string.IsNullOrEmpty(maxAreaText))
            {
                if (!double.TryParse(maxAreaText, out double parsedMaxArea) || parsedMaxArea <= 0)
                {
                    MessageBox.Show("Maximum Area must be a positive number, or left blank.", "Polygon Overlap is Sliver", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                maxArea = parsedMaxArea;
            }

            string outputPath = OutputPathTextBox.Text?.Trim();
            if (string.IsNullOrEmpty(outputPath))
            {
                MessageBox.Show("Please specify an output feature class.", "Polygon Overlap is Sliver", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

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
                long records = await QcRunUi.CountAsync(layerA, layerB);
                run = QcRunProgress.ForSingle("Polygon Overlap is Sliver", layerA.Name, records, cts.Token);
                ProgressPanel.Start(run);
                run.BeginUnit("Polygon Overlap is Sliver", layerA.Name);
                var messages = new List<string>();
                var (scan, sr) = await QueuedTask.Run(() =>
                {
                    var found = SliverOverlapFinder.Find(f => layerA.Search(f), f => layerB.Search(f), layerA == layerB, thinness, maxArea, run.Current);
                    using var fc = layerA.GetFeatureClass();
                    return (found, fc.GetDefinition().GetSpatialReference());
                });

                messages.Add($"{scan.Results.Count} overlap sliver(s) matched between {layerA.Name} and {layerB.Name} ({scan.Candidates} overlap(s) found in total).");
                if (scan.Results.Count == 0 && scan.Candidates > 0)
                    messages.Add($"The thinnest overlap had a Thinness Ratio of {scan.ThinnestRatio:0.###} (area {scan.ThinnestArea:0.###}). Set the threshold above that to flag it (and check Maximum Area).");
                await QcFeatureClassWriter.WriteSliverResultsAsync(sr, scan.Results, outputPath, messages);

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

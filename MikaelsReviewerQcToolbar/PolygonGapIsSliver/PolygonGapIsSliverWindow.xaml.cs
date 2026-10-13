using ArcGIS.Desktop.Catalog;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using MikaelsReviewerQcToolbar.Common;
using System;
using System.Threading;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace MikaelsReviewerQcToolbar.PolygonGapIsSliver
{
    public partial class PolygonGapIsSliverWindow : ArcGIS.Desktop.Framework.Controls.ProWindow
    {
        private readonly System.Windows.Forms.TextBox _logTextBox;

        public PolygonGapIsSliverWindow()
        {
            InitializeComponent();
            _logTextBox = QcUiHelpers.CreateLogTextBox();
            LogHost.Child = _logTextBox;
            QcUiHelpers.ConfigureDefaultGdbToggle(UseDefaultGdbCheck, OutputPathTextBox, BrowseButton, "GapSliverOutput");
            LayerACombo.SelectionChanged += async (s, e) =>
            {
                var layer = (LayerACombo.SelectedItem as QcUiHelpers.LayerItem)?.Layer;
                GapWidthLabel.Text = $"Maximum Gap Width ({await QcUiHelpers.GetDistanceUnitHintAsync(layer)}):";
            };
            Loaded += PolygonGapIsSliverWindow_Loaded;
        }

        private void SetLog(IEnumerable<string> lines) => _logTextBox.Text = string.Join(Environment.NewLine, lines);

        private async void PolygonGapIsSliverWindow_Loaded(object sender, RoutedEventArgs e)
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
                OutputPathTextBox.Text = System.IO.Path.Combine(gdbPath, "GapSliverOutput");
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
                MessageBox.Show("Please select both polygon layers.", "Polygon Gap is Sliver", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!double.TryParse(SearchDistanceTextBox.Text?.Trim(), out double searchDistance) || searchDistance < 0)
            {
                MessageBox.Show("Maximum Gap Width must be zero or a positive number.", "Polygon Gap is Sliver", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!double.TryParse(ThinnessTextBox.Text?.Trim(), out double thinness) || thinness <= 0 || thinness > 1)
            {
                MessageBox.Show("Thinness Ratio Threshold must be a number between 0 and 1.", "Polygon Gap is Sliver", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            double? maxArea = null;
            var maxAreaText = MaxAreaTextBox.Text?.Trim();
            if (!string.IsNullOrEmpty(maxAreaText))
            {
                if (!double.TryParse(maxAreaText, out double parsedMaxArea) || parsedMaxArea <= 0)
                {
                    MessageBox.Show("Maximum Area must be a positive number, or left blank.", "Polygon Gap is Sliver", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                maxArea = parsedMaxArea;
            }

            string outputPath = OutputPathTextBox.Text?.Trim();
            if (string.IsNullOrEmpty(outputPath))
            {
                MessageBox.Show("Please specify an output feature class.", "Polygon Gap is Sliver", MessageBoxButton.OK, MessageBoxImage.Warning);
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
                run = QcRunProgress.ForSingle("Polygon Gap is Sliver", layerA.Name, records, cts.Token);
                ProgressPanel.Start(run);
                run.BeginUnit("Polygon Gap is Sliver", layerA.Name);
                var messages = new List<string>();
                bool onlySlivers = OnlySliversCheck.IsChecked == true;
                var (scan, sr) = await QueuedTask.Run(() =>
                {
                    var found = SliverGapFinder.Find(f => layerA.Search(f), f => layerB.Search(f), layerA == layerB,
                        searchDistance, thinness, maxArea, onlySlivers, run.Current);
                    using var fc = layerA.GetFeatureClass();
                    return (found, fc.GetDefinition().GetSpatialReference());
                });

                messages.Add($"Found {scan.Candidates} gap(s) between {layerA.Name} and {layerB.Name}: {scan.EnclosedFound} fully enclosed, {scan.OpenFound} open strip(s). {scan.SliverCount} classified as slivers.");
                if (onlySlivers && scan.SkippedNonSliver > 0)
                    messages.Add($"\"Only output gaps classified as slivers\" is ticked, so {scan.SkippedNonSliver} gap-shaped area(s) that are not sliver-shaped were skipped without being validated (this is what makes it faster). Untick it to see every gap.");
                messages.Add($"{scan.Results.Count} gap(s) written to the output.");
                if (scan.Candidates == 0 && scan.SkippedNonSliver == 0)
                    messages.Add(searchDistance > 0
                        ? "No gaps found. Fully enclosed gaps are always found; open strips need to be narrower than the Maximum Gap Width, so try a larger width."
                        : "No fully enclosed gaps found. Maximum Gap Width is 0, so open strips between polygons were not searched.");
                if (scan.SkippedNotBetweenPolygons > 0)
                    messages.Add($"{scan.SkippedNotBetweenPolygons} other gap-like shape(s) were checked and rejected as not being gaps between polygons (a notch or hole inside a single polygon, or a rounded corner where polygons meet).");

                messages.Add("Where the time went: " + string.Join("; ", scan.Notes));
                var features = SliverGapFinder.ToFeatures(scan.Results);
                var fields = SliverGapFinder.OutputFields;
                await QcFeatureClassWriter.WriteFeaturesAsync(sr, "POLYGON", features, fields, outputPath, messages,
                    "No gaps to write; output not created.", "gap polygon(s)");

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

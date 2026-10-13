using ArcGIS.Desktop.Catalog;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using MikaelsReviewerQcToolbar.Common;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows;

namespace MikaelsReviewerQcToolbar.ZeroLengthArea
{
    public partial class ZeroLengthAreaWindow : ArcGIS.Desktop.Framework.Controls.ProWindow
    {
        private const string Title = "Zero Length / Zero Area";
        private readonly System.Windows.Forms.TextBox _logTextBox;
        private CancellationTokenSource _cts;

        public ZeroLengthAreaWindow()
        {
            InitializeComponent();
            _logTextBox = QcUiHelpers.CreateLogTextBox();
            LogHost.Child = _logTextBox;
            QcUiHelpers.ConfigureDefaultGdbToggle(UseDefaultGdbCheck, OutputPathTextBox, BrowseButton, "ZeroLengthAreaOutput");
            LayerCombo.SelectionChanged += async (s, e) =>
            {
                var layer = (LayerCombo.SelectedItem as QcUiHelpers.LayerItem)?.Layer;
                string unit = await QcUiHelpers.GetDistanceUnitHintAsync(layer);
                LengthLabel.Text = $"Polyline length at or below ({unit}):";
                AreaLabel.Text = $"Polygon area at or below (square {unit}):";
            };
            Loaded += ZeroLengthAreaWindow_Loaded;
        }

        private void SetLog(IEnumerable<string> lines) => _logTextBox.Text = string.Join(Environment.NewLine, lines);

        private async void ZeroLengthAreaWindow_Loaded(object sender, RoutedEventArgs e)
        {
            var layers = await QcUiHelpers.GetPolylineOrPolygonLayerItemsAsync();
            LayerCombo.ItemsSource = layers;
            if (layers.Count > 0) LayerCombo.SelectedIndex = 0;
            else SetLog(new[] { "No polyline or polygon layers found in the active map." });
        }

        private void BrowseButton_Click(object sender, RoutedEventArgs e)
        {
            var saveDialog = new SaveItemDialog { Title = "Output Feature Class", OverwritePrompt = true };
            if (saveDialog.ShowDialog() == true)
                OutputPathTextBox.Text = saveDialog.FilePath;
        }

        private async void RunButton_Click(object sender, RoutedEventArgs e)
        {
            var layer = (LayerCombo.SelectedItem as QcUiHelpers.LayerItem)?.Layer;
            if (layer == null)
            {
                MessageBox.Show("Please select an input layer.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!double.TryParse(LengthTextBox.Text?.Trim(), out double lengthLimit) || lengthLimit < 0 ||
                !double.TryParse(AreaTextBox.Text?.Trim(), out double areaLimit) || areaLimit < 0)
            {
                MessageBox.Show("The length and area limits must be zero or a positive number.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string outputPath = string.IsNullOrWhiteSpace(OutputPathTextBox.Text) ? null : OutputPathTextBox.Text.Trim();

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
                run = QcRunProgress.ForSingle(Title, layer.Name, records, cts.Token);
                ProgressPanel.Start(run);
                run.BeginUnit(Title, layer.Name);

                var messages = new List<string>();
                var scan = await QueuedTask.Run(() =>
                {
                    var found = ZeroSizeChecker.Find(f => layer.Search(f), lengthLimit, areaLimit, run.Current);
                    QcOutputHelper.SelectOids(layer, found.Oids);
                    return found;
                });

                messages.Add($"{scan.Oids.Count} feature(s) flagged on {layer.Name}: {scan.NullOrEmpty} empty/missing shape(s), " +
                             $"{scan.ZeroLengthLines} zero-length line(s), {scan.ZeroAreaPolygons} zero-area polygon(s), " +
                             $"{scan.CollapsedPart} with a collapsed part.");
                await QcOutputHelper.CopyToOutputAsync(layer, scan.Oids.Count, outputPath, messages);

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

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
            CancelButton.IsEnabled = false;
            CancelButton.Content = "Cancelling...";
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
    }
}

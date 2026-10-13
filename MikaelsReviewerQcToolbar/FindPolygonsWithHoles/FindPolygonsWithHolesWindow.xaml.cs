using ArcGIS.Desktop.Catalog;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using MikaelsReviewerQcToolbar.Common;
using System;
using System.Threading;
using System.Collections.Generic;
using System.IO;
using System.Windows;

namespace MikaelsReviewerQcToolbar.FindPolygonsWithHoles
{
    public partial class FindPolygonsWithHolesWindow : ArcGIS.Desktop.Framework.Controls.ProWindow
    {
        private readonly System.Windows.Forms.TextBox _logTextBox;

        public FindPolygonsWithHolesWindow()
        {
            InitializeComponent();
            _logTextBox = QcUiHelpers.CreateLogTextBox();
            LogHost.Child = _logTextBox;

            UseDefaultGdbCheck.Checked += (s, e) => ApplyDefaultGdb(true);
            UseDefaultGdbCheck.Unchecked += (s, e) => ApplyDefaultGdb(false);

            Loaded += FindPolygonsWithHolesWindow_Loaded;
        }

        private void ApplyDefaultGdb(bool useDefault)
        {
            HolesBrowseButton.IsEnabled = !useDefault;
            BrowseButton.IsEnabled = !useDefault;
            if (useDefault)
            {
                var gdbPath = ArcGIS.Desktop.Core.Project.Current?.DefaultGeodatabasePath;
                HolesOutputTextBox.Text = string.IsNullOrEmpty(gdbPath) ? "" : Path.Combine(gdbPath, "PolygonHoles");
                // The flagged-polygons copy is optional; leave it blank unless the user asks for it.
            }
            else
            {
                HolesOutputTextBox.Text = "";
                OutputPathTextBox.Text = "";
            }
        }

        private void SetLog(IEnumerable<string> lines) => _logTextBox.Text = string.Join(Environment.NewLine, lines);

        private async void FindPolygonsWithHolesWindow_Loaded(object sender, RoutedEventArgs e)
        {
            var layers = await QcUiHelpers.GetPolygonLayerItemsAsync();
            LayerCombo.ItemsSource = layers;
            if (layers.Count > 0) LayerCombo.SelectedIndex = 0;
            else SetLog(new[] { "No polygon layers found in the active map." });

            UseDefaultGdbCheck.IsChecked = true;
        }

        private void HolesBrowseButton_Click(object sender, RoutedEventArgs e)
        {
            var saveDialog = new SaveItemDialog { Title = "Hole Polygons Output", OverwritePrompt = true };
            if (saveDialog.ShowDialog() == true)
                HolesOutputTextBox.Text = saveDialog.FilePath;
        }

        private void BrowseButton_Click(object sender, RoutedEventArgs e)
        {
            var saveDialog = new SaveItemDialog { Title = "Flagged Polygons Output", OverwritePrompt = true };
            if (saveDialog.ShowDialog() == true)
                OutputPathTextBox.Text = saveDialog.FilePath;
        }

        private async void RunButton_Click(object sender, RoutedEventArgs e)
        {
            var layer = (LayerCombo.SelectedItem as QcUiHelpers.LayerItem)?.Layer;
            if (layer == null)
            {
                MessageBox.Show("Please select an input polygon layer.", "Find Polygons with Holes", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string holesPath = string.IsNullOrWhiteSpace(HolesOutputTextBox.Text) ? null : HolesOutputTextBox.Text.Trim();
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
                run = QcRunProgress.ForSingle("Find Polygons with Holes", layer.Name, records, cts.Token);
                ProgressPanel.Start(run);
                run.BeginUnit("Find Polygons with Holes", layer.Name);
                var messages = new List<string>();
                var (oids, holes, sr) = await QueuedTask.Run(() =>
                {
                    var (found, holePolygons) = PolygonHoleFinder.Find(f => layer.Search(f), run.Current);
                    QcOutputHelper.SelectOids(layer, found);
                    using var fc = layer.GetFeatureClass();
                    return (found, holePolygons, fc.GetDefinition().GetSpatialReference());
                });

                messages.Add($"{oids.Count} polygon(s) with holes flagged on {layer.Name} ({holes.Count} hole(s) total).");

                await QcOutputHelper.CopyToOutputAsync(layer, oids.Count, flaggedPath, messages);

                if (holesPath != null)
                {
                    var fields = PolygonHoleFinder.OutputFields;
                    await QcFeatureClassWriter.WriteFeaturesAsync(sr, "POLYGON", holes, fields, holesPath, messages,
                        "No holes found; hole polygons output not created.", "hole polygon(s)");
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

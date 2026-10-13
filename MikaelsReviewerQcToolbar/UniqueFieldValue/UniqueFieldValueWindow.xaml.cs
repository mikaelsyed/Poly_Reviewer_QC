using ArcGIS.Desktop.Catalog;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using MikaelsReviewerQcToolbar.Common;
using System;
using System.Threading;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace MikaelsReviewerQcToolbar.UniqueFieldValue
{
    public partial class UniqueFieldValueWindow : ArcGIS.Desktop.Framework.Controls.ProWindow
    {
        private readonly System.Windows.Forms.TextBox _logTextBox;

        public UniqueFieldValueWindow()
        {
            InitializeComponent();
            _logTextBox = QcUiHelpers.CreateLogTextBox();
            LogHost.Child = _logTextBox;
            QcUiHelpers.ConfigureDefaultGdbToggle(UseDefaultGdbCheck, OutputPathTextBox, BrowseButton, "UniqueFieldValueOutput");
            LayerCombo.SelectionChanged += async (s, e) => await LoadFieldsAsync();
            Loaded += UniqueFieldValueWindow_Loaded;
        }

        private void SetLog(IEnumerable<string> lines) => _logTextBox.Text = string.Join(Environment.NewLine, lines);

        private async void UniqueFieldValueWindow_Loaded(object sender, RoutedEventArgs e)
        {
            var layers = await QcUiHelpers.GetAnyFeatureLayerItemsAsync();
            LayerCombo.ItemsSource = layers;
            if (layers.Count > 0) LayerCombo.SelectedIndex = 0;
            else SetLog(new[] { "No feature layers found in the active map." });
        }

        private async System.Threading.Tasks.Task LoadFieldsAsync()
        {
            var layer = (LayerCombo.SelectedItem as QcUiHelpers.LayerItem)?.Layer;
            FieldsListBox.ItemsSource = await QcUiHelpers.GetFieldItemsAsync(layer);
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
                MessageBox.Show("Please select an input layer.", "Unique Field Value", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var fields = FieldsListBox.SelectedItems.Cast<QcUiHelpers.FieldItem>().Select(f => f.Name).ToList();
            if (fields.Count == 0)
            {
                MessageBox.Show("Please select at least one attribute to compare.", "Unique Field Value", MessageBoxButton.OK, MessageBoxImage.Warning);
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
                run = QcRunProgress.ForSingle("Unique Field Value", layer.Name, records, cts.Token);
                ProgressPanel.Start(run);
                run.BeginUnit("Unique Field Value", layer.Name);
                var messages = new List<string>();
                var oids = await QueuedTask.Run(() =>
                {
                    var found = UniqueFieldValueChecker.FindOids(layer, fields, run.Current);
                    return QcOutputHelper.SelectOids(layer, found);
                });

                messages.Add($"{oids.Count} feature(s) flagged on {layer.Name}.");
                await QcOutputHelper.CopyToOutputAsync(layer, oids.Count, outputPath, messages);

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

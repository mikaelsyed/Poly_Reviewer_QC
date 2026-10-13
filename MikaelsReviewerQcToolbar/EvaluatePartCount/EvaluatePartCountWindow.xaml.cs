using ArcGIS.Desktop.Catalog;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Mapping;
using MikaelsReviewerQcToolbar.Common;
using System;
using System.Threading;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace MikaelsReviewerQcToolbar.EvaluatePartCount
{
    public partial class EvaluatePartCountWindow : ArcGIS.Desktop.Framework.Controls.ProWindow
    {
        private class OperatorItem
        {
            public string Label { get; set; }
            public PartCountOperator Operator { get; set; }
            public bool NeedsSecondValue { get; set; }
            public override string ToString() => Label;
        }

        private readonly System.Windows.Forms.TextBox _logTextBox;

        public EvaluatePartCountWindow()
        {
            InitializeComponent();

            _logTextBox = QcUiHelpers.CreateLogTextBox();
            LogHost.Child = _logTextBox;
            QcUiHelpers.ConfigureDefaultGdbToggle(UseDefaultGdbCheck, OutputPathTextBox, BrowseButton, "EvaluatePartCountOutput");

            OperatorCombo.ItemsSource = new List<OperatorItem>
            {
                new OperatorItem { Label = "Equal To", Operator = PartCountOperator.Equal },
                new OperatorItem { Label = "Not Equal To", Operator = PartCountOperator.NotEqual },
                new OperatorItem { Label = "Greater Than", Operator = PartCountOperator.GreaterThan },
                new OperatorItem { Label = "Greater Than Or Equal To", Operator = PartCountOperator.GreaterThanOrEqual },
                new OperatorItem { Label = "Less Than", Operator = PartCountOperator.LessThan },
                new OperatorItem { Label = "Less Than Or Equal To", Operator = PartCountOperator.LessThanOrEqual },
                new OperatorItem { Label = "Between (inclusive)", Operator = PartCountOperator.Between, NeedsSecondValue = true },
                new OperatorItem { Label = "Outside Range", Operator = PartCountOperator.Outside, NeedsSecondValue = true },
            };
            OperatorCombo.SelectionChanged += (s, e) => UpdateSecondValueVisibility();
            OperatorCombo.SelectedIndex = 2; // Greater Than 1 finds multipart features by default

            Loaded += EvaluatePartCountWindow_Loaded;
        }

        private void UpdateSecondValueVisibility()
        {
            bool needsSecond = (OperatorCombo.SelectedItem as OperatorItem)?.NeedsSecondValue == true;
            Value2Label.Visibility = needsSecond ? Visibility.Visible : Visibility.Collapsed;
            Value2TextBox.Visibility = needsSecond ? Visibility.Visible : Visibility.Collapsed;
        }

        private void SetLog(IEnumerable<string> lines) => _logTextBox.Text = string.Join(Environment.NewLine, lines);

        private async void EvaluatePartCountWindow_Loaded(object sender, RoutedEventArgs e)
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
                MessageBox.Show("Please select an input layer.", "Evaluate Part Count", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var opItem = OperatorCombo.SelectedItem as OperatorItem;
            if (opItem == null || !int.TryParse(Value1TextBox.Text?.Trim(), out int value1))
            {
                MessageBox.Show("Please enter a whole number value.", "Evaluate Part Count", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            int value2 = 0;
            if (opItem.NeedsSecondValue && !int.TryParse(Value2TextBox.Text?.Trim(), out value2))
            {
                MessageBox.Show("Please enter a whole number for the second value.", "Evaluate Part Count", MessageBoxButton.OK, MessageBoxImage.Warning);
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
                run = QcRunProgress.ForSingle("Evaluate Part Count", layer.Name, records, cts.Token);
                ProgressPanel.Start(run);
                run.BeginUnit("Evaluate Part Count", layer.Name);
                var messages = new List<string>();
                var oids = await QueuedTask.Run(() =>
                {
                    var found = PartCountChecker.FindOids(layer, opItem.Operator, value1, value2, run.Current);
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

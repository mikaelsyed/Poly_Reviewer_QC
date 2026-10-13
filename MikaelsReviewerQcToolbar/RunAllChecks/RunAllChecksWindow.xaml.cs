using ArcGIS.Core.Data;
using ArcGIS.Core.Geometry;
using ArcGIS.Desktop.Core;
using ArcGIS.Desktop.Core.Geoprocessing;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Mapping;
using MikaelsReviewerQcToolbar.Common;
using MikaelsReviewerQcToolbar.DuplicateFeature;
using MikaelsReviewerQcToolbar.DuplicateVertex;
using MikaelsReviewerQcToolbar.EvaluatePartCount;
using MikaelsReviewerQcToolbar.FindPolygonsWithHoles;
using MikaelsReviewerQcToolbar.PolygonGapIsSliver;
using MikaelsReviewerQcToolbar.PolygonOverlapIsSliver;
using MikaelsReviewerQcToolbar.UniqueFieldValue;
using MikaelsReviewerQcToolbar.ZeroLengthArea;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace MikaelsReviewerQcToolbar.RunAllChecks
{
    public partial class RunAllChecksWindow : ArcGIS.Desktop.Framework.Controls.ProWindow
    {
        private const string Title = "Run All QC Checks";

        private class OperatorItem
        {
            public string Label { get; set; }
            public PartCountOperator Operator { get; set; }
            public bool NeedsSecondValue { get; set; }
            public override string ToString() => Label;
        }

        /// <summary>One step's result on one layer; Export runs later, only if the false-positive gate allows output.</summary>
        private class LayerRun
        {
            public FeatureLayer Layer { get; set; }
            public int Count { get; set; }
            public Func<Task> Export { get; set; }
        }

        private readonly System.Windows.Forms.TextBox _logTextBox;
        private List<string> _log;
        private int _tableCounter;
        private CancellationTokenSource _cts;
        private QcRunProgress _run;
        private QcRunLog _runLog;
        private readonly Dictionary<string, QcLogStep> _records = new Dictionary<string, QcLogStep>();
        private readonly Dictionary<FeatureLayer, string> _datasets = new Dictionary<FeatureLayer, string>();
        private List<string> _exports;

        public RunAllChecksWindow()
        {
            InitializeComponent();
            _logTextBox = QcUiHelpers.CreateLogTextBox();
            LogHost.Child = _logTextBox;
            LogPathText.Text = DefaultLogPath();

            S1_OpCombo.ItemsSource = new List<OperatorItem>
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
            S1_OpCombo.SelectionChanged += (s, e) =>
                S1_V2Panel.Visibility = (S1_OpCombo.SelectedItem as OperatorItem)?.NeedsSecondValue == true ? Visibility.Visible : Visibility.Collapsed;
            S1_OpCombo.SelectedIndex = 2;

            PolygonList.SelectionChanged += async (s, e) => await RefreshFieldListsAsync(PolygonList, S6_PolyFieldsList, S7_PolyFieldsList);
            LineList.SelectionChanged += async (s, e) => await RefreshFieldListsAsync(LineList, S6_LineFieldsList, S7_LineFieldsList);

            Loaded += RunAllChecksWindow_Loaded;
        }

        /// <summary>
        /// Fills the field pickers for one layer type with every attribute field found on the selected
        /// layers of that type (a field missing from some of them is handled per layer at run time),
        /// keeping whatever the user had already picked where the field still exists.
        /// </summary>
        private static async Task RefreshFieldListsAsync(ListBox layerList, params ListBox[] fieldLists)
        {
            var layers = layerList.SelectedItems.Cast<QcUiHelpers.LayerItem>().Select(i => i.Layer).ToList();
            var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var layer in layers)
                foreach (var field in await QcUiHelpers.GetFieldItemsAsync(layer))
                    names.Add(field.Name);

            foreach (var list in fieldLists)
            {
                var previous = new HashSet<string>(list.SelectedItems.Cast<QcUiHelpers.FieldItem>().Select(f => f.Name), StringComparer.OrdinalIgnoreCase);
                var items = names.Select(n => new QcUiHelpers.FieldItem { Name = n }).ToList();
                list.ItemsSource = items;
                foreach (var item in items.Where(i => previous.Contains(i.Name)))
                    list.SelectedItems.Add(item);
            }
        }

        private void FlushLog() => _logTextBox.Text = string.Join(Environment.NewLine, _log);

        private async void RunAllChecksWindow_Loaded(object sender, RoutedEventArgs e)
        {
            var polygons = await QcUiHelpers.GetPolygonLayerItemsAsync();
            var lines = await QcUiHelpers.GetPolylineLayerItemsAsync();
            PolygonList.ItemsSource = polygons;
            LineList.ItemsSource = lines;
            if (polygons.Count == 0 && lines.Count == 0)
                _logTextBox.Text = "No polygon or polyline layers found in the active map.";
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
            CancelButton.IsEnabled = false;
            CancelButton.Content = "Cancelling...";
            _log?.Add("Cancel requested - stopping at the next safe point (a merge or buffer operation already underway must finish first).");
            FlushLog();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

        // ------------------------------------------------------------------
        // QC output log file
        // ------------------------------------------------------------------

        private static string DefaultLogPath()
        {
            string folder = Project.Current?.HomeFolderPath;
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
                folder = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            return Path.Combine(folder, QcRunLog.ReviewerDefaultFileName);
        }

        private void LogBrowseButton_Click(object sender, RoutedEventArgs e)
        {
            string current = LogPathText.Text?.Trim();
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "QC output log file",
                Filter = "Text file (*.txt)|*.txt",
                DefaultExt = ".txt",
                OverwritePrompt = false, // the log is meant to be replaced by each run
                FileName = string.IsNullOrEmpty(current) ? QcRunLog.ReviewerDefaultFileName : Path.GetFileName(current),
                InitialDirectory = string.IsNullOrEmpty(current) ? "" : Path.GetDirectoryName(current)
            };
            if (dialog.ShowDialog(this) == true)
                LogPathText.Text = dialog.FileName;
        }

        private static string YesNo(bool value) => value ? "yes" : "no";

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        private static double ParseOr(TextBox box, double fallback) => double.TryParse(box.Text?.Trim(), out var v) ? v : fallback;
        private static int ParseIntOr(TextBox box, int fallback) => int.TryParse(box.Text?.Trim(), out var v) ? v : fallback;

        private static List<string> SelectedFieldNames(ListBox list) =>
            list.SelectedItems.Cast<QcUiHelpers.FieldItem>().Select(f => f.Name).ToList();

        private static double? ParseOptionalPositive(TextBox box, out bool valid)
        {
            valid = true;
            var text = box.Text?.Trim();
            if (string.IsNullOrEmpty(text)) return null;
            if (double.TryParse(text, out var v) && v > 0) return v;
            valid = false;
            return null;
        }

        private static string SafeName(string name) => Regex.Replace(name ?? "layer", "[^A-Za-z0-9_]", "_");

        private string DefaultOutputPath(string baseName, FeatureLayer layer)
        {
            var gdb = Project.Current?.DefaultGeodatabasePath;
            return string.IsNullOrEmpty(gdb) ? null : Path.Combine(gdb, baseName + "_" + SafeName(layer.Name));
        }

        private void Warn(string message) => MessageBox.Show(message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);

        private static Task<Dictionary<string, string>> GetFieldNamesAsync(FeatureLayer layer) =>
            QueuedTask.Run(() => layer.GetTable().GetDefinition().GetFields()
                .GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Name, StringComparer.OrdinalIgnoreCase));

        private static Task<SpatialReference> GetSpatialReferenceAsync(FeatureLayer layer) =>
            QueuedTask.Run(() =>
            {
                using var fc = layer.GetFeatureClass();
                return fc.GetDefinition().GetSpatialReference();
            });

        private async Task ExportFlaggedAsync(FeatureLayer layer, int count, string baseName, string label)
        {
            var path = DefaultOutputPath(baseName, layer);
            if (path == null)
            {
                _log.Add($"{label} / {layer.Name}: could not resolve the project's default geodatabase; export skipped.");
                return;
            }
            var messages = new List<string>();
            await QcOutputHelper.CopyToOutputAsync(layer, count, path, messages);
            if (messages.Any(m => m.StartsWith("Copied", StringComparison.Ordinal))) _exports?.Add(path);
            _log.AddRange(messages.Select(m => $"{label} / {layer.Name}: {m}"));
        }

        private async Task ExportDerivedAsync(
            FeatureLayer layer, string geometryType, List<DerivedFeature> features, (string Name, string GpType)[] fields,
            string baseName, string label, string noResults, string countLabel)
        {
            var path = DefaultOutputPath(baseName, layer);
            if (path == null)
            {
                _log.Add($"{label} / {layer.Name}: could not resolve the project's default geodatabase; export skipped.");
                return;
            }
            var sr = await GetSpatialReferenceAsync(layer);
            var messages = new List<string>();
            await QcFeatureClassWriter.WriteFeaturesAsync(sr, geometryType, features, fields, path, messages, noResults, countLabel);
            if (messages.Any(m => m.StartsWith("Wrote", StringComparison.Ordinal))) _exports?.Add(path);
            _log.AddRange(messages.Select(m => $"{label} / {layer.Name}: {m}"));
        }

        private string DatasetOf(FeatureLayer layer) => _datasets.TryGetValue(layer, out var d) ? d : layer.Name;

        /// <summary>
        /// Runs one step on every applicable layer: skip check, per-layer compute (which also selects the
        /// flagged features), then the false-positive gate on the TOTAL across layers, then each layer's
        /// export. Everything is recorded in the step's log record. Returns false if the run should abort
        /// (false-positive mismatch).
        /// </summary>
        private async Task<bool> RunStepAsync(
            string label, CheckBox skip, CheckBox fpCheck, TextBox fpThreshold,
            IReadOnlyList<FeatureLayer> layers, Func<FeatureLayer, Task<LayerRun>> runLayer)
        {
            _records.TryGetValue(label, out var rec);

            if (skip.IsChecked == true)
            {
                _log.Add($"{label}: skipped.");
                FlushLog();
                return true;
            }
            if (layers.Count == 0)
            {
                _log.Add($"{label}: no applicable layers selected; skipped.");
                FlushLog();
                return true;
            }

            var stepTimer = Stopwatch.StartNew();
            try
            {
                var runs = new List<LayerRun>();
                var logged = new Dictionary<LayerRun, QcLogLayerResult>();
                bool hadErrors = false;
                foreach (var layer in layers)
                {
                    var layerTimer = Stopwatch.StartNew();
                    QcLogLayerResult result = null;
                    try
                    {
                        // Start from a clean selection: a previous step's selection would otherwise
                        // restrict geoprocessing tools and be mistaken for this step's results.
                        _run?.BeginUnit(label, layer.Name);
                        await QueuedTask.Run(() => layer.ClearSelection());
                        var run = await runLayer(layer);
                        if (run == null)
                        {
                            result = new QcLogLayerResult
                            {
                                Layer = layer.Name, Dataset = DatasetOf(layer),
                                Note = QcLogLayerResult.NotCheckedPrefix + " - the check could not run on this layer (see the run messages)"
                            };
                            continue;
                        }
                        _log.Add($"{label} / {layer.Name}: {run.Count} flagged.");
                        runs.Add(run);
                        result = new QcLogLayerResult { Layer = layer.Name, Dataset = DatasetOf(layer), Found = run.Count };
                        logged[run] = result;
                    }
                    catch (OperationCanceledException)
                    {
                        if (rec != null) rec.Status = QcStatus.Cancelled;
                        throw;
                    }
                    catch (Exception ex)
                    {
                        hadErrors = true;
                        _log.Add($"{label} / {layer.Name}: error - {ex.Message}");
                        result = new QcLogLayerResult
                        {
                            Layer = layer.Name, Dataset = DatasetOf(layer),
                            Note = QcLogLayerResult.ErrorPrefix + " - " + ex.Message
                        };
                    }
                    finally
                    {
                        _run?.EndUnit();
                        layerTimer.Stop();
                        if (result != null)
                        {
                            result.Seconds = layerTimer.Elapsed.TotalSeconds;
                            rec?.Layers.Add(result);
                        }
                    }
                    FlushLog();
                }

                int total = runs.Sum(r => r.Count);
                var (shouldWrite, abort) = QcUiHelpers.EvaluateFalsePositiveGate(
                    total, fpCheck.IsChecked == true, ParseIntOr(fpThreshold, 0), label, _log);

                if (rec != null)
                {
                    if (abort) rec.Status = QcStatus.FpMismatch;
                    else rec.ApplyGate(rec.FpEnabled); // no abort with the check on means the total matched the expected false positives
                    if (hadErrors) rec.Status += " (one or more layers failed - see the notes)";
                }

                if (shouldWrite)
                {
                    _run?.Current.Working("Exporting results to the default geodatabase", 1, 0);
                    foreach (var run in runs.Where(r => r.Count > 0 && r.Export != null))
                    {
                        _exports = new List<string>();
                        try { await run.Export(); }
                        catch (Exception ex) { _log.Add($"{label} / {run.Layer.Name}: export error - {ex.Message}"); }
                        if (logged.TryGetValue(run, out var lr)) lr.ExportedTo = string.Join("; ", _exports);
                        _exports = null;
                    }
                }

                FlushLog();
                return !abort;
            }
            finally
            {
                stepTimer.Stop();
                if (rec != null) rec.Seconds = stepTimer.Elapsed.TotalSeconds;
            }
        }

        // ------------------------------------------------------------------
        // Run All
        // ------------------------------------------------------------------

        private async void RunButton_Click(object sender, RoutedEventArgs e)
        {
            var polygons = PolygonList.SelectedItems.Cast<QcUiHelpers.LayerItem>().Select(i => i.Layer).ToList();
            var lines = LineList.SelectedItems.Cast<QcUiHelpers.LayerItem>().Select(i => i.Layer).ToList();
            var both = polygons.Concat(lines).ToList();
            if (both.Count == 0)
            {
                Warn("Please select at least one polygon or polyline layer.");
                return;
            }

            // ---- Upfront validation (only for steps that aren't skipped) ----
            var opItem = S1_OpCombo.SelectedItem as OperatorItem;
            int s1V1 = 0, s1V2 = 0;
            if (S1_SkipCheck.IsChecked != true)
            {
                if (opItem == null || !int.TryParse(S1_V1Text.Text?.Trim(), out s1V1) ||
                    (opItem.NeedsSecondValue && !int.TryParse(S1_V2Text.Text?.Trim(), out s1V2)))
                {
                    Warn("Step 1 (Evaluate Part Count) needs whole-number values.");
                    return;
                }
            }

            double s2Tol = 0, s2Margin = 0;
            if (S2_SkipCheck.IsChecked != true)
            {
                if (!double.TryParse(S2_ToleranceText.Text?.Trim(), out s2Tol) || s2Tol <= 0)
                {
                    Warn("Step 2 (Vertex Tolerance Check): the tolerance must be a positive number.");
                    return;
                }
                if (!double.TryParse(S2_MarginText.Text?.Trim(), out s2Margin) || s2Margin < 0 || s2Margin >= s2Tol)
                {
                    Warn("Step 2 (Vertex Tolerance Check): the margin must be zero or more, and smaller than the tolerance.");
                    return;
                }
                if (S2_BelowCheck.IsChecked != true && S2_AtCheck.IsChecked != true)
                {
                    Warn("Step 2 (Vertex Tolerance Check): tick at least one of 'below the tolerance' / 'at the tolerance', or skip the step.");
                    return;
                }
            }

            double s3Thin = 0, s4Thin = 0, s4Width = 0;
            double? s3MaxArea = null, s4MaxArea = null;
            if (S3_SkipCheck.IsChecked != true)
            {
                s3MaxArea = ParseOptionalPositive(S3_MaxAreaText, out bool maxAreaOk);
                if (!double.TryParse(S3_ThinnessText.Text?.Trim(), out s3Thin) || s3Thin <= 0 || s3Thin > 1 || !maxAreaOk)
                {
                    Warn("Step 3 (Overlap Sliver): the Thinness Ratio must be between 0 and 1, and the maximum area blank or positive.");
                    return;
                }
            }
            if (S4_SkipCheck.IsChecked != true)
            {
                s4MaxArea = ParseOptionalPositive(S4_MaxAreaText, out bool maxAreaOk);
                if (!double.TryParse(S4_WidthText.Text?.Trim(), out s4Width) || s4Width < 0 ||
                    !double.TryParse(S4_ThinnessText.Text?.Trim(), out s4Thin) || s4Thin <= 0 || s4Thin > 1 || !maxAreaOk)
                {
                    Warn("Step 4 (Gap Sliver): the gap width must be zero or more, the Thinness Ratio between 0 and 1, and the maximum area blank or positive.");
                    return;
                }
            }

            double s9Length = 0, s9Area = 0;
            if (S9_SkipCheck.IsChecked != true &&
                (!double.TryParse(S9_LengthText.Text?.Trim(), out s9Length) || s9Length < 0 ||
                 !double.TryParse(S9_AreaText.Text?.Trim(), out s9Area) || s9Area < 0))
            {
                Warn("Step 9 (Zero Length / Zero Area): the length and area limits must be zero or a positive number.");
                return;
            }

            var s6PolyFields = SelectedFieldNames(S6_PolyFieldsList);
            var s6LineFields = SelectedFieldNames(S6_LineFieldsList);
            var s7PolyFields = SelectedFieldNames(S7_PolyFieldsList);
            var s7LineFields = SelectedFieldNames(S7_LineFieldsList);
            if (S7_SkipCheck.IsChecked != true &&
                ((polygons.Count > 0 && s7PolyFields.Count == 0) || (lines.Count > 0 && s7LineFields.Count == 0)))
            {
                Warn("Step 7 (Unique Field Value) needs at least one field picked for each layer type you selected (polygons and/or polylines), or skip it.");
                return;
            }
            var polygonSet = new HashSet<FeatureLayer>(polygons);

            bool writeLog = WriteLogCheck.IsChecked == true;
            string logPath = LogPathText.Text?.Trim();
            if (writeLog)
            {
                if (string.IsNullOrEmpty(logPath)) logPath = DefaultLogPath();
                try { logPath = Path.GetFullPath(logPath); }
                catch
                {
                    Warn("The QC output log path is not a valid file path.");
                    return;
                }
            }

            // Capture UI state up front; the work below leaves the UI thread.
            bool s1Export = S1_ExportCheck.IsChecked == true;
            bool s2Below = S2_BelowCheck.IsChecked == true, s2At = S2_AtCheck.IsChecked == true;
            bool s2ExportPoints = S2_ExportPointsCheck.IsChecked == true, s2Export = S2_ExportCheck.IsChecked == true;
            bool s3Export = S3_ExportCheck.IsChecked == true;
            bool s4OnlySlivers = S4_OnlySliversCheck.IsChecked == true, s4Export = S4_ExportCheck.IsChecked == true;
            bool s5ExportHoles = S5_ExportHolesCheck.IsChecked == true, s5Export = S5_ExportCheck.IsChecked == true;
            bool s6Export = S6_ExportCheck.IsChecked == true;
            bool s7Export = S7_ExportCheck.IsChecked == true;
            bool s8Export = S8_ExportCheck.IsChecked == true;
            bool s9Export = S9_ExportCheck.IsChecked == true;

            RunButton.IsEnabled = false;
            CloseButton.IsEnabled = false;
            _log = new List<string> { "Running..." };
            FlushLog();
            var cts = new CancellationTokenSource();
            _cts = cts;
            CancelButton.Content = "Cancel";
            CancelButton.IsEnabled = true;
            CancelButton.Visibility = Visibility.Visible;
            string outcome = "Finished";
            _records.Clear();
            _datasets.Clear();
            _exports = null;
            _runLog = new QcRunLog
            {
                ToolbarId = QcRunLog.ReviewerToolbarId,
                ToolbarName = "Reviewer QC",
                Title = "Reviewer QC Output Log",
                Started = DateTime.Now,
                Project = Project.Current?.Path ?? "",
                DefaultGeodatabase = Project.Current?.DefaultGeodatabasePath ?? ""
            };

            try
            {
                // ---- Plan the run: every (step x layer), weighted by expected cost ----
                // Factors are rough milliseconds per record; they only shape the bar and the time
                // estimate (the estimate corrects itself from the speed actually measured).
                var counts = new Dictionary<FeatureLayer, long>();
                foreach (var layer in both)
                {
                    counts[layer] = await QcRunUi.CountAsync(layer);
                    var described = await QcRunUi.DescribeAsync(layer);
                    _datasets[layer] = described.Dataset;
                    _runLog.Layers.Add(new QcLogLayerInfo
                    {
                        Role = polygonSet.Contains(layer) ? "Polygons" : "Polylines",
                        Name = layer.Name, Source = described.Source, Features = counts[layer]
                    });
                }

                string Fields(List<string> f) => f.Count == 0 ? "(none)" : string.Join(", ", f);
                string Limit(double? v) => v.HasValue ? v.Value.ToString() : "no limit";
                string opText = opItem == null ? "" : opItem.NeedsSecondValue ? $"{opItem.Label} {s1V1} and {s1V2}" : $"{opItem.Label} {s1V1}";

                var specs = new List<(string Key, string Name, CheckBox Skip, List<FeatureLayer> Layers, double Factor, CheckBox Fp, TextBox FpText, CheckBox Export, string Parameters)>
                {
                    ("PartCount", "Evaluate Part Count", S1_SkipCheck, both, 0.1, S1_FpCheck, S1_FpThresholdText, S1_ExportCheck,
                        $"Number of parts: {opText}"),
                    ("VertexTolerance", "Vertex Tolerance Check", S2_SkipCheck, both, 0.4, S2_FpCheck, S2_FpThresholdText, S2_ExportCheck,
                        $"Tolerance = {s2Tol}; at-tolerance margin = {s2Margin}; report below = {YesNo(s2Below)}; report at tolerance = {YesNo(s2At)}; export vertex points = {YesNo(s2ExportPoints)}"),
                    ("OverlapSliver", "Polygon Overlap is Sliver", S3_SkipCheck, polygons, 1.0, S3_FpCheck, S3_FpThresholdText, S3_ExportCheck,
                        $"Thinness ratio below {s3Thin}; maximum area = {Limit(s3MaxArea)}"),
                    ("GapSliver", "Polygon Gap is Sliver", S4_SkipCheck, polygons, s4Width > 0 ? 8.0 : 1.0, S4_FpCheck, S4_FpThresholdText, S4_ExportCheck,
                        $"Maximum gap width = {s4Width} (0 = fully enclosed gaps only); thinness ratio below {s4Thin}; maximum area = {Limit(s4MaxArea)}; only output slivers = {YesNo(s4OnlySlivers)}"),
                    ("PolygonHoles", "Find Polygons with Holes", S5_SkipCheck, polygons, 0.3, S5_FpCheck, S5_FpThresholdText, S5_ExportCheck,
                        $"Export hole polygons = {YesNo(s5ExportHoles)}"),
                    ("DuplicateFeature", "Duplicate Feature", S6_SkipCheck, both, 0.5, S6_FpCheck, S6_FpThresholdText, S6_ExportCheck,
                        $"Also matching attributes - polygons: {Fields(s6PolyFields)}; polylines: {Fields(s6LineFields)}"),
                    ("UniqueFieldValue", "Unique Field Value", S7_SkipCheck, both, 0.1, S7_FpCheck, S7_FpThresholdText, S7_ExportCheck,
                        $"Fields - polygons: {Fields(s7PolyFields)}; polylines: {Fields(s7LineFields)}"),
                    ("CheckGeometry", "Check Geometry", S8_SkipCheck, both, 0.5, S8_FpCheck, S8_FpThresholdText, S8_ExportCheck,
                        "Esri's Check Geometry tool, default options"),
                    ("ZeroLengthArea", "Zero Length / Zero Area", S9_SkipCheck, both, 0.15, S9_FpCheck, S9_FpThresholdText, S9_ExportCheck,
                        $"Polyline length at or below {s9Length}; polygon area at or below {s9Area}"),
                };

                var units = new List<QcRunProgress.Unit>();
                int stepNumber = 0;
                foreach (var spec in specs)
                {
                    stepNumber++;
                    bool skippedByUser = spec.Skip.IsChecked == true;
                    bool noLayers = spec.Layers.Count == 0;
                    var rec = new QcLogStep
                    {
                        Number = stepNumber, Key = spec.Key, Name = spec.Name, Parameters = spec.Parameters,
                        Skipped = skippedByUser || noLayers,
                        FpEnabled = spec.Fp.IsChecked == true, ExpectedFp = ParseIntOr(spec.FpText, 0),
                        ExportRequested = spec.Export.IsChecked == true,
                        Status = skippedByUser ? QcStatus.Skipped : noLayers ? "Skipped - no applicable layers selected" : QcStatus.NotRun
                    };
                    _records[spec.Name] = rec;
                    _runLog.Steps.Add(rec);

                    if (rec.Skipped) continue;
                    foreach (var layer in spec.Layers)
                        units.Add(new QcRunProgress.Unit
                        {
                            Step = spec.Name,
                            StepNumber = stepNumber,
                            Layer = layer.Name,
                            Records = counts[layer],
                            Weight = Math.Max(counts[layer], 1) * spec.Factor
                        });
                }
                _run = new QcRunProgress(units, specs.Count, cts.Token);
                ProgressPanel.Start(_run);
                var progress = _run.Current;

                bool ok;

                // ---- Step 1: Evaluate Part Count ----
                ok = await RunStepAsync("Evaluate Part Count", S1_SkipCheck, S1_FpCheck, S1_FpThresholdText, both, async layer =>
                {
                    var oids = await QueuedTask.Run(() =>
                        QcOutputHelper.SelectOids(layer, PartCountChecker.FindOids(layer, opItem.Operator, s1V1, s1V2, progress)));
                    return new LayerRun
                    {
                        Layer = layer,
                        Count = oids.Count,
                        Export = () => s1Export ? ExportFlaggedAsync(layer, oids.Count, "PartCountOutput", "Evaluate Part Count") : Task.CompletedTask
                    };
                });
                if (!ok) { outcome = "Stopped by false-positive check"; return; }

                // ---- Step 2: Vertex Tolerance Check ----
                ok = await RunStepAsync("Vertex Tolerance Check", S2_SkipCheck, S2_FpCheck, S2_FpThresholdText, both, async layer =>
                {
                    var scan = await QueuedTask.Run(() =>
                    {
                        var found = DuplicateVertexChecker.Find(f => layer.Search(f), s2Tol, s2Margin, s2Below, s2At, progress);
                        QcOutputHelper.SelectOids(layer, found.Oids.ToList());
                        return found;
                    });
                    return new LayerRun
                    {
                        Layer = layer,
                        Count = scan.Oids.Count,
                        Export = async () =>
                        {
                            if (s2Export) await ExportFlaggedAsync(layer, scan.Oids.Count, "VertexToleranceOutput", "Vertex Tolerance Check");
                            if (s2ExportPoints)
                                await ExportDerivedAsync(layer, "POINT", DuplicateVertexChecker.ToFeatures(scan.Hits), DuplicateVertexChecker.OutputFields,
                                    "VertexToleranceVertices", "Vertex Tolerance Check", "No vertices to report.", "vertex point(s)");
                        }
                    };
                });
                if (!ok) { outcome = "Stopped by false-positive check"; return; }

                // ---- Step 3: Polygon Overlap is Sliver ----
                ok = await RunStepAsync("Polygon Overlap is Sliver", S3_SkipCheck, S3_FpCheck, S3_FpThresholdText, polygons, async layer =>
                {
                    var scan = await QueuedTask.Run(() =>
                        SliverOverlapFinder.Find(f => layer.Search(f), f => layer.Search(f), true, s3Thin, s3MaxArea, progress));
                    return new LayerRun
                    {
                        Layer = layer,
                        Count = scan.Results.Count,
                        Export = async () =>
                        {
                            if (!s3Export) return;
                            var path = DefaultOutputPath("OverlapSliverOutput", layer);
                            if (path == null) { _log.Add($"Polygon Overlap is Sliver / {layer.Name}: could not resolve the project's default geodatabase; export skipped."); return; }
                            var messages = new List<string>();
                            await QcFeatureClassWriter.WriteSliverResultsAsync(await GetSpatialReferenceAsync(layer), scan.Results, path, messages);
                            if (messages.Any(m => m.StartsWith("Wrote", StringComparison.Ordinal))) _exports?.Add(path);
                            _log.AddRange(messages.Select(m => $"Polygon Overlap is Sliver / {layer.Name}: {m}"));
                        }
                    };
                });
                if (!ok) { outcome = "Stopped by false-positive check"; return; }

                // ---- Step 4: Polygon Gap is Sliver ----
                ok = await RunStepAsync("Polygon Gap is Sliver", S4_SkipCheck, S4_FpCheck, S4_FpThresholdText, polygons, async layer =>
                {
                    var scan = await QueuedTask.Run(() =>
                        SliverGapFinder.Find(f => layer.Search(f), f => layer.Search(f), true, s4Width, s4Thin, s4MaxArea, s4OnlySlivers, progress));
                    _log.Add($"Polygon Gap is Sliver / {layer.Name}: found {scan.Candidates} gap(s) ({scan.EnclosedFound} enclosed, {scan.OpenFound} open strips), {scan.SliverCount} classified as slivers"
                        + (s4OnlySlivers ? $"; {scan.SkippedNonSliver} non-sliver shape(s) skipped unvalidated." : "."));
                    _log.Add($"Polygon Gap is Sliver / {layer.Name}: where the time went - " + string.Join("; ", scan.Notes));
                    return new LayerRun
                    {
                        Layer = layer,
                        Count = scan.Results.Count,
                        Export = () => s4Export
                            ? ExportDerivedAsync(layer, "POLYGON", SliverGapFinder.ToFeatures(scan.Results), SliverGapFinder.OutputFields,
                                "GapSliverOutput", "Polygon Gap is Sliver", "No gaps to write.", "gap polygon(s)")
                            : Task.CompletedTask
                    };
                });
                if (!ok) { outcome = "Stopped by false-positive check"; return; }

                // ---- Step 5: Find Polygons with Holes ----
                ok = await RunStepAsync("Find Polygons with Holes", S5_SkipCheck, S5_FpCheck, S5_FpThresholdText, polygons, async layer =>
                {
                    var (oids, holes) = await QueuedTask.Run(() =>
                    {
                        var found = PolygonHoleFinder.Find(f => layer.Search(f), progress);
                        QcOutputHelper.SelectOids(layer, found.Oids);
                        return found;
                    });
                    return new LayerRun
                    {
                        Layer = layer,
                        Count = oids.Count,
                        Export = async () =>
                        {
                            if (s5Export) await ExportFlaggedAsync(layer, oids.Count, "PolygonsWithHolesOutput", "Find Polygons with Holes");
                            if (s5ExportHoles)
                                await ExportDerivedAsync(layer, "POLYGON", holes, PolygonHoleFinder.OutputFields,
                                    "PolygonHoles", "Find Polygons with Holes", "No holes found.", "hole polygon(s)");
                        }
                    };
                });
                if (!ok) { outcome = "Stopped by false-positive check"; return; }

                // ---- Step 6: Duplicate Feature ----
                ok = await RunStepAsync("Duplicate Feature", S6_SkipCheck, S6_FpCheck, S6_FpThresholdText, both, async layer =>
                {
                    var available = await GetFieldNamesAsync(layer);
                    var wanted = polygonSet.Contains(layer) ? s6PolyFields : s6LineFields;
                    var fields = wanted.Where(available.ContainsKey).Select(f => available[f]).ToList();
                    foreach (var missing in wanted.Where(f => !available.ContainsKey(f)))
                        _log.Add($"Duplicate Feature / {layer.Name}: field '{missing}' not found; not compared for this layer.");

                    var oids = await QueuedTask.Run(() => QcOutputHelper.SelectOids(layer, DuplicateFeatureFinder.FindOids(layer, fields, progress)));
                    return new LayerRun
                    {
                        Layer = layer,
                        Count = oids.Count,
                        Export = () => s6Export ? ExportFlaggedAsync(layer, oids.Count, "DuplicateFeatureOutput", "Duplicate Feature") : Task.CompletedTask
                    };
                });
                if (!ok) { outcome = "Stopped by false-positive check"; return; }

                // ---- Step 7: Unique Field Value ----
                ok = await RunStepAsync("Unique Field Value", S7_SkipCheck, S7_FpCheck, S7_FpThresholdText, both, async layer =>
                {
                    var available = await GetFieldNamesAsync(layer);
                    var wanted = polygonSet.Contains(layer) ? s7PolyFields : s7LineFields;
                    var fields = wanted.Where(available.ContainsKey).Select(f => available[f]).ToList();
                    if (fields.Count == 0)
                    {
                        _log.Add($"Unique Field Value / {layer.Name}: none of the chosen field(s) exist on this layer; skipped.");
                        return null;
                    }
                    foreach (var missing in wanted.Where(f => !available.ContainsKey(f)))
                        _log.Add($"Unique Field Value / {layer.Name}: field '{missing}' not found; not compared for this layer.");

                    var oids = await QueuedTask.Run(() => QcOutputHelper.SelectOids(layer, UniqueFieldValueChecker.FindOids(layer, fields, progress)));
                    return new LayerRun
                    {
                        Layer = layer,
                        Count = oids.Count,
                        Export = () => s7Export ? ExportFlaggedAsync(layer, oids.Count, "UniqueFieldValueOutput", "Unique Field Value") : Task.CompletedTask
                    };
                });
                if (!ok) { outcome = "Stopped by false-positive check"; return; }

                // ---- Step 8: Check Geometry (Esri's standalone tool) ----
                ok = await RunStepAsync("Check Geometry", S8_SkipCheck, S8_FpCheck, S8_FpThresholdText, both, async layer =>
                {
                    progress.Working("Running Esri's Check Geometry tool (it has no record counter)", 0, 0.9);
                    string tempTable = $"memory\\CheckGeometryTemp_{_tableCounter++}";
                    var overwrite = Geoprocessing.MakeEnvironmentArray(overwriteoutput: true);

                    var check = await Geoprocessing.ExecuteToolAsync("management.CheckGeometry",
                        Geoprocessing.MakeValueArray(layer, tempTable), overwrite, null, null, GPExecuteToolFlags.None);
                    if (check.IsFailed)
                    {
                        _log.Add($"Check Geometry / {layer.Name}: failed - " + string.Join("; ", check.ErrorMessages.Select(m => m.Text)));
                        return null;
                    }

                    var counted = await Geoprocessing.ExecuteToolAsync("management.GetCount",
                        Geoprocessing.MakeValueArray(tempTable), null, null, null, GPExecuteToolFlags.None);
                    int problems = int.TryParse(counted.ReturnValue, out var c) ? c : 0;

                    return new LayerRun
                    {
                        Layer = layer,
                        Count = problems,
                        Export = async () =>
                        {
                            if (!s8Export) return;
                            var path = DefaultOutputPath("CheckGeometryOutput", layer);
                            if (path == null) { _log.Add($"Check Geometry / {layer.Name}: could not resolve the project's default geodatabase; export skipped."); return; }
                            var copy = await Geoprocessing.ExecuteToolAsync("management.CopyRows",
                                Geoprocessing.MakeValueArray(tempTable, path), overwrite, null, null, GPExecuteToolFlags.None);
                            if (!copy.IsFailed) _exports?.Add(path);
                            _log.Add(copy.IsFailed
                                ? $"Check Geometry / {layer.Name}: CopyRows failed - " + string.Join("; ", copy.ErrorMessages.Select(m => m.Text))
                                : $"Check Geometry / {layer.Name}: exported {problems} problem(s) to {path}");
                        }
                    };
                });
                if (!ok) { outcome = "Stopped by false-positive check"; return; }

                // ---- Step 9: Zero Length / Zero Area ----
                ok = await RunStepAsync("Zero Length / Zero Area", S9_SkipCheck, S9_FpCheck, S9_FpThresholdText, both, async layer =>
                {
                    var scan = await QueuedTask.Run(() =>
                    {
                        var found = ZeroSizeChecker.Find(f => layer.Search(f), s9Length, s9Area, progress);
                        QcOutputHelper.SelectOids(layer, found.Oids);
                        return found;
                    });
                    return new LayerRun
                    {
                        Layer = layer,
                        Count = scan.Oids.Count,
                        Export = () => s9Export ? ExportFlaggedAsync(layer, scan.Oids.Count, "ZeroLengthAreaOutput", "Zero Length / Zero Area") : Task.CompletedTask
                    };
                });
                if (!ok) { outcome = "Stopped by false-positive check"; return; }

                _log.Add("All steps complete.");
                FlushLog();
            }
            catch (OperationCanceledException)
            {
                outcome = "Cancelled";
                _log.Add("Cancelled by user. Results already exported by earlier steps are kept.");
                FlushLog();
            }
            catch (Exception ex)
            {
                outcome = "Stopped by an error";
                _log.Add("Error: " + ex.Message);
                FlushLog();
            }
            finally
            {
                _runLog.Finished = DateTime.Now;
                _runLog.Outcome = outcome;
                ProgressPanel.Stop(outcome);

                if (writeLog)
                {
                    try
                    {
                        _runLog.Messages.AddRange(_log.Where(m => m != "Running..."));
                        string written = _runLog.Write(logPath);
                        _log.Add($"QC output log written to {written}");
                    }
                    catch (Exception ex)
                    {
                        _log.Add($"Could not write the QC output log to {logPath}: {ex.Message}");
                    }
                    FlushLog();
                }

                CancelButton.Visibility = Visibility.Collapsed;
                _cts = null;
                _run = null;
                cts.Dispose();
                RunButton.IsEnabled = true;
                CloseButton.IsEnabled = true;
            }
        }
    }
}

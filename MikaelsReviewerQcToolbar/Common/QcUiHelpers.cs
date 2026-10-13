using ArcGIS.Core.CIM;
using ArcGIS.Core.Data;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Mapping;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Controls;

namespace MikaelsReviewerQcToolbar.Common
{
    /// <summary>Shared UI helpers reused by every QC tool dialog.</summary>
    internal static class QcUiHelpers
    {
        public class LayerItem
        {
            public string Name { get; set; }
            public FeatureLayer Layer { get; set; }
            public override string ToString() => Name;
        }

        public class FieldItem
        {
            public string Name { get; set; }
            public override string ToString() => Name;
        }

        public static Task<List<LayerItem>> GetPolygonLayerItemsAsync() =>
            GetLayerItemsAsync(esriGeometryType.esriGeometryPolygon);

        public static Task<List<LayerItem>> GetPolylineLayerItemsAsync() =>
            GetLayerItemsAsync(esriGeometryType.esriGeometryPolyline);

        public static Task<List<LayerItem>> GetPolylineOrPolygonLayerItemsAsync() =>
            GetLayerItemsAsync(esriGeometryType.esriGeometryPolyline, esriGeometryType.esriGeometryPolygon);

        public static Task<List<LayerItem>> GetAnyFeatureLayerItemsAsync() =>
            GetLayerItemsAsync();

        public static async Task<List<LayerItem>> GetLayerItemsAsync(params esriGeometryType[] shapeTypes)
        {
            return await QueuedTask.Run(() =>
            {
                var map = MapView.Active?.Map;
                var layers = map?.GetLayersAsFlattenedList().OfType<FeatureLayer>() ?? Enumerable.Empty<FeatureLayer>();
                if (shapeTypes != null && shapeTypes.Length > 0)
                    layers = layers.Where(l => shapeTypes.Contains(l.ShapeType));
                return layers.Select(l => new LayerItem { Name = l.Name, Layer = l }).ToList();
            });
        }

        /// <summary>
        /// The layer's distance unit (e.g. "Meter"), so tolerance/distance labels say what they
        /// are measured in. Geographic layers are flagged since a small number is huge there.
        /// </summary>
        public static async Task<string> GetDistanceUnitHintAsync(FeatureLayer layer)
        {
            if (layer == null) return "map units";
            return await QueuedTask.Run(() =>
            {
                using var featureClass = layer.GetFeatureClass();
                var sr = featureClass.GetDefinition().GetSpatialReference();
                if (sr == null) return "map units";
                string name = sr.Unit?.Name ?? "map units";
                return sr.IsGeographic ? $"{name} - geographic layer, 1 is huge" : name;
            });
        }

        public static async Task<List<FieldItem>> GetFieldItemsAsync(FeatureLayer layer)
        {
            if (layer == null) return new List<FieldItem>();
            return await QueuedTask.Run(() =>
            {
                var fields = layer.GetTable().GetDefinition().GetFields();
                return fields.Where(f => f.FieldType != FieldType.Geometry && f.FieldType != FieldType.OID)
                    .Select(f => new FieldItem { Name = f.Name })
                    .ToList();
            });
        }

        /// <summary>
        /// Wires a "use project default geodatabase" checkbox to an output-path textbox +
        /// browse button: checking it fills in "&lt;DefaultGdb&gt;\&lt;defaultName&gt;" and
        /// disables Browse, but leaves the textbox editable. Unchecking clears it and hands
        /// full control back.
        /// </summary>
        public static void ConfigureDefaultGdbToggle(CheckBox toggle, TextBox outputPathTextBox, Button browseButton, string defaultName)
        {
            toggle.Checked += (s, e) =>
            {
                browseButton.IsEnabled = false;
                var gdbPath = ArcGIS.Desktop.Core.Project.Current?.DefaultGeodatabasePath;
                outputPathTextBox.Text = string.IsNullOrEmpty(gdbPath) ? "" : Path.Combine(gdbPath, defaultName);
            };
            toggle.Unchecked += (s, e) =>
            {
                browseButton.IsEnabled = true;
                outputPathTextBox.Text = "";
            };
        }

        /// <summary>
        /// False-positive gate used by the batch run. When enabled and the count doesn't match the
        /// expected threshold, the whole run aborts (ShouldWrite=false, Abort=true). When it matches,
        /// every hit is an already-known false positive, so no output is written even though the
        /// run continues (ShouldWrite=false, Abort=false). When disabled, output is always written.
        /// </summary>
        public static (bool ShouldWrite, bool Abort) EvaluateFalsePositiveGate(
            int count, bool fpEnabled, int fpThreshold, string label, List<string> messages)
        {
            if (!fpEnabled) return (true, false);
            if (count != fpThreshold)
            {
                messages.Add($"[{label}] {count} error(s) found when false positive input was {fpThreshold}, recheck errors. Aborting remaining steps.");
                return (false, true);
            }
            messages.Add($"{count} error(s) found matches expected false positive count of {fpThreshold} for {label} -- false positives equal errors, no output generated.");
            return (false, false);
        }

        /// <summary>
        /// A native WinForms TextBox hosted via WindowsFormsHost, used for every tool's log
        /// panel. Painted via Win32/GDI, independent of WPF's renderer and Pro's WPF theme.
        /// </summary>
        public static System.Windows.Forms.TextBox CreateLogTextBox()
        {
            return new System.Windows.Forms.TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = System.Windows.Forms.ScrollBars.Vertical,
                BackColor = System.Drawing.Color.White,
                ForeColor = System.Drawing.Color.Black,
                BorderStyle = System.Windows.Forms.BorderStyle.None,
                Font = new System.Drawing.Font("Segoe UI", 9)
            };
        }
    }
}

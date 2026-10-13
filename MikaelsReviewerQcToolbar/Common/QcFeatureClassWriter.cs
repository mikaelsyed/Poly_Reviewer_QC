using ArcGIS.Core.Geometry;
using ArcGIS.Desktop.Core.Geoprocessing;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace MikaelsReviewerQcToolbar.Common
{
    /// <summary>
    /// Writes brand-new derived features (sliver/gap polygons, hole polygons, vertex points - geometry that
    /// doesn't correspond to any single existing input feature) to a file-geodatabase output
    /// feature class: creates the schema via the standard CreateFeatureclass/AddField tools
    /// (overwriting an existing output of the same name), then inserts the rows.
    /// File geodatabase output only.
    /// </summary>
    internal static class QcFeatureClassWriter
    {
        public static Task WriteSliverResultsAsync(SpatialReference sr, List<SliverResult> results, string outputPath, List<string> messages)
        {
            var polygons = results.Select(r =>
            {
                var p = new DerivedFeature { Geometry = r.Geometry };
                p.Attributes["OID_A"] = r.OidA;
                p.Attributes["OID_B"] = r.OidB;
                p.Attributes["AREA"] = r.Area;
                p.Attributes["THINNESS_RATIO"] = r.ThinnessRatio;
                return p;
            }).ToList();

            var fields = new[] { ("OID_A", "LONG"), ("OID_B", "LONG"), ("AREA", "DOUBLE"), ("THINNESS_RATIO", "DOUBLE") };
            return WriteFeaturesAsync(sr, "POLYGON", polygons, fields, outputPath, messages, "No slivers matched; output not created.", "sliver polygon(s)");
        }

        public static async Task WriteFeaturesAsync(
            SpatialReference sr, string geometryType, List<DerivedFeature> features, (string Name, string GpType)[] fields,
            string outputPath, List<string> messages, string noResultsMessage, string countLabel)
        {
            if (features.Count == 0)
            {
                messages.Add(noResultsMessage);
                return;
            }

            string gdbPath = Path.GetDirectoryName(outputPath);
            string fcName = Path.GetFileName(outputPath);
            if (string.IsNullOrEmpty(gdbPath) || !gdbPath.EndsWith(".gdb", StringComparison.OrdinalIgnoreCase))
            {
                messages.Add("Output must be a feature class inside a file geodatabase (a \".gdb\" folder).");
                return;
            }

            var overwrite = Geoprocessing.MakeEnvironmentArray(overwriteoutput: true);

            var createParams = Geoprocessing.MakeValueArray(gdbPath, fcName, geometryType, null, null, null, sr);
            var createResult = await Geoprocessing.ExecuteToolAsync("management.CreateFeatureclass", createParams,
                overwrite, null, null, GPExecuteToolFlags.None);
            if (createResult.IsFailed)
            {
                messages.Add("CreateFeatureclass failed: " + string.Join("; ", createResult.ErrorMessages.Select(m => m.Text)));
                return;
            }

            foreach (var (name, type) in fields)
            {
                var addFieldParams = Geoprocessing.MakeValueArray(outputPath, name, type);
                var addFieldResult = await Geoprocessing.ExecuteToolAsync("management.AddField", addFieldParams,
                    overwrite, null, null, GPExecuteToolFlags.None);
                if (addFieldResult.IsFailed)
                {
                    messages.Add($"AddField {name} failed: " + string.Join("; ", addFieldResult.ErrorMessages.Select(m => m.Text)));
                    return;
                }
            }

            await ArcGIS.Desktop.Framework.Threading.Tasks.QueuedTask.Run(() => FeatureInserter.Insert(gdbPath, fcName, features));

            messages.Add($"Wrote {features.Count} {countLabel} to {outputPath}");
        }
    }
}

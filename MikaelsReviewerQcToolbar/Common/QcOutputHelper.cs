using ArcGIS.Core.Data;
using ArcGIS.Desktop.Core.Geoprocessing;
using ArcGIS.Desktop.Mapping;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace MikaelsReviewerQcToolbar.Common
{
    /// <summary>
    /// Shared "select the flagged features, optionally copy them to an output feature class"
    /// finish step used by every check that flags existing input features (as opposed to the
    /// sliver checks, which derive brand new geometry and always need their own output).
    /// Must be called from within QueuedTask.Run for the selection step; the optional copy
    /// step calls a geoprocessing tool and must NOT be awaited from inside QueuedTask.Run.
    /// </summary>
    internal static class QcOutputHelper
    {
        public static List<long> SelectOids(FeatureLayer layer, List<long> oids)
        {
            if (oids.Count > 0)
                layer.Select(new QueryFilter { ObjectIDs = oids }, SelectionCombinationMethod.New);
            else
                layer.ClearSelection();
            return oids;
        }

        public static async Task CopyToOutputAsync(FeatureLayer layer, int flaggedCount, string outputPath, List<string> messages)
        {
            if (string.IsNullOrEmpty(outputPath)) return;

            if (flaggedCount == 0)
            {
                messages.Add("No features flagged; output not created.");
                return;
            }

            var gpParams = Geoprocessing.MakeValueArray(layer, outputPath);
            var gpResult = await Geoprocessing.ExecuteToolAsync("management.CopyFeatures", gpParams,
                Geoprocessing.MakeEnvironmentArray(overwriteoutput: true), null, null, GPExecuteToolFlags.None);

            messages.Add(gpResult.IsFailed
                ? "CopyFeatures failed: " + string.Join("; ", gpResult.ErrorMessages.Select(m => m.Text))
                : $"Copied {flaggedCount} flagged feature(s) to {outputPath}");
        }
    }
}

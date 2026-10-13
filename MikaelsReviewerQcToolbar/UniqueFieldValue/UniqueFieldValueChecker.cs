using ArcGIS.Core.Data;
using ArcGIS.Desktop.Mapping;
using MikaelsReviewerQcToolbar.Common;
using System.Collections.Generic;
using System.Linq;

namespace MikaelsReviewerQcToolbar.UniqueFieldValue
{
    /// <summary>
    /// Re-implementation of Data Reviewer's "Unique Field Value" check: flags rows whose
    /// combined value across the chosen fields is not unique in the layer. Must be called
    /// from within QueuedTask.Run.
    /// </summary>
    internal static class UniqueFieldValueChecker
    {
        public static List<long> FindOids(FeatureLayer layer, List<string> fields, QcProgress progress = null)
        {
            var groups = new Dictionary<string, List<long>>();
            progress?.BeginRecords("Reading field values");
            using var cursor = layer.Search(null);
            while (cursor.MoveNext())
            {
                using var feature = (Feature)cursor.Current;
                progress?.Tick(feature.GetObjectID());
                string key = string.Join("|", fields.Select(f => feature[f]?.ToString() ?? "<null>"));
                if (!groups.TryGetValue(key, out var list))
                    groups[key] = list = new List<long>();
                list.Add(feature.GetObjectID());
            }
            return groups.Values.Where(l => l.Count > 1).SelectMany(l => l).ToList();
        }
    }
}

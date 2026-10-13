using ArcGIS.Core.Data;
using ArcGIS.Core.Geometry;
using ArcGIS.Desktop.Mapping;
using MikaelsReviewerQcToolbar.Common;
using System;
using System.Collections.Generic;
using System.Linq;

namespace MikaelsReviewerQcToolbar.DuplicateFeature
{
    /// <summary>
    /// Re-implementation of Data Reviewer's "Duplicate Feature" check: flags features that are
    /// collocated (identical geometry) with another feature in the same layer, optionally also
    /// requiring the chosen attribute fields to match. Buckets by rounded extent (+ attribute
    /// key) first so only genuinely nearby candidates pay for the exact geometry comparison.
    /// Must be called from within QueuedTask.Run.
    /// </summary>
    internal static class DuplicateFeatureFinder
    {
        public static List<long> FindOids(FeatureLayer layer, List<string> compareFields, QcProgress progress = null)
        {
            var records = new List<(long Oid, Geometry Geom, string BucketKey)>();
            progress?.BeginRecords("Reading features", 0, 0.7);
            using (var cursor = layer.Search(null))
            {
                while (cursor.MoveNext())
                {
                    using var feature = (Feature)cursor.Current;
                    progress?.Tick(feature.GetObjectID());
                    var geom = feature.GetShape();
                    if (geom == null || geom.IsEmpty) continue;

                    string attrKey = compareFields.Count == 0
                        ? ""
                        : string.Join("|", compareFields.Select(f => feature[f]?.ToString() ?? ""));

                    var ext = geom.Extent;
                    string bucketKey = $"{Math.Round(ext.XMin, 3)}_{Math.Round(ext.YMin, 3)}_{Math.Round(ext.XMax, 3)}_{Math.Round(ext.YMax, 3)}_{attrKey}";
                    records.Add((feature.GetObjectID(), geom, bucketKey));
                }
            }

            var oids = new HashSet<long>();
            var groups = records.GroupBy(r => r.BucketKey).ToList();
            progress?.Begin("Comparing nearby features", groups.Count, 0.7, 0.3);
            foreach (var group in groups)
            {
                progress?.Tick();
                var list = group.ToList();
                if (list.Count < 2) continue;
                for (int i = 0; i < list.Count; i++)
                {
                    for (int j = i + 1; j < list.Count; j++)
                    {
                        if (GeometryEngine.Instance.Equals(list[i].Geom, list[j].Geom))
                        {
                            oids.Add(list[i].Oid);
                            oids.Add(list[j].Oid);
                        }
                    }
                }
            }
            return oids.ToList();
        }
    }
}

using ArcGIS.Core.Data;
using ArcGIS.Core.Geometry;
using ArcGIS.Desktop.Mapping;
using MikaelsReviewerQcToolbar.Common;
using System;
using System.Collections.Generic;

namespace MikaelsReviewerQcToolbar.EvaluatePartCount
{
    internal enum PartCountOperator { Equal, NotEqual, GreaterThan, GreaterThanOrEqual, LessThan, LessThanOrEqual, Between, Outside }

    /// <summary>
    /// Re-implementation of Data Reviewer's "Evaluate Part Count" check: flags features whose
    /// geometry part count matches (or falls outside, for Between/Outside) a user-specified
    /// query. Must be called from within QueuedTask.Run.
    /// </summary>
    internal static class PartCountChecker
    {
        public static List<long> FindOids(FeatureLayer layer, PartCountOperator op, int value1, int value2, QcProgress progress = null)
        {
            var oids = new List<long>();
            progress?.BeginRecords("Counting parts");
            using var cursor = layer.Search(null);
            while (cursor.MoveNext())
            {
                using var feature = (Feature)cursor.Current;
                progress?.Tick(feature.GetObjectID());
                var geometry = feature.GetShape();
                int partCount = CountParts(geometry);

                bool match = op switch
                {
                    PartCountOperator.Equal => partCount == value1,
                    PartCountOperator.NotEqual => partCount != value1,
                    PartCountOperator.GreaterThan => partCount > value1,
                    PartCountOperator.GreaterThanOrEqual => partCount >= value1,
                    PartCountOperator.LessThan => partCount < value1,
                    PartCountOperator.LessThanOrEqual => partCount <= value1,
                    PartCountOperator.Between => partCount >= Math.Min(value1, value2) && partCount <= Math.Max(value1, value2),
                    PartCountOperator.Outside => partCount < Math.Min(value1, value2) || partCount > Math.Max(value1, value2),
                    _ => false
                };

                if (match) oids.Add(feature.GetObjectID());
            }
            return oids;
        }

        /// <summary>
        /// Separate pieces of a feature. A polygon's holes are rings too, but they are not
        /// separate pieces, so only exterior rings are counted for polygons.
        /// </summary>
        private static int CountParts(Geometry geometry)
        {
            if (geometry == null || geometry.IsEmpty) return 0;
            if (geometry is Polygon polygon)
            {
                int exteriorRings = 0;
                for (int i = 0; i < polygon.PartCount; i++)
                    if (polygon.IsExteriorRing(i)) exteriorRings++;
                return exteriorRings;
            }
            return (geometry as Multipart)?.PartCount ?? 1;
        }
    }
}

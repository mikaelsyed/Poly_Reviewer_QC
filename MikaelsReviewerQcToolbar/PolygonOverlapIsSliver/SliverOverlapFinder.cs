using ArcGIS.Core.Data;
using ArcGIS.Core.Geometry;
using MikaelsReviewerQcToolbar.Common;
using System;

namespace MikaelsReviewerQcToolbar.PolygonOverlapIsSliver
{
    /// <summary>
    /// Re-implementation of Data Reviewer's "Polygon Overlap is Sliver" check: for every pair
    /// of overlapping polygons from two feature sources, computes the overlap geometry's
    /// thinness ratio (4*pi*area/perimeter^2, standard Polsby-Popper compactness) and flags it
    /// if the overlap is thinner than the threshold and (optionally) small enough. Uses the
    /// source's spatial index via SpatialQueryFilter rather than a full cross join. Must be
    /// called from within QueuedTask.Run.
    /// </summary>
    internal static class SliverOverlapFinder
    {
        public static SliverScan Find(
            Func<QueryFilter, RowCursor> searchA, Func<QueryFilter, RowCursor> searchB, bool sameLayer,
            double thinnessThreshold, double? maxArea, QcProgress progress = null)
        {
            var scan = new SliverScan();

            progress?.BeginRecords("Finding overlaps");
            using var cursorA = searchA(null);
            while (cursorA.MoveNext())
            {
                using var featureA = (Feature)cursorA.Current;
                progress?.Tick(featureA.GetObjectID());
                if (featureA.GetShape() is not Polygon geomA || geomA.IsEmpty) continue;

                var spatialFilter = new SpatialQueryFilter { FilterGeometry = geomA, SpatialRelationship = SpatialRelationship.Intersects };
                using var cursorB = searchB(spatialFilter);
                while (cursorB.MoveNext())
                {
                    using var featureB = (Feature)cursorB.Current;
                    if (featureB.GetShape() is not Polygon geomB || geomB.IsEmpty) continue;
                    // Same layer on both sides: skip self-pairs and report each pair only once.
                    if (sameLayer && featureA.GetObjectID() >= featureB.GetObjectID()) continue;

                    if (GeometryEngine.Instance.Intersection(geomA, geomB) is not Polygon intersection || intersection.IsEmpty)
                        continue;

                    double area = intersection.Area;
                    if (area <= 0) continue;
                    double thinness = GeometryMath.ThinnessRatio(area, intersection.Length);
                    scan.Note(thinness, area);

                    if (thinness < thinnessThreshold && (maxArea == null || area <= maxArea))
                    {
                        scan.Results.Add(new SliverResult
                        {
                            Geometry = intersection,
                            OidA = featureA.GetObjectID(),
                            OidB = featureB.GetObjectID(),
                            Area = area,
                            ThinnessRatio = thinness
                        });
                    }
                }
            }
            return scan;
        }
    }
}

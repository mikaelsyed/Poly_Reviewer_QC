using ArcGIS.Core.Data;
using ArcGIS.Core.Geometry;
using MikaelsReviewerQcToolbar.Common;
using System;
using System.Collections.Generic;

namespace MikaelsReviewerQcToolbar.FindPolygonsWithHoles
{
    /// <summary>
    /// Re-implementation of Data Reviewer's "Find Polygons with Holes" check. A hole is any
    /// polygon ring that is not an exterior ring (Polygon.IsExteriorRing is false), so islands
    /// inside holes are handled correctly. Besides the OIDs of polygons that have holes, each
    /// hole is returned as its own polygon with its area and its share of the parent polygon's
    /// footprint, so small (likely error) and large (likely intentional) holes can be told
    /// apart. Must be called from within QueuedTask.Run.
    /// </summary>
    internal static class PolygonHoleFinder
    {
        public static readonly (string Name, string GpType)[] OutputFields =
            { ("SOURCE_OID", "LONG"), ("HOLE_AREA", "DOUBLE"), ("PARENT_AREA", "DOUBLE"), ("PCT_OF_PARENT", "DOUBLE") };

        public static (List<long> Oids, List<DerivedFeature> Holes) Find(Func<QueryFilter, RowCursor> search, QcProgress progress = null)
        {
            var oids = new List<long>();
            var holes = new List<DerivedFeature>();

            progress?.BeginRecords("Looking for holes");
            using var cursor = search(null);
            while (cursor.MoveNext())
            {
                using var feature = (Feature)cursor.Current;
                progress?.Tick(feature.GetObjectID());
                if (feature.GetShape() is not Polygon polygon || polygon.IsEmpty) continue;

                var featureHoles = new List<(Polygon Ring, double Area)>();
                var exteriorRings = new List<Geometry>();
                for (int i = 0; i < polygon.PartCount; i++)
                {
                    var builder = new PolygonBuilderEx(polygon.SpatialReference);
                    builder.AddPart(polygon.Parts[i]);
                    if (GeometryEngine.Instance.SimplifyAsFeature(builder.ToGeometry(), true) is not Polygon ring || ring.IsEmpty)
                        continue;

                    if (polygon.IsExteriorRing(i)) exteriorRings.Add(ring);
                    else featureHoles.Add((ring, ring.Area));
                }

                if (featureHoles.Count == 0) continue;

                long oid = feature.GetObjectID();
                oids.Add(oid);

                // The polygon's footprint with its holes filled in. Union (rather than summing)
                // so an island sitting inside a hole isn't counted twice.
                double parentArea = exteriorRings.Count == 1
                    ? ((Polygon)exteriorRings[0]).Area
                    : ((Polygon)GeometryEngine.Instance.Union(exteriorRings)).Area;

                foreach (var (ring, area) in featureHoles)
                {
                    var hole = new DerivedFeature { Geometry = ring };
                    hole.Attributes["SOURCE_OID"] = oid;
                    hole.Attributes["HOLE_AREA"] = area;
                    hole.Attributes["PARENT_AREA"] = parentArea;
                    hole.Attributes["PCT_OF_PARENT"] = parentArea > 0 ? area / parentArea * 100 : 0;
                    holes.Add(hole);
                }
            }
            return (oids, holes);
        }
    }
}

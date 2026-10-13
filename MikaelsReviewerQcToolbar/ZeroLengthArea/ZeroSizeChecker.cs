using ArcGIS.Core.Data;
using ArcGIS.Core.Geometry;
using MikaelsReviewerQcToolbar.Common;
using System;
using System.Collections.Generic;
using System.Linq;

namespace MikaelsReviewerQcToolbar.ZeroLengthArea
{
    internal class ZeroSizeScan
    {
        public List<long> Oids { get; } = new List<long>();
        public int NullOrEmpty { get; set; }
        public int ZeroLengthLines { get; set; }
        public int ZeroAreaPolygons { get; set; }
        /// <summary>Features whose total is fine but that contain at least one collapsed part (e.g. a zero-area ring).</summary>
        public int CollapsedPart { get; set; }
    }

    /// <summary>
    /// Flags degenerate geometry: null or empty shapes, polylines whose length is at or below a
    /// threshold, and polygons whose area is at or below a threshold. Each part is checked as well
    /// as the whole feature, so a multipart feature with one collapsed part (a zero-length path or a
    /// zero-area ring, hole rings included) is caught even though its total is fine. With the
    /// default threshold of 0 this finds true zero; raise it to catch near-zero features.
    /// Must be called from within QueuedTask.Run.
    /// </summary>
    internal static class ZeroSizeChecker
    {
        private const double Epsilon = 1e-12;

        public static ZeroSizeScan Find(
            Func<QueryFilter, RowCursor> search, double lengthThreshold, double areaThreshold, QcProgress progress = null)
        {
            var scan = new ZeroSizeScan();
            double lengthLimit = Math.Max(lengthThreshold, Epsilon);
            double areaLimit = Math.Max(areaThreshold, Epsilon);

            progress?.BeginRecords("Measuring lengths and areas");
            using var cursor = search(null);
            while (cursor.MoveNext())
            {
                using var feature = (Feature)cursor.Current;
                long oid = feature.GetObjectID();
                progress?.Tick(oid);

                var shape = feature.GetShape();
                if (shape == null || shape.IsEmpty)
                {
                    scan.Oids.Add(oid);
                    scan.NullOrEmpty++;
                    continue;
                }

                if (shape is Polyline polyline)
                {
                    var partLengths = polyline.Parts.Select(PartLength).ToList();
                    if (partLengths.Sum() <= lengthLimit)
                    {
                        scan.Oids.Add(oid);
                        scan.ZeroLengthLines++;
                    }
                    else if (partLengths.Any(l => l <= lengthLimit))
                    {
                        scan.Oids.Add(oid);
                        scan.CollapsedPart++;
                    }
                }
                else if (shape is Polygon polygon)
                {
                    var ringAreas = Enumerable.Range(0, polygon.PartCount).Select(i => RingArea(polygon, i)).ToList();
                    double signedTotal = 0;
                    for (int i = 0; i < ringAreas.Count; i++)
                        signedTotal += polygon.IsExteriorRing(i) ? ringAreas[i] : -ringAreas[i];

                    if (Math.Abs(signedTotal) <= areaLimit)
                    {
                        scan.Oids.Add(oid);
                        scan.ZeroAreaPolygons++;
                    }
                    else if (ringAreas.Any(a => a <= areaLimit))
                    {
                        scan.Oids.Add(oid);
                        scan.CollapsedPart++;
                    }
                }
            }
            return scan;
        }

        private static double PartLength(ReadOnlySegmentCollection part)
        {
            double total = 0;
            foreach (var segment in part) total += segment.Length;
            return total;
        }

        // Absolute area enclosed by one ring. Straight-segment rings use the shoelace formula;
        // rings with curved segments are measured by the geometry engine instead, because a ring
        // made of a single full-circle arc starts and ends at the same point (shoelace gives 0).
        private static double RingArea(Polygon polygon, int partIndex)
        {
            var part = polygon.Parts[partIndex];
            bool allStraight = true;
            double twiceArea = 0;
            // Measured relative to the first vertex: with map coordinates in the millions, the raw
            // products are huge and their rounding error would swamp a genuinely tiny ring.
            double x0 = part[0].StartPoint.X, y0 = part[0].StartPoint.Y;
            foreach (var segment in part)
            {
                if (segment.SegmentType != SegmentType.Line) allStraight = false;
                var a = segment.StartPoint;
                var b = segment.EndPoint;
                twiceArea += (a.X - x0) * (b.Y - y0) - (b.X - x0) * (a.Y - y0);
            }
            if (allStraight) return Math.Abs(twiceArea) / 2;

            var builder = new PolygonBuilderEx(polygon.SpatialReference);
            builder.AddPart(part);
            return Math.Abs(builder.ToGeometry().Area);
        }
    }
}

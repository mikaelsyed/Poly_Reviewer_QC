using ArcGIS.Core.Data;
using ArcGIS.Core.Geometry;
using MikaelsReviewerQcToolbar.Common;
using System;
using System.Collections.Generic;
using System.Linq;

namespace MikaelsReviewerQcToolbar.DuplicateVertex
{
    internal enum VertexStatus { Below, AtTolerance }

    internal class VertexHit
    {
        public long Oid { get; set; }
        public int Part { get; set; }
        public int Vertex { get; set; }
        public MapPoint Point { get; set; }
        /// <summary>Distance to the closest other vertex that put this one within reach of the tolerance.</summary>
        public double Distance { get; set; }
        public VertexStatus Status { get; set; }
    }

    internal class VertexToleranceScan
    {
        public List<VertexHit> Hits { get; } = new List<VertexHit>();
        public HashSet<long> Oids { get; } = new HashSet<long>();
        /// <summary>Closest vertex pair seen within 10x the tolerance, so a 0 result can say how close it got.</summary>
        public double ClosestDistance { get; set; } = double.MaxValue;
        public long ClosestOid { get; set; } = -1;
    }

    /// <summary>
    /// Vertex Tolerance Check: finds vertices (within the same part of a polyline/polygon
    /// feature) that sit closer to another vertex than the tolerance. A pair counts as
    /// BELOW when its distance is under (tolerance - margin) and as AT_TOLERANCE when it is
    /// within +/- margin of the tolerance, i.e. cutting it close. Closing vertices of rings
    /// are not treated as duplicates of the first vertex. Candidate pairs are found by sorting
    /// vertices by X and only comparing neighbours within reach, so long lines stay fast.
    /// Must be called from within QueuedTask.Run.
    /// </summary>
    internal static class DuplicateVertexChecker
    {
        public static readonly (string Name, string GpType)[] OutputFields =
            { ("SOURCE_OID", "LONG"), ("PART_NO", "LONG"), ("VERTEX_NO", "LONG"), ("DISTANCE", "DOUBLE"), ("STATUS", "TEXT") };

        public static List<DerivedFeature> ToFeatures(IEnumerable<VertexHit> hits)
        {
            return hits.Select(h =>
            {
                var p = new DerivedFeature { Geometry = h.Point };
                p.Attributes["SOURCE_OID"] = h.Oid;
                p.Attributes["PART_NO"] = h.Part;
                p.Attributes["VERTEX_NO"] = h.Vertex;
                p.Attributes["DISTANCE"] = h.Distance;
                p.Attributes["STATUS"] = h.Status == VertexStatus.Below ? "BELOW" : "AT_TOLERANCE";
                return p;
            }).ToList();
        }

        public static VertexToleranceScan Find(
            Func<QueryFilter, RowCursor> search, double tolerance, double margin, bool reportBelow, bool reportAt, QcProgress progress = null)
        {
            var scan = new VertexToleranceScan();
            double belowLimit = tolerance - margin;
            double reach = tolerance + margin;

            progress?.BeginRecords("Checking vertex spacing");
            using var cursor = search(null);
            while (cursor.MoveNext())
            {
                using var feature = (Feature)cursor.Current;
                long oid = feature.GetObjectID();
                progress?.Tick(oid);
                if (feature.GetShape() is not Multipart multipart) continue;

                int partIndex = -1;
                foreach (var part in multipart.Parts)
                {
                    partIndex++;
                    var points = new List<MapPoint>();
                    foreach (var segment in part) points.Add(segment.StartPoint);
                    if (part.Count > 0) points.Add(part[part.Count - 1].EndPoint);

                    // A closed ring/loop repeats its first vertex as its last; that's the closure,
                    // not a second vertex.
                    if (points.Count > 1 && Distance(points[0], points[points.Count - 1]) <= 1e-9)
                        points.RemoveAt(points.Count - 1);

                    ScanPart(points, oid, partIndex, belowLimit, reach, reportBelow, reportAt, scan);
                }
            }
            return scan;
        }

        private static void ScanPart(
            List<MapPoint> points, long oid, int partIndex, double belowLimit, double reach,
            bool reportBelow, bool reportAt, VertexToleranceScan scan)
        {
            int n = points.Count;
            if (n < 2) return;

            var order = Enumerable.Range(0, n).ToArray();
            var sortedX = points.Select(p => p.X).ToArray();
            Array.Sort(sortedX, order);

            var closestBelow = new Dictionary<int, double>();
            var closestAt = new Dictionary<int, double>();
            double diagnosticReach = reach * 10;

            for (int a = 0; a < n; a++)
            {
                for (int b = a + 1; b < n && sortedX[b] - sortedX[a] <= diagnosticReach; b++)
                {
                    int i = order[a], j = order[b];
                    double d = Distance(points[i], points[j]);

                    if (d <= diagnosticReach && d < scan.ClosestDistance)
                    {
                        scan.ClosestDistance = d;
                        scan.ClosestOid = oid;
                    }
                    if (d > reach) continue;

                    var target = d < belowLimit ? closestBelow : closestAt;
                    Record(target, i, d);
                    Record(target, j, d);
                }
            }

            // A vertex with both a BELOW and an AT_TOLERANCE neighbour shows up as BELOW when
            // both categories are reported, and in whichever category is enabled otherwise.
            foreach (int vertex in closestBelow.Keys.Union(closestAt.Keys).OrderBy(v => v))
            {
                VertexStatus status;
                double distance;
                if (reportBelow && closestBelow.TryGetValue(vertex, out distance)) status = VertexStatus.Below;
                else if (reportAt && closestAt.TryGetValue(vertex, out distance)) status = VertexStatus.AtTolerance;
                else continue;

                scan.Oids.Add(oid);
                scan.Hits.Add(new VertexHit
                {
                    Oid = oid,
                    Part = partIndex,
                    Vertex = vertex,
                    Point = points[vertex],
                    Distance = distance,
                    Status = status
                });
            }
        }

        private static void Record(Dictionary<int, double> closest, int vertex, double d)
        {
            if (!closest.TryGetValue(vertex, out double current) || d < current)
                closest[vertex] = d;
        }

        private static double Distance(MapPoint a, MapPoint b)
        {
            double dx = a.X - b.X;
            double dy = a.Y - b.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }
    }
}

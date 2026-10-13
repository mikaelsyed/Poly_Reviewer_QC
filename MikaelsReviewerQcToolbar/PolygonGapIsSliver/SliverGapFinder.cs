using ArcGIS.Core.Data;
using ArcGIS.Core.Geometry;
using MikaelsReviewerQcToolbar.Common;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace MikaelsReviewerQcToolbar.PolygonGapIsSliver
{
    /// <summary>
    /// Finds the gaps in a polygon coverage (Data Reviewer's "Polygon Gap is Sliver" is
    /// approximated - Esri's exact algorithm isn't published). Works on the union of every
    /// polygon from both sources rather than pair by pair, so a gap bounded by three or more
    /// polygons comes out as one complete shape:
    ///   1. Enclosed gaps - voids completely surrounded by polygons - are found exactly (the
    ///      union with its holes filled, minus the union). They need no width setting and are
    ///      never missed, however large.
    ///   2. Open channels - narrow strips between polygons that reach the edge of the coverage -
    ///      are found by morphological closing (buffer out by half the Maximum Gap Width, then
    ///      back in): anything narrower than the Maximum Gap Width is bridged.
    /// Two guards keep the closing step honest (they apply to open strips only - an enclosed gap
    /// is genuine even when the polygons around it overlap each other): a strip must touch two
    /// polygons that are actually separated from each other, which rejects the filleted
    /// corners closing creates where polygons meet, and every gap must be wider than the
    /// geodatabase's XY tolerance, which rejects numerical hairlines along straight edges.
    /// Every gap found is reported with its area and thinness ratio and classified as a sliver
    /// or not, instead of being thrown away by the settings.
    /// Speed: the Maximum Gap Width is what costs time - closing a large width produces many more
    /// candidate shapes that each have to be validated. Neighbours are looked up in an in-memory
    /// grid built from the polygons already read, not by re-querying the data for every shape.
    /// Must be called from within QueuedTask.Run.
    /// </summary>
    internal static class SliverGapFinder
    {
        public static readonly (string Name, string GpType)[] OutputFields =
            { ("OID_A", "LONG"), ("OID_B", "LONG"), ("NEIGHBORS", "LONG"), ("AREA", "DOUBLE"), ("THINNESS_RATIO", "DOUBLE"), ("IS_SLIVER", "SHORT") };

        public static List<DerivedFeature> ToFeatures(IEnumerable<SliverResult> results)
        {
            return results.Select(r =>
            {
                var f = new DerivedFeature { Geometry = r.Geometry };
                f.Attributes["OID_A"] = r.OidA;
                f.Attributes["OID_B"] = r.OidB;
                f.Attributes["NEIGHBORS"] = r.Neighbors;
                f.Attributes["AREA"] = r.Area;
                f.Attributes["THINNESS_RATIO"] = r.ThinnessRatio;
                f.Attributes["IS_SLIVER"] = r.IsSliver ? 1 : 0;
                return f;
            }).ToList();
        }

        public static SliverScan Find(
            Func<QueryFilter, RowCursor> searchA, Func<QueryFilter, RowCursor> searchB, bool sameLayer,
            double maxGapWidth, double thinnessThreshold, double? maxArea, bool onlySlivers, QcProgress progress = null)
        {
            var scan = new SliverScan();
            var clock = Stopwatch.StartNew();
            void Lap(string what) { scan.Notes.Add($"{what}: {clock.Elapsed.TotalSeconds:0.0} s"); clock.Restart(); }

            // Share of the unit's progress bar given to each phase (read, merge, fill, enclosed, closing, open).
            var weights = maxGapWidth > 0 ? new[] { 3.0, 5, 12, 4, 40, 36 } : new[] { 10.0, 25, 50, 15, 0, 0 };
            double scale = 1 / weights.Sum();
            double cursor = 0;
            (double Start, double Span) Next(int phase)
            {
                double start = cursor;
                cursor += weights[phase] * scale;
                return (start, weights[phase] * scale);
            }

            var records = new List<PolygonRecord>();
            var (readStart, readSpan) = Next(0);
            progress?.BeginRecords("Reading polygons", readStart, readSpan);
            ReadPolygons(searchA, records, 0, progress);
            if (!sameLayer) ReadPolygons(searchB, records, 1, progress);
            Lap($"read {records.Count} polygons");
            if (records.Count < 2) return scan;

            var (mergeStart, mergeSpan) = Next(1);
            progress?.Working("Merging all polygons into one shape (one long operation, no record counter)", mergeStart, mergeSpan);
            if (GeometryEngine.Instance.Union(records.Select(r => (Geometry)r.Shape).ToList()) is not Polygon union || union.IsEmpty) return scan;
            Lap("merge into one shape");

            double xyTolerance = union.SpatialReference?.XYTolerance ?? 0;
            double minArea = Math.Max(xyTolerance * xyTolerance, 1e-12);
            double reach = Math.Max(xyTolerance * 2, 1e-9);
            var index = new PolygonIndex(records);

            // Handles each candidate shape. Enclosed gaps are genuine by definition (the polygons
            // fully surround them, even when those polygons overlap each other), so they only need
            // to touch two polygons. Open strips come from the closing step, which also produces
            // filleted corners and hairlines, so they must sit between two separated polygons.
            void Emit(Geometry gaps, bool enclosed, string phaseName, (double Start, double Span) range)
            {
                if (gaps == null || gaps.IsEmpty) return;
                var pieces = SplitPieces(gaps);
                progress?.Begin(phaseName, pieces.Count, range.Start, range.Span);

                foreach (var piece in pieces)
                {
                    progress?.Tick();

                    double area = piece.Area;
                    if (area < minArea) continue;

                    // Mean width of a strip is about 2*area/perimeter; below the XY tolerance the
                    // geodatabase can't tell it from coincident edges.
                    double length = Math.Max(piece.Length, 1e-12);
                    if (2 * area / length < xyTolerance * 2) continue;

                    double thinness = GeometryMath.ThinnessRatio(area, length);
                    bool isSliver = thinness < thinnessThreshold && (maxArea == null || area <= maxArea);

                    // With "only slivers" on, shapes that can't be slivers never need validating -
                    // the expensive part - because they would be discarded anyway.
                    if (onlySlivers && !isSliver)
                    {
                        scan.NoteUnvalidated(thinness, area);
                        continue;
                    }

                    var neighbors = index.FindNeighbors(piece, reach);
                    bool valid = enclosed ? neighbors.Count >= 2 : HasSeparatedPair(neighbors);
                    if (!valid)
                    {
                        scan.SkippedNotBetweenPolygons++;
                        continue;
                    }

                    scan.Note(thinness, area);
                    if (enclosed) scan.EnclosedFound++; else scan.OpenFound++;
                    if (isSliver) scan.SliverCount++;

                    scan.Results.Add(new SliverResult
                    {
                        Geometry = piece,
                        OidA = neighbors.Count > 0 ? neighbors[0].Oid : 0,
                        OidB = neighbors.Count > 1 ? neighbors[1].Oid : 0,
                        Neighbors = neighbors.Count,
                        Area = area,
                        ThinnessRatio = thinness,
                        IsSliver = isSliver
                    });
                }
            }

            var (fillStart, fillSpan) = Next(2);
            progress?.Working("Filling holes to find fully enclosed gaps (one long operation, no record counter)", fillStart, fillSpan);
            var filled = FillHoles(union);
            var voids = GeometryEngine.Instance.Difference(filled, union);
            Lap("fill holes + find enclosed voids");

            Emit(voids, enclosed: true, "Validating enclosed gaps", Next(3));
            Lap($"validate enclosed gaps ({scan.EnclosedFound} kept)");

            if (maxGapWidth > 0)
            {
                double r = maxGapWidth / 2;
                var (closeStart, closeSpan) = Next(4);
                progress?.Working("Closing narrow strips between polygons (one long operation, no record counter)", closeStart, closeSpan);
                var grown = GeometryEngine.Instance.Buffer(union, r);
                progress?.Check();
                var closing = GeometryEngine.Instance.Buffer(grown, -r);
                progress?.Check();
                Lap("closing: grow + shrink");
                if (closing != null && !closing.IsEmpty)
                {
                    var channels = GeometryEngine.Instance.Difference(closing, filled);
                    Lap("closing: subtract");
                    Emit(channels, enclosed: false, "Validating open strips", Next(5));
                    Lap($"validate open strips ({scan.OpenFound} kept, {scan.SkippedNotBetweenPolygons} rejected)");
                }
            }
            return scan;
        }

        // ------------------------------------------------------------------
        // Input
        // ------------------------------------------------------------------

        private class PolygonRecord
        {
            public long Oid { get; set; }
            public int Layer { get; set; }
            public Polygon Shape { get; set; }
            public double XMin, YMin, XMax, YMax;
        }

        private static void ReadPolygons(Func<QueryFilter, RowCursor> search, List<PolygonRecord> into, int layer, QcProgress progress)
        {
            using var cursor = search(null);
            while (cursor.MoveNext())
            {
                using var feature = (Feature)cursor.Current;
                long oid = feature.GetObjectID();
                progress?.Tick(oid);
                if (feature.GetShape() is Polygon polygon && !polygon.IsEmpty)
                {
                    var e = polygon.Extent;
                    into.Add(new PolygonRecord { Oid = oid, Layer = layer, Shape = polygon, XMin = e.XMin, YMin = e.YMin, XMax = e.XMax, YMax = e.YMax });
                }
            }
        }

        // ------------------------------------------------------------------
        // Splitting and filling
        // ------------------------------------------------------------------

        // Splits a multipart polygon into one polygon per exterior ring (with the holes that follow
        // it). Much faster than GeometryEngine.MultipartToSinglePart on shapes with hundreds of
        // parts, which has to work out ring containment. Simple polygons store each exterior ring
        // followed by its own interior rings; anything else falls back to the engine's version.
        private static List<Polygon> SplitPieces(Geometry gaps)
        {
            if (gaps is Polygon polygon && polygon.PartCount > 1)
            {
                var pieces = new List<Polygon>();
                PolygonBuilderEx current = null;
                bool orderedOk = true;
                for (int i = 0; i < polygon.PartCount; i++)
                {
                    if (polygon.IsExteriorRing(i))
                    {
                        if (current != null) pieces.Add(current.ToGeometry());
                        current = new PolygonBuilderEx(polygon.SpatialReference);
                    }
                    else if (current == null)
                    {
                        orderedOk = false;
                        break;
                    }
                    current.AddPart(polygon.Parts[i]);
                }
                if (orderedOk)
                {
                    if (current != null) pieces.Add(current.ToGeometry());
                    return pieces;
                }
            }
            return GeometryEngine.Instance.MultipartToSinglePart(gaps).OfType<Polygon>().Where(p => !p.IsEmpty).ToList();
        }

        // The polygon with every hole filled in. Built by unioning the exterior rings one by
        // one, so an island sitting inside a hole simply merges into the surrounding ring.
        private static Geometry FillHoles(Polygon polygon)
        {
            var exteriors = new List<Geometry>();
            for (int i = 0; i < polygon.PartCount; i++)
            {
                if (!polygon.IsExteriorRing(i)) continue;
                var builder = new PolygonBuilderEx(polygon.SpatialReference);
                builder.AddPart(polygon.Parts[i]);
                if (GeometryEngine.Instance.SimplifyAsFeature(builder.ToGeometry(), true) is Polygon ring && !ring.IsEmpty)
                    exteriors.Add(ring);
            }
            return exteriors.Count == 1 ? exteriors[0] : GeometryEngine.Instance.Union(exteriors);
        }

        // ------------------------------------------------------------------
        // Neighbours
        // ------------------------------------------------------------------

        // True when at least two of the polygons touching the gap are separated from each other:
        // no overlapping interiors and no shared edge (touching only at a corner point still counts
        // as separated, e.g. three polygons meeting corner to corner around a void).
        private static bool HasSeparatedPair(List<PolygonRecord> neighbors)
        {
            // Stop at the first separated pair; cap the work on shapes that touch very many polygons.
            int n = Math.Min(neighbors.Count, 12);
            for (int i = 0; i < n; i++)
            {
                for (int j = i + 1; j < n; j++)
                {
                    var a = neighbors[i].Shape;
                    var b = neighbors[j].Shape;
                    if (!GeometryEngine.Instance.Relate(a, b, "T********") && !GeometryEngine.Instance.Relate(a, b, "****1****"))
                        return true;
                }
            }
            return false;
        }

        /// <summary>A uniform grid over the input polygons' bounding boxes, for finding the polygons near a shape without re-querying the data.</summary>
        private class PolygonIndex
        {
            private readonly List<PolygonRecord> _records;
            private readonly Dictionary<long, List<int>> _cells = new Dictionary<long, List<int>>();
            private readonly List<int> _oversized = new List<int>();
            private readonly double _cellSize;
            private readonly double _originX;
            private readonly double _originY;
            private const int MaxCellsPerPolygon = 400;

            public PolygonIndex(List<PolygonRecord> records)
            {
                _records = records;
                _originX = records.Min(r => r.XMin);
                _originY = records.Min(r => r.YMin);

                var sizes = records.Select(r => Math.Max(r.XMax - r.XMin, r.YMax - r.YMin)).OrderBy(x => x).ToList();
                _cellSize = Math.Max(sizes[sizes.Count / 2] * 2, 1e-6);

                for (int i = 0; i < records.Count; i++)
                {
                    var r = records[i];
                    int x0 = Cell(r.XMin, _originX), x1 = Cell(r.XMax, _originX);
                    int y0 = Cell(r.YMin, _originY), y1 = Cell(r.YMax, _originY);
                    if ((long)(x1 - x0 + 1) * (y1 - y0 + 1) > MaxCellsPerPolygon)
                    {
                        _oversized.Add(i);
                        continue;
                    }
                    for (int x = x0; x <= x1; x++)
                        for (int y = y0; y <= y1; y++)
                            Add(x, y, i);
                }
            }

            private int Cell(double value, double origin) => (int)Math.Floor((value - origin) / _cellSize);
            private static long Key(int x, int y) => ((long)x << 32) ^ (uint)y;

            private void Add(int x, int y, int i)
            {
                long key = Key(x, y);
                if (!_cells.TryGetValue(key, out var list)) _cells[key] = list = new List<int>();
                list.Add(i);
            }

            /// <summary>The distinct input polygons within 'reach' of the gap (touching it counts).</summary>
            public List<PolygonRecord> FindNeighbors(Polygon gap, double reach)
            {
                var e = gap.Extent;
                double xMin = e.XMin - reach, yMin = e.YMin - reach, xMax = e.XMax + reach, yMax = e.YMax + reach;

                var candidates = new HashSet<int>(_oversized);
                int x0 = Cell(xMin, _originX), x1 = Cell(xMax, _originX), y0 = Cell(yMin, _originY), y1 = Cell(yMax, _originY);
                for (int x = x0; x <= x1; x++)
                    for (int y = y0; y <= y1; y++)
                        if (_cells.TryGetValue(Key(x, y), out var list))
                            foreach (int i in list) candidates.Add(i);

                var result = new List<PolygonRecord>();
                foreach (int i in candidates.OrderBy(c => c))
                {
                    var r = _records[i];
                    if (r.XMax < xMin || r.XMin > xMax || r.YMax < yMin || r.YMin > yMax) continue;
                    if (GeometryEngine.Instance.Distance(gap, r.Shape) <= reach) result.Add(r);
                }
                return result;
            }
        }
    }
}

using ArcGIS.Core.Data;
using ArcGIS.Core.Geometry;
using System;
using System.Collections.Generic;

namespace MikaelsReviewerQcToolbar.Common
{
    /// <summary>A brand-new feature (hole, sliver, gap, vertex point...) plus the attribute values to store with it.</summary>
    internal class DerivedFeature
    {
        public Geometry Geometry { get; set; }
        public Dictionary<string, object> Attributes { get; } = new Dictionary<string, object>();
    }

    internal class SliverResult
    {
        public Polygon Geometry { get; set; }
        public long OidA { get; set; }
        public long OidB { get; set; }
        public double Area { get; set; }
        public double ThinnessRatio { get; set; }
        public int Neighbors { get; set; }
        public bool IsSliver { get; set; }
    }

    /// <summary>
    /// What a sliver scan saw, not just what passed the filters, so the log can explain an
    /// empty result ("found 1 overlap but its thinness ratio was 0.62, above your threshold").
    /// </summary>
    internal class SliverScan
    {
        public List<SliverResult> Results { get; } = new List<SliverResult>();
        public int Candidates { get; set; }
        /// <summary>Human-readable timing/diagnostic lines (where the time went) for the log.</summary>
        public List<string> Notes { get; } = new List<string>();
        public int EnclosedFound { get; set; }
        public int OpenFound { get; set; }
        public int SliverCount { get; set; }
        /// <summary>Gap-shaped areas skipped without full validation because "only slivers" was on and they aren't sliver-shaped.</summary>
        public int SkippedNonSliver { get; set; }
        /// <summary>Gap-like shapes dropped because they aren't between two separated polygons (a notch in one polygon, or a corner where polygons overlap/meet).</summary>
        public int SkippedNotBetweenPolygons { get; set; }
        public double ThinnestRatio { get; set; } = double.MaxValue;
        public double ThinnestArea { get; set; }

        public void NoteUnvalidated(double thinness, double area)
        {
            SkippedNonSliver++;
            if (thinness < ThinnestRatio)
            {
                ThinnestRatio = thinness;
                ThinnestArea = area;
            }
        }

        public void Note(double thinness, double area)
        {
            Candidates++;
            if (thinness < ThinnestRatio)
            {
                ThinnestRatio = thinness;
                ThinnestArea = area;
            }
        }
    }

    /// <summary>
    /// Inserts derived features into an existing file-geodatabase feature class. Z/M values are
    /// stripped first: the output feature classes are created 2D, and inserting a geometry that
    /// has Z (common when the input layer is 3D) fails with "geometry cannot have Z values".
    /// Must be called from within QueuedTask.Run (or a CoreHost thread).
    /// </summary>
    internal static class FeatureInserter
    {
        public static void Insert(string gdbPath, string fcName, List<DerivedFeature> features)
        {
            using var geodatabase = new Geodatabase(new FileGeodatabaseConnectionPath(new Uri(gdbPath)));
            using var featureClass = geodatabase.OpenDataset<FeatureClass>(fcName);
            using var insertCursor = featureClass.CreateInsertCursor();
            string shapeField = featureClass.GetDefinition().GetShapeField();
            foreach (var feature in features)
            {
                using var rowBuffer = featureClass.CreateRowBuffer();
                rowBuffer[shapeField] = StripZM(feature.Geometry);
                foreach (var kvp in feature.Attributes)
                    rowBuffer[kvp.Key] = kvp.Value;
                insertCursor.Insert(rowBuffer);
            }
            insertCursor.Flush();
        }

        public static Geometry StripZM(Geometry geometry)
        {
            if (!geometry.HasZ && !geometry.HasM) return geometry;
            switch (geometry)
            {
                case Polygon polygon:
                    return new PolygonBuilderEx(polygon) { HasZ = false, HasM = false }.ToGeometry();
                case MapPoint point:
                    return new MapPointBuilderEx(point) { HasZ = false, HasM = false }.ToGeometry();
                default:
                    return geometry;
            }
        }
    }
}

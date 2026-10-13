using System;

namespace MikaelsReviewerQcToolbar.Common
{
    internal static class GeometryMath
    {
        /// <summary>
        /// Standard polygon compactness (Polsby-Popper) thinness ratio: 4*pi*area / perimeter^2.
        /// 1.0 for a circle, approaching 0 for a long thin sliver. Used by both sliver checks
        /// to decide whether a derived overlap/gap polygon is "sliver-shaped".
        /// </summary>
        public static double ThinnessRatio(double area, double perimeter)
        {
            if (perimeter <= 0) return 0;
            return (4 * Math.PI * Math.Abs(area)) / (perimeter * perimeter);
        }
    }
}

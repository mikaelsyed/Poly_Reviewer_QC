using ArcGIS.Desktop.Core.Geoprocessing;
using ArcGIS.Desktop.Framework.Contracts;

namespace MikaelsReviewerQcToolbar.CheckGeometry
{
    /// <summary>
    /// Check Geometry is not a Data Reviewer check - it's the standard, always-available
    /// Data Management tool that already validates geometry (null/empty shapes, self
    /// intersections, etc.), so there's nothing to reimplement here. Opens Esri's own tool
    /// dialog directly.
    /// </summary>
    internal class CheckGeometryButton : Button
    {
        protected override void OnClick()
        {
            Geoprocessing.OpenToolDialog("management.CheckGeometry", null);
        }
    }
}

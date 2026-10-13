using ArcGIS.Desktop.Framework.Contracts;

namespace MikaelsReviewerQcToolbar.FindPolygonsWithHoles
{
    internal class FindPolygonsWithHolesButton : Button
    {
        protected override void OnClick()
        {
            var window = new FindPolygonsWithHolesWindow { Owner = System.Windows.Application.Current.MainWindow };
            window.ShowDialog();
        }
    }
}

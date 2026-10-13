using ArcGIS.Desktop.Framework.Contracts;

namespace MikaelsReviewerQcToolbar.PolygonGapIsSliver
{
    internal class PolygonGapIsSliverButton : Button
    {
        protected override void OnClick()
        {
            var window = new PolygonGapIsSliverWindow { Owner = System.Windows.Application.Current.MainWindow };
            window.ShowDialog();
        }
    }
}

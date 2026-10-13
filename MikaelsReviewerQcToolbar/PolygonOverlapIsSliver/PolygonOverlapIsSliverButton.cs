using ArcGIS.Desktop.Framework.Contracts;

namespace MikaelsReviewerQcToolbar.PolygonOverlapIsSliver
{
    internal class PolygonOverlapIsSliverButton : Button
    {
        protected override void OnClick()
        {
            var window = new PolygonOverlapIsSliverWindow { Owner = System.Windows.Application.Current.MainWindow };
            window.ShowDialog();
        }
    }
}

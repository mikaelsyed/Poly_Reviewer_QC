using ArcGIS.Desktop.Framework.Contracts;

namespace MikaelsReviewerQcToolbar.DuplicateFeature
{
    internal class DuplicateFeatureButton : Button
    {
        protected override void OnClick()
        {
            var window = new DuplicateFeatureWindow { Owner = System.Windows.Application.Current.MainWindow };
            window.ShowDialog();
        }
    }
}

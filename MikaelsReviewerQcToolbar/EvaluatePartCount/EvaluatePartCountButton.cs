using ArcGIS.Desktop.Framework.Contracts;

namespace MikaelsReviewerQcToolbar.EvaluatePartCount
{
    internal class EvaluatePartCountButton : Button
    {
        protected override void OnClick()
        {
            var window = new EvaluatePartCountWindow { Owner = System.Windows.Application.Current.MainWindow };
            window.ShowDialog();
        }
    }
}

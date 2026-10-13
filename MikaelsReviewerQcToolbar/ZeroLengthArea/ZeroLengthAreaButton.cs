using ArcGIS.Desktop.Framework.Contracts;

namespace MikaelsReviewerQcToolbar.ZeroLengthArea
{
    internal class ZeroLengthAreaButton : Button
    {
        protected override void OnClick()
        {
            var window = new ZeroLengthAreaWindow { Owner = System.Windows.Application.Current.MainWindow };
            window.ShowDialog();
        }
    }
}

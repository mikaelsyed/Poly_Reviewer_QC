using ArcGIS.Desktop.Framework.Contracts;

namespace MikaelsReviewerQcToolbar.RunAllChecks
{
    internal class RunAllChecksButton : Button
    {
        protected override void OnClick()
        {
            var window = new RunAllChecksWindow { Owner = System.Windows.Application.Current.MainWindow };
            window.ShowDialog();
        }
    }
}

using ArcGIS.Desktop.Framework.Contracts;

namespace MikaelsReviewerQcToolbar.UniqueFieldValue
{
    internal class UniqueFieldValueButton : Button
    {
        protected override void OnClick()
        {
            var window = new UniqueFieldValueWindow { Owner = System.Windows.Application.Current.MainWindow };
            window.ShowDialog();
        }
    }
}

using ArcGIS.Desktop.Framework.Contracts;

namespace MikaelsReviewerQcToolbar.DuplicateVertex
{
    internal class DuplicateVertexButton : Button
    {
        protected override void OnClick()
        {
            var window = new DuplicateVertexWindow { Owner = System.Windows.Application.Current.MainWindow };
            window.ShowDialog();
        }
    }
}

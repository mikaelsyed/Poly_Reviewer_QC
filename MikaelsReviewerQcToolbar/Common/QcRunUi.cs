using ArcGIS.Core.Data;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Mapping;
using System.Linq;
using System.Threading.Tasks;

namespace MikaelsReviewerQcToolbar.Common
{
    internal static class QcRunUi
    {
        /// <summary>Total feature count of the given layers, used for the "record n of N" display and to weight the time estimate.</summary>
        public static Task<long> CountAsync(params FeatureLayer[] layers)
        {
            return QueuedTask.Run(() =>
            {
                long total = 0;
                foreach (var layer in layers.Where(l => l != null).Distinct())
                {
                    using var fc = layer.GetFeatureClass();
                    total += fc.GetCount();
                }
                return total;
            });
        }

        /// <summary>
        /// The layer's dataset (feature class / shapefile) name and where it lives on disk, for the QC output
        /// log. The dataset name is what lets the Metadata Editor match a log to the feature class it is editing.
        /// </summary>
        public static Task<(string Dataset, string Source)> DescribeAsync(FeatureLayer layer)
        {
            return QueuedTask.Run(() =>
            {
                try
                {
                    using var fc = layer.GetFeatureClass();
                    string name = fc.GetDefinition().GetName();
                    using var datastore = fc.GetDatastore();
                    string folder = datastore is Geodatabase gdb ? gdb.GetPath()?.LocalPath
                        : datastore is FileSystemDatastore fs ? fs.GetPath()?.LocalPath : null;
                    return (name, string.IsNullOrEmpty(folder) ? name : System.IO.Path.Combine(folder, name));
                }
                catch
                {
                    return (layer.Name, "");
                }
            });
        }

        /// <summary>Feature count of one layer; call from inside QueuedTask.Run.</summary>
        public static long CountNow(FeatureLayer layer)
        {
            if (layer == null) return 0;
            using var fc = layer.GetFeatureClass();
            return fc.GetCount();
        }
    }
}

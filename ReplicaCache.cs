using System;
using System.Threading.Tasks;
using Esri.ArcGISRuntime.UI.Controls;
using POSM_MR3_2;

namespace WpfMapApp1
{
    // Lightweight wrapper around InMemorySearchIndex acting as an attribute replica cache
    public class ReplicaCache
    {
        private readonly MapView _mapView;
        private readonly InMemorySearchIndex _index = new InMemorySearchIndex();

        public ReplicaCache(MapView mapView)
        {
            _mapView = mapView;
        }

        public InMemorySearchIndex Index => _index;

        public Task WarmAsync(IProgress<string>? progress = null)
        {
            return _index.BuildIndexAsync(_mapView, progress);
        }

        public void Upsert(string layerName, string fieldName, string value, string displayValue,
            Esri.ArcGISRuntime.Data.Feature? feature, object? featureId)
        {
            _index.Upsert(layerName, fieldName, value, displayValue, feature, featureId);
        }
    }
}


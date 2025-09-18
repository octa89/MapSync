using Esri.ArcGISRuntime.Geometry;
using Esri.ArcGISRuntime.Mapping;
using System.Collections.Generic;
using System.Linq;

namespace POSM_MR3_2
{
    /// <summary>
    /// Helper class to combine the full extents of multiple layers.
    /// </summary>
    public static class MapExtentHelper
    {
        /// <summary>
        /// Combines the full extents of the given layers into a single Envelope
        /// (the bounding box that contains all layer extents).
        /// Returns null if no layer has a valid FullExtent.
        /// </summary>
        /// <param name="layers">A collection of layers.</param>
        /// <returns>An Envelope (bounding box) for all layer extents, or null if none exist.</returns>
        public static Envelope? CombineLayerExtents(IEnumerable<Layer> layers)
        {
            Envelope? combinedExtent = null;

            foreach (var layer in layers)
            {
                Envelope? layerExtent = layer.FullExtent;
                if (layerExtent == null)
                    continue;  // Skip layers with no extent

                if (combinedExtent == null)
                {
                    // First valid extent becomes our starting envelope
                    combinedExtent = layerExtent;
                }
                else
                {
                    // Union the two geometries, then get the bounding envelope (Extent)
                    var unionGeometry = combinedExtent.Union(layerExtent);
                    combinedExtent = unionGeometry.Extent;
                }
            }

            return combinedExtent;
        }
    }
}

using System;

namespace POSM_MR3_2
{
    /// <summary>
    /// Represents inspection image information with distance and path data
    /// </summary>
    public class ImageInfo
    {
        public string FilePath { get; set; } = string.Empty;
        public string MediaFolder { get; set; } = string.Empty;
        public string PictureLocation { get; set; } = string.Empty;
        public double Distance { get; set; }
        public DateTime? InspectionDate { get; set; }

        public ImageInfo(string filePath, string mediaFolder = "", string pictureLocation = "", double distance = 0.0)
        {
            FilePath = filePath;
            MediaFolder = mediaFolder;
            PictureLocation = pictureLocation;
            Distance = distance;
        }

        public override string ToString()
        {
            return $"ImageInfo: Distance={Distance:F2}, PictureLocation='{PictureLocation}', Exists={System.IO.File.Exists(FilePath)}";
        }
    }
}
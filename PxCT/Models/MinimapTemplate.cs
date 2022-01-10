namespace PxCT.Models
{
    /// <summary>Model used to create the json file for the minimap.</summary>
    internal class MinimapTemplate
    {
        #region Properties

        public string filename { get; set; }

        public int height { get; set; }

        public int width { get; set; }

        public int x { get; set; }

        public int y { get; set; }

        #endregion
    }
}
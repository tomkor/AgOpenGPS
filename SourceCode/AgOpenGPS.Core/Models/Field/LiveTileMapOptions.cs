namespace AgOpenGPS.Core.Models
{
    public enum LiveTileMapSource
    {
        OpenStreetMap = 0,
        EsriWorldImagery = 1,
        GeoportalOrtho = 2,
    }

    public class LiveTileMapOptions
    {
        public LiveTileMapOptions(
            bool isEnabled,
            LiveTileMapSource source,
            bool withParcelsOverlay)
        {
            IsEnabled = isEnabled;
            Source = source;
            WithParcelsOverlay = withParcelsOverlay;
        }

        public bool IsEnabled { get; }
        public LiveTileMapSource Source { get; }
        public bool WithParcelsOverlay { get; }
    }
}

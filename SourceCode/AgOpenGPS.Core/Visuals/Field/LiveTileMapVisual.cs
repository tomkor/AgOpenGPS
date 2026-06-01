using AgLibrary.Logging;
using AgOpenGPS.Core.DrawLib;
using AgOpenGPS.Core.Models;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace AgOpenGPS.Core.Visuals
{
    public class LiveTileMapVisual
    {
        private const string OsmUrlTemplate = "https://tile.openstreetmap.org/{z}/{x}/{y}.png";
        private const string EsriUrlTemplate = "https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}";
        private const string GeoportalWmsBase = "https://mapy.geoportal.gov.pl/wss/service/PZGIK/ORTO/WMS/HighResolution";
        private const string EwidencjaWmsBase = "https://integracja.gugik.gov.pl/cgi-bin/KrajowaIntegracjaEwidencjiGruntow";
        private const int TileSize = 256;
        private const int MinZoom = 12;
        private const int MaxTilesPerFrame = 80;
        private const int MaxTileLoadsQueuedPerFrame = 6;
        private const int MaxCachedTiles = 320;
        private const int MaxFailureLogLines = 8;
        private const double MaxMercatorLatitude = 85.05112878;

        private static readonly HttpClient HttpClient = BuildHttpClient();
        private static readonly SemaphoreSlim DownloadSemaphore = new SemaphoreSlim(4);
        private static readonly TimeSpan FailedTileRetryDelay = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan EsriOsmTimeout = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan GeoportalTimeout = TimeSpan.FromSeconds(30);

        private readonly object _sync = new object();
        private readonly Dictionary<TileKey, TileEntry> _tiles = new Dictionary<TileKey, TileEntry>();
        private readonly List<TileEntry> _pendingDispose = new List<TileEntry>();
        private long _accessCounter;
        private LiveTileMapOptions _options;
        private int _downloadFailureLogCount;
        private bool _readyTileLogged;
        private bool _drawStartedLogged;

        private readonly TileDiskCache _diskCache;
        private const long MaxDiskCacheBytes = 500L * 1024 * 1024; // 500 MB

        public LiveTileMapVisual(LiveTileMapOptions options, string cacheDirectory)
        {
            _options = options;
            _diskCache = new TileDiskCache(cacheDirectory);
            _diskCache.EvictToLimit(MaxDiskCacheBytes);
        }

        public void UpdateOptions(LiveTileMapOptions options)
        {
            if (_options.Source == options.Source &&
                _options.WithParcelsOverlay == options.WithParcelsOverlay &&
                _options.IsEnabled == options.IsEnabled)
            {
                return;
            }

            _options = options;
            lock (_sync)
            {
                foreach (TileEntry entry in _tiles.Values)
                {
                    _pendingDispose.Add(entry);
                }
                _tiles.Clear();
            }
        }

        public void Draw(LocalPlane localPlane, GeoBoundingBox worldBounds, double cameraZoom)
        {
            if (!_options.IsEnabled || localPlane == null || worldBounds.IsEmpty)
            {
                return;
            }

            DisposePendingEntries();

            GLW.SetColor(new ColorRgba(1.0f, 1.0f, 1.0f, 1.0f));
            GeoBoundingBox renderBounds = CreateRenderBounds(worldBounds, cameraZoom);
            WgsBounds wgsBounds = ConvertToWgsBounds(localPlane, renderBounds);
            TileWindow tileWindow = CreateTileWindow(wgsBounds, SelectZoom(cameraZoom));
            LogDrawStartedOnce(localPlane, wgsBounds, tileWindow);

            List<TileKey> keys = tileWindow.Keys.Take(MaxTilesPerFrame).ToList();
            int queuedThisFrame = 0;
            foreach (TileKey key in keys)
            {
                TileEntry entry = GetOrQueueTile(key, ref queuedThisFrame);
                DrawTileIfReady(localPlane, key, entry);
            }

            PruneCache();
        }

        private TileEntry GetOrQueueTile(TileKey key, ref int queuedThisFrame)
        {
            lock (_sync)
            {
                if (!_tiles.TryGetValue(key, out TileEntry entry))
                {
                    entry = new TileEntry { State = TileState.Pending };
                    _tiles[key] = entry;
                }

                bool shouldRetry = entry.State == TileState.Failed &&
                    DateTime.UtcNow - entry.LastAttemptUtc >= FailedTileRetryDelay;
                bool shouldQueue = entry.State == TileState.Pending || shouldRetry;
                if (shouldQueue && queuedThisFrame < MaxTileLoadsQueuedPerFrame)
                {
                    QueueLoad(key, entry);
                    queuedThisFrame++;
                }

                entry.LastAccess = ++_accessCounter;
                return entry;
            }
        }

        private void QueueLoad(TileKey key, TileEntry entry)
        {
            entry.State = TileState.Loading;
            entry.LastAttemptUtc = DateTime.UtcNow;
            Task.Run(() =>
            {
                Bitmap bitmap = null;
                Exception downloadException = null;
                try
                {
                    DownloadSemaphore.Wait();
                    try
                    {
                        bitmap = DownloadTileBitmap(key);
                    }
                    finally
                    {
                        DownloadSemaphore.Release();
                    }
                }
                catch (Exception ex)
                {
                    downloadException = ex;
                    bitmap?.Dispose();
                    bitmap = null;
                }

                lock (_sync)
                {
                    if (bitmap == null)
                    {
                        entry.State = TileState.Failed;
                        LogTileFailure(key, downloadException, "tile");
                        return;
                    }

                    entry.Bitmap = bitmap;
                    entry.State = TileState.Ready;
                    LogReadyTileOnce(key);
                }
            });
        }

        private void DrawTileIfReady(LocalPlane localPlane, TileKey key, TileEntry entry)
        {
            Bitmap bitmap;
            lock (_sync)
            {
                if (entry.State != TileState.Ready)
                {
                    return;
                }

                if (entry.Texture == null)
                {
                    entry.Texture = new GeoTexture2D(entry.Bitmap);
                }

                bitmap = entry.Bitmap;
            }

            if (bitmap == null)
            {
                return;
            }

            TileBounds bounds = GetTileBounds(key.Zoom, key.X, key.Y);
            GeoCoord topLeft = localPlane.ConvertWgs84ToGeoCoord(new Wgs84(bounds.LatTop, bounds.LonLeft));
            GeoCoord bottomRight = localPlane.ConvertWgs84ToGeoCoord(new Wgs84(bounds.LatBottom, bounds.LonRight));
            GeoCoord u0v0Map = new GeoCoord(topLeft.Easting, topLeft.Northing);
            GeoCoord u1v1Map = new GeoCoord(bottomRight.Easting, bottomRight.Northing);

            lock (_sync)
            {
                entry.Texture?.DrawZ(u0v0Map, u1v1Map, -0.09);
            }
        }

        private void PruneCache()
        {
            lock (_sync)
            {
                if (_tiles.Count <= MaxCachedTiles)
                {
                    return;
                }

                List<TileKey> keysToRemove = _tiles
                    .Where(pair => pair.Value.State != TileState.Loading)
                    .OrderBy(pair => pair.Value.LastAccess)
                    .Take(_tiles.Count - MaxCachedTiles)
                    .Select(pair => pair.Key)
                    .ToList();

                foreach (TileKey key in keysToRemove)
                {
                    TileEntry entry = _tiles[key];
                    _tiles.Remove(key);
                    _pendingDispose.Add(entry);
                }
            }

            DisposePendingEntries();
        }

        private void DisposePendingEntries()
        {
            List<TileEntry> toDispose;
            lock (_sync)
            {
                if (_pendingDispose.Count == 0)
                {
                    return;
                }

                toDispose = new List<TileEntry>(_pendingDispose);
                _pendingDispose.Clear();
            }

            foreach (TileEntry entry in toDispose)
            {
                entry.Texture?.Dispose();
                entry.Bitmap?.Dispose();
            }
        }

        private Bitmap DownloadTileBitmap(TileKey key)
        {
            string sourceName = _options.Source.ToString();

            byte[] cached = _diskCache.TryRead(sourceName, key.Zoom, key.X, key.Y, _options.WithParcelsOverlay);
            if (cached != null)
            {
                return CreateBitmap(cached);
            }

            TimeSpan baseTimeout = _options.Source == LiveTileMapSource.GeoportalOrtho
                ? GeoportalTimeout : EsriOsmTimeout;

            byte[] baseBytes = SafeDownloadImageBytes(BuildBaseTileUrl(key.Zoom, key.X, key.Y), key, "base", baseTimeout);
            if (baseBytes == null && _options.Source == LiveTileMapSource.GeoportalOrtho)
            {
                baseBytes = SafeDownloadImageBytes(BuildEsriTileUrl(key.Zoom, key.X, key.Y), key, "esri fallback", EsriOsmTimeout);
            }

            if (baseBytes == null)
            {
                return null;
            }

            if (!_options.WithParcelsOverlay)
            {
                _diskCache.Write(sourceName, key.Zoom, key.X, key.Y, false, baseBytes);
                return CreateBitmap(baseBytes);
            }

            byte[] overlayBytes = SafeDownloadImageBytes(BuildParcelsOverlayWmsUrl(key.Zoom, key.X, key.Y), key, "parcels", EsriOsmTimeout);
            if (overlayBytes == null)
            {
                // still cache the base so the next pass is instant
                _diskCache.Write(sourceName, key.Zoom, key.X, key.Y, false, baseBytes);
                return CreateBitmap(baseBytes);
            }

            using (Bitmap baseBitmap = CreateBitmap(baseBytes))
            using (Bitmap overlayBitmap = CreateBitmap(overlayBytes))
            {
                Bitmap merged = new Bitmap(TileSize, TileSize, PixelFormat.Format32bppArgb);
                using (Graphics graphics = Graphics.FromImage(merged))
                {
                    graphics.DrawImage(baseBitmap, 0, 0, TileSize, TileSize);
                    graphics.DrawImage(overlayBitmap, 0, 0, TileSize, TileSize);
                }
                using (MemoryStream ms = new MemoryStream())
                {
                    merged.Save(ms, ImageFormat.Png);
                    _diskCache.Write(sourceName, key.Zoom, key.X, key.Y, true, ms.ToArray());
                }
                return merged;
            }
        }

        private byte[] SafeDownloadImageBytes(string url, TileKey key, string layer, TimeSpan timeout)
        {
            try
            {
                return DownloadImageBytes(url, timeout);
            }
            catch (Exception ex)
            {
                LogTileFailure(key, ex, layer);
                return null;
            }
        }

        private string BuildBaseTileUrl(int z, int x, int y)
        {
            switch (_options.Source)
            {
                case LiveTileMapSource.EsriWorldImagery:
                    return BuildEsriTileUrl(z, x, y);

                case LiveTileMapSource.GeoportalOrtho:
                    return BuildGeoportalOrthoWmsUrl(z, x, y);

                default:
                    return OsmUrlTemplate
                        .Replace("{z}", z.ToString(CultureInfo.InvariantCulture))
                        .Replace("{x}", x.ToString(CultureInfo.InvariantCulture))
                        .Replace("{y}", y.ToString(CultureInfo.InvariantCulture));
            }
        }

        private static string BuildEsriTileUrl(int z, int x, int y)
        {
            return EsriUrlTemplate
                .Replace("{z}", z.ToString(CultureInfo.InvariantCulture))
                .Replace("{x}", x.ToString(CultureInfo.InvariantCulture))
                .Replace("{y}", y.ToString(CultureInfo.InvariantCulture));
        }

        private static Bitmap CreateBitmap(byte[] bytes)
        {
            using (MemoryStream stream = new MemoryStream(bytes))
            using (Image image = Image.FromStream(stream))
            {
                return new Bitmap(image);
            }
        }

        private static byte[] DownloadImageBytes(string url, TimeSpan timeout)
        {
            using (var cts = new CancellationTokenSource(timeout))
            using (HttpResponseMessage response = HttpClient.GetAsync(url, cts.Token).GetAwaiter().GetResult())
            {
                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }

                byte[] bytes = response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                string contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
                if (!contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) &&
                    !LooksLikeImage(bytes))
                {
                    return null;
                }

                return bytes;
            }
        }

        private static bool LooksLikeImage(byte[] bytes)
        {
            if (bytes == null || bytes.Length < 4)
            {
                return false;
            }

            bool isPng = bytes.Length > 8 &&
                bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47;
            bool isJpeg = bytes[0] == 0xFF && bytes[1] == 0xD8;
            bool isGif = bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46;
            bool isBmp = bytes[0] == 0x42 && bytes[1] == 0x4D;
            return isPng || isJpeg || isGif || isBmp;
        }

        private void LogTileFailure(TileKey key, Exception exception, string layer)
        {
            if (_downloadFailureLogCount >= MaxFailureLogLines)
            {
                return;
            }

            _downloadFailureLogCount++;
            string detail = exception == null ? "no image returned" : exception.Message;
            Log.EventWriter($"Live map {layer} failed ({_options.Source}) z{key.Zoom} x{key.X} y{key.Y}: {detail}");
        }

        private void LogReadyTileOnce(TileKey key)
        {
            if (_readyTileLogged)
            {
                return;
            }

            _readyTileLogged = true;
            Log.EventWriter($"Live map tile ready ({_options.Source}) z{key.Zoom} x{key.X} y{key.Y}");
        }

        private void LogDrawStartedOnce(LocalPlane localPlane, WgsBounds bounds, TileWindow tileWindow)
        {
            if (_drawStartedLogged)
            {
                return;
            }

            _drawStartedLogged = true;
            Log.EventWriter(string.Format(
                CultureInfo.InvariantCulture,
                "Live map draw started enabled={0} source={1} parcels={2} origin={3:F6},{4:F6} bounds={5:F6},{6:F6},{7:F6},{8:F6} zoom={9} tiles={10}",
                _options.IsEnabled,
                _options.Source,
                _options.WithParcelsOverlay,
                localPlane.Origin.Latitude,
                localPlane.Origin.Longitude,
                bounds.LatMin,
                bounds.LatMax,
                bounds.LonMin,
                bounds.LonMax,
                tileWindow.Zoom,
                tileWindow.Count));
        }

        private static string BuildGeoportalOrthoWmsUrl(int z, int x, int y)
        {
            TileBounds bounds = GetTileBounds(z, x, y);
            string bbox = string.Format(
                CultureInfo.InvariantCulture,
                "{0:F6},{1:F6},{2:F6},{3:F6}",
                bounds.LonLeft,
                bounds.LatBottom,
                bounds.LonRight,
                bounds.LatTop);

            return GeoportalWmsBase +
                   "?SERVICE=WMS&VERSION=1.3.0&REQUEST=GetMap" +
                   "&LAYERS=Raster&STYLES=&CRS=CRS:84" +
                   $"&BBOX={bbox}&WIDTH={TileSize}&HEIGHT={TileSize}&FORMAT=image/jpeg";
        }

        private static string BuildParcelsOverlayWmsUrl(int z, int x, int y)
        {
            const double halfCircle = 20037508.3427892;
            double tileSize = halfCircle * 2.0 / (1 << z);

            double xMin = x * tileSize - halfCircle;
            double xMax = xMin + tileSize;
            double yMax = halfCircle - y * tileSize;
            double yMin = yMax - tileSize;

            string bbox = string.Format(
                CultureInfo.InvariantCulture,
                "{0:F2},{1:F2},{2:F2},{3:F2}",
                xMin,
                yMin,
                xMax,
                yMax);

            return EwidencjaWmsBase +
                   "?SERVICE=WMS&VERSION=1.3.0&REQUEST=GetMap" +
                   "&LAYERS=dzialki,numery_dzialek&STYLES=" +
                   "&CRS=EPSG:3857" +
                   $"&BBOX={bbox}&WIDTH={TileSize}&HEIGHT={TileSize}&FORMAT=image/png&TRANSPARENT=TRUE";
        }

        private static HttpClient BuildHttpClient()
        {
            HttpClient httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("AgOpenGPS/1.0 (live-tile-client)");
            return httpClient;
        }

        private static GeoBoundingBox CreateRenderBounds(GeoBoundingBox worldBounds, double cameraZoom)
        {
            GeoCoord center = worldBounds.CenterCoord;
            double radius = Math.Max(160.0, Math.Min(4500.0, cameraZoom * cameraZoom * 3.0));
            GeoBoundingBox renderBounds = GeoBoundingBox.CreateEmpty();
            renderBounds.Include(new GeoCoord(center.Northing - radius, center.Easting - radius));
            renderBounds.Include(new GeoCoord(center.Northing + radius, center.Easting + radius));
            return renderBounds;
        }

        private static WgsBounds ConvertToWgsBounds(LocalPlane localPlane, GeoBoundingBox bounds)
        {
            Wgs84 northWest = localPlane.ConvertGeoCoordToWgs84(new GeoCoord(bounds.MaxNorthing, bounds.MinEasting));
            Wgs84 southEast = localPlane.ConvertGeoCoordToWgs84(new GeoCoord(bounds.MinNorthing, bounds.MaxEasting));
            return new WgsBounds(
                ClampLatitude(southEast.Latitude),
                ClampLatitude(northWest.Latitude),
                NormalizeLongitude(northWest.Longitude),
                NormalizeLongitude(southEast.Longitude));
        }

        private int SelectZoom(double cameraZoom)
        {
            int maxZoom = _options.Source == LiveTileMapSource.GeoportalOrtho ? 20 : 19;
            if (cameraZoom <= 7) return maxZoom;
            if (cameraZoom <= 12) return Math.Min(maxZoom, 18);
            if (cameraZoom <= 20) return Math.Min(maxZoom, 17);
            if (cameraZoom <= 35) return 16;
            if (cameraZoom <= 60) return 15;
            if (cameraZoom <= 100) return 14;
            return 13;
        }

        private static TileWindow CreateTileWindow(WgsBounds bounds, int zoom)
        {
            TileWindow tileWindow;
            do
            {
                tileWindow = TileWindow.Create(bounds, zoom);
                if (tileWindow.Count <= MaxTilesPerFrame || zoom <= MinZoom)
                {
                    return tileWindow;
                }
                zoom--;
            }
            while (true);
        }

        private static TileBounds GetTileBounds(int z, int x, int y)
        {
            int n = 1 << z;
            double lonLeft = (double)x / n * 360.0 - 180.0;
            double lonRight = (double)(x + 1) / n * 360.0 - 180.0;
            double latTop = TileYToLatitude(y, n);
            double latBottom = TileYToLatitude(y + 1, n);
            return new TileBounds(latTop, latBottom, lonLeft, lonRight);
        }

        private static double TileYToLatitude(int y, int tileCount)
        {
            return Math.Atan(Math.Sinh(Math.PI * (1.0 - 2.0 * y / tileCount))) * 180.0 / Math.PI;
        }

        private static int LongitudeToTileX(double lon, int zoom)
        {
            int n = 1 << zoom;
            int x = (int)Math.Floor((NormalizeLongitude(lon) + 180.0) / 360.0 * n);
            return NormalizeTileX(x, n);
        }

        private static int LatitudeToTileY(double lat, int zoom)
        {
            int n = 1 << zoom;
            double latRad = ClampLatitude(lat) * Math.PI / 180.0;
            int y = (int)Math.Floor((1.0 - Math.Log(Math.Tan(latRad) + 1.0 / Math.Cos(latRad)) / Math.PI) / 2.0 * n);
            return Math.Max(0, Math.Min(n - 1, y));
        }

        private static int NormalizeTileX(int x, int tileCount)
        {
            int normalized = x % tileCount;
            if (normalized < 0)
            {
                normalized += tileCount;
            }
            return normalized;
        }

        private static double ClampLatitude(double lat)
        {
            return Math.Max(-MaxMercatorLatitude, Math.Min(MaxMercatorLatitude, lat));
        }

        private static double NormalizeLongitude(double lon)
        {
            while (lon < -180.0) lon += 360.0;
            while (lon > 180.0) lon -= 360.0;
            return lon;
        }

        private struct TileKey : IEquatable<TileKey>
        {
            public TileKey(int zoom, int x, int y)
            {
                Zoom = zoom;
                X = x;
                Y = y;
            }

            public int Zoom { get; }
            public int X { get; }
            public int Y { get; }

            public bool Equals(TileKey other)
            {
                return Zoom == other.Zoom &&
                       X == other.X &&
                       Y == other.Y;
            }

            public override bool Equals(object obj)
            {
                return obj is TileKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    int hashCode = Zoom;
                    hashCode = (hashCode * 397) ^ X;
                    hashCode = (hashCode * 397) ^ Y;
                    return hashCode;
                }
            }
        }

        private class TileEntry
        {
            public TileState State { get; set; }
            public Bitmap Bitmap { get; set; }
            public GeoTexture2D Texture { get; set; }
            public long LastAccess { get; set; }
            public DateTime LastAttemptUtc { get; set; }
        }

        private enum TileState
        {
            Pending,
            Loading,
            Ready,
            Failed,
        }

        private struct WgsBounds
        {
            public WgsBounds(double latMin, double latMax, double lonMin, double lonMax)
            {
                LatMin = Math.Min(latMin, latMax);
                LatMax = Math.Max(latMin, latMax);
                LonMin = Math.Min(lonMin, lonMax);
                LonMax = Math.Max(lonMin, lonMax);
            }

            public double LatMin { get; }
            public double LatMax { get; }
            public double LonMin { get; }
            public double LonMax { get; }
        }

        private struct TileBounds
        {
            public TileBounds(double latTop, double latBottom, double lonLeft, double lonRight)
            {
                LatTop = latTop;
                LatBottom = latBottom;
                LonLeft = lonLeft;
                LonRight = lonRight;
            }

            public double LatTop { get; }
            public double LatBottom { get; }
            public double LonLeft { get; }
            public double LonRight { get; }
        }

        private class TileWindow
        {
            private TileWindow(List<TileKey> keys, int zoom)
            {
                Keys = keys;
                Zoom = zoom;
            }

            public List<TileKey> Keys { get; }
            public int Count => Keys.Count;
            public int Zoom { get; }

            public static TileWindow Create(WgsBounds bounds, int zoom)
            {
                int n = 1 << zoom;
                int xMin = LongitudeToTileX(bounds.LonMin, zoom);
                int xMax = LongitudeToTileX(bounds.LonMax, zoom);
                int yMin = LatitudeToTileY(bounds.LatMax, zoom);
                int yMax = LatitudeToTileY(bounds.LatMin, zoom);

                List<TileKey> keys = new List<TileKey>();
                for (int x = xMin; x <= xMax; x++)
                {
                    for (int y = yMin; y <= yMax; y++)
                    {
                        keys.Add(new TileKey(zoom, NormalizeTileX(x, n), y));
                    }
                }

                return new TileWindow(keys, zoom);
            }
        }
    }
}

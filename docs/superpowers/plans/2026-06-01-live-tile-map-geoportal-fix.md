# Live Tile Map – Geoportal Fix Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the geoportal ortophoto render reliably under the tractor by adding a fast ESRI fallback layer, a persistent on-disk tile cache, and a longer per-request download timeout for geoportal.

**Architecture:** All rendering/download logic stays in `LiveTileMapVisual`. A new, pure, unit-tested `TileDiskCache` handles disk persistence. The cache directory is plumbed from the GPS app (`RegistrySettings.baseDirectory`) through `WorldGrid` into `LiveTileMapVisual`. The dead GMap.NET file is removed.

**Tech Stack:** C# .NET Framework 4.8, NUnit 4 (`AgOpenGPS.Core.Tests`), System.Drawing, OpenTK/OpenGL (`GeoTexture2D`), `System.Net.Http.HttpClient`.

**Branch:** `fix/live-tile-map-geoportal` (already created; spec already committed).

---

## Background / Root Cause (read first)

Log evidence (`Documents\AgOpenGPS\Logs\AgOpenGPS_Events_Log.txt`):
- Config + draw start correctly; coordinates and bounds are valid.
- Exactly one tile rendered (`tile ready z17 x73671 y42396`); the other 24 failed with `Zadanie zostało anulowane` = `TaskCanceledException` = the global `HttpClient.Timeout = 10s` cancelling slow geoportal WMS requests.
- No disk cache exists, so restarts re-hit the timeouts.

Full design: `docs/superpowers/specs/2026-06-01-live-tile-map-geoportal-fix-design.md`.

## File Structure

- **Create** `SourceCode/AgOpenGPS.Core/Visuals/Field/TileDiskCache.cs` — pure disk cache: key→path, read, write, size-based eviction. No GL, no network. Unit-tested.
- **Create** `SourceCode/AgOpenGPS.Core.Tests/Visuals/TileDiskCacheTests.cs` — NUnit tests for the cache.
- **Modify** `SourceCode/AgOpenGPS.Core/Visuals/Field/LiveTileMapVisual.cs` — per-request timeout, disk-cache integration, two-layer (ESRI under geoportal) tiles.
- **Modify** `SourceCode/AgOpenGPS.Core/Models/WorldGrid.cs` — accept + forward a `TileCacheDirectory`.
- **Modify** `SourceCode/GPS/Forms/GUI.Designer.cs` (`ConfigureLiveTileMap`) — set the cache directory from `RegistrySettings.baseDirectory`.
- **Delete** `SourceCode/GPS/Map/TileOverlayMapProvider.cs` — dead, unused GMap.NET approach.

Notes on conventions:
- Core models/visuals use **no `C` prefix**; `GPS/Classes` use `C` prefix. New types live in Core, so: `TileDiskCache` (no prefix).
- Cache files are stored extension-agnostic (`.tile`); decoding uses `Image.FromStream`, which sniffs the format, so the extension does not matter.
- Cache filename encodes the parcels flag so merged (`_p`) and base tiles never collide.

---

## Task 1: TileDiskCache (TDD)

**Files:**
- Create: `SourceCode/AgOpenGPS.Core/Visuals/Field/TileDiskCache.cs`
- Test: `SourceCode/AgOpenGPS.Core.Tests/Visuals/TileDiskCacheTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `SourceCode/AgOpenGPS.Core.Tests/Visuals/TileDiskCacheTests.cs`:

```csharp
using System;
using System.IO;
using AgOpenGPS.Core.Visuals;
using NUnit.Framework;

namespace AgOpenGPS.Core.Tests.Visuals
{
    [TestFixture]
    public class TileDiskCacheTests
    {
        private string _root;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "AogTileCacheTests_" + Guid.NewGuid().ToString("N"));
        }

        [TearDown]
        public void TearDown()
        {
            try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
        }

        [Test]
        public void Disabled_When_Root_Is_Null_Or_Empty()
        {
            var cache = new TileDiskCache(null);
            Assert.That(cache.IsEnabled, Is.False);
            cache.Write("GeoportalOrtho", 17, 1, 2, false, new byte[] { 1, 2, 3 });
            Assert.That(cache.TryRead("GeoportalOrtho", 17, 1, 2, false), Is.Null);
        }

        [Test]
        public void Write_Then_Read_Roundtrips_Bytes()
        {
            var cache = new TileDiskCache(_root);
            byte[] data = { 9, 8, 7, 6 };
            cache.Write("GeoportalOrtho", 17, 73671, 42396, false, data);

            byte[] read = cache.TryRead("GeoportalOrtho", 17, 73671, 42396, false);
            Assert.That(read, Is.EqualTo(data));
        }

        [Test]
        public void Parcels_Flag_Does_Not_Collide_With_Base()
        {
            var cache = new TileDiskCache(_root);
            cache.Write("GeoportalOrtho", 17, 1, 1, false, new byte[] { 1 });
            cache.Write("GeoportalOrtho", 17, 1, 1, true, new byte[] { 2 });

            Assert.That(cache.TryRead("GeoportalOrtho", 17, 1, 1, false), Is.EqualTo(new byte[] { 1 }));
            Assert.That(cache.TryRead("GeoportalOrtho", 17, 1, 1, true), Is.EqualTo(new byte[] { 2 }));
        }

        [Test]
        public void TryRead_Returns_Null_When_Absent()
        {
            var cache = new TileDiskCache(_root);
            Assert.That(cache.TryRead("EsriWorldImagery", 10, 5, 5, false), Is.Null);
        }

        [Test]
        public void EvictToLimit_Removes_Oldest_First()
        {
            var cache = new TileDiskCache(_root);
            // Each blob is 1000 bytes; write three with increasing timestamps.
            cache.Write("S", 1, 0, 0, false, new byte[1000]);
            string oldestPath = cache.GetFilePath("S", 1, 0, 0, false);
            File.SetLastWriteTimeUtc(oldestPath, DateTime.UtcNow.AddMinutes(-30));

            cache.Write("S", 1, 0, 1, false, new byte[1000]);
            File.SetLastWriteTimeUtc(cache.GetFilePath("S", 1, 0, 1, false), DateTime.UtcNow.AddMinutes(-20));

            cache.Write("S", 1, 0, 2, false, new byte[1000]);
            File.SetLastWriteTimeUtc(cache.GetFilePath("S", 1, 0, 2, false), DateTime.UtcNow.AddMinutes(-10));

            // Keep only ~2000 bytes -> oldest (0,0) must be deleted.
            cache.EvictToLimit(2000);

            Assert.That(cache.TryRead("S", 1, 0, 0, false), Is.Null, "oldest should be evicted");
            Assert.That(cache.TryRead("S", 1, 0, 1, false), Is.Not.Null);
            Assert.That(cache.TryRead("S", 1, 0, 2, false), Is.Not.Null);
        }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test SourceCode/AgOpenGPS.Core.Tests/AgOpenGPS.Core.Tests.csproj --filter "FullyQualifiedName~TileDiskCacheTests"`
Expected: FAIL — `TileDiskCache` does not exist (compile error).

- [ ] **Step 3: Implement TileDiskCache**

Create `SourceCode/AgOpenGPS.Core/Visuals/Field/TileDiskCache.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AgOpenGPS.Core.Visuals
{
    /// <summary>
    /// Best-effort on-disk cache for map tiles. All operations swallow IO errors:
    /// a broken cache must never break rendering. Files are stored extension-agnostic
    /// (.tile); decoding relies on Image.FromStream sniffing the format.
    /// </summary>
    public sealed class TileDiskCache
    {
        private readonly string _root;

        public TileDiskCache(string rootDirectory)
        {
            _root = string.IsNullOrWhiteSpace(rootDirectory) ? null : rootDirectory;
        }

        public bool IsEnabled => _root != null;

        public string GetFilePath(string source, int z, int x, int y, bool withParcels)
        {
            string name = withParcels ? $"{y}_p.tile" : $"{y}.tile";
            return Path.Combine(_root, source, z.ToString(), x.ToString(), name);
        }

        public byte[] TryRead(string source, int z, int x, int y, bool withParcels)
        {
            if (!IsEnabled) return null;
            try
            {
                string path = GetFilePath(source, z, x, y, withParcels);
                return File.Exists(path) ? File.ReadAllBytes(path) : null;
            }
            catch
            {
                return null;
            }
        }

        public void Write(string source, int z, int x, int y, bool withParcels, byte[] bytes)
        {
            if (!IsEnabled || bytes == null || bytes.Length == 0) return;
            try
            {
                string path = GetFilePath(source, z, x, y, withParcels);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllBytes(path, bytes);
            }
            catch
            {
                // ignore: cache writes are best-effort
            }
        }

        /// <summary>Deletes oldest files (by last write time) until total size is under maxBytes.</summary>
        public void EvictToLimit(long maxBytes)
        {
            if (!IsEnabled) return;
            try
            {
                if (!Directory.Exists(_root)) return;

                List<FileInfo> files = new DirectoryInfo(_root)
                    .GetFiles("*.tile", SearchOption.AllDirectories)
                    .OrderBy(f => f.LastWriteTimeUtc)
                    .ToList();

                long total = files.Sum(f => f.Length);
                foreach (FileInfo file in files)
                {
                    if (total <= maxBytes) break;
                    long len = file.Length;
                    try { file.Delete(); total -= len; } catch { }
                }
            }
            catch
            {
                // ignore: eviction is best-effort
            }
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test SourceCode/AgOpenGPS.Core.Tests/AgOpenGPS.Core.Tests.csproj --filter "FullyQualifiedName~TileDiskCacheTests"`
Expected: PASS (5 tests).

- [ ] **Step 5: Commit**

```bash
git add SourceCode/AgOpenGPS.Core/Visuals/Field/TileDiskCache.cs SourceCode/AgOpenGPS.Core.Tests/Visuals/TileDiskCacheTests.cs
git commit -m "Add TileDiskCache for persistent map tile storage"
```

---

## Task 2: Per-request download timeout in LiveTileMapVisual

Replace the global `HttpClient.Timeout` with a per-request `CancellationToken` so geoportal gets 30s while ESRI/OSM stay at 10s. (No unit test — network code; verified by build + Task 7 run.)

**Files:**
- Modify: `SourceCode/AgOpenGPS.Core/Visuals/Field/LiveTileMapVisual.cs`

- [ ] **Step 1: Remove the global timeout from the HttpClient factory**

In `BuildHttpClient()` delete the `httpClient.Timeout = ...` line (leave the UserAgent header):

```csharp
private static HttpClient BuildHttpClient()
{
    HttpClient httpClient = new HttpClient();
    httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("AgOpenGPS/1.0 (live-tile-client)");
    return httpClient;
}
```

- [ ] **Step 2: Add a per-source timeout constant and thread it into downloads**

Add constants near the other `private const` fields:

```csharp
private static readonly TimeSpan EsriOsmTimeout = TimeSpan.FromSeconds(10);
private static readonly TimeSpan GeoportalTimeout = TimeSpan.FromSeconds(30);
```

Change `DownloadImageBytes` to accept a timeout and pass a cancellation token:

```csharp
private static byte[] DownloadImageBytes(string url, TimeSpan timeout)
{
    using (var cts = new System.Threading.CancellationTokenSource(timeout))
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
```

- [ ] **Step 3: Update callers to pass a timeout**

`SafeDownloadImageBytes` gains a timeout parameter and forwards it:

```csharp
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
```

In `DownloadTileBitmap`, pass the correct timeout per layer (geoportal base = `GeoportalTimeout`, esri fallback + parcels = `EsriOsmTimeout`; OSM/ESRI base = `EsriOsmTimeout`):

```csharp
private Bitmap DownloadTileBitmap(TileKey key)
{
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
        return CreateBitmap(baseBytes);
    }

    byte[] overlayBytes = SafeDownloadImageBytes(BuildParcelsOverlayWmsUrl(key.Zoom, key.X, key.Y), key, "parcels", EsriOsmTimeout);
    if (overlayBytes == null)
    {
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
        return merged;
    }
}
```

- [ ] **Step 4: Build to verify it compiles**

Run: `dotnet build SourceCode/AgOpenGPS.Core/AgOpenGPS.Core.csproj -c Release`
Expected: Build succeeded, 0 errors.

- [ ] **Step 5: Commit**

```bash
git add SourceCode/AgOpenGPS.Core/Visuals/Field/LiveTileMapVisual.cs
git commit -m "Use per-request timeout (30s geoportal, 10s esri/osm) for tile downloads"
```

---

## Task 3: Plumb the cache directory from GPS into LiveTileMapVisual

Pass `Documents\AgOpenGPS\TileCache` from the GPS app down to the visual, and construct a `TileDiskCache`. (No unit test — wiring; verified by build.)

**Files:**
- Modify: `SourceCode/AgOpenGPS.Core/Visuals/Field/LiveTileMapVisual.cs`
- Modify: `SourceCode/AgOpenGPS.Core/Models/WorldGrid.cs`
- Modify: `SourceCode/GPS/Forms/GUI.Designer.cs`

- [ ] **Step 1: Give LiveTileMapVisual a cache and a constructor parameter**

Add a field and accept the cache directory in the constructor:

```csharp
private readonly TileDiskCache _diskCache;
private const long MaxDiskCacheBytes = 500L * 1024 * 1024; // 500 MB

public LiveTileMapVisual(LiveTileMapOptions options, string cacheDirectory)
{
    _options = options;
    _diskCache = new TileDiskCache(cacheDirectory);
    _diskCache.EvictToLimit(MaxDiskCacheBytes);
}
```

- [ ] **Step 2: WorldGrid forwards a cache directory**

In `SourceCode/AgOpenGPS.Core/Models/WorldGrid.cs` add a property and use it when creating the visual. Add near the other fields:

```csharp
public string TileCacheDirectory { private get; set; }
```

Update the `LiveTileMapOptions` setter construction line:

```csharp
if (_liveTileMapVisual == null)
{
    _liveTileMapVisual = new LiveTileMapVisual(_liveTileMapOptions, TileCacheDirectory);
}
else
{
    _liveTileMapVisual.UpdateOptions(_liveTileMapOptions);
}
```

- [ ] **Step 3: GPS sets the cache directory before options**

In `SourceCode/GPS/Forms/GUI.Designer.cs`, in `ConfigureLiveTileMap()`, set the directory **before** assigning `LiveTileMapOptions` (so it is available when the visual is first constructed). Insert just before the `worldGrid.LiveTileMapOptions = ...` assignment:

```csharp
worldGrid.TileCacheDirectory = System.IO.Path.Combine(RegistrySettings.baseDirectory, "TileCache");
```

(`RegistrySettings.baseDirectory` is `public static string`; confirmed in `SourceCode/GPS/Properties/RegistrySettings.cs:41`.)

- [ ] **Step 4: Integrate disk cache into the download path**

In `LiveTileMapVisual.DownloadTileBitmap`, check disk first and write back on success. Replace the method body’s top and the two return points so that:
- before downloading, `TryRead` the cached tile (keyed by the user-selected source name + parcels flag) and, if present, decode and return it;
- after producing `baseBytes` (non-parcels) or the merged PNG bytes (parcels), persist via `Write`.

```csharp
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
```

- [ ] **Step 5: Build the full solution**

Run: `dotnet build SourceCode/AgOpenGPS.sln -c Release`
Expected: Build succeeded, 0 errors. (Confirms the new constructor param and `WorldGrid`/`GUI.Designer` wiring all line up.)

- [ ] **Step 6: Commit**

```bash
git add SourceCode/AgOpenGPS.Core/Visuals/Field/LiveTileMapVisual.cs SourceCode/AgOpenGPS.Core/Models/WorldGrid.cs SourceCode/GPS/Forms/GUI.Designer.cs
git commit -m "Persist map tiles to disk cache under Documents\\AgOpenGPS\\TileCache"
```

---

## Task 4: Two-layer tiles — ESRI fast fallback under geoportal

When the selected source is `GeoportalOrtho`, also load an ESRI tile for each cell. Draw ESRI immediately; replace with geoportal when it arrives. (No unit test — GL; verified by build + Task 7 run.)

**Files:**
- Modify: `SourceCode/AgOpenGPS.Core/Visuals/Field/LiveTileMapVisual.cs`

- [ ] **Step 1: Extend TileEntry with a fast (ESRI) layer**

Replace the `TileEntry` class and `TileState` usage so each entry tracks two independent layers:

```csharp
private class TileEntry
{
    public TileState PrimaryState { get; set; }
    public Bitmap PrimaryBitmap { get; set; }
    public GeoTexture2D PrimaryTexture { get; set; }

    public TileState FastState { get; set; }
    public Bitmap FastBitmap { get; set; }
    public GeoTexture2D FastTexture { get; set; }

    public long LastAccess { get; set; }
    public DateTime PrimaryLastAttemptUtc { get; set; }
    public DateTime FastLastAttemptUtc { get; set; }
}
```

`TileState` keeps its existing values (`Pending, Loading, Ready, Failed`); add `Disabled` for the fast layer when no fallback is wanted:

```csharp
private enum TileState
{
    Pending,
    Loading,
    Ready,
    Failed,
    Disabled,
}
```

- [ ] **Step 2: Helper — should this source use a fast fallback layer?**

Add:

```csharp
private bool UseFastFallback => _options.Source == LiveTileMapSource.GeoportalOrtho;
```

- [ ] **Step 3: Queue both layers in GetOrQueueTile**

Replace `GetOrQueueTile` so it initialises both layers and queues each independently. The fast layer is `Disabled` for non-geoportal sources:

```csharp
private TileEntry GetOrQueueTile(TileKey key, ref int queuedThisFrame)
{
    lock (_sync)
    {
        if (!_tiles.TryGetValue(key, out TileEntry entry))
        {
            entry = new TileEntry
            {
                PrimaryState = TileState.Pending,
                FastState = UseFastFallback ? TileState.Pending : TileState.Disabled,
            };
            _tiles[key] = entry;
        }

        // Fast (ESRI) layer first so cells fill immediately.
        if (entry.FastState != TileState.Disabled)
        {
            TryQueueLayer(key, entry, isFast: true, ref queuedThisFrame);
        }
        TryQueueLayer(key, entry, isFast: false, ref queuedThisFrame);

        entry.LastAccess = ++_accessCounter;
        return entry;
    }
}

// Must hold _sync.
private void TryQueueLayer(TileKey key, TileEntry entry, bool isFast, ref int queuedThisFrame)
{
    TileState state = isFast ? entry.FastState : entry.PrimaryState;
    DateTime lastAttempt = isFast ? entry.FastLastAttemptUtc : entry.PrimaryLastAttemptUtc;

    bool shouldRetry = state == TileState.Failed &&
        DateTime.UtcNow - lastAttempt >= FailedTileRetryDelay;
    bool shouldQueue = state == TileState.Pending || shouldRetry;
    if (shouldQueue && queuedThisFrame < MaxTileLoadsQueuedPerFrame)
    {
        QueueLoad(key, entry, isFast);
        queuedThisFrame++;
    }
}
```

- [ ] **Step 4: QueueLoad loads either layer**

Replace `QueueLoad` so it downloads the correct layer and stores into the matching slot:

```csharp
private void QueueLoad(TileKey key, TileEntry entry, bool isFast)
{
    if (isFast) { entry.FastState = TileState.Loading; entry.FastLastAttemptUtc = DateTime.UtcNow; }
    else { entry.PrimaryState = TileState.Loading; entry.PrimaryLastAttemptUtc = DateTime.UtcNow; }

    Task.Run(() =>
    {
        Bitmap bitmap = null;
        Exception downloadException = null;
        try
        {
            DownloadSemaphore.Wait();
            try
            {
                bitmap = isFast ? DownloadFastBitmap(key) : DownloadTileBitmap(key);
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
                if (isFast) entry.FastState = TileState.Failed;
                else entry.PrimaryState = TileState.Failed;
                LogTileFailure(key, downloadException, isFast ? "fast" : "tile");
                return;
            }

            if (isFast) { entry.FastBitmap = bitmap; entry.FastState = TileState.Ready; }
            else { entry.PrimaryBitmap = bitmap; entry.PrimaryState = TileState.Ready; }
            LogReadyTileOnce(key);
        }
    });
}
```

- [ ] **Step 5: Add DownloadFastBitmap (ESRI, disk-cached)**

```csharp
private Bitmap DownloadFastBitmap(TileKey key)
{
    const string fastSource = "EsriWorldImagery";
    byte[] cached = _diskCache.TryRead(fastSource, key.Zoom, key.X, key.Y, false);
    if (cached != null)
    {
        return CreateBitmap(cached);
    }

    byte[] bytes = SafeDownloadImageBytes(BuildEsriTileUrl(key.Zoom, key.X, key.Y), key, "fast", EsriOsmTimeout);
    if (bytes == null)
    {
        return null;
    }

    _diskCache.Write(fastSource, key.Zoom, key.X, key.Y, false, bytes);
    return CreateBitmap(bytes);
}
```

- [ ] **Step 6: Draw best-available layer**

Replace `DrawTileIfReady` to draw primary if ready, else fast:

```csharp
private void DrawTileIfReady(LocalPlane localPlane, TileKey key, TileEntry entry)
{
    Bitmap drawBitmap = null;
    bool drawPrimary;

    lock (_sync)
    {
        if (entry.PrimaryState == TileState.Ready)
        {
            if (entry.PrimaryTexture == null) entry.PrimaryTexture = new GeoTexture2D(entry.PrimaryBitmap);
            drawBitmap = entry.PrimaryBitmap;
            drawPrimary = true;
        }
        else if (entry.FastState == TileState.Ready)
        {
            if (entry.FastTexture == null) entry.FastTexture = new GeoTexture2D(entry.FastBitmap);
            drawBitmap = entry.FastBitmap;
            drawPrimary = false;
        }
        else
        {
            return;
        }
    }

    if (drawBitmap == null) return;

    TileBounds bounds = GetTileBounds(key.Zoom, key.X, key.Y);
    GeoCoord topLeft = localPlane.ConvertWgs84ToGeoCoord(new Wgs84(bounds.LatTop, bounds.LonLeft));
    GeoCoord bottomRight = localPlane.ConvertWgs84ToGeoCoord(new Wgs84(bounds.LatBottom, bounds.LonRight));
    GeoCoord u0v0Map = new GeoCoord(topLeft.Easting, topLeft.Northing);
    GeoCoord u1v1Map = new GeoCoord(bottomRight.Easting, bottomRight.Northing);

    lock (_sync)
    {
        if (drawPrimary) entry.PrimaryTexture?.DrawZ(u0v0Map, u1v1Map, -0.09);
        else entry.FastTexture?.DrawZ(u0v0Map, u1v1Map, -0.095);
    }
}
```

- [ ] **Step 7: Update disposal and pruning for two layers**

In `DisposePendingEntries`, dispose both textures/bitmaps:

```csharp
foreach (TileEntry entry in toDispose)
{
    entry.PrimaryTexture?.Dispose();
    entry.PrimaryBitmap?.Dispose();
    entry.FastTexture?.Dispose();
    entry.FastBitmap?.Dispose();
}
```

In `PruneCache`, treat an entry as "loading" (not evictable) if **either** layer is loading. Change the `.Where(...)` predicate:

```csharp
List<TileKey> keysToRemove = _tiles
    .Where(pair => pair.Value.PrimaryState != TileState.Loading &&
                   pair.Value.FastState != TileState.Loading)
    .OrderBy(pair => pair.Value.LastAccess)
    .Take(_tiles.Count - MaxCachedTiles)
    .Select(pair => pair.Key)
    .ToList();
```

- [ ] **Step 8: Build the full solution**

Run: `dotnet build SourceCode/AgOpenGPS.sln -c Release`
Expected: Build succeeded, 0 errors.

- [ ] **Step 9: Commit**

```bash
git add SourceCode/AgOpenGPS.Core/Visuals/Field/LiveTileMapVisual.cs
git commit -m "Draw fast ESRI fallback layer under geoportal tiles"
```

---

## Task 5: Remove dead TileOverlayMapProvider.cs

**Files:**
- Delete: `SourceCode/GPS/Map/TileOverlayMapProvider.cs`

- [ ] **Step 1: Confirm it is referenced nowhere**

Run: `git grep -n "TileOverlayMapProvider\|TileMapProviderFactory" -- SourceCode`
Expected: matches **only** inside `SourceCode/GPS/Map/TileOverlayMapProvider.cs` itself.

- [ ] **Step 2: Delete the file**

Run: `git rm SourceCode/GPS/Map/TileOverlayMapProvider.cs`
(The file is currently untracked — if `git rm` reports it is not tracked, delete it with `Remove-Item SourceCode/GPS/Map/TileOverlayMapProvider.cs` and remove the empty `SourceCode/GPS/Map` folder.)

- [ ] **Step 3: Build the full solution**

Run: `dotnet build SourceCode/AgOpenGPS.sln -c Release`
Expected: Build succeeded, 0 errors.

- [ ] **Step 4: Commit**

```bash
git add -A SourceCode/GPS/Map
git commit -m "Remove unused GMap.NET TileOverlayMapProvider"
```

---

## Task 6: Final verification (build, tests, field run)

- [ ] **Step 1: Full build**

Run: `dotnet build SourceCode/AgOpenGPS.sln -c Release`
Expected: Build succeeded, 0 errors, 0 warnings.

- [ ] **Step 2: Run all Core tests**

Run: `dotnet test SourceCode/AgOpenGPS.Core.Tests/AgOpenGPS.Core.Tests.csproj`
Expected: PASS, including the 5 `TileDiskCacheTests`.

- [ ] **Step 3: Manual run against a real field**

Launch AgOpenGPS, open a field near the logged origin (53.49, 22.35), enable the live map with `GeoportalOrtho` + parcels.

Expected behaviour:
- Imagery appears under the tractor within ~1s (ESRI fast layer), then sharpens to geoportal ortho as primary tiles arrive.
- In `Documents\AgOpenGPS\Logs\AgOpenGPS_Events_Log.txt`: early `tile ready` lines (fast/ESRI), then geoportal readiness; far fewer `... base failed ... Zadanie zostało anulowane` lines than before.
- `Documents\AgOpenGPS\TileCache\` is populated with `.tile` files.

- [ ] **Step 4: Restart-over-same-field check**

Close and reopen AgOpenGPS over the same field.
Expected: tiles render almost instantly from `TileCache` with no network wait; log shows no/very few download lines for already-cached cells.

---

## Self-Review notes

- **Spec coverage:** ESRI fast layer (Task 4), disk cache (Tasks 1+3), per-request timeout (Task 2), delete dead file (Task 5), dual-layer only for geoportal (`UseFastFallback`, Task 4 Step 2), parcels composited onto primary (Task 3 Step 4), cache eviction ~500 MB at startup (Task 3 Step 1). All covered.
- **Type consistency:** `LiveTileMapVisual(LiveTileMapOptions, string)` constructor used by `WorldGrid` (Task 3 Step 2). `TileDiskCache` API (`IsEnabled`, `GetFilePath`, `TryRead`, `Write`, `EvictToLimit`) identical across Task 1 + callers in Tasks 3–4. `TileState.Disabled` added before use. `DownloadFastBitmap`/`DownloadTileBitmap` both referenced in `QueueLoad`.
- **Placeholders:** none — every code step shows full code.
```

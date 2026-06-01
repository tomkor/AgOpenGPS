# Live Tile Map – Geoportal Fix (Design)

**Date:** 2026-06-01
**Author:** tomkor (with Claude)
**Status:** Approved design, pending spec review
**Scope:** Bug fix + small hardening of the live tile map under the moving tractor. Maintenance mode: smallest change that makes geoportal usable in the field.

## Problem

The live tile map (geoportal ORTO ortophoto drawn as a texture under the vehicle) appears blank in the main navigation view.

### Evidence (from `Documents\AgOpenGPS\Logs\AgOpenGPS_Events_Log.txt`)

```
Live map configured enabled=True source=GeoportalOrtho parcels=True       -> config OK
Live map draw started ... origin=53.49,22.35 ... zoom=17 tiles=25          -> draw runs, LocalPlane + bounds OK
Live map tile ready (GeoportalOrtho) z17 x73671 y42396                     -> ONE tile downloaded + rendered
Live map base failed (GeoportalOrtho) ... : Zadanie zostało anulowane.     -> 24/25 tiles: TaskCanceledException (timeout)
```

### Root cause

Wiring, tile↔WMS BBOX math, `LocalPlane`, and `GeoTexture2D.DrawZ` are all correct — one tile rendered successfully. The failure is **download throughput**: the Geoportal ORTO WMS HighResolution endpoint is slow, and with a global `HttpClient.Timeout = 10s` and 25 tiles requested at zoom 17, almost every request is cancelled (`TaskCanceledException` = "Zadanie zostało anulowane"). Net result: ~1/25 tiles load → effectively blank.

Secondary: there is **no persistent (disk) cache** — only an in-memory cache (`MaxCachedTiles = 320`). Every app restart re-downloads everything and re-hits the timeouts.

## Goal

Chosen direction: **Option B** — geoportal stays the target imagery, but:
1. ESRI World Imagery shows immediately as a fast fallback background while geoportal tiles load.
2. Geoportal downloads are hardened (separate longer timeout) so tiles stop being cancelled.
3. Tiles persist to disk so subsequent views/restarts are instant.

## Design

All changes are concentrated in `AgOpenGPS.Core/Visuals/Field/LiveTileMapVisual.cs`. The wiring (`WorldGrid`, `FormGPS.ConfigureLiveTileMap`, settings, `LiveTileMapOptions`) is unchanged.

### 1. Two-layer tile (core change)

- `TileEntry` gains two textures instead of one:
  - `FastTexture` / `FastBitmap` — ESRI World Imagery (fast XYZ).
  - `PrimaryTexture` / `PrimaryBitmap` — the user-selected source (geoportal).
- Load order per tile cell:
  - **ESRI first** (fast) → drawn immediately so there are no blank cells.
  - **Geoportal in background** → when ready, drawn on top (replaces ESRI for that cell).
- Per-cell draw rule: draw `Primary` if ready, else draw `Fast` if ready, else nothing.
- The dual-layer behaviour is **only active when `_options.Source == GeoportalOrtho`**. For `OpenStreetMap` / `EsriWorldImagery` sources the visual behaves as today (single layer, no extra ESRI fetch). No new settings toggle.
- Parcels overlay (`WithParcelsOverlay`) is composited onto the **primary** (geoportal) tile as today; if only the fast tile is showing, parcels are drawn once the primary arrives.

### 2. Persistent disk cache

- Location: `Documents\AgOpenGPS\TileCache\{source}\{z}\{x}\{y}.{ext}` (`jpg` for geoportal/ESRI base, `png` for merged-with-parcels).
- Read path: before any network request, check disk → if present, decode and use (no network).
- Write path: after a successful download (and after parcels merge), persist the bytes/bitmap to disk.
- Eviction: at startup, if total cache size exceeds **~500 MB**, delete oldest files (by last-write time) until under the limit. Cheap, best-effort, wrapped in try/catch — cache failures never break rendering.
- Cache key includes the parcels flag so merged vs. non-merged tiles don't collide (e.g. separate file name suffix or subfolder).

### 3. Hardened geoportal download

- Replace the single global `HttpClient.Timeout` with a **per-request `CancellationToken`** (via `CancellationTokenSource` with a timeout):
  - ESRI / OSM: ~10 s.
  - Geoportal ORTO: ~30 s.
- Keep the existing failed-tile retry/backoff (`FailedTileRetryDelay`). Because ESRI now fills the cell immediately, the user never stares at a blank area while geoportal retries.
- Keep `DownloadSemaphore` concurrency limiting; geoportal’s longer timeout no longer starves the queue because results are cached after first success.

### Out of scope (YAGNI)

- No migration to WMTS (potential later optimization).
- No new UI toggle.
- Delete the unused, untracked `SourceCode/GPS/Map/TileOverlayMapProvider.cs` — it is a dead parallel GMap.NET approach, wired nowhere, and only causes confusion.

## Files touched

- `SourceCode/AgOpenGPS.Core/Visuals/Field/LiveTileMapVisual.cs` — two-layer tiles, disk cache, per-request timeout.
- `SourceCode/GPS/Map/TileOverlayMapProvider.cs` — removed (dead code).

## Verification

1. Build the solution (Release) — 0 errors.
2. Run with geoportal source + parcels enabled near a known field (origin 53.49, 22.35).
3. Expect in the log: immediate `tile ready` for ESRI cells, then geoportal `tile ready` lines; the count of `... base failed ... Zadanie zostało anulowane` drops sharply.
4. Visually: imagery appears under the tractor within ~1 s (ESRI), sharpens to geoportal ortho as tiles arrive.
5. Restart app over the same field → tiles load from `TileCache` with no network wait.

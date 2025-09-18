Context Overview

- App: Single-project WPF (.NET) ArcGIS Runtime app. Solution `POSM_MR3.sln`, main project `POSM_MR3_2.csproj`.
- Core: `MainWindow.xaml(.cs)` hosts the `MapView`, unified search panel, and asset/video tooling. Services under `Services/` encapsulate config, networking, map loading, and offline.
- Config: `config.json` holds `apiKey`, `runtimeLicenseString`, `mapId` (WebMap ID or MMPK path), `idField`, `selectedLayer`, and `queryLayers` (configurable layer search).
- Maps: `MapService` loads either MMPK (local path) or WebMap (GUID via ArcGIS Online) and initializes `MapView` with extents and basemap.
- Search:
  - Address mode uses ArcGIS World Geocoding with the API key.
  - Asset mode searches configured `queryLayers` across `searchFields`, with optional `displayField` and global `idField` fallback.
  - `InMemorySearchIndex` builds an attribute replica for instant suggestions; `LayerSearchSource` provides suggestions and live LIKE queries.
  - `ReplicaCache` (wrapper) warms the index at startup and supports streaming results from live queries into the cache.
- UX:
  - Toggle: Address | Asset, TextBox with autocomplete, suggestions list (keyboard nav + Enter), and found/not found indicator.
  - On selection/search, map zooms/highlights. Address mode drops a pin overlay.
- Offline: `OfflineMapService` manages taking maps offline; `InspectionImageOverlay` displays inspection image pins.

Key Files

- `MainWindow.xaml(.cs)`: UI + unified dual-mode search, geocode + asset search flow.
- `Services/MapService.cs`: Loads MMPK or WebMap, initializes view, sets extents.
- `Services/ConfigurationService.cs`: Loads/saves `config.json`.
- `LayerSearchService.cs`: Asset search across `queryLayers` + fields, with simple caching.
- `InMemorySearchIndex.cs`: Attribute replica index for instant autocomplete and exact/substring matching.
- `ReplicaCache.cs`: Thin wrapper around `InMemorySearchIndex` used to warm and stream entries.

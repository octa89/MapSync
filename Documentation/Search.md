Enhanced Dual-Mode Search

Overview

- Two modes: Address (geocode) and Asset (layer find).
- Mode toggle in the unified search panel: Address | Asset.
- Autocomplete suggestions in Asset mode are instant from an in-memory replica; falls back to live LIKE queries if needed.
- UI stays responsive with 300ms debounce, cancellation of stale queries, and progress/feedback text.

Configuration

- `config.json` keys:
  - `apiKey`: ArcGIS API key used for the World Geocoding service.
  - `mapId`: Either a local MMPK path or a WebMap GUID. MMPK loads offline; WebMap requires connectivity.
  - `idField`: Global fallback ID field when a layer has no `searchFields`.
  - `queryLayers`: Array of objects defining asset search:
    - `layerName` (string): Target layer to query.
    - `searchFields` (string[]): Fields to search with LIKE.
    - `displayField` (string, optional): Field to show in results; defaults to the matching field.
    - `enabled` (bool): Only `true` layers are included.

Startup Flow

- App reads and validates `config.json` (see `Services/ConfigurationService.cs`).
- Map loads via `Services/MapService.cs`:
  - If `mapId` is a file path → open MMPK and use first map.
  - If `mapId` is a GUID → load WebMap via ArcGIS Online (API key set in `App.xaml.cs`).
- Attribute replica warms in memory:
  - `ReplicaCache` wraps `InMemorySearchIndex` and pre-fetches `{OBJECTID, searchFields, displayField}` for each enabled layer, without geometry.
  - Replica provides instant suggestions and exact/substring matching.

Address Mode

- Uses ArcGIS World Geocoding (`https://geocode-api.arcgis.com/.../World/GeocodeServer`).
- Constrained by current map extent and `CountryCode="USA"` (adjustable).
- On success: centers/zooms to `DisplayLocation` and drops a pin overlay.

Asset Mode

- `LayerSearchService` queries only `enabled` `queryLayers`.
- Restricts search strictly to `searchFields` per layer; uses `displayField` when provided and `idField` as a global fallback.
- Query semantics: server-side `UPPER(field) LIKE UPPER('%term%')` with result limits per field.
- Results list is shown; double-click or Enter zooms and highlights the feature.

Autocomplete

- Primary source: `ReplicaCache` (instant, client-side) via `InMemorySearchIndex`.
- Fallback: live LIKE queries via `LayerSearchSource.SuggestAsync` with paging limits.
- Live suggestions stream into the replica cache (`Upsert`) for subsequent instant results.

Performance & UX

- Debounce: 300ms between keystrokes before querying.
- Cancellation: stale suggestion queries are cancelled (3s timeout guard).
- Async/await throughout; UI remains responsive.
- Feedback: status text shows “Searching…”, “Found”, or “Not found”.
- Keyboard: Up/Down to navigate suggestions/results; Enter to apply/auto-run.

Key Components

- `MainWindow.xaml(.cs)`: Unified search UI and handlers, pin overlay, result selection + zoom.
- `ReplicaCache.cs`: Wrapper to warm and stream into `InMemorySearchIndex`.
- `InMemorySearchIndex.cs`: Attribute replica with exact/substring matching and suggestion generation.
- `LayerSearchSource.cs`: Autocomplete source; uses replica first, falls back to live LIKE queries; streams new hits to cache.
- `LayerSearchService.cs`: Executes asset searches across layers/fields with basic caching.

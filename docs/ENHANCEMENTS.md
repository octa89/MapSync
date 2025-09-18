# Enhancements Summary

This document summarizes key enhancements and fixes made to Map Sync and provides context for why they were implemented.

## 2025‑01‑10

### Offline Map Generation (Basemap by Reference)

- Added “Take Map Offline…” and “Return to Online Map” under Map Options
- Implemented `OfflineMapTask` using the current WebMap’s `PortalItem` to avoid losing identity
- If the WebMap sets `ReferenceBasemapFilename`, and the matching `.tpkx` exists under `offlineBasemapPath`, the app reuses it by reference (faster, no basemap export/sign‑in)
- Detailed Debug logging for OfflineMap jobs (PortalItem id, filename, progress, layer errors, additional errors)

### Robust WebMap vs MMPK Handling

- Startup now detects `mapId`:
  - If it’s a file path → open MMPK
  - Otherwise → treat as WebMap ID
- Reprojection logic updated to preserve the existing `Map` instance (and its `PortalItem`); only the basemap is swapped — fixes “Illegal state: The online map must have an item property…” during offline generation

### Default Basemap & Layer Visibility

- Configurable default basemap saved in config and synchronized with the gallery
- Layer visibility defaults persisted between sessions and applied at startup

### Loader Improvements

- Semi‑transparent frosted background with crisp content (logo/progress)
- Blurs only the map while loading; overlay content stays sharp

### Configuration Additions

- `offlineBasemapPath` folder for on‑device `.tpkx`
- `webMapId` for online return
- `defaultBasemap`, `layerVisibilities` for UX defaults

### Quality & Diagnostics

- Additional Debug logging for map init, offline job processing, and error capture


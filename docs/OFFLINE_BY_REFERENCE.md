# Offline Map Generation — Basemap by Reference

This guide explains how Map Sync takes a WebMap offline using the current view as the area of interest (AOI), and reuses a basemap already on the device instead of exporting one.

## Why use a device basemap?

- Reduce download size and time when creating offline maps
- Share a single `.tpkx` across many offline maps
- Use custom basemaps authored in ArcGIS Pro
- Avoid sign‑in for Esri basemaps when local tiles exist

## Authoring prerequisites (WebMap)

- The WebMap should set `ReferenceBasemapFilename` to the expected basemap file name (e.g., `my_imagery.tpkx`)

## App configuration

- `offlineBasemapPath`: absolute folder path containing the `.tpkx` basemap files
- Optionally, `webMapId` for “Return to Online Map”

Example `config.json`:

```
{
  "mapId": "acc027394bc84c2fb04d1ed317aac674",
  "webMapId": "acc027394bc84c2fb04d1ed317aac674",
  "offlineBasemapPath": "C:\\Basemaps"
}
```

## How it works

1. Load your WebMap (ensure `mapId` is a WebMap ID, not a file path)
2. Choose Map Options → “Take Map Offline…”
3. If the WebMap sets `ReferenceBasemapFilename` and the file exists under `offlineBasemapPath`, Map Sync prompts: “Use the offline basemap?”
4. Choose Yes to reference the `.tpkx` (no basemap export)
5. The offline package is created under `%TEMP%\POSMOfflineMap\<timestamp>` and loaded into the app
6. When ready to return to online, use Map Options → “Return to Online Map”

## Implementation details

- The app creates `OfflineMapTask` from the current `PortalItem` (not a mutated `Map`)
- `GenerateOfflineMapParameters` is configured with the AOI and `ContinueOnErrors = true`
- If `ReferenceBasemapFilename` is set and the file is found under `offlineBasemapPath`, `ReferenceBasemapDirectory` is set to that folder

## Troubleshooting

- If the app says it isn’t a WebMap:
  - Ensure you switched to online (`webMapId` or a non‑file `mapId`)
  - Confirm the map wasn’t replaced internally — Map Sync now preserves `Map.Item` by only swapping basemaps
- If no prompt appears for basemap reuse:
  - Ensure the WebMap sets `ReferenceBasemapFilename`
  - Confirm `offlineBasemapPath` contains the matching `.tpkx`
- If the job fails with layer errors:
  - Some services/layers may not be offline‑enabled; see Debug console logs for details


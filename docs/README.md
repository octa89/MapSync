# Map Sync — POSM GIS Asset Management Tool

## Overview

Map Sync is a Windows Presentation Foundation (WPF) application built in C# using the Esri ArcGIS Runtime SDK. It is a companion to the POSM software to facilitate seamless integration with GIS data. Map Sync lets you extract and interact with asset information from GIS maps, launch inspections, view historical data, and manage assets efficiently — all in one intuitive interface.

The tool works with both industry‑standard datasets and custom templates. It aligns field mappings to ensure asset information is accurately transferred to inspection templates, making it easier to review previous inspections, access metadata, and link to web‑based media.

## What’s New (2025‑01‑10)

- Add “Take Map Offline…” and “Return to Online Map” under Map Options
- Offline map generation supports reusing a local basemap (.tpkx) by reference when the web map specifies `ReferenceBasemapFilename` and `offlineBasemapPath` points to the basemap folder
- New configuration keys documented below: `offlineBasemapPath` and optional `webMapId`
- Robust WebMap vs MMPK detection at startup; preserves WebMap identity (PortalItem) so offline works reliably
- Loader refreshed with a frosted, translucent background and crisp foreground (logo + progress)
- Additional Debug logging so offline job issues are easy to diagnose

## Features & Capabilities

### Critical Capability: Hybrid Online/Offline Operation

- Offline Mode: open Mobile Map Packages (MMPK files) without internet
- Online Mode: access WebMaps via ArcGIS Portal using WebMap IDs
- Automatic Detection: adapts to network availability
- Seamless Transition: switch between online/offline without restarting
- Preserved Functionality: core data workflows work in both modes

### Offline Map Generation (Basemap by Reference)

- Take a WebMap offline using the current view as the AOI
- If the WebMap author sets `ReferenceBasemapFilename`, Map Sync reuses a local basemap by reference (no export/sign‑in)
- Configure the on‑device basemap directory via `offlineBasemapPath` in `config.json`
- Menu actions: Map Options → “Take Map Offline…” and “Return to Online Map”

### GIS Data Integration

- Load maps via a WebMap ID (online) or a local MMPK path (offline)
- Identify features and display built‑in popups with details
- Optional draggable popups for flexible on‑screen positioning

### Navigation & Basemaps

- Zoom controls and programmatic zoom to selection
- Basemap Gallery window for switching basemaps
- Default basemap preference persisted via configuration

### POSM Integration

- Launch the external POSM application with selected Asset ID and configured inspection type
- Supports mapping layer fields to POSM database fields and appending data

### Historical Inspection & Metadata Access

- View previous inspections and link to media/metadata via URLs

## Installation

### Prerequisites

- OS: Windows 10/11
- .NET: .NET 8 SDK
- SDKs & Libraries:
  - Esri ArcGIS Runtime SDK for .NET
  - Newtonsoft.Json (NuGet)
- POSM Software: installed and `posmExecutablePath` configured

## Configuration Keys (Quick Reference)

- `runtimeLicenseString`: ArcGIS Runtime license string
- `apiKey`: ArcGIS Runtime API key for premium/online content
- `posmExecutablePath`: full path to `POSM.exe`
- `mapId`: WebMap ID (online) or full path to a `.mmpk` (offline)
- `webMapId` (optional): explicit WebMap ID for “Return to Online Map”
- `offlineBasemapPath`: folder containing local `.tpkx` basemaps for offline by reference
- `defaultBasemap` (optional): friendly basemap name (e.g., “World Imagery”)
- `selectedLayer` / `idField`: app workflow defaults
- `queryLayers`: search configuration for layers and fields
- `layerVisibilities`: persisted defaults for layer on/off state

Example `config.json`:

```
{
  "runtimeLicenseString": "runtimelite,...",
  "apiKey": "<your-key>",
  "posmExecutablePath": "C:\\POSM\\POSM.exe",
  "mapId": "acc027394bc84c2fb04d1ed317aac674",
  "webMapId": "acc027394bc84c2fb04d1ed317aac674",
  "offlineBasemapPath": "C:\\Basemaps",
  "defaultBasemap": "World Imagery",
  "selectedLayer": "ssGravityMain",
  "idField": "AssetID",
  "queryLayers": [],
  "layerVisibilities": {}
}
```

## Usage

### Launching the Application

- `dotnet run --project POSM_MR3_2.csproj`
- App loads `mapId`: if it’s a file path → opens MMPK; else treats as WebMap ID

### Online/Offline Toggle

- Take Map Offline: Menu → Map Options → “Take Map Offline…”
  - Uses current view as AOI
  - If WebMap sets `ReferenceBasemapFilename`, Map Sync checks `offlineBasemapPath` for a matching `.tpkx` and references it
- Return to Online Map: Menu → Map Options → “Return to Online Map”
  - Loads `webMapId` if set; otherwise uses `mapId` when it is not a file path

### Interacting with the Map

- Identify features by tapping/clicking; highlights and shows a popup
- Draggable popups
- Zoom In/Zoom Out buttons and programmatic zoom to selection

### Loading an MMPK

- File → Open MMPK
- Choose a local `.mmpk` file; the first map in the package is loaded

### Basemap Gallery

- Menu → Map Options → Basemap; choose from common ArcGIS/OSM styles

### Editing Configuration

- Configuration → Edit Configuration; set API key, POSM path, inspection type, default basemap, search layer settings

### Launching POSM

- After selecting a feature and confirming the Asset ID, click “Launch POSM”
- If the POSM executable is missing, you’ll be prompted to update settings

## Code Structure

- `App.xaml.cs`: Startup logic, configuration loading, ArcGIS Runtime licensing
- `MainWindow.xaml(.cs)`: Core logic
  - Map initialization (WebMap + MMPK)
  - Identify/popups, zoom & navigation, loader management
  - Offline map generation (OfflineMapTask) and basemap by reference
- `BasemapGalleryWindow`: Basemap selection
- `ConfigWindow`: Configuration editing (incl. default basemap & layer search)
- `LayersWindow`: Layer visibility and defaults
- `AppendToPosmDbWindow`: Append asset data to POSM database

## Enhancements

See `docs/ENHANCEMENTS.md` for a detailed list of recent changes and their rationale.

## Troubleshooting

- Map Loading Errors:
  - Verify `mapId` is correct and accessible
  - For MMPK: ensure file exists and contains at least one map
  - For WebMap: verify internet connectivity and WebMap ID
- Offline Issues:
  - For basemap by reference, ensure WebMap sets `ReferenceBasemapFilename` and `offlineBasemapPath` contains the matching `.tpkx`
  - If basemap export is required, ensure your API key/credentials are valid
- WebMap/MMPK Switching:
  - “Return to Online Map” relies on `webMapId` or a non‑file `mapId`
- POSM Launch:
  - Confirm `posmExecutablePath` points to a valid POSM executable
- Licensing/API Key:
  - Ensure API key and license string are valid; API key is not required for pure MMPK workflows

## License

POSM Software LLC

## Contact

- Developed by Octavio Pereira
- Email: Octavio@posmsoftware.com
- Organization: POSM Software

## Building the Installer (Optional)

- Recommended: use `dotnet publish` to produce a distributable
- Example: `dotnet publish POSM_MR3_2.csproj -c Release -r win-x64 --self-contained true -o .\publish`


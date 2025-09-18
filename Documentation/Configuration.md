# Configuration Guide

## Overview

POSM Map Reader uses a single `config.json` file as the central configuration source. This file controls all runtime behavior, from map settings to search parameters.

## Configuration File Structure

### **Complete config.json Example**
```json
{
  "runtimeLicenseString": "runtimelite,1000,rud4288660560,none,MJJC7XLS1ML0LAMEY242",
  "apiKey": "AAPT85fOqywZsicJupSmVSCGrpXO1qJwPQjNUMcDYphlO6sfLZegLdT1g4dF4BoRRYtJ1c1p_5YXGfzbmTgx5up-1fxMheVBom1uGtjz0ztA_h7cTKdlUm-XX-i6pqHBzXvzVJ4hLPvi-g-hgHPamxLyJi9INldxIDGLgLDd6E9anTY1lfk7H72yC5Y0ze7inpFYGbyngZNu2kxBx1ZzGIx4XugmcE3US4dSVSVFn-kpbyE.AT2_tWnrrjbG",
  "posmExecutablePath": "C:\\POSM\\POSM.exe",
  "mapId": "3a0241d5bb564c9b86a7a312ba2703d3",
  "idField": "AssetID",
  "inspectionType": "POSM",
  "selectedLayer": "ssGravityMain",
  "offlineMode": false,
  "offlineBasemapPath": "",
  "defaultBasemap": "World_Imagery",
  "layerVisibilities": {
    "FeatureLayer_ssGravityMain": true,
    "FeatureLayer_ssManholes": false
  },
  "queryLayers": [
    {
      "layerName": "ssGravityMain",
      "searchFields": ["AssetID", "StartID"],
      "displayField": "AssetID",
      "enabled": true
    },
    {
      "layerName": "ssManholes", 
      "searchFields": ["AssetID"],
      "displayField": "AssetID",
      "enabled": true
    },
    {
      "layerName": "F_O",
      "searchFields": ["FaultName"],
      "displayField": "AssetID", 
      "enabled": true
    }
  ]
}
```

## Configuration Properties

### **Core Settings**

#### **runtimeLicenseString** (string, required)
- **Purpose**: ArcGIS Runtime license for production deployment
- **Example**: `"runtimelite,1000,rud4288660560,none,MJJC7XLS1ML0LAMEY242"`
- **Notes**: Required for removing "Developer Use Only" watermark

#### **apiKey** (string, required)
- **Purpose**: ArcGIS API key for premium services and content
- **Example**: `"AAPT85fOqywZsicJupSmVSCG..."`
- **Notes**: Required for geocoding, premium basemaps, and online services

#### **mapId** (string, required)
- **Purpose**: WebMap ID or path to MMPK file
- **Examples**: 
  - WebMap: `"3a0241d5bb564c9b86a7a312ba2703d3"`
  - MMPK: `"C:\\Maps\\MyMap.mmpk"`

### **POSM Integration**

#### **posmExecutablePath** (string)
- **Purpose**: Path to POSM.exe for launching inspections
- **Default**: `"C:\\POSM\\POSM.exe"`
- **Notes**: Must be valid path for POSM integration to work

#### **inspectionType** (string)
- **Purpose**: Type of inspection system being used
- **Default**: `"POSM"`
- **Options**: `"POSM"`, `"NASSCO PACP"`

#### **idField** (string)
- **Purpose**: Field name used to link GIS features to POSM sessions
- **Default**: `"AssetID"`
- **Notes**: Must match field name in both GIS and POSM database

#### **selectedLayer** (string)
- **Purpose**: Default layer for POSM inspections
- **Example**: `"ssGravityMain"`
- **Notes**: Should match one of the layer names in the map

### **Map Display Settings**

#### **offlineMode** (boolean)
- **Purpose**: Force offline mode, skip online connectivity checks
- **Default**: `false`
- **Notes**: When true, disables geocoding and online basemaps

#### **offlineBasemapPath** (string)
- **Purpose**: Path to offline basemap for reprojection scenarios
- **Example**: `"C:\\Basemaps\\OfflineBasemap.tpk"`
- **Notes**: Used when online basemaps are unavailable

#### **defaultBasemap** (string)
- **Purpose**: Default basemap selection
- **Options**: `"World_Imagery"`, `"World_Street_Map"`, `"World_Topographic_Map"`
- **Notes**: Applied after map loads if specified

#### **layerVisibilities** (object)
- **Purpose**: Persistent layer visibility settings
- **Structure**: `{ "layerId": boolean }`
- **Example**: 
  ```json
  {
    "FeatureLayer_ssGravityMain": true,
    "FeatureLayer_ssManholes": false
  }
  ```
- **Notes**: Updated automatically by LayersVisibilityWindow

### **Search System Configuration**

#### **queryLayers** (array)
The heart of the search system configuration. Each object defines a searchable layer:

```json
{
  "layerName": "ssGravityMain",
  "searchFields": ["AssetID", "StartID", "Material"],
  "displayField": "AssetID",
  "enabled": true
}
```

**Properties:**
- **layerName** (string, required): Exact layer name as it appears in the map
- **searchFields** (array, required): List of field names to search within
- **displayField** (string, required): Field to display in search results
- **enabled** (boolean, required): Whether to include this layer in searches

**Performance Notes:**
- Only enabled layers are queried
- Limit searchFields to essential fields for better performance
- Use indexed fields in searchFields when possible

## Configuration Management

### **Automatic Configuration Creation**
If `config.json` doesn't exist, the application creates a default configuration:

```csharp
Configuration = new Config
{
    runtimeLicenseString = string.Empty, // Must be provided by user
    apiKey = string.Empty, // Must be provided by user
    posmExecutablePath = @"C:\POSM\POSM.exe",
    inspectionType = "NASSCO PACP",
    mapId = string.Empty, // Must be provided by user
    idField = "AssetID",
    selectedLayer = string.Empty,
    queryLayers = new List<QueryLayerConfig>
    {
        new QueryLayerConfig
        {
            layerName = "ssGravityMain",
            searchFields = new List<string> { "AssetID", "Material", "Size" },
            displayField = "AssetID",
            enabled = true
        }
    }
};
```

### **Configuration Service**
The `IConfigurationService` handles all configuration operations:

- **Loading**: Reads config.json at startup
- **Saving**: Persists changes back to file
- **Updating**: Thread-safe in-memory updates
- **Validation**: Ensures required fields are present

### **Runtime Configuration Updates**
Some settings can be updated through the UI:
- **ConfigWindow**: Modify core settings
- **LayersVisibilityWindow**: Update layer visibility preferences
- **Search Settings**: Enable/disable layers through the interface

## Best Practices

### **Security**
- **Never commit**: API keys or license strings to source control
- **Environment Variables**: Consider using environment variables for sensitive data
- **Access Control**: Restrict config.json file permissions

### **Performance**
- **Limit Search Fields**: Only include essential fields in queryLayers
- **Enable Status**: Set enabled=false for unused layers
- **Cache Management**: Let the application handle search caching automatically

### **Maintenance**
- **Backup Configuration**: Keep backup copies of working configurations
- **Version Control**: Track configuration changes separately from code
- **Documentation**: Document custom configuration for team members

## Troubleshooting

### **Common Issues**

#### **"Map not configured" Error**
- **Cause**: Empty or invalid mapId
- **Solution**: Set valid WebMap ID or MMPK path in config.json

#### **"Developer Use Only" Watermark**
- **Cause**: Missing or invalid runtimeLicenseString
- **Solution**: Obtain valid ArcGIS Runtime license and update config

#### **Search Not Working**
- **Cause**: No enabled layers in queryLayers or invalid layer names
- **Solution**: Verify layer names match exactly and at least one layer is enabled

#### **POSM Integration Fails**
- **Cause**: Invalid posmExecutablePath or missing POSM installation
- **Solution**: Verify POSM.exe path and installation

### **Validation Commands**
Check configuration validity:
```bash
# Verify config.json syntax
Get-Content config.json | ConvertFrom-Json

# Check required fields
$config = Get-Content config.json | ConvertFrom-Json
$config.mapId
$config.apiKey
$config.runtimeLicenseString
```

### **Configuration Templates**

#### **Development Template**
```json
{
  "mapId": "your-development-webmap-id",
  "offlineMode": true,
  "runtimeLicenseString": "",
  "apiKey": "",
  "queryLayers": [
    {
      "layerName": "TestLayer",
      "searchFields": ["ID"],
      "displayField": "ID",
      "enabled": true
    }
  ]
}
```

#### **Production Template**
```json
{
  "mapId": "production-webmap-id",
  "offlineMode": false,
  "runtimeLicenseString": "your-production-license",
  "apiKey": "your-production-api-key",
  "posmExecutablePath": "C:\\POSM\\POSM.exe",
  "queryLayers": [
    // Full production layer configuration
  ]
}
```
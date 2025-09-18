# Enhanced Offline Basemap Guide

## Overview

The POSM Map Reader 3.3.0 includes a completely redesigned offline basemap system that addresses common issues with basemap detection and provides enterprise-grade functionality for offline map generation.

## Key Improvements

### **🔧 Enhanced Basemap Detection**
- **Multiple Format Support**: Automatically detects TPK, TPKX, VTPK, and MMPK files
- **File Validation**: Verifies basemap files before use to prevent corruption issues
- **Smart Fallback**: Intelligent selection when multiple basemaps are available
- **Directory Scanning**: Recursive scanning with detailed file information

### **⚡ Performance Optimizations**
- **Reduced Download Sizes**: Option to use local basemaps instead of downloading
- **Schema-Only Mode**: Download only database structure for editable layers
- **Attachment Control**: Configurable attachment inclusion/exclusion
- **Feature Limiting**: Set maximum features per layer to control size

### **🎯 User Experience Enhancements**
- **Interactive UI**: Comprehensive offline map generation dialog
- **Progress Tracking**: Real-time progress with cancellation support
- **Error Handling**: Graceful error recovery with detailed logging
- **Configuration Integration**: Seamless integration with app configuration

## Architecture

### **Service Layer Design**

#### **IOfflineMapService Interface**
```csharp
public interface IOfflineMapService
{
    // Generate offline map with enhanced basemap handling
    Task<GenerateOfflineMapResult> GenerateOfflineMapAsync(
        Map map, Envelope areaOfInterest, string outputPath, 
        OfflineMapOptions options, CancellationToken cancellationToken);
    
    // Detect and validate local basemap files
    Task<List<BasemapFileInfo>> DetectLocalBasemapsAsync(string basemapDirectory);
    
    // Create enhanced parameters with automatic configuration
    Task<GenerateOfflineMapParameters> CreateEnhancedParametersAsync(Map map, Envelope areaOfInterest);
}
```

#### **Enhanced Options Configuration**
```csharp
var options = new OfflineMapOptions
{
    IncludeBasemap = true,
    LocalBasemapDirectory = @"C:\Basemaps",
    LocalBasemapFilename = "custom_basemap.tpkx",
    ForceLocalBasemap = false,
    SchemaOnlyForEditableLayers = true,
    AttachmentSyncDirection = AttachmentSyncDirection.None,
    MaxFeaturesPerLayer = 10000,
    ShowUserConfirmation = true
};
```

## Implementation Guide

### **1. Basic Offline Map Generation**

```csharp
// Inject the service
private readonly IOfflineMapService _offlineMapService;

// Generate offline map with default settings
var result = await _offlineMapService.GenerateOfflineMapAsync(
    currentMap, 
    mapExtent, 
    @"C:\OfflineMaps\MyMap",
    new OfflineMapOptions { IncludeBasemap = true }
);
```

### **2. Using Local Basemaps**

```csharp
// Detect available local basemaps
var basemaps = await _offlineMapService.DetectLocalBasemapsAsync(@"C:\Basemaps");
var validBasemaps = basemaps.Where(b => b.IsValid).ToList();

// Configure options to use local basemap
var options = new OfflineMapOptions
{
    LocalBasemapDirectory = @"C:\Basemaps",
    LocalBasemapFilename = validBasemaps.First().FileName,
    ForceLocalBasemap = true
};

// Generate with local basemap
var result = await _offlineMapService.GenerateOfflineMapAsync(
    currentMap, mapExtent, outputPath, options);
```

### **3. Performance Optimization**

```csharp
var options = new OfflineMapOptions
{
    // Reduce download size
    SchemaOnlyForEditableLayers = true,
    AttachmentSyncDirection = AttachmentSyncDirection.None,
    MaxFeaturesPerLayer = 5000,
    
    // Use local basemap to avoid download
    LocalBasemapDirectory = basemapPath,
    ForceLocalBasemap = true
};
```

## Configuration Integration

### **config.json Settings**

```json
{
  "offlineBasemapPath": "C:\\Basemaps\\production_basemap.tpkx",
  "offlineMode": false,
  "queryLayers": [
    {
      "layerName": "Assets",
      "enabled": true,
      "maxFeaturesOffline": 10000
    }
  ]
}
```

### **Automatic Configuration Application**
The service automatically applies configuration settings:
- **offlineBasemapPath**: Used as default local basemap
- **offlineMode**: Forces offline-only operation
- **queryLayers**: Applied to offline generation parameters

## Basemap File Support

### **Supported Formats**

| Format | Extension | Description | Use Case |
|--------|-----------|-------------|----------|
| **TPK** | .tpk | Tile Package (Legacy) | Offline tile basemaps |
| **TPKX** | .tpkx | Compact Tile Package | Modern tile basemaps |
| **VTPK** | .vtpk | Vector Tile Package | Styleable vector basemaps |
| **MMPK** | .mmpk | Mobile Map Package | Complete map with layers |

### **File Validation Process**

1. **Extension Check**: Verifies file has supported extension
2. **Size Validation**: Ensures file size is reasonable (0 < size < 50GB)
3. **Existence Check**: Confirms file exists and is accessible
4. **Format Validation**: Basic format verification (future: deep validation)

### **BasemapFileInfo Structure**

```csharp
public class BasemapFileInfo
{
    public string FilePath { get; set; }           // Full path to file
    public string FileName { get; set; }           // File name without extension
    public string FileType { get; set; }           // File extension (tpk, tpkx, etc.)
    public long FileSizeBytes { get; set; }        // File size in bytes
    public DateTime LastModified { get; set; }     // Last modification date
    public bool IsValid { get; set; }              // Validation status
    public string ValidationError { get; set; }    // Error message if invalid
    public string DisplayName { get; }             // User-friendly display name
}
```

## User Interface

### **OfflineMapWindow Features**

#### **Basemap Selection**
- **Radio Button Choice**: Download online vs. use local basemap
- **Directory Browser**: Easy selection of basemap folder
- **File Detection**: Automatic scanning and validation
- **Preview Information**: File size, type, and validation status

#### **Advanced Options**
- **Schema Only**: Faster downloads for data collection scenarios
- **Attachment Control**: Include/exclude existing attachments
- **Feature Limits**: Set maximum features per layer
- **Progress Monitoring**: Real-time progress with cancel option

#### **Validation & Error Handling**
- **Input Validation**: Comprehensive validation before generation
- **Error Messages**: Clear, actionable error messages
- **Recovery Options**: Graceful handling of failures

## Best Practices

### **🏗️ Development Guidelines**

#### **Service Implementation**
```csharp
// ✅ Good: Use dependency injection
public OfflineMapWindow(IOfflineMapService offlineMapService) 
{
    _offlineMapService = offlineMapService;
}

// ❌ Avoid: Direct instantiation
var service = new OfflineMapService();
```

#### **Error Handling**
```csharp
// ✅ Good: Comprehensive error handling
try 
{
    var result = await _offlineMapService.GenerateOfflineMapAsync(...);
    HandleSuccess(result);
}
catch (OperationCanceledException)
{
    HandleCancellation();
}
catch (Exception ex)
{
    _logger.LogError(ex, "Offline map generation failed");
    HandleError(ex);
}
```

#### **Progress Reporting**
```csharp
// ✅ Good: Subscribe to progress events
_offlineMapService.ProgressChanged += (sender, args) => 
{
    Dispatcher.BeginInvoke(() => UpdateProgress(args));
};
```

### **📁 File Organization**

#### **Recommended Directory Structure**
```
C:\POSM_Basemaps\
├── Production\
│   ├── aerial_imagery.tpkx
│   └── street_map.tpk
├── Development\
│   ├── test_basemap.tpkx
│   └── local_imagery.vtpk
└── Archive\
    └── old_basemaps\
```

#### **Naming Conventions**
- **Descriptive Names**: `aerial_2024_highres.tpkx`
- **Version Information**: `basemap_v2.1_production.tpkx`
- **Environment Indicators**: `dev_basemap.tpk`, `prod_imagery.tpkx`

### **⚙️ Configuration Best Practices**

#### **Production Configuration**
```json
{
  "offlineBasemapPath": "C:\\POSM_Basemaps\\Production\\primary_basemap.tpkx",
  "offlineMode": false,
  "defaultOfflineOptions": {
    "schemaOnlyForEditableLayers": true,
    "maxFeaturesPerLayer": 10000,
    "excludeAttachments": true
  }
}
```

#### **Development Configuration**
```json
{
  "offlineBasemapPath": "C:\\POSM_Basemaps\\Development\\test_basemap.tpkx",
  "offlineMode": true,
  "defaultOfflineOptions": {
    "schemaOnlyForEditableLayers": false,
    "maxFeaturesPerLayer": 1000,
    "excludeAttachments": false
  }
}
```

## Troubleshooting

### **Common Issues & Solutions**

#### **"Basemap not recognized" Error**

**Symptoms:**
- Basemap files detected but validation fails
- "File validation failed" message in UI
- Offline map generation fails with basemap errors

**Solutions:**
1. **Check File Integrity**: Verify the basemap file isn't corrupted
   ```bash
   # Check file size and accessibility
   dir "C:\Basemaps\*.tpkx"
   ```

2. **Verify Format Support**: Ensure file extension is supported
   - Supported: `.tpk`, `.tpkx`, `.vtpk`, `.mmpk`
   - Update to supported format if necessary

3. **Check File Permissions**: Ensure read access to basemap files
   ```bash
   # Check file permissions
   icacls "C:\Basemaps\basemap.tpkx"
   ```

4. **Validate ArcGIS Compatibility**: Test basemap in ArcGIS Pro/Online
   - Open basemap in ArcGIS Pro to verify compatibility
   - Re-export if necessary with correct settings

#### **"Local basemap not copying" Issue**

**Root Causes:**
- Incorrect `ReferenceBasemapDirectory` path
- Missing `ReferenceBasemapFilename` configuration
- File path case sensitivity issues
- Directory permissions

**Enhanced Solution Implementation:**
```csharp
// Enhanced basemap configuration with validation
private async Task ConfigureLocalBasemapAsync(GenerateOfflineMapParameters parameters, OfflineMapOptions options)
{
    // Validate directory exists
    if (!Directory.Exists(options.LocalBasemapDirectory))
    {
        throw new DirectoryNotFoundException($"Basemap directory not found: {options.LocalBasemapDirectory}");
    }

    // Detect and validate basemap files
    var basemaps = await DetectLocalBasemapsAsync(options.LocalBasemapDirectory);
    var validBasemap = basemaps.FirstOrDefault(b => b.IsValid && 
        Path.GetFileName(b.FilePath).Equals(options.LocalBasemapFilename, StringComparison.OrdinalIgnoreCase));

    if (validBasemap == null)
    {
        throw new FileNotFoundException($"Valid basemap file not found: {options.LocalBasemapFilename}");
    }

    // Configure parameters with absolute paths
    parameters.ReferenceBasemapDirectory = Path.GetFullPath(options.LocalBasemapDirectory);
    parameters.ReferenceBasemapFilename = Path.GetFileName(validBasemap.FilePath);
    
    _logger.LogInformation("Configured local basemap: {Directory}\\{Filename}", 
        parameters.ReferenceBasemapDirectory, parameters.ReferenceBasemapFilename);
}
```

#### **Slow Offline Generation**

**Performance Optimization Checklist:**
- [ ] Enable `SchemaOnlyForEditableLayers` for data collection scenarios
- [ ] Set reasonable `MaxFeaturesPerLayer` limits
- [ ] Exclude attachments if not needed: `AttachmentSyncDirection = None`
- [ ] Use local basemaps to avoid downloading tiles
- [ ] Optimize area of interest to minimum required extent

#### **Memory Issues During Generation**

**Memory Management Solutions:**
```csharp
// Configure generation for large datasets
var options = new OfflineMapOptions
{
    MaxFeaturesPerLayer = 5000,           // Limit feature count
    SchemaOnlyForEditableLayers = true,   // Reduce data download
    AttachmentSyncDirection = AttachmentSyncDirection.None,  // Exclude attachments
    ForceLocalBasemap = true              // Avoid basemap download
};
```

### **Logging & Diagnostics**

#### **Enable Debug Logging**
```csharp
// In App.xaml.cs ConfigureServices
services.AddLogging(builder =>
{
    builder.AddConsole();
    builder.SetMinimumLevel(LogLevel.Debug);  // Enable debug logging
});
```

#### **Key Log Categories**
- `[OFFLINE MAP]`: General offline map operations
- `[OFFLINE MAP UI]`: User interface interactions
- `[PERFORMANCE]`: Performance metrics and timing
- `[CONFIG]`: Configuration loading and validation

#### **Sample Debug Output**
```
[OFFLINE MAP] Starting offline map generation to C:\OfflineMaps\TestMap
[OFFLINE MAP] Found 3 basemap files (2 valid)
[OFFLINE MAP] Configured local basemap: C:\Basemaps\aerial.tpkx
[OFFLINE MAP] Job created, starting generation...
[PERFORMANCE] Generation completed in 145.7s
[OFFLINE MAP] Generation completed with status: Succeeded
```

## Future Enhancements

### **Planned Improvements**
- **Deep File Validation**: Content-based validation of basemap files
- **Compression Options**: Configurable compression levels for offline packages
- **Synchronization**: Bidirectional sync with online services
- **Batch Processing**: Generate multiple offline maps in sequence
- **Cloud Storage**: Support for cloud-based basemap repositories

### **API Extensions**
- **Basemap Metadata**: Extract and display basemap metadata
- **Preview Generation**: Create thumbnail previews of basemap content
- **Version Management**: Track and manage basemap versions
- **Auto-Update**: Automatic basemap updates when newer versions available

The enhanced offline basemap system provides a robust, production-ready solution for offline map generation with comprehensive error handling, performance optimization, and user-friendly interfaces.
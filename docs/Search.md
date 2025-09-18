# Enhanced Dual-Mode Search System

## Overview

POSM Map Reader 3.3.0 features a completely redesigned search system that combines high-performance attribute replica caching with dual-mode search capabilities. The system provides instant autocomplete suggestions and supports both address geocoding and asset field searching.

## Key Features

### 🔄 **Dual Search Modes**
- **🗺️ Address Mode**: ArcGIS World Geocoding service with local constraints
- **🏢 Asset Mode**: Configuration-driven field search across enabled layers
- **Toggle Interface**: Single button switches between modes seamlessly
- **Context-Aware**: Different placeholder text and behavior per mode

### ⚡ **Performance Optimizations**
- **Startup Replica**: Pre-fetches lightweight attribute data for instant search
- **Lazy Loading**: Falls back to live queries when cache misses occur
- **300ms Debouncing**: Prevents excessive queries during active typing
- **LRU Caching**: 50-entry cache with automatic cleanup
- **Query Cancellation**: Prevents resource buildup from abandoned searches

### 🏗️ **Enterprise Architecture**
- **Service-Based Design**: IReplicaCacheService, ILayerSearchService interfaces
- **Dependency Injection**: Full DI integration with logging and progress reporting
- **Thread-Safe Operations**: Lock-based synchronization for shared cache access
- **Configuration-Driven**: All behavior controlled via config.json

## Architecture

### **Service Layer Design**

#### **IReplicaCacheService**
```csharp
public interface IReplicaCacheService
{
    event EventHandler<ProgressEventArgs> ProgressChanged;
    Task WarmCacheAsync(CancellationToken cancellationToken = default);
    List<InMemorySearchIndex.IndexEntry> Search(string searchText, int maxResults = 20);
    List<string> GetSuggestions(string searchText, int maxSuggestions = 10);
    Task<List<SearchResultItem>> LazyLoadAndCacheAsync(string searchText, CancellationToken cancellationToken = default);
}
```

**Features:**
- **Startup Warming**: Pre-fetches {ObjectID, searchFields, displayField} on app start
- **In-Memory Index**: Trigram-based substring matching for ultra-fast search
- **Progress Reporting**: Event-driven updates during cache building
- **Lazy Loading**: Streams new results into cache when not found

#### **ILayerSearchService**
```csharp
public interface ILayerSearchService
{
    Task<List<SearchResultItem>> SearchLayersAsync(string searchText, CancellationToken cancellationToken = default);
    Task<List<string>> GetSuggestionsAsync(string searchText, int maxSuggestions = 10, CancellationToken cancellationToken = default);
    Task<List<SearchResultItem>> SearchSpecificLayerAsync(string layerName, List<string> searchFields, string searchText, int maxResults = 200, CancellationToken cancellationToken = default);
    Task<List<string>> ValidateConfiguredLayersAsync();
}
```

**Features:**
- **Configuration-Driven**: Reads queryLayers array from config.json
- **Field Validation**: Validates configured fields exist in layers
- **Parallel Queries**: Processes multiple layers concurrently
- **SQL Injection Protection**: Proper parameter escaping

## Implementation Guide

### **1. Configuration Setup**

#### **Enhanced config.json**
```json
{
  "apiKey": "your-arcgis-api-key",
  "mapId": "webmap-id-or-path-to-file.mmpk",
  "queryLayers": [
    {
      "layerName": "ssGravityMain",
      "searchFields": ["AssetID", "StartID", "Material"],
      "displayField": "AssetID",
      "enabled": true
    },
    {
      "layerName": "ssManholes", 
      "searchFields": ["AssetID", "ManholeID"],
      "displayField": "AssetID",
      "enabled": true
    },
    {
      "layerName": "F_O",
      "searchFields": ["FaultName"],
      "displayField": "AssetID",
      "enabled": false
    }
  ]
}
```

**Configuration Properties:**
- **layerName**: Must match exact layer name in the map
- **searchFields**: Array of field names to search within
- **displayField**: Field shown in search results (falls back to first searchField)
- **enabled**: Boolean to include/exclude layer from searches

### **2. Service Registration**

#### **Dependency Injection Setup**
```csharp
// App.xaml.cs - ConfigureServices()
services.AddSingleton<IReplicaCacheService, ReplicaCacheService>();
services.AddTransient<ILayerSearchService, LayerSearchService>();
```

#### **Constructor Injection**
```csharp
// MainWindow.xaml.cs
public MainWindow(
    IReplicaCacheService replicaCacheService,
    ILayerSearchService layerSearchService,
    // ... other services
)
{
    _replicaCacheService = replicaCacheService;
    _layerSearchService = layerSearchService;
}
```

### **3. Initialization Sequence**

#### **Startup Cache Warming**
```csharp
private async Task BuildSearchIndexAsync()
{
    // Initialize the replica cache service with MapView
    if (_replicaCacheService is ReplicaCacheService replicaService)
    {
        replicaService.Initialize(MyMapView);
    }
    
    // Subscribe to progress updates
    _replicaCacheService.ProgressChanged += (sender, args) =>
    {
        Console.WriteLine($"[REPLICA CACHE] {args.Message}");
        _progressReporter.Report(args.Message, args.Percentage);
    };
    
    // Warm the cache with progress reporting
    await _replicaCacheService.WarmCacheAsync();
}
```

#### **Expected Console Output**
```
[REPLICA CACHE] Starting cache warming...
[REPLICA CACHE] Indexing layer 1/3: ssGravityMain
[REPLICA CACHE] Indexing layer 2/3: ssManholes  
[REPLICA CACHE] Indexing layer 3/3: F_O
[REPLICA CACHE] Cache warmed successfully: 15,432 entries, 8,642 index keys
```

## User Interface

### **Toggle Button Interface**

#### **XAML Implementation**
```xml
<ToggleButton x:Name="SearchModeToggle"
             Content="🗺️ Address"
             Checked="SearchModeToggle_Checked"
             Unchecked="SearchModeToggle_Unchecked">
    <ToggleButton.Style>
        <Style TargetType="ToggleButton">
            <Setter Property="Background" Value="#2196F3"/>
            <Setter Property="Foreground" Value="White"/>
            <Style.Triggers>
                <Trigger Property="IsChecked" Value="False">
                    <Setter Property="Background" Value="#4CAF50"/>
                    <Setter Property="Content" Value="🏢 Assets"/>
                </Trigger>
            </Style.Triggers>
        </Style>
    </ToggleButton.Style>
</ToggleButton>
```

#### **Mode Toggle Logic**
```csharp
private void SearchModeToggle_Checked(object sender, RoutedEventArgs e)
{
    _isAddressMode = true;
    UpdateSearchPlaceholder("Search addresses...");
    SearchModeIndicator.Text = "Address search mode - toggle to switch to Assets";
}

private void SearchModeToggle_Unchecked(object sender, RoutedEventArgs e)
{
    _isAddressMode = false;
    UpdateSearchPlaceholder("Search assets (AssetID, StartID)...");
    SearchModeIndicator.Text = "Asset search mode - toggle to switch to Address";
}
```

### **Keyboard Navigation**

#### **Full Keyboard Support**
- **↑/↓ Arrow Keys**: Navigate through autocomplete suggestions
- **Enter**: Select highlighted suggestion and perform search
- **Escape**: Hide suggestions dropdown
- **Tab**: Move focus to next control

#### **Implementation**
```csharp
private async void UnifiedSearchTextBox_KeyDown(object sender, KeyEventArgs e)
{
    switch (e.Key)
    {
        case Key.Down:
            MoveSelectionDown();
            e.Handled = true;
            break;
        case Key.Up:
            MoveSelectionUp();
            e.Handled = true;
            break;
        case Key.Enter:
            await PerformUnifiedSearchAsync();
            e.Handled = true;
            break;
        case Key.Escape:
            HideSuggestions();
            e.Handled = true;
            break;
    }
}
```

## Search Flow Diagrams

### **Address Search Flow**
```
User Input → Toggle to 🗺️ → Type Address → Enter
    ↓
ArcGIS Geocoding Service
    ↓
Country Filter (USA) + Map Extent Bounds
    ↓
Pan/Zoom to Location + Add Pin Graphic
```

### **Asset Search Flow**
```
User Input → Toggle to 🏢 → Type Text
    ↓
Debounce Timer (300ms)
    ↓
Check Replica Cache → Cache Hit? → Display Instant Results
    ↓ (Cache Miss)
Lazy Load from Server → Update Cache → Display Results
```

## Performance Characteristics

### **Typical Response Times**
- **Cache Hits**: < 50ms (instant)
- **Cache Misses**: 150-1000ms (varies by data size)
- **Address Geocoding**: 150-500ms (varies by network)
- **Debounce Delay**: 300ms after typing stops

### **Memory Usage**
- **Replica Cache**: ~2-5MB for full index
- **LRU Suggestion Cache**: ~1MB for 50 entries
- **Query Buffers**: Minimal due to cancellation patterns

### **Performance Monitoring**

#### **Debug Console Logging**
```csharp
#if DEBUG
Console.WriteLine($"[DEBOUNCE] Starting 300ms debounce timer for: '{searchText}'");
Console.WriteLine($"[PERFORMANCE] Query completed in {stopwatch.ElapsedMilliseconds}ms");
Console.WriteLine($"[CACHE] Hit: {cachedResults.Count} items");
Console.WriteLine($"[AUTOCOMPLETE] ✓ Displaying {suggestions.Count} suggestions");
#endif
```

#### **Production Logging Categories**
- `[REPLICA CACHE]`: Cache warming and management operations
- `[LAYER SEARCH]`: Live database query operations
- `[PERFORMANCE]`: Query timing and performance metrics
- `[AUTOCOMPLETE]`: UI suggestion handling
- `[GEOCODING]`: Address search operations

## Configuration Validation

### **Layer Validation Service**
```csharp
var validationIssues = await _layerSearchService.ValidateConfiguredLayersAsync();
foreach (var issue in validationIssues)
{
    _logger.LogWarning("[CONFIG VALIDATION] {Issue}", issue);
}
```

### **Common Validation Issues**
- `Configured layer 'LayerName' not found in map`
- `Layer 'LayerName' missing fields: Field1, Field2`
- `Layer 'LayerName' missing display field: DisplayField`

## Advanced Features

### **Search Result Enhancement**
```csharp
public class SearchResultItem
{
    public string LayerName { get; set; }        // Source layer name
    public Feature Feature { get; set; }         // Full ArcGIS feature
    public string DisplayText { get; set; }      // User-friendly display text
}
```

### **Suggestion Caching Strategy**
```csharp
// LRU Cache with automatic cleanup
private readonly Dictionary<string, List<string>> _suggestionCache = new();

lock (_searchLock)
{
    _suggestionCache[searchText.ToLowerInvariant()] = suggestions;
    
    // Clean cache if it gets too large
    if (_suggestionCache.Count > 100)
    {
        var keysToRemove = _suggestionCache.Keys.Take(50).ToList();
        foreach (var key in keysToRemove)
        {
            _suggestionCache.Remove(key);
        }
    }
}
```

### **Query Cancellation Pattern**
```csharp
private CancellationTokenSource? _currentSearchCts;

private async Task PerformSearchAsync(string searchText)
{
    // Cancel previous search
    _currentSearchCts?.Cancel();
    _currentSearchCts = new CancellationTokenSource();
    
    try
    {
        var results = await SearchServiceAsync(searchText, _currentSearchCts.Token);
        UpdateSuggestions(results);
    }
    catch (OperationCanceledException)
    {
        // Search was cancelled, ignore
    }
}
```

## Troubleshooting Guide

### **Common Issues & Solutions**

#### **No Search Results**
**Symptoms**: Search returns no results despite data existing
**Solutions**:
1. Check queryLayers configuration - verify `enabled: true`
2. Verify layer names match exactly (case-sensitive)
3. Confirm searchFields exist in the layer schema
4. Test with simple field values first

#### **Slow Search Performance**
**Symptoms**: Search takes >5 seconds to respond
**Solutions**:
1. Reduce number of searchFields in configuration
2. Disable unused layers with `enabled: false`
3. Check network connectivity for live queries
4. Monitor database performance on server side

#### **Cache Not Building**
**Symptoms**: Console shows "Cache warmed: 0 entries"
**Solutions**:
1. Verify map is loaded before cache warming
2. Check that layers have queryable features
3. Ensure API key has proper permissions
4. Review layer security settings

#### **Suggestions Not Appearing**
**Symptoms**: Autocomplete dropdown never shows
**Solutions**:
1. Confirm Asset mode is selected (Address mode has no autocomplete)
2. Check minimum 2-character search requirement
3. Verify cache built successfully during startup
4. Test with known existing field values

### **Debug Commands**

#### **Cache Statistics**
```csharp
var stats = _replicaCacheService.GetStatistics();
Console.WriteLine($"Cache: {stats.totalEntries} entries, {stats.indexKeys} keys, initialized: {stats.isInitialized}");
```

#### **Layer Validation**
```csharp
var issues = await _layerSearchService.ValidateConfiguredLayersAsync();
if (issues.Any())
{
    Console.WriteLine($"Configuration issues found: {string.Join(", ", issues)}");
}
else
{
    Console.WriteLine("All configured layers validated successfully");
}
```

## Best Practices

### **Configuration**
- **Limit Search Fields**: Only include frequently searched fields
- **Strategic Layer Enabling**: Only enable layers users actually search
- **Field Selection**: Use indexed fields when possible for better performance
- **Display Field**: Choose user-friendly fields for results display

### **Performance**
- **Cache Management**: Let the system handle cache lifecycle automatically
- **Query Limits**: Use reasonable maxResults to prevent memory issues  
- **Network Optimization**: Ensure stable connectivity for geocoding services
- **Resource Cleanup**: Services automatically handle resource disposal

### **User Experience**
- **Mode Indication**: Clear visual feedback about current search mode
- **Progress Feedback**: Show loading states during search operations
- **Error Handling**: Provide meaningful error messages to users
- **Keyboard Support**: Ensure full keyboard navigation works consistently

The Enhanced Dual-Mode Search System provides enterprise-grade search performance while maintaining an intuitive user interface. The combination of startup replica caching and lazy loading ensures both instant results for common searches and comprehensive coverage for all data.
# Service Layer Documentation

## Overview

The Service Layer provides a clean abstraction over infrastructure concerns, implementing dependency injection patterns for maintainable and testable code. This layer encapsulates configuration management, map operations, network connectivity, and progress reporting.

## Service Interfaces

### **IConfigurationService**
Manages application configuration with thread-safe operations.

```csharp
public interface IConfigurationService
{
    Config Configuration { get; }
    Task LoadConfigurationAsync();
    Task SaveConfigurationAsync(Config config);
    void UpdateConfiguration(Config config);
}
```

**Responsibilities:**
- Load configuration from config.json at startup
- Provide thread-safe access to configuration data
- Save configuration changes to persistent storage
- Handle default configuration creation

**Usage Example:**
```csharp
// Injected into MainWindow constructor
private readonly IConfigurationService _configurationService;

// Access configuration
var mapId = _configurationService.Configuration?.mapId;

// Update configuration
var config = _configurationService.Configuration;
config.selectedLayer = "newLayer";
_configurationService.UpdateConfiguration(config);
await _configurationService.SaveConfigurationAsync(config);
```

### **IMapService**
Handles map loading, initialization, and spatial operations.

```csharp
public interface IMapService
{
    Task<Map> LoadMapAsync(string mapId, IProgress<string>? progress = null);
    Task InitializeMapViewAsync(MapView mapView, IProgress<string>? progress = null);
    Task<bool> TryGeocodeAndZoomAsync(MapView mapView, string text, double scale = 5000);
}
```

**Responsibilities:**
- Load maps from WebMap IDs or MMPK files
- Handle online/offline scenarios automatically
- Manage spatial reference and reprojection
- Initialize MapView with proper extent and settings
- Provide geocoding capabilities

**Implementation Details:**
```csharp
public async Task<Map> LoadMapAsync(string mapId, IProgress<string>? progress = null)
{
    // 1. Determine if MMPK or WebMap
    bool isMmpk = File.Exists(mapId);
    bool online = await _networkService.IsArcGisOnlineReachableAsync();
    
    // 2. Load appropriate map type
    Map map = isMmpk ? await LoadMmpkMap(mapId) : await LoadWebMap(mapId);
    
    // 3. Handle reprojection if needed
    if (map.SpatialReference?.Wkid != 102100 && online)
    {
        map = await ReprojectMapAsync(map);
    }
    
    // 4. Load operational layers
    await LoadOperationalLayersAsync(map, progress);
    
    return map;
}
```

### **INetworkService**
Manages network connectivity and API key configuration.

```csharp
public interface INetworkService
{
    Task<bool> IsArcGisOnlineReachableAsync(CancellationToken cancellationToken = default);
    Task<bool> EnsureOnlineApiKeyIfAvailableAsync(CancellationToken cancellationToken = default);
}
```

**Responsibilities:**
- Check ArcGIS Online connectivity with timeouts
- Validate and set API keys for online services
- Handle network failures gracefully
- Provide fast failure detection (3-second timeout)

**Performance Features:**
- **Fast Timeout**: 3-second timeout for quick failure detection
- **Head Requests**: Uses HTTP HEAD to minimize bandwidth
- **User Agent**: Identifies as "POSM-MapReader/3.3.0"
- **Cancellation Support**: Proper cancellation token handling

### **IProgressReporter**
Provides unified progress reporting across the application.

```csharp
public interface IProgressReporter
{
    event EventHandler<ProgressEventArgs> ProgressChanged;
    void Report(string message, double? percentage = null);
    void ReportComplete(string? message = null);
    void ReportError(string message);
}
```

**Event Model:**
```csharp
public class ProgressEventArgs : EventArgs
{
    public string Message { get; set; }
    public double? Percentage { get; set; }
    public bool IsComplete { get; set; }
    public bool IsError { get; set; }
}
```

**Usage Patterns:**
```csharp
// Subscribe to progress updates
_progressReporter.ProgressChanged += OnProgressChanged;

// Report progress during operations
_progressReporter.Report("Loading map layers...", 45.0);
_progressReporter.ReportComplete("Map loaded successfully");
_progressReporter.ReportError("Failed to connect to service");

// Handle progress in UI
private void OnProgressChanged(object? sender, ProgressEventArgs e)
{
    Dispatcher.BeginInvoke(() =>
    {
        if (e.IsError)
        {
            ShowErrorMessage(e.Message);
        }
        else if (e.IsComplete)
        {
            HideLoadingOverlay();
        }
        else
        {
            UpdateProgressBar(e.Percentage);
            UpdateStatusText(e.Message);
        }
    });
}
```

## Dependency Injection Setup

### **Service Registration**
Services are registered in `App.ConfigureServices()`:

```csharp
private void ConfigureServices()
{
    var services = new ServiceCollection();

    // Core infrastructure
    services.AddLogging(builder =>
    {
        builder.AddConsole();
        builder.SetMinimumLevel(LogLevel.Information);
    });
    services.AddHttpClient();

    // Application services with appropriate lifetimes
    services.AddSingleton<IConfigurationService>(provider =>
    {
        var logger = provider.GetRequiredService<ILogger<ConfigurationService>>();
        var configService = new ConfigurationService(logger);
        configService.UpdateConfiguration(Configuration); // Pre-loaded config
        return configService;
    });

    services.AddSingleton<INetworkService, NetworkService>();
    services.AddSingleton<IMapService, MapService>();
    services.AddTransient<IProgressReporter, ProgressReporter>();

    // UI components
    services.AddTransient<MainWindow>();

    ServiceProvider = services.BuildServiceProvider();
}
```

### **Service Lifetimes**

#### **Singleton Services**
- **IConfigurationService**: Shared configuration state
- **INetworkService**: Reusable HTTP client and network state
- **IMapService**: Expensive initialization, shared map operations

#### **Transient Services**
- **IProgressReporter**: Per-operation progress tracking
- **MainWindow**: Each window instance needs independent state

### **Constructor Injection**
Services are injected into consuming classes:

```csharp
public MainWindow(
    IConfigurationService configurationService,
    IMapService mapService,
    INetworkService networkService,
    IProgressReporter progressReporter,
    ILogger<MainWindow> logger)
{
    _configurationService = configurationService;
    _mapService = mapService;
    _networkService = networkService;
    _progressReporter = progressReporter;
    _logger = logger;

    InitializeComponent();
    InitializeMapAsync();
}
```

## Service Implementation Details

### **ConfigurationService Implementation**

```csharp
public class ConfigurationService : IConfigurationService
{
    private readonly ILogger<ConfigurationService> _logger;
    private Config _configuration;
    private const string ConfigFileName = "config.json";

    public Config Configuration => _configuration ?? new Config();

    public async Task LoadConfigurationAsync()
    {
        try
        {
            if (File.Exists(ConfigFileName))
            {
                _logger.LogInformation("Loading configuration from {ConfigFile}", ConfigFileName);
                var json = File.ReadAllText(ConfigFileName);
                _configuration = JsonConvert.DeserializeObject<Config>(json) ?? new Config();
            }
            else
            {
                _configuration = CreateDefaultConfiguration();
                await SaveConfigurationAsync(_configuration);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading configuration");
            _configuration = new Config();
        }
    }
}
```

**Key Features:**
- **Null Safety**: Always returns non-null configuration
- **Default Creation**: Creates default config.json if missing
- **Error Recovery**: Falls back to empty config on errors
- **Structured Logging**: Comprehensive logging with context

### **NetworkService Implementation**

```csharp
public class NetworkService : INetworkService
{
    private readonly HttpClient _httpClient;
    private readonly IConfigurationService _configurationService;
    private readonly ILogger<NetworkService> _logger;

    public async Task<bool> IsArcGisOnlineReachableAsync(CancellationToken cancellationToken = default)
    {
        if (!NetworkInterface.GetIsNetworkAvailable())
            return false;

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(3));
            
            using var request = new HttpRequestMessage(HttpMethod.Head, 
                "https://www.arcgis.com/sharing/rest?f=pjson");
            
            using var response = await _httpClient.SendAsync(request, 
                HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);
            
            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Network connectivity check timed out");
            return false;
        }
    }
}
```

**Performance Optimizations:**
- **Network Interface Check**: Quick local check before HTTP request
- **HEAD Requests**: Minimal bandwidth usage
- **Response Headers Only**: Doesn't download response body
- **Linked Cancellation**: Respects both method and operation cancellation

## Error Handling Patterns

### **Graceful Degradation**
Services are designed to fail gracefully:

```csharp
// Network service: offline mode when connectivity fails
public async Task<bool> EnsureOnlineApiKeyIfAvailableAsync()
{
    var key = _configurationService.Configuration?.apiKey;
    if (string.IsNullOrWhiteSpace(key))
    {
        _logger.LogDebug("No API key configured");
        return false; // Continue in offline mode
    }

    if (await IsArcGisOnlineReachableAsync())
    {
        ArcGISRuntimeEnvironment.ApiKey = key;
        return true;
    }

    _logger.LogDebug("Cannot set API key - ArcGIS Online not reachable");
    return false; // Continue without online services
}
```

### **Exception Handling Strategy**
1. **Log All Exceptions**: Comprehensive logging with context
2. **User-Friendly Messages**: Don't expose technical details to users
3. **Fallback Behavior**: Provide reasonable defaults when possible
4. **Resource Cleanup**: Proper disposal and cancellation

### **Retry Patterns**
Network operations include retry logic:

```csharp
private async Task<T> ExecuteWithRetry<T>(Func<Task<T>> operation, int maxRetries = 3)
{
    for (int attempt = 1; attempt <= maxRetries; attempt++)
    {
        try
        {
            return await operation();
        }
        catch (HttpRequestException ex) when (attempt < maxRetries)
        {
            _logger.LogWarning("Attempt {Attempt} failed: {Error}", attempt, ex.Message);
            await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt))); // Exponential backoff
        }
    }
    
    // Final attempt without catching exceptions
    return await operation();
}
```

## Testing Considerations

### **Service Mocking**
Interfaces enable easy unit testing:

```csharp
[Test]
public async Task LoadMapAsync_WithValidWebMapId_ReturnsMap()
{
    // Arrange
    var mockConfig = new Mock<IConfigurationService>();
    var mockNetwork = new Mock<INetworkService>();
    var mockLogger = new Mock<ILogger<MapService>>();
    
    mockNetwork.Setup(x => x.IsArcGisOnlineReachableAsync(It.IsAny<CancellationToken>()))
             .ReturnsAsync(true);
    
    var mapService = new MapService(mockConfig.Object, mockNetwork.Object, mockLogger.Object);
    
    // Act
    var map = await mapService.LoadMapAsync("valid-webmap-id");
    
    // Assert
    Assert.IsNotNull(map);
    mockNetwork.Verify(x => x.IsArcGisOnlineReachableAsync(It.IsAny<CancellationToken>()), Times.Once);
}
```

### **Integration Testing**
Services can be tested with real dependencies:

```csharp
[Test]
public async Task ConfigurationService_LoadSave_RoundTrip()
{
    // Arrange
    var logger = new Mock<ILogger<ConfigurationService>>();
    var configService = new ConfigurationService(logger.Object);
    var testConfig = new Config { mapId = "test-map-id" };
    
    // Act
    await configService.SaveConfigurationAsync(testConfig);
    await configService.LoadConfigurationAsync();
    
    // Assert
    Assert.AreEqual("test-map-id", configService.Configuration.mapId);
}
```

## Performance Monitoring

### **Logging Integration**
Services provide comprehensive logging:

```csharp
_logger.LogInformation("Loading map from {Source}: {MapId}", 
    isMmpk ? "MMPK" : "WebMap", mapId);

_logger.LogDebug("Layer '{LayerName}' loaded in {ElapsedMs}ms", 
    layer.Name, stopwatch.ElapsedMilliseconds);

_logger.LogWarning("Failed to load layer '{LayerName}': {Error}", 
    layer.Name, ex.Message);
```

### **Performance Metrics**
Key metrics to monitor:
- **Map Load Time**: Total time to load and initialize map
- **Network Response Time**: ArcGIS Online connectivity checks
- **Configuration Load Time**: Config.json parsing and validation
- **Service Resolution Time**: Dependency injection performance

### **Memory Management**
Services implement proper resource management:
- **HttpClient**: Shared instance with proper disposal
- **Cancellation Tokens**: Prevent resource leaks from abandoned operations
- **Event Handlers**: Proper subscription/unsubscription patterns
- **Configuration Caching**: In-memory configuration with file system synchronization
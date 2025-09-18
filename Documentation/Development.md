# Development Guide

## Development Environment Setup

### **Prerequisites**
- **Visual Studio 2022** or **Visual Studio Code** with C# extension
- **.NET 8.0 SDK** (Windows target framework)
- **Git** for version control
- **ArcGIS Runtime SDK** 200.7.0 (installed via NuGet)

### **Getting Started**
```bash
# Clone the repository
git clone <repository-url>
cd "MaReader 3.3.0 - MapSync"

# Restore dependencies
dotnet restore POSM_MR3_2.csproj

# Build the project
dotnet build POSM_MR3_2.csproj

# Run in debug mode
dotnet run POSM_MR3_2.csproj --configuration Debug
```

### **Required Configuration**
1. **Create config.json** in the project root:
```json
{
  "runtimeLicenseString": "your-runtime-license",
  "apiKey": "your-arcgis-api-key",
  "mapId": "your-webmap-id-or-mmpk-path",
  "idField": "AssetID",
  "queryLayers": [
    {
      "layerName": "YourLayerName",
      "searchFields": ["ID", "Name"],
      "displayField": "ID",
      "enabled": true
    }
  ]
}
```

2. **Install POSM** (optional for full functionality):
   - Place POSM.exe at `C:\POSM\POSM.exe` or configure path in config.json

## Architecture Patterns

### **Dependency Injection**
The application uses Microsoft.Extensions.DependencyInjection:

```csharp
// Service registration in App.xaml.cs
services.AddSingleton<IConfigurationService, ConfigurationService>();
services.AddSingleton<IMapService, MapService>();
services.AddTransient<IProgressReporter, ProgressReporter>();

// Constructor injection in MainWindow
public MainWindow(
    IConfigurationService configurationService,
    IMapService mapService,
    INetworkService networkService,
    IProgressReporter progressReporter,
    ILogger<MainWindow> logger)
```

### **Service Layer Pattern**
- **Interfaces**: Define contracts in `Services/I*.cs`
- **Implementations**: Concrete classes in `Services/*.cs`
- **Lifetime Management**: Singleton for shared state, Transient for per-operation

### **Async/Await Patterns**
All I/O operations use proper async patterns:

```csharp
// ✅ Good: Proper async implementation
public async Task<Map> LoadMapAsync(string mapId, IProgress<string>? progress = null)
{
    progress?.Report("Loading map...");
    var map = await MapService.LoadAsync(mapId);
    progress?.Report("Map loaded successfully");
    return map;
}

// ❌ Avoid: Blocking async calls
public Map LoadMap(string mapId)
{
    return LoadMapAsync(mapId).Result; // Deadlock risk
}
```

### **Error Handling Strategy**
```csharp
// Service-level error handling
public async Task<T> ExecuteWithLogging<T>(Func<Task<T>> operation, string operationName)
{
    try
    {
        _logger.LogInformation("Starting {Operation}", operationName);
        var result = await operation();
        _logger.LogInformation("Completed {Operation}", operationName);
        return result;
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Failed {Operation}: {Error}", operationName, ex.Message);
        throw; // Re-throw for UI handling
    }
}

// UI-level error handling
private async void OnLoadMapClicked(object sender, RoutedEventArgs e)
{
    try
    {
        await _mapService.LoadMapAsync(mapId);
    }
    catch (Exception ex)
    {
        MessageBox.Show($"Failed to load map: {ex.Message}", "Error", 
            MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
```

## Coding Standards

### **Naming Conventions**
- **Classes**: PascalCase (`ConfigurationService`)
- **Interfaces**: IPascalCase (`IConfigurationService`)
- **Methods**: PascalCase (`LoadMapAsync`)
- **Fields**: _camelCase (`_configurationService`)
- **Properties**: PascalCase (`Configuration`)
- **Constants**: PascalCase (`ConfigFileName`)

### **File Organization**
```
Project Root/
├── Services/           # Service layer
│   ├── IConfigurationService.cs
│   ├── ConfigurationService.cs
│   └── ...
├── Documentation/      # Project documentation
├── Assets/            # Images and resources
├── MainWindow.xaml    # Main UI
├── App.xaml.cs        # Application bootstrap
└── config.json        # Configuration file
```

### **Code Comments**
- **XML Documentation**: Public interfaces and methods
- **Inline Comments**: Complex logic and business rules
- **TODO Comments**: Temporary notes with context

```csharp
/// <summary>
/// Loads a map from either a WebMap ID or MMPK file path.
/// </summary>
/// <param name="mapId">WebMap ID or path to MMPK file</param>
/// <param name="progress">Optional progress reporting</param>
/// <returns>Loaded and initialized map</returns>
public async Task<Map> LoadMapAsync(string mapId, IProgress<string>? progress = null)
{
    // Check if mapId is a file path (MMPK) or WebMap ID
    bool isMmpk = File.Exists(mapId);
    
    // TODO: Add support for TPK files in future version
    if (isMmpk)
    {
        return await LoadMmpkMapAsync(mapId, progress);
    }
    else
    {
        return await LoadWebMapAsync(mapId, progress);
    }
}
```

## Adding New Features

### **1. Creating a New Service**

**Step 1: Define Interface**
```csharp
// Services/IMyNewService.cs
public interface IMyNewService
{
    Task<MyResult> DoSomethingAsync(string parameter);
    event EventHandler<MyEventArgs> SomethingHappened;
}
```

**Step 2: Implement Service**
```csharp
// Services/MyNewService.cs
public class MyNewService : IMyNewService
{
    private readonly ILogger<MyNewService> _logger;
    
    public MyNewService(ILogger<MyNewService> logger)
    {
        _logger = logger;
    }
    
    public async Task<MyResult> DoSomethingAsync(string parameter)
    {
        _logger.LogInformation("Processing {Parameter}", parameter);
        // Implementation here
        return new MyResult();
    }
    
    public event EventHandler<MyEventArgs>? SomethingHappened;
}
```

**Step 3: Register Service**
```csharp
// App.xaml.cs - ConfigureServices method
services.AddSingleton<IMyNewService, MyNewService>();
```

**Step 4: Inject into Consumer**
```csharp
// MainWindow.xaml.cs or other consumer
public MainWindow(
    IMyNewService myNewService, // Add new parameter
    // ... other existing services
)
{
    _myNewService = myNewService;
    // ... existing initialization
}
```

### **2. Adding Configuration Properties**

**Step 1: Update Config Class**
```csharp
// Config.cs (or wherever Config is defined)
public class Config
{
    // Existing properties...
    
    public string MyNewProperty { get; set; } = "default-value";
    public bool MyNewFeatureEnabled { get; set; } = false;
}
```

**Step 2: Update Default Configuration**
```csharp
// ConfigurationService.cs - CreateDefaultConfiguration method
private Config CreateDefaultConfiguration()
{
    return new Config
    {
        // Existing defaults...
        MyNewProperty = "production-default",
        MyNewFeatureEnabled = true
    };
}
```

**Step 3: Use in Services**
```csharp
// Access configuration in any service
var myValue = _configurationService.Configuration?.MyNewProperty ?? "fallback";

if (_configurationService.Configuration?.MyNewFeatureEnabled == true)
{
    // Execute new feature
}
```

### **3. Adding UI Components**

**Step 1: Create Window/Control**
```xaml
<!-- MyNewWindow.xaml -->
<Window x:Class="WpfMapApp1.MyNewWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        Title="My New Window" Height="300" Width="400">
    <Grid>
        <!-- UI content -->
    </Grid>
</Window>
```

**Step 2: Code-behind with DI**
```csharp
// MyNewWindow.xaml.cs
public partial class MyNewWindow : Window
{
    private readonly IMyNewService _myNewService;
    
    public MyNewWindow(IMyNewService myNewService)
    {
        _myNewService = myNewService;
        InitializeComponent();
    }
}
```

**Step 3: Register in DI Container**
```csharp
// App.xaml.cs - ConfigureServices method
services.AddTransient<MyNewWindow>();
```

**Step 4: Show Window via DI**
```csharp
// From MainWindow or other component
private void ShowMyNewWindow()
{
    var window = App.ServiceProvider.GetRequiredService<MyNewWindow>();
    window.Show();
}
```

## Performance Optimization

### **Search System Optimization**
- **Debouncing**: 300ms delay for user input
- **Caching**: LRU cache with 5-minute TTL
- **Query Limits**: MaxFeatures=3, ReturnGeometry=false
- **Cancellation**: Cancel previous searches when new ones start

### **Memory Management**
```csharp
// ✅ Good: Proper disposal patterns
public async Task<List<SearchResult>> SearchAsync(string query, CancellationToken cancellationToken)
{
    using var httpClient = new HttpClient();
    using var response = await httpClient.GetAsync(url, cancellationToken);
    // Automatic disposal
}

// ✅ Good: Cancellation token usage
public async Task LongRunningOperationAsync(CancellationToken cancellationToken)
{
    for (int i = 0; i < 1000; i++)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await ProcessItemAsync(i);
    }
}
```

### **UI Responsiveness**
```csharp
// ✅ Good: Async UI operations
private async void OnButtonClick(object sender, RoutedEventArgs e)
{
    loadingSpinner.Visibility = Visibility.Visible;
    try
    {
        await LongRunningOperationAsync();
    }
    finally
    {
        loadingSpinner.Visibility = Visibility.Collapsed;
    }
}

// ✅ Good: Background thread for heavy work
private async Task ProcessLargeDatasetAsync()
{
    var result = await Task.Run(() =>
    {
        // CPU-intensive work on background thread
        return ProcessData();
    });
    
    // Update UI on main thread
    Dispatcher.BeginInvoke(() =>
    {
        UpdateUI(result);
    });
}
```

## Testing Guidelines

### **Unit Testing Services**
```csharp
[TestClass]
public class ConfigurationServiceTests
{
    private Mock<ILogger<ConfigurationService>> _mockLogger;
    private ConfigurationService _configService;
    
    [TestInitialize]
    public void Setup()
    {
        _mockLogger = new Mock<ILogger<ConfigurationService>>();
        _configService = new ConfigurationService(_mockLogger.Object);
    }
    
    [TestMethod]
    public async Task LoadConfigurationAsync_FileExists_LoadsSuccessfully()
    {
        // Arrange
        var testConfig = new Config { mapId = "test-map" };
        await _configService.SaveConfigurationAsync(testConfig);
        
        // Act
        await _configService.LoadConfigurationAsync();
        
        // Assert
        Assert.AreEqual("test-map", _configService.Configuration.mapId);
    }
}
```

### **Integration Testing**
```csharp
[TestClass]
public class MapServiceIntegrationTests
{
    private IServiceProvider _serviceProvider;
    
    [TestInitialize]
    public void Setup()
    {
        var services = new ServiceCollection();
        // Configure services for testing
        services.AddSingleton<IConfigurationService, ConfigurationService>();
        services.AddSingleton<INetworkService, NetworkService>();
        services.AddSingleton<IMapService, MapService>();
        services.AddLogging();
        services.AddHttpClient();
        
        _serviceProvider = services.BuildServiceProvider();
    }
    
    [TestMethod]
    public async Task LoadMapAsync_ValidWebMapId_ReturnsLoadedMap()
    {
        // Arrange
        var mapService = _serviceProvider.GetRequiredService<IMapService>();
        
        // Act
        var map = await mapService.LoadMapAsync("valid-webmap-id");
        
        // Assert
        Assert.IsNotNull(map);
        Assert.IsTrue(map.OperationalLayers.Count > 0);
    }
}
```

## Debugging Techniques

### **Logging Configuration**
```csharp
// Enable detailed logging in debug builds
services.AddLogging(builder =>
{
#if DEBUG
    builder.SetMinimumLevel(LogLevel.Debug);
    builder.AddConsole();
    builder.AddDebug();
#else
    builder.SetMinimumLevel(LogLevel.Information);
    builder.AddConsole();
#endif
});
```

### **Performance Profiling**
```csharp
// Measure operation performance
public async Task<T> MeasureAsync<T>(Func<Task<T>> operation, string operationName)
{
    var stopwatch = Stopwatch.StartNew();
    try
    {
        var result = await operation();
        _logger.LogInformation("{Operation} completed in {ElapsedMs}ms", 
            operationName, stopwatch.ElapsedMilliseconds);
        return result;
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "{Operation} failed after {ElapsedMs}ms", 
            operationName, stopwatch.ElapsedMilliseconds);
        throw;
    }
}
```

### **Common Debug Patterns**
```csharp
// Service method tracing
public async Task<Map> LoadMapAsync(string mapId, IProgress<string>? progress = null)
{
    _logger.LogDebug("LoadMapAsync called with mapId: {MapId}", mapId);
    
    try
    {
        var result = await InternalLoadMapAsync(mapId, progress);
        _logger.LogDebug("LoadMapAsync completed successfully");
        return result;
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "LoadMapAsync failed");
        throw;
    }
}

// Configuration validation
private void ValidateConfiguration()
{
    var config = _configurationService.Configuration;
    
    if (string.IsNullOrEmpty(config?.mapId))
    {
        _logger.LogWarning("No mapId configured");
    }
    
    if (string.IsNullOrEmpty(config?.apiKey))
    {
        _logger.LogWarning("No API key configured - online features may not work");
    }
}
```

## Deployment

### **Build Configuration**
```bash
# Debug build (development)
dotnet build POSM_MR3_2.csproj --configuration Debug

# Release build (production)
dotnet build POSM_MR3_2.csproj --configuration Release

# Publish for deployment
dotnet publish POSM_MR3_2.csproj --configuration Release --runtime win-x64 --self-contained true
```

### **Configuration Management**
- **Development**: Use development config.json with test data
- **Staging**: Use staging config.json with staging services
- **Production**: Use production config.json with production licenses and API keys

### **Runtime Dependencies**
- **ArcGIS Runtime**: Automatically copied to output directory
- **.NET 8.0 Runtime**: Self-contained deployment includes runtime
- **Visual C++ Redistributable**: Required for ArcGIS Runtime native components

## Troubleshooting

### **Common Development Issues**

#### **Dependency Injection Errors**
```
Error: No default constructor found for type 'MainWindow'
Solution: Ensure MainWindow is registered in DI container and App.xaml doesn't have StartupUri
```

#### **Configuration Not Loading**
```
Error: Configuration values are null/empty
Solution: Check config.json exists and has valid JSON syntax
```

#### **ArcGIS Runtime License Issues**
```
Error: "Developer Use Only" watermark appears
Solution: Verify runtimeLicenseString in config.json is valid
```

#### **API Key Problems**
```
Error: Geocoding/online services fail
Solution: Check apiKey in config.json and verify network connectivity
```

### **Performance Issues**
- **Slow Search**: Check queryLayers configuration, reduce searchFields
- **Memory Leaks**: Verify proper disposal of HttpClient, CancellationTokens
- **UI Freezing**: Ensure all I/O operations use async/await patterns
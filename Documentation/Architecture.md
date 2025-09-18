# Architecture Overview

## System Design

POSM Map Reader 3.3.0 follows a layered architecture with dependency injection, providing clear separation of concerns and maintainable code structure.

```
┌─────────────────────────────────────────┐
│              Presentation Layer          │
│  MainWindow, ConfigWindow, LayersWindow │
└─────────────────┬───────────────────────┘
                  │
┌─────────────────▼───────────────────────┐
│               Service Layer              │
│  IMapService, IConfigurationService,    │
│  INetworkService, IProgressReporter     │
└─────────────────┬───────────────────────┘
                  │
┌─────────────────▼───────────────────────┐
│            Infrastructure Layer         │
│  ArcGIS Runtime, File System,           │
│  Network, Database (POSM)               │
└─────────────────────────────────────────┘
```

## Core Components

### 1. **Application Bootstrap (App.xaml.cs)**
- **Dependency Injection Setup**: Configures Microsoft.Extensions.DependencyInjection
- **Service Registration**: Registers all services with appropriate lifetimes
- **ArcGIS Runtime Initialization**: License validation and environment setup
- **Configuration Loading**: Loads config.json and creates default if missing

```csharp
private void ConfigureServices()
{
    var services = new ServiceCollection();
    
    // Logging
    services.AddLogging(builder => 
    {
        builder.AddConsole();
        builder.SetMinimumLevel(LogLevel.Information);
    });
    
    // HttpClient for network operations
    services.AddHttpClient();
    
    // Application services
    services.AddSingleton<IConfigurationService, ConfigurationService>();
    services.AddSingleton<INetworkService, NetworkService>();
    services.AddSingleton<IMapService, MapService>();
    services.AddTransient<IProgressReporter, ProgressReporter>();
    
    // UI Components
    services.AddTransient<MainWindow>();
    
    ServiceProvider = services.BuildServiceProvider();
}
```

### 2. **Service Layer Architecture**

#### **IConfigurationService**
- **Purpose**: Centralized configuration management
- **Responsibilities**: Load, save, and update application configuration
- **Implementation**: Thread-safe configuration access with validation

#### **IMapService**
- **Purpose**: Map loading and initialization
- **Responsibilities**: 
  - Load maps from WebMap ID or MMPK files
  - Handle map reprojection and spatial reference management
  - Initialize MapView with proper extent and settings
  - Integrate with online/offline scenarios

#### **INetworkService**
- **Purpose**: Network connectivity and API key management
- **Responsibilities**:
  - Check ArcGIS Online reachability
  - Manage API key configuration
  - Handle network timeouts and retries

#### **IProgressReporter**
- **Purpose**: Unified progress reporting
- **Responsibilities**:
  - Event-driven progress updates
  - Error state management
  - UI-agnostic progress notifications

### 3. **Dependency Injection Patterns**

#### **Service Lifetimes**
- **Singleton**: Configuration, Network, Map services (shared state)
- **Transient**: Progress reporters, UI windows (per-request instances)
- **Scoped**: Not used (WPF doesn't have natural scopes)

#### **Constructor Injection**
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

## Data Flow

### 1. **Application Startup**
```
App.Application_Startup()
├── LoadConfiguration()
├── ConfigureServices()
├── ArcGIS Runtime License Setup
├── Create MainWindow via DI
└── Show MainWindow
```

### 2. **Map Initialization**
```
MainWindow.InitializeMapAsync()
├── MapService.LoadMapAsync()
│   ├── Check if MMPK or WebMap
│   ├── Handle online/offline scenarios
│   ├── Load operational layers
│   └── Apply reprojection if needed
├── MapService.InitializeMapViewAsync()
│   ├── Calculate combined extent
│   ├── Set initial viewpoint
│   └── Initialize geocoding service
└── Initialize search and UI components
```

### 3. **Search System Flow**
```
User Input (Search TextBox)
├── Debounce Timer (300ms)
├── Query Cancellation (previous searches)
├── Cache Check (LRU cache)
├── If not cached:
│   ├── Layer Search (Asset mode)
│   └── Geocoding Search (Address mode)
├── Update Suggestions Dropdown
└── Cache Results
```

## Performance Considerations

### **Memory Management**
- **Service Singletons**: Shared instances reduce memory overhead
- **LRU Cache**: Automatic eviction prevents memory leaks
- **Query Cancellation**: Prevents resource buildup from abandoned operations

### **Threading**
- **UI Thread**: All UI updates via Dispatcher.BeginInvoke
- **Background Threading**: Search operations and map loading
- **Thread-Safe Collections**: Used for caches and shared state

### **Network Optimization**
- **Connection Pooling**: HttpClient with proper disposal
- **Timeout Management**: Configurable timeouts for network operations
- **Retry Logic**: Built into network service for resilience

## Extension Points

### **Adding New Services**
1. Create interface in `Services/` folder
2. Implement concrete class
3. Register in `App.ConfigureServices()`
4. Inject into consuming components

### **Custom Progress Reporting**
Implement `IProgressReporter` for custom progress handling:
```csharp
public class CustomProgressReporter : IProgressReporter
{
    public event EventHandler<ProgressEventArgs> ProgressChanged;
    
    public void Report(string message, double? percentage = null)
    {
        // Custom progress handling
        ProgressChanged?.Invoke(this, new ProgressEventArgs(message, percentage));
    }
}
```

### **Configuration Extensions**
Add new configuration properties to `Config` class and update `IConfigurationService` as needed. The service handles serialization/deserialization automatically.

## Security Considerations

### **API Key Management**
- API keys stored in config.json (not in source code)
- Network service validates keys before use
- Keys are set only when online connectivity is confirmed

### **Input Validation**
- All user inputs are validated before processing
- SQL injection protection via parameterized queries
- File path validation for MMPK and configuration files

### **Error Handling**
- Comprehensive try-catch blocks with logging
- Graceful degradation for network failures
- User-friendly error messages without exposing system details
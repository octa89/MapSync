# Map Reader 3.3.0 - MapSync

> 📚 **Complete Documentation**: For detailed technical information, see the [Documentation](Documentation/) folder

## Overview

POSM Map Reader 3.3.0 - MapSync is a professional WPF desktop application built on ArcGIS Runtime to manage pipe assets in GIS, launch POSM inspections, and georeference POSM media observations as point features. This enterprise-grade tool features advanced search capabilities, professional service architecture, and seamless POSM integration using a single config.json for all runtime parameters.

The application combines the enhanced search system of MR3 3.3.0 with the robust service layer architecture from MR3 3.1.0, providing a maintainable, testable, and scalable solution for GIS-based asset management and inspection workflows.

## 📖 Quick Navigation

| Topic | Description | Link |
|-------|-------------|------|
| 🏗️ **System Architecture** | Service layer design and dependency injection | [Architecture.md](Documentation/Architecture.md) |
| ⚙️ **Configuration** | Complete config.json reference and setup | [Configuration.md](Documentation/Configuration.md) |
| 🔍 **Search System** | Advanced search implementation and performance | [SearchSystem.md](Documentation/SearchSystem.md) |
| 🛠️ **Development** | Best practices and coding guidelines | [Development.md](Documentation/Development.md) |
| 🎯 **Service Layer** | Dependency injection patterns and services | [ServiceLayer.md](Documentation/ServiceLayer.md) |
| 🗺️ **Offline Maps** | Offline basemap generation guide | [OfflineBasemapGuide.md](Documentation/OfflineBasemapGuide.md) |

## Key Features

### 🔍 **Advanced Search System** → [📖 Details](Documentation/SearchSystem.md)
- **Dual-Mode Search**: Toggle between Address geocoding and Asset field searching
- **Real-Time Autocomplete**: Debounced input (300ms) with intelligent caching
- **Performance Optimized**: LRU cache (50 entries, 5-minute TTL) with query cancellation
- **Local Context**: USA-constrained geocoding with map extent filtering
- **Keyboard Navigation**: Full arrow key and Enter support for suggestions

### 🎬 **POSM Video Integration**
- **Seamless Launch**: Launch POSM with selected Asset ID and inspection type
- **Video Popup Buttons**: Direct access to inspection videos from map popups
- **Media Path Management**: Automatic construction of video/image file paths
- **Database Integration**: Query POSM.mdb for latest inspection data

### 🏗️ **Enterprise Service Architecture** → [📖 Details](Documentation/ServiceLayer.md)
- **Dependency Injection**: Microsoft.Extensions.DependencyInjection container
- **Service Abstractions**: IConfigurationService, IMapService, INetworkService, IVideoService
- **Structured Logging**: Categorized console output with ILogger<T> pattern
- **Progress Reporting**: Unified progress updates across all operations

### 🗺️ **Enhanced Map Management** → [📖 Offline Guide](Documentation/OfflineBasemapGuide.md)
- **Multi-Source Support**: Web Maps, MMPK files, and offline basemaps
- **Layer Visibility Controls**: Advanced layer tree with persistent settings
- **Offline Map Generation**: Complete layer download with intelligent output naming
- **Basemap Gallery**: Switch between different basemap styles

### ⚙️ **Configuration-Driven Design** → [📖 Config Reference](Documentation/Configuration.md)
- **Single Config File**: All runtime parameters in config.json
- **Layer Search Configuration**: Configurable searchFields and displayFields per layer
- **Dynamic Field Validation**: Startup validation with comprehensive logging
- **Environment Flexibility**: Easy switching between development and production settings

## Quick Start

### Prerequisites
- **.NET 8.0** Windows Runtime
- **ArcGIS Runtime SDK 200.7.0** for .NET
- **Valid ArcGIS API Key** and Runtime License String
- **POSM Software** (for inspection integration)

### Dependencies
- **Microsoft.Extensions.DependencyInjection 9.0.1** (IoC container)
- **Microsoft.Extensions.Hosting 9.0.1** (Service lifetime management)
- **Microsoft.Extensions.Logging 9.0.1** (Structured logging)
- **Newtonsoft.Json 13.0.3** (Configuration and JSON handling)

### Installation & Configuration
1. **Clone the repository**
2. **Configure settings**: Copy and modify `config.json` with your:
   - ArcGIS API key and runtime license
   - Map ID (web map or MMPK file path)
   - POSM executable path
   - Layer search configuration
3. **Build and run**:
   ```bash
   dotnet build POSM_MR3_2.csproj
   dotnet run POSM_MR3_2.csproj
   ```

## Core Workflows

### 1. **Advanced Search Operations**
- **Toggle Search Mode**: Click 🗺️ (Address) or 🏢 (Asset) to switch search types
- **Real-Time Search**: Type in the search box for instant autocomplete suggestions
- **Navigation**: Use ↑/↓ arrow keys to navigate suggestions, Enter to select
- **Performance**: Automatic debouncing and caching for optimal performance

### 2. **Map Interaction & Feature Selection**
- **Feature Identification**: Click on map features to view detailed popups
- **Asset Information**: Automatic extraction of Asset IDs and attribute data
- **Draggable Popups**: Reposition popups for better map visibility
- **Layer Management**: Use layer visibility controls to toggle data layers

### 3. **POSM Integration Workflow**
- **Feature Selection**: Select a pipe/asset feature on the map
- **Inspection Launch**: Click "Open Latest Inspection" in the popup
- **Automatic Linkage**: Application queries POSM database for latest session
- **Media Access**: Direct video/image access via constructed media paths

### 4. **Offline Map Generation**
- **Source Detection**: Automatically detects current map source (online/offline)
- **Complete Layer Download**: Forces visibility and removes scale restrictions
- **Intelligent Naming**: Output paths include source information
- **Progress Monitoring**: Real-time progress updates during generation

### 5. **Configuration Management**
- **Runtime Configuration**: All settings managed via config.json
- **Layer Search Setup**: Configure searchFields and displayFields per layer
- **Validation**: Automatic validation at startup with comprehensive logging
- **Dynamic Updates**: Modify settings without rebuilding application

## Architecture Overview

### **Service Layer (Enterprise Pattern)** → [📖 Architecture Guide](Documentation/Architecture.md)
- **App.xaml.cs**: Dependency injection container setup and service registration
- **Services/**: Core business logic abstracted into testable services
  - `IConfigurationService`: Thread-safe configuration management
  - `IMapService`: Map loading and spatial operations
  - `INetworkService`: Connectivity and API key management
  - `IVideoService`: POSM video integration
  - `IProgressReporter`: Unified progress reporting

### **User Interface (WPF)** → [📖 Development Guide](Documentation/Development.md)
- **MainWindow.xaml.cs**: Primary application window with search and map controls
- **LayersVisibilityWindow.xaml.cs**: Advanced layer management interface
- **BasemapGalleryWindow.xaml.cs**: Basemap selection and switching
- **VideoPlayerWindow.xaml.cs**: Dedicated POSM video player
- **OfflineMapWindow.xaml.cs**: Offline map generation interface

### **Data Integration** → [📖 Search Details](Documentation/SearchSystem.md)
- **LayerSearchService.cs**: High-performance multi-layer search engine
- **LayerSearchSource.cs**: ArcGIS Runtime search integration
- **Database Integration**: POSM.mdb connectivity for inspection data

## Performance & Monitoring

### **Console Logging Patterns**
Monitor application health through categorized console output:
- `[CONFIG]`: Configuration loading and validation
- `[MAP]`: Map loading and initialization operations
- `[LAYER SEARCH]`: Asset field searching operations
- `[GEOCODING]`: Address geocoding operations
- `[AUTOCOMPLETE]`: UI suggestion handling
- `[PERFORMANCE]`: Query timing and caching metrics
- `[DEBOUNCE]`: Input delay management
- `[OFFLINE MAP]`: Offline map generation and layer processing
- `[VIDEO]`: POSM video integration operations

### **Performance Optimization**
- **Search Performance**: Queries optimized to 150-1000ms with intelligent caching
- **Memory Management**: LRU cache with automatic cleanup and thread-safe operations
- **Query Efficiency**: ReturnGeometry=false, MaxFeatures=3 per query
- **Debouncing**: 300ms delay reduces query volume by ~70% during active typing

## Troubleshooting

### **Common Issues & Solutions**
- **Slow Autocomplete**: Check debouncing is working (300ms delay in logs)
- **No Search Suggestions**: Verify layers are enabled in config.json and exist in map
- **Geocoding Wrong Area**: Confirm USA constraint and map extent are working
- **Keyboard Navigation Fails**: Check suggestion dropdown is visible and populated
- **POSM Integration Issues**: Verify posmExecutablePath and database connectivity
- **Layer Download Issues**: Check layer visibility and scale restrictions in logs

### **Configuration Validation**
- Verify `queryLayers` array contains layers with `enabled: true`
- Ensure `searchFields` arrays are populated for enabled layers
- Check that `layerName` values match actual layer names in the map
- Confirm `idField` matches the primary identifier field name

## 📚 Complete Documentation Suite

| Document | Purpose | When to Use |
|----------|---------|-------------|
| **[📖 Documentation Index](Documentation/README.md)** | Complete documentation overview | Start here for comprehensive information |
| **[🏗️ Architecture Guide](Documentation/Architecture.md)** | System design and service layer | Understanding codebase structure |
| **[⚙️ Configuration Reference](Documentation/Configuration.md)** | Complete config.json guide | Setting up and configuring the application |
| **[🔍 Search System](Documentation/SearchSystem.md)** | Advanced search implementation | Understanding search performance and features |
| **[🎯 Service Layer](Documentation/ServiceLayer.md)** | Dependency injection patterns | Working with services and DI container |
| **[🛠️ Development Guide](Documentation/Development.md)** | Best practices and guidelines | Contributing to the codebase |
| **[🗺️ Offline Maps Guide](Documentation/OfflineBasemapGuide.md)** | Offline map generation | Working with offline basemaps |
| **[📋 Context Guide](Documentation/Context.md)** | Project context and background | Understanding project history and goals |

### 🚀 **Getting Started Path**
1. **New Users**: Start with [Documentation/README.md](Documentation/README.md)
2. **Configuration**: Follow [Configuration.md](Documentation/Configuration.md)
3. **Development**: Read [Development.md](Documentation/Development.md)
4. **Advanced Features**: Explore [SearchSystem.md](Documentation/SearchSystem.md) and [ServiceLayer.md](Documentation/ServiceLayer.md)

## Version History
- **3.3.0**: Advanced search system with performance optimizations
- **3.1.0**: Enterprise service architecture with dependency injection
- **3.2.3**: Offline functionality bug fixes

## Contributing

Contributions to this project are welcome! Please feel free to open an issue or submit a pull request with your improvements or bug fixes.

## License

POSM Software LLC

## Contact

Developed by **Octavio Pereira**  
Email: [Octavio@posmsoftware.com](mailto:Octavio@posmsoftware.com)  
Organization: **POSM Software**

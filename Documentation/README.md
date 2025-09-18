# POSM Map Reader 3.3.0 - MapSync Documentation

> 🏠 **Main Project**: Return to [README.md](../README.md) for quick start and overview

## Overview

POSM Map Reader 3.3.0 - MapSync is a professional WPF ArcGIS Runtime application that manages pipe assets in GIS, launches POSM inspections, and georeferencesPOSM media observations as point features. This version combines the advanced search capabilities of 3.3.0 with the professional service architecture from MR3 3.1.0.

## Quick Start

1. **Prerequisites**
   - .NET 8.0 Windows Runtime
   - ArcGIS Runtime 200.7.0
   - Valid ArcGIS API Key and Runtime License

2. **Configuration**
   - Copy `config.json` template
   - Set your API key, license string, and map ID
   - Configure layer settings and search parameters

3. **Build & Run**
   ```bash
   dotnet build POSM_MR3_2.csproj
   dotnet run POSM_MR3_2.csproj
   ```

## Documentation Structure

- [Architecture Overview](Architecture.md) - System design and service layer
- [Configuration Guide](Configuration.md) - Complete config.json reference
- [Search System](SearchSystem.md) - Advanced search features and performance
- [POSM Integration](POSMIntegration.md) - Video and database integration
- [Service Layer](ServiceLayer.md) - Dependency injection and services
- [Development Guide](Development.md) - Best practices and patterns
- [Troubleshooting](Troubleshooting.md) - Common issues and solutions
- [API Reference](APIReference.md) - Service interfaces and methods

## Key Features

### 🔍 **Advanced Search System**
- Dual-mode search (Address/Asset)
- Real-time autocomplete with 300ms debouncing
- Intelligent caching (50-entry LRU cache, 5-minute TTL)
- Performance-optimized queries
- USA-constrained geocoding

### 🎬 **POSM Video Integration**
- Video popup buttons in map popups
- Dedicated video player window
- Inspection image overlays
- Database service integration

### 🏗️ **Professional Architecture**
- Dependency injection with Microsoft.Extensions
- Service layer abstraction
- Structured logging with categorized output
- Progress reporting with event-driven updates

### 🗺️ **Layer Management**
- Advanced layer visibility controls
- Hierarchical layer tree
- Persistent visibility settings
- Real-time layer toggling

## Recent Enhancements (v3.3.0)

### Service Architecture Integration
- ✅ Added dependency injection container
- ✅ Implemented service abstractions (IConfigurationService, IMapService, etc.)
- ✅ Integrated structured logging
- ✅ Added progress reporting system

### UI Enhancements
- ✅ LayersVisibilityWindow from MR3 3.1.0
- ✅ Enhanced error handling and user feedback
- ✅ Professional loading states with progress updates

### Performance Optimizations
- ✅ Query cancellation tokens
- ✅ Debounced search input
- ✅ Thread-safe caching
- ✅ Memory-efficient operations

## Support

For technical support or feature requests, refer to:
- [Troubleshooting Guide](Troubleshooting.md)
- [Development Guide](Development.md)
- Internal documentation and code comments
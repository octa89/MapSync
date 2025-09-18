# Current Search System Context

## How Search Currently Works

### **Existing Architecture**
- **Dual-mode search**: Toggle between 🗺️ Address and 🏢 Asset modes via `_isAddressMode` boolean
- **Unified TextBox**: Single search input with mode-specific behavior
- **Debounce Timer**: 300ms delay via `DispatcherTimer` to prevent excessive queries
- **Cancellation Tokens**: `CancellationTokenSource` for stopping abandoned searches
- **LRU Cache**: 50-entry cache with 5-minute TTL for performance

### **Current Behavior by Mode**

#### **Address Mode (🗺️)**
- **Current State**: No type-ahead suggestions - only performs search on Enter
- **Search Action**: Direct geocoding via `LocatorTask.GeocodeAsync()`
- **Constraints**: USA-only + current map extent bounds
- **Result**: Zooms to geocoded location
- **Limitation**: User must type full query and press Enter (no autocomplete)

#### **Asset Mode (🏢)**
- **Current State**: Full type-ahead suggestions working
- **Search Source**: Replica cache + LayerSearchSource
- **Data**: Searches configured `queryLayers` fields (AssetID, StartID, etc.)
- **Performance**: In-memory index with debouncing and caching
- **Result**: Highlights matching features on map

### **Key Methods & Fields**
- **`_isAddressMode`**: Boolean toggle between search modes
- **`_geocodingService`**: LocatorTask for Address geocoding
- **`_debounceTimer`**: 300ms delay for type-ahead
- **`_currentSearchCts`**: Cancellation token source
- **`_suggestionCache`**: LRU cache for Asset suggestions
- **`UnifiedSearchTextBox_TextChanged()`**: Main text input handler
- **`ShowSuggestionsAsync()`**: Suggestion display logic
- **`ApplySelectedSuggestion()`**: Selection handler

## Where We'll Patch

### **New Components to Add**
- **`_geoSuggestionIndex`**: Dictionary to map suggestion text to `SuggestResult` objects
- **`BuildSuggestParams()`**: Create `SuggestParameters` with USA + extent constraints
- **`BuildGeocodeParams()`**: Create `GeocodeParameters` with same constraints
- **`GetAddressSuggestionsAsync()`**: Call `LocatorTask.SuggestAsync()` for Address suggestions
- **`GeocodeFromSuggestionAsync()`**: Convert selected suggestion to map location

### **Methods to Modify**
- **`UnifiedSearchTextBox_TextChanged()`**: Enable debounce for both Address and Asset modes
- **`ShowSuggestionsAsync()`**: Branch by mode - Asset uses existing logic, Address calls new geocoding
- **`ApplySelectedSuggestion()`**: Add Address mode branch to geocode and zoom to result

### **Shared Infrastructure**
- **Debounce Pipeline**: Reuse existing 300ms timer for both modes
- **Cancellation**: Use same `CancellationTokenSource` pattern with 3s timeout
- **UI Components**: Keep existing suggestion dropdown and keyboard navigation
- **Configuration**: Leverage existing map extent and network service

### **Expected Flow After Patch**
1. **User types in Address mode** → Debounce timer starts
2. **Timer fires** → `GetAddressSuggestionsAsync()` calls ArcGIS SuggestAsync
3. **Suggestions returned** → Populate dropdown with formatted results
4. **User selects suggestion** → `GeocodeFromSuggestionAsync()` geocodes and zooms to location
5. **Asset mode unchanged** → Continues using existing replica cache system
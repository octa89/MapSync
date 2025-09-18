using System;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using Esri.ArcGISRuntime;
using Microsoft.Extensions.Logging;

namespace WpfMapApp1.Services
{
    public class NetworkService : INetworkService
    {
        private readonly HttpClient _httpClient;
        private readonly IConfigurationService _configurationService;
        private readonly ILogger<NetworkService> _logger;

        public NetworkService(
            HttpClient httpClient,
            IConfigurationService configurationService,
            ILogger<NetworkService> logger)
        {
            _httpClient = httpClient;
            _configurationService = configurationService;
            _logger = logger;
            
            // Configure HttpClient
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("POSM-MapReader/3.3.0");
            _httpClient.Timeout = TimeSpan.FromSeconds(5);
        }

        public async Task<bool> IsArcGisOnlineReachableAsync(CancellationToken cancellationToken = default)
        {
            if (!NetworkInterface.GetIsNetworkAvailable())
            {
                _logger.LogDebug("Network interface not available");
                return false;
            }

            try
            {
                // Use shorter timeout for faster failure detection
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(3));
                
                using var request = new HttpRequestMessage(HttpMethod.Head, 
                    "https://www.arcgis.com/sharing/rest?f=pjson");
                
                using var response = await _httpClient.SendAsync(request, 
                    HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);
                
                var isReachable = response.IsSuccessStatusCode;
                _logger.LogDebug("ArcGIS Online reachable: {IsReachable}", isReachable);
                return isReachable;
            }
            catch (OperationCanceledException)
            {
                _logger.LogDebug("Network connectivity check timed out");
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to reach ArcGIS Online");
                return false;
            }
        }

        public async Task<bool> EnsureOnlineApiKeyIfAvailableAsync(CancellationToken cancellationToken = default)
        {
            var key = _configurationService.Configuration?.apiKey;
            if (string.IsNullOrWhiteSpace(key))
            {
                _logger.LogDebug("No API key configured");
                return false;
            }

            if (await IsArcGisOnlineReachableAsync(cancellationToken))
            {
                ArcGISRuntimeEnvironment.ApiKey = key;
                _logger.LogInformation("API key set for online services");
                return true;
            }

            _logger.LogDebug("Cannot set API key - ArcGIS Online not reachable");
            return false;
        }
    }
}
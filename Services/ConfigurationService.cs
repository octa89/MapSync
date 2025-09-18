using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace WpfMapApp1.Services
{
    public class ConfigurationService : IConfigurationService
    {
        private readonly ILogger<ConfigurationService> _logger;
        private Config _configuration;
        private const string ConfigFileName = "config.json";

        public Config Configuration => _configuration ?? new Config();

        public ConfigurationService(ILogger<ConfigurationService> logger)
        {
            _logger = logger;
            _configuration = new Config();
        }

        public async Task LoadConfigurationAsync()
        {
            try
            {
                if (File.Exists(ConfigFileName))
                {
                    _logger.LogInformation("Loading configuration from {ConfigFile}", ConfigFileName);
                    var json = File.ReadAllText(ConfigFileName); // Use synchronous version for startup
                    _configuration = string.IsNullOrWhiteSpace(json)
                        ? new Config()
                        : JsonConvert.DeserializeObject<Config>(json) ?? new Config();
                    _logger.LogInformation("Configuration loaded successfully");
                }
                else
                {
                    _logger.LogWarning("Configuration file not found, creating default configuration");
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

        public async Task SaveConfigurationAsync(Config config)
        {
            try
            {
                var json = JsonConvert.SerializeObject(config, Formatting.Indented);
                await File.WriteAllTextAsync(ConfigFileName, json);
                _configuration = config;
                _logger.LogInformation("Configuration saved successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving configuration");
                throw;
            }
        }

        public void UpdateConfiguration(Config config)
        {
            _configuration = config ?? throw new ArgumentNullException(nameof(config));
        }

        private Config CreateDefaultConfiguration()
        {
            return new Config
            {
                runtimeLicenseString = string.Empty, // User must provide
                apiKey = string.Empty, // User must provide
                posmExecutablePath = @"C:\POSM\POSM.exe",
                inspectionType = "NASSCO PACP",
                mapId = string.Empty, // User must provide
                idField = "AssetID",
                selectedLayer = string.Empty
            };
        }
    }
}
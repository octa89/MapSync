using System.Configuration;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Windows;
using Esri.ArcGISRuntime;
using Esri.ArcGISRuntime.Http;
using Esri.ArcGISRuntime.Security;
using Esri.ArcGISRuntime.Symbology;
using Esri.ArcGISRuntime.Tasks.Geocoding;
using Esri.ArcGISRuntime.UI;
using Newtonsoft.Json;
using POSM_MR3_2;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WpfMapApp1.Services;

namespace WpfMapApp1
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : System.Windows.Application
    {
        public static Config Configuration { get; set; } = new Config();
        public static IServiceProvider ServiceProvider { get; private set; } = null!;

        // If your App.xaml uses: Startup="Application_Startup", keep it.
        // If not, you can hook this up or convert to OnStartup override.
        private void Application_Startup(object sender, StartupEventArgs e)
        {
            // Set up global exception handlers
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
            Application.Current.DispatcherUnhandledException += Application_DispatcherUnhandledException;
            TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;

            // 1) Load config (no ArcGIS objects yet)
            LoadConfiguration();

            // 2) Configure dependency injection
            ConfigureServices();

            // 3) Apply production Runtime license BEFORE any MapView/Map/etc. is created
            // Prefer reading from config.json so you don't hard-code it here.
            var licenseString = (Configuration.runtimeLicenseString ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(licenseString))
            {
                // Fallback (leave empty if you want to force config-only)
                // licenseString = "runtimelite,1000,...."; // ← your real license string
            }

            var setResult = ArcGISRuntimeEnvironment.SetLicense(licenseString);
            Debug.WriteLine($"[Licensing] SetLicense => Status={setResult.LicenseStatus}");

            var lic = ArcGISRuntimeEnvironment.GetLicense();
            var expiryText = lic.IsPermanent ? "permanent" : lic.Expiry.ToString("u");
            var key = (Configuration?.apiKey ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(key))
                ArcGISRuntimeEnvironment.ApiKey = key;
           

            Debug.WriteLine($"[Licensing] GetLicense => Status={lic.LicenseStatus}, Level={lic.LicenseLevel}, Permanent={lic.IsPermanent}, Expiry={expiryText}");

           

            if (lic.LicenseStatus != LicenseStatus.Valid)
            {
                // Optional: show a gentle warning so you notice in Release builds
                MessageBox.Show(
                    "ArcGIS Runtime license is not valid (app may show 'Developer Use Only').\n" +
                    "Put your production Runtime license in config.json → runtimeLicenseString.",
                    "Licensing", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            // 4) Initialize Runtime (NO global API key here)
            ArcGISRuntimeEnvironment.Initialize(cfg =>
                cfg.ConfigureAuthentication(a => a.UseDefaultChallengeHandler())
            );

            // 5) Create and show main window using dependency injection
            
            var mainWindow = ServiceProvider.GetRequiredService<MainWindow>();
            mainWindow.Show();
        }

        private void ConfigureServices()
        {
            var services = new ServiceCollection();

            // Add logging
            services.AddLogging(builder =>
            {
                builder.AddConsole();
                builder.SetMinimumLevel(LogLevel.Information);
            });

            // Add HttpClient
            services.AddHttpClient();

            // Add our services
            services.AddSingleton<IConfigurationService>(provider =>
            {
                var logger = provider.GetRequiredService<ILogger<ConfigurationService>>();
                var configService = new ConfigurationService(logger);
                configService.UpdateConfiguration(Configuration);
                return configService;
            });

            services.AddSingleton<INetworkService, NetworkService>();
            services.AddSingleton<IMapService, MapService>();
            services.AddSingleton<IOfflineMapService, OfflineMapService>();
            services.AddTransient<IProgressReporter, ProgressReporter>();
            
            // Enhanced search services
            services.AddSingleton<IReplicaCacheService, ReplicaCacheService>();
            services.AddTransient<ILayerSearchService, LayerSearchService>();

            // Add Windows
            services.AddTransient<MainWindow>();
            // Note: OfflineMapWindow is created manually with constructor parameters, not through DI

            ServiceProvider = services.BuildServiceProvider();
        }

        private void LoadConfiguration()
        {
            try
            {
                const string cfgPath = "config.json";
                if (File.Exists(cfgPath))
                {
                    var json = File.ReadAllText(cfgPath);
                    Configuration = string.IsNullOrWhiteSpace(json)
                        ? new Config()
                        : (JsonConvert.DeserializeObject<Config>(json) ?? new Config());
                }
                else
                {
                    // First-run defaults (edit as you like)
                    Configuration = new Config
                    {
                        runtimeLicenseString = "runtimelite,1000,rud4288660560,none,MJJC7XLS1ML0LAMEY242",
                        apiKey = "AAPT85fOqywZsicJupSmVSCGrpXO1qJwPQjNUMcDYphlO6sfLZegLdT1g4dF4BoRRYtJ1c1p_5YXGfzbmTgx5up-1fxMheVBom1uGtjz0ztA_h7cTKdlUm-XX-i6pqHBzXvzVJ4hLPvi-g-hgHPamxLyJi9INldxIDGLgLDd6E9anTY1lfk7H72yC5Y0ze7inpFYGbyngZNu2kxBx1ZzGIx4XugmcE3US4dSVSVFn-kpbyE.AT2_tWnrrjbG",
                        posmExecutablePath = @"C:\POSM\POSM.exe",
                        inspectionType = "NASSCO PACP",
                        mapId = "3a0241d5bb564c9b86a7a312ba2703d3",
                        idField = "AssetID",
                        selectedLayer = "",
                        offlineMode = false,
                        offlineBasemapPath = "",
                        queryLayers = new List<QueryLayerConfig>
                        {
                            new QueryLayerConfig
                            {
                                layerName = "ssGravityMain",
                                searchFields = new List<string> { "AssetID", "Material", "Size" },
                                displayField = "AssetID",
                                enabled = true
                            }
                        }
                    };

                    File.WriteAllText(cfgPath, JsonConvert.SerializeObject(Configuration, Formatting.Indented));
                }
            }
            catch
            {
                Configuration = new Config();
            }
        }

        #region Global Exception Handlers
        
        private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            var exception = e.ExceptionObject as Exception;
            var errorMessage = $"[FATAL ERROR] Unhandled exception in AppDomain: {exception?.Message ?? "Unknown error"}";
            
            Console.WriteLine($"{errorMessage}");
            Console.WriteLine($"[FATAL ERROR] Stack Trace: {exception?.StackTrace ?? "No stack trace"}");
            Console.WriteLine($"[FATAL ERROR] Is Terminating: {e.IsTerminating}");
            
            if (exception?.InnerException != null)
            {
                Console.WriteLine($"[FATAL ERROR] Inner Exception: {exception.InnerException.Message}");
                Console.WriteLine($"[FATAL ERROR] Inner Stack Trace: {exception.InnerException.StackTrace}");
            }
            
            // Show user-friendly error dialog
            MessageBox.Show($"A fatal error occurred: {exception?.Message ?? "Unknown error"}\n\nPlease check the console output for details.", 
                           "Fatal Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        private void Application_DispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
        {
            var errorMessage = $"[UI ERROR] Unhandled exception in UI thread: {e.Exception.Message}";
            
            Console.WriteLine($"{errorMessage}");
            Console.WriteLine($"[UI ERROR] Stack Trace: {e.Exception.StackTrace}");
            Console.WriteLine($"[UI ERROR] Source: {e.Exception.Source}");
            
            if (e.Exception.InnerException != null)
            {
                Console.WriteLine($"[UI ERROR] Inner Exception: {e.Exception.InnerException.Message}");
                Console.WriteLine($"[UI ERROR] Inner Stack Trace: {e.Exception.InnerException.StackTrace}");
            }
            
            // Mark as handled to prevent application crash
            e.Handled = true;
            
            // Show user-friendly error dialog
            MessageBox.Show($"An error occurred in the user interface: {e.Exception.Message}\n\nThe application will continue running. Please check the console output for details.", 
                           "UI Error", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        private void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            var errorMessage = $"[ASYNC ERROR] Unobserved task exception: {e.Exception.Message}";
            
            Console.WriteLine($"{errorMessage}");
            Console.WriteLine($"[ASYNC ERROR] Stack Trace: {e.Exception.StackTrace}");
            
            foreach (var innerEx in e.Exception.InnerExceptions)
            {
                Console.WriteLine($"[ASYNC ERROR] Inner Exception: {innerEx.Message}");
                Console.WriteLine($"[ASYNC ERROR] Inner Stack Trace: {innerEx.StackTrace}");
            }
            
            // Mark as observed to prevent application crash
            e.SetObserved();
            
            Console.WriteLine($"[ASYNC ERROR] Exception marked as observed to prevent crash");
        }
        
        #endregion
    }
}

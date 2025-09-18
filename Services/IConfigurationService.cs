using System.Threading.Tasks;

namespace WpfMapApp1.Services
{
    public interface IConfigurationService
    {
        Config Configuration { get; }
        Task LoadConfigurationAsync();
        Task SaveConfigurationAsync(Config config);
        void UpdateConfiguration(Config config);
    }
}
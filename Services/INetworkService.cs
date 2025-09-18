using System.Threading;
using System.Threading.Tasks;

namespace WpfMapApp1.Services
{
    public interface INetworkService
    {
        Task<bool> IsArcGisOnlineReachableAsync(CancellationToken cancellationToken = default);
        Task<bool> EnsureOnlineApiKeyIfAvailableAsync(CancellationToken cancellationToken = default);
    }
}
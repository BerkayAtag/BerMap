using System.Threading;
using System.Threading.Tasks;

namespace BerMap.Application.Interfaces;

public interface ICredentialStore
{
    Task StoreSecretAsync(string key, string plainTextSecret, CancellationToken cancellationToken = default);
    Task<string?> RetrieveSecretAsync(string key, CancellationToken cancellationToken = default);
    Task DeleteSecretAsync(string key, CancellationToken cancellationToken = default);
}

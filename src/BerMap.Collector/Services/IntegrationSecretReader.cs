using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace BerMap.Collector.Services;

/// <summary>
/// Supplies the adapter secret for a run. It is read again before every run,
/// so a rotated password needs no restart, and is never written to a log.
/// </summary>
public interface IIntegrationSecretReader
{
    /// <exception cref="CollectorSecretException">The secret is missing or unreadable.</exception>
    Task<string> ReadSecretAsync(CollectorIntegration integration, CancellationToken cancellationToken = default);
}

/// <summary>The secret could not be obtained. The message names the source, never the value.</summary>
public sealed class CollectorSecretException : Exception
{
    public CollectorSecretException(string message) : base(message)
    {
    }
}

public sealed class IntegrationSecretReader : IIntegrationSecretReader
{
    public async Task<string> ReadSecretAsync(CollectorIntegration integration, CancellationToken cancellationToken = default)
    {
        string? secret;
        if (integration.SecretFile is { } file)
        {
            try
            {
                secret = await File.ReadAllTextAsync(file, cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
                // Only the exception type: some messages quote file content or ACL details.
                throw new CollectorSecretException(
                    $"The secret file '{file}' could not be read ({ex.GetType().Name}). Check that it exists and that the collector service account can read it.");
            }
        }
        else if (integration.SecretEnvironmentVariable is { } variable)
        {
            secret = Environment.GetEnvironmentVariable(variable);
            if (secret == null)
                throw new CollectorSecretException($"The environment variable '{variable}' that should hold the secret is not set.");
        }
        else
        {
            throw new CollectorSecretException("No secret source is configured.");
        }

        // Editors and "echo" add a line break; the secret itself is everything before it.
        secret = secret.TrimEnd('\r', '\n');
        if (secret.Length == 0)
            throw new CollectorSecretException($"The secret from {integration.SecretSourceDescription} is empty.");
        return secret;
    }
}

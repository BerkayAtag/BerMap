using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using BerMap.Domain.Entities;
using BerMap.Domain.Enums;
using BerMap.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BerMap.Api.Security;

/// <summary>
/// Brings a fresh or upgraded database to the current schema and makes sure
/// the platform has its built-in roles and one local administrator, so the
/// console is usable right after installation.
/// </summary>
public static class DatabaseInitializer
{
    public const string InitialPasswordFileName = "initial-admin-password.txt";

    public static async Task InitializeAsync(IServiceProvider services, IConfiguration configuration,
        string vaultDirectory, ILogger logger, CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<BerMapDbContext>();

        if (configuration.GetValue("BerMap:Database:ApplyMigrationsOnStartup", true))
        {
            var pending = (await context.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
            if (pending.Count > 0)
            {
                logger.LogInformation("Applying {Count} database migration(s): {Migrations}",
                    pending.Count, string.Join(", ", pending));
                await context.Database.MigrateAsync(cancellationToken);
            }
        }

        await RecoverInterruptedJobsAsync(context, logger, cancellationToken);
        await EnsureRolesAsync(context, cancellationToken);
        await EnsureAdministratorAsync(context, configuration, vaultDirectory, logger, cancellationToken);
    }

    /// <summary>
    /// Discovery jobs run in-process, so a job still marked Running at startup
    /// was interrupted by a crash or restart. Left alone it would block new
    /// discovery of its integration forever (DISCOVERY_ALREADY_ACTIVE).
    /// </summary>
    private static async Task RecoverInterruptedJobsAsync(BerMapDbContext context, ILogger logger,
        CancellationToken cancellationToken)
    {
        var interrupted = await context.DiscoveryJobs
            .Include(j => j.Integration)
            .Where(j => j.Status == DiscoveryJobStatus.Running)
            .ToListAsync(cancellationToken);

        if (interrupted.Count == 0)
            return;

        var now = DateTime.UtcNow;
        foreach (var job in interrupted)
        {
            job.Status = DiscoveryJobStatus.Failed;
            job.CompletedAt = now;
            job.CurrentPhase = "Failed";
            job.CurrentMessage = "The server restarted while this discovery was running.";
            job.ErrorsJson = "[\"DISCOVERY_INTERRUPTED: The server restarted while this discovery was running.\"]";
            job.DurationMs = (long)(now - job.StartedAt).TotalMilliseconds;

            if (job.Integration.Status == IntegrationStatus.Testing)
            {
                job.Integration.Status = IntegrationStatus.Error;
                job.Integration.LastError = "DISCOVERY_INTERRUPTED: The server restarted while discovery was running.";
                job.Integration.LastErrorAt = now;
            }
        }

        await context.SaveChangesAsync(cancellationToken);
        logger.LogWarning("Marked {Count} interrupted discovery job(s) as failed.", interrupted.Count);
    }

    private static async Task EnsureRolesAsync(BerMapDbContext context, CancellationToken cancellationToken)
    {
        var existing = await context.Roles.Select(r => r.RoleType).ToListAsync(cancellationToken);
        foreach (var roleType in Enum.GetValues<UserRole>().Except(existing))
        {
            context.Roles.Add(new Role
            {
                Name = roleType.ToString(),
                Description = $"Built-in {roleType} role.",
                RoleType = roleType
            });
        }

        await context.SaveChangesAsync(cancellationToken);
    }

    private static async Task EnsureAdministratorAsync(BerMapDbContext context, IConfiguration configuration,
        string vaultDirectory, ILogger logger, CancellationToken cancellationToken)
    {
        if (await context.Users.AnyAsync(cancellationToken))
            return;

        var username = configuration["BerMap:Bootstrap:AdminUsername"];
        if (string.IsNullOrWhiteSpace(username))
            username = "admin";

        var password = configuration["BerMap:Bootstrap:AdminPassword"];
        var generated = string.IsNullOrWhiteSpace(password);
        if (generated)
            password = GeneratePassword();

        var adminRole = await context.Roles.FirstAsync(r => r.RoleType == UserRole.PlatformAdministrator, cancellationToken);
        var user = new User
        {
            Username = username.Trim(),
            DisplayName = "Platform Administrator",
            Email = string.Empty,
            PasswordHash = PasswordHasher.Hash(password!),
            IsActive = true
        };
        user.Roles.Add(new UserRoleAssignment { User = user, RoleId = adminRole.Id });
        context.Users.Add(user);
        await context.SaveChangesAsync(cancellationToken);

        if (generated)
        {
            SecretFile.EnsureDirectory(vaultDirectory);
            var path = Path.Combine(vaultDirectory, InitialPasswordFileName);
            SecretFile.WriteAllText(path, $"username: {user.Username}{Environment.NewLine}password: {password}{Environment.NewLine}");
            logger.LogWarning(
                "Created the initial local administrator '{Username}'. The generated password was written to {Path}. " +
                "Sign in, then delete that file.", user.Username, path);
        }
        else
        {
            logger.LogInformation("Created the initial local administrator '{Username}' from configuration.", user.Username);
        }
    }

    private static string GeneratePassword()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";
        return RandomNumberGenerator.GetString(alphabet, 20);
    }
}

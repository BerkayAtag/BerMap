using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace BerMap.Infrastructure.Data;

/// <summary>
/// Design-time factory used by `dotnet ef migrations`. It never connects to a
/// database; the connection string is only used to build the Npgsql model.
/// </summary>
public sealed class BerMapDbContextFactory : IDesignTimeDbContextFactory<BerMapDbContext>
{
    public BerMapDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("BERMAP_CONNECTION_STRING")
            ?? "Host=localhost;Port=5432;Database=bermap_db;Username=postgres;Password=postgres";

        var options = new DbContextOptionsBuilder<BerMapDbContext>()
            .UseNpgsql(connectionString, b => b.MigrationsAssembly("BerMap.Infrastructure"))
            .Options;

        return new BerMapDbContext(options);
    }
}

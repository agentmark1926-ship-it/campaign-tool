using CampaignTool.Web.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;

namespace CampaignTool.Tests.Infrastructure;

/// <summary>
/// Base SQL Server connection string (no database) comes from CAMPAIGNTOOL_TEST_SQL, e.g. a local SQL container
/// or LocalDB. Each TestApp gets its own throwaway database, dropped on dispose.
/// </summary>
public static class TestSql
{
    public static string ConnectionString(string database)
    {
        var baseCs = Environment.GetEnvironmentVariable("CAMPAIGNTOOL_TEST_SQL");
        if (string.IsNullOrWhiteSpace(baseCs))
            throw new InvalidOperationException("Set CAMPAIGNTOOL_TEST_SQL to a SQL Server connection string (without a database) to run database tests.");
        return new SqlConnectionStringBuilder(baseCs) { InitialCatalog = database, TrustServerCertificate = true }.ConnectionString;
    }

    public static AppDbContext NewContext(string connectionString) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(connectionString).Options);
}

public class TestApp : WebApplicationFactory<Program>
{
    public string ConnectionString { get; } = TestSql.ConnectionString($"ct_test_{Guid.NewGuid():N}");
    public string Environment { get; init; } = "Production";
    public Dictionary<string, string?> Settings { get; } = new() { ["Auth:AllowedUsers"] = "owner@example.com, second@example.com" };

    /// <summary>False: the background CampaignWorker is not started, so a test drives sending itself.</summary>
    public bool RunWorker { get; init; } = true;

    /// <summary>Extra service replacements (fake clock, scripted email sender, …).</summary>
    public Action<IServiceCollection>? ConfigureServices { get; init; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environment);
        builder.UseSetting("ConnectionStrings:Sql", ConnectionString);
        foreach (var (key, value) in Settings)
            builder.UseSetting(key, value);
        builder.ConfigureTestServices(services =>
        {
            if (!RunWorker)
                foreach (var d in services.Where(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(CampaignTool.Web.Workers.CampaignWorker)).ToList())
                    services.Remove(d);
            ConfigureServices?.Invoke(services);
        });
    }

    public AppDbContext NewDbContext() => TestSql.NewContext(ConnectionString);

    public override async ValueTask DisposeAsync()
    {
        await using (var db = NewDbContext())
            await db.Database.EnsureDeletedAsync();
        await base.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}

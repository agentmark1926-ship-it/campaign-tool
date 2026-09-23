using CampaignTool.Web.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

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

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environment);
        builder.UseSetting("ConnectionStrings:Sql", ConnectionString);
        foreach (var (key, value) in Settings)
            builder.UseSetting(key, value);
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

using System.Net;
using CampaignTool.Tests.Infrastructure;
using CampaignTool.Web;
using CampaignTool.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace CampaignTool.Tests;

public class FoundationTests : IAsyncLifetime
{
    private readonly TestApp _app = new();
    private HttpClient _client = null!;

    public Task InitializeAsync()
    {
        _client = _app.CreateClient(new() { AllowAutoRedirect = false });
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();

    [Fact]
    public async Task Health_is_anonymous_and_returns_200()
    {
        var response = await _client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task App_page_without_signed_in_user_is_401()
    {
        var response = await _client.GetAsync("/");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("owner@example.com")]
    [InlineData("SECOND@example.com")]
    public async Task Allowed_user_gets_the_app(string upn)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Add(AllowedUsersMiddleware.PrincipalNameHeader, upn);
        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task User_not_in_allowed_list_is_403()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Add(AllowedUsersMiddleware.PrincipalNameHeader, "intruder@example.com");
        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("/webhooks/acs")]
    [InlineData("/unsubscribe/abc")]
    public async Task Public_paths_skip_the_sign_in_check(string path)
    {
        var response = await _client.GetAsync(path);
        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Migration_creates_every_table_in_the_data_model()
    {
        await _client.GetAsync("/health"); // app start applies migrations
        await using var db = _app.NewDbContext();
        var tables = await db.Database
            .SqlQueryRaw<string>("SELECT name AS [Value] FROM sys.tables WHERE name <> '__EFMigrationsHistory'")
            .ToListAsync();
        string[] expected = ["Contacts", "Lists", "ListContacts", "Suppressions", "Templates", "Campaigns", "CampaignRecipients", "EmailEvents", "Imports", "Settings"];
        Assert.Equal(expected.Order(), tables.Order());
    }

    [Theory]
    [InlineData("a@x.com, b@x.com", "B@X.COM", true)]
    [InlineData("a@x.com", " a@x.com ", true)]
    [InlineData("a@x.com", "c@x.com", false)]
    [InlineData("", "a@x.com", false)]
    [InlineData("a@x.com", "", false)]
    public void AllowedUsers_matching(string allowed, string upn, bool expected) =>
        Assert.Equal(expected, new AuthOptions { AllowedUsers = allowed }.IsAllowed(upn));
}

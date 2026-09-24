using CampaignTool.Tests.Infrastructure;
using CampaignTool.Web.Services;
using Microsoft.Extensions.DependencyInjection;

namespace CampaignTool.Tests;

public class SettingsAndTestEmailTests : IAsyncLifetime
{
    private readonly TestApp _app = new();

    public Task InitializeAsync()
    {
        _app.Settings["App:MailingAddress"] = "1 Test St, Miami, FL 33131";
        _app.Settings["Acs:SenderAddress"] = "DoNotReply@test.azurecomm.net";
        _app.Settings["Acs:SenderAddresses"] = "DoNotReply@news.example.com,DoNotReply@test.azurecomm.net";
        _app.CreateClient();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();

    private T Get<T>(IServiceScope scope) where T : notnull => scope.ServiceProvider.GetRequiredService<T>();

    [Fact]
    public async Task Settings_default_from_configuration_then_save_and_reload()
    {
        using (var scope = _app.Services.CreateScope())
        {
            var s = await Get<SettingsService>(scope).GetAsync();
            Assert.Equal("1 Test St, Miami, FL 33131", s.MailingAddress);
            Assert.Equal("DoNotReply@test.azurecomm.net", s.SenderAddress);
            Assert.Equal(25, s.MaxPerMinute);
            Assert.Equal(90, s.MaxPerHour);

            s.ReplyTo = "Owner@Example.com";
            s.MaxPerMinute = 20;
            s.MaxPerHour = 80;
            s.TimeZone = "America/New_York";
            Assert.Empty(await Get<SettingsService>(scope).SaveAsync(s));
        }
        using (var scope = _app.Services.CreateScope())
        {
            var s = await Get<SettingsService>(scope).GetAsync();
            Assert.Equal("owner@example.com", s.ReplyTo);
            Assert.Equal(20, s.MaxPerMinute);
            Assert.Equal(80, s.MaxPerHour);
            Assert.Equal("America/New_York", s.TimeZone);
        }
    }

    [Fact]
    public async Task The_from_address_can_be_switched_to_any_linked_domain_and_test_emails_use_it()
    {
        using (var scope = _app.Services.CreateScope())
        {
            var service = Get<SettingsService>(scope);
            var s = await service.GetAsync();
            Assert.Equal(["DoNotReply@test.azurecomm.net", "DoNotReply@news.example.com"], s.AllowedSenders);
            s.ReplyTo = "owner@example.com";

            s.SenderAddress = "DoNotReply@elsewhere.com";
            Assert.Contains(await service.SaveAsync(s), e => e.Contains("isn't set up in Azure"));

            s.SenderAddress = "DoNotReply@news.example.com";
            Assert.Empty(await service.SaveAsync(s));
        }
        using (var scope = _app.Services.CreateScope())
        {
            Assert.Equal("DoNotReply@news.example.com", (await Get<SettingsService>(scope).GetAsync()).SenderAddress);
            var sender = (InMemoryEmailSender)Get<IEmailSender>(scope);
            await Get<TestEmailService>(scope).SendAsync("from-check@example.com", "Hello", "<p>Hi</p>");
            Assert.Equal("DoNotReply@news.example.com", sender.Sent.Single(x => x.Email.To == "from-check@example.com").Email.From);
        }
    }

    [Fact]
    public async Task Invalid_settings_are_not_saved()
    {
        using var scope = _app.Services.CreateScope();
        var service = Get<SettingsService>(scope);
        var s = await service.GetAsync();
        s.ReplyTo = "";
        s.MailingAddress = " ";
        s.TimeZone = "Mars/Olympus";
        s.MaxPerMinute = 100;
        s.MaxPerHour = 10;
        Assert.Equal(4, (await service.SaveAsync(s)).Count);
        Assert.Equal("1 Test St, Miami, FL 33131", (await service.GetAsync()).MailingAddress);
    }

    [Fact]
    public async Task Test_email_goes_to_each_address_with_the_mailing_address_footer()
    {
        using var scope = _app.Services.CreateScope();
        var sender = (InMemoryEmailSender)Get<IEmailSender>(scope);
        var outcomes = await Get<TestEmailService>(scope).SendAsync("A@example.com, b@example.com", "Hello", "<p>Hi</p>");

        Assert.Equal(["a@example.com", "b@example.com"], outcomes.Select(o => o.Address));
        Assert.All(outcomes, o => Assert.True(o.Result.Success));
        var sent = sender.Sent.Where(x => x.Email.To is "a@example.com" or "b@example.com").ToList();
        Assert.Equal(2, sent.Count);
        Assert.All(sent, x => Assert.Contains("1 Test St, Miami, FL 33131", x.Email.Html));
        Assert.All(sent, x => Assert.StartsWith("[TEST]", x.Email.Subject));
    }

    [Theory]
    [InlineData("a@x.com,b@x.com,c@x.com,d@x.com,e@x.com,f@x.com")]
    [InlineData("not-an-address")]
    [InlineData("  ")]
    public async Task Test_email_rejects_bad_requests(string addresses)
    {
        using var scope = _app.Services.CreateScope();
        await Assert.ThrowsAsync<ArgumentException>(() => Get<TestEmailService>(scope).SendAsync(addresses, "s", "<p>b</p>"));
    }
}

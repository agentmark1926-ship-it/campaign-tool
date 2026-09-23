using System.Net;
using System.Text;
using System.Text.Json;
using Anthropic;
using CampaignTool.Tests.Infrastructure;
using CampaignTool.Web;
using CampaignTool.Web.Data;
using CampaignTool.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CampaignTool.Tests;

public class AiEmailWriterTests
{
    /// <summary>Captures the request and answers like the Messages API.</summary>
    private sealed class FakeApi(string text, string stopReason = "end_turn") : HttpMessageHandler
    {
        public JsonDocument? Request { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Request = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var body = JsonSerializer.Serialize(new
            {
                id = "msg_test", type = "message", role = "assistant", model = "claude-opus-5",
                content = new[] { new { type = "text", text } },
                stop_reason = stopReason, stop_sequence = (string?)null,
                usage = new { input_tokens = 10, output_tokens = 20 },
            });
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class TestWriter(FakeApi api) : AiEmailWriter(Options.Create(new AiOptions { AnthropicApiKey = "test-key" }), NullLogger<AiEmailWriter>.Instance)
    {
        protected override AnthropicClient CreateClient(string apiKey) =>
            new() { ApiKey = apiKey, HttpClient = new HttpClient(api), MaxRetries = 0 };
    }

    [Fact]
    public async Task Writes_html_with_the_expected_request_and_strips_code_fences()
    {
        var api = new FakeApi("```html\n<html><body><p>Hi {{ first_name | default: \"there\" }}</p></body></html>\n```");
        var html = await new TestWriter(api).WriteAsync(TemplateFormat.Html, "Announce the new Miami facility", null);

        Assert.Equal("<html><body><p>Hi {{ first_name | default: \"there\" }}</p></body></html>", html);
        var req = api.Request!.RootElement;
        Assert.Equal("claude-opus-5", req.GetProperty("model").GetString());
        Assert.Equal("default", req.GetProperty("fallbacks").GetString());
        Assert.Contains("table-based layout", req.GetProperty("system").GetString());
        Assert.Contains("Announce the new Miami facility", req.GetProperty("messages")[0].GetProperty("content").GetString());
    }

    [Fact]
    public async Task Revise_sends_the_current_body_and_plain_text_rules()
    {
        var api = new FakeApi("Hi there,\n\nNew text.");
        await new TestWriter(api).WriteAsync(TemplateFormat.Text, "Make it shorter", "Hi there,\n\nOld long text.");
        var req = api.Request!.RootElement;
        Assert.Contains("plain text only", req.GetProperty("system").GetString());
        var content = req.GetProperty("messages")[0].GetProperty("content").GetString()!;
        Assert.Contains("<current_email>\nHi there,\n\nOld long text.\n</current_email>", content);
        Assert.Contains("Make it shorter", content);
    }

    [Fact]
    public async Task Refusal_is_reported_in_one_sentence()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new TestWriter(new FakeApi("", "refusal")).WriteAsync(TemplateFormat.Html, "something", null));
        Assert.Contains("declined", ex.Message);
    }

    [Fact]
    public async Task Missing_api_key_explains_the_setting()
    {
        var writer = new AiEmailWriter(Options.Create(new AiOptions()), NullLogger<AiEmailWriter>.Instance);
        Assert.False(writer.IsConfigured);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => writer.WriteAsync(TemplateFormat.Html, "x", null));
        Assert.Contains("Ai__AnthropicApiKey", ex.Message);
    }
}

public class PlainTextFormatTests : IAsyncLifetime
{
    private readonly TestApp _app = new();

    public Task InitializeAsync()
    {
        _app.Settings["App:MailingAddress"] = "1 Test St, Miami, FL";
        _app.CreateClient();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();

    [Fact]
    public void Text_to_html_encodes_keeps_paragraphs_and_links()
    {
        var html = TemplateRenderer.TextToHtml("Hi <Ann> & co,\n\nLine one\nLine two https://example.com/offer.");
        Assert.Contains("Hi &lt;Ann&gt; &amp; co,", html);
        Assert.Contains("Line one<br/>Line two", html);
        Assert.Contains("<a href=\"https://example.com/offer\">https://example.com/offer</a>.", html);
        Assert.Equal(2, html.Split("<p ").Length - 1);
    }

    [Fact]
    public async Task Plain_text_test_send_carries_text_and_html_versions_with_the_footer()
    {
        using var scope = _app.Services.CreateScope();
        var sender = (InMemoryEmailSender)scope.ServiceProvider.GetRequiredService<IEmailSender>();
        await scope.ServiceProvider.GetRequiredService<TestEmailService>()
            .SendAsync("pt@example.com", "Hello {{ first_name | default: \"there\" }}", "Hi {{ first_name | default: \"there\" }},\n\nSee you soon.", TemplateFormat.Text);

        var (email, _) = sender.Sent.Single(x => x.Email.To == "pt@example.com");
        Assert.Equal("[TEST] Hello there", email.Subject);
        Assert.StartsWith("Hi there,\n\nSee you soon.", email.PlainText);
        Assert.Contains("1 Test St, Miami, FL", email.PlainText);
        Assert.Contains("<p style=\"margin:0 0 16px\">Hi there,</p>", email.Html);
    }

    [Fact]
    public async Task Template_saves_format_and_body_and_existing_rows_default_to_html()
    {
        using var scope = _app.Services.CreateScope();
        var templates = scope.ServiceProvider.GetRequiredService<TemplateService>();
        var t = await templates.CreateAsync();
        Assert.Equal(TemplateFormat.Html, t.Format);
        Assert.Null(await templates.SaveAsync(t.Id, "Letter", TemplateFormat.Text, "Dear {{ first_name | default: \"there\" }}"));
        Assert.NotNull(await templates.SaveAsync(t.Id, "Letter", TemplateFormat.Text, "Dear {{ first_name "));

        await using var db = _app.NewDbContext();
        var saved = db.Templates.Single(x => x.Id == t.Id);
        Assert.Equal(TemplateFormat.Text, saved.Format);
        Assert.Equal("Dear {{ first_name | default: \"there\" }}", saved.Text);
    }
}

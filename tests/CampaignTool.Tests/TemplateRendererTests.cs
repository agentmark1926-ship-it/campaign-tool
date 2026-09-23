using CampaignTool.Web.Data;
using CampaignTool.Web.Services;

namespace CampaignTool.Tests;

public class TemplateRendererTests
{
    private static Contact Named => new() { Email = "ann@example.com", FirstName = "Ann", LastName = "Lee", CustomFields = "{\"Unit Size\":\"10x10\"}" };
    private static Contact EmailOnly => new() { Email = "sub@example.com" };

    [Fact]
    public void Name_renders_when_present() =>
        Assert.Equal("<p>Hi Ann,</p>", TemplateRenderer.RenderHtml("<p>Hi {{ first_name | default: \"there\" }},</p>", Named));

    [Fact]
    public void Fallback_renders_for_email_only_contact() =>
        Assert.Equal("<p>Hi there,</p>", TemplateRenderer.RenderHtml("<p>Hi {{ first_name | default: \"there\" }},</p>", EmailOnly));

    [Fact]
    public void Every_value_is_html_encoded()
    {
        var c = new Contact { Email = "x@example.com", FirstName = "<script>alert(1)</script>", CustomFields = "{\"Note\":\"A & B\"}" };
        Assert.Equal("&lt;script&gt;alert(1)&lt;/script&gt; A &amp; B", TemplateRenderer.RenderHtml("{{ first_name }} {{ note }}", c));
    }

    [Fact]
    public void Subject_is_not_html_encoded() =>
        Assert.Equal("Tom & Jerry's offer", TemplateRenderer.RenderText("{{ first_name }}'s offer", new Contact { Email = "t@example.com", FirstName = "Tom & Jerry" }));

    [Fact]
    public void Unknown_field_renders_empty() =>
        Assert.Equal("[]", TemplateRenderer.RenderHtml("[{{ favourite_colour }}]", Named));

    [Fact]
    public void Custom_fields_are_available_in_snake_case() =>
        Assert.Equal("Your 10x10 unit", TemplateRenderer.RenderHtml("Your {{ unit_size }} unit", Named));

    [Fact]
    public void Quotes_encoded_by_the_visual_editor_still_parse() =>
        Assert.Equal("<p>Hi there</p>", TemplateRenderer.RenderHtml("<p>Hi {{ first_name | default: &quot;there&quot; }}</p>", EmailOnly));

    [Fact]
    public void Broken_tag_is_reported() =>
        Assert.NotNull(TemplateRenderer.Validate("Hi {{ first_name | default: \"there\" "));
}

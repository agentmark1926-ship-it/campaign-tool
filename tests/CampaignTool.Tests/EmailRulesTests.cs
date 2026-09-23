using CampaignTool.Web.Services;

namespace CampaignTool.Tests;

public class EmailRulesTests
{
    [Theory]
    [InlineData("  Jane@Example.COM  ", "jane@example.com")]
    [InlineData("Jane Doe <Jane@Example.com>", "jane@example.com")]
    [InlineData("\"jane@example.com\"", "jane@example.com")]
    [InlineData("﻿jane@example.com", "jane@example.com")]
    [InlineData("'jane@example.com' ", "jane@example.com")]
    public void Normalize(string raw, string expected) => Assert.Equal(expected, EmailRules.Normalize(raw));

    [Theory]
    [InlineData("jane@example.com", true)]
    [InlineData("jane.doe+news@mail.example.co.uk", true)]
    [InlineData("jane.example.com", false)]
    [InlineData("jane@@example.com", false)]
    [InlineData("jane@example", false)]
    [InlineData("jane..doe@example.com", false)]
    [InlineData("jane@example..com", false)]
    [InlineData("jane doe@example.com", false)]
    [InlineData(".jane@example.com", false)]
    [InlineData("@example.com", false)]
    [InlineData("", false)]
    public void IsValid(string normalized, bool expected) => Assert.Equal(expected, EmailRules.IsValid(normalized));
}

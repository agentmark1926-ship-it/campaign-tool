using System.Text;
using CampaignTool.Web.Services;

namespace CampaignTool.Tests;

public class CsvDetectorTests
{
    private static CsvLayout Detect(string text, bool bom = false)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        if (bom) bytes = [.. Encoding.UTF8.GetPreamble(), .. bytes];
        return CsvDetector.Detect(new MemoryStream(bytes));
    }

    [Fact]
    public void One_column_without_header()
    {
        var l = Detect("a@example.com\nb@example.com\nc@example.com\n");
        Assert.False(l.HasHeader);
        Assert.True(l.IsSingleColumn);
        Assert.Equal(0, l.EmailColumn);
        Assert.Equal("a@example.com", l.SampleRows[0][0]);
    }

    [Fact]
    public void One_column_with_header()
    {
        var l = Detect("Email\na@example.com\nb@example.com\n");
        Assert.True(l.HasHeader);
        Assert.True(l.IsSingleColumn);
        Assert.Equal("Email", l.Columns[0]);
        Assert.Equal(2, l.SampleRows.Count);
    }

    [Fact]
    public void Semicolon_delimited()
    {
        var l = Detect("First;Last;E-mail\nAnn;Lee;ann@example.com\nBo;Kim;bo@example.com\n");
        Assert.Equal(";", l.Delimiter);
        Assert.Equal(3, l.Columns.Count);
        Assert.Equal(2, l.EmailColumn);
    }

    [Fact]
    public void Tab_delimited()
    {
        var l = Detect("email\tname\na@example.com\tAnn\nb@example.com\tBo\n");
        Assert.Equal("\t", l.Delimiter);
        Assert.Equal(0, l.EmailColumn);
    }

    [Fact]
    public void Quoted_fields_with_delimiters_inside()
    {
        var l = Detect("name,email,company\n\"Lee, Ann\",ann@example.com,\"Acme, Inc.\"\n\"Kim, Bo\",bo@example.com,\"Beta, LLC\"\n");
        Assert.Equal(",", l.Delimiter);
        Assert.Equal(3, l.Columns.Count);
        Assert.Equal("Lee, Ann", l.SampleRows[0][0]);
        Assert.Equal(1, l.EmailColumn);
    }

    [Fact]
    public void Excel_export_with_bom_and_crlf()
    {
        var l = Detect("Email Address,First Name\r\nann@example.com,Ann\r\nbo@example.com,Bo\r\n", bom: true);
        Assert.True(l.HasHeader);
        Assert.Equal("Email Address", l.Columns[0]);
        Assert.Equal(0, l.EmailColumn);
    }

    [Fact]
    public void Email_header_is_used_when_too_many_values_are_invalid_for_the_90_percent_rule()
    {
        var l = Detect("Name,E-mail,City\nAnn,ann@example.com,Miami\nBo,bo@example.com,Tampa\nCy,not-an-email,Orlando\n");
        Assert.Equal(1, l.EmailColumn);
    }

    [Fact]
    public void Default_mapping_recognises_name_columns_and_keeps_others_as_custom_fields()
    {
        var l = Detect("First Name,last_name,Email,Unit Size\nAnn,Lee,ann@example.com,10x10\n");
        Assert.Equal(["first_name", "last_name", "email", "custom:Unit Size"], ContactImportService.DefaultMapping(l));
    }

    [Fact]
    public void Export_neutralises_formula_cells()
    {
        Assert.Equal("'=HYPERLINK(\"x\")", CsvExporter.Safe("=HYPERLINK(\"x\")"));
        Assert.Equal("'+1", CsvExporter.Safe("+1"));
        Assert.Equal("'-1", CsvExporter.Safe("-1"));
        Assert.Equal("'@SUM(A1)", CsvExporter.Safe("@SUM(A1)"));
        Assert.Equal("ann@example.com", CsvExporter.Safe("ann@example.com"));
        Assert.Equal("", CsvExporter.Safe(null));
    }
}

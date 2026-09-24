using DocReader.Domain;
using DocReader.Domain.Documents;
using Xunit;

namespace DocReader.UnitTests.Domain;

public sealed class EnumNamingTests
{
    [Theory]
    [InlineData(DocumentStatus.Queued, "QUEUED")]
    [InlineData(DocumentStatus.OcrRunning, "OCR_RUNNING")]
    [InlineData(DocumentStatus.Completed, "COMPLETED")]
    [InlineData(DocumentStatus.Rejected, "REJECTED")]
    public void Writes_statuses_in_upper_snake_case(DocumentStatus status, string expected)
    {
        Assert.Equal(expected, EnumNaming.ToUpperSnakeCase(status));
    }

    [Theory]
    [InlineData(UploadChannel.Api, "API")]
    [InlineData(UploadChannel.Web, "WEB")]
    public void Writes_channels_in_upper_snake_case(UploadChannel channel, string expected)
    {
        Assert.Equal(expected, EnumNaming.ToUpperSnakeCase(channel));
    }

    [Theory]
    [InlineData("OCR_RUNNING", DocumentStatus.OcrRunning)]
    [InlineData("ocr_running", DocumentStatus.OcrRunning)]
    [InlineData("OcrRunning", DocumentStatus.OcrRunning)]
    [InlineData("queued", DocumentStatus.Queued)]
    public void Parses_every_spelling_of_a_status(string value, DocumentStatus expected)
    {
        Assert.True(EnumNaming.TryParse<DocumentStatus>(value, out var parsed));
        Assert.Equal(expected, parsed);
    }

    [Theory]
    [InlineData("NOT_A_STATUS")]
    [InlineData("7")]
    [InlineData("99")]
    [InlineData("")]
    [InlineData(null)]
    public void Refuses_a_value_outside_the_enum(string? value)
    {
        Assert.False(EnumNaming.TryParse<DocumentStatus>(value, out _));
    }

    [Fact]
    public void Lists_every_member_in_wire_form()
    {
        var names = EnumNaming.NamesOf<DocumentStatus>();

        Assert.Contains("OCR_RUNNING", names);
        Assert.Contains("REJECTED", names);
        Assert.Equal(Enum.GetValues<DocumentStatus>().Length, names.Count);
    }
}

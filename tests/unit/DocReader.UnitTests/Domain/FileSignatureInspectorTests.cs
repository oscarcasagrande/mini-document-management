using DocReader.Domain.Files;
using Xunit;

namespace DocReader.UnitTests.Domain;

public sealed class FileSignatureInspectorTests
{
    [Theory]
    [InlineData(Samples.OnePagePdf, FileSignatureInspector.PdfMimeType, ".pdf")]
    [InlineData(Samples.Png, FileSignatureInspector.PngMimeType, ".png")]
    [InlineData(Samples.TwoPageTiff, FileSignatureInspector.TiffMimeType, ".tif")]
    public void Recognizes_the_accepted_formats(string sample, string expectedMimeType, string expectedExtension)
    {
        var result = FileSignatureInspector.Inspect(Samples.BytesOf(sample));

        Assert.Equal(FileInspectionOutcome.Supported, result.Outcome);
        Assert.Equal(expectedMimeType, result.MimeType);
        Assert.Equal(expectedExtension, result.Extension);
    }

    [Fact]
    public void Recognizes_jpeg_from_its_signature()
    {
        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46];

        var result = FileSignatureInspector.Inspect(jpeg);

        Assert.Equal(FileInspectionOutcome.Supported, result.Outcome);
        Assert.Equal(FileSignatureInspector.JpegMimeType, result.MimeType);
    }

    [Fact]
    public void Blocks_an_executable_wearing_a_pdf_name()
    {
        var result = FileSignatureInspector.Inspect(Samples.BytesOf(Samples.DisguisedExecutable));

        Assert.Equal(FileInspectionOutcome.Blocked, result.Outcome);
        Assert.Equal("dos-pe-executable", result.SignatureName);
        Assert.Null(result.MimeType);
    }

    [Theory]
    [InlineData(new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x14, 0x00 }, "zip-container")]
    [InlineData(new byte[] { 0x7F, 0x45, 0x4C, 0x46, 0x02, 0x01 }, "elf-executable")]
    [InlineData(new byte[] { 0x23, 0x21, 0x2F, 0x62, 0x69, 0x6E }, "script-shebang")]
    [InlineData(new byte[] { 0x1F, 0x8B, 0x08, 0x00, 0x00, 0x00 }, "gzip-archive")]
    public void Blocks_archives_and_scripts(byte[] header, string expectedSignature)
    {
        var result = FileSignatureInspector.Inspect(header);

        Assert.Equal(FileInspectionOutcome.Blocked, result.Outcome);
        Assert.Equal(expectedSignature, result.SignatureName);
    }

    [Fact]
    public void Blocks_markup_pretending_to_be_a_document()
    {
        var html = "  <!DOCTYPE html><html><body>nope</body></html>"u8.ToArray();

        var result = FileSignatureInspector.Inspect(html);

        Assert.Equal(FileInspectionOutcome.Blocked, result.Outcome);
        Assert.Equal("html-or-xml-markup", result.SignatureName);
    }

    [Fact]
    public void Reports_an_unknown_signature_as_unsupported()
    {
        var bytes = "just some plain text without any signature at all"u8.ToArray();

        var result = FileSignatureInspector.Inspect(bytes);

        Assert.Equal(FileInspectionOutcome.Unsupported, result.Outcome);
    }

    [Fact]
    public void Reports_an_empty_file_as_empty()
    {
        var result = FileSignatureInspector.Inspect([]);

        Assert.Equal(FileInspectionOutcome.Empty, result.Outcome);
        Assert.False(result.IsSupported);
    }

    [Fact]
    public void Accepts_a_pdf_marker_slightly_after_offset_zero()
    {
        var bytes = new byte[64];
        "junk"u8.CopyTo(bytes);
        "%PDF-1.4"u8.CopyTo(bytes.AsSpan(4));

        var result = FileSignatureInspector.Inspect(bytes);

        Assert.Equal(FileSignatureInspector.PdfMimeType, result.MimeType);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("application/pdf", true)]
    [InlineData("application/pdf; charset=binary", true)]
    [InlineData("application/octet-stream", true)]
    [InlineData("image/png", false)]
    [InlineData("text/plain", false)]
    public void Compares_the_declared_type_with_the_detected_one(string? declared, bool expected)
    {
        var consistent = FileSignatureInspector.IsDeclaredTypeConsistent(
            declared,
            FileSignatureInspector.PdfMimeType);

        Assert.Equal(expected, consistent);
    }

    [Theory]
    [InlineData("image/jpg")]
    [InlineData("image/pjpeg")]
    [InlineData("IMAGE/JPEG")]
    public void Tolerates_the_usual_jpeg_aliases(string declared)
    {
        Assert.True(FileSignatureInspector.IsDeclaredTypeConsistent(
            declared,
            FileSignatureInspector.JpegMimeType));
    }
}

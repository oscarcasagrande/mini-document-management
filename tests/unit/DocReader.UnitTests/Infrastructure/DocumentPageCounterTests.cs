using DocReader.Application.Errors;
using DocReader.Domain.Files;
using DocReader.Infrastructure.Files;
using Xunit;

namespace DocReader.UnitTests.Infrastructure;

public sealed class DocumentPageCounterTests
{
    private readonly DocumentPageCounter _counter = new();

    [Theory]
    [InlineData(Samples.OnePagePdf, 1)]
    [InlineData(Samples.ThreePagePdf, 3)]
    [InlineData(Samples.SixtyPagePdf, 60)]
    public async Task Counts_pdf_pages(string sample, int expected)
    {
        await using var content = Samples.StreamOf(sample);

        var pages = await _counter.CountPagesAsync(content, FileSignatureInspector.PdfMimeType, TestContext.Current.CancellationToken);

        Assert.Equal(expected, pages);
    }

    [Fact]
    public async Task Counts_every_directory_of_a_multipage_tiff()
    {
        await using var content = Samples.StreamOf(Samples.TwoPageTiff);

        var pages = await _counter.CountPagesAsync(content, FileSignatureInspector.TiffMimeType, TestContext.Current.CancellationToken);

        Assert.Equal(2, pages);
    }

    [Theory]
    [InlineData(FileSignatureInspector.PngMimeType)]
    [InlineData(FileSignatureInspector.JpegMimeType)]
    public async Task Treats_a_single_image_as_one_page(string mimeType)
    {
        await using var content = Samples.StreamOf(Samples.Png);

        var pages = await _counter.CountPagesAsync(content, mimeType, TestContext.Current.CancellationToken);

        Assert.Equal(1, pages);
    }

    [Fact]
    public async Task Reports_a_corrupted_pdf_as_unreadable()
    {
        await using var content = Samples.StreamOf(Samples.CorruptedPdf);

        await Assert.ThrowsAsync<UnreadableDocumentException>(() =>
            _counter.CountPagesAsync(content, FileSignatureInspector.PdfMimeType, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Restores_the_stream_position()
    {
        await using var content = Samples.StreamOf(Samples.OnePagePdf);
        content.Position = 3;

        await _counter.CountPagesAsync(content, FileSignatureInspector.PdfMimeType, TestContext.Current.CancellationToken);

        Assert.Equal(3, content.Position);
    }

    [Fact]
    public async Task Refuses_a_tiff_whose_directory_chain_points_past_the_end()
    {
        byte[] broken = [0x49, 0x49, 0x2A, 0x00, 0xFF, 0xFF, 0x00, 0x00, 0x00, 0x00];
        await using var content = new MemoryStream(broken, writable: false);

        await Assert.ThrowsAsync<UnreadableDocumentException>(() =>
            _counter.CountPagesAsync(content, FileSignatureInspector.TiffMimeType, TestContext.Current.CancellationToken));
    }
}

using DocReader.Application.Abstractions;
using DocReader.Application.Errors;
using DocReader.Domain.Files;
using UglyToad.PdfPig;

namespace DocReader.Infrastructure.Files;

/// <summary>
/// Counts pages of the accepted formats so the page limit of RF-002 can be enforced before the
/// document is queued. PDFs are parsed by PdfPig; multi page TIFFs are counted by walking the
/// directory chain; PNG and JPEG are single page by definition.
/// </summary>
public sealed class DocumentPageCounter : IPageCounter
{
    /// <summary>Upper bound on the TIFF directory walk, a guard against a malformed chain.</summary>
    private const int MaxTiffDirectories = 10_000;

    public Task<int> CountPagesAsync(Stream content, string mimeType, CancellationToken ct)
    {
        var originalPosition = content.CanSeek ? content.Position : 0;

        try
        {
            var pageCount = mimeType switch
            {
                FileSignatureInspector.PdfMimeType => CountPdfPages(content),
                FileSignatureInspector.TiffMimeType => CountTiffPages(content),
                FileSignatureInspector.PngMimeType or FileSignatureInspector.JpegMimeType => 1,
                _ => throw new UnreadableDocumentException($"Cannot count pages of {mimeType}.")
            };

            return Task.FromResult(pageCount);
        }
        finally
        {
            if (content.CanSeek)
            {
                content.Position = originalPosition;
            }
        }
    }

    private static int CountPdfPages(Stream content)
    {
        content.Position = 0;

        try
        {
            using var document = PdfDocument.Open(content, new ParsingOptions { UseLenientParsing = true });
            return document.NumberOfPages > 0
                ? document.NumberOfPages
                : throw new UnreadableDocumentException("The PDF reports no pages.");
        }
        catch (UnreadableDocumentException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new UnreadableDocumentException(
                "The PDF could not be parsed. It may be corrupted or password protected.",
                exception);
        }
    }

    /// <summary>
    /// Walks the TIFF image file directory chain. Each directory is one page, and the chain ends at
    /// a zero offset.
    /// </summary>
    private static int CountTiffPages(Stream content)
    {
        content.Position = 0;

        var header = new byte[8];
        ReadExactly(content, header, 8);

        var isLittleEndian = header[0] == 0x49;
        var version = ReadUInt16(header.AsSpan(2, 2), isLittleEndian);

        return version switch
        {
            42 => CountClassicTiffDirectories(content, header, isLittleEndian),
            43 => CountBigTiffDirectories(content, isLittleEndian),
            _ => throw new UnreadableDocumentException("The TIFF header has an unknown version.")
        };
    }

    private static int CountClassicTiffDirectories(Stream content, byte[] header, bool isLittleEndian)
    {
        var offset = ReadUInt32(header.AsSpan(4, 4), isLittleEndian);
        var visited = new HashSet<ulong>();
        var pages = 0;
        var buffer = new byte[4];

        while (offset != 0)
        {
            if (!visited.Add(offset) || pages >= MaxTiffDirectories)
            {
                throw new UnreadableDocumentException("The TIFF directory chain is malformed.");
            }

            Seek(content, offset);

            ReadExactly(content, buffer, 2);
            var entryCount = ReadUInt16(buffer.AsSpan(0, 2), isLittleEndian);

            Seek(content, offset + 2 + ((ulong)entryCount * 12));

            ReadExactly(content, buffer, 4);
            offset = ReadUInt32(buffer.AsSpan(0, 4), isLittleEndian);
            pages++;
        }

        return pages > 0 ? pages : throw new UnreadableDocumentException("The TIFF has no pages.");
    }

    private static int CountBigTiffDirectories(Stream content, bool isLittleEndian)
    {
        Seek(content, 8);

        var buffer = new byte[8];
        ReadExactly(content, buffer, 8);
        var offset = ReadUInt64(buffer, isLittleEndian);

        var visited = new HashSet<ulong>();
        var pages = 0;

        while (offset != 0)
        {
            if (!visited.Add(offset) || pages >= MaxTiffDirectories)
            {
                throw new UnreadableDocumentException("The TIFF directory chain is malformed.");
            }

            Seek(content, offset);

            ReadExactly(content, buffer, 8);
            var entryCount = ReadUInt64(buffer, isLittleEndian);

            Seek(content, offset + 8 + (entryCount * 20));

            ReadExactly(content, buffer, 8);
            offset = ReadUInt64(buffer, isLittleEndian);
            pages++;
        }

        return pages > 0 ? pages : throw new UnreadableDocumentException("The TIFF has no pages.");
    }

    private static void Seek(Stream content, ulong offset)
    {
        if (offset > long.MaxValue || (long)offset >= content.Length)
        {
            throw new UnreadableDocumentException("The TIFF points past the end of the file.");
        }

        content.Position = (long)offset;
    }

    private static void ReadExactly(Stream content, byte[] buffer, int count)
    {
        try
        {
            content.ReadExactly(buffer, 0, count);
        }
        catch (EndOfStreamException exception)
        {
            throw new UnreadableDocumentException("The image ends before its structure is complete.", exception);
        }
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> bytes, bool isLittleEndian) =>
        isLittleEndian
            ? System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(bytes)
            : System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(bytes);

    private static uint ReadUInt32(ReadOnlySpan<byte> bytes, bool isLittleEndian) =>
        isLittleEndian
            ? System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes)
            : System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes);

    private static ulong ReadUInt64(ReadOnlySpan<byte> bytes, bool isLittleEndian) =>
        isLittleEndian
            ? System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(bytes)
            : System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(bytes);
}

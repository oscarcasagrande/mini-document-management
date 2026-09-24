using System.Text;

namespace DocReader.Domain.Files;

/// <summary>
/// Detects the real format of an upload from its leading bytes. The declared
/// <c>Content-Type</c> is never trusted (PRD section 21).
/// </summary>
public static class FileSignatureInspector
{
    /// <summary>Number of leading bytes the inspector needs.</summary>
    public const int HeaderSize = 1024;

    public const string PdfMimeType = "application/pdf";
    public const string PngMimeType = "image/png";
    public const string JpegMimeType = "image/jpeg";
    public const string TiffMimeType = "image/tiff";

    private static readonly byte[] PdfMagic = Encoding.ASCII.GetBytes("%PDF-");
    private static readonly byte[] PngMagic = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly byte[] JpegMagic = [0xFF, 0xD8, 0xFF];
    private static readonly byte[] TiffLittleEndian = [0x49, 0x49, 0x2A, 0x00];
    private static readonly byte[] TiffBigEndian = [0x4D, 0x4D, 0x00, 0x2A];
    private static readonly byte[] BigTiffLittleEndian = [0x49, 0x49, 0x2B, 0x00];
    private static readonly byte[] BigTiffBigEndian = [0x4D, 0x4D, 0x00, 0x2B];

    private static readonly (byte[] Magic, string Name)[] BlockedSignatures =
    [
        ([0x4D, 0x5A], "dos-pe-executable"),
        ([0x7F, 0x45, 0x4C, 0x46], "elf-executable"),
        ([0xCA, 0xFE, 0xBA, 0xBE], "mach-o-or-java-class"),
        ([0xFE, 0xED, 0xFA, 0xCE], "mach-o-executable"),
        ([0xCF, 0xFA, 0xED, 0xFE], "mach-o-executable"),
        ([0x50, 0x4B, 0x03, 0x04], "zip-container"),
        ([0x50, 0x4B, 0x05, 0x06], "zip-container"),
        ([0x50, 0x4B, 0x07, 0x08], "zip-container"),
        ([0x52, 0x61, 0x72, 0x21], "rar-archive"),
        ([0x37, 0x7A, 0xBC, 0xAF], "7z-archive"),
        ([0x1F, 0x8B], "gzip-archive"),
        ([0x4D, 0x53, 0x43, 0x46], "cab-archive"),
        ([0xD0, 0xCF, 0x11, 0xE0], "ole-compound-file"),
        ([0x23, 0x21], "script-shebang")
    ];

    /// <summary>
    /// Inspects the header of an upload. Only the formats accepted by RF-002 are supported.
    /// </summary>
    public static FileInspectionResult Inspect(ReadOnlySpan<byte> header)
    {
        if (header.IsEmpty)
        {
            return new FileInspectionResult(FileInspectionOutcome.Empty, null, null, null);
        }

        foreach (var (magic, name) in BlockedSignatures)
        {
            if (header.StartsWith(magic))
            {
                return new FileInspectionResult(FileInspectionOutcome.Blocked, null, null, name);
            }
        }

        if (header.StartsWith(PngMagic))
        {
            return new FileInspectionResult(FileInspectionOutcome.Supported, PngMimeType, ".png", "png");
        }

        if (header.StartsWith(JpegMagic))
        {
            return new FileInspectionResult(FileInspectionOutcome.Supported, JpegMimeType, ".jpg", "jpeg");
        }

        if (header.StartsWith(TiffLittleEndian) || header.StartsWith(TiffBigEndian) ||
            header.StartsWith(BigTiffLittleEndian) || header.StartsWith(BigTiffBigEndian))
        {
            return new FileInspectionResult(FileInspectionOutcome.Supported, TiffMimeType, ".tif", "tiff");
        }

        // PDF readers tolerate a few junk bytes before the header, so the marker is searched for
        // in the first block instead of being required at offset zero.
        if (header.IndexOf(PdfMagic) >= 0)
        {
            return new FileInspectionResult(FileInspectionOutcome.Supported, PdfMimeType, ".pdf", "pdf");
        }

        if (LooksLikeMarkup(header))
        {
            return new FileInspectionResult(FileInspectionOutcome.Blocked, null, null, "html-or-xml-markup");
        }

        return new FileInspectionResult(FileInspectionOutcome.Unsupported, null, null, null);
    }

    /// <summary>
    /// True when the declared MIME type is consistent with the detected one. A client may omit the
    /// type or send a generic one; only an explicit mismatch is rejected.
    /// </summary>
    public static bool IsDeclaredTypeConsistent(string? declaredMimeType, string detectedMimeType)
    {
        if (string.IsNullOrWhiteSpace(declaredMimeType))
        {
            return true;
        }

        var declared = declaredMimeType.Split(';')[0].Trim();

        if (declared.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase) ||
            declared.Equals("*/*", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (declared.Equals(detectedMimeType, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // image/jpg and image/tif are common aliases emitted by browsers and scanners.
        return (detectedMimeType, declared.ToLowerInvariant()) switch
        {
            (JpegMimeType, "image/jpg") => true,
            (JpegMimeType, "image/pjpeg") => true,
            (TiffMimeType, "image/tif") => true,
            (TiffMimeType, "image/x-tiff") => true,
            (PngMimeType, "image/x-png") => true,
            _ => false
        };
    }

    private static bool LooksLikeMarkup(ReadOnlySpan<byte> header)
    {
        var length = Math.Min(header.Length, 64);
        for (var index = 0; index < length; index++)
        {
            var current = header[index];
            if (current is 0x20 or 0x09 or 0x0D or 0x0A or 0xEF or 0xBB or 0xBF)
            {
                continue;
            }

            return current == 0x3C;
        }

        return false;
    }
}

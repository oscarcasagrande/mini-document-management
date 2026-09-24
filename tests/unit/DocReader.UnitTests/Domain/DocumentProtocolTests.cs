using DocReader.Domain.Documents;
using Xunit;

namespace DocReader.UnitTests.Domain;

public sealed class DocumentProtocolTests
{
    [Theory]
    [InlineData(1, "DOC-20260924-000001")]
    [InlineData(42, "DOC-20260924-000042")]
    [InlineData(999_999, "DOC-20260924-999999")]
    [InlineData(1_000_000, "DOC-20260924-1000000")]
    public void Formats_the_daily_sequence_with_six_digits(long sequence, string expected)
    {
        var protocol = DocumentProtocol.Format(new DateOnly(2026, 9, 24), sequence);

        Assert.Equal(expected, protocol);
    }

    [Fact]
    public void Refuses_a_non_positive_sequence()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => DocumentProtocol.Format(new DateOnly(2026, 9, 24), 0));
    }

    [Theory]
    [InlineData("DOC-20260924-000001", true)]
    [InlineData("DOC-20260924-1000000", true)]
    [InlineData("DOC-20261332-000001", false)]
    [InlineData("doc-20260924-000001", false)]
    [InlineData("DOC-20260924-00001", false)]
    [InlineData("DOC-20260924-00000A", false)]
    [InlineData("DOC20260924000001", false)]
    [InlineData("DOC-20260924-000001; DROP TABLE documents", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Validates_the_shape_before_it_reaches_a_query(string? candidate, bool expected)
    {
        Assert.Equal(expected, DocumentProtocol.IsWellFormed(candidate));
    }
}

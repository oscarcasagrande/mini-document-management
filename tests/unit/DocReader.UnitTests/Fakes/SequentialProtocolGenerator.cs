using DocReader.Application.Abstractions;
using DocReader.Domain.Documents;

namespace DocReader.UnitTests.Fakes;

/// <summary>Deterministic protocol generator, so assertions can name the expected protocol.</summary>
public sealed class SequentialProtocolGenerator : IProtocolGenerator
{
    private long _sequence;

    public Task<string> NextAsync(DateTimeOffset uploadedAt, CancellationToken ct)
    {
        var next = Interlocked.Increment(ref _sequence);

        return Task.FromResult(DocumentProtocol.Format(DateOnly.FromDateTime(uploadedAt.UtcDateTime), next));
    }
}

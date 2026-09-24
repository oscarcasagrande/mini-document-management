using Microsoft.Extensions.Options;

namespace DocReader.Application.Options;

/// <summary>
/// Rejects at startup a combination that would make healthy workers steal each other's jobs: a lock
/// timeout that a single slow page can outlast.
/// </summary>
public sealed class ProcessingOptionsValidator(IOptions<OcrProviderOptions> ocrOptions)
    : IValidateOptions<ProcessingQueueOptions>
{
    public ValidateOptionsResult Validate(string? name, ProcessingQueueOptions options)
    {
        var pageTimeout = ocrOptions.Value.PageTimeout;

        if (options.JobLockTimeout <= pageTimeout)
        {
            return ValidateOptionsResult.Fail(
                $"DocReader:Queue:JobLockTimeout ({options.JobLockTimeout}) must be greater than " +
                $"DocReader:Ocr:PageTimeout ({pageTimeout}); otherwise one slow page makes a healthy job look stuck.");
        }

        if (options.ProcessingTimeout <= options.JobLockTimeout)
        {
            return ValidateOptionsResult.Fail(
                "DocReader:Queue:ProcessingTimeout must be greater than DocReader:Queue:JobLockTimeout.");
        }

        return ValidateOptionsResult.Success;
    }
}

using DocReader.Application.Options;
using Microsoft.Extensions.Options;
using Xunit;

namespace DocReader.UnitTests.Application;

/// <summary>
/// A lock timeout that one slow page can outlast would make healthy workers steal each other's jobs.
/// The options must refuse that combination at startup instead of failing at 3 a.m.
/// </summary>
public sealed class ProcessingOptionsValidatorTests
{
    private static ValidateOptionsResult Validate(ProcessingQueueOptions queue, OcrProviderOptions? ocr = null) =>
        new ProcessingOptionsValidator(Options.Create(ocr ?? new OcrProviderOptions())).Validate(null, queue);

    [Fact]
    public void Valores_padrao_sao_coerentes()
    {
        Assert.True(Validate(new ProcessingQueueOptions()).Succeeded);
    }

    [Fact]
    public void Timeout_da_reserva_menor_que_o_de_uma_pagina_e_recusado()
    {
        var queue = new ProcessingQueueOptions { JobLockTimeout = TimeSpan.FromSeconds(60) };
        var ocr = new OcrProviderOptions { PageTimeout = TimeSpan.FromSeconds(150) };

        var result = Validate(queue, ocr);

        Assert.True(result.Failed);
        Assert.Contains("JobLockTimeout", result.FailureMessage);
    }

    [Fact]
    public void Timeout_da_reserva_igual_ao_de_uma_pagina_tambem_e_recusado()
    {
        var queue = new ProcessingQueueOptions { JobLockTimeout = TimeSpan.FromSeconds(150) };
        var ocr = new OcrProviderOptions { PageTimeout = TimeSpan.FromSeconds(150) };

        Assert.True(Validate(queue, ocr).Failed);
    }

    [Fact]
    public void Teto_de_uma_tentativa_menor_que_a_reserva_e_recusado()
    {
        var queue = new ProcessingQueueOptions
        {
            JobLockTimeout = TimeSpan.FromMinutes(5),
            ProcessingTimeout = TimeSpan.FromMinutes(4)
        };

        Assert.True(Validate(queue).Failed);
    }
}

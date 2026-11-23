using System.Data.Common;
using System.Text.Json;
using Erp.Kernel.Application.Documents;
using Erp.Kernel.Contracts.Documents;
using Erp.Kernel.Contracts.Jobs;
using Erp.Kernel.Infrastructure.Documents.Templates;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.UnitTests.Documents;

/// <summary>The batch job of slice S1.11c2 (KRN-DOC-05, ruling Q27): many documents of one template, one failed record does not stop the rest.</summary>
public sealed class GenerateBatchJobTests
{
    private sealed class FakeTemplates(HashSet<Guid> failing) : ITemplateService
    {
        public List<Guid> Made { get; } = [];

        public Task<GeneratedDocumentInfo> GenerateAsync(GenerateRequest request, CancellationToken cancellationToken)
        {
            if (failing.Contains(request.OwnerId))
            {
                throw new DocumentException(DocumentErrors.NotFound, 404);
            }

            Made.Add(request.OwnerId);
            return Task.FromResult(new GeneratedDocumentInfo(Guid.NewGuid(), Guid.NewGuid(), "t", request.OwnerId, Guid.NewGuid(), "pdf", null, DateTimeOffset.UnixEpoch));
        }

        public Task<TemplateView> CreateAsync(TemplateCreate create, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<TemplateVersionView>> VersionsAsync(Guid templateId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<TemplateVersionView> NewVersionAsync(Guid templateId, TemplateVersionEdit edit, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<TemplateVersionView> UpdateDraftAsync(Guid versionId, TemplateVersionEdit edit, long rowVersion, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<TemplateVersionView> ActivateAsync(Guid versionId, long rowVersion, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<TemplateView> RetireAsync(Guid templateId, long rowVersion, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<TemplatePreview> PreviewAsync(TemplatePreviewRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<GeneratedDocumentInfo>> GeneratedAsync(string ownerType, Guid ownerId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<TemplateEntityFields>> EntityFieldsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FakeRun(JsonElement parameters, IServiceProvider services) : IJobExecution
    {
        public List<(string Level, string Message)> Log { get; } = [];

        public Guid RunId => Guid.NewGuid();

        public string Code => GenerateBatchJob.JobCode;

        public Guid? CompanyId => null;

        public JsonElement Parameters => parameters;

        public JsonElement? Checkpoint => null;

        public int Attempt => 1;

        public DateTimeOffset? ScheduledFor => null;

        public IServiceProvider Services => services;

        public Task CommitChunkAsync(Func<DbConnection, DbTransaction, CancellationToken, Task>? work, object? checkpoint, decimal? progressPct, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task LogAsync(string level, string message, CancellationToken cancellationToken)
        {
            Log.Add((level, message));
            return Task.CompletedTask;
        }
    }

    private static (FakeRun Run, FakeTemplates Templates) Run(object parameters, params Guid[] failing)
    {
        var templates = new FakeTemplates([.. failing]);
        var services = new ServiceCollection().AddSingleton<ITemplateService>(templates).BuildServiceProvider();
        return (new FakeRun(JsonSerializer.SerializeToElement(parameters), services), templates);
    }

    [Fact]
    public async Task S111c2_KRN_DOC_05_a_batch_makes_a_document_for_every_record()
    {
        var owners = Enumerable.Range(0, 3).Select(_ => Guid.NewGuid()).ToArray();
        var (run, templates) = Run(new { templateId = Guid.NewGuid(), ownerIds = owners });
        await new GenerateBatchJob().RunAsync(run, TestContext.Current.CancellationToken);
        Assert.Equal(owners, templates.Made);
        Assert.Equal("3 documents made, 0 failed", run.Log.Single().Message);
    }

    [Fact]
    public async Task S111c2_KRN_DOC_05_a_record_that_fails_is_logged_the_rest_are_done_and_the_run_ends_failed()
    {
        var owners = Enumerable.Range(0, 3).Select(_ => Guid.NewGuid()).ToArray();
        var (run, templates) = Run(new { templateId = Guid.NewGuid(), ownerIds = owners }, owners[1]);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => new GenerateBatchJob().RunAsync(run, TestContext.Current.CancellationToken));
        Assert.Contains("1 of 3", ex.Message, StringComparison.Ordinal);
        Assert.Equal([owners[0], owners[2]], templates.Made);
        Assert.Contains(run.Log, l => l.Level == "warning" && l.Message.Contains(owners[1].ToString(), StringComparison.Ordinal));
    }

    [Fact]
    public async Task S111c2_KRN_DOC_05_a_batch_without_the_parameters_or_with_too_many_records_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        await Assert.ThrowsAsync<InvalidOperationException>(() => new GenerateBatchJob().RunAsync(Run(new { }).Run, ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new GenerateBatchJob().RunAsync(Run(new { templateId = Guid.NewGuid(), ownerIds = Array.Empty<Guid>() }).Run, ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new GenerateBatchJob().RunAsync(Run(new { templateId = Guid.NewGuid(), ownerIds = Enumerable.Range(0, GenerateBatchJob.MaxRecords + 1).Select(_ => Guid.NewGuid()) }).Run, ct));
    }
}

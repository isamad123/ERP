using System.Globalization;
using System.Text.Json;
using Erp.Kernel.Application.Documents;
using Erp.Kernel.Contracts.Documents;
using Erp.Kernel.Contracts.Jobs;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.Kernel.Infrastructure.Documents.Templates;

/// <summary>
/// Produces one template's document for many records, as the person who asked (slice S1.11c2, ruling Q27: batches are jobs, single
/// documents are made in the request). Parameters: <c>templateId</c> and <c>ownerIds</c>. A record that cannot be done is logged
/// and the rest go on; the run ends failed when any record failed. It is never retried, so no document is made twice.
/// </summary>
public sealed class GenerateBatchJob : IJobHandler
{
    public const string JobCode = "kernel.doc_generate_batch";

    /// <summary>Most records one run takes.</summary>
    public const int MaxRecords = 5000;

    public string Code => JobCode;

    public async Task RunAsync(IJobExecution job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        var p = job.Parameters;
        if (p.ValueKind != JsonValueKind.Object || !p.TryGetProperty("templateId", out var t) || !t.TryGetGuid(out var templateId) || !p.TryGetProperty("ownerIds", out var o) || o.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("The run needs the parameters templateId and ownerIds.");
        }

        var owners = o.EnumerateArray().Select(e => e.TryGetGuid(out var id) ? id : Guid.Empty).Distinct().ToList();
        if (owners.Count == 0 || owners.Count > MaxRecords || owners.Contains(Guid.Empty))
        {
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"ownerIds must list between 1 and {MaxRecords} record ids."));
        }

        var service = job.Services.GetRequiredService<ITemplateService>();
        var done = 0;
        var failed = 0;
        foreach (var owner in owners)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await service.GenerateAsync(new GenerateRequest(templateId, owner), cancellationToken).ConfigureAwait(false);
                done++;
            }
            catch (DocumentException e)
            {
                failed++;
                await job.LogAsync("warning", string.Create(CultureInfo.InvariantCulture, $"Record {owner}: {e.Message}"), cancellationToken).ConfigureAwait(false);
            }
        }

        await job.LogAsync(failed > 0 ? "warning" : "info", string.Create(CultureInfo.InvariantCulture, $"{done} documents made, {failed} failed"), cancellationToken).ConfigureAwait(false);
        if (failed > 0)
        {
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"{failed} of {owners.Count} documents could not be made; see the run log."));
        }
    }
}

using System.Data.Common;
using Erp.Kernel.Contracts.Workflow;
using Erp.Kernel.Infrastructure.Workflow;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.UnitTests.Workflow;

/// <summary>
/// Review S1.10a finding 4 (CR-059): the workflow set-up is checked when a host starts. A document type without exactly
/// one outcome handler, or a wrong manifest, stops the host instead of failing requests at their final decision.
/// </summary>
public sealed class WorkflowStartupCheckTests
{
    private const string Manifest = """
        {"module": "demo", "document_types": [
          {"code": "demo.claim", "entitlement_key": "platform.base", "name": {"en": "Claim", "ur": "دعویٰ"},
           "fields": [{"name": "company", "type": "company", "label": {"en": "Company", "ur": "کمپنی"}}],
           "company_field": "company", "subject_fields": [], "title": {"en": "Claim", "ur": "دعویٰ"}, "link": "/demo/{document_id}",
           "default_route": {"steps": [{"key": "admin", "name": {"en": "Administrator", "ur": "منتظم"}, "approver": {"kind": "role", "role": "tenant_admin"}}]}}
        ]}
        """;

    [Fact]
    public void FR_KRN_WFL_01_CR059_every_document_type_needs_exactly_one_outcome_handler()
    {
        Assert.Empty(WorkflowCatalog.HandlerProblems(["demo.claim", "demo.loan"], ["demo.loan", "demo.claim", "other.type"]));
        Assert.Equal(["demo.claim: no outcome handler is registered"], WorkflowCatalog.HandlerProblems(["demo.claim"], []));
        Assert.Equal(["demo.claim: 2 outcome handlers are registered (one is allowed)"], WorkflowCatalog.HandlerProblems(["demo.claim"], ["demo.claim", "demo.claim"]));
    }

    [Fact]
    public void FR_KRN_WFL_01_CR059_a_host_with_a_document_type_but_no_handler_does_not_start()
    {
        using var missing = Provider(withHandler: false);
        var refused = Assert.Throws<InvalidDataException>(missing.VerifyWorkflow);
        Assert.Contains("demo.claim: no outcome handler is registered", refused.Message, StringComparison.Ordinal);

        using var complete = Provider(withHandler: true);
        complete.VerifyWorkflow();
    }

    [Fact]
    public void FR_KRN_WFL_01_CR059_a_wrong_manifest_stops_the_host_when_it_starts_not_at_the_first_request()
    {
        var services = new ServiceCollection();
        services.AddKernelWorkflow();
        services.AddSingleton<IWorkflowDocumentTypeSource>(new WorkflowManifestSource(Manifest.Replace("\"company_field\": \"company\"", "\"company_field\": \"nothing\"", StringComparison.Ordinal)));
        using var provider = services.BuildServiceProvider();
        Assert.Throws<InvalidDataException>(provider.VerifyWorkflow);
    }

    private static ServiceProvider Provider(bool withHandler)
    {
        var services = new ServiceCollection();
        services.AddKernelWorkflow();
        services.AddSingleton<IWorkflowDocumentTypeSource>(new WorkflowManifestSource(Manifest));
        if (withHandler)
        {
            services.AddScoped<IWorkflowOutcomeHandler, ClaimHandler>();
        }

        return services.BuildServiceProvider();
    }

    private sealed class ClaimHandler : IWorkflowOutcomeHandler
    {
        public string DocumentType => "demo.claim";

        public Task OnOutcomeAsync(WorkflowOutcome outcome, DbTransaction transaction, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}

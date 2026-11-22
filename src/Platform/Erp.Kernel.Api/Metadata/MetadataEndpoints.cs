using Erp.BuildingBlocks.Application.Security;
using Erp.BuildingBlocks.Contracts.Api;
using Erp.Kernel.Contracts.Identity;
using Erp.Kernel.Contracts.Metadata;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Erp.Kernel.Api.Metadata;

/// <summary>
/// Label overrides, option lists and custom-field definitions (slice S1.15b; contracts "Label administration API", "Option list
/// API", "Custom field API", "Custom field form API"). The generated writes of the four tables are closed; reads of the tables
/// stay generated. Error titles are localization keys (<c>Metadata/Localization/metadata.*.json</c>). The tenant-isolation
/// suite probes these as access-control endpoints.
/// </summary>
public static class MetadataEndpoints
{
    public static IEndpointRouteBuilder MapMetadataEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        MapLabels(app);
        MapOptionLists(app);
        MapCustomFields(app);
        return app;
    }

    private static void MapLabels(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/cfg/label-overrides").WithTags("cfg.labels");
        Tag(g.MapGet("/", (ILabelAdministration s, IPermissionChecker p, CancellationToken ct) =>
            RunAsync(p, MetadataPermissions.LabelsRead, async () => Results.Ok(await s.ListAsync(ct).ConfigureAwait(false)), ct)),
            "catalog", "cfg.labels", MetadataPermissions.LabelsRead);
        Tag(g.MapPut("/", (LabelOverrideWrite body, ILabelAdministration s, IPermissionChecker p, CancellationToken ct) =>
            RunAsync(p, MetadataPermissions.LabelsUpdate, async () => Results.Ok(await s.SetAsync(body, ct).ConfigureAwait(false)), ct)),
            "create", "cfg.labels", MetadataPermissions.LabelsUpdate);
    }

    private static void MapOptionLists(IEndpointRouteBuilder app)
    {
        const string lists = "cfg.option_lists";
        var g = app.MapGroup("/api/cfg/option-lists").WithTags(lists);
        Tag(g.MapGet("/", (IOptionListAdministration s, IPermissionChecker p, CancellationToken ct) =>
            RunAsync(p, MetadataPermissions.OptionListsRead, async () => Results.Ok(new { items = await s.ListAsync(ct).ConfigureAwait(false) }), ct)),
            "catalog", lists, MetadataPermissions.OptionListsRead);
        Tag(g.MapGet("/{id:guid}", (Guid id, IOptionListAdministration s, IPermissionChecker p, CancellationToken ct) =>
            RunAsync(p, MetadataPermissions.OptionListsRead, async () => await s.GetAsync(id, ct).ConfigureAwait(false) is { } detail ? Results.Ok(detail) : NotFound(), ct)),
            "record_action", lists, MetadataPermissions.OptionListsRead);
        Tag(g.MapPost("/", (OptionListCreate body, IOptionListAdministration s, IPermissionChecker p, CancellationToken ct) =>
            RunAsync(p, MetadataPermissions.OptionListsCreate, async () =>
            {
                var detail = await s.CreateAsync(body, ct).ConfigureAwait(false);
                return Results.Created("/api/cfg/option-lists/" + detail.Id, detail);
            }, ct)),
            "create", lists, MetadataPermissions.OptionListsCreate);
        Tag(g.MapPut("/{id:guid}", (Guid id, OptionListEdit body, IOptionListAdministration s, IPermissionChecker p, CancellationToken ct) =>
            RunAsync(p, MetadataPermissions.OptionListsUpdate, async () => Results.Ok(await s.EditAsync(id, body, ct).ConfigureAwait(false)), ct)),
            "record_action", lists, MetadataPermissions.OptionListsUpdate);
        Tag(g.MapPost("/{id:guid}/items", (Guid id, OptionItemCreate body, IOptionListAdministration s, IPermissionChecker p, CancellationToken ct) =>
            RunAsync(p, MetadataPermissions.OptionItemsCreate, async () =>
            {
                var detail = await s.AddItemAsync(id, body, ct).ConfigureAwait(false);
                return Results.Created("/api/cfg/option-lists/" + id, detail);
            }, ct)),
            "record_action", lists, MetadataPermissions.OptionItemsCreate);
        Tag(g.MapPut("/{id:guid}/items/{itemId:guid}", (Guid id, Guid itemId, OptionItemEdit body, IOptionListAdministration s, IPermissionChecker p, CancellationToken ct) =>
            RunAsync(p, MetadataPermissions.OptionItemsUpdate, async () => Results.Ok(await s.EditItemAsync(id, itemId, body, ct).ConfigureAwait(false)), ct)),
            "record_action", lists, MetadataPermissions.OptionItemsUpdate);
        Tag(g.MapPost("/{id:guid}/items/order", (Guid id, OptionOrder body, IOptionListAdministration s, IPermissionChecker p, CancellationToken ct) =>
            RunAsync(p, MetadataPermissions.OptionItemsUpdate, async () => Results.Ok(await s.OrderAsync(id, body, ct).ConfigureAwait(false)), ct)),
            "record_action", lists, MetadataPermissions.OptionItemsUpdate);
    }

    private static void MapCustomFields(IEndpointRouteBuilder app)
    {
        const string defs = "cfg.custom_field_defs";
        var g = app.MapGroup("/api/cfg").WithTags(defs);
        Tag(g.MapGet("/custom-field-entities", (ICustomFieldAdministration s, IPermissionChecker p, CancellationToken ct) =>
            RunAsync(p, MetadataPermissions.CustomFieldsRead, async () => Results.Ok(await s.EntitiesAsync(ct).ConfigureAwait(false)), ct)),
            "catalog", defs, MetadataPermissions.CustomFieldsRead);
        Tag(g.MapGet("/custom-fields", ([FromQuery] string? entityType, ICustomFieldAdministration s, IPermissionChecker p, CancellationToken ct) =>
            RunAsync(p, MetadataPermissions.CustomFieldsRead, async () => Results.Ok(await s.ListAsync(entityType, ct).ConfigureAwait(false)), ct)),
            "catalog", defs, MetadataPermissions.CustomFieldsRead);
        Tag(g.MapPost("/custom-fields", (CustomFieldCreate body, ICustomFieldAdministration s, IPermissionChecker p, CancellationToken ct) =>
            RunAsync(p, MetadataPermissions.CustomFieldsCreate, async () =>
            {
                var created = await s.CreateAsync(body, ct).ConfigureAwait(false);
                return Results.Created("/api/cfg/custom-fields/" + created.Id, created);
            }, ct)),
            "create", defs, MetadataPermissions.CustomFieldsCreate);
        Tag(g.MapPut("/custom-fields/{id:guid}", (Guid id, CustomFieldEdit body, ICustomFieldAdministration s, IPermissionChecker p, CancellationToken ct) =>
            RunAsync(p, MetadataPermissions.CustomFieldsUpdate, async () => Results.Ok(await s.EditAsync(id, body, ct).ConfigureAwait(false)), ct)),
            "record_action", defs, MetadataPermissions.CustomFieldsUpdate);
        Tag(g.MapPost("/custom-fields/{id:guid}/retire", (Guid id, CustomFieldState body, ICustomFieldAdministration s, IPermissionChecker p, CancellationToken ct) =>
            RunAsync(p, MetadataPermissions.CustomFieldsUpdate, async () => Results.Ok(await s.SetStateAsync(id, body, restore: false, ct).ConfigureAwait(false)), ct)),
            "record_action", defs, MetadataPermissions.CustomFieldsUpdate);
        Tag(g.MapPost("/custom-fields/{id:guid}/restore", (Guid id, CustomFieldState body, ICustomFieldAdministration s, IPermissionChecker p, CancellationToken ct) =>
            RunAsync(p, MetadataPermissions.CustomFieldsUpdate, async () => Results.Ok(await s.SetStateAsync(id, body, restore: true, ct).ConfigureAwait(false)), ct)),
            "record_action", defs, MetadataPermissions.CustomFieldsUpdate);

        // The fields a record form shows: for anyone who may read the table (checked in the service), so no administrator permission.
        app.MapGet("/api/kernel/custom-fields/{entityType}", async (string entityType, [FromQuery] Guid? companyId, ICustomFieldAdministration s, IPermissionChecker p, CancellationToken ct) =>
            await p.HasPermissionAsync(IdentityPermissions.SelfManage, ct).ConfigureAwait(false)
                ? await s.FormAsync(entityType, companyId, ct).ConfigureAwait(false) is { } form ? Results.Ok(form) : Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "errors.data.not_found")
                : Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "errors.access.forbidden"))
            .WithTags("kernel.custom_fields")
            .WithMetadata(new IdentityEndpointMetadata("self", IdentityPermissions.SelfManage, AllowedWhenReadOnly: true));
    }

    private static async Task<IResult> RunAsync(IPermissionChecker permissions, string permission, Func<Task<IResult>> action, CancellationToken cancellationToken)
    {
        if (!await permissions.HasPermissionAsync(permission, cancellationToken).ConfigureAwait(false))
        {
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "errors.access.forbidden");
        }

        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (MetadataRefusedException refusal)
        {
            return refusal.Field is { } field
                ? Results.ValidationProblem(new Dictionary<string, string[]> { [field] = [refusal.ErrorKey] }, statusCode: refusal.Status, title: refusal.ErrorKey)
                : Results.Problem(statusCode: refusal.Status, title: refusal.ErrorKey);
        }
    }

    private static IResult NotFound() => Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "errors.data.not_found");

    private static void Tag(RouteHandlerBuilder endpoint, string kind, string resource, string permission) =>
        endpoint.WithMetadata(new AccessEndpointMetadata(kind, resource, permission));
}

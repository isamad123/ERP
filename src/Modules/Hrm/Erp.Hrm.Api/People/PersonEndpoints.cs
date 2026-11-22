using Erp.BuildingBlocks.Contracts.Api;
using Erp.Hrm.Application.People;
using Erp.Hrm.Contracts;
using Erp.Hrm.Contracts.People;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Erp.Hrm.Api.People;

/// <summary>
/// The HR person endpoints (slice S1.20a, ruling Q1): create and change persons and their sections, reveal an identifier, the
/// duplicate review queue, merge, and the person catalogue. Reads of the records stay the generated <c>/api/hrm/persons</c>,
/// <c>/api/hrm/person-identifiers</c> and so on (with data scopes and field security); their generated writes are closed.
/// Refusals are problem answers whose title is a localization key (people.en.json).
/// </summary>
public static class PersonEndpoints
{
    private const string Persons = "hrm.persons";
    private const string Cases = "hrm.person_duplicate_cases";
    private static readonly EntitlementMetadata Core = new("hrm.core", "hrm");

    public static IEndpointRouteBuilder MapHrmPeopleEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        Tag(app.MapPost("/api/hrm/persons", async (CreatePersonRequest body, IPersonService s, CancellationToken ct) =>
            Answer(await s.CreateAsync(body, "create", ct).ConfigureAwait(false))), "create", Persons, PeoplePermissions.PersonsCreate);
        Tag(app.MapPut("/api/hrm/persons/{id:guid}", async (Guid id, UpdatePersonRequest body, IPersonService s, CancellationToken ct) =>
            Answer(await s.UpdateAsync(id, body, ct).ConfigureAwait(false))), "record_action", Persons, PeoplePermissions.PersonsUpdate);
        Tag(app.MapPost("/api/hrm/persons/{id:guid}/merge", async (Guid id, MergeRequest body, IPersonService s, CancellationToken ct) =>
            Answer(await s.MergeAsync(id, body, ct).ConfigureAwait(false))), "record_action", Persons, PeoplePermissions.PersonsMerge);

        Tag(app.MapPost("/api/hrm/persons/{id:guid}/identifiers", async (Guid id, IdentifierInput body, IPersonService s, CancellationToken ct) =>
            Answer(await s.AddIdentifierAsync(id, body, ct).ConfigureAwait(false))), "record_action", Persons, PeoplePermissions.IdentifiersCreate);
        Tag(app.MapPut("/api/hrm/persons/{id:guid}/identifiers/{rowId:guid}", async (Guid id, Guid rowId, IdentifierInput body, IPersonService s, CancellationToken ct) =>
            Answer(await s.UpdateIdentifierAsync(id, rowId, body, ct).ConfigureAwait(false))), "record_action", Persons, PeoplePermissions.IdentifiersUpdate);
        Tag(app.MapDelete("/api/hrm/persons/{id:guid}/identifiers/{rowId:guid}", async (Guid id, Guid rowId, [FromQuery] long? rowVersion, IPersonService s, CancellationToken ct) =>
            Answer(await s.DeleteIdentifierAsync(id, rowId, rowVersion, ct).ConfigureAwait(false))), "record_action", Persons, PeoplePermissions.IdentifiersDelete);
        Tag(app.MapGet("/api/hrm/persons/{id:guid}/identifiers/{rowId:guid}/value", async (Guid id, Guid rowId, IPersonService s, CancellationToken ct) =>
            Answer(await s.RevealIdentifierAsync(id, rowId, ct).ConfigureAwait(false))), "record_action", Persons, PeoplePermissions.IdentifiersReveal);

        Section<ContactInput>(app, "contacts", "hrm.person_contacts",
            (s, id, b, ct) => s.AddContactAsync(id, b, ct), (s, id, r, b, ct) => s.UpdateContactAsync(id, r, b, ct), (s, id, r, v, ct) => s.DeleteContactAsync(id, r, v, ct));
        Section<AddressInput>(app, "addresses", "hrm.person_addresses",
            (s, id, b, ct) => s.AddAddressAsync(id, b, ct), (s, id, r, b, ct) => s.UpdateAddressAsync(id, r, b, ct), (s, id, r, v, ct) => s.DeleteAddressAsync(id, r, v, ct));
        Section<RelationInput>(app, "relations", "hrm.person_relations",
            (s, id, b, ct) => s.AddRelationAsync(id, b, ct), (s, id, r, b, ct) => s.UpdateRelationAsync(id, r, b, ct), (s, id, r, v, ct) => s.DeleteRelationAsync(id, r, v, ct));
        Section<QualificationInput>(app, "qualifications", "hrm.person_qualifications",
            (s, id, b, ct) => s.AddQualificationAsync(id, b, ct), (s, id, r, b, ct) => s.UpdateQualificationAsync(id, r, b, ct), (s, id, r, v, ct) => s.DeleteQualificationAsync(id, r, v, ct));

        Tag(app.MapGet("/api/hrm/duplicates", async ([FromQuery] string? status, [FromQuery] Guid? after, [FromQuery] int? limit, IPersonService s, CancellationToken ct) =>
            Answer(await s.DuplicatesAsync(status, after, limit, ct).ConfigureAwait(false))), "catalog", Cases, PeoplePermissions.DuplicatesRead);
        Tag(app.MapPost("/api/hrm/duplicates/{id:guid}/distinct", async (Guid id, DecideCaseRequest body, IPersonService s, CancellationToken ct) =>
            Answer(await s.MarkDistinctAsync(id, body, ct).ConfigureAwait(false))), "record_action", Cases, PeoplePermissions.DuplicatesUpdate);
        Tag(app.MapGet("/api/hrm/person-catalogue", async (IPersonService s, CancellationToken ct) =>
            Answer(await s.CatalogueAsync(ct).ConfigureAwait(false))), "catalog", Persons, PeoplePermissions.IdentifiersRead);
        return app;
    }

    private static void Section<TInput>(
        IEndpointRouteBuilder app, string path, string table,
        Func<IPersonService, Guid, TInput, CancellationToken, Task<PeopleResult>> add,
        Func<IPersonService, Guid, Guid, TInput, CancellationToken, Task<PeopleResult>> update,
        Func<IPersonService, Guid, Guid, long?, CancellationToken, Task<PeopleResult>> delete)
    {
        Tag(app.MapPost($"/api/hrm/persons/{{id:guid}}/{path}", async (Guid id, TInput body, IPersonService s, CancellationToken ct) =>
            Answer(await add(s, id, body, ct).ConfigureAwait(false))), "record_action", Persons, PeoplePermissions.Of(table, "create"));
        Tag(app.MapPut($"/api/hrm/persons/{{id:guid}}/{path}/{{rowId:guid}}", async (Guid id, Guid rowId, TInput body, IPersonService s, CancellationToken ct) =>
            Answer(await update(s, id, rowId, body, ct).ConfigureAwait(false))), "record_action", Persons, PeoplePermissions.Of(table, "update"));
        Tag(app.MapDelete($"/api/hrm/persons/{{id:guid}}/{path}/{{rowId:guid}}", async (Guid id, Guid rowId, [FromQuery] long? rowVersion, IPersonService s, CancellationToken ct) =>
            Answer(await delete(s, id, rowId, rowVersion, ct).ConfigureAwait(false))), "record_action", Persons, PeoplePermissions.Of(table, "delete"));
    }

    private static void Tag(RouteHandlerBuilder endpoint, string kind, string resource, string permission) =>
        endpoint.WithTags(resource).WithMetadata(new HrmEndpointMetadata(kind, resource, permission), Core);

    /// <summary>The HTTP answer of a person service result.</summary>
    public static IResult Answer(PeopleResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Outcome switch
        {
            PeopleOutcome.Done => Results.Ok(result.Body),
            PeopleOutcome.Created => Results.Json(result.Body, statusCode: StatusCodes.Status201Created),
            PeopleOutcome.Pending => Results.Json(result.Body, statusCode: StatusCodes.Status202Accepted),
            PeopleOutcome.NoContent => Results.NoContent(),
            PeopleOutcome.Invalid when result.FieldErrors is { Count: > 0 } errors =>
                Results.ValidationProblem(errors, title: result.Error, extensions: result.Extensions),
            PeopleOutcome.Invalid => Problem(StatusCodes.Status400BadRequest, result),
            PeopleOutcome.Forbidden => Problem(StatusCodes.Status403Forbidden, result),
            PeopleOutcome.NotFound => Problem(StatusCodes.Status404NotFound, result),
            _ => Problem(StatusCodes.Status409Conflict, result),
        };
    }

    private static IResult Problem(int status, PeopleResult result) =>
        Results.Problem(statusCode: status, title: result.Error, extensions: result.Extensions);
}

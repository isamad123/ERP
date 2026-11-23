namespace Erp.Hrm.Contracts;

/// <summary>
/// Marks a purpose-built HR endpoint (slice S1.20a onward) for the tenant-isolation suite (S1.04). <see cref="Resource"/> is the
/// table whose ids the route or body carries. <see cref="Kind"/>: <c>create</c> (POST of a new record; a body naming another
/// customer's ids is refused and writes nothing there), <c>record_action</c> (a route with the record's id: another customer's
/// id is 404) or <c>catalog</c> (a list of the bound customer's records or catalogue).
/// </summary>
public sealed record HrmEndpointMetadata(string Kind, string Resource, string Permission);

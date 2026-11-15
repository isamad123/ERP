using Npgsql;

namespace Erp.CodeGen.Dictionary;

public sealed record CatalogColumn(string Name, string Type, bool NotNull, string? Default, string? Generated, string? Comment);

public sealed record CatalogConstraint(string Name, char Kind, string Definition);

public sealed record CatalogIndex(string Name, string Definition);

public sealed record CatalogTrigger(string Name, string Definition);

public sealed record CatalogTable(
    string Schema,
    string Name,
    bool IsPartitioned,
    int PartitionCount,
    string? Comment,
    IReadOnlyList<CatalogColumn> Columns,
    IReadOnlyList<CatalogConstraint> Constraints,
    IReadOnlyList<CatalogIndex> Indexes,
    IReadOnlyList<CatalogTrigger> Triggers)
{
    public string FullName => Schema + "." + Name;
}

public sealed record CatalogFunction(string Schema, string Name, string? Comment);

public sealed record CatalogSnapshot(IReadOnlyList<CatalogTable> Tables, IReadOnlyList<CatalogFunction> Functions);

/// <summary>Reads the live PostgreSQL catalogue of one database (all application schemas), in a stable order.</summary>
public static class CatalogReader
{
    private const string SchemaFilter = "n.nspname not in ('pg_catalog', 'information_schema', 'public', 'pg_toast') and n.nspname not like 'pg_temp%' and n.nspname not like 'pg_toast_temp%'";

    public static async Task<CatalogSnapshot> ReadAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var tables = await ListAsync(connection, $"""
            select n.nspname, c.relname, c.relkind = 'p', obj_description(c.oid, 'pg_class'),
                   (select count(*)::int from pg_inherits i where i.inhparent = c.oid), c.oid::bigint
            from pg_class c join pg_namespace n on n.oid = c.relnamespace
            where c.relkind in ('r', 'p') and not c.relispartition and {SchemaFilter}
            order by n.nspname, c.relname
            """, r => (Schema: r.GetString(0), Name: r.GetString(1), Partitioned: r.GetBoolean(2), Comment: r.IsDBNull(3) ? null : r.GetString(3), Partitions: r.GetInt32(4), Oid: r.GetInt64(5)), cancellationToken).ConfigureAwait(false);

        var columns = (await ListAsync(connection, $"""
            select c.oid::bigint, a.attname, format_type(a.atttypid, a.atttypmod), a.attnotnull,
                   case when a.attgenerated = '' then pg_get_expr(d.adbin, d.adrelid) end,
                   case when a.attgenerated <> '' then pg_get_expr(d.adbin, d.adrelid) end,
                   col_description(c.oid, a.attnum)
            from pg_attribute a join pg_class c on c.oid = a.attrelid join pg_namespace n on n.oid = c.relnamespace
            left join pg_attrdef d on d.adrelid = a.attrelid and d.adnum = a.attnum
            where a.attnum > 0 and not a.attisdropped and c.relkind in ('r', 'p') and not c.relispartition and {SchemaFilter}
            order by c.oid, a.attnum
            """, r => (Oid: r.GetInt64(0), Column: new CatalogColumn(r.GetString(1), r.GetString(2), r.GetBoolean(3), NullableString(r, 4), NullableString(r, 5), NullableString(r, 6))), cancellationToken).ConfigureAwait(false))
            .ToLookup(x => x.Oid, x => x.Column);

        var constraints = (await ListAsync(connection, $"""
            select c.oid::bigint, k.conname, k.contype::text, pg_get_constraintdef(k.oid)
            from pg_constraint k join pg_class c on c.oid = k.conrelid join pg_namespace n on n.oid = c.relnamespace
            where not c.relispartition and k.contype in ('p', 'u', 'f', 'c', 'x') and k.conparentid = 0 and {SchemaFilter}
            order by c.oid, k.conname
            """, r => (Oid: r.GetInt64(0), Constraint: new CatalogConstraint(r.GetString(1), r.GetString(2)[0], r.GetString(3))), cancellationToken).ConfigureAwait(false))
            .ToLookup(x => x.Oid, x => x.Constraint);

        var indexes = (await ListAsync(connection, $"""
            select c.oid::bigint, ic.relname, pg_get_indexdef(i.indexrelid)
            from pg_index i join pg_class c on c.oid = i.indrelid join pg_class ic on ic.oid = i.indexrelid
            join pg_namespace n on n.oid = c.relnamespace
            where not c.relispartition and {SchemaFilter}
              and not exists (select 1 from pg_constraint k where k.conindid = i.indexrelid and k.conrelid = c.oid)
            order by c.oid, ic.relname
            """, r => (Oid: r.GetInt64(0), Index: new CatalogIndex(r.GetString(1), r.GetString(2))), cancellationToken).ConfigureAwait(false))
            .ToLookup(x => x.Oid, x => x.Index);

        var triggers = (await ListAsync(connection, $"""
            select c.oid::bigint, t.tgname, pg_get_triggerdef(t.oid)
            from pg_trigger t join pg_class c on c.oid = t.tgrelid join pg_namespace n on n.oid = c.relnamespace
            where not t.tgisinternal and not c.relispartition and {SchemaFilter}
            order by c.oid, t.tgname
            """, r => (Oid: r.GetInt64(0), Trigger: new CatalogTrigger(r.GetString(1), r.GetString(2))), cancellationToken).ConfigureAwait(false))
            .ToLookup(x => x.Oid, x => x.Trigger);

        var functions = await ListAsync(connection, $"""
            select n.nspname, p.proname, obj_description(p.oid, 'pg_proc')
            from pg_proc p join pg_namespace n on n.oid = p.pronamespace
            where {SchemaFilter} and not exists (select 1 from pg_depend d where d.objid = p.oid and d.deptype = 'e')
            order by n.nspname, p.proname
            """, r => new CatalogFunction(r.GetString(0), r.GetString(1), NullableString(r, 2)), cancellationToken).ConfigureAwait(false);

        var result = tables.Select(t => new CatalogTable(
            t.Schema, t.Name, t.Partitioned, t.Partitions, t.Comment,
            columns[t.Oid].ToList(), constraints[t.Oid].ToList(), indexes[t.Oid].ToList(), triggers[t.Oid].ToList())).ToList();
        return new CatalogSnapshot(result, functions);
    }

    private static string? NullableString(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static async Task<List<T>> ListAsync<T>(NpgsqlConnection connection, string sql, Func<NpgsqlDataReader, T> map, CancellationToken cancellationToken)
    {
#pragma warning disable CA2100 // Fixed catalogue queries; no input is concatenated.
        await using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var list = new List<T>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            list.Add(map(reader));
        }

        return list;
    }
}

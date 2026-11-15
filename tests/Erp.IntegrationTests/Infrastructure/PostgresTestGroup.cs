namespace Erp.IntegrationTests.Infrastructure;

[CollectionDefinition(Name)]
public sealed class PostgresTestGroup : ICollectionFixture<PostgresFixture>
{
    public const string Name = "PostgreSQL";
}

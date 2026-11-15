using Erp.IntegrationTests.Infrastructure;

namespace Erp.IntegrationTests;

[Collection(PostgresTestGroup.Name)]
public sealed class PostgresSmokeTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Connects_to_a_real_postgresql_16_and_runs_select_1()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = await postgres.OpenConnectionAsync(ct);

        await using var select = connection.CreateCommand();
        select.CommandText = "select 1";
        var one = await select.ExecuteScalarAsync(ct);

        await using var version = connection.CreateCommand();
        version.CommandText = "show server_version_num";
        var versionNumber = int.Parse((string)(await version.ExecuteScalarAsync(ct))!, System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal(1, one);
        Assert.InRange(versionNumber, 160000, 169999);
    }
}

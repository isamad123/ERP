// Code generator (slice S1.02). Reads docs/data-design/*.yaml and writes migrations, entities, EF Core configuration,
// repositories, minimal API endpoints, TypeScript types and screen metadata, the placeholder register and the expected
// database catalogue used by the constraint tests. Operator console output (developer tool), so it is not localized.
using Erp.CodeGen;
using Erp.CodeGen.Design;

try
{
    return await Cli.RunAsync(args).ConfigureAwait(false);
}
catch (DesignException ex)
{
    Console.Error.WriteLine("Erp.CodeGen: the data design cannot be generated without guessing:");
    Console.Error.WriteLine(ex.Message);
    return 2;
}

using Erp.Host.Migrator;

// Migrator and operator commands (S1.03). Operator console output, not end-user UI, so it is not localized.
return await OperatorCommands.RunAsync(args, Console.Out, Console.Error, CancellationToken.None).ConfigureAwait(false);

using Authagonal.Migration.Cli;

// ---------------------------------------------------------------------------
// Subcommand dispatch. No verb (or any other first argument) runs the original Duende migration —
// every existing invocation of this tool keeps working unchanged. "import-ndjson-users" runs the
// generic NDJSON user-import source instead.
//
//   dotnet run -- --Source:ConnectionString "..." --Target:ConnectionString "..." --DryRun true
//   dotnet run -- import-ndjson-users --Input ./users.ndjson --Target:ConnectionString "..." --DryRun true
// ---------------------------------------------------------------------------
if (args.Length > 0 && args[0] == "import-ndjson-users")
    return await NdjsonImportCommand.RunAsync(args[1..]);

return await DuendeMigrationCommand.RunAsync(args);

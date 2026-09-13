using System.Text.Json;
using Authagonal.Migration;
using Microsoft.Extensions.Configuration;

namespace Authagonal.Migration.Cli;

/// <summary>
/// The <c>import-ndjson-users</c> subcommand: parse args → build the target user store → run
/// <see cref="NdjsonUserImportEngine"/> → print the report. Same option style, target wiring
/// (<see cref="StoreFactory"/>, Azure Table Storage) and PII-plaintext gate as
/// <see cref="DuendeMigrationCommand"/> — this is a new import SOURCE sharing that command's
/// conventions, not a new pipeline.
/// </summary>
/// <remarks>
///   dotnet run --project tools/Authagonal.Migration.Cli -- import-ndjson-users \
///       --Input ./users.ndjson \
///       --Target:ConnectionString "UseDevelopmentStorage=true" \
///       --DryRun true --OnDuplicate skip --BatchSize 500 --AllowPlaintextPii true
/// </remarks>
internal static class NdjsonImportCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        var config = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", optional: true)
            .AddCommandLine(args)
            .Build();

        var options = config.Get<NdjsonUserImportOptions>() ?? new NdjsonUserImportOptions();

        if (string.IsNullOrWhiteSpace(options.Input))
        {
            Console.Error.WriteLine("ERROR: --Input is required (path to the NDJSON file).");
            return 1;
        }

        if (!File.Exists(options.Input))
        {
            Console.Error.WriteLine($"ERROR: input file not found: {options.Input}");
            return 1;
        }

        var targetConnectionString = options.Target.ConnectionString;
        if (string.IsNullOrWhiteSpace(targetConnectionString))
        {
            Console.Error.WriteLine("ERROR: --Target:ConnectionString is required (Azure Table Storage).");
            return 1;
        }

        Console.WriteLine("NDJSON user import (CLI)");
        Console.WriteLine($"  Input:            {options.Input}");
        Console.WriteLine($"  Target:           Azure Table Storage");
        Console.WriteLine($"  DryRun:           {options.DryRun}");
        Console.WriteLine($"  OnDuplicate:      {options.OnDuplicate}");
        Console.WriteLine($"  BatchSize:        {options.BatchSize}");
        Console.WriteLine($"  AllowUnknownFlds: {options.AllowUnknownFields}");
        Console.WriteLine($"  ContinueOnError:  {options.ContinueOnError}");
        Console.WriteLine();

        // Same PII rationale as DuendeMigrationCommand: this import writes AuthUser rows (email, name,
        // phone, custom attributes) straight to the target's Table Storage without going through a
        // host's IFieldCipher/IIndexTokenizer, which are registered in code and cannot be reconstructed
        // from a connection string. No secret-provider gate is needed here (unlike the Duende command)
        // because this source never writes MFA TOTP seeds or OAuth client secrets — only user profile
        // fields and a password hash stored verbatim.
        if (!config.GetValue("AllowPlaintextPii", false))
        {
            Console.Error.WriteLine(
                "ERROR: this tool writes user PII with no at-rest field encryption and no blind-index\n" +
                "       tokenization, because IFieldCipher / IIndexTokenizer are registered in code, not in\n" +
                "       configuration. If the TARGET deployment registers either, wire\n" +
                "       NdjsonUserImportEngine into the host's own DI container instead of this CLI. If the\n" +
                "       target registers neither, pass --AllowPlaintextPii true to confirm that.");
            return 1;
        }

        Console.WriteLine();
        Console.WriteLine("  PII:     PLAINTEXT (--AllowPlaintextPii) — no field encryption, no blind-index");
        Console.WriteLine("           tokenization. Writes ARE captured in the change log, so an incremental");
        Console.WriteLine("           backup sees the imported rows.");
        Console.WriteLine();

        var duendeStores = StoreFactory.Create(targetConnectionString);
        var stores = new NdjsonUserImportStores { Users = duendeStores.Users };
        var engine = new NdjsonUserImportEngine(stores);

        NdjsonUserImportReport report;
        try
        {
            report = await engine.RunAsync(options);
        }
        catch (NdjsonDuplicateUserImportException ex)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine($"ERROR: {ex.Message}");
            return 2;
        }

        Console.WriteLine();
        Console.WriteLine("=== Report ===");
        Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));

        if (report.DryRun)
            Console.WriteLine("\n** DRY RUN — no data was written **");

        // Unlike the Duende command's Errors.Count check, "failed" here means individual malformed
        // lines the engine already skipped past (not a fatal condition) — --ContinueOnError controls
        // only whether that still exits non-zero, matching the task's documented CLI contract.
        if (report.Failed > 0 && !options.ContinueOnError)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine(
                $"ERROR: {report.Failed} line(s) failed. Pass --ContinueOnError true to exit 0 anyway.");
            return 1;
        }

        return 0;
    }
}

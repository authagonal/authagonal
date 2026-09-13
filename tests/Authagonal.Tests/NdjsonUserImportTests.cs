using Authagonal.Core.Models;
using Authagonal.Core.Services;
using Authagonal.Core.Stores;
using Authagonal.Migration;
using Authagonal.SqlProvider.Sql;
using Authagonal.SqlProvider.Stores;
using Authagonal.Tests.Infrastructure;

namespace Authagonal.Tests;

/// <summary>
/// Coverage for the generic NDJSON user-import source: <see cref="NdjsonUserImportReader"/> (parsing +
/// validation, no store involved), <see cref="NdjsonUserMapper"/> (field mapping, no store involved),
/// and <see cref="NdjsonUserImportEngine"/> end-to-end against a SQLite-backed <see cref="SqlUserStore"/>
/// — the same fast/no-Docker target <see cref="SqlProviderTestsBase"/> uses, per the house convention
/// for importer tests that don't need a real backend to prove their logic.
/// </summary>
public sealed class NdjsonUserImportReaderTests
{
    // ── invalid JSON / shape ──────────────────────────────────────────────

    [Fact]
    public void ParseLine_InvalidJson_Fails()
    {
        var result = NdjsonUserImportReader.ParseLine("{not json", allowUnknownFields: false);

        Assert.False(result.IsSuccess);
        Assert.Contains("invalid JSON", result.Error);
    }

    [Fact]
    public void ParseLine_NonObjectLine_Fails()
    {
        var result = NdjsonUserImportReader.ParseLine("[1,2,3]", allowUnknownFields: false);

        Assert.False(result.IsSuccess);
        Assert.Contains("not a JSON object", result.Error);
    }

    // ── unknown fields: strict vs allowed ────────────────────────────────

    [Fact]
    public void ParseLine_UnknownField_StrictByDefault_Fails()
    {
        var result = NdjsonUserImportReader.ParseLine(
            """{"email":"a@example.com","bogusField":"x"}""", allowUnknownFields: false);

        Assert.False(result.IsSuccess);
        Assert.Contains("unknown field(s)", result.Error);
        Assert.Contains("bogusField", result.Error);
    }

    [Fact]
    public void ParseLine_UnknownField_AllowedWhenFlagSet_Succeeds()
    {
        var result = NdjsonUserImportReader.ParseLine(
            """{"email":"a@example.com","bogusField":"x"}""", allowUnknownFields: true);

        Assert.True(result.IsSuccess);
        Assert.Equal("a@example.com", result.Record!.Email);
    }

    // ── email ─────────────────────────────────────────────────────────────

    [Fact]
    public void ParseLine_MissingEmail_Fails()
    {
        var result = NdjsonUserImportReader.ParseLine("{}", allowUnknownFields: false);

        Assert.False(result.IsSuccess);
        Assert.Contains("email is required", result.Error);
    }

    [Theory]
    [InlineData("not-an-email")]
    [InlineData("missing-at-sign.example.com")]
    [InlineData("a b@example.com")]
    public void ParseLine_BadEmail_Fails(string email)
    {
        var result = NdjsonUserImportReader.ParseLine($$"""{"email":"{{email}}"}""", allowUnknownFields: false);

        Assert.False(result.IsSuccess);
        Assert.Contains("valid email", result.Error);
    }

    [Fact]
    public void ParseLine_ValidMinimalLine_Succeeds()
    {
        var result = NdjsonUserImportReader.ParseLine("""{"email":"a@example.com"}""", allowUnknownFields: false);

        Assert.True(result.IsSuccess);
        Assert.Equal("a@example.com", result.Record!.Email);
    }

    // ── passwordHash: non-empty-if-present, never format-validated ───────

    [Fact]
    public void ParseLine_BlankPasswordHash_Fails()
    {
        var result = NdjsonUserImportReader.ParseLine(
            """{"email":"a@example.com","passwordHash":"   "}""", allowUnknownFields: false);

        Assert.False(result.IsSuccess);
        Assert.Contains("passwordHash must not be empty", result.Error);
    }

    [Theory]
    [InlineData("$2b$12$abcdefghijklmnopqrstuvABCDEFGHIJKLMNOPQRSTUVWXYZ012345")] // bcrypt-shaped
    [InlineData("$s2$16384$8$1$abcdefghijkl$mnopqrstuvwxyz")] // scrypt-shaped
    [InlineData("AQAAAAEAACcQAAAAE...")] // ASP.NET Identity V3-shaped (base64)
    public void ParseLine_AnyNonEmptyPasswordHashShape_SucceedsUnvalidated(string hash)
    {
        var result = NdjsonUserImportReader.ParseLine(
            $$"""{"email":"a@example.com","passwordHash":"{{hash}}"}""", allowUnknownFields: false);

        Assert.True(result.IsSuccess);
        Assert.Equal(hash, result.Record!.PasswordHash);
    }

    // ── createdAt: ISO 8601 or bust ────────────────────────────────────────

    [Fact]
    public void ParseLine_BadCreatedAt_Fails()
    {
        var result = NdjsonUserImportReader.ParseLine(
            """{"email":"a@example.com","createdAt":"not-a-date"}""", allowUnknownFields: false);

        Assert.False(result.IsSuccess);
        Assert.Contains("createdAt is not a valid ISO 8601 date", result.Error);
    }

    [Fact]
    public void ParseLine_ValidCreatedAt_Succeeds()
    {
        var result = NdjsonUserImportReader.ParseLine(
            """{"email":"a@example.com","createdAt":"2024-01-15T10:30:00Z"}""", allowUnknownFields: false);

        Assert.True(result.IsSuccess);
        Assert.Equal(
            DateTimeOffset.Parse("2024-01-15T10:30:00Z"), NdjsonUserMapper.ParseCreatedAt(result.Record!.CreatedAt));
    }

    // ── each optional field, individually ─────────────────────────────────

    [Theory]
    [InlineData("""{"email":"a@example.com","username":"legacy_bob"}""")]
    [InlineData("""{"email":"a@example.com","givenName":"Ada"}""")]
    [InlineData("""{"email":"a@example.com","familyName":"Lovelace"}""")]
    [InlineData("""{"email":"a@example.com","displayName":"Ada L."}""")]
    [InlineData("""{"email":"a@example.com","emailVerified":true}""")]
    [InlineData("""{"email":"a@example.com","roles":["admin","member"]}""")]
    [InlineData("""{"email":"a@example.com","organizationId":"org-123"}""")]
    [InlineData("""{"email":"a@example.com","attributes":{"dept":"eng"}}""")]
    [InlineData("""{"email":"a@example.com","phoneNumber":"+61400000000"}""")]
    [InlineData("""{"email":"a@example.com","disabled":true}""")]
    [InlineData("""{"email":"a@example.com","externalId":"legacy-id-42"}""")]
    public void ParseLine_EachOptionalFieldInIsolation_Succeeds(string line)
    {
        var result = NdjsonUserImportReader.ParseLine(line, allowUnknownFields: false);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public void ParseLine_EveryFieldPresent_ParsesEachOntoTheRecord()
    {
        var line = """
            {"email":"ada@example.com","username":"ada_legacy","givenName":"Ada","familyName":"Lovelace",
             "displayName":"Ada Lovelace","emailVerified":true,"passwordHash":"$2b$12$hash",
             "roles":["admin","member"],"organizationId":"org-1","attributes":{"dept":"eng"},
             "phoneNumber":"+61400000000","disabled":false,"createdAt":"2024-01-15T10:30:00Z",
             "externalId":"legacy-42"}
            """;

        var result = NdjsonUserImportReader.ParseLine(line, allowUnknownFields: false);

        Assert.True(result.IsSuccess);
        var r = result.Record!;
        Assert.Equal("ada@example.com", r.Email);
        Assert.Equal("ada_legacy", r.Username);
        Assert.Equal("Ada", r.GivenName);
        Assert.Equal("Lovelace", r.FamilyName);
        Assert.Equal("Ada Lovelace", r.DisplayName);
        Assert.True(r.EmailVerified);
        Assert.Equal("$2b$12$hash", r.PasswordHash);
        Assert.Equal(["admin", "member"], r.Roles);
        Assert.Equal("org-1", r.OrganizationId);
        Assert.Equal("eng", r.Attributes!["dept"]);
        Assert.Equal("+61400000000", r.PhoneNumber);
        Assert.False(r.Disabled);
        Assert.Equal("legacy-42", r.ExternalId);
    }
}

public sealed class NdjsonUserMapperTests
{
    [Fact]
    public void ToNewAuthUser_EveryFieldLandsOnAuthUser()
    {
        var record = new NdjsonUserRecord
        {
            Email = " Ada@Example.com ",
            Username = "ada_legacy",
            GivenName = "Ada",
            FamilyName = "Lovelace",
            DisplayName = "Ada Lovelace",
            EmailVerified = true,
            PasswordHash = "$2b$12$verbatim-hash-value",
            Roles = ["admin", "member"],
            OrganizationId = "org-1",
            Attributes = new Dictionary<string, string> { ["dept"] = "eng" },
            PhoneNumber = "+61400000000",
            Disabled = false,
            CreatedAt = "2024-01-15T10:30:00Z",
            ExternalId = "legacy-42",
        };

        var now = DateTimeOffset.UtcNow;
        var user = NdjsonUserMapper.ToNewAuthUser(record, now);

        Assert.False(string.IsNullOrWhiteSpace(user.Id));
        Assert.Equal("Ada@Example.com", user.Email); // trimmed, casing preserved
        Assert.Equal("ADA@EXAMPLE.COM", user.NormalizedEmail);
        Assert.Equal("$2b$12$verbatim-hash-value", user.PasswordHash); // stored verbatim
        Assert.True(user.EmailConfirmed);
        Assert.Equal("Ada", user.FirstName);
        Assert.Equal("Lovelace", user.LastName);
        Assert.Equal("+61400000000", user.Phone);
        Assert.Equal("org-1", user.OrganizationId);
        Assert.Equal("legacy-42", user.ExternalId);
        Assert.True(user.IsActive);
        Assert.Equal(["admin", "member"], user.Roles);
        Assert.Equal("eng", user.CustomAttributes["dept"]);
        Assert.Equal("ada_legacy", user.CustomAttributes["username"]);
        Assert.Equal("Ada Lovelace", user.CustomAttributes["displayName"]);
        Assert.Equal(DateTimeOffset.Parse("2024-01-15T10:30:00Z"), user.CreatedAt);
        Assert.False(string.IsNullOrEmpty(user.SecurityStamp));
    }

    [Fact]
    public void ToNewAuthUser_DisabledTrue_SetsIsActiveFalse()
    {
        var record = new NdjsonUserRecord { Email = "a@example.com", Disabled = true };
        var user = NdjsonUserMapper.ToNewAuthUser(record, DateTimeOffset.UtcNow);

        Assert.False(user.IsActive);
    }

    [Fact]
    public void ToNewAuthUser_NoCreatedAt_DefaultsToNow()
    {
        var now = DateTimeOffset.UtcNow;
        var record = new NdjsonUserRecord { Email = "a@example.com" };

        var user = NdjsonUserMapper.ToNewAuthUser(record, now);

        Assert.Equal(now, user.CreatedAt);
    }

    [Fact]
    public void ApplyToExisting_OnlyOverwritesFieldsPresentOnTheLine()
    {
        var existing = new AuthUser
        {
            Id = "u1",
            Email = "a@example.com",
            NormalizedEmail = "A@EXAMPLE.COM",
            FirstName = "OriginalFirst",
            LastName = "OriginalLast",
            OrganizationId = "org-original",
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-30),
        };
        existing.CustomAttributes["displayName"] = "Old Display Name";

        // Only displayName is present on this line.
        var record = new NdjsonUserRecord { Email = "a@example.com", DisplayName = "New Display Name" };
        var now = DateTimeOffset.UtcNow;

        NdjsonUserMapper.ApplyToExisting(existing, record, now);

        Assert.Equal("New Display Name", existing.CustomAttributes["displayName"]);
        Assert.Equal("OriginalFirst", existing.FirstName); // untouched
        Assert.Equal("OriginalLast", existing.LastName); // untouched
        Assert.Equal("org-original", existing.OrganizationId); // untouched
        Assert.Equal("u1", existing.Id); // never touched
        Assert.Equal(now, existing.UpdatedAt);
    }

    [Fact]
    public void ApplyToExisting_ExplicitUsername_WinsOverSameKeyInAttributesBag()
    {
        var existing = new AuthUser { Id = "u1", Email = "a@example.com", NormalizedEmail = "A@EXAMPLE.COM" };
        var record = new NdjsonUserRecord
        {
            Email = "a@example.com",
            Username = "explicit-username",
            Attributes = new Dictionary<string, string> { ["username"] = "from-attributes-bag" },
        };

        NdjsonUserMapper.ApplyToExisting(existing, record, DateTimeOffset.UtcNow);

        Assert.Equal("explicit-username", existing.CustomAttributes["username"]);
    }
}

/// <summary>
/// End-to-end coverage against a SQLite-backed <see cref="SqlUserStore"/> — the fast, no-Docker target
/// <see cref="SqlProviderTestsBase"/> already uses for the self-hosted SQL provider's own suite.
/// </summary>
public sealed class NdjsonUserImportEngineTests : IAsyncLifetime
{
    private SqlDataSource _source = null!;
    private NdjsonUserImportStores _stores = null!;
    private readonly List<string> _tempFiles = [];

    public async Task InitializeAsync()
    {
        _source = SqlTestSource.Sqlite();
        SqlTable T(string name)
        {
            _source.EnsureTableAsync(name).GetAwaiter().GetResult();
            return new SqlTable(_source, name);
        }

        var userStore = new SqlUserStore(
            T("Users"), T("UserEmails"), T("UserLogins"), T("UserExternalIds"),
            userFirstNames: null, userLastNames: null, EnvPartitioner.Live);
        _stores = new NdjsonUserImportStores { Users = userStore };
        await Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        foreach (var file in _tempFiles)
        {
            try { File.Delete(file); } catch { /* best-effort cleanup */ }
        }

        await _source.DisposeAsync();
    }

    private string WriteNdjson(params string[] lines)
    {
        var path = Path.Combine(Path.GetTempPath(), $"ndjson-import-test-{Guid.NewGuid():N}.ndjson");
        File.WriteAllLines(path, lines);
        _tempFiles.Add(path);
        return path;
    }

    private NdjsonUserImportEngine Engine() => new(_stores);

    [Fact]
    public async Task DryRun_ParsesAndReportsButWritesNothing()
    {
        var path = WriteNdjson(
            """{"email":"one@example.com"}""",
            """{"email":"two@example.com"}""",
            """{"email":"three@example.com"}""");

        var report = await Engine().RunAsync(new NdjsonUserImportOptions { Input = path, DryRun = true });

        Assert.Equal(3, report.TotalLines);
        Assert.Equal(3, report.Imported);
        Assert.Equal(0, report.Failed);
        Assert.True(report.DryRun);

        Assert.Null(await _stores.Users.FindByEmailAsync("one@example.com"));
        Assert.Null(await _stores.Users.FindByEmailAsync("two@example.com"));
        Assert.Null(await _stores.Users.FindByEmailAsync("three@example.com"));
    }

    [Fact]
    public async Task Import_ThenReimportWithSkip_IsIdempotent()
    {
        var path = WriteNdjson(
            """{"email":"ada@example.com","givenName":"Ada"}""",
            """{"email":"bob@example.com","givenName":"Bob"}""");

        var first = await Engine().RunAsync(new NdjsonUserImportOptions { Input = path });
        Assert.Equal(2, first.Imported);
        Assert.Equal(0, first.Skipped);

        var second = await Engine().RunAsync(new NdjsonUserImportOptions { Input = path }); // default OnDuplicate = Skip
        Assert.Equal(0, second.Imported);
        Assert.Equal(2, second.Skipped);
        Assert.Equal(0, second.Failed);

        var ada = await _stores.Users.FindByEmailAsync("ada@example.com");
        Assert.NotNull(ada);
        Assert.Equal("Ada", ada!.FirstName); // untouched by the skipped re-run
    }

    [Fact]
    public async Task OnDuplicateUpdate_ChangesDisplayNameOnReimport()
    {
        var firstPath = WriteNdjson("""{"email":"ada@example.com","displayName":"Old Name"}""");
        await Engine().RunAsync(new NdjsonUserImportOptions { Input = firstPath });

        var existing = await _stores.Users.FindByEmailAsync("ada@example.com");
        Assert.Equal("Old Name", existing!.CustomAttributes["displayName"]);

        var secondPath = WriteNdjson("""{"email":"ada@example.com","displayName":"New Name"}""");
        var report = await Engine().RunAsync(
            new NdjsonUserImportOptions { Input = secondPath, OnDuplicate = OnDuplicateAction.Update });

        Assert.Equal(1, report.Updated);
        Assert.Equal(0, report.Imported);

        var updated = await _stores.Users.FindByEmailAsync("ada@example.com");
        Assert.Equal("New Name", updated!.CustomAttributes["displayName"]);
    }

    [Fact]
    public async Task OnDuplicateFail_RaisesOnAlreadyImportedEmail()
    {
        var path = WriteNdjson("""{"email":"ada@example.com"}""");
        await Engine().RunAsync(new NdjsonUserImportOptions { Input = path });

        var secondPath = WriteNdjson("""{"email":"ada@example.com"}""");
        var ex = await Assert.ThrowsAsync<NdjsonDuplicateUserImportException>(() =>
            Engine().RunAsync(new NdjsonUserImportOptions { Input = secondPath, OnDuplicate = OnDuplicateAction.Fail }));

        Assert.Equal("ada@example.com", ex.Email);
        Assert.Equal(1, ex.LineNumber);
    }

    [Fact]
    public async Task MalformedLine_IsRecordedAsFailureAndOthersStillImport()
    {
        var path = WriteNdjson(
            """{"email":"good@example.com"}""",
            """{"passwordHash":"no-email-on-this-line"}""",
            """{"email":"also-good@example.com"}""");

        var report = await Engine().RunAsync(new NdjsonUserImportOptions { Input = path });

        Assert.Equal(3, report.TotalLines);
        Assert.Equal(2, report.Imported);
        Assert.Equal(1, report.Failed);
        Assert.Single(report.Failures);
        Assert.Equal(2, report.Failures[0].LineNumber);
        Assert.Contains("email is required", report.Failures[0].Reason);

        Assert.NotNull(await _stores.Users.FindByEmailAsync("good@example.com"));
        Assert.NotNull(await _stores.Users.FindByEmailAsync("also-good@example.com"));
    }

    [Fact]
    public async Task PasswordHash_IsStoredVerbatim()
    {
        const string hash = "$2b$12$abcdefghijklmnopqrstuvABCDEFGHIJKLMNOPQRSTUVWXYZ012345";
        var path = WriteNdjson($$"""{"email":"a@example.com","passwordHash":"{{hash}}"}""");

        await Engine().RunAsync(new NdjsonUserImportOptions { Input = path });

        var user = await _stores.Users.FindByEmailAsync("a@example.com");
        Assert.Equal(hash, user!.PasswordHash);
    }

    [Fact]
    public async Task OrganizationId_LandsOnAuthUser()
    {
        var path = WriteNdjson("""{"email":"a@example.com","organizationId":"org-123"}""");

        await Engine().RunAsync(new NdjsonUserImportOptions { Input = path });

        var user = await _stores.Users.FindByEmailAsync("a@example.com");
        Assert.Equal("org-123", user!.OrganizationId);
    }

    [Fact]
    public async Task BlankLines_AreSkippedAndNotCounted()
    {
        var path = WriteNdjson(
            """{"email":"a@example.com"}""",
            "",
            "   ",
            """{"email":"b@example.com"}""");

        var report = await Engine().RunAsync(new NdjsonUserImportOptions { Input = path });

        Assert.Equal(2, report.TotalLines);
        Assert.Equal(2, report.Imported);
        Assert.Equal(0, report.Failed);
    }
}

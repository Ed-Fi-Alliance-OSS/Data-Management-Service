// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.External.Security;
using FluentAssertions;
using Npgsql;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

/// <summary>
/// Live-provider coverage for descriptor POST, PUT and DELETE under <c>OwnershipBased</c> on PostgreSQL,
/// through the repository and the production descriptor write handler.
/// </summary>
/// <remarks>
/// Rows are seeded through a create under <c>NoFurtherAuthorizationRequired</c>, which stamps the creating
/// client's token — or null for a client with none — exactly as a legacy or misconfigured create did. Every
/// denied create counts <c>dms.Document</c> and <c>dms.Descriptor</c> and finds nothing; every denied update
/// shows the document, descriptor row and referential identities unchanged; every denied delete finds both
/// rows still present.
/// </remarks>
[TestFixture]
[NonParallelizable]
[Category("Authorization")]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
public class Given_A_Postgresql_Descriptor_Write_With_Ownership_Authorization
{
    private const string DescriptorProject = "ed-fi";
    private const string DescriptorResource = "SchoolTypeDescriptor";
    private const short OwnedToken = 42;
    private const short OtherToken = 7;

    private static readonly DocumentUuid _documentUuid = new(
        Guid.Parse("d1d1d1d1-0000-0000-0000-000000000001")
    );

    private static readonly UpsertActionAuthorization _sharedOwnership =
        UpsertActionAuthorization.SamePolicyForCreateAndUpdate([
            new AuthorizationStrategyEvaluator(
                AuthorizationStrategyNameConstants.OwnershipBased,
                [],
                FilterOperator.And
            ),
        ]);

    private PostgresqlRelationalQueryAuthorizationTestContext _context = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _context = new PostgresqlRelationalQueryAuthorizationTestContext();
        await _context.InitializeAsync(
            RelationshipAuthorizationCrudTestSupport.FixtureRelativePath,
            strict: false,
            replaceReadTargetLookup: false
        );
    }

    [SetUp]
    public async Task SetUp() => await _context.Database.ResetAsync();

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_context is not null)
        {
            await _context.DisposeAsync();
        }
    }

    // ── Create ───────────────────────────────────────────────────────────

    [TestCase(OwnershipAuthorizationFailureKind.StoredOwnershipTokenUninitialized)]
    [TestCase(OwnershipAuthorizationFailureKind.OwnershipTokenMismatch)]
    public async Task It_denies_a_descriptor_create_the_caller_could_not_own_and_writes_no_row(
        OwnershipAuthorizationFailureKind expectedKind
    )
    {
        (await CountRowsAsync()).Should().Be((0, 0));

        var result = await PostAsync(
            "Original",
            _sharedOwnership,
            expectedKind is OwnershipAuthorizationFailureKind.StoredOwnershipTokenUninitialized
                ? null
                : OwnedToken,
            [OtherToken]
        );

        AssertDenied(result, expectedKind);
        (await CountRowsAsync()).Should().Be((0, 0));
    }

    [Test]
    public async Task It_creates_and_stamps_a_descriptor_for_a_client_holding_its_creator_token()
    {
        var result = await PostAsync("Original", _sharedOwnership, OwnedToken, [OtherToken, OwnedToken]);

        result.Should().BeOfType<UpsertResult.InsertSuccess>();
        (await CountRowsAsync()).Should().Be((1, 1));
        (await ReadStoredOwnershipTokenAsync()).Should().Be(OwnedToken);
    }

    // ── POST that updates ────────────────────────────────────────────────

    /// <summary>
    /// A holder of other tokens is refused and nothing moves; the owner updates; and a read/modify-only
    /// client — no creator token, but holding the row's token — updates too, because an update is decided by
    /// the stored stamp alone.
    /// </summary>
    [Test]
    public async Task It_decides_a_descriptor_post_as_update_by_the_stored_stamp()
    {
        await SeedAsync(OwnedToken);
        var before = await ReadStateAsync();

        var foreign = await PostAsync("Changed", _sharedOwnership, OtherToken, [OtherToken]);

        AssertDenied(foreign, OwnershipAuthorizationFailureKind.OwnershipTokenMismatch);
        (await ReadStateAsync()).Should().BeEquivalentTo(before);

        var owner = await PostAsync("Changed", _sharedOwnership, OwnedToken, [OwnedToken]);
        var readModifyOnly = await PostAsync("Changed again", _sharedOwnership, null, [OwnedToken]);

        owner.Should().BeOfType<UpsertResult.UpdateSuccess>();
        readModifyOnly.Should().BeOfType<UpsertResult.UpdateSuccess>();
        (await ReadStoredOwnershipTokenAsync()).Should().Be(OwnedToken);
    }

    [Test]
    public async Task It_denies_a_descriptor_post_as_update_on_a_descriptor_never_stamped()
    {
        await SeedAsync(storedToken: null);
        var before = await ReadStateAsync();

        var result = await PostAsync("Changed", _sharedOwnership, OwnedToken, [OwnedToken]);

        AssertDenied(result, OwnershipAuthorizationFailureKind.StoredOwnershipTokenUninitialized);
        (await ReadStateAsync()).Should().BeEquivalentTo(before);
    }

    // ── PUT ──────────────────────────────────────────────────────────────

    [TestCase(OwnershipAuthorizationFailureKind.OwnershipTokenMismatch)]
    [TestCase(OwnershipAuthorizationFailureKind.StoredOwnershipTokenUninitialized)]
    public async Task It_denies_a_descriptor_put_the_caller_does_not_own_and_moves_nothing(
        OwnershipAuthorizationFailureKind expectedKind
    )
    {
        await SeedAsync(
            expectedKind is OwnershipAuthorizationFailureKind.OwnershipTokenMismatch ? OwnedToken : null
        );
        var before = await ReadStateAsync();

        var result = await PutAsync("Changed", [OtherToken]);

        result
            .Should()
            .BeOfType<UpdateResult.UpdateFailureOwnershipNotAuthorized>()
            .Which.OwnershipFailure.FailureKind.Should()
            .Be(expectedKind);
        (await ReadStateAsync()).Should().BeEquivalentTo(before);
    }

    [Test]
    public async Task It_updates_a_descriptor_through_put_for_its_owner()
    {
        await SeedAsync(OwnedToken);

        var result = await PutAsync("Changed", [OtherToken, OwnedToken]);

        result.Should().BeOfType<UpdateResult.UpdateSuccess>();
        (await ReadStoredOwnershipTokenAsync()).Should().Be(OwnedToken);
    }

    /// <summary>
    /// The stored-stamp check runs against the locked row before the precondition compare, so a non-owner
    /// under a stale If-Match is told only that it does not own the row.
    /// </summary>
    [Test]
    public async Task It_reports_the_descriptor_put_ownership_denial_ahead_of_a_stale_if_match()
    {
        await SeedAsync(OwnedToken);
        var before = await ReadStateAsync();

        var result = await PutAsync("Changed", [OtherToken], ifMatch: "\"stale-etag\"");

        result.Should().BeOfType<UpdateResult.UpdateFailureOwnershipNotAuthorized>();
        (await ReadStateAsync()).Should().BeEquivalentTo(before);
    }

    // ── DELETE ───────────────────────────────────────────────────────────

    /// <summary>
    /// A holder of other tokens and a caller facing a row never stamped are refused and the row stays; only
    /// OwnershipBased is configured, so nothing but the stored-stamp check can stop the delete.
    /// </summary>
    [TestCase(OwnershipAuthorizationFailureKind.OwnershipTokenMismatch)]
    [TestCase(OwnershipAuthorizationFailureKind.StoredOwnershipTokenUninitialized)]
    public async Task It_denies_a_descriptor_delete_the_caller_does_not_own_and_keeps_the_row(
        OwnershipAuthorizationFailureKind expectedKind
    )
    {
        await SeedAsync(
            expectedKind is OwnershipAuthorizationFailureKind.OwnershipTokenMismatch ? OwnedToken : null
        );
        var before = await ReadStateAsync();

        var result = await DeleteAsync([OtherToken]);

        var failure = result
            .Should()
            .BeOfType<DeleteResult.DeleteFailureOwnershipNotAuthorized>()
            .Subject.OwnershipFailure;
        failure.FailureKind.Should().Be(expectedKind);
        failure.StrategyName.Should().Be(AuthorizationStrategyNameConstants.OwnershipBased);
        (await CountRowsAsync()).Should().Be((1, 1));
        (await ReadStateAsync()).Should().BeEquivalentTo(before);
    }

    [Test]
    public async Task It_deletes_a_descriptor_for_its_owner()
    {
        await SeedAsync(OwnedToken);

        var result = await DeleteAsync([OtherToken, OwnedToken]);

        result.Should().BeOfType<DeleteResult.DeleteSuccess>();
        (await CountRowsAsync()).Should().Be((0, 0));
    }

    /// <summary>
    /// A missing target is a 404 even for a caller holding none of the tokens a stamp could carry: there is no
    /// row to check, and a 403 would claim one exists.
    /// </summary>
    [Test]
    public async Task It_reports_not_exists_for_a_missing_descriptor_delete_target_under_ownership()
    {
        var result = await DeleteAsync([OtherToken]);

        result.Should().BeOfType<DeleteResult.DeleteFailureNotExists>();
    }

    /// <summary>
    /// The stored-stamp check runs against the locked row before the If-Match compare, so a non-owner under a
    /// stale ETag is told only that it does not own the row; the owner with the same stale ETag gets the 412,
    /// so the precondition is live and ownership is what decided the first request.
    /// </summary>
    [Test]
    public async Task It_reports_the_descriptor_delete_ownership_denial_ahead_of_a_stale_if_match()
    {
        await SeedAsync(OwnedToken);

        var foreign = await DeleteAsync([OtherToken], ifMatch: "\"stale-etag\"");
        var owner = await DeleteAsync([OwnedToken], ifMatch: "\"stale-etag\"");

        foreign.Should().BeOfType<DeleteResult.DeleteFailureOwnershipNotAuthorized>();
        owner.Should().BeOfType<DeleteResult.DeleteFailureETagMisMatch>();
        (await CountRowsAsync()).Should().Be((1, 1));
    }

    // ── Token cap ────────────────────────────────────────────────────────

    /// <summary>
    /// 2,000 tokens fail a POST in whichever branch its target selects, attributed to that action, and move
    /// nothing; the list holds the creator token, so only the cap is deciding.
    /// </summary>
    [TestCase(false)]
    [TestCase(true)]
    public async Task It_fails_a_descriptor_post_at_the_ownership_token_cap_and_moves_nothing(
        bool targetExists
    )
    {
        if (targetExists)
        {
            await SeedAsync(OwnedToken);
        }

        var before = await CountRowsAsync();

        var result = await PostAsync(
            "Changed",
            _sharedOwnership,
            OwnedToken,
            TokenRange(OwnershipTokenLimitExceededException.OwnershipTokenLimit)
        );

        var failure = result.Should().BeOfType<UpsertResult.UpsertFailureSecurityConfiguration>().Subject;
        failure
            .TargetAction.Should()
            .Be(targetExists ? UpsertTargetAction.Update : UpsertTargetAction.Create);
        failure.Errors.Should().ContainSingle().Which.Should().Contain("2,000");
        (await CountRowsAsync()).Should().Be(before);

        if (targetExists)
        {
            (await ReadStateAsync()).Descriptor().Should().Contain("Original");
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task It_applies_a_descriptor_post_one_below_the_ownership_token_cap(bool targetExists)
    {
        if (targetExists)
        {
            await SeedAsync(OwnedToken);
        }

        var result = await PostAsync(
            "Changed",
            _sharedOwnership,
            OwnedToken,
            TokenRange(OwnershipTokenLimitExceededException.OwnershipTokenLimit - 1)
        );

        result
            .Should()
            .BeOfType(targetExists ? typeof(UpsertResult.UpdateSuccess) : typeof(UpsertResult.InsertSuccess));
        (await CountRowsAsync()).Should().Be((1, 1));
    }

    [Test]
    public async Task It_fails_a_descriptor_put_at_the_ownership_token_cap_and_moves_nothing()
    {
        await SeedAsync(OwnedToken);
        var before = await ReadStateAsync();

        var result = await PutAsync(
            "Changed",
            TokenRange(OwnershipTokenLimitExceededException.OwnershipTokenLimit)
        );

        result
            .Should()
            .BeOfType<UpdateResult.UpdateFailureSecurityConfiguration>()
            .Which.Errors.Should()
            .ContainSingle()
            .Which.Should()
            .Contain("2,000");
        (await ReadStateAsync()).Should().BeEquivalentTo(before);
    }

    [Test]
    public async Task It_fails_a_descriptor_delete_at_the_ownership_token_cap_and_keeps_the_row()
    {
        await SeedAsync(OwnedToken);

        var result = await DeleteAsync(TokenRange(OwnershipTokenLimitExceededException.OwnershipTokenLimit));

        result
            .Should()
            .BeOfType<DeleteResult.DeleteFailureSecurityConfiguration>()
            .Which.Errors.Should()
            .ContainSingle()
            .Which.Should()
            .Contain("2,000");
        (await CountRowsAsync()).Should().Be((1, 1));
    }

    [Test]
    public async Task It_deletes_a_descriptor_one_below_the_ownership_token_cap()
    {
        await SeedAsync(OwnedToken);

        var result = await DeleteAsync(
            TokenRange(OwnershipTokenLimitExceededException.OwnershipTokenLimit - 1)
        );

        result.Should().BeOfType<DeleteResult.DeleteSuccess>();
        (await CountRowsAsync()).Should().Be((0, 0));
    }

    // ── Support ──────────────────────────────────────────────────────────

    private static void AssertDenied(UpsertResult result, OwnershipAuthorizationFailureKind expectedKind)
    {
        var failure = result
            .Should()
            .BeOfType<UpsertResult.UpsertFailureOwnershipNotAuthorized>()
            .Subject.OwnershipFailure;
        failure.FailureKind.Should().Be(expectedKind);
        failure.StrategyName.Should().Be(AuthorizationStrategyNameConstants.OwnershipBased);
    }

    /// <summary>
    /// Seeds the descriptor under <c>NoFurtherAuthorizationRequired</c>, which stamps the creating client's
    /// token, or null for a client with none.
    /// </summary>
    private async Task SeedAsync(short? storedToken)
    {
        var seeded = await PostAsync(
            "Original",
            UpsertActionAuthorizationTestSupport.NoFurtherAuthorizationRequiredForCreateAndUpdate,
            storedToken,
            []
        );
        seeded.Should().BeOfType<UpsertResult.InsertSuccess>();
        (await ReadStoredOwnershipTokenAsync()).Should().Be(storedToken);
    }

    private Task<UpsertResult> PostAsync(
        string shortDescription,
        UpsertActionAuthorization actionAuthorization,
        short? creatorOwnershipTokenId,
        IReadOnlyList<short> ownershipTokenIds
    ) =>
        _context.UpsertWithActionAuthorizationAsync(
            DescriptorProject,
            DescriptorResource,
            Body(shortDescription),
            _documentUuid,
            actionAuthorization,
            creatorOwnershipTokenId: creatorOwnershipTokenId,
            ownershipTokenIds: ownershipTokenIds
        );

    private Task<UpdateResult> PutAsync(
        string shortDescription,
        IReadOnlyList<short> ownershipTokenIds,
        string? ifMatch = null
    ) =>
        _context.UpdateWithAuthorizationAsync(
            DescriptorProject,
            DescriptorResource,
            Body(shortDescription),
            _documentUuid,
            [AuthorizationStrategyNameConstants.OwnershipBased],
            creatorOwnershipTokenId: OtherToken,
            ownershipTokenIds: ownershipTokenIds,
            ifMatch: ifMatch
        );

    private Task<DeleteResult> DeleteAsync(IReadOnlyList<short> ownershipTokenIds, string? ifMatch = null) =>
        _context.DeleteByIdAsync(
            DescriptorProject,
            DescriptorResource,
            _documentUuid,
            [],
            [AuthorizationStrategyNameConstants.OwnershipBased],
            ifMatch: ifMatch,
            ownershipTokenIds: ownershipTokenIds
        );

    private Task<AuthorizationWriteSideEffectState> ReadStateAsync() =>
        _context.ReadSideEffectStateAsync(DescriptorProject, DescriptorResource, _documentUuid);

    private async Task<(long Documents, long Descriptors)> CountRowsAsync() =>
        (
            await _context.CountDocumentsAsync(DescriptorProject, DescriptorResource),
            await _context.Database.ExecuteScalarAsync<long>("""SELECT COUNT(*) FROM "dms"."Descriptor";""")
        );

    private async Task<short?> ReadStoredOwnershipTokenAsync()
    {
        var rows = await _context.Database.QueryRowsAsync(
            """
            SELECT "CreatedByOwnershipTokenId"
            FROM "dms"."Document"
            WHERE "DocumentUuid" = @documentUuid;
            """,
            new NpgsqlParameter("documentUuid", _documentUuid.Value)
        );

        rows.Should().ContainSingle();
        var value = rows[0]["CreatedByOwnershipTokenId"];
        return value is null or DBNull ? null : Convert.ToInt16(value, CultureInfo.InvariantCulture);
    }

    private static short[] TokenRange(int count) =>
        [.. Enumerable.Range(1, count).Select(static tokenId => (short)tokenId)];

    private static JsonNode Body(string shortDescription) =>
        new JsonObject
        {
            ["namespace"] = "uri://ns1.org/SchoolTypeDescriptor",
            ["codeValue"] = "OwnershipWrite",
            ["shortDescription"] = shortDescription,
        };
}

file static class AuthorizationWriteSideEffectStateExtensions
{
    /// <summary>Every value in the descriptor row, for asserting what the stored representation says.</summary>
    public static IEnumerable<string?> Descriptor(this AuthorizationWriteSideEffectState state) =>
        state.ResourceTables.SelectMany(static table => table.Rows).SelectMany(static row => row.Values);
}

// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

// Slice 7 (DMS-1124) Workstream B regression test for cross-path guarded no-op ordinal
// alignment between the no-profile UpsertRequest seed path (RelationalWriteFlattener,
// which stamps 0-based ordinals into collection rows from the request item index) and
// the profile PUT path (ProfileCollectionWalker, which previously stamped 1-based
// ordinals — `i + 1` at ProfileCollectionWalker.cs:415, now `i`). The Slice 6 sibling
// fixture at PostgresqlProfileGuardedNoOpTests.cs:1321 seeds via the profiled POST
// path so seed and PUT exercise the same code path; this fixture inverts the seed
// path: it seeds via the no-profile UpsertRequest path so the persisted addresses
// land at 0-based ordinals, then issues a byte-identical profiled PUT carrying the
// same VisiblePresent profile context. With the post-merge effective rowset matching
// the stored rowset on row identity and content the guarded no-op short-circuit
// fires — no DML against edfi.SchoolAddress, no Document version/timestamp metadata
// mutation, no DocumentChangeEvent row. Before the Workstream B alignment, this
// fixture failed because the executor fell through to real collection DML and
// returned UpdateSuccess WITHOUT the no-op short-circuit.

using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using Npgsql;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

file static class ProfileGuardedNoOpOrdinalAlignmentIntegrationTestSupport
{
    public static async Task<ProfileGuardedNoOpPersistedState> ReadPersistedStateAsync(
        PostgresqlGeneratedDdlTestDatabase database,
        Guid documentUuid,
        Func<
            PostgresqlGeneratedDdlTestDatabase,
            long,
            Task<IReadOnlyDictionary<string, object?>>
        > readRootRowByDocumentId
    ) =>
        await ProfileGuardedNoOpPersistedStateSupport
            .ReadPersistedStateAsync(
                database,
                documentUuid,
                ReadDocumentRowsAsync,
                readRootRowByDocumentId,
                ReadDocumentChangeEventRowsAsync
            )
            .ConfigureAwait(false);

    private static async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ReadDocumentRowsAsync(
        PostgresqlGeneratedDdlTestDatabase database,
        Guid documentUuid
    ) =>
        await database
            .QueryRowsAsync(
                """
                SELECT "DocumentId", "DocumentUuid", "ResourceKeyId",
                       "ContentVersion", "ContentLastModifiedAt",
                       "IdentityVersion", "IdentityLastModifiedAt"
                FROM "dms"."Document"
                WHERE "DocumentUuid" = @documentUuid;
                """,
                new NpgsqlParameter("documentUuid", documentUuid)
            )
            .ConfigureAwait(false);

    private static async Task<
        IReadOnlyList<IReadOnlyDictionary<string, object?>>
    > ReadDocumentChangeEventRowsAsync(PostgresqlGeneratedDdlTestDatabase database, long documentId) =>
        await database
            .QueryRowsAsync(
                """
                SELECT COUNT(*) AS "RowCount"
                FROM "dms"."DocumentChangeEvent"
                WHERE "DocumentId" = @documentId;
                """,
                new NpgsqlParameter("documentId", documentId)
            )
            .ConfigureAwait(false);
}

/// <summary>
/// Slice 7 (DMS-1124) Workstream B1 regression test. Seeds a <c>School</c> document
/// via the NO-PROFILE <see cref="PostgresqlProfileTopLevelCollectionMergeSupport.SeedAsync"/>
/// helper so the persisted <c>edfi.SchoolAddress</c> rows land at the 0-based ordinals
/// the <c>RelationalWriteFlattener</c> stamps from the request item index, then issues
/// a profiled <c>PUT</c> carrying a byte-identical body and the same fully-VisiblePresent
/// profile context the Slice 6 top-level-collection sibling uses. With Workstream B2's
/// fix to <c>ProfileCollectionWalker.cs:415</c> the merge synthesizer stamps the same
/// 0-based ordinals, the executor's positional <c>SequenceEqual</c> between merged
/// rows and stored rows succeeds, and the guarded no-op short-circuit fires — neither
/// root row, nor collection row count, nor collection row contents (including
/// <c>CollectionItemId</c> and <c>Ordinal</c>), nor Document version/timestamp
/// metadata, nor a <c>DocumentChangeEvent</c> row may be written. Before B2 lands the
/// merge synthesizer stamps 1-based ordinals against the 0-based stored rows, the
/// positional comparison fails, the executor issues real collection DML, and this
/// fixture's no-op invariants fail — exactly the failure mode the fix resolves.
/// </summary>
[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
internal class Given_A_Postgresql_Relational_Profile_Guarded_No_Op_Put_With_Top_Level_Collection_Created_Via_No_Profile_Path
    : CollectionShapeProfileGuardedNoOpFixtureBase
{
    private static readonly DocumentUuid DocumentUuid = new(
        Guid.Parse("eeeeeeee-0000-0000-0000-000000000020")
    );

    private ProfileGuardedNoOpPersistedState _stateBeforeUpdate = null!;
    private ProfileGuardedNoOpPersistedState _stateAfterUpdate = null!;
    private UpdateResult _updateResult = null!;
    private long _addressCountBefore;
    private long _addressCountAfter;
    private IReadOnlyList<PostgresqlProfileTopLevelCollectionAddressRow> _addressesBefore = null!;
    private IReadOnlyList<PostgresqlProfileTopLevelCollectionAddressRow> _addressesAfter = null!;

    /// <summary>
    /// Overrides the Slice 6 base's profiled-POST seed path with the no-profile
    /// UpsertRequest seed path (<see cref="PostgresqlProfileTopLevelCollectionMergeSupport.SeedAsync"/>)
    /// so the persisted <c>edfi.SchoolAddress</c> rows carry 0-based ordinals stamped
    /// by <c>RelationalWriteFlattener</c>. This is the cross-path inversion this
    /// regression test pins: seed via no-profile (0-based), update via profile (which
    /// must stamp the same 0-base after Workstream B2's fix).
    /// </summary>
    protected override async Task ExecuteProfiledShapeCreateAsync(DocumentUuid documentUuid)
    {
        var seedBody = IdenticalRequestBody.DeepClone();
        var seedResult = await PostgresqlProfileTopLevelCollectionMergeSupport.SeedAsync(
            _serviceProvider,
            _database,
            _mappingSet,
            DefaultSchoolId,
            seedBody,
            documentUuid,
            "pg-profile-guarded-no-op-top-level-collection-no-profile-seed"
        );
        seedResult.Should().BeOfType<UpsertResult.InsertSuccess>();
    }

    protected override async Task SetUpTestAsync()
    {
        await ExecuteProfiledShapeCreateAsync(DocumentUuid);
        var documentId = await PostgresqlProfileTopLevelCollectionMergeSupport.ReadDocumentIdAsync(
            _database,
            DocumentUuid
        );
        _stateBeforeUpdate =
            await ProfileGuardedNoOpOrdinalAlignmentIntegrationTestSupport.ReadPersistedStateAsync(
                _database,
                DocumentUuid.Value,
                ReadShapeRootRowByDocumentIdAsync
            );
        _addressCountBefore = await PostgresqlProfileTopLevelCollectionMergeSupport.ReadAddressCountAsync(
            _database
        );
        _addressesBefore = await PostgresqlProfileTopLevelCollectionMergeSupport.ReadAddressesAsync(
            _database,
            documentId
        );

        _updateResult = await ExecuteProfiledShapeIdenticalPutAsync(DocumentUuid);

        _stateAfterUpdate =
            await ProfileGuardedNoOpOrdinalAlignmentIntegrationTestSupport.ReadPersistedStateAsync(
                _database,
                DocumentUuid.Value,
                ReadShapeRootRowByDocumentIdAsync
            );
        _addressCountAfter = await PostgresqlProfileTopLevelCollectionMergeSupport.ReadAddressCountAsync(
            _database
        );
        _addressesAfter = await PostgresqlProfileTopLevelCollectionMergeSupport.ReadAddressesAsync(
            _database,
            documentId
        );
    }

    [Test]
    public void It_returns_guarded_no_op_success()
    {
        _updateResult.Should().BeOfType<UpdateResult.UpdateSuccess>();
        _updateResult.As<UpdateResult.UpdateSuccess>().ExistingDocumentUuid.Should().Be(DocumentUuid);
    }

    [Test]
    public void It_does_not_modify_the_addresses_collection()
    {
        _addressCountAfter.Should().Be(_addressCountBefore);
        _addressesAfter.Should().BeEquivalentTo(_addressesBefore);
    }

    [Test]
    public void It_does_not_modify_the_root_school_row()
    {
        _stateAfterUpdate.RootRow.Should().BeEquivalentTo(_stateBeforeUpdate.RootRow);
    }

    [Test]
    public void It_does_not_change_content_version()
    {
        _stateAfterUpdate.Document.ContentVersion.Should().Be(_stateBeforeUpdate.Document.ContentVersion);
    }

    [Test]
    public void It_does_not_emit_a_document_change_event_row()
    {
        _stateAfterUpdate.DocumentChangeEventCount.Should().Be(_stateBeforeUpdate.DocumentChangeEventCount);
    }
}

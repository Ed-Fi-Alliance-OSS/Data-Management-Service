// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

// Pins the cross-path guarded no-op invariant: a top-level collection document seeded
// via the no-profile UpsertRequest path (which stamps 0-based ordinals from the
// request item index) followed by a byte-identical profiled PUT must hit the guarded
// no-op short-circuit. The merged rowset must match the stored rowset on row identity
// and content so the executor's positional SequenceEqual succeeds — no DML against
// edfi.SchoolAddress, no Document version/timestamp mutation, no DocumentChangeEvent
// row.

using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

/// <summary>
/// Pins the cross-path guarded no-op invariant for top-level collections. Seeds a
/// <c>School</c> document via the no-profile <see cref="PostgresqlProfileTopLevelCollectionMergeSupport.SeedAsync"/>
/// helper so the persisted <c>edfi.SchoolAddress</c> rows land at the 0-based ordinals
/// <c>RelationalWriteFlattener</c> stamps from the request item index, then issues a
/// profiled <c>PUT</c> with a byte-identical body and a fully-VisiblePresent profile
/// context. The merged rowset must match the stored rowset on row identity and content
/// so the executor's positional <c>SequenceEqual</c> succeeds and the guarded no-op
/// short-circuit fires — neither root row, nor collection row count, nor collection
/// row contents (including <c>CollectionItemId</c> and <c>Ordinal</c>), nor Document
/// version/timestamp metadata, nor a <c>DocumentChangeEvent</c> row may be written.
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
    /// Overrides the base's profiled-POST seed with the no-profile UpsertRequest seed
    /// (<see cref="PostgresqlProfileTopLevelCollectionMergeSupport.SeedAsync"/>) so the
    /// persisted <c>edfi.SchoolAddress</c> rows carry the 0-based ordinals
    /// <c>RelationalWriteFlattener</c> stamps. This is the cross-path inversion the
    /// regression pins: seed via no-profile (0-based), update via profile (which must
    /// stamp the same 0-base for the no-op invariant to hold).
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
        _stateBeforeUpdate = await ProfileGuardedNoOpIntegrationTestSupport.ReadPersistedStateAsync(
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

        _stateAfterUpdate = await ProfileGuardedNoOpIntegrationTestSupport.ReadPersistedStateAsync(
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

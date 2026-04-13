// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
[Parallelizable]
public class Given_Relational_Write_Persister
{
    private RelationalWritePersister _sut = null!;

    [SetUp]
    public void Setup()
    {
        _sut = new RelationalWritePersister();
    }

    [Test]
    public async Task It_inserts_document_root_and_root_extension_rows_for_create_requests()
    {
        var rootPlan = CreateRootPlan();
        var rootExtensionPlan = CreateRootExtensionPlan();
        var writePlan = CreateWritePlan([rootPlan, rootExtensionPlan]);
        var request = CreateRequest(writePlan, RelationalWriteOperationKind.Post);
        var mergeResult = new RelationalWriteNoProfileMergeResult([
            new RelationalWriteNoProfileTableState(
                rootPlan,
                [],
                [CreateRow(FlattenedWriteValue.UnresolvedRootDocumentId.Instance, 255901, "Lincoln High")]
            ),
            new RelationalWriteNoProfileTableState(
                rootExtensionPlan,
                [],
                [CreateRow(FlattenedWriteValue.UnresolvedRootDocumentId.Instance, "BLUE")]
            ),
        ]);
        var writeSession = new RecordingRelationalWriteSession([
            new CommandResponse(ScalarResult: 910L),
            new CommandResponse(),
            new CommandResponse(),
        ]);

        var result = await _sut.PersistAsync(request, mergeResult, writeSession);

        result
            .Should()
            .Be(
                new RelationalWritePersistResult(
                    910L,
                    new DocumentUuid(Guid.Parse("cccccccc-1111-2222-3333-dddddddddddd"))
                )
            );
        writeSession.Commands.Should().HaveCount(3);
        writeSession.Commands[0].CommandText.Should().Contain("INSERT INTO dms.\"Document\"");
        GetParameterValue(writeSession.Commands[0], "@documentUuid")
            .Should()
            .Be(Guid.Parse("cccccccc-1111-2222-3333-dddddddddddd"));
        GetParameterValue(writeSession.Commands[0], "@resourceKeyId").Should().Be((short)1);

        writeSession.Commands[1].CommandText.Should().Be(rootPlan.InsertSql);
        GetParameterValue(writeSession.Commands[1], "@DocumentId").Should().Be(910L);
        GetParameterValue(writeSession.Commands[1], "@SchoolId").Should().Be(255901);
        GetParameterValue(writeSession.Commands[1], "@Name").Should().Be("Lincoln High");

        writeSession.Commands[2].CommandText.Should().Be(rootExtensionPlan.InsertSql);
        GetParameterValue(writeSession.Commands[2], "@DocumentId").Should().Be(910L);
        GetParameterValue(writeSession.Commands[2], "@ExtensionCode").Should().Be("BLUE");
    }

    [Test]
    public async Task It_updates_matched_rows_and_clears_inlined_scope_columns_for_existing_document_requests()
    {
        var rootPlan = CreateRootPlan(includeShortName: true);
        var writePlan = CreateWritePlan([rootPlan]);
        var request = CreateRequest(writePlan, RelationalWriteOperationKind.Put);
        var mergeResult = new RelationalWriteNoProfileMergeResult([
            new RelationalWriteNoProfileTableState(
                rootPlan,
                [CreateRow(345L, 255901, "Lincoln High", "LHS")],
                [CreateRow(345L, 255901, "Lincoln High Updated", null)]
            ),
        ]);
        var writeSession = new RecordingRelationalWriteSession([new CommandResponse()]);

        var result = await _sut.PersistAsync(request, mergeResult, writeSession);

        result
            .Should()
            .Be(
                new RelationalWritePersistResult(
                    345L,
                    new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"))
                )
            );
        writeSession.Commands.Should().ContainSingle();
        writeSession.Commands[0].CommandText.Should().Be(rootPlan.UpdateSql);
        GetParameterValue(writeSession.Commands[0], "@DocumentId").Should().Be(345L);
        GetParameterValue(writeSession.Commands[0], "@SchoolId").Should().Be(255901);
        GetParameterValue(writeSession.Commands[0], "@Name").Should().Be("Lincoln High Updated");
        GetParameterValue(writeSession.Commands[0], "@ShortName").Should().BeNull();
    }

    [Test]
    public async Task It_deletes_omitted_separate_table_rows_when_scope_becomes_absent()
    {
        var rootPlan = CreateRootPlan();
        var rootExtensionPlan = CreateRootExtensionPlan();
        var writePlan = CreateWritePlan([rootPlan, rootExtensionPlan]);
        var request = CreateRequest(writePlan, RelationalWriteOperationKind.Put);
        var mergeResult = new RelationalWriteNoProfileMergeResult([
            new RelationalWriteNoProfileTableState(
                rootPlan,
                [CreateRow(345L, 255901, "Lincoln High")],
                [CreateRow(345L, 255901, "Lincoln High")]
            ),
            new RelationalWriteNoProfileTableState(rootExtensionPlan, [CreateRow(345L, "BLUE")], []),
        ]);
        var writeSession = new RecordingRelationalWriteSession([new CommandResponse()]);

        await _sut.PersistAsync(request, mergeResult, writeSession);
        writeSession.Commands.Should().ContainSingle();
        writeSession.Commands[0].CommandText.Should().Be(rootExtensionPlan.DeleteByParentSql);
        GetParameterValue(writeSession.Commands[0], "@DocumentId").Should().Be(345L);
        GetParameterValue(writeSession.Commands[0], "@ExtensionCode").Should().Be("BLUE");
    }

    [Test]
    public async Task It_updates_collection_aligned_one_to_one_extension_scopes_when_base_collection_rows_are_unchanged()
    {
        var rootPlan = CreateRootPlan();
        var collectionPlan = CreateCollectionPlan();
        var collectionExtensionScopePlan = CreateCollectionExtensionScopePlan();
        var writePlan = CreateWritePlan([rootPlan, collectionPlan, collectionExtensionScopePlan]);
        var request = CreateRequest(writePlan, RelationalWriteOperationKind.Put);
        var mergeResult = new RelationalWriteNoProfileMergeResult([
            new RelationalWriteNoProfileTableState(
                rootPlan,
                [CreateRow(345L, 255901, "Lincoln High")],
                [CreateRow(345L, 255901, "Lincoln High")]
            ),
            new RelationalWriteNoProfileTableState(
                collectionPlan,
                [CreateRow(44L, 345L, 0, "Mailing")],
                [CreateRow(44L, 345L, 0, "Mailing")]
            ),
            new RelationalWriteNoProfileTableState(
                collectionExtensionScopePlan,
                [CreateRow(44L, "Blue")],
                [CreateRow(44L, "Red")]
            ),
        ]);
        var writeSession = new RecordingRelationalWriteSession([new CommandResponse()]);

        await _sut.PersistAsync(request, mergeResult, writeSession);
        writeSession.Commands.Should().ContainSingle();
        writeSession.Commands[0].CommandText.Should().Be(collectionExtensionScopePlan.UpdateSql);
        GetParameterValue(writeSession.Commands[0], "@BaseCollectionItemId").Should().Be(44L);
        GetParameterValue(writeSession.Commands[0], "@FavoriteColor").Should().Be("Red");
    }

    [Test]
    public async Task It_batches_collection_aligned_extension_scope_inserts_by_parent_row_identity()
    {
        var rootPlan = CreateRootPlan();
        var collectionPlan = CreateCollectionPlan();
        var collectionExtensionScopePlan = CreateCollectionExtensionScopePlan() with
        {
            BulkInsertBatching = new BulkInsertBatchingInfo(
                MaxRowsPerBatch: 2,
                ParametersPerRow: 2,
                MaxParametersPerCommand: 4
            ),
        };
        var writePlan = CreateWritePlan([rootPlan, collectionPlan, collectionExtensionScopePlan]);
        var request = CreateRequest(writePlan, RelationalWriteOperationKind.Put);
        var mergeResult = new RelationalWriteNoProfileMergeResult([
            new RelationalWriteNoProfileTableState(
                rootPlan,
                [CreateRow(345L, 255901, "Lincoln High")],
                [CreateRow(345L, 255901, "Lincoln High")]
            ),
            new RelationalWriteNoProfileTableState(
                collectionPlan,
                [
                    CreateRow(44L, 345L, 0, "Mailing"),
                    CreateRow(45L, 345L, 1, "Home"),
                    CreateRow(46L, 345L, 2, "Work"),
                    CreateRow(47L, 345L, 3, "Shipping"),
                ],
                [
                    CreateRow(44L, 345L, 0, "Mailing"),
                    CreateRow(45L, 345L, 1, "Home"),
                    CreateRow(46L, 345L, 2, "Work"),
                    CreateRow(47L, 345L, 3, "Shipping"),
                ]
            ),
            new RelationalWriteNoProfileTableState(
                collectionExtensionScopePlan,
                [],
                [
                    CreateRow(44L, "Blue"),
                    CreateRow(45L, "Red"),
                    CreateRow(46L, "Orange"),
                    CreateRow(47L, "Purple"),
                ]
            ),
        ]);
        var writeSession = new RecordingRelationalWriteSession([
            new CommandResponse(),
            new CommandResponse(),
        ]);
        var batchSqlEmitter = new WritePlanBatchSqlEmitter(SqlDialect.Pgsql);

        await _sut.PersistAsync(request, mergeResult, writeSession);
        writeSession.Commands.Should().HaveCount(2);
        writeSession
            .Commands[0]
            .CommandText.Should()
            .Be(batchSqlEmitter.EmitInsertBatch(collectionExtensionScopePlan, 2));
        writeSession.Commands[0].Parameters.Should().HaveCount(4);
        GetParameterValue(writeSession.Commands[0], "@BaseCollectionItemId_0").Should().Be(44L);
        GetParameterValue(writeSession.Commands[0], "@FavoriteColor_0").Should().Be("Blue");
        GetParameterValue(writeSession.Commands[0], "@BaseCollectionItemId_1").Should().Be(45L);
        GetParameterValue(writeSession.Commands[0], "@FavoriteColor_1").Should().Be("Red");

        writeSession
            .Commands[1]
            .CommandText.Should()
            .Be(batchSqlEmitter.EmitInsertBatch(collectionExtensionScopePlan, 2));
        writeSession.Commands[1].Parameters.Should().HaveCount(4);
        GetParameterValue(writeSession.Commands[1], "@BaseCollectionItemId_0").Should().Be(46L);
        GetParameterValue(writeSession.Commands[1], "@FavoriteColor_0").Should().Be("Orange");
        GetParameterValue(writeSession.Commands[1], "@BaseCollectionItemId_1").Should().Be(47L);
        GetParameterValue(writeSession.Commands[1], "@FavoriteColor_1").Should().Be("Purple");
    }

    [Test]
    public async Task It_batches_collection_aligned_extension_scope_updates_by_parent_row_identity()
    {
        var rootPlan = CreateRootPlan();
        var collectionPlan = CreateCollectionPlan();
        var collectionExtensionScopePlan = CreateCollectionExtensionScopePlan() with
        {
            BulkInsertBatching = new BulkInsertBatchingInfo(
                MaxRowsPerBatch: 2,
                ParametersPerRow: 2,
                MaxParametersPerCommand: 4
            ),
        };
        var writePlan = CreateWritePlan([rootPlan, collectionPlan, collectionExtensionScopePlan]);
        var request = CreateRequest(writePlan, RelationalWriteOperationKind.Put);
        var mergeResult = new RelationalWriteNoProfileMergeResult([
            new RelationalWriteNoProfileTableState(
                rootPlan,
                [CreateRow(345L, 255901, "Lincoln High")],
                [CreateRow(345L, 255901, "Lincoln High")]
            ),
            new RelationalWriteNoProfileTableState(
                collectionPlan,
                [
                    CreateRow(44L, 345L, 0, "Mailing"),
                    CreateRow(45L, 345L, 1, "Home"),
                    CreateRow(46L, 345L, 2, "Work"),
                    CreateRow(47L, 345L, 3, "Shipping"),
                ],
                [
                    CreateRow(44L, 345L, 0, "Mailing"),
                    CreateRow(45L, 345L, 1, "Home"),
                    CreateRow(46L, 345L, 2, "Work"),
                    CreateRow(47L, 345L, 3, "Shipping"),
                ]
            ),
            new RelationalWriteNoProfileTableState(
                collectionExtensionScopePlan,
                [
                    CreateRow(44L, "Blue"),
                    CreateRow(45L, "Green"),
                    CreateRow(46L, "Orange"),
                    CreateRow(47L, "Purple"),
                ],
                [
                    CreateRow(44L, "Blue-Updated"),
                    CreateRow(45L, "Green-Updated"),
                    CreateRow(46L, "Orange-Updated"),
                    CreateRow(47L, "Purple-Updated"),
                ]
            ),
        ]);
        var writeSession = new RecordingRelationalWriteSession([
            new CommandResponse(),
            new CommandResponse(),
        ]);
        var batchSqlEmitter = new WritePlanBatchSqlEmitter(SqlDialect.Pgsql);

        await _sut.PersistAsync(request, mergeResult, writeSession);
        writeSession.Commands.Should().HaveCount(2);
        writeSession
            .Commands[0]
            .CommandText.Should()
            .Be(batchSqlEmitter.EmitUpdateBatch(collectionExtensionScopePlan, 2));
        writeSession.Commands[0].Parameters.Should().HaveCount(4);
        GetParameterValue(writeSession.Commands[0], "@BaseCollectionItemId_0").Should().Be(44L);
        GetParameterValue(writeSession.Commands[0], "@FavoriteColor_0").Should().Be("Blue-Updated");
        GetParameterValue(writeSession.Commands[0], "@BaseCollectionItemId_1").Should().Be(45L);
        GetParameterValue(writeSession.Commands[0], "@FavoriteColor_1").Should().Be("Green-Updated");

        writeSession
            .Commands[1]
            .CommandText.Should()
            .Be(batchSqlEmitter.EmitUpdateBatch(collectionExtensionScopePlan, 2));
        writeSession.Commands[1].Parameters.Should().HaveCount(4);
        GetParameterValue(writeSession.Commands[1], "@BaseCollectionItemId_0").Should().Be(46L);
        GetParameterValue(writeSession.Commands[1], "@FavoriteColor_0").Should().Be("Orange-Updated");
        GetParameterValue(writeSession.Commands[1], "@BaseCollectionItemId_1").Should().Be(47L);
        GetParameterValue(writeSession.Commands[1], "@FavoriteColor_1").Should().Be("Purple-Updated");
    }

    [Test]
    public async Task It_batches_collection_aligned_extension_scope_deletes_by_parent_row_identity()
    {
        var rootPlan = CreateRootPlan();
        var collectionPlan = CreateCollectionPlan();
        var collectionExtensionScopePlan = CreateCollectionExtensionScopePlan() with
        {
            BulkInsertBatching = new BulkInsertBatchingInfo(
                MaxRowsPerBatch: 2,
                ParametersPerRow: 2,
                MaxParametersPerCommand: 4
            ),
        };
        var writePlan = CreateWritePlan([rootPlan, collectionPlan, collectionExtensionScopePlan]);
        var request = CreateRequest(writePlan, RelationalWriteOperationKind.Put);
        var mergeResult = new RelationalWriteNoProfileMergeResult([
            new RelationalWriteNoProfileTableState(
                rootPlan,
                [CreateRow(345L, 255901, "Lincoln High")],
                [CreateRow(345L, 255901, "Lincoln High")]
            ),
            new RelationalWriteNoProfileTableState(
                collectionPlan,
                [
                    CreateRow(44L, 345L, 0, "Mailing"),
                    CreateRow(45L, 345L, 1, "Home"),
                    CreateRow(46L, 345L, 2, "Work"),
                    CreateRow(47L, 345L, 3, "Shipping"),
                ],
                [
                    CreateRow(44L, 345L, 0, "Mailing"),
                    CreateRow(45L, 345L, 1, "Home"),
                    CreateRow(46L, 345L, 2, "Work"),
                    CreateRow(47L, 345L, 3, "Shipping"),
                ]
            ),
            new RelationalWriteNoProfileTableState(
                collectionExtensionScopePlan,
                [
                    CreateRow(44L, "Blue"),
                    CreateRow(45L, "Green"),
                    CreateRow(46L, "Orange"),
                    CreateRow(47L, "Purple"),
                ],
                []
            ),
        ]);
        var writeSession = new RecordingRelationalWriteSession([
            new CommandResponse(),
            new CommandResponse(),
        ]);
        var batchSqlEmitter = new WritePlanBatchSqlEmitter(SqlDialect.Pgsql);

        await _sut.PersistAsync(request, mergeResult, writeSession);
        writeSession.Commands.Should().HaveCount(2);
        writeSession
            .Commands[0]
            .CommandText.Should()
            .Be(batchSqlEmitter.EmitDeleteByParentBatch(collectionExtensionScopePlan, 2));
        writeSession.Commands[0].Parameters.Should().HaveCount(4);
        GetParameterValue(writeSession.Commands[0], "@BaseCollectionItemId_0").Should().Be(44L);
        GetParameterValue(writeSession.Commands[0], "@FavoriteColor_0").Should().Be("Blue");
        GetParameterValue(writeSession.Commands[0], "@BaseCollectionItemId_1").Should().Be(45L);
        GetParameterValue(writeSession.Commands[0], "@FavoriteColor_1").Should().Be("Green");

        writeSession
            .Commands[1]
            .CommandText.Should()
            .Be(batchSqlEmitter.EmitDeleteByParentBatch(collectionExtensionScopePlan, 2));
        writeSession.Commands[1].Parameters.Should().HaveCount(4);
        GetParameterValue(writeSession.Commands[1], "@BaseCollectionItemId_0").Should().Be(46L);
        GetParameterValue(writeSession.Commands[1], "@FavoriteColor_0").Should().Be("Orange");
        GetParameterValue(writeSession.Commands[1], "@BaseCollectionItemId_1").Should().Be(47L);
        GetParameterValue(writeSession.Commands[1], "@FavoriteColor_1").Should().Be("Purple");
    }

    [Test]
    public async Task It_defers_collection_aligned_extension_scope_inserts_until_parent_collection_ids_are_reserved()
    {
        var rootPlan = CreateRootPlan();
        var collectionPlan = CreateCollectionPlan();
        var collectionExtensionScopePlan = CreateCollectionExtensionScopePlan();
        var writePlan = CreateWritePlan([rootPlan, collectionExtensionScopePlan, collectionPlan]);
        var request = CreateRequest(writePlan, RelationalWriteOperationKind.Put);
        var addressCollectionItemId = NewCollectionItemId();
        var mergeResult = new RelationalWriteNoProfileMergeResult([
            new RelationalWriteNoProfileTableState(
                rootPlan,
                [CreateRow(345L, 255901, "Lincoln High")],
                [CreateRow(345L, 255901, "Lincoln High")]
            ),
            new RelationalWriteNoProfileTableState(
                collectionExtensionScopePlan,
                [],
                [CreateRow(addressCollectionItemId, "Blue")]
            ),
            new RelationalWriteNoProfileTableState(
                collectionPlan,
                [],
                [CreateRow(addressCollectionItemId, 345L, 0, "Home")]
            ),
        ]);
        var writeSession = new RecordingRelationalWriteSession([
            new CommandResponse(ScalarResult: 910L),
            new CommandResponse(),
            new CommandResponse(),
        ]);

        await _sut.PersistAsync(request, mergeResult, writeSession);
        writeSession.Commands.Should().HaveCount(3);
        writeSession.Commands[0].CommandText.Should().Contain("CollectionItemIdSequence");
        writeSession.Commands[1].CommandText.Should().Be(collectionPlan.InsertSql);
        GetParameterValue(writeSession.Commands[1], "@CollectionItemId").Should().Be(910L);
        GetParameterValue(writeSession.Commands[1], "@School_DocumentId").Should().Be(345L);
        GetParameterValue(writeSession.Commands[1], "@Ordinal").Should().Be(0);
        GetParameterValue(writeSession.Commands[1], "@AddressType").Should().Be("Home");

        writeSession.Commands[2].CommandText.Should().Be(collectionExtensionScopePlan.InsertSql);
        GetParameterValue(writeSession.Commands[2], "@BaseCollectionItemId").Should().Be(910L);
        GetParameterValue(writeSession.Commands[2], "@FavoriteColor").Should().Be("Blue");
    }

    [Test]
    public async Task It_deletes_updates_and_inserts_collection_aligned_extension_scope_rows_by_parent_row_identity()
    {
        var rootPlan = CreateRootPlan();
        var collectionPlan = CreateCollectionPlan();
        var collectionExtensionScopePlan = CreateCollectionExtensionScopePlan();
        var writePlan = CreateWritePlan([rootPlan, collectionPlan, collectionExtensionScopePlan]);
        var request = CreateRequest(writePlan, RelationalWriteOperationKind.Put);
        var mergeResult = new RelationalWriteNoProfileMergeResult([
            new RelationalWriteNoProfileTableState(
                rootPlan,
                [CreateRow(345L, 255901, "Lincoln High")],
                [CreateRow(345L, 255901, "Lincoln High")]
            ),
            new RelationalWriteNoProfileTableState(
                collectionPlan,
                [
                    CreateRow(44L, 345L, 0, "Mailing"),
                    CreateRow(45L, 345L, 1, "Home"),
                    CreateRow(46L, 345L, 2, "Work"),
                ],
                [
                    CreateRow(44L, 345L, 0, "Mailing"),
                    CreateRow(45L, 345L, 1, "Home"),
                    CreateRow(46L, 345L, 2, "Work"),
                ]
            ),
            new RelationalWriteNoProfileTableState(
                collectionExtensionScopePlan,
                [CreateRow(44L, "Blue"), CreateRow(45L, "Green")],
                [CreateRow(44L, "Purple"), CreateRow(46L, "Orange")]
            ),
        ]);
        var writeSession = new RecordingRelationalWriteSession([
            new CommandResponse(),
            new CommandResponse(),
            new CommandResponse(),
        ]);

        await _sut.PersistAsync(request, mergeResult, writeSession);
        writeSession.Commands.Should().HaveCount(3);

        writeSession.Commands[0].CommandText.Should().Be(collectionExtensionScopePlan.DeleteByParentSql);
        GetParameterValue(writeSession.Commands[0], "@BaseCollectionItemId").Should().Be(45L);
        GetParameterValue(writeSession.Commands[0], "@FavoriteColor").Should().Be("Green");

        writeSession.Commands[1].CommandText.Should().Be(collectionExtensionScopePlan.UpdateSql);
        GetParameterValue(writeSession.Commands[1], "@BaseCollectionItemId").Should().Be(44L);
        GetParameterValue(writeSession.Commands[1], "@FavoriteColor").Should().Be("Purple");

        writeSession.Commands[2].CommandText.Should().Be(collectionExtensionScopePlan.InsertSql);
        GetParameterValue(writeSession.Commands[2], "@BaseCollectionItemId").Should().Be(46L);
        GetParameterValue(writeSession.Commands[2], "@FavoriteColor").Should().Be("Orange");
    }

    [Test]
    public async Task It_deletes_updates_and_inserts_base_collection_rows_using_stable_row_identity()
    {
        var rootPlan = CreateRootPlan();
        var collectionPlan = CreateCollectionPlan();
        var writePlan = CreateWritePlan([rootPlan, collectionPlan]);
        var request = CreateRequest(writePlan, RelationalWriteOperationKind.Put);
        var mergeResult = new RelationalWriteNoProfileMergeResult([
            new RelationalWriteNoProfileTableState(
                rootPlan,
                [CreateRow(345L, 255901, "Lincoln High")],
                [CreateRow(345L, 255901, "Lincoln High")]
            ),
            new RelationalWriteNoProfileTableState(
                collectionPlan,
                [CreateRow(44L, 345L, 0, "Mailing"), CreateRow(45L, 345L, 1, "Home")],
                [CreateRow(45L, 345L, 0, "Home"), CreateRow(NewCollectionItemId(), 345L, 1, "Physical")]
            ),
        ]);
        var writeSession = new RecordingRelationalWriteSession([
            new CommandResponse(),
            new CommandResponse(),
            new CommandResponse(ScalarResult: 91L),
            new CommandResponse(),
        ]);

        await _sut.PersistAsync(request, mergeResult, writeSession);
        writeSession.Commands.Should().HaveCount(4);

        writeSession
            .Commands[0]
            .CommandText.Should()
            .Be(collectionPlan.CollectionMergePlan!.DeleteByStableRowIdentitySql);
        GetParameterValue(writeSession.Commands[0], "@CollectionItemId").Should().Be(44L);

        writeSession
            .Commands[1]
            .CommandText.Should()
            .Be(collectionPlan.CollectionMergePlan!.UpdateByStableRowIdentitySql);
        GetParameterValue(writeSession.Commands[1], "@CollectionItemId").Should().Be(45L);
        GetParameterValue(writeSession.Commands[1], "@School_DocumentId").Should().Be(345L);
        GetParameterValue(writeSession.Commands[1], "@Ordinal").Should().Be(0);
        GetParameterValue(writeSession.Commands[1], "@AddressType").Should().Be("Home");

        writeSession.Commands[2].CommandText.Should().Contain("CollectionItemIdSequence");
        writeSession.Commands[3].CommandText.Should().Be(collectionPlan.InsertSql);
        GetParameterValue(writeSession.Commands[3], "@CollectionItemId").Should().Be(91L);
        GetParameterValue(writeSession.Commands[3], "@School_DocumentId").Should().Be(345L);
        GetParameterValue(writeSession.Commands[3], "@Ordinal").Should().Be(1);
        GetParameterValue(writeSession.Commands[3], "@AddressType").Should().Be("Physical");
    }

    [Test]
    public async Task It_reserves_collection_ids_for_nested_base_collection_inserts()
    {
        var rootPlan = CreateRootPlan();
        var addressPlan = CreateCollectionPlan();
        var periodPlan = CreatePeriodPlan();
        var writePlan = CreateWritePlan([rootPlan, addressPlan, periodPlan]);
        var request = CreateRequest(writePlan, RelationalWriteOperationKind.Put);
        var addressCollectionItemId = NewCollectionItemId();
        var periodCollectionItemId = NewCollectionItemId();
        var mergeResult = new RelationalWriteNoProfileMergeResult([
            new RelationalWriteNoProfileTableState(
                rootPlan,
                [CreateRow(345L, 255901, "Lincoln High")],
                [CreateRow(345L, 255901, "Lincoln High")]
            ),
            new RelationalWriteNoProfileTableState(
                addressPlan,
                [],
                [CreateRow(addressCollectionItemId, 345L, 0, "Home")]
            ),
            new RelationalWriteNoProfileTableState(
                periodPlan,
                [],
                [CreateRow(periodCollectionItemId, 345L, addressCollectionItemId, 0, "2026-09-01")]
            ),
        ]);
        var writeSession = new RecordingRelationalWriteSession([
            new CommandResponse(ScalarResult: 910L),
            new CommandResponse(),
            new CommandResponse(ScalarResult: 911L),
            new CommandResponse(),
        ]);

        await _sut.PersistAsync(request, mergeResult, writeSession);
        writeSession.Commands.Should().HaveCount(4);
        writeSession.Commands[0].CommandText.Should().Contain("CollectionItemIdSequence");
        writeSession.Commands[1].CommandText.Should().Be(addressPlan.InsertSql);
        GetParameterValue(writeSession.Commands[1], "@CollectionItemId").Should().Be(910L);
        GetParameterValue(writeSession.Commands[1], "@School_DocumentId").Should().Be(345L);
        GetParameterValue(writeSession.Commands[1], "@Ordinal").Should().Be(0);
        GetParameterValue(writeSession.Commands[1], "@AddressType").Should().Be("Home");

        writeSession.Commands[2].CommandText.Should().Contain("CollectionItemIdSequence");
        writeSession.Commands[3].CommandText.Should().Be(periodPlan.InsertSql);
        GetParameterValue(writeSession.Commands[3], "@CollectionItemId").Should().Be(911L);
        GetParameterValue(writeSession.Commands[3], "@School_DocumentId").Should().Be(345L);
        GetParameterValue(writeSession.Commands[3], "@ParentCollectionItemId").Should().Be(910L);
        GetParameterValue(writeSession.Commands[3], "@Ordinal").Should().Be(0);
        GetParameterValue(writeSession.Commands[3], "@BeginDate").Should().Be("2026-09-01");
    }

    [Test]
    public async Task It_deletes_updates_and_inserts_root_extension_collection_rows_using_stable_row_identity()
    {
        var rootPlan = CreateRootPlan();
        var extensionCollectionPlan = CreateExtensionCollectionPlan();
        var writePlan = CreateWritePlan([rootPlan, extensionCollectionPlan]);
        var request = CreateRequest(writePlan, RelationalWriteOperationKind.Put);
        var mergeResult = new RelationalWriteNoProfileMergeResult([
            new RelationalWriteNoProfileTableState(
                rootPlan,
                [CreateRow(345L, 255901, "Lincoln High")],
                [CreateRow(345L, 255901, "Lincoln High")]
            ),
            new RelationalWriteNoProfileTableState(
                extensionCollectionPlan,
                [CreateRow(44L, 345L, 0, "Tutor"), CreateRow(45L, 345L, 1, "Mentor")],
                [
                    CreateRow(45L, 345L, 0, "Mentor Updated"),
                    CreateRow(NewCollectionItemId(), 345L, 1, "Coach"),
                ]
            ),
        ]);
        var writeSession = new RecordingRelationalWriteSession([
            new CommandResponse(),
            new CommandResponse(),
            new CommandResponse(ScalarResult: 91L),
            new CommandResponse(),
        ]);

        await _sut.PersistAsync(request, mergeResult, writeSession);
        writeSession.Commands.Should().HaveCount(4);

        writeSession
            .Commands[0]
            .CommandText.Should()
            .Be(extensionCollectionPlan.CollectionMergePlan!.DeleteByStableRowIdentitySql);
        GetParameterValue(writeSession.Commands[0], "@CollectionItemId").Should().Be(44L);

        writeSession
            .Commands[1]
            .CommandText.Should()
            .Be(extensionCollectionPlan.CollectionMergePlan!.UpdateByStableRowIdentitySql);
        GetParameterValue(writeSession.Commands[1], "@CollectionItemId").Should().Be(45L);
        GetParameterValue(writeSession.Commands[1], "@School_DocumentId").Should().Be(345L);
        GetParameterValue(writeSession.Commands[1], "@Ordinal").Should().Be(0);
        GetParameterValue(writeSession.Commands[1], "@InterventionCode").Should().Be("Mentor Updated");

        writeSession.Commands[2].CommandText.Should().Contain("CollectionItemIdSequence");
        writeSession.Commands[3].CommandText.Should().Be(extensionCollectionPlan.InsertSql);
        GetParameterValue(writeSession.Commands[3], "@CollectionItemId").Should().Be(91L);
        GetParameterValue(writeSession.Commands[3], "@School_DocumentId").Should().Be(345L);
        GetParameterValue(writeSession.Commands[3], "@Ordinal").Should().Be(1);
        GetParameterValue(writeSession.Commands[3], "@InterventionCode").Should().Be("Coach");
    }

    [Test]
    public async Task It_deletes_updates_and_inserts_collection_aligned_extension_child_rows_using_base_row_identity()
    {
        var rootPlan = CreateRootPlan();
        var addressPlan = CreateCollectionPlan();
        var collectionAlignedExtensionChildPlan = CreateCollectionAlignedExtensionChildCollectionPlan();
        var writePlan = CreateWritePlan([rootPlan, addressPlan, collectionAlignedExtensionChildPlan]);
        var request = CreateRequest(writePlan, RelationalWriteOperationKind.Put);
        var mergeResult = new RelationalWriteNoProfileMergeResult([
            new RelationalWriteNoProfileTableState(
                rootPlan,
                [CreateRow(345L, 255901, "Lincoln High")],
                [CreateRow(345L, 255901, "Lincoln High")]
            ),
            new RelationalWriteNoProfileTableState(
                addressPlan,
                [CreateRow(44L, 345L, 0, "Home")],
                [CreateRow(44L, 345L, 0, "Home")]
            ),
            new RelationalWriteNoProfileTableState(
                collectionAlignedExtensionChildPlan,
                [CreateRow(500L, 345L, 44L, 0, "Bus"), CreateRow(501L, 345L, 44L, 1, "Meal")],
                [
                    CreateRow(501L, 345L, 44L, 0, "Meal Updated"),
                    CreateRow(NewCollectionItemId(), 345L, 44L, 1, "Tutor"),
                ]
            ),
        ]);
        var writeSession = new RecordingRelationalWriteSession([
            new CommandResponse(),
            new CommandResponse(),
            new CommandResponse(ScalarResult: 91L),
            new CommandResponse(),
        ]);

        await _sut.PersistAsync(request, mergeResult, writeSession);
        writeSession.Commands.Should().HaveCount(4);

        writeSession
            .Commands[0]
            .CommandText.Should()
            .Be(collectionAlignedExtensionChildPlan.CollectionMergePlan!.DeleteByStableRowIdentitySql);
        GetParameterValue(writeSession.Commands[0], "@CollectionItemId").Should().Be(500L);

        writeSession
            .Commands[1]
            .CommandText.Should()
            .Be(collectionAlignedExtensionChildPlan.CollectionMergePlan!.UpdateByStableRowIdentitySql);
        GetParameterValue(writeSession.Commands[1], "@CollectionItemId").Should().Be(501L);
        GetParameterValue(writeSession.Commands[1], "@School_DocumentId").Should().Be(345L);
        GetParameterValue(writeSession.Commands[1], "@BaseCollectionItemId").Should().Be(44L);
        GetParameterValue(writeSession.Commands[1], "@Ordinal").Should().Be(0);
        GetParameterValue(writeSession.Commands[1], "@ServiceName").Should().Be("Meal Updated");

        writeSession.Commands[2].CommandText.Should().Contain("CollectionItemIdSequence");
        writeSession.Commands[3].CommandText.Should().Be(collectionAlignedExtensionChildPlan.InsertSql);
        GetParameterValue(writeSession.Commands[3], "@CollectionItemId").Should().Be(91L);
        GetParameterValue(writeSession.Commands[3], "@School_DocumentId").Should().Be(345L);
        GetParameterValue(writeSession.Commands[3], "@BaseCollectionItemId").Should().Be(44L);
        GetParameterValue(writeSession.Commands[3], "@Ordinal").Should().Be(1);
        GetParameterValue(writeSession.Commands[3], "@ServiceName").Should().Be("Tutor");
    }

    [Test]
    public async Task It_resolves_new_parent_collection_ids_for_collection_aligned_extension_child_inserts()
    {
        var rootPlan = CreateRootPlan();
        var addressPlan = CreateCollectionPlan();
        var collectionAlignedExtensionChildPlan = CreateCollectionAlignedExtensionChildCollectionPlan();
        var writePlan = CreateWritePlan([rootPlan, addressPlan, collectionAlignedExtensionChildPlan]);
        var request = CreateRequest(writePlan, RelationalWriteOperationKind.Put);
        var addressCollectionItemId = NewCollectionItemId();
        var serviceCollectionItemId = NewCollectionItemId();
        var mergeResult = new RelationalWriteNoProfileMergeResult([
            new RelationalWriteNoProfileTableState(
                rootPlan,
                [CreateRow(345L, 255901, "Lincoln High")],
                [CreateRow(345L, 255901, "Lincoln High")]
            ),
            new RelationalWriteNoProfileTableState(
                addressPlan,
                [],
                [CreateRow(addressCollectionItemId, 345L, 0, "Home")]
            ),
            new RelationalWriteNoProfileTableState(
                collectionAlignedExtensionChildPlan,
                [],
                [CreateRow(serviceCollectionItemId, 345L, addressCollectionItemId, 0, "Bus")]
            ),
        ]);
        var writeSession = new RecordingRelationalWriteSession([
            new CommandResponse(ScalarResult: 910L),
            new CommandResponse(),
            new CommandResponse(ScalarResult: 911L),
            new CommandResponse(),
        ]);

        await _sut.PersistAsync(request, mergeResult, writeSession);
        writeSession.Commands.Should().HaveCount(4);
        writeSession.Commands[0].CommandText.Should().Contain("CollectionItemIdSequence");
        writeSession.Commands[1].CommandText.Should().Be(addressPlan.InsertSql);
        GetParameterValue(writeSession.Commands[1], "@CollectionItemId").Should().Be(910L);
        GetParameterValue(writeSession.Commands[1], "@School_DocumentId").Should().Be(345L);
        GetParameterValue(writeSession.Commands[1], "@Ordinal").Should().Be(0);
        GetParameterValue(writeSession.Commands[1], "@AddressType").Should().Be("Home");

        writeSession.Commands[2].CommandText.Should().Contain("CollectionItemIdSequence");
        writeSession.Commands[3].CommandText.Should().Be(collectionAlignedExtensionChildPlan.InsertSql);
        GetParameterValue(writeSession.Commands[3], "@CollectionItemId").Should().Be(911L);
        GetParameterValue(writeSession.Commands[3], "@School_DocumentId").Should().Be(345L);
        GetParameterValue(writeSession.Commands[3], "@BaseCollectionItemId").Should().Be(910L);
        GetParameterValue(writeSession.Commands[3], "@Ordinal").Should().Be(0);
        GetParameterValue(writeSession.Commands[3], "@ServiceName").Should().Be("Bus");
    }

    [Test]
    public async Task It_batches_collection_updates_to_avoid_one_command_per_row()
    {
        var rootPlan = CreateRootPlan();
        var collectionPlan = CreateCollectionPlan() with
        {
            BulkInsertBatching = new BulkInsertBatchingInfo(
                MaxRowsPerBatch: 2,
                ParametersPerRow: 4,
                MaxParametersPerCommand: 8
            ),
        };
        var writePlan = CreateWritePlan([rootPlan, collectionPlan]);
        var request = CreateRequest(writePlan, RelationalWriteOperationKind.Put);
        var mergeResult = new RelationalWriteNoProfileMergeResult([
            new RelationalWriteNoProfileTableState(
                rootPlan,
                [CreateRow(345L, 255901, "Lincoln High")],
                [CreateRow(345L, 255901, "Lincoln High")]
            ),
            new RelationalWriteNoProfileTableState(
                collectionPlan,
                [
                    CreateRow(44L, 345L, 0, "Mailing"),
                    CreateRow(45L, 345L, 1, "Home"),
                    CreateRow(46L, 345L, 2, "Physical"),
                    CreateRow(47L, 345L, 3, "Temporary"),
                    CreateRow(48L, 345L, 4, "Shipping"),
                ],
                [
                    CreateRow(44L, 345L, 0, "Mailing Updated"),
                    CreateRow(45L, 345L, 1, "Home Updated"),
                    CreateRow(46L, 345L, 2, "Physical Updated"),
                    CreateRow(47L, 345L, 3, "Temporary Updated"),
                    CreateRow(48L, 345L, 4, "Shipping Updated"),
                ]
            ),
        ]);
        var writeSession = new RecordingRelationalWriteSession([
            new CommandResponse(),
            new CommandResponse(),
            new CommandResponse(),
        ]);
        var batchSqlEmitter = new WritePlanBatchSqlEmitter(SqlDialect.Pgsql);

        await _sut.PersistAsync(request, mergeResult, writeSession);
        writeSession.Commands.Should().HaveCount(3);
        writeSession
            .Commands[0]
            .CommandText.Should()
            .Be(batchSqlEmitter.EmitCollectionUpdateByStableRowIdentityBatch(collectionPlan, 2));
        writeSession.Commands[0].Parameters.Should().HaveCount(8);
        writeSession
            .Commands[1]
            .CommandText.Should()
            .Be(batchSqlEmitter.EmitCollectionUpdateByStableRowIdentityBatch(collectionPlan, 2));
        writeSession.Commands[1].Parameters.Should().HaveCount(8);
        writeSession
            .Commands[2]
            .CommandText.Should()
            .Be(collectionPlan.CollectionMergePlan!.UpdateByStableRowIdentitySql);
        writeSession.Commands[2].Parameters.Should().HaveCount(4);
        GetParameterValue(writeSession.Commands[0], "@CollectionItemId_0").Should().Be(44L);
        GetParameterValue(writeSession.Commands[0], "@CollectionItemId_1").Should().Be(45L);
        GetParameterValue(writeSession.Commands[1], "@CollectionItemId_0").Should().Be(46L);
        GetParameterValue(writeSession.Commands[1], "@CollectionItemId_1").Should().Be(47L);
        GetParameterValue(writeSession.Commands[2], "@CollectionItemId").Should().Be(48L);
    }

    [Test]
    public async Task It_batches_collection_deletes_to_avoid_one_command_per_row()
    {
        var rootPlan = CreateRootPlan();
        var collectionPlan = CreateCollectionPlan() with
        {
            BulkInsertBatching = new BulkInsertBatchingInfo(
                MaxRowsPerBatch: 2,
                ParametersPerRow: 4,
                MaxParametersPerCommand: 8
            ),
        };
        var writePlan = CreateWritePlan([rootPlan, collectionPlan]);
        var request = CreateRequest(writePlan, RelationalWriteOperationKind.Put);
        var mergeResult = new RelationalWriteNoProfileMergeResult([
            new RelationalWriteNoProfileTableState(
                rootPlan,
                [CreateRow(345L, 255901, "Lincoln High")],
                [CreateRow(345L, 255901, "Lincoln High")]
            ),
            new RelationalWriteNoProfileTableState(
                collectionPlan,
                [
                    CreateRow(44L, 345L, 0, "Mailing"),
                    CreateRow(45L, 345L, 1, "Home"),
                    CreateRow(46L, 345L, 2, "Physical"),
                    CreateRow(47L, 345L, 3, "Temporary"),
                    CreateRow(48L, 345L, 4, "Shipping"),
                ],
                []
            ),
        ]);
        var writeSession = new RecordingRelationalWriteSession([
            new CommandResponse(),
            new CommandResponse(),
            new CommandResponse(),
        ]);
        var batchSqlEmitter = new WritePlanBatchSqlEmitter(SqlDialect.Pgsql);

        await _sut.PersistAsync(request, mergeResult, writeSession);
        writeSession.Commands.Should().HaveCount(3);
        writeSession
            .Commands[0]
            .CommandText.Should()
            .Be(batchSqlEmitter.EmitCollectionDeleteByStableRowIdentityBatch(collectionPlan, 2));
        writeSession.Commands[0].Parameters.Should().HaveCount(8);
        GetParameterValue(writeSession.Commands[0], "@CollectionItemId_0").Should().Be(44L);
        GetParameterValue(writeSession.Commands[0], "@CollectionItemId_1").Should().Be(45L);

        writeSession
            .Commands[1]
            .CommandText.Should()
            .Be(batchSqlEmitter.EmitCollectionDeleteByStableRowIdentityBatch(collectionPlan, 2));
        writeSession.Commands[1].Parameters.Should().HaveCount(8);
        GetParameterValue(writeSession.Commands[1], "@CollectionItemId_0").Should().Be(46L);
        GetParameterValue(writeSession.Commands[1], "@CollectionItemId_1").Should().Be(47L);

        writeSession
            .Commands[2]
            .CommandText.Should()
            .Be(collectionPlan.CollectionMergePlan!.DeleteByStableRowIdentitySql);
        writeSession.Commands[2].Parameters.Should().HaveCount(4);
        GetParameterValue(writeSession.Commands[2], "@CollectionItemId").Should().Be(48L);
    }

    [Test]
    public async Task It_uses_sql_server_multi_batch_collection_delete_commands_when_omitted_rows_cross_the_limit()
    {
        var rootPlan = CreateRootPlan();
        var collectionPlan = CreateMssqlCollectionPlan() with
        {
            BulkInsertBatching = new BulkInsertBatchingInfo(
                MaxRowsPerBatch: 2,
                ParametersPerRow: 4,
                MaxParametersPerCommand: 8
            ),
        };
        var writePlan = CreateWritePlan([rootPlan, collectionPlan]);
        var request = CreateRequest(writePlan, RelationalWriteOperationKind.Put, SqlDialect.Mssql);
        var mergeResult = new RelationalWriteNoProfileMergeResult([
            new RelationalWriteNoProfileTableState(
                rootPlan,
                [CreateRow(345L, 255901, "Lincoln High")],
                [CreateRow(345L, 255901, "Lincoln High")]
            ),
            new RelationalWriteNoProfileTableState(
                collectionPlan,
                [
                    CreateRow(44L, 345L, 0, "Mailing"),
                    CreateRow(45L, 345L, 1, "Home"),
                    CreateRow(46L, 345L, 2, "Physical"),
                    CreateRow(47L, 345L, 3, "Temporary"),
                    CreateRow(48L, 345L, 4, "Shipping"),
                ],
                []
            ),
        ]);
        var writeSession = new RecordingRelationalWriteSession([
            new CommandResponse(),
            new CommandResponse(),
            new CommandResponse(),
        ]);
        var batchSqlEmitter = new WritePlanBatchSqlEmitter(SqlDialect.Mssql);

        await _sut.PersistAsync(request, mergeResult, writeSession);
        writeSession.Commands.Should().HaveCount(3);
        writeSession
            .Commands[0]
            .CommandText.Should()
            .Be(batchSqlEmitter.EmitCollectionDeleteByStableRowIdentityBatch(collectionPlan, 2));
        writeSession.Commands[0].Parameters.Should().HaveCount(8);
        GetParameterValue(writeSession.Commands[0], "@CollectionItemId_0").Should().Be(44L);
        GetParameterValue(writeSession.Commands[0], "@CollectionItemId_1").Should().Be(45L);

        writeSession
            .Commands[1]
            .CommandText.Should()
            .Be(batchSqlEmitter.EmitCollectionDeleteByStableRowIdentityBatch(collectionPlan, 2));
        writeSession.Commands[1].Parameters.Should().HaveCount(8);
        GetParameterValue(writeSession.Commands[1], "@CollectionItemId_0").Should().Be(46L);
        GetParameterValue(writeSession.Commands[1], "@CollectionItemId_1").Should().Be(47L);

        writeSession
            .Commands[2]
            .CommandText.Should()
            .Be(collectionPlan.CollectionMergePlan!.DeleteByStableRowIdentitySql);
        writeSession.Commands[2].Parameters.Should().HaveCount(4);
        GetParameterValue(writeSession.Commands[2], "@CollectionItemId").Should().Be(48L);
    }

    [Test]
    public async Task It_uses_temporary_negative_ordinals_to_avoid_transient_ordinal_uniqueness_collisions_during_collection_reorders()
    {
        var rootPlan = CreateRootPlan();
        var collectionPlan = CreateCollectionPlan() with
        {
            BulkInsertBatching = new BulkInsertBatchingInfo(
                MaxRowsPerBatch: 2,
                ParametersPerRow: 4,
                MaxParametersPerCommand: 8
            ),
        };
        var writePlan = CreateWritePlan([rootPlan, collectionPlan]);
        var request = CreateRequest(writePlan, RelationalWriteOperationKind.Put);
        var mergeResult = new RelationalWriteNoProfileMergeResult([
            new RelationalWriteNoProfileTableState(
                rootPlan,
                [CreateRow(345L, 255901, "Lincoln High")],
                [CreateRow(345L, 255901, "Lincoln High")]
            ),
            new RelationalWriteNoProfileTableState(
                collectionPlan,
                [CreateRow(44L, 345L, 0, "Mailing"), CreateRow(45L, 345L, 1, "Home")],
                [CreateRow(45L, 345L, 0, "Home"), CreateRow(44L, 345L, 1, "Mailing")]
            ),
        ]);
        var writeSession = new RecordingRelationalWriteSession([
            new CommandResponse(),
            new CommandResponse(),
        ]);
        var batchSqlEmitter = new WritePlanBatchSqlEmitter(SqlDialect.Pgsql);

        await _sut.PersistAsync(request, mergeResult, writeSession);
        writeSession.Commands.Should().HaveCount(2);

        // A sequential swap from 0<->1 would collide on the sibling-order uniqueness constraint unless the first pass
        // moves both matched rows out of the way.
        writeSession
            .Commands[0]
            .CommandText.Should()
            .Be(batchSqlEmitter.EmitCollectionUpdateByStableRowIdentityBatch(collectionPlan, 2));
        GetParameterValue(writeSession.Commands[0], "@CollectionItemId_0").Should().Be(45L);
        GetParameterValue(writeSession.Commands[0], "@CollectionItemId_1").Should().Be(44L);
        GetParameterValue(writeSession.Commands[0], "@Ordinal_0").Should().Be(-1);
        GetParameterValue(writeSession.Commands[0], "@Ordinal_1").Should().Be(-2);

        writeSession
            .Commands[1]
            .CommandText.Should()
            .Be(batchSqlEmitter.EmitCollectionUpdateByStableRowIdentityBatch(collectionPlan, 2));
        GetParameterValue(writeSession.Commands[1], "@CollectionItemId_0").Should().Be(45L);
        GetParameterValue(writeSession.Commands[1], "@CollectionItemId_1").Should().Be(44L);
        GetParameterValue(writeSession.Commands[1], "@Ordinal_0").Should().Be(0);
        GetParameterValue(writeSession.Commands[1], "@Ordinal_1").Should().Be(1);
    }

    [Test]
    public async Task It_batches_collection_id_reservations_and_insert_commands_for_large_collection_inserts()
    {
        var rootPlan = CreateRootPlan();
        var collectionPlan = CreateCollectionPlan() with
        {
            BulkInsertBatching = new BulkInsertBatchingInfo(
                MaxRowsPerBatch: 2,
                ParametersPerRow: 4,
                MaxParametersPerCommand: 8
            ),
        };
        var writePlan = CreateWritePlan([rootPlan, collectionPlan]);
        var request = CreateRequest(writePlan, RelationalWriteOperationKind.Put);
        var mergeResult = new RelationalWriteNoProfileMergeResult([
            new RelationalWriteNoProfileTableState(
                rootPlan,
                [CreateRow(345L, 255901, "Lincoln High")],
                [CreateRow(345L, 255901, "Lincoln High")]
            ),
            new RelationalWriteNoProfileTableState(
                collectionPlan,
                [],
                [
                    CreateRow(NewCollectionItemId(), 345L, 0, "Mailing"),
                    CreateRow(NewCollectionItemId(), 345L, 1, "Home"),
                    CreateRow(NewCollectionItemId(), 345L, 2, "Physical"),
                    CreateRow(NewCollectionItemId(), 345L, 3, "Temporary"),
                    CreateRow(NewCollectionItemId(), 345L, 4, "Shipping"),
                ]
            ),
        ]);
        var writeSession = new RecordingRelationalWriteSession([
            new CommandResponse(
                ReservationRows:
                [
                    new ReservedCollectionItemIdRow(1, 910L),
                    new ReservedCollectionItemIdRow(2, 911L),
                ]
            ),
            new CommandResponse(),
            new CommandResponse(
                ReservationRows:
                [
                    new ReservedCollectionItemIdRow(1, 912L),
                    new ReservedCollectionItemIdRow(2, 913L),
                ]
            ),
            new CommandResponse(),
            new CommandResponse(ScalarResult: 914L),
            new CommandResponse(),
        ]);
        var batchSqlEmitter = new WritePlanBatchSqlEmitter(SqlDialect.Pgsql);

        await _sut.PersistAsync(request, mergeResult, writeSession);
        writeSession.Commands.Should().HaveCount(6);

        writeSession.Commands[0].CommandText.Should().Contain("generate_series");
        GetParameterValue(writeSession.Commands[0], "@count").Should().Be(2);

        writeSession.Commands[1].CommandText.Should().Be(batchSqlEmitter.EmitInsertBatch(collectionPlan, 2));
        writeSession.Commands[1].Parameters.Should().HaveCount(8);
        AssertBatchedParameterNames(writeSession.Commands[1], collectionPlan, 2);
        GetParameterValue(writeSession.Commands[1], "@CollectionItemId_0").Should().Be(910L);
        GetParameterValue(writeSession.Commands[1], "@CollectionItemId_1").Should().Be(911L);

        writeSession.Commands[2].CommandText.Should().Contain("generate_series");
        GetParameterValue(writeSession.Commands[2], "@count").Should().Be(2);

        writeSession.Commands[3].CommandText.Should().Be(batchSqlEmitter.EmitInsertBatch(collectionPlan, 2));
        writeSession.Commands[3].Parameters.Should().HaveCount(8);
        AssertBatchedParameterNames(writeSession.Commands[3], collectionPlan, 2);
        GetParameterValue(writeSession.Commands[3], "@CollectionItemId_0").Should().Be(912L);
        GetParameterValue(writeSession.Commands[3], "@CollectionItemId_1").Should().Be(913L);

        writeSession.Commands[4].CommandText.Should().Contain("CollectionItemIdSequence");
        writeSession.Commands[5].CommandText.Should().Be(collectionPlan.InsertSql);
        GetParameterValue(writeSession.Commands[5], "@CollectionItemId").Should().Be(914L);
        GetParameterValue(writeSession.Commands[5], "@AddressType").Should().Be("Shipping");
    }

    [Test]
    public async Task It_uses_sql_server_batch_reservation_and_insert_sql_for_multi_row_collection_inserts()
    {
        var rootPlan = CreateRootPlan();
        var collectionPlan = CreateMssqlCollectionPlan() with
        {
            BulkInsertBatching = new BulkInsertBatchingInfo(
                MaxRowsPerBatch: 2,
                ParametersPerRow: 4,
                MaxParametersPerCommand: 8
            ),
        };
        var writePlan = CreateWritePlan([rootPlan, collectionPlan]);
        var request = CreateRequest(writePlan, RelationalWriteOperationKind.Put, SqlDialect.Mssql);
        var mergeResult = new RelationalWriteNoProfileMergeResult([
            new RelationalWriteNoProfileTableState(
                rootPlan,
                [CreateRow(345L, 255901, "Lincoln High")],
                [CreateRow(345L, 255901, "Lincoln High")]
            ),
            new RelationalWriteNoProfileTableState(
                collectionPlan,
                [],
                [
                    CreateRow(NewCollectionItemId(), 345L, 0, "Mailing"),
                    CreateRow(NewCollectionItemId(), 345L, 1, "Home"),
                ]
            ),
        ]);
        var writeSession = new RecordingRelationalWriteSession([
            new CommandResponse(
                ReservationRows:
                [
                    new ReservedCollectionItemIdRow(1, 910L),
                    new ReservedCollectionItemIdRow(2, 911L),
                ]
            ),
            new CommandResponse(),
        ]);
        var batchSqlEmitter = new WritePlanBatchSqlEmitter(SqlDialect.Mssql);

        await _sut.PersistAsync(request, mergeResult, writeSession);
        writeSession.Commands.Should().HaveCount(2);
        writeSession
            .Commands[0]
            .CommandText.Should()
            .Contain("NEXT VALUE FOR [dms].[CollectionItemIdSequence] OVER");
        GetParameterValue(writeSession.Commands[0], "@count").Should().Be(2);
        writeSession.Commands[1].CommandText.Should().Be(batchSqlEmitter.EmitInsertBatch(collectionPlan, 2));
        AssertBatchedParameterNames(writeSession.Commands[1], collectionPlan, 2);
        GetParameterValue(writeSession.Commands[1], "@CollectionItemId_0").Should().Be(910L);
        GetParameterValue(writeSession.Commands[1], "@CollectionItemId_1").Should().Be(911L);
    }

    [Test]
    public async Task It_uses_sql_server_multi_batch_collection_id_reservations_when_insert_batches_cross_the_limit()
    {
        var rootPlan = CreateRootPlan();
        var collectionPlan = CreateMssqlCollectionPlan() with
        {
            BulkInsertBatching = new BulkInsertBatchingInfo(
                MaxRowsPerBatch: 2,
                ParametersPerRow: 4,
                MaxParametersPerCommand: 8
            ),
        };
        var writePlan = CreateWritePlan([rootPlan, collectionPlan]);
        var request = CreateRequest(writePlan, RelationalWriteOperationKind.Put, SqlDialect.Mssql);
        var mergeResult = new RelationalWriteNoProfileMergeResult([
            new RelationalWriteNoProfileTableState(
                rootPlan,
                [CreateRow(345L, 255901, "Lincoln High")],
                [CreateRow(345L, 255901, "Lincoln High")]
            ),
            new RelationalWriteNoProfileTableState(
                collectionPlan,
                [],
                [
                    CreateRow(NewCollectionItemId(), 345L, 0, "Mailing"),
                    CreateRow(NewCollectionItemId(), 345L, 1, "Home"),
                    CreateRow(NewCollectionItemId(), 345L, 2, "Physical"),
                    CreateRow(NewCollectionItemId(), 345L, 3, "Temporary"),
                    CreateRow(NewCollectionItemId(), 345L, 4, "Shipping"),
                ]
            ),
        ]);
        var writeSession = new RecordingRelationalWriteSession([
            new CommandResponse(
                ReservationRows:
                [
                    new ReservedCollectionItemIdRow(1, 910L),
                    new ReservedCollectionItemIdRow(2, 911L),
                ]
            ),
            new CommandResponse(),
            new CommandResponse(
                ReservationRows:
                [
                    new ReservedCollectionItemIdRow(1, 912L),
                    new ReservedCollectionItemIdRow(2, 913L),
                ]
            ),
            new CommandResponse(),
            new CommandResponse(ScalarResult: 914L),
            new CommandResponse(),
        ]);
        var batchSqlEmitter = new WritePlanBatchSqlEmitter(SqlDialect.Mssql);

        await _sut.PersistAsync(request, mergeResult, writeSession);
        writeSession.Commands.Should().HaveCount(6);

        writeSession
            .Commands[0]
            .CommandText.Should()
            .Contain("NEXT VALUE FOR [dms].[CollectionItemIdSequence] OVER");
        GetParameterValue(writeSession.Commands[0], "@count").Should().Be(2);

        writeSession.Commands[1].CommandText.Should().Be(batchSqlEmitter.EmitInsertBatch(collectionPlan, 2));
        writeSession.Commands[1].Parameters.Should().HaveCount(8);
        AssertBatchedParameterNames(writeSession.Commands[1], collectionPlan, 2);
        GetParameterValue(writeSession.Commands[1], "@CollectionItemId_0").Should().Be(910L);
        GetParameterValue(writeSession.Commands[1], "@CollectionItemId_1").Should().Be(911L);

        writeSession
            .Commands[2]
            .CommandText.Should()
            .Contain("NEXT VALUE FOR [dms].[CollectionItemIdSequence] OVER");
        GetParameterValue(writeSession.Commands[2], "@count").Should().Be(2);

        writeSession.Commands[3].CommandText.Should().Be(batchSqlEmitter.EmitInsertBatch(collectionPlan, 2));
        writeSession.Commands[3].Parameters.Should().HaveCount(8);
        AssertBatchedParameterNames(writeSession.Commands[3], collectionPlan, 2);
        GetParameterValue(writeSession.Commands[3], "@CollectionItemId_0").Should().Be(912L);
        GetParameterValue(writeSession.Commands[3], "@CollectionItemId_1").Should().Be(913L);

        writeSession
            .Commands[4]
            .CommandText.Should()
            .Contain("SELECT NEXT VALUE FOR [dms].[CollectionItemIdSequence];");
        writeSession.Commands[5].CommandText.Should().Be(collectionPlan.InsertSql);
        GetParameterValue(writeSession.Commands[5], "@CollectionItemId").Should().Be(914L);
        GetParameterValue(writeSession.Commands[5], "@AddressType").Should().Be("Shipping");
    }

    private static object? GetParameterValue(RelationalCommand command, string parameterName)
    {
        return command.Parameters.Single(parameter => parameter.Name == parameterName).Value;
    }

    private static void AssertBatchedParameterNames(
        RelationalCommand command,
        TableWritePlan tableWritePlan,
        int rowCount
    )
    {
        var expectedParameterNames = Enumerable
            .Range(0, rowCount)
            .SelectMany(rowIndex =>
                tableWritePlan.ColumnBindings.Select(binding =>
                    $"@{binding.ParameterName.TrimStart('@')}_{rowIndex}"
                )
            )
            .ToArray();

        command.Parameters.Select(static parameter => parameter.Name).Should().Equal(expectedParameterNames);
        command.Parameters.Select(static parameter => parameter.Name).Should().OnlyHaveUniqueItems();
    }

    private static RelationalWriteExecutorRequest CreateRequest(
        ResourceWritePlan writePlan,
        RelationalWriteOperationKind operationKind,
        SqlDialect dialect = SqlDialect.Pgsql
    )
    {
        var mappingSet = CreateMappingSet(writePlan.Model, dialect);

        return new RelationalWriteExecutorRequest(
            mappingSet,
            operationKind,
            operationKind == RelationalWriteOperationKind.Put
                ? new RelationalWriteTargetRequest.Put(
                    new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"))
                )
                : new RelationalWriteTargetRequest.Post(
                    new ReferentialId(Guid.NewGuid()),
                    new DocumentUuid(Guid.Parse("cccccccc-1111-2222-3333-dddddddddddd"))
                ),
            writePlan,
            operationKind == RelationalWriteOperationKind.Put
                ? CreateReadPlan(writePlan.Model, dialect)
                : null,
            JsonNode.Parse("""{"schoolId":255901,"name":"Lincoln High"}""")!,
            false,
            new TraceId("no-profile-persister-test"),
            new ReferenceResolverRequest(mappingSet, writePlan.Model.Resource, [], []),
            targetContext: operationKind == RelationalWriteOperationKind.Put
                ? new RelationalWriteTargetContext.ExistingDocument(
                    345L,
                    new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"))
                )
                : new RelationalWriteTargetContext.CreateNew(
                    new DocumentUuid(Guid.Parse("cccccccc-1111-2222-3333-dddddddddddd"))
                )
        );
    }

    private static MappingSet CreateMappingSet(RelationalResourceModel resourceModel, SqlDialect dialect)
    {
        var resource = resourceModel.Resource;
        var resourceKey = new ResourceKeyEntry(1, resource, "1.0.0", false);

        return new MappingSet(
            Key: new MappingSetKey("schema-hash", dialect, "v1"),
            Model: new DerivedRelationalModelSet(
                EffectiveSchema: new EffectiveSchemaInfo(
                    ApiSchemaFormatVersion: "1.0",
                    RelationalMappingVersion: "v1",
                    EffectiveSchemaHash: "schema-hash",
                    ResourceKeyCount: 1,
                    ResourceKeySeedHash: [1, 2, 3],
                    SchemaComponentsInEndpointOrder:
                    [
                        new SchemaComponentInfo("ed-fi", "Ed-Fi", "1.0.0", false, "component-hash"),
                    ],
                    ResourceKeysInIdOrder: [resourceKey]
                ),
                Dialect: dialect,
                ProjectSchemasInEndpointOrder:
                [
                    new ProjectSchemaInfo("ed-fi", "Ed-Fi", "1.0.0", false, new DbSchemaName("edfi")),
                ],
                ConcreteResourcesInNameOrder:
                [
                    new ConcreteResourceModel(
                        resourceKey,
                        ResourceStorageKind.RelationalTables,
                        resourceModel
                    ),
                ],
                AbstractIdentityTablesInNameOrder: [],
                AbstractUnionViewsInNameOrder: [],
                IndexesInCreateOrder: [],
                TriggersInCreateOrder: []
            ),
            WritePlansByResource: new Dictionary<QualifiedResourceName, ResourceWritePlan>
            {
                [resource] = CreateWritePlan(
                    resourceModel.TablesInDependencyOrder.Select(CreatePlanForModel).ToArray()
                ),
            },
            ReadPlansByResource: new Dictionary<QualifiedResourceName, ResourceReadPlan>(),
            ResourceKeyIdByResource: new Dictionary<QualifiedResourceName, short>
            {
                [resource] = resourceKey.ResourceKeyId,
            },
            ResourceKeyById: new Dictionary<short, ResourceKeyEntry>
            {
                [resourceKey.ResourceKeyId] = resourceKey,
            },
            SecurableElementColumnPathsByResource: new Dictionary<
                QualifiedResourceName,
                IReadOnlyList<ResolvedSecurableElementPath>
            >()
        );
    }

    private static ResourceReadPlan CreateReadPlan(RelationalResourceModel resourceModel, SqlDialect dialect)
    {
        return new ResourceReadPlan(
            resourceModel,
            KeysetTableConventions.GetKeysetTableContract(dialect),
            resourceModel
                .TablesInDependencyOrder.Select(tableModel => new TableReadPlan(
                    tableModel,
                    $"select * from {tableModel.Table.Schema.Value}.\"{tableModel.Table.Name}\""
                ))
                .ToArray(),
            [],
            []
        );
    }

    private static ResourceWritePlan CreateWritePlan(IReadOnlyList<TableWritePlan> tablePlans)
    {
        var rootTable = tablePlans[0].TableModel;
        var resourceModel = new RelationalResourceModel(
            Resource: new QualifiedResourceName("Ed-Fi", "School"),
            PhysicalSchema: new DbSchemaName("edfi"),
            StorageKind: ResourceStorageKind.RelationalTables,
            Root: rootTable,
            TablesInDependencyOrder: tablePlans.Select(static plan => plan.TableModel).ToArray(),
            DocumentReferenceBindings: [],
            DescriptorEdgeSources: []
        );

        return new ResourceWritePlan(resourceModel, tablePlans);
    }

    private static TableWritePlan CreatePlanForModel(DbTableModel tableModel)
    {
        return tableModel.Table.Name switch
        {
            "School" => CreateRootPlan(
                includeShortName: tableModel.Columns.Any(column => column.ColumnName.Value == "ShortName")
            ),
            "SchoolExtension" => CreateRootExtensionPlan(),
            "SchoolAddress" => CreateCollectionPlan(),
            "SchoolAddressPeriod" => CreatePeriodPlan(),
            "SchoolExtensionIntervention" => CreateExtensionCollectionPlan(),
            "SchoolExtensionAddress" => CreateCollectionExtensionScopePlan(),
            "SchoolExtensionAddressService" => CreateCollectionAlignedExtensionChildCollectionPlan(),
            _ => throw new InvalidOperationException($"Unsupported table '{tableModel.Table.Name}'."),
        };
    }

    private static TableWritePlan CreateRootPlan(bool includeShortName = false)
    {
        List<DbColumnModel> columns =
        [
            CreateColumn("DocumentId", ColumnKind.ParentKeyPart),
            CreateColumn("SchoolId", ColumnKind.Scalar),
            CreateColumn("Name", ColumnKind.Scalar),
        ];

        if (includeShortName)
        {
            columns.Add(CreateColumn("ShortName", ColumnKind.Scalar, isNullable: true));
        }

        var tableModel = new DbTableModel(
            new DbTableName(new DbSchemaName("edfi"), "School"),
            new JsonPathExpression("$", []),
            new TableKey(
                "PK_School",
                [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            columns,
            []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                DbTableKind.Root,
                [new DbColumnName("DocumentId")],
                [new DbColumnName("DocumentId")],
                [],
                []
            ),
        };

        var bindings = new List<WriteColumnBinding>
        {
            new(tableModel.Columns[0], new WriteValueSource.DocumentId(), "DocumentId"),
            new(
                tableModel.Columns[1],
                new WriteValueSource.Scalar(
                    new JsonPathExpression("$.schoolId", []),
                    new RelationalScalarType(ScalarKind.Int32)
                ),
                "SchoolId"
            ),
            new(
                tableModel.Columns[2],
                new WriteValueSource.Scalar(
                    new JsonPathExpression("$.name", []),
                    new RelationalScalarType(ScalarKind.String, MaxLength: 75)
                ),
                "Name"
            ),
        };

        if (includeShortName)
        {
            bindings.Add(
                new WriteColumnBinding(
                    tableModel.Columns[3],
                    new WriteValueSource.Scalar(
                        new JsonPathExpression("$.shortName", []),
                        new RelationalScalarType(ScalarKind.String, MaxLength: 30)
                    ),
                    "ShortName"
                )
            );
        }

        return new TableWritePlan(
            tableModel,
            InsertSql: includeShortName
                ? """
                insert into edfi."School" values (@DocumentId, @SchoolId, @Name, @ShortName)
                """
                : """
                insert into edfi."School" values (@DocumentId, @SchoolId, @Name)
                """,
            UpdateSql: includeShortName
                ? """
                update edfi."School" set "SchoolId" = @SchoolId, "Name" = @Name, "ShortName" = @ShortName where "DocumentId" = @DocumentId
                """
                : """
                update edfi."School" set "SchoolId" = @SchoolId, "Name" = @Name where "DocumentId" = @DocumentId
                """,
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(100, bindings.Count, 1000),
            ColumnBindings: bindings,
            KeyUnificationPlans: []
        );
    }

    private static TableWritePlan CreateRootExtensionPlan()
    {
        var tableModel = new DbTableModel(
            new DbTableName(new DbSchemaName("sample"), "SchoolExtension"),
            new JsonPathExpression("$._ext.sample", []),
            new TableKey(
                "PK_SchoolExtension",
                [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            [
                CreateColumn("DocumentId", ColumnKind.ParentKeyPart),
                CreateColumn("ExtensionCode", ColumnKind.Scalar),
            ],
            []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                DbTableKind.RootExtension,
                [new DbColumnName("DocumentId")],
                [new DbColumnName("DocumentId")],
                [new DbColumnName("DocumentId")],
                []
            ),
        };

        return new TableWritePlan(
            tableModel,
            InsertSql: """
            insert into sample."SchoolExtension" values (@DocumentId, @ExtensionCode)
            """,
            UpdateSql: """
            update sample."SchoolExtension" set "ExtensionCode" = @ExtensionCode where "DocumentId" = @DocumentId
            """,
            DeleteByParentSql: """
            delete from sample."SchoolExtension" where "DocumentId" = @DocumentId
            """,
            BulkInsertBatching: new BulkInsertBatchingInfo(100, 2, 1000),
            ColumnBindings:
            [
                new WriteColumnBinding(
                    tableModel.Columns[0],
                    new WriteValueSource.ParentKeyPart(0),
                    "DocumentId"
                ),
                new WriteColumnBinding(
                    tableModel.Columns[1],
                    new WriteValueSource.Scalar(
                        new JsonPathExpression("$.extensionCode", []),
                        new RelationalScalarType(ScalarKind.String, MaxLength: 30)
                    ),
                    "ExtensionCode"
                ),
            ],
            KeyUnificationPlans: []
        );
    }

    private static TableWritePlan CreateCollectionPlan()
    {
        var tableModel = new DbTableModel(
            new DbTableName(new DbSchemaName("edfi"), "SchoolAddress"),
            new JsonPathExpression("$.addresses[*]", []),
            new TableKey(
                "PK_SchoolAddress",
                [new DbKeyColumn(new DbColumnName("CollectionItemId"), ColumnKind.CollectionKey)]
            ),
            [
                CreateColumn("CollectionItemId", ColumnKind.CollectionKey),
                CreateColumn("School_DocumentId", ColumnKind.ParentKeyPart),
                CreateColumn("Ordinal", ColumnKind.Ordinal),
                CreateColumn("AddressType", ColumnKind.Scalar),
            ],
            []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                DbTableKind.Collection,
                [new DbColumnName("CollectionItemId")],
                [new DbColumnName("School_DocumentId")],
                [new DbColumnName("School_DocumentId")],
                [
                    new CollectionSemanticIdentityBinding(
                        new JsonPathExpression("$.addressType", []),
                        new DbColumnName("AddressType")
                    ),
                ]
            ),
        };

        return new TableWritePlan(
            tableModel,
            InsertSql: """
            insert into edfi."SchoolAddress" values (@CollectionItemId, @School_DocumentId, @Ordinal, @AddressType)
            """,
            UpdateSql: null,
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(100, 4, 1000),
            ColumnBindings:
            [
                new WriteColumnBinding(
                    tableModel.Columns[0],
                    new WriteValueSource.Precomputed(),
                    "CollectionItemId"
                ),
                new WriteColumnBinding(
                    tableModel.Columns[1],
                    new WriteValueSource.ParentKeyPart(0),
                    "School_DocumentId"
                ),
                new WriteColumnBinding(tableModel.Columns[2], new WriteValueSource.Ordinal(), "Ordinal"),
                new WriteColumnBinding(
                    tableModel.Columns[3],
                    new WriteValueSource.Scalar(
                        new JsonPathExpression("$.addressType", []),
                        new RelationalScalarType(ScalarKind.String, MaxLength: 30)
                    ),
                    "AddressType"
                ),
            ],
            KeyUnificationPlans: [],
            CollectionMergePlan: new CollectionMergePlan(
                [new CollectionMergeSemanticIdentityBinding(new JsonPathExpression("$.addressType", []), 3)],
                StableRowIdentityBindingIndex: 0,
                UpdateByStableRowIdentitySql: """
                update edfi."SchoolAddress" set "Ordinal" = @Ordinal, "AddressType" = @AddressType where "CollectionItemId" = @CollectionItemId
                """,
                DeleteByStableRowIdentitySql: """
                delete from edfi."SchoolAddress" where "CollectionItemId" = @CollectionItemId
                """,
                OrdinalBindingIndex: 2,
                CompareBindingIndexesInOrder: [0, 1, 2, 3]
            ),
            CollectionKeyPreallocationPlan: new CollectionKeyPreallocationPlan(
                new DbColumnName("CollectionItemId"),
                0
            )
        );
    }

    private static TableWritePlan CreateMssqlCollectionPlan()
    {
        var collectionPlan = CreateCollectionPlan();

        return collectionPlan with
        {
            InsertSql = """
                insert into [edfi].[SchoolAddress] values (@CollectionItemId, @School_DocumentId, @Ordinal, @AddressType)
                """,
            CollectionMergePlan = collectionPlan.CollectionMergePlan! with
            {
                UpdateByStableRowIdentitySql = """
                    update [edfi].[SchoolAddress] set [Ordinal] = @Ordinal, [AddressType] = @AddressType where [CollectionItemId] = @CollectionItemId
                    """,
                DeleteByStableRowIdentitySql = """
                    delete from [edfi].[SchoolAddress] where [CollectionItemId] = @CollectionItemId
                    """,
            },
        };
    }

    private static TableWritePlan CreatePeriodPlan()
    {
        var tableModel = new DbTableModel(
            new DbTableName(new DbSchemaName("edfi"), "SchoolAddressPeriod"),
            new JsonPathExpression("$.addresses[*].periods[*]", []),
            new TableKey(
                "PK_SchoolAddressPeriod",
                [new DbKeyColumn(new DbColumnName("CollectionItemId"), ColumnKind.CollectionKey)]
            ),
            [
                CreateColumn("CollectionItemId", ColumnKind.CollectionKey),
                CreateColumn("School_DocumentId", ColumnKind.ParentKeyPart),
                CreateColumn("ParentCollectionItemId", ColumnKind.ParentKeyPart),
                CreateColumn("Ordinal", ColumnKind.Ordinal),
                CreateColumn("BeginDate", ColumnKind.Scalar),
            ],
            []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                DbTableKind.Collection,
                [new DbColumnName("CollectionItemId")],
                [new DbColumnName("School_DocumentId")],
                [new DbColumnName("ParentCollectionItemId")],
                [
                    new CollectionSemanticIdentityBinding(
                        new JsonPathExpression("$.beginDate", []),
                        new DbColumnName("BeginDate")
                    ),
                ]
            ),
        };

        return new TableWritePlan(
            tableModel,
            InsertSql: """
            insert into edfi."SchoolAddressPeriod" values (@CollectionItemId, @School_DocumentId, @ParentCollectionItemId, @Ordinal, @BeginDate)
            """,
            UpdateSql: null,
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(100, 5, 1000),
            ColumnBindings:
            [
                new WriteColumnBinding(
                    tableModel.Columns[0],
                    new WriteValueSource.Precomputed(),
                    "CollectionItemId"
                ),
                new WriteColumnBinding(
                    tableModel.Columns[1],
                    new WriteValueSource.ParentKeyPart(0),
                    "School_DocumentId"
                ),
                new WriteColumnBinding(
                    tableModel.Columns[2],
                    new WriteValueSource.ParentKeyPart(1),
                    "ParentCollectionItemId"
                ),
                new WriteColumnBinding(tableModel.Columns[3], new WriteValueSource.Ordinal(), "Ordinal"),
                new WriteColumnBinding(
                    tableModel.Columns[4],
                    new WriteValueSource.Scalar(
                        new JsonPathExpression("$.beginDate", []),
                        new RelationalScalarType(ScalarKind.String, MaxLength: 30)
                    ),
                    "BeginDate"
                ),
            ],
            KeyUnificationPlans: [],
            CollectionMergePlan: new CollectionMergePlan(
                [new CollectionMergeSemanticIdentityBinding(new JsonPathExpression("$.beginDate", []), 4)],
                StableRowIdentityBindingIndex: 0,
                UpdateByStableRowIdentitySql: """
                update edfi."SchoolAddressPeriod" set "Ordinal" = @Ordinal, "BeginDate" = @BeginDate where "CollectionItemId" = @CollectionItemId
                """,
                DeleteByStableRowIdentitySql: """
                delete from edfi."SchoolAddressPeriod" where "CollectionItemId" = @CollectionItemId
                """,
                OrdinalBindingIndex: 3,
                CompareBindingIndexesInOrder: [3, 4]
            ),
            CollectionKeyPreallocationPlan: new CollectionKeyPreallocationPlan(
                new DbColumnName("CollectionItemId"),
                0
            )
        );
    }

    private static TableWritePlan CreateExtensionCollectionPlan()
    {
        var tableModel = new DbTableModel(
            new DbTableName(new DbSchemaName("sample"), "SchoolExtensionIntervention"),
            new JsonPathExpression("$._ext.sample.interventions[*]", []),
            new TableKey(
                "PK_SchoolExtensionIntervention",
                [new DbKeyColumn(new DbColumnName("CollectionItemId"), ColumnKind.CollectionKey)]
            ),
            [
                CreateColumn("CollectionItemId", ColumnKind.CollectionKey),
                CreateColumn("School_DocumentId", ColumnKind.ParentKeyPart),
                CreateColumn("Ordinal", ColumnKind.Ordinal),
                CreateColumn("InterventionCode", ColumnKind.Scalar),
            ],
            []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                DbTableKind.ExtensionCollection,
                [new DbColumnName("CollectionItemId")],
                [new DbColumnName("School_DocumentId")],
                [new DbColumnName("School_DocumentId")],
                [
                    new CollectionSemanticIdentityBinding(
                        new JsonPathExpression("$.interventionCode", []),
                        new DbColumnName("InterventionCode")
                    ),
                ]
            ),
        };

        return new TableWritePlan(
            tableModel,
            InsertSql: """
            insert into sample."SchoolExtensionIntervention" values (@CollectionItemId, @School_DocumentId, @Ordinal, @InterventionCode)
            """,
            UpdateSql: null,
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(100, 4, 1000),
            ColumnBindings:
            [
                new WriteColumnBinding(
                    tableModel.Columns[0],
                    new WriteValueSource.Precomputed(),
                    "CollectionItemId"
                ),
                new WriteColumnBinding(
                    tableModel.Columns[1],
                    new WriteValueSource.ParentKeyPart(0),
                    "School_DocumentId"
                ),
                new WriteColumnBinding(tableModel.Columns[2], new WriteValueSource.Ordinal(), "Ordinal"),
                new WriteColumnBinding(
                    tableModel.Columns[3],
                    new WriteValueSource.Scalar(
                        new JsonPathExpression("$.interventionCode", []),
                        new RelationalScalarType(ScalarKind.String, MaxLength: 30)
                    ),
                    "InterventionCode"
                ),
            ],
            KeyUnificationPlans: [],
            CollectionMergePlan: new CollectionMergePlan(
                [
                    new CollectionMergeSemanticIdentityBinding(
                        new JsonPathExpression("$.interventionCode", []),
                        3
                    ),
                ],
                StableRowIdentityBindingIndex: 0,
                UpdateByStableRowIdentitySql: """
                update sample."SchoolExtensionIntervention" set "Ordinal" = @Ordinal, "InterventionCode" = @InterventionCode where "CollectionItemId" = @CollectionItemId
                """,
                DeleteByStableRowIdentitySql: """
                delete from sample."SchoolExtensionIntervention" where "CollectionItemId" = @CollectionItemId
                """,
                OrdinalBindingIndex: 2,
                CompareBindingIndexesInOrder: [2, 3]
            ),
            CollectionKeyPreallocationPlan: new CollectionKeyPreallocationPlan(
                new DbColumnName("CollectionItemId"),
                0
            )
        );
    }

    private static TableWritePlan CreateCollectionExtensionScopePlan()
    {
        var tableModel = new DbTableModel(
            new DbTableName(new DbSchemaName("sample"), "SchoolExtensionAddress"),
            new JsonPathExpression("$.addresses[*]._ext.sample", []),
            new TableKey(
                "PK_SchoolExtensionAddress",
                [new DbKeyColumn(new DbColumnName("BaseCollectionItemId"), ColumnKind.ParentKeyPart)]
            ),
            [
                CreateColumn("BaseCollectionItemId", ColumnKind.ParentKeyPart),
                CreateColumn("FavoriteColor", ColumnKind.Scalar),
            ],
            []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                DbTableKind.CollectionExtensionScope,
                [new DbColumnName("BaseCollectionItemId")],
                [new DbColumnName("BaseCollectionItemId")],
                [new DbColumnName("BaseCollectionItemId")],
                []
            ),
        };

        return new TableWritePlan(
            tableModel,
            InsertSql: """
            insert into sample."SchoolExtensionAddress" values (@BaseCollectionItemId, @FavoriteColor)
            """,
            UpdateSql: """
            update sample."SchoolExtensionAddress" set "FavoriteColor" = @FavoriteColor where "BaseCollectionItemId" = @BaseCollectionItemId
            """,
            DeleteByParentSql: """
            delete from sample."SchoolExtensionAddress" where "BaseCollectionItemId" = @BaseCollectionItemId
            """,
            BulkInsertBatching: new BulkInsertBatchingInfo(100, 2, 1000),
            ColumnBindings:
            [
                new WriteColumnBinding(
                    tableModel.Columns[0],
                    new WriteValueSource.ParentKeyPart(0),
                    "BaseCollectionItemId"
                ),
                new WriteColumnBinding(
                    tableModel.Columns[1],
                    new WriteValueSource.Scalar(
                        new JsonPathExpression("$.favoriteColor", []),
                        new RelationalScalarType(ScalarKind.String, MaxLength: 30)
                    ),
                    "FavoriteColor"
                ),
            ],
            KeyUnificationPlans: []
        );
    }

    private static TableWritePlan CreateCollectionAlignedExtensionChildCollectionPlan()
    {
        var tableModel = new DbTableModel(
            new DbTableName(new DbSchemaName("sample"), "SchoolExtensionAddressService"),
            new JsonPathExpression("$.addresses[*]._ext.sample.services[*]", []),
            new TableKey(
                "PK_SchoolExtensionAddressService",
                [new DbKeyColumn(new DbColumnName("CollectionItemId"), ColumnKind.CollectionKey)]
            ),
            [
                CreateColumn("CollectionItemId", ColumnKind.CollectionKey),
                CreateColumn("School_DocumentId", ColumnKind.ParentKeyPart),
                CreateColumn("BaseCollectionItemId", ColumnKind.ParentKeyPart),
                CreateColumn("Ordinal", ColumnKind.Ordinal),
                CreateColumn("ServiceName", ColumnKind.Scalar),
            ],
            []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                DbTableKind.ExtensionCollection,
                [new DbColumnName("CollectionItemId")],
                [new DbColumnName("School_DocumentId")],
                [new DbColumnName("BaseCollectionItemId")],
                [
                    new CollectionSemanticIdentityBinding(
                        new JsonPathExpression("$.serviceName", []),
                        new DbColumnName("ServiceName")
                    ),
                ]
            ),
        };

        return new TableWritePlan(
            tableModel,
            InsertSql: """
            insert into sample."SchoolExtensionAddressService" values (@CollectionItemId, @School_DocumentId, @BaseCollectionItemId, @Ordinal, @ServiceName)
            """,
            UpdateSql: null,
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(100, 5, 1000),
            ColumnBindings:
            [
                new WriteColumnBinding(
                    tableModel.Columns[0],
                    new WriteValueSource.Precomputed(),
                    "CollectionItemId"
                ),
                new WriteColumnBinding(
                    tableModel.Columns[1],
                    new WriteValueSource.DocumentId(),
                    "School_DocumentId"
                ),
                new WriteColumnBinding(
                    tableModel.Columns[2],
                    new WriteValueSource.ParentKeyPart(0),
                    "BaseCollectionItemId"
                ),
                new WriteColumnBinding(tableModel.Columns[3], new WriteValueSource.Ordinal(), "Ordinal"),
                new WriteColumnBinding(
                    tableModel.Columns[4],
                    new WriteValueSource.Scalar(
                        new JsonPathExpression("$.serviceName", []),
                        new RelationalScalarType(ScalarKind.String, MaxLength: 30)
                    ),
                    "ServiceName"
                ),
            ],
            KeyUnificationPlans: [],
            CollectionMergePlan: new CollectionMergePlan(
                [new CollectionMergeSemanticIdentityBinding(new JsonPathExpression("$.serviceName", []), 4)],
                StableRowIdentityBindingIndex: 0,
                UpdateByStableRowIdentitySql: """
                update sample."SchoolExtensionAddressService" set "Ordinal" = @Ordinal, "ServiceName" = @ServiceName where "CollectionItemId" = @CollectionItemId
                """,
                DeleteByStableRowIdentitySql: """
                delete from sample."SchoolExtensionAddressService" where "CollectionItemId" = @CollectionItemId
                """,
                OrdinalBindingIndex: 3,
                CompareBindingIndexesInOrder: [3, 4]
            ),
            CollectionKeyPreallocationPlan: new CollectionKeyPreallocationPlan(
                new DbColumnName("CollectionItemId"),
                0
            )
        );
    }

    private static DbColumnModel CreateColumn(string name, ColumnKind kind, bool isNullable = false)
    {
        return new DbColumnModel(
            new DbColumnName(name),
            kind,
            kind is ColumnKind.Scalar or ColumnKind.Ordinal
                ? new RelationalScalarType(ScalarKind.String)
                : null,
            isNullable,
            null,
            null,
            new ColumnStorage.Stored()
        );
    }

    private static MergeTableRow CreateRow(params object?[] values)
    {
        return new MergeTableRow(
            values.Select(value =>
                value switch
                {
                    FlattenedWriteValue flattenedWriteValue => flattenedWriteValue,
                    _ => new FlattenedWriteValue.Literal(value),
                }
            ),
            values.Select(value =>
                value switch
                {
                    FlattenedWriteValue flattenedWriteValue => flattenedWriteValue,
                    _ => new FlattenedWriteValue.Literal(value),
                }
            )
        );
    }

    private static FlattenedWriteValue.UnresolvedCollectionItemId NewCollectionItemId() =>
        FlattenedWriteValue.UnresolvedCollectionItemId.Create();

    private sealed record ReservedCollectionItemIdRow(int Ordinal, long CollectionItemId);

    private sealed record CommandResponse(
        object? ScalarResult = null,
        int NonQueryResult = 1,
        IReadOnlyList<ReservedCollectionItemIdRow>? ReservationRows = null
    );

    private sealed class RecordingRelationalWriteSession : IRelationalWriteSession
    {
        private readonly DbConnection _connection = new StubDbConnection();
        private readonly Queue<CommandResponse> _responses;

        public List<RelationalCommand> Commands { get; } = [];

        public DbConnection Connection => _connection;

        public DbTransaction Transaction { get; }

        public RecordingRelationalWriteSession(IEnumerable<CommandResponse> responses)
        {
            _responses = new Queue<CommandResponse>(responses);
            Transaction = new StubDbTransaction(_connection);
        }

        public DbCommand CreateCommand(RelationalCommand command)
        {
            Commands.Add(command);
            var response = _responses.Count == 0 ? new CommandResponse() : _responses.Dequeue();

            return new RecordingDbCommand(response);
        }

        public Task CommitAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RollbackAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingDbCommand(CommandResponse response) : DbCommand
    {
        protected override DbConnection? DbConnection { get; set; }

        protected override DbParameterCollection DbParameterCollection { get; } =
            new StubDbParameterCollection();

        protected override DbTransaction? DbTransaction { get; set; }

        [AllowNull]
        public override string CommandText { get; set; } = string.Empty;

        public override int CommandTimeout { get; set; }

        public override CommandType CommandType { get; set; }

        public override bool DesignTimeVisible { get; set; }

        public override UpdateRowSource UpdatedRowSource { get; set; }

        public override void Cancel() { }

        public override int ExecuteNonQuery() => response.NonQueryResult;

        public override object? ExecuteScalar() => response.ScalarResult;

        public override void Prepare() { }

        protected override DbParameter CreateDbParameter() => new StubDbParameter();

        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) =>
            CreateReservationReader(response);

        protected override Task<DbDataReader> ExecuteDbDataReaderAsync(
            CommandBehavior behavior,
            CancellationToken cancellationToken
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<DbDataReader>(CreateReservationReader(response));
        }

        public override Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(response.NonQueryResult);
        }

        public override Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(response.ScalarResult);
        }

        private static DbDataReader CreateReservationReader(CommandResponse response)
        {
            var table = new DataTable();
            table.Columns.Add("Ordinal", typeof(int));
            table.Columns.Add("CollectionItemId", typeof(long));

            foreach (var row in response.ReservationRows ?? [])
            {
                table.Rows.Add(row.Ordinal, row.CollectionItemId);
            }

            return table.CreateDataReader();
        }
    }

    private sealed class StubDbParameterCollection : DbParameterCollection
    {
        public override int Count => 0;

        public override object SyncRoot => this;

        public override int Add(object value) => 0;

        public override void AddRange(Array values) { }

        public override void Clear() { }

        public override bool Contains(object value) => false;

        public override bool Contains(string value) => false;

        public override void CopyTo(Array array, int index) { }

        public override IEnumerator GetEnumerator() => Array.Empty<object>().GetEnumerator();

        protected override DbParameter GetParameter(int index) => throw new IndexOutOfRangeException();

        protected override DbParameter GetParameter(string parameterName) =>
            throw new IndexOutOfRangeException();

        public override int IndexOf(object value) => -1;

        public override int IndexOf(string parameterName) => -1;

        public override void Insert(int index, object value) { }

        public override void Remove(object value) { }

        public override void RemoveAt(int index) { }

        public override void RemoveAt(string parameterName) { }

        protected override void SetParameter(int index, DbParameter value) { }

        protected override void SetParameter(string parameterName, DbParameter value) { }
    }

    private sealed class StubDbParameter : DbParameter
    {
        public override DbType DbType { get; set; }

        public override ParameterDirection Direction { get; set; }

        public override bool IsNullable { get; set; }

        [AllowNull]
        public override string ParameterName { get; set; } = string.Empty;

        [AllowNull]
        public override string SourceColumn { get; set; } = string.Empty;

        public override object? Value { get; set; }

        public override bool SourceColumnNullMapping { get; set; }

        public override int Size { get; set; }

        public override void ResetDbType() { }
    }

    private sealed class StubDbConnection : DbConnection
    {
        [AllowNull]
        public override string ConnectionString { get; set; } = string.Empty;

        public override string Database => "stub";

        public override string DataSource => "stub";

        public override string ServerVersion => "1.0";

        public override ConnectionState State => ConnectionState.Open;

        public override void ChangeDatabase(string databaseName) { }

        public override void Close() { }

        public override void Open() { }

        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
        {
            throw new NotSupportedException();
        }

        protected override DbCommand CreateDbCommand()
        {
            throw new NotSupportedException();
        }
    }

    private sealed class StubDbTransaction(DbConnection connection) : DbTransaction
    {
        public override IsolationLevel IsolationLevel => IsolationLevel.ReadCommitted;

        protected override DbConnection DbConnection => connection;

        public override void Commit() { }

        public override void Rollback() { }
    }
}

internal sealed record RelationalWriteNoProfileMergeResult
{
    public RelationalWriteNoProfileMergeResult(
        IEnumerable<RelationalWriteNoProfileTableState> tablesInDependencyOrder
    )
    {
        TablesInDependencyOrder = [.. tablesInDependencyOrder];
    }

    public IReadOnlyList<RelationalWriteNoProfileTableState> TablesInDependencyOrder { get; init; }

    public static implicit operator RelationalWriteMergeResult(
        RelationalWriteNoProfileMergeResult legacyResult
    )
    {
        ArgumentNullException.ThrowIfNull(legacyResult);

        return new RelationalWriteMergeResult(
            legacyResult.TablesInDependencyOrder.Select(tableState => tableState.ToUnifiedState())
        );
    }
}

internal sealed record RelationalWriteNoProfileTableState
{
    public RelationalWriteNoProfileTableState(
        TableWritePlan tableWritePlan,
        IEnumerable<MergeTableRow> currentRows,
        IEnumerable<MergeTableRow> mergedRows
    )
    {
        TableWritePlan = tableWritePlan;
        CurrentRows = [.. currentRows];
        MergedRows = [.. mergedRows];
    }

    public TableWritePlan TableWritePlan { get; init; }

    public IReadOnlyList<MergeTableRow> CurrentRows { get; init; }

    public IReadOnlyList<MergeTableRow> MergedRows { get; init; }

    public RelationalWriteMergeTableState ToUnifiedState()
    {
        List<MergeRowInsert> inserts = [];
        List<MergeRowUpdate> updates = [];
        List<MergeRowDelete> deletes = [];

        if (IsCollectionAlignedExtensionScope(TableWritePlan))
        {
            var currentRowsByPhysicalIdentity = CurrentRows.ToDictionary(currentRow =>
                ResolvePhysicalRowIdentityKey(TableWritePlan, currentRow)
            );
            var mergedRowsByPhysicalIdentity = MergedRows.ToDictionary(mergedRow =>
                ResolvePhysicalRowIdentityKey(TableWritePlan, mergedRow)
            );

            foreach (var currentRow in CurrentRows)
            {
                if (
                    !mergedRowsByPhysicalIdentity.ContainsKey(
                        ResolvePhysicalRowIdentityKey(TableWritePlan, currentRow)
                    )
                )
                {
                    deletes.Add(new MergeRowDelete(StableRowIdentityValue: null));
                }
            }

            foreach (var mergedRow in MergedRows)
            {
                var physicalIdentity = ResolvePhysicalRowIdentityKey(TableWritePlan, mergedRow);

                if (!currentRowsByPhysicalIdentity.TryGetValue(physicalIdentity, out var currentRow))
                {
                    inserts.Add(new MergeRowInsert([.. mergedRow.Values]));
                    continue;
                }

                if (!currentRow.Values.SequenceEqual(mergedRow.Values))
                {
                    updates.Add(new MergeRowUpdate([.. mergedRow.Values], StableRowIdentityValue: null));
                }
            }
        }
        else if (TableWritePlan.CollectionMergePlan is not null)
        {
            var currentRowsByStableIdentity = CurrentRows.ToDictionary(currentRow =>
                ResolveStableRowIdentityLiteral(
                    TableWritePlan,
                    currentRow.Values[TableWritePlan.CollectionMergePlan.StableRowIdentityBindingIndex]
                )
            );

            foreach (var currentRow in CurrentRows)
            {
                var stableRowIdentity = ResolveStableRowIdentityLiteral(
                    TableWritePlan,
                    currentRow.Values[TableWritePlan.CollectionMergePlan.StableRowIdentityBindingIndex]
                );

                if (
                    !MergedRows.Any(mergedRow =>
                        mergedRow.Values[TableWritePlan.CollectionMergePlan.StableRowIdentityBindingIndex]
                        is FlattenedWriteValue.Literal
                        && ResolveStableRowIdentityLiteral(
                                TableWritePlan,
                                mergedRow.Values[
                                    TableWritePlan.CollectionMergePlan.StableRowIdentityBindingIndex
                                ]
                            )
                            == stableRowIdentity
                    )
                )
                {
                    deletes.Add(new MergeRowDelete(stableRowIdentity));
                }
            }

            foreach (var mergedRow in MergedRows)
            {
                var stableIdentityValue =
                    mergedRow.Values[TableWritePlan.CollectionMergePlan.StableRowIdentityBindingIndex];

                if (stableIdentityValue is FlattenedWriteValue.UnresolvedCollectionItemId)
                {
                    inserts.Add(new MergeRowInsert([.. mergedRow.Values]));
                    continue;
                }

                var stableRowIdentity = ResolveStableRowIdentityLiteral(
                    TableWritePlan,
                    stableIdentityValue
                );

                if (!currentRowsByStableIdentity.TryGetValue(stableRowIdentity, out var currentRow))
                {
                    throw new InvalidOperationException(
                        $"Compatibility merge result for table '{TableWritePlan.TableModel.Table}' produced stable identity '{stableRowIdentity}' without a matching current row."
                    );
                }

                if (!currentRow.Values.SequenceEqual(mergedRow.Values))
                {
                    updates.Add(
                        new MergeRowUpdate([.. mergedRow.Values], StableRowIdentityValue: stableRowIdentity)
                    );
                }
            }
        }
        else
        {
            var currentRow = CurrentRows.Count == 1 ? CurrentRows[0] : null;
            var mergedRow = MergedRows.Count == 1 ? MergedRows[0] : null;

            if (currentRow is null && mergedRow is not null)
            {
                inserts.Add(new MergeRowInsert([.. mergedRow.Values]));
            }
            else if (currentRow is not null && mergedRow is null)
            {
                deletes.Add(new MergeRowDelete(StableRowIdentityValue: null));
            }
            else if (
                currentRow is not null
                && mergedRow is not null
                && !currentRow.Values.SequenceEqual(mergedRow.Values)
            )
            {
                updates.Add(new MergeRowUpdate([.. mergedRow.Values], StableRowIdentityValue: null));
            }
        }

        return new RelationalWriteMergeTableState(
            TableWritePlan,
            inserts,
            updates,
            deletes,
            preservedRows: [],
            comparableCurrentRowset: CurrentRows,
            comparableMergedRowset: MergedRows
        );
    }

    private static bool IsCollectionAlignedExtensionScope(TableWritePlan tableWritePlan) =>
        tableWritePlan.TableModel.IdentityMetadata.TableKind == DbTableKind.CollectionExtensionScope;

    private static string ResolvePhysicalRowIdentityKey(TableWritePlan tableWritePlan, MergeTableRow row) =>
        RelationalWritePersisterShared.ResolvePhysicalRowIdentityKey(tableWritePlan, row);

    private static long ResolveStableRowIdentityLiteral(
        TableWritePlan tableWritePlan,
        FlattenedWriteValue stableRowIdentityValue
    )
    {
        return stableRowIdentityValue switch
        {
            FlattenedWriteValue.Literal { Value: long stableId } => stableId,
            FlattenedWriteValue.Literal { Value: int stableId } => stableId,
            FlattenedWriteValue.Literal { Value: short stableId } => stableId,
            FlattenedWriteValue.Literal { Value: byte stableId } => stableId,
            FlattenedWriteValue.Literal { Value: var value } => Convert.ToInt64(value, CultureInfo.InvariantCulture),
            _ => throw new InvalidOperationException(
                $"Collection table '{tableWritePlan.TableModel.Table}' expected a literal stable row identity."
            ),
        };
    }
}

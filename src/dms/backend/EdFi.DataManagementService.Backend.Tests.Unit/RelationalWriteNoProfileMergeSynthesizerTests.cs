// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Core.Profile;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
[Parallelizable]
public class Given_Relational_Write_No_Profile_Merge_Synthesizer
{
    private RelationalWriteNoProfileMergeSynthesizer _sut = null!;

    [SetUp]
    public void Setup()
    {
        _sut = new RelationalWriteNoProfileMergeSynthesizer();
    }

    [Test]
    public void It_projects_non_collection_rows_into_the_shared_compare_space()
    {
        var fixture = CreateFixture();
        var flattenedWriteSet = new FlattenedWriteSet(
            new RootWriteRowBuffer(
                fixture.RootPlan,
                [Literal(345L), Literal("Lincoln Updated")],
                rootExtensionRows:
                [
                    new RootExtensionWriteRowBuffer(
                        fixture.RootExtensionPlan,
                        [Literal(345L), Literal("sample-2")]
                    ),
                ]
            )
        );
        var currentState = CreateCurrentState(
            fixture,
            rootRows:
            [
                [345L, "Lincoln High"],
            ],
            rootExtensionRows:
            [
                [345L, "sample-1"],
            ]
        );

        var result = _sut.Synthesize(
            new RelationalWriteNoProfileMergeRequest(fixture.WritePlan, flattenedWriteSet, currentState)
        );

        result.TablesInDependencyOrder.Should().HaveCount(4);

        var rootState = result.TablesInDependencyOrder[0];
        rootState.CurrentRows.Should().ContainSingle();
        rootState.MergedRows.Should().ContainSingle();
        LiteralValue(rootState.CurrentRows[0].Values[0]).Should().Be(345L);
        LiteralValue(rootState.CurrentRows[0].Values[1]).Should().Be("Lincoln High");
        LiteralValue(rootState.MergedRows[0].Values[0]).Should().Be(345L);
        LiteralValue(rootState.MergedRows[0].Values[1]).Should().Be("Lincoln Updated");
        rootState.CurrentRows[0].ComparableValues.Should().BeEquivalentTo(rootState.CurrentRows[0].Values);
        rootState.MergedRows[0].ComparableValues.Should().BeEquivalentTo(rootState.MergedRows[0].Values);

        var rootExtensionState = result.TablesInDependencyOrder[1];
        rootExtensionState.CurrentRows.Should().ContainSingle();
        rootExtensionState.MergedRows.Should().ContainSingle();
        LiteralValue(rootExtensionState.CurrentRows[0].Values[1]).Should().Be("sample-1");
        LiteralValue(rootExtensionState.MergedRows[0].Values[1]).Should().Be("sample-2");
        rootExtensionState
            .CurrentRows[0]
            .ComparableValues.Should()
            .BeEquivalentTo(rootExtensionState.CurrentRows[0].Values);
        rootExtensionState
            .MergedRows[0]
            .ComparableValues.Should()
            .BeEquivalentTo(rootExtensionState.MergedRows[0].Values);

        result.TablesInDependencyOrder[2].CurrentRows.Should().BeEmpty();
        result.TablesInDependencyOrder[2].MergedRows.Should().BeEmpty();
        result.TablesInDependencyOrder[3].CurrentRows.Should().BeEmpty();
        result.TablesInDependencyOrder[3].MergedRows.Should().BeEmpty();
    }

    [Test]
    public void It_merges_collection_candidates_using_compare_order_and_request_order()
    {
        var fixture = CreateFixture();
        var homeCollectionItemId = NewCollectionItemId();
        var physicalCollectionItemId = NewCollectionItemId();
        var flattenedWriteSet = new FlattenedWriteSet(
            new RootWriteRowBuffer(
                fixture.RootPlan,
                [Literal(345L), Literal("Lincoln High")],
                collectionCandidates:
                [
                    CreateAddressCandidate(
                        fixture,
                        requestOrder: 0,
                        collectionItemId: homeCollectionItemId,
                        addressType: "Home",
                        city: "Oak Updated"
                    ),
                    CreateAddressCandidate(
                        fixture,
                        requestOrder: 1,
                        collectionItemId: physicalCollectionItemId,
                        addressType: "Physical",
                        city: "New"
                    ),
                ]
            )
        );
        var currentState = CreateCurrentState(
            fixture,
            rootRows:
            [
                [345L, "Lincoln High"],
            ],
            addressRows:
            [
                [10L, 345L, 0, "Mailing", "Old"],
                [11L, 345L, 1, "Home", "Oak"],
            ]
        );

        var result = _sut.Synthesize(
            new RelationalWriteNoProfileMergeRequest(fixture.WritePlan, flattenedWriteSet, currentState)
        );

        var addressState = result.TablesInDependencyOrder[2];
        addressState.CurrentRows.Should().HaveCount(2);
        addressState.MergedRows.Should().HaveCount(2);
        addressState.CurrentRows[0].ComparableValues.Select(LiteralValue).Should().Equal(0, "Mailing", "Old");
        addressState.CurrentRows[1].ComparableValues.Select(LiteralValue).Should().Equal(1, "Home", "Oak");
        addressState
            .MergedRows[0]
            .ComparableValues.Select(LiteralValue)
            .Should()
            .Equal(0, "Home", "Oak Updated");
        addressState.MergedRows[1].ComparableValues.Select(LiteralValue).Should().Equal(1, "Physical", "New");

        LiteralValue(addressState.MergedRows[0].Values[0]).Should().Be(11L);
        LiteralValue(addressState.MergedRows[0].Values[2]).Should().Be(0);
        LiteralValue(addressState.MergedRows[0].Values[3]).Should().Be("Home");
        LiteralValue(addressState.MergedRows[0].Values[4]).Should().Be("Oak Updated");
        addressState.MergedRows[1].Values[0].Should().BeSameAs(physicalCollectionItemId);
        LiteralValue(addressState.MergedRows[1].Values[2]).Should().Be(1);
        LiteralValue(addressState.MergedRows[1].Values[3]).Should().Be("Physical");
        LiteralValue(addressState.MergedRows[1].Values[4]).Should().Be("New");
    }

    [Test]
    public void It_distinguishes_missing_from_explicit_null_when_matching_collection_current_rows()
    {
        // Repro for Finding 17: a request candidate with a missing identity part must not
        // match a current row whose persisted identity column is NULL, while a request
        // candidate with an explicit JSON null identity must match that same NULL row.
        var fixture = CreateFixture();
        var missingCandidateId = NewCollectionItemId();
        var explicitNullCandidateId = NewCollectionItemId();

        var missingIdentity = ImmutableArray.Create(
            new SemanticIdentityPart("addressType", Value: null, IsPresent: false)
        );
        var explicitNullIdentity = ImmutableArray.Create(
            new SemanticIdentityPart("addressType", Value: null, IsPresent: true)
        );

        var flattenedWriteSet = new FlattenedWriteSet(
            new RootWriteRowBuffer(
                fixture.RootPlan,
                [Literal(345L), Literal("Lincoln High")],
                collectionCandidates:
                [
                    CreateAddressCandidate(
                        fixture,
                        requestOrder: 0,
                        collectionItemId: missingCandidateId,
                        addressType: null,
                        city: "Missing City",
                        semanticIdentityInOrder: missingIdentity
                    ),
                    CreateAddressCandidate(
                        fixture,
                        requestOrder: 1,
                        collectionItemId: explicitNullCandidateId,
                        addressType: null,
                        city: "Null City",
                        semanticIdentityInOrder: explicitNullIdentity
                    ),
                ]
            )
        );

        var currentState = CreateCurrentState(
            fixture,
            rootRows:
            [
                [345L, "Lincoln High"],
            ],
            addressRows:
            [
                [11L, 345L, 0, null, "Persisted City"],
            ]
        );

        var result = _sut.Synthesize(
            new RelationalWriteNoProfileMergeRequest(fixture.WritePlan, flattenedWriteSet, currentState)
        );

        var addressState = result.TablesInDependencyOrder[2];
        addressState.MergedRows.Should().HaveCount(2);

        // The missing-identity candidate must not match the persisted NULL row, so it keeps
        // its original unresolved collection-id placeholder.
        addressState.MergedRows[0].Values[0].Should().BeSameAs(missingCandidateId);

        // The explicit-null-identity candidate must match the persisted NULL row and reuse
        // the stable id 11.
        LiteralValue(addressState.MergedRows[1].Values[0]).Should().Be(11L);
    }

    [Test]
    public void It_rewrites_nested_parent_scope_keys_from_matched_collection_rows()
    {
        var fixture = CreateFixture();
        var addressCollectionItemId = NewCollectionItemId();
        var periodCollectionItemId = NewCollectionItemId();
        var nestedPeriodCandidate = CreatePeriodCandidate(
            fixture,
            requestOrder: 0,
            collectionItemId: periodCollectionItemId,
            parentCollectionItemId: addressCollectionItemId,
            beginDate: "2026-09-01",
            room: "Updated Room"
        );
        var flattenedWriteSet = new FlattenedWriteSet(
            new RootWriteRowBuffer(
                fixture.RootPlan,
                [Literal(345L), Literal("Lincoln High")],
                collectionCandidates:
                [
                    CreateAddressCandidate(
                        fixture,
                        requestOrder: 0,
                        collectionItemId: addressCollectionItemId,
                        addressType: "Home",
                        city: "Oak Updated",
                        periods: [nestedPeriodCandidate]
                    ),
                ]
            )
        );
        var currentState = CreateCurrentState(
            fixture,
            rootRows:
            [
                [345L, "Lincoln High"],
            ],
            addressRows:
            [
                [11L, 345L, 0, "Home", "Oak"],
            ],
            periodRows:
            [
                [101L, 345L, 11L, 0, "2026-09-01", "Morning"],
            ]
        );

        var result = _sut.Synthesize(
            new RelationalWriteNoProfileMergeRequest(fixture.WritePlan, flattenedWriteSet, currentState)
        );

        var addressState = result.TablesInDependencyOrder[2];
        var periodState = result.TablesInDependencyOrder[3];

        addressState.MergedRows.Should().ContainSingle();
        periodState.CurrentRows.Should().ContainSingle();
        periodState.MergedRows.Should().ContainSingle();
        LiteralValue(addressState.MergedRows[0].Values[0]).Should().Be(11L);
        LiteralValue(periodState.MergedRows[0].Values[0]).Should().Be(101L);
        LiteralValue(periodState.MergedRows[0].Values[2]).Should().Be(11L);
        LiteralValue(periodState.MergedRows[0].Values[4]).Should().Be("2026-09-01");
        LiteralValue(periodState.MergedRows[0].Values[5]).Should().Be("Updated Room");
        periodState
            .MergedRows[0]
            .ComparableValues.Select(LiteralValue)
            .Should()
            .Equal(0, "2026-09-01", "Updated Room");
    }

    [Test]
    public void It_normalizes_sql_server_date_and_time_root_values_into_the_shared_compare_space()
    {
        var fixture = CreateDateAndTimeFixture();
        var flattenedWriteSet = new FlattenedWriteSet(
            new RootWriteRowBuffer(
                fixture.RootPlan,
                [Literal(345L), Literal(new DateOnly(2026, 8, 20)), Literal(new TimeOnly(14, 5, 7))]
            )
        );
        var currentState = CreateDateAndTimeCurrentState(
            fixture,
            rootRows:
            [
                [345L, new DateTime(2026, 8, 20, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(14, 5, 7)],
            ]
        );

        var result = _sut.Synthesize(
            new RelationalWriteNoProfileMergeRequest(fixture.WritePlan, flattenedWriteSet, currentState)
        );

        var rootState = result.TablesInDependencyOrder[0];

        LiteralValue(rootState.CurrentRows[0].Values[1]).Should().Be(new DateOnly(2026, 8, 20));
        LiteralValue(rootState.CurrentRows[0].Values[2]).Should().Be(new TimeOnly(14, 5, 7));
        rootState
            .CurrentRows[0]
            .ComparableValues.Select(LiteralValue)
            .Should()
            .Equal(345L, new DateOnly(2026, 8, 20), new TimeOnly(14, 5, 7));
        rootState
            .MergedRows[0]
            .ComparableValues.Select(LiteralValue)
            .Should()
            .Equal(345L, new DateOnly(2026, 8, 20), new TimeOnly(14, 5, 7));
        RelationalWriteGuardedNoOp.IsNoOpCandidate(result).Should().BeTrue();
    }

    [Test]
    public void It_uses_normalized_sql_server_date_and_time_values_for_collection_semantic_identity_matching()
    {
        var fixture = CreateDateAndTimeFixture();
        var scheduleCollectionItemId = NewCollectionItemId();
        var flattenedWriteSet = new FlattenedWriteSet(
            new RootWriteRowBuffer(
                fixture.RootPlan,
                [Literal(345L), Literal(new DateOnly(2026, 8, 20)), Literal(new TimeOnly(14, 5, 7))],
                collectionCandidates:
                [
                    CreateScheduleCandidate(
                        fixture,
                        requestOrder: 0,
                        collectionItemId: scheduleCollectionItemId,
                        sessionDate: new DateOnly(2026, 9, 1),
                        startTime: new TimeOnly(8, 15),
                        room: "Updated Room"
                    ),
                ]
            )
        );
        var currentState = CreateDateAndTimeCurrentState(
            fixture,
            rootRows:
            [
                [345L, new DateTime(2026, 8, 20, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(14, 5, 7)],
            ],
            scheduleRows:
            [
                [
                    77L,
                    345L,
                    0,
                    new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Unspecified),
                    new TimeSpan(8, 15, 0),
                    "Morning Room",
                ],
            ]
        );

        var result = _sut.Synthesize(
            new RelationalWriteNoProfileMergeRequest(fixture.WritePlan, flattenedWriteSet, currentState)
        );

        var scheduleState = result.TablesInDependencyOrder[1];

        LiteralValue(scheduleState.CurrentRows[0].Values[3]).Should().Be(new DateOnly(2026, 9, 1));
        LiteralValue(scheduleState.CurrentRows[0].Values[4]).Should().Be(new TimeOnly(8, 15));
        LiteralValue(scheduleState.MergedRows[0].Values[0]).Should().Be(77L);
        scheduleState
            .CurrentRows[0]
            .ComparableValues.Select(LiteralValue)
            .Should()
            .Equal(0, new DateOnly(2026, 9, 1), new TimeOnly(8, 15), "Morning Room");
        scheduleState
            .MergedRows[0]
            .ComparableValues.Select(LiteralValue)
            .Should()
            .Equal(0, new DateOnly(2026, 9, 1), new TimeOnly(8, 15), "Updated Room");
    }

    private static WritePlanFixture CreateFixture()
    {
        var rootPlan = CreateRootPlan();
        var rootExtensionPlan = CreateRootExtensionPlan();
        var addressPlan = CreateAddressPlan();
        var periodPlan = CreatePeriodPlan();
        var resourceModel = new RelationalResourceModel(
            Resource: new QualifiedResourceName("Ed-Fi", "School"),
            PhysicalSchema: new DbSchemaName("edfi"),
            StorageKind: ResourceStorageKind.RelationalTables,
            Root: rootPlan.TableModel,
            TablesInDependencyOrder:
            [
                rootPlan.TableModel,
                rootExtensionPlan.TableModel,
                addressPlan.TableModel,
                periodPlan.TableModel,
            ],
            DocumentReferenceBindings: [],
            DescriptorEdgeSources: []
        );

        return new WritePlanFixture(
            new ResourceWritePlan(resourceModel, [rootPlan, rootExtensionPlan, addressPlan, periodPlan]),
            rootPlan,
            rootExtensionPlan,
            addressPlan,
            periodPlan
        );
    }

    private static DateAndTimeWritePlanFixture CreateDateAndTimeFixture()
    {
        var rootPlan = CreateDateAndTimeRootPlan();
        var schedulePlan = CreateSchedulePlan();
        var resourceModel = new RelationalResourceModel(
            Resource: new QualifiedResourceName("Ed-Fi", "School"),
            PhysicalSchema: new DbSchemaName("edfi"),
            StorageKind: ResourceStorageKind.RelationalTables,
            Root: rootPlan.TableModel,
            TablesInDependencyOrder: [rootPlan.TableModel, schedulePlan.TableModel],
            DocumentReferenceBindings: [],
            DescriptorEdgeSources: []
        );

        return new DateAndTimeWritePlanFixture(
            new ResourceWritePlan(resourceModel, [rootPlan, schedulePlan]),
            rootPlan,
            schedulePlan
        );
    }

    private static RelationalWriteCurrentState CreateCurrentState(
        WritePlanFixture fixture,
        IReadOnlyList<object?[]>? rootRows = null,
        IReadOnlyList<object?[]>? rootExtensionRows = null,
        IReadOnlyList<object?[]>? addressRows = null,
        IReadOnlyList<object?[]>? periodRows = null
    )
    {
        return new RelationalWriteCurrentState(
            new DocumentMetadataRow(
                345L,
                Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"),
                44L,
                44L,
                new DateTimeOffset(2026, 4, 2, 12, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 4, 2, 12, 0, 0, TimeSpan.Zero)
            ),
            [
                new HydratedTableRows(fixture.RootPlan.TableModel, rootRows ?? []),
                new HydratedTableRows(fixture.RootExtensionPlan.TableModel, rootExtensionRows ?? []),
                new HydratedTableRows(fixture.AddressPlan.TableModel, addressRows ?? []),
                new HydratedTableRows(fixture.PeriodPlan.TableModel, periodRows ?? []),
            ],
            []
        );
    }

    private static RelationalWriteCurrentState CreateDateAndTimeCurrentState(
        DateAndTimeWritePlanFixture fixture,
        IReadOnlyList<object?[]>? rootRows = null,
        IReadOnlyList<object?[]>? scheduleRows = null
    )
    {
        return new RelationalWriteCurrentState(
            new DocumentMetadataRow(
                345L,
                Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"),
                44L,
                44L,
                new DateTimeOffset(2026, 4, 2, 12, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 4, 2, 12, 0, 0, TimeSpan.Zero)
            ),
            [
                new HydratedTableRows(fixture.RootPlan.TableModel, rootRows ?? []),
                new HydratedTableRows(fixture.SchedulePlan.TableModel, scheduleRows ?? []),
            ],
            []
        );
    }

    private static FlattenedWriteValue.UnresolvedCollectionItemId NewCollectionItemId() =>
        FlattenedWriteValue.UnresolvedCollectionItemId.Create();

    private static CollectionWriteCandidate CreateAddressCandidate(
        WritePlanFixture fixture,
        int requestOrder,
        FlattenedWriteValue collectionItemId,
        string? addressType,
        string city,
        IReadOnlyList<CollectionWriteCandidate>? periods = null,
        ImmutableArray<SemanticIdentityPart>? semanticIdentityInOrder = null
    )
    {
        var resolvedSemanticIdentityInOrder =
            semanticIdentityInOrder ?? BuildScopeRelativeIdentity(fixture.AddressPlan, [addressType]);

        return new CollectionWriteCandidate(
            fixture.AddressPlan,
            ordinalPath: [requestOrder],
            requestOrder: requestOrder,
            values:
            [
                collectionItemId,
                Literal(345L),
                Literal(requestOrder),
                Literal(addressType),
                Literal(city),
            ],
            semanticIdentityValues: [addressType],
            collectionCandidates: periods ?? [],
            semanticIdentityInOrder: resolvedSemanticIdentityInOrder.ToArray()
        );
    }

    private static CollectionWriteCandidate CreatePeriodCandidate(
        WritePlanFixture fixture,
        int requestOrder,
        FlattenedWriteValue collectionItemId,
        FlattenedWriteValue parentCollectionItemId,
        string beginDate,
        string room
    )
    {
        return new CollectionWriteCandidate(
            fixture.PeriodPlan,
            ordinalPath: [0, requestOrder],
            requestOrder: requestOrder,
            values:
            [
                collectionItemId,
                Literal(345L),
                parentCollectionItemId,
                Literal(requestOrder),
                Literal(beginDate),
                Literal(room),
            ],
            semanticIdentityValues: [beginDate],
            semanticIdentityInOrder: BuildScopeRelativeIdentity(fixture.PeriodPlan, [beginDate]).ToArray()
        );
    }

    private static CollectionWriteCandidate CreateScheduleCandidate(
        DateAndTimeWritePlanFixture fixture,
        int requestOrder,
        FlattenedWriteValue collectionItemId,
        DateOnly sessionDate,
        TimeOnly startTime,
        string room
    )
    {
        return new CollectionWriteCandidate(
            fixture.SchedulePlan,
            ordinalPath: [requestOrder],
            requestOrder: requestOrder,
            values:
            [
                collectionItemId,
                Literal(345L),
                Literal(requestOrder),
                Literal(sessionDate),
                Literal(startTime),
                Literal(room),
            ],
            semanticIdentityValues: [sessionDate, startTime],
            semanticIdentityInOrder: BuildScopeRelativeIdentity(
                    fixture.SchedulePlan,
                    [sessionDate, startTime]
                )
                .ToArray()
        );
    }

    /// <summary>
    /// Helper that builds a presence-aware <see cref="SemanticIdentityPart"/> sequence using
    /// the same scope-relative path convention the production flattener emits, so test
    /// candidates align with the new no-profile merge matcher's current-row identity contract.
    /// </summary>
    private static ImmutableArray<SemanticIdentityPart> BuildScopeRelativeIdentity(
        TableWritePlan tableWritePlan,
        IReadOnlyList<object?> semanticIdentityValues
    )
    {
        var mergePlan = tableWritePlan.CollectionMergePlan!;
        var scopeCanonical = tableWritePlan.TableModel.JsonScope.Canonical;
        var bindings = mergePlan.SemanticIdentityBindings;
        var parts = new SemanticIdentityPart[bindings.Length];

        for (var index = 0; index < bindings.Length; index++)
        {
            var rawValue = semanticIdentityValues[index];
            JsonNode? jsonValue = rawValue is null ? null : JsonValue.Create(rawValue);
            var relativePath = ToScopeRelativeIdentityPath(
                bindings[index].RelativePath.Canonical,
                scopeCanonical
            );
            parts[index] = new SemanticIdentityPart(relativePath, jsonValue, IsPresent: rawValue is not null);
        }

        return [.. parts];
    }

    private static string ToScopeRelativeIdentityPath(string canonicalPath, string scopeCanonical)
    {
        var scopePrefix = scopeCanonical + ".";
        if (canonicalPath.StartsWith(scopePrefix, StringComparison.Ordinal))
        {
            return canonicalPath[scopePrefix.Length..];
        }

        return canonicalPath.StartsWith("$.", StringComparison.Ordinal) ? canonicalPath[2..] : canonicalPath;
    }

    private static TableWritePlan CreateRootPlan()
    {
        var tableModel = new DbTableModel(
            new DbTableName(new DbSchemaName("edfi"), "School"),
            new JsonPathExpression("$", []),
            new TableKey(
                "PK_School",
                [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            [
                new DbColumnModel(
                    new DbColumnName("DocumentId"),
                    ColumnKind.ParentKeyPart,
                    null,
                    false,
                    null,
                    null,
                    new ColumnStorage.Stored()
                ),
                new DbColumnModel(
                    new DbColumnName("Name"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 75),
                    false,
                    new JsonPathExpression("$.name", [new JsonPathSegment.Property("name")]),
                    null,
                    new ColumnStorage.Stored()
                ),
            ],
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

        return new TableWritePlan(
            tableModel,
            InsertSql: "insert into edfi.\"School\" values (@DocumentId, @Name)",
            UpdateSql: "update edfi.\"School\" set \"Name\" = @Name where \"DocumentId\" = @DocumentId",
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(100, 2, 1000),
            ColumnBindings:
            [
                new WriteColumnBinding(
                    tableModel.Columns[0],
                    new WriteValueSource.DocumentId(),
                    "DocumentId"
                ),
                new WriteColumnBinding(
                    tableModel.Columns[1],
                    new WriteValueSource.Scalar(
                        new JsonPathExpression("$.name", [new JsonPathSegment.Property("name")]),
                        new RelationalScalarType(ScalarKind.String, MaxLength: 75)
                    ),
                    "Name"
                ),
            ],
            KeyUnificationPlans: []
        );
    }

    private static TableWritePlan CreateDateAndTimeRootPlan()
    {
        var tableModel = new DbTableModel(
            new DbTableName(new DbSchemaName("edfi"), "School"),
            new JsonPathExpression("$", []),
            new TableKey(
                "PK_School",
                [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            [
                new DbColumnModel(
                    new DbColumnName("DocumentId"),
                    ColumnKind.ParentKeyPart,
                    null,
                    false,
                    null,
                    null,
                    new ColumnStorage.Stored()
                ),
                new DbColumnModel(
                    new DbColumnName("SessionDate"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Date),
                    false,
                    new JsonPathExpression("$.sessionDate", [new JsonPathSegment.Property("sessionDate")]),
                    null,
                    new ColumnStorage.Stored()
                ),
                new DbColumnModel(
                    new DbColumnName("StartTime"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Time),
                    false,
                    new JsonPathExpression("$.startTime", [new JsonPathSegment.Property("startTime")]),
                    null,
                    new ColumnStorage.Stored()
                ),
            ],
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

        return new TableWritePlan(
            tableModel,
            InsertSql: "insert into edfi.\"School\" values (@DocumentId, @SessionDate, @StartTime)",
            UpdateSql: "update edfi.\"School\" set \"SessionDate\" = @SessionDate, \"StartTime\" = @StartTime where \"DocumentId\" = @DocumentId",
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(100, 3, 1000),
            ColumnBindings:
            [
                new WriteColumnBinding(
                    tableModel.Columns[0],
                    new WriteValueSource.DocumentId(),
                    "DocumentId"
                ),
                new WriteColumnBinding(
                    tableModel.Columns[1],
                    new WriteValueSource.Scalar(
                        new JsonPathExpression(
                            "$.sessionDate",
                            [new JsonPathSegment.Property("sessionDate")]
                        ),
                        new RelationalScalarType(ScalarKind.Date)
                    ),
                    "SessionDate"
                ),
                new WriteColumnBinding(
                    tableModel.Columns[2],
                    new WriteValueSource.Scalar(
                        new JsonPathExpression("$.startTime", [new JsonPathSegment.Property("startTime")]),
                        new RelationalScalarType(ScalarKind.Time)
                    ),
                    "StartTime"
                ),
            ],
            KeyUnificationPlans: []
        );
    }

    private static TableWritePlan CreateRootExtensionPlan()
    {
        var tableModel = new DbTableModel(
            new DbTableName(new DbSchemaName("sample"), "SchoolExtension"),
            new JsonPathExpression(
                "$._ext.sample",
                [new JsonPathSegment.Property("_ext"), new JsonPathSegment.Property("sample")]
            ),
            new TableKey(
                "PK_SchoolExtension",
                [new DbKeyColumn(new DbColumnName("School_DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            [
                new DbColumnModel(
                    new DbColumnName("School_DocumentId"),
                    ColumnKind.ParentKeyPart,
                    null,
                    false,
                    null,
                    null,
                    new ColumnStorage.Stored()
                ),
                new DbColumnModel(
                    new DbColumnName("ExtensionCode"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 75),
                    false,
                    new JsonPathExpression(
                        "$._ext.sample.extensionCode",
                        [
                            new JsonPathSegment.Property("_ext"),
                            new JsonPathSegment.Property("sample"),
                            new JsonPathSegment.Property("extensionCode"),
                        ]
                    ),
                    null,
                    new ColumnStorage.Stored()
                ),
            ],
            []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                DbTableKind.RootExtension,
                [new DbColumnName("School_DocumentId")],
                [new DbColumnName("School_DocumentId")],
                [new DbColumnName("School_DocumentId")],
                []
            ),
        };

        return new TableWritePlan(
            tableModel,
            InsertSql: "insert into sample.\"SchoolExtension\" values (@School_DocumentId, @ExtensionCode)",
            UpdateSql: "update sample.\"SchoolExtension\" set \"ExtensionCode\" = @ExtensionCode where \"School_DocumentId\" = @School_DocumentId",
            DeleteByParentSql: "delete from sample.\"SchoolExtension\" where \"School_DocumentId\" = @School_DocumentId",
            BulkInsertBatching: new BulkInsertBatchingInfo(100, 2, 1000),
            ColumnBindings:
            [
                new WriteColumnBinding(
                    tableModel.Columns[0],
                    new WriteValueSource.DocumentId(),
                    "School_DocumentId"
                ),
                new WriteColumnBinding(
                    tableModel.Columns[1],
                    new WriteValueSource.Scalar(
                        new JsonPathExpression(
                            "$.extensionCode",
                            [new JsonPathSegment.Property("extensionCode")]
                        ),
                        new RelationalScalarType(ScalarKind.String, MaxLength: 75)
                    ),
                    "ExtensionCode"
                ),
            ],
            KeyUnificationPlans: []
        );
    }

    private static TableWritePlan CreateAddressPlan()
    {
        var tableModel = new DbTableModel(
            new DbTableName(new DbSchemaName("edfi"), "SchoolAddress"),
            new JsonPathExpression(
                "$.addresses[*]",
                [new JsonPathSegment.Property("addresses"), new JsonPathSegment.AnyArrayElement()]
            ),
            new TableKey(
                "PK_SchoolAddress",
                [new DbKeyColumn(new DbColumnName("CollectionItemId"), ColumnKind.CollectionKey)]
            ),
            [
                new DbColumnModel(
                    new DbColumnName("CollectionItemId"),
                    ColumnKind.CollectionKey,
                    new RelationalScalarType(ScalarKind.Int64),
                    false,
                    null,
                    null,
                    new ColumnStorage.Stored()
                ),
                new DbColumnModel(
                    new DbColumnName("School_DocumentId"),
                    ColumnKind.ParentKeyPart,
                    new RelationalScalarType(ScalarKind.Int64),
                    false,
                    null,
                    null,
                    new ColumnStorage.Stored()
                ),
                new DbColumnModel(
                    new DbColumnName("Ordinal"),
                    ColumnKind.Ordinal,
                    new RelationalScalarType(ScalarKind.Int32),
                    false,
                    null,
                    null,
                    new ColumnStorage.Stored()
                ),
                new DbColumnModel(
                    new DbColumnName("AddressType"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 50),
                    false,
                    new JsonPathExpression("$.addressType", [new JsonPathSegment.Property("addressType")]),
                    null,
                    new ColumnStorage.Stored()
                ),
                new DbColumnModel(
                    new DbColumnName("City"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 75),
                    false,
                    new JsonPathExpression("$.city", [new JsonPathSegment.Property("city")]),
                    null,
                    new ColumnStorage.Stored()
                ),
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
                        new JsonPathExpression(
                            "$.addressType",
                            [new JsonPathSegment.Property("addressType")]
                        ),
                        new DbColumnName("AddressType")
                    ),
                ]
            ),
        };

        return new TableWritePlan(
            tableModel,
            InsertSql: "insert into edfi.\"SchoolAddress\" values (@CollectionItemId, @School_DocumentId, @Ordinal, @AddressType, @City)",
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
                new WriteColumnBinding(tableModel.Columns[2], new WriteValueSource.Ordinal(), "Ordinal"),
                new WriteColumnBinding(
                    tableModel.Columns[3],
                    new WriteValueSource.Scalar(
                        new JsonPathExpression(
                            "$.addressType",
                            [new JsonPathSegment.Property("addressType")]
                        ),
                        new RelationalScalarType(ScalarKind.String, MaxLength: 50)
                    ),
                    "AddressType"
                ),
                new WriteColumnBinding(
                    tableModel.Columns[4],
                    new WriteValueSource.Scalar(
                        new JsonPathExpression("$.city", [new JsonPathSegment.Property("city")]),
                        new RelationalScalarType(ScalarKind.String, MaxLength: 75)
                    ),
                    "City"
                ),
            ],
            KeyUnificationPlans: [],
            CollectionMergePlan: new CollectionMergePlan(
                SemanticIdentityBindings:
                [
                    new CollectionMergeSemanticIdentityBinding(
                        new JsonPathExpression(
                            "$.addressType",
                            [new JsonPathSegment.Property("addressType")]
                        ),
                        BindingIndex: 3
                    ),
                ],
                StableRowIdentityBindingIndex: 0,
                UpdateByStableRowIdentitySql: "update edfi.\"SchoolAddress\" set \"Ordinal\" = @Ordinal, \"AddressType\" = @AddressType, \"City\" = @City where \"CollectionItemId\" = @CollectionItemId",
                DeleteByStableRowIdentitySql: "delete from edfi.\"SchoolAddress\" where \"CollectionItemId\" = @CollectionItemId",
                OrdinalBindingIndex: 2,
                CompareBindingIndexesInOrder: [2, 3, 4]
            ),
            CollectionKeyPreallocationPlan: new CollectionKeyPreallocationPlan(
                new DbColumnName("CollectionItemId"),
                0
            )
        );
    }

    private static TableWritePlan CreatePeriodPlan()
    {
        var tableModel = new DbTableModel(
            new DbTableName(new DbSchemaName("edfi"), "SchoolAddressPeriod"),
            new JsonPathExpression(
                "$.addresses[*].periods[*]",
                [
                    new JsonPathSegment.Property("addresses"),
                    new JsonPathSegment.AnyArrayElement(),
                    new JsonPathSegment.Property("periods"),
                    new JsonPathSegment.AnyArrayElement(),
                ]
            ),
            new TableKey(
                "PK_SchoolAddressPeriod",
                [new DbKeyColumn(new DbColumnName("CollectionItemId"), ColumnKind.CollectionKey)]
            ),
            [
                new DbColumnModel(
                    new DbColumnName("CollectionItemId"),
                    ColumnKind.CollectionKey,
                    new RelationalScalarType(ScalarKind.Int64),
                    false,
                    null,
                    null,
                    new ColumnStorage.Stored()
                ),
                new DbColumnModel(
                    new DbColumnName("School_DocumentId"),
                    ColumnKind.ParentKeyPart,
                    new RelationalScalarType(ScalarKind.Int64),
                    false,
                    null,
                    null,
                    new ColumnStorage.Stored()
                ),
                new DbColumnModel(
                    new DbColumnName("ParentCollectionItemId"),
                    ColumnKind.ParentKeyPart,
                    new RelationalScalarType(ScalarKind.Int64),
                    false,
                    null,
                    null,
                    new ColumnStorage.Stored()
                ),
                new DbColumnModel(
                    new DbColumnName("Ordinal"),
                    ColumnKind.Ordinal,
                    new RelationalScalarType(ScalarKind.Int32),
                    false,
                    null,
                    null,
                    new ColumnStorage.Stored()
                ),
                new DbColumnModel(
                    new DbColumnName("BeginDate"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 20),
                    false,
                    new JsonPathExpression("$.beginDate", [new JsonPathSegment.Property("beginDate")]),
                    null,
                    new ColumnStorage.Stored()
                ),
                new DbColumnModel(
                    new DbColumnName("Room"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 50),
                    false,
                    new JsonPathExpression("$.room", [new JsonPathSegment.Property("room")]),
                    null,
                    new ColumnStorage.Stored()
                ),
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
                        new JsonPathExpression("$.beginDate", [new JsonPathSegment.Property("beginDate")]),
                        new DbColumnName("BeginDate")
                    ),
                ]
            ),
        };

        return new TableWritePlan(
            tableModel,
            InsertSql: "insert into edfi.\"SchoolAddressPeriod\" values (@CollectionItemId, @School_DocumentId, @ParentCollectionItemId, @Ordinal, @BeginDate, @Room)",
            UpdateSql: null,
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(100, 6, 1000),
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
                    "ParentCollectionItemId"
                ),
                new WriteColumnBinding(tableModel.Columns[3], new WriteValueSource.Ordinal(), "Ordinal"),
                new WriteColumnBinding(
                    tableModel.Columns[4],
                    new WriteValueSource.Scalar(
                        new JsonPathExpression("$.beginDate", [new JsonPathSegment.Property("beginDate")]),
                        new RelationalScalarType(ScalarKind.String, MaxLength: 20)
                    ),
                    "BeginDate"
                ),
                new WriteColumnBinding(
                    tableModel.Columns[5],
                    new WriteValueSource.Scalar(
                        new JsonPathExpression("$.room", [new JsonPathSegment.Property("room")]),
                        new RelationalScalarType(ScalarKind.String, MaxLength: 50)
                    ),
                    "Room"
                ),
            ],
            KeyUnificationPlans: [],
            CollectionMergePlan: new CollectionMergePlan(
                SemanticIdentityBindings:
                [
                    new CollectionMergeSemanticIdentityBinding(
                        new JsonPathExpression("$.beginDate", [new JsonPathSegment.Property("beginDate")]),
                        BindingIndex: 4
                    ),
                ],
                StableRowIdentityBindingIndex: 0,
                UpdateByStableRowIdentitySql: "update edfi.\"SchoolAddressPeriod\" set \"Ordinal\" = @Ordinal, \"BeginDate\" = @BeginDate, \"Room\" = @Room where \"CollectionItemId\" = @CollectionItemId",
                DeleteByStableRowIdentitySql: "delete from edfi.\"SchoolAddressPeriod\" where \"CollectionItemId\" = @CollectionItemId",
                OrdinalBindingIndex: 3,
                CompareBindingIndexesInOrder: [3, 4, 5]
            ),
            CollectionKeyPreallocationPlan: new CollectionKeyPreallocationPlan(
                new DbColumnName("CollectionItemId"),
                0
            )
        );
    }

    private static TableWritePlan CreateSchedulePlan()
    {
        var tableModel = new DbTableModel(
            new DbTableName(new DbSchemaName("edfi"), "SchoolSchedule"),
            new JsonPathExpression(
                "$.schedules[*]",
                [new JsonPathSegment.Property("schedules"), new JsonPathSegment.AnyArrayElement()]
            ),
            new TableKey(
                "PK_SchoolSchedule",
                [new DbKeyColumn(new DbColumnName("CollectionItemId"), ColumnKind.CollectionKey)]
            ),
            [
                new DbColumnModel(
                    new DbColumnName("CollectionItemId"),
                    ColumnKind.CollectionKey,
                    new RelationalScalarType(ScalarKind.Int64),
                    false,
                    null,
                    null,
                    new ColumnStorage.Stored()
                ),
                new DbColumnModel(
                    new DbColumnName("School_DocumentId"),
                    ColumnKind.ParentKeyPart,
                    new RelationalScalarType(ScalarKind.Int64),
                    false,
                    null,
                    null,
                    new ColumnStorage.Stored()
                ),
                new DbColumnModel(
                    new DbColumnName("Ordinal"),
                    ColumnKind.Ordinal,
                    new RelationalScalarType(ScalarKind.Int32),
                    false,
                    null,
                    null,
                    new ColumnStorage.Stored()
                ),
                new DbColumnModel(
                    new DbColumnName("SessionDate"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Date),
                    false,
                    new JsonPathExpression("$.sessionDate", [new JsonPathSegment.Property("sessionDate")]),
                    null,
                    new ColumnStorage.Stored()
                ),
                new DbColumnModel(
                    new DbColumnName("StartTime"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Time),
                    false,
                    new JsonPathExpression("$.startTime", [new JsonPathSegment.Property("startTime")]),
                    null,
                    new ColumnStorage.Stored()
                ),
                new DbColumnModel(
                    new DbColumnName("Room"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 75),
                    false,
                    new JsonPathExpression("$.room", [new JsonPathSegment.Property("room")]),
                    null,
                    new ColumnStorage.Stored()
                ),
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
                        new JsonPathExpression(
                            "$.sessionDate",
                            [new JsonPathSegment.Property("sessionDate")]
                        ),
                        new DbColumnName("SessionDate")
                    ),
                    new CollectionSemanticIdentityBinding(
                        new JsonPathExpression("$.startTime", [new JsonPathSegment.Property("startTime")]),
                        new DbColumnName("StartTime")
                    ),
                ]
            ),
        };

        return new TableWritePlan(
            tableModel,
            InsertSql: "insert into edfi.\"SchoolSchedule\" values (@CollectionItemId, @School_DocumentId, @Ordinal, @SessionDate, @StartTime, @Room)",
            UpdateSql: null,
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(100, 6, 1000),
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
                new WriteColumnBinding(tableModel.Columns[2], new WriteValueSource.Ordinal(), "Ordinal"),
                new WriteColumnBinding(
                    tableModel.Columns[3],
                    new WriteValueSource.Scalar(
                        new JsonPathExpression(
                            "$.sessionDate",
                            [new JsonPathSegment.Property("sessionDate")]
                        ),
                        new RelationalScalarType(ScalarKind.Date)
                    ),
                    "SessionDate"
                ),
                new WriteColumnBinding(
                    tableModel.Columns[4],
                    new WriteValueSource.Scalar(
                        new JsonPathExpression("$.startTime", [new JsonPathSegment.Property("startTime")]),
                        new RelationalScalarType(ScalarKind.Time)
                    ),
                    "StartTime"
                ),
                new WriteColumnBinding(
                    tableModel.Columns[5],
                    new WriteValueSource.Scalar(
                        new JsonPathExpression("$.room", [new JsonPathSegment.Property("room")]),
                        new RelationalScalarType(ScalarKind.String, MaxLength: 75)
                    ),
                    "Room"
                ),
            ],
            KeyUnificationPlans: [],
            CollectionMergePlan: new CollectionMergePlan(
                SemanticIdentityBindings:
                [
                    new CollectionMergeSemanticIdentityBinding(
                        new JsonPathExpression(
                            "$.sessionDate",
                            [new JsonPathSegment.Property("sessionDate")]
                        ),
                        BindingIndex: 3
                    ),
                    new CollectionMergeSemanticIdentityBinding(
                        new JsonPathExpression("$.startTime", [new JsonPathSegment.Property("startTime")]),
                        BindingIndex: 4
                    ),
                ],
                StableRowIdentityBindingIndex: 0,
                UpdateByStableRowIdentitySql: "update edfi.\"SchoolSchedule\" set \"Ordinal\" = @Ordinal, \"SessionDate\" = @SessionDate, \"StartTime\" = @StartTime, \"Room\" = @Room where \"CollectionItemId\" = @CollectionItemId",
                DeleteByStableRowIdentitySql: "delete from edfi.\"SchoolSchedule\" where \"CollectionItemId\" = @CollectionItemId",
                OrdinalBindingIndex: 2,
                CompareBindingIndexesInOrder: [2, 3, 4, 5]
            ),
            CollectionKeyPreallocationPlan: new CollectionKeyPreallocationPlan(
                new DbColumnName("CollectionItemId"),
                0
            )
        );
    }

    private static FlattenedWriteValue Literal(object? value) => new FlattenedWriteValue.Literal(value);

    private static object? LiteralValue(FlattenedWriteValue value) =>
        value is FlattenedWriteValue.Literal literalValue
            ? literalValue.Value
            : throw new AssertionException($"Expected a literal value but found '{value.GetType().Name}'.");

    private sealed record WritePlanFixture(
        ResourceWritePlan WritePlan,
        TableWritePlan RootPlan,
        TableWritePlan RootExtensionPlan,
        TableWritePlan AddressPlan,
        TableWritePlan PeriodPlan
    );

    private sealed record DateAndTimeWritePlanFixture(
        ResourceWritePlan WritePlan,
        TableWritePlan RootPlan,
        TableWritePlan SchedulePlan
    );
}

// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

/// <summary>
/// Builds a Session-shaped root whose identity is (SessionName, School_SchoolId): one scalar member and
/// one member derived from the School reference, whose School_DocumentId is an outgoing reference column
/// that is not part of the identity projection. Runs the identity guard and the DML planner the way the
/// executor does, so the emitted root update reflects the identity state the guard established.
/// </summary>
internal static class RelationalWriteRootUpdateShapeTestSupport
{
    public const long DocumentId = 345L;

    public static readonly SessionRow Stored = new(
        "Fall Semester",
        901L,
        255901,
        "2026-08-01",
        "2026-12-15",
        80
    );

    public sealed record SessionRow(
        string SessionName,
        long SchoolDocumentId,
        int SchoolId,
        string BeginDate,
        string EndDate,
        int TotalInstructionalDays
    );

    public static TableWritePlan CreateSessionPlan(bool withNonIdentityColumns = true)
    {
        var stringType = new RelationalScalarType(ScalarKind.String, MaxLength: 60);
        var int32Type = new RelationalScalarType(ScalarKind.Int32);
        var int64Type = new RelationalScalarType(ScalarKind.Int64);
        var dateType = new RelationalScalarType(ScalarKind.Date);

        List<DbColumnModel> columns =
        [
            Column("DocumentId", ColumnKind.ParentKeyPart, int64Type),
            Column(
                "SessionName",
                ColumnKind.Scalar,
                stringType,
                "$.sessionName",
                usesSqlServerIdentityCollation: true
            ),
            Column("School_SchoolId", ColumnKind.Scalar, int32Type, "$.schoolReference.schoolId"),
        ];

        if (withNonIdentityColumns)
        {
            columns.Add(Column("School_DocumentId", ColumnKind.DocumentFk, int64Type, "$.schoolReference"));
            columns.Add(Column("BeginDate", ColumnKind.Scalar, dateType, "$.beginDate"));
            columns.Add(Column("EndDate", ColumnKind.Scalar, dateType, "$.endDate"));
            columns.Add(
                Column("TotalInstructionalDays", ColumnKind.Scalar, int32Type, "$.totalInstructionalDays")
            );
        }

        var tableModel = new DbTableModel(
            new DbTableName(new DbSchemaName("edfi"), "Session"),
            new JsonPathExpression("$", []),
            new TableKey(
                "PK_Session",
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

        var schoolReferencePath = new JsonPathExpression("$.schoolReference", []);
        List<WriteColumnBinding> bindings =
        [
            new(tableModel.Columns[0], new WriteValueSource.DocumentId(), "DocumentId"),
            new(
                tableModel.Columns[1],
                new WriteValueSource.Scalar(new JsonPathExpression("$.sessionName", []), stringType),
                "SessionName"
            ),
            new(
                tableModel.Columns[2],
                new WriteValueSource.ReferenceDerived(
                    new ReferenceDerivedValueSourceMetadata(
                        BindingIndex: 0,
                        ReferenceObjectPath: schoolReferencePath,
                        IdentityJsonPath: new JsonPathExpression("$.schoolId", []),
                        ReferenceJsonPath: new JsonPathExpression("$.schoolReference.schoolId", [])
                    )
                ),
                "School_SchoolId"
            ),
        ];

        if (withNonIdentityColumns)
        {
            bindings.Add(
                new(tableModel.Columns[3], new WriteValueSource.DocumentReference(0), "School_DocumentId")
            );
            bindings.Add(
                new(
                    tableModel.Columns[4],
                    new WriteValueSource.Scalar(new JsonPathExpression("$.beginDate", []), dateType),
                    "BeginDate"
                )
            );
            bindings.Add(
                new(
                    tableModel.Columns[5],
                    new WriteValueSource.Scalar(new JsonPathExpression("$.endDate", []), dateType),
                    "EndDate"
                )
            );
            bindings.Add(
                new(
                    tableModel.Columns[6],
                    new WriteValueSource.Scalar(
                        new JsonPathExpression("$.totalInstructionalDays", []),
                        int32Type
                    ),
                    "TotalInstructionalDays"
                )
            );
        }

        var setList = string.Join(
            ", ",
            bindings
                .Skip(1)
                .Select(binding => $"\"{binding.Column.ColumnName.Value}\" = @{binding.ParameterName}")
        );

        return new TableWritePlan(
            tableModel,
            InsertSql: "insert into edfi.\"Session\" values (...)",
            UpdateSql: $"update edfi.\"Session\" set {setList} where \"DocumentId\" = @DocumentId",
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(100, bindings.Count, 1000),
            ColumnBindings: bindings,
            KeyUnificationPlans: []
        );
    }

    public static RelationalWriteExecutorRequest CreateRequest(
        TableWritePlan rootPlan,
        SqlDialect dialect,
        bool allowIdentityUpdates
    )
    {
        var resourceModel = Given_Default_Relational_Write_Executor.CreateRelationalResourceModel(
            rootPlan.TableModel,
            new QualifiedResourceName("Ed-Fi", "Session")
        );
        var baseMappingSet = Given_Default_Relational_Write_Executor.CreateMappingSet(
            resourceModel,
            [rootPlan],
            dialect
        );
        var mappingSet = baseMappingSet with
        {
            Model = baseMappingSet.Model with
            {
                TriggersInCreateOrder =
                [
                    new DbTriggerInfo(
                        new DbTriggerName("TR_Session_DocumentStamping"),
                        rootPlan.TableModel.Table,
                        [new DbColumnName("DocumentId")],
                        [new DbColumnName("SessionName"), new DbColumnName("School_SchoolId")],
                        new TriggerKindParameters.DocumentStamping()
                    ),
                ],
            },
        };
        var writePlan = new ResourceWritePlan(resourceModel, [rootPlan]);
        var documentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));

        return new RelationalWriteExecutorRequest(
            mappingSet,
            RelationalWriteOperationKind.Put,
            new RelationalWriteTargetRequest.Put(documentUuid),
            writePlan,
            existingDocumentReadPlan: null,
            JsonNode.Parse("""{"sessionName":"Fall Semester"}""")!,
            allowIdentityUpdates,
            new TraceId("root-update-shape-test"),
            new ReferenceResolverRequest(mappingSet, writePlan.Model.Resource, [], DescriptorReferences: []),
            new RelationalWriteTargetContext.ExistingDocument(DocumentId, documentUuid, 44L)
        );
    }

    /// <summary>
    /// Applies the identity guard to a stored/merged pair and plans the DML, returning the root update
    /// statement, or null when the plan owes none.
    /// </summary>
    public static RelationalWriteDmlStatement? PlanRootUpdate(
        TableWritePlan rootPlan,
        SqlDialect dialect,
        SessionRow current,
        SessionRow merged,
        bool allowIdentityUpdates = false
    )
    {
        var request = CreateRequest(rootPlan, dialect, allowIdentityUpdates);
        var mergeResult = new RelationalWriteMergeResult(
            [
                new RelationalWriteMergedTableState(
                    rootPlan,
                    [CreateRow(rootPlan, current)],
                    [CreateRow(rootPlan, merged)]
                ),
            ],
            supportsGuardedNoOp: true
        );

        var (guardedMergeResult, failure) = RelationalWriteIdentityStability.Apply(request, mergeResult);

        failure.Should().BeNull();

        var plan = RelationalWriteDmlStatementPlanner.Plan(
            dialect,
            guardedMergeResult,
            RelationalWriteCollectionItemIdBindings.Create(dialect, guardedMergeResult)
        );

        return plan.StatementsInOrder.SingleOrDefault(statement =>
            statement.Label.StartsWith("update-scope:", StringComparison.Ordinal)
        );
    }

    /// <summary>
    /// The columns assigned by every UPDATE in <paramref name="sql"/>, in order: each quoted column
    /// assigned a parameter between a <c>SET</c> keyword and the <c>WHERE</c> that follows it.
    /// </summary>
    public static IReadOnlyList<string> AssignedColumns(string sql) =>
        Regex
            .Matches(
                sql,
                @"\bSET\b(?<assignments>.*?)\bWHERE\b",
                RegexOptions.IgnoreCase | RegexOptions.Singleline
            )
            .SelectMany(setList =>
                Regex
                    .Matches(setList.Groups["assignments"].Value, @"[""\[](?<column>[^""\]]+)[""\]]\s*=\s*@")
                    .Select(match => match.Groups["column"].Value)
            )
            .ToArray();

    private static RelationalWriteMergedTableRow CreateRow(TableWritePlan rootPlan, SessionRow row)
    {
        List<FlattenedWriteValue> values =
        [
            new FlattenedWriteValue.Literal(DocumentId),
            new FlattenedWriteValue.Literal(row.SessionName),
            new FlattenedWriteValue.Literal(row.SchoolId),
        ];

        if (rootPlan.ColumnBindings.Length > values.Count)
        {
            values.Add(new FlattenedWriteValue.Literal(row.SchoolDocumentId));
            values.Add(new FlattenedWriteValue.Literal(row.BeginDate));
            values.Add(new FlattenedWriteValue.Literal(row.EndDate));
            values.Add(new FlattenedWriteValue.Literal(row.TotalInstructionalDays));
        }

        return RelationalWriteRowHelpers.CreateMergedTableRow(rootPlan, values);
    }

    private static DbColumnModel Column(
        string name,
        ColumnKind kind,
        RelationalScalarType type,
        string? path = null,
        bool usesSqlServerIdentityCollation = false
    ) =>
        new(
            new DbColumnName(name),
            kind,
            type,
            false,
            path is null ? null : new JsonPathExpression(path, []),
            null,
            new ColumnStorage.Stored(),
            UsesSqlServerIdentityCollation: usesSqlServerIdentityCollation
        );
}

[TestFixture(SqlDialect.Pgsql)]
[TestFixture(SqlDialect.Mssql)]
[Parallelizable]
public class Given_A_Root_Update_Whose_Identity_Is_Unchanged(SqlDialect dialect)
{
    private static readonly string[] _reducedSetList =
    [
        "School_DocumentId",
        "BeginDate",
        "EndDate",
        "TotalInstructionalDays",
    ];

    private TableWritePlan _rootPlan = null!;
    private RelationalWriteDmlStatement _statement = null!;

    [SetUp]
    public void Setup()
    {
        _rootPlan = RelationalWriteRootUpdateShapeTestSupport.CreateSessionPlan();
        _statement = RelationalWriteRootUpdateShapeTestSupport.PlanRootUpdate(
            _rootPlan,
            dialect,
            RelationalWriteRootUpdateShapeTestSupport.Stored,
            RelationalWriteRootUpdateShapeTestSupport.Stored with
            {
                EndDate = "2026-12-18",
            }
        )!;
    }

    [Test]
    public void It_omits_the_scalar_and_reference_derived_identity_columns_and_assigns_every_other_non_key_column()
    {
        _statement.Should().NotBeNull();
        RelationalWriteRootUpdateShapeTestSupport
            .AssignedColumns(_statement.SingleRowSql)
            .Should()
            .Equal(_reducedSetList);
    }

    [Test]
    public void It_binds_the_unsuffixed_compiled_parameter_names_in_the_single_row_statement()
    {
        _statement.SingleRowSql.Should().Contain("= @EndDate").And.NotContain("@EndDate_0");
        _statement.SingleRowSql.Should().Contain("= @DocumentId)");
    }

    [Test]
    public void It_emits_the_same_text_whichever_non_key_values_changed()
    {
        var otherStatement = RelationalWriteRootUpdateShapeTestSupport.PlanRootUpdate(
            _rootPlan,
            dialect,
            RelationalWriteRootUpdateShapeTestSupport.Stored,
            RelationalWriteRootUpdateShapeTestSupport.Stored with
            {
                BeginDate = "2026-08-03",
                TotalInstructionalDays = 78,
            }
        )!;

        otherStatement.SingleRowSql.Should().BeSameAs(_statement.SingleRowSql);
    }
}

[TestFixture(SqlDialect.Pgsql)]
[TestFixture(SqlDialect.Mssql)]
[Parallelizable]
public class Given_A_Root_Update_Whose_Identity_Changes_With_Identity_Updates_Allowed(SqlDialect dialect)
{
    private static readonly string[] _fullSetList =
    [
        "SessionName",
        "School_SchoolId",
        "School_DocumentId",
        "BeginDate",
        "EndDate",
        "TotalInstructionalDays",
    ];

    [Test]
    public void It_uses_the_compiled_full_update_when_the_scalar_identity_member_changes()
    {
        var rootPlan = RelationalWriteRootUpdateShapeTestSupport.CreateSessionPlan();

        var statement = RelationalWriteRootUpdateShapeTestSupport.PlanRootUpdate(
            rootPlan,
            dialect,
            RelationalWriteRootUpdateShapeTestSupport.Stored,
            RelationalWriteRootUpdateShapeTestSupport.Stored with
            {
                SessionName = "Spring Semester",
            },
            allowIdentityUpdates: true
        )!;

        statement.SingleRowSql.Should().Be(rootPlan.UpdateSql);
        RelationalWriteRootUpdateShapeTestSupport
            .AssignedColumns(statement.EmitBatchSql(1))
            .Should()
            .Equal(_fullSetList);
    }

    [Test]
    public void It_uses_the_compiled_full_update_when_the_reference_derived_identity_member_changes()
    {
        var rootPlan = RelationalWriteRootUpdateShapeTestSupport.CreateSessionPlan();

        var statement = RelationalWriteRootUpdateShapeTestSupport.PlanRootUpdate(
            rootPlan,
            dialect,
            RelationalWriteRootUpdateShapeTestSupport.Stored,
            RelationalWriteRootUpdateShapeTestSupport.Stored with
            {
                SchoolDocumentId = 902L,
                SchoolId = 255902,
            },
            allowIdentityUpdates: true
        )!;

        statement.SingleRowSql.Should().Be(rootPlan.UpdateSql);
        RelationalWriteRootUpdateShapeTestSupport
            .AssignedColumns(statement.EmitBatchSql(1))
            .Should()
            .Equal(_fullSetList);
    }

    [Test]
    public void It_keeps_the_full_shape_when_only_non_key_values_change_alongside_an_identity_change()
    {
        var rootPlan = RelationalWriteRootUpdateShapeTestSupport.CreateSessionPlan();

        var statement = RelationalWriteRootUpdateShapeTestSupport.PlanRootUpdate(
            rootPlan,
            dialect,
            RelationalWriteRootUpdateShapeTestSupport.Stored,
            RelationalWriteRootUpdateShapeTestSupport.Stored with
            {
                SessionName = "Spring Semester",
                EndDate = "2026-12-18",
            },
            allowIdentityUpdates: true
        )!;

        statement.SingleRowSql.Should().Be(rootPlan.UpdateSql);
    }
}

/// <summary>
/// A SQL Server identity value that differs from the stored one only under the identity collation is
/// rebound to the stored value, so it is not an identity change and the reduced shape applies. A
/// genuine change on another identity member still selects the full shape.
/// </summary>
[TestFixture]
[Parallelizable]
public class Given_A_Root_Update_Whose_Identity_Differs_Only_In_Case
{
    [Test]
    public void It_omits_the_identity_columns_when_the_case_only_difference_is_the_sole_identity_difference()
    {
        var statement = RelationalWriteRootUpdateShapeTestSupport.PlanRootUpdate(
            RelationalWriteRootUpdateShapeTestSupport.CreateSessionPlan(),
            SqlDialect.Mssql,
            RelationalWriteRootUpdateShapeTestSupport.Stored,
            RelationalWriteRootUpdateShapeTestSupport.Stored with
            {
                SessionName = "FALL SEMESTER",
                EndDate = "2026-12-18",
            }
        )!;

        RelationalWriteRootUpdateShapeTestSupport
            .AssignedColumns(statement.SingleRowSql)
            .Should()
            .NotContain(["SessionName", "School_SchoolId"])
            .And.Contain("EndDate");
    }

    [Test]
    public void It_uses_the_full_shape_when_a_genuine_identity_change_accompanies_the_case_only_difference()
    {
        var rootPlan = RelationalWriteRootUpdateShapeTestSupport.CreateSessionPlan();

        var statement = RelationalWriteRootUpdateShapeTestSupport.PlanRootUpdate(
            rootPlan,
            SqlDialect.Mssql,
            RelationalWriteRootUpdateShapeTestSupport.Stored,
            RelationalWriteRootUpdateShapeTestSupport.Stored with
            {
                SessionName = "FALL SEMESTER",
                SchoolDocumentId = 902L,
                SchoolId = 255902,
            },
            allowIdentityUpdates: true
        )!;

        statement.SingleRowSql.Should().Be(rootPlan.UpdateSql);
    }

    [Test]
    public void It_plans_no_root_update_when_an_identity_only_root_differs_only_in_case()
    {
        var statement = RelationalWriteRootUpdateShapeTestSupport.PlanRootUpdate(
            RelationalWriteRootUpdateShapeTestSupport.CreateSessionPlan(withNonIdentityColumns: false),
            SqlDialect.Mssql,
            RelationalWriteRootUpdateShapeTestSupport.Stored,
            RelationalWriteRootUpdateShapeTestSupport.Stored with
            {
                SessionName = "FALL SEMESTER",
            }
        );

        statement.Should().BeNull();
    }
}

/// <summary>
/// A root whose only writable non-key columns are its identity columns has nothing left to assign once
/// they are omitted. With the identity unchanged the stored row already matches, so the plan owes no root
/// update rather than an UPDATE with an empty SET list.
/// </summary>
[TestFixture(SqlDialect.Pgsql)]
[TestFixture(SqlDialect.Mssql)]
[Parallelizable]
public class Given_A_Root_Whose_Only_Non_Key_Columns_Are_Its_Unchanged_Identity(SqlDialect dialect)
{
    [Test]
    public void It_plans_no_root_update()
    {
        var statement = RelationalWriteRootUpdateShapeTestSupport.PlanRootUpdate(
            RelationalWriteRootUpdateShapeTestSupport.CreateSessionPlan(withNonIdentityColumns: false),
            dialect,
            RelationalWriteRootUpdateShapeTestSupport.Stored,
            RelationalWriteRootUpdateShapeTestSupport.Stored
        );

        statement.Should().BeNull();
    }
}

/// <summary>
/// Only the identity guard marks bindings as omitted. A merge result that never passed through it keeps the
/// compiled full update, so non-root scopes and directly constructed merge results are unaffected.
/// </summary>
[TestFixture]
[Parallelizable]
public class Given_A_Changed_Root_Row_That_Was_Not_Marked_By_The_Identity_Guard
{
    [Test]
    public void It_uses_the_compiled_full_update()
    {
        var rootPlan = RelationalWriteRootUpdateShapeTestSupport.CreateSessionPlan();
        RelationalWriteMergeResult mergeResult = new(
            [
                new RelationalWriteMergedTableState(
                    rootPlan,
                    [
                        RelationalWriteRowHelpers.CreateMergedTableRow(
                            rootPlan,
                            [
                                new FlattenedWriteValue.Literal(345L),
                                new FlattenedWriteValue.Literal("Fall Semester"),
                                new FlattenedWriteValue.Literal(255901),
                                new FlattenedWriteValue.Literal(901L),
                                new FlattenedWriteValue.Literal("2026-08-01"),
                                new FlattenedWriteValue.Literal("2026-12-15"),
                                new FlattenedWriteValue.Literal(80),
                            ]
                        ),
                    ],
                    [
                        RelationalWriteRowHelpers.CreateMergedTableRow(
                            rootPlan,
                            [
                                new FlattenedWriteValue.Literal(345L),
                                new FlattenedWriteValue.Literal("Fall Semester"),
                                new FlattenedWriteValue.Literal(255901),
                                new FlattenedWriteValue.Literal(901L),
                                new FlattenedWriteValue.Literal("2026-08-01"),
                                new FlattenedWriteValue.Literal("2026-12-18"),
                                new FlattenedWriteValue.Literal(80),
                            ]
                        ),
                    ]
                ),
            ],
            supportsGuardedNoOp: true
        );

        var statement = RelationalWriteDmlStatementPlanner
            .Plan(
                SqlDialect.Pgsql,
                mergeResult,
                RelationalWriteCollectionItemIdBindings.Create(SqlDialect.Pgsql, mergeResult)
            )
            .StatementsInOrder.Single();

        statement.SingleRowSql.Should().Be(rootPlan.UpdateSql);
    }
}

/// <summary>
/// The reduced update is rendered by the emitter that compiles <see cref="TableWritePlan.UpdateSql"/>, once
/// per plan, so the per-request planner only selects between two fixed texts.
/// </summary>
[TestFixture(SqlDialect.Pgsql)]
[TestFixture(SqlDialect.Mssql)]
[Parallelizable]
public class Given_The_Reduced_Root_Update_Emitter(SqlDialect dialect)
{
    private TableWritePlan _rootPlan = null!;
    private WritePlanBatchSqlEmitter _sut = null!;

    [SetUp]
    public void Setup()
    {
        var plan = RelationalWriteRootUpdateShapeTestSupport.CreateSessionPlan();
        var setBindings = plan.ColumnBindings.Skip(1).ToArray();

        _rootPlan = plan with
        {
            UpdateSql = new SimpleUpdateSqlEmitter(dialect).Emit(
                plan.TableModel.Table,
                [.. setBindings.Select(static binding => binding.Column.ColumnName)],
                [.. setBindings.Select(static binding => binding.ParameterName)],
                [new DbColumnName("DocumentId")],
                ["DocumentId"]
            ),
        };
        _sut = new WritePlanBatchSqlEmitter(dialect);
    }

    [Test]
    public void It_reproduces_the_compiled_update_text_when_nothing_is_omitted()
    {
        _sut.EmitUpdate(_rootPlan, []).Should().Be(_rootPlan.UpdateSql);
    }

    [Test]
    public void It_renders_the_text_once_per_plan_and_reuses_it_from_any_emitter()
    {
        var first = _sut.EmitUpdate(_rootPlan, [1, 2]);

        new WritePlanBatchSqlEmitter(dialect).EmitUpdate(_rootPlan, [1, 2]).Should().BeSameAs(first);
        RelationalWriteRootUpdateShapeTestSupport
            .AssignedColumns(first)
            .Should()
            .Equal("School_DocumentId", "BeginDate", "EndDate", "TotalInstructionalDays");
    }

    [Test]
    public void It_renders_a_different_omitted_set_rather_than_reusing_the_cached_text()
    {
        _sut.EmitUpdate(_rootPlan, [1, 2]);

        RelationalWriteRootUpdateShapeTestSupport
            .AssignedColumns(_sut.EmitUpdate(_rootPlan, [1]))
            .Should()
            .Equal("School_SchoolId", "School_DocumentId", "BeginDate", "EndDate", "TotalInstructionalDays");
    }
}

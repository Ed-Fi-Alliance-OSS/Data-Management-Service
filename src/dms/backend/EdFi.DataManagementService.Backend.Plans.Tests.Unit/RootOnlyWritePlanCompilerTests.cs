// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

[TestFixture]
public class Given_RootOnlyWritePlanCompiler
{
    private RelationalResourceModel _supportedRootOnlyModel = null!;

    [SetUp]
    public void Setup()
    {
        _supportedRootOnlyModel = CreateSupportedRootOnlyModel();
    }

    [Test]
    public void It_should_compile_stored_column_bindings_in_model_order_with_deterministic_parameter_names()
    {
        var writePlan = new RootOnlyWritePlanCompiler(SqlDialect.Pgsql).Compile(_supportedRootOnlyModel);
        var tablePlan = writePlan.TablePlansInDependencyOrder.Single();

        tablePlan
            .ColumnBindings.Select(binding => binding.Column.ColumnName.Value)
            .Should()
            .Equal("DocumentId", "SchoolYear", "LocalEducationAgencyId");

        tablePlan
            .ColumnBindings.Select(binding => binding.ParameterName)
            .Should()
            .Equal("documentId", "schoolYear", "localEducationAgencyId");

        tablePlan.ColumnBindings[0].Source.Should().BeOfType<WriteValueSource.DocumentId>();
        tablePlan.ColumnBindings[1].Source.Should().BeOfType<WriteValueSource.Scalar>();
        tablePlan.ColumnBindings[2].Source.Should().BeOfType<WriteValueSource.Scalar>();

        tablePlan.UpdateSql.Should().NotBeNull();
        tablePlan.DeleteByParentSql.Should().BeNull();
        tablePlan.KeyUnificationPlans.Should().BeEmpty();

        tablePlan.BulkInsertBatching.ParametersPerRow.Should().Be(3);
        tablePlan.BulkInsertBatching.MaxRowsPerBatch.Should().Be(1000);
        tablePlan.BulkInsertBatching.MaxParametersPerCommand.Should().Be(65535);
    }

    [Test]
    public void It_should_emit_canonical_pgsql_insert_sql_using_binding_column_and_parameter_order()
    {
        var writePlan = new RootOnlyWritePlanCompiler(SqlDialect.Pgsql).Compile(_supportedRootOnlyModel);
        var tablePlan = writePlan.TablePlansInDependencyOrder.Single();

        tablePlan
            .InsertSql.Should()
            .Be(
                """
                INSERT INTO "edfi"."Student"
                (
                    "DocumentId",
                    "SchoolYear",
                    "LocalEducationAgencyId"
                )
                VALUES
                (
                    @documentId,
                    @schoolYear,
                    @localEducationAgencyId
                )
                ;

                """
            );
    }

    [Test]
    public void It_should_emit_canonical_mssql_insert_sql_using_binding_column_and_parameter_order()
    {
        var writePlan = new RootOnlyWritePlanCompiler(SqlDialect.Mssql).Compile(_supportedRootOnlyModel);
        var tablePlan = writePlan.TablePlansInDependencyOrder.Single();

        tablePlan
            .InsertSql.Should()
            .Be(
                """
                INSERT INTO [edfi].[Student]
                (
                    [DocumentId],
                    [SchoolYear],
                    [LocalEducationAgencyId]
                )
                VALUES
                (
                    @documentId,
                    @schoolYear,
                    @localEducationAgencyId
                )
                ;

                """
            );

        tablePlan.BulkInsertBatching.ParametersPerRow.Should().Be(3);
        tablePlan.BulkInsertBatching.MaxRowsPerBatch.Should().Be(700);
        tablePlan.BulkInsertBatching.MaxParametersPerCommand.Should().Be(2100);
    }

    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public void It_should_emit_insert_sql_from_column_bindings_in_order(SqlDialect dialect)
    {
        var tablePlan = new RootOnlyWritePlanCompiler(dialect)
            .Compile(_supportedRootOnlyModel)
            .TablePlansInDependencyOrder.Single();

        var expectedInsertSql = new SimpleInsertSqlEmitter(dialect).Emit(
            tablePlan.TableModel.Table,
            tablePlan.ColumnBindings.Select(static binding => binding.Column.ColumnName).ToArray(),
            tablePlan.ColumnBindings.Select(static binding => binding.ParameterName).ToArray()
        );

        tablePlan.InsertSql.Should().Be(expectedInsertSql);
    }

    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public void It_should_emit_identical_insert_sql_across_repeated_compilation_and_permuted_non_writable_column_order(
        SqlDialect dialect
    )
    {
        var compiler = new RootOnlyWritePlanCompiler(dialect);

        var firstInsertSql = compiler
            .Compile(_supportedRootOnlyModel)
            .TablePlansInDependencyOrder.Single()
            .InsertSql;

        var secondInsertSql = compiler
            .Compile(_supportedRootOnlyModel)
            .TablePlansInDependencyOrder.Single()
            .InsertSql;

        var permutedInsertSql = compiler
            .Compile(CreateSupportedRootOnlyModelWithUnifiedAliasColumnFirst())
            .TablePlansInDependencyOrder.Single()
            .InsertSql;

        firstInsertSql.Should().Be(secondInsertSql);
        permutedInsertSql.Should().Be(firstInsertSql);
    }

    [Test]
    public void It_should_emit_canonical_pgsql_update_sql_using_non_key_set_columns_and_key_where_columns()
    {
        var writePlan = new RootOnlyWritePlanCompiler(SqlDialect.Pgsql).Compile(_supportedRootOnlyModel);
        var tablePlan = writePlan.TablePlansInDependencyOrder.Single();

        tablePlan
            .UpdateSql.Should()
            .Be(
                """
                UPDATE "edfi"."Student"
                SET
                    "SchoolYear" = @schoolYear,
                    "LocalEducationAgencyId" = @localEducationAgencyId
                WHERE
                    ("DocumentId" = @documentId)
                ;

                """
            );
    }

    [Test]
    public void It_should_emit_canonical_mssql_update_sql_using_non_key_set_columns_and_key_where_columns()
    {
        var writePlan = new RootOnlyWritePlanCompiler(SqlDialect.Mssql).Compile(_supportedRootOnlyModel);
        var tablePlan = writePlan.TablePlansInDependencyOrder.Single();

        tablePlan
            .UpdateSql.Should()
            .Be(
                """
                UPDATE [edfi].[Student]
                SET
                    [SchoolYear] = @schoolYear,
                    [LocalEducationAgencyId] = @localEducationAgencyId
                WHERE
                    ([DocumentId] = @documentId)
                ;

                """
            );
    }

    [Test]
    public void It_should_leave_update_sql_null_when_no_stored_writable_non_key_columns_exist()
    {
        var keyOnlyModel = CreateRootOnlyKeyOnlyModel();

        var writePlan = new RootOnlyWritePlanCompiler(SqlDialect.Pgsql).Compile(keyOnlyModel);
        var tablePlan = writePlan.TablePlansInDependencyOrder.Single();

        tablePlan.UpdateSql.Should().BeNull();
    }

    [Test]
    public void It_should_mark_resources_with_root_key_unification_classes_as_unsupported_for_write_compilation()
    {
        var keyUnificationModel = CreateRootOnlyModelWithKeyUnificationClass();
        var compiler = new RootOnlyWritePlanCompiler(SqlDialect.Pgsql);

        RootOnlyWritePlanCompiler.IsSupported(keyUnificationModel).Should().BeFalse();

        var wasCompiled = compiler.TryCompile(keyUnificationModel, out var writePlan);

        wasCompiled.Should().BeFalse();
        writePlan.Should().BeNull();
    }

    [Test]
    public void It_should_allow_precomputed_stored_non_key_columns_for_write_compilation()
    {
        var precomputedColumnModel = CreateRootOnlyModelWithStoredPrecomputedNonKeyColumn();
        var compiler = new RootOnlyWritePlanCompiler(SqlDialect.Pgsql);

        RootOnlyWritePlanCompiler.IsSupported(precomputedColumnModel).Should().BeTrue();

        var wasCompiled = compiler.TryCompile(precomputedColumnModel, out var writePlan);

        wasCompiled.Should().BeTrue();
        writePlan.Should().NotBeNull();
        writePlan!
            .TablePlansInDependencyOrder.Single()
            .ColumnBindings.Where(binding => binding.Column.ColumnName.Value == "CanonicalSchoolYear")
            .Single()
            .Source.Should()
            .BeOfType<WriteValueSource.Precomputed>();
    }

    [Test]
    public void It_should_allow_mapping_set_loop_to_omit_unsupported_write_plan_and_keep_read_plan_compilation()
    {
        var keyUnificationModel = CreateRootOnlyModelWithKeyUnificationClass();
        var compiler = new RootOnlyWritePlanCompiler(SqlDialect.Pgsql);
        var writePlansByResource = new Dictionary<QualifiedResourceName, ResourceWritePlan>();
        var readPlansByResource = new Dictionary<QualifiedResourceName, ResourceReadPlan>();

        var act = () =>
        {
            readPlansByResource[keyUnificationModel.Resource] = CreateRootOnlyReadPlanStub(
                keyUnificationModel
            );

            if (compiler.TryCompile(keyUnificationModel, out var writePlan))
            {
                writePlansByResource[keyUnificationModel.Resource] = writePlan;
            }
        };

        act.Should().NotThrow();
        writePlansByResource.Should().BeEmpty();
        readPlansByResource.Should().ContainKey(keyUnificationModel.Resource);
    }

    [Test]
    public void It_should_compile_each_supported_write_value_source_for_single_table_bindings()
    {
        var model = CreateSingleTableModelCoveringWriteValueSourceKinds();

        var writePlan = new RootOnlyWritePlanCompiler(SqlDialect.Pgsql).Compile(model);
        var tablePlan = writePlan.TablePlansInDependencyOrder.Single();

        tablePlan
            .ColumnBindings.Select(binding => binding.Column.ColumnName.Value)
            .Should()
            .Equal(
                "DocumentId",
                "ParentAddressOrdinal",
                "Ordinal",
                "AddressScopeValue",
                "StreetNumber",
                "School_DocumentId",
                "ProgramTypeDescriptorId",
                "CanonicalProgramTypeCode"
            );

        tablePlan
            .ColumnBindings.Select(binding => binding.ParameterName)
            .Should()
            .Equal(
                "documentId",
                "parentAddressOrdinal",
                "ordinal",
                "addressScopeValue",
                "streetNumber",
                "school_DocumentId",
                "programTypeDescriptorId",
                "canonicalProgramTypeCode"
            );

        tablePlan.ColumnBindings[0].Source.Should().BeOfType<WriteValueSource.DocumentId>();
        tablePlan
            .ColumnBindings[1]
            .Source.Should()
            .BeEquivalentTo(new WriteValueSource.ParentKeyPart(Index: 1));
        tablePlan.ColumnBindings[2].Source.Should().BeOfType<WriteValueSource.Ordinal>();
        tablePlan
            .ColumnBindings[3]
            .Source.Should()
            .BeEquivalentTo(
                new WriteValueSource.Scalar(
                    RelativePath: new JsonPathExpression("$", []),
                    Type: new RelationalScalarType(ScalarKind.String)
                )
            );
        tablePlan
            .ColumnBindings[4]
            .Source.Should()
            .BeEquivalentTo(
                new WriteValueSource.Scalar(
                    RelativePath: new JsonPathExpression(
                        "$.streetNumber",
                        [new JsonPathSegment.Property("streetNumber")]
                    ),
                    Type: new RelationalScalarType(ScalarKind.String)
                )
            );
        tablePlan
            .ColumnBindings[5]
            .Source.Should()
            .BeEquivalentTo(new WriteValueSource.DocumentReference(BindingIndex: 0));
        tablePlan
            .ColumnBindings[6]
            .Source.Should()
            .BeEquivalentTo(
                new WriteValueSource.DescriptorReference(
                    DescriptorResource: new QualifiedResourceName("Ed-Fi", "ProgramTypeDescriptor"),
                    RelativePath: new JsonPathExpression(
                        "$.programTypeDescriptor",
                        [new JsonPathSegment.Property("programTypeDescriptor")]
                    ),
                    DescriptorValuePath: new JsonPathExpression(
                        "$.addresses[*].programTypeDescriptor",
                        [
                            new JsonPathSegment.Property("addresses"),
                            new JsonPathSegment.AnyArrayElement(),
                            new JsonPathSegment.Property("programTypeDescriptor"),
                        ]
                    )
                )
            );
        tablePlan.ColumnBindings[7].Source.Should().BeOfType<WriteValueSource.Precomputed>();
    }

    [Test]
    public void It_should_fail_fast_when_key_column_is_unified_alias()
    {
        var unsupportedModel = CreateRootOnlyModelWithUnifiedAliasKeyColumn();
        var act = () => new RootOnlyWritePlanCompiler(SqlDialect.Pgsql).Compile(unsupportedModel);

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                "Cannot compile write plan for 'edfi.Student': key column 'SchoolYearAlias' is UnifiedAlias*"
            );
    }

    [Test]
    public void It_should_compile_table_plans_for_all_tables_in_dependency_order_for_multi_table_resources()
    {
        var model = CreateSupportedMultiTableModel();
        var writePlan = new RootOnlyWritePlanCompiler(SqlDialect.Pgsql).Compile(model);

        writePlan.TablePlansInDependencyOrder.Should().HaveCount(model.TablesInDependencyOrder.Count);
        writePlan
            .TablePlansInDependencyOrder.Select(static tablePlan => tablePlan.TableModel.Table)
            .Should()
            .Equal(model.TablesInDependencyOrder.Select(static table => table.Table));

        var rootPlan = writePlan.TablePlansInDependencyOrder[0];
        var rootExtensionPlan = writePlan.TablePlansInDependencyOrder[1];
        var childPlan = writePlan.TablePlansInDependencyOrder[2];

        rootPlan.UpdateSql.Should().NotBeNull();
        rootExtensionPlan.UpdateSql.Should().NotBeNull();
        childPlan.UpdateSql.Should().BeNull();

        foreach (var tablePlan in writePlan.TablePlansInDependencyOrder)
        {
            tablePlan.InsertSql.Should().NotBeNullOrWhiteSpace();
            tablePlan.DeleteByParentSql.Should().BeNull();
            tablePlan.BulkInsertBatching.ParametersPerRow.Should().Be(tablePlan.ColumnBindings.Length);
            tablePlan.KeyUnificationPlans.Should().BeEmpty();
        }
    }

    [Test]
    public void It_should_compile_deterministic_table_plans_for_multi_table_resources_under_unified_alias_column_permutations()
    {
        var compiler = new RootOnlyWritePlanCompiler(SqlDialect.Pgsql);

        var first = compiler.Compile(CreateSupportedMultiTableModel());
        var second = compiler.Compile(CreateSupportedMultiTableModel());
        var permuted = compiler.Compile(CreateSupportedMultiTableModelWithUnifiedAliasColumnsFirst());

        var firstFingerprint = CreateWritePlanFingerprint(first);
        var secondFingerprint = CreateWritePlanFingerprint(second);
        var permutedFingerprint = CreateWritePlanFingerprint(permuted);

        secondFingerprint.Should().Be(firstFingerprint);
        permutedFingerprint.Should().Be(firstFingerprint);
    }

    [Test]
    public void It_should_keep_try_compile_limited_to_thin_slice_root_only_resources()
    {
        var multiTableModel = CreateSupportedMultiTableModel();
        var compiler = new RootOnlyWritePlanCompiler(SqlDialect.Pgsql);

        RootOnlyWritePlanCompiler.IsSupported(multiTableModel).Should().BeFalse();

        var wasCompiled = compiler.TryCompile(multiTableModel, out var writePlan);

        wasCompiled.Should().BeFalse();
        writePlan.Should().BeNull();
    }

    [Test]
    public void It_should_fail_fast_for_non_relational_storage_resources()
    {
        var unsupportedModel = _supportedRootOnlyModel with
        {
            StorageKind = ResourceStorageKind.SharedDescriptorTable,
        };

        var act = () => new RootOnlyWritePlanCompiler(SqlDialect.Pgsql).Compile(unsupportedModel);

        act.Should()
            .Throw<NotSupportedException>()
            .WithMessage("Only relational-table resources are supported for write-plan compilation.*");
    }

    [Test]
    public void It_should_fail_fast_when_root_table_has_key_unification_classes()
    {
        var unsupportedModel = CreateRootOnlyModelWithKeyUnificationClass();
        var act = () => new RootOnlyWritePlanCompiler(SqlDialect.Pgsql).Compile(unsupportedModel);

        act.Should()
            .Throw<NotSupportedException>()
            .WithMessage("Write-plan compilation for key-unification tables is not implemented yet.*");
    }

    private static RelationalResourceModel CreateSupportedRootOnlyModel()
    {
        var rootTable = new DbTableModel(
            Table: new DbTableName(new DbSchemaName("edfi"), "Student"),
            JsonScope: new JsonPathExpression("$", []),
            Key: new TableKey(
                ConstraintName: "PK_Student",
                Columns: [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            Columns:
            [
                new DbColumnModel(
                    ColumnName: new DbColumnName("DocumentId"),
                    Kind: ColumnKind.ParentKeyPart,
                    ScalarType: new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("SchoolYear"),
                    Kind: ColumnKind.Scalar,
                    ScalarType: new RelationalScalarType(ScalarKind.Int32),
                    IsNullable: false,
                    SourceJsonPath: new JsonPathExpression(
                        "$.schoolYear",
                        [new JsonPathSegment.Property("schoolYear")]
                    ),
                    TargetResource: null
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("LocalEducationAgencyId"),
                    Kind: ColumnKind.Scalar,
                    ScalarType: new RelationalScalarType(ScalarKind.Int32),
                    IsNullable: false,
                    SourceJsonPath: new JsonPathExpression(
                        "$.localEducationAgencyId",
                        [new JsonPathSegment.Property("localEducationAgencyId")]
                    ),
                    TargetResource: null
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("SchoolYearAlias"),
                    Kind: ColumnKind.Scalar,
                    ScalarType: new RelationalScalarType(ScalarKind.Int32),
                    IsNullable: false,
                    SourceJsonPath: new JsonPathExpression(
                        "$.schoolYear",
                        [new JsonPathSegment.Property("schoolYear")]
                    ),
                    TargetResource: null,
                    Storage: new ColumnStorage.UnifiedAlias(
                        CanonicalColumn: new DbColumnName("SchoolYear"),
                        PresenceColumn: null
                    )
                ),
            ],
            Constraints: []
        );

        return new RelationalResourceModel(
            Resource: new QualifiedResourceName("Ed-Fi", "Student"),
            PhysicalSchema: new DbSchemaName("edfi"),
            StorageKind: ResourceStorageKind.RelationalTables,
            Root: rootTable,
            TablesInDependencyOrder: [rootTable],
            DocumentReferenceBindings: [],
            DescriptorEdgeSources: []
        );
    }

    private static RelationalResourceModel CreateRootOnlyKeyOnlyModel()
    {
        var rootTable = new DbTableModel(
            Table: new DbTableName(new DbSchemaName("edfi"), "Student"),
            JsonScope: new JsonPathExpression("$", []),
            Key: new TableKey(
                ConstraintName: "PK_Student",
                Columns: [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            Columns:
            [
                new DbColumnModel(
                    ColumnName: new DbColumnName("DocumentId"),
                    Kind: ColumnKind.ParentKeyPart,
                    ScalarType: new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
            ],
            Constraints: []
        );

        return new RelationalResourceModel(
            Resource: new QualifiedResourceName("Ed-Fi", "Student"),
            PhysicalSchema: new DbSchemaName("edfi"),
            StorageKind: ResourceStorageKind.RelationalTables,
            Root: rootTable,
            TablesInDependencyOrder: [rootTable],
            DocumentReferenceBindings: [],
            DescriptorEdgeSources: []
        );
    }

    private static RelationalResourceModel CreateRootOnlyModelWithKeyUnificationClass()
    {
        var model = CreateSupportedRootOnlyModel();
        var rootTable = model.Root with
        {
            KeyUnificationClasses =
            [
                new KeyUnificationClass(
                    CanonicalColumn: new DbColumnName("SchoolYear"),
                    MemberPathColumns: [new DbColumnName("SchoolYear"), new DbColumnName("SchoolYearAlias")]
                ),
            ],
        };

        return model with
        {
            Root = rootTable,
            TablesInDependencyOrder = [rootTable],
        };
    }

    private static RelationalResourceModel CreateRootOnlyModelWithStoredPrecomputedNonKeyColumn()
    {
        var model = CreateSupportedRootOnlyModel();
        var rootTable = model.Root with
        {
            Columns =
            [
                .. model.Root.Columns,
                new DbColumnModel(
                    ColumnName: new DbColumnName("CanonicalSchoolYear"),
                    Kind: ColumnKind.Scalar,
                    ScalarType: new RelationalScalarType(ScalarKind.Int32),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
            ],
        };

        return model with
        {
            Root = rootTable,
            TablesInDependencyOrder = [rootTable],
        };
    }

    private static RelationalResourceModel CreateRootOnlyModelWithUnifiedAliasKeyColumn()
    {
        var model = CreateSupportedRootOnlyModel();
        var rootTable = model.Root with
        {
            Key = new TableKey(
                ConstraintName: "PK_Student",
                Columns:
                [
                    new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart),
                    new DbKeyColumn(new DbColumnName("SchoolYearAlias"), ColumnKind.Scalar),
                ]
            ),
        };

        return model with
        {
            Root = rootTable,
            TablesInDependencyOrder = [rootTable],
        };
    }

    private static RelationalResourceModel CreateSupportedRootOnlyModelWithUnifiedAliasColumnFirst()
    {
        var model = CreateSupportedRootOnlyModel();

        var unifiedAliasColumns = model
            .Root.Columns.Where(static column => column.Storage is ColumnStorage.UnifiedAlias)
            .ToArray();

        var storedColumns = model
            .Root.Columns.Where(static column => column.Storage is ColumnStorage.Stored)
            .ToArray();

        var rootTable = model.Root with { Columns = [.. unifiedAliasColumns, .. storedColumns] };

        return model with
        {
            Root = rootTable,
            TablesInDependencyOrder = [rootTable],
        };
    }

    private static RelationalResourceModel CreateSupportedMultiTableModel()
    {
        var model = CreateSupportedRootOnlyModel();
        var rootTable = model.Root;
        var rootScopeExtensionTable = CreateRootScopeExtensionTable();
        var childCollectionTable = CreateChildCollectionTable();

        return model with
        {
            Root = rootTable,
            TablesInDependencyOrder = [rootTable, rootScopeExtensionTable, childCollectionTable],
        };
    }

    private static RelationalResourceModel CreateSupportedMultiTableModelWithUnifiedAliasColumnsFirst()
    {
        var model = CreateSupportedMultiTableModel();
        var permutedTables = model.TablesInDependencyOrder.Select(ReorderUnifiedAliasColumnsFirst).ToArray();

        return model with
        {
            Root = permutedTables[0],
            TablesInDependencyOrder = permutedTables,
        };
    }

    private static DbTableModel ReorderUnifiedAliasColumnsFirst(DbTableModel tableModel)
    {
        var unifiedAliasColumns = tableModel
            .Columns.Where(static column => column.Storage is ColumnStorage.UnifiedAlias)
            .ToArray();

        if (unifiedAliasColumns.Length == 0)
        {
            return tableModel;
        }

        var storedColumns = tableModel
            .Columns.Where(static column => column.Storage is ColumnStorage.Stored)
            .ToArray();

        return tableModel with
        {
            Columns = [.. unifiedAliasColumns, .. storedColumns],
        };
    }

    private static DbTableModel CreateRootScopeExtensionTable()
    {
        return new DbTableModel(
            Table: new DbTableName(new DbSchemaName("sample"), "StudentExtension"),
            JsonScope: CreatePath(
                "$._ext.sample",
                new JsonPathSegment.Property("_ext"),
                new JsonPathSegment.Property("sample")
            ),
            Key: new TableKey(
                ConstraintName: "PK_StudentExtension",
                Columns: [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            Columns:
            [
                new DbColumnModel(
                    ColumnName: new DbColumnName("DocumentId"),
                    Kind: ColumnKind.ParentKeyPart,
                    ScalarType: new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("FavoriteColor"),
                    Kind: ColumnKind.Scalar,
                    ScalarType: new RelationalScalarType(ScalarKind.String),
                    IsNullable: true,
                    SourceJsonPath: CreatePath(
                        "$._ext.sample.favoriteColor",
                        new JsonPathSegment.Property("_ext"),
                        new JsonPathSegment.Property("sample"),
                        new JsonPathSegment.Property("favoriteColor")
                    ),
                    TargetResource: null
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("FavoriteColorAlias"),
                    Kind: ColumnKind.Scalar,
                    ScalarType: new RelationalScalarType(ScalarKind.String),
                    IsNullable: true,
                    SourceJsonPath: CreatePath(
                        "$._ext.sample.favoriteColor",
                        new JsonPathSegment.Property("_ext"),
                        new JsonPathSegment.Property("sample"),
                        new JsonPathSegment.Property("favoriteColor")
                    ),
                    TargetResource: null,
                    Storage: new ColumnStorage.UnifiedAlias(
                        CanonicalColumn: new DbColumnName("FavoriteColor"),
                        PresenceColumn: null
                    )
                ),
            ],
            Constraints: []
        );
    }

    private static DbTableModel CreateChildCollectionTable()
    {
        return new DbTableModel(
            Table: new DbTableName(new DbSchemaName("edfi"), "StudentAddress"),
            JsonScope: CreatePath(
                "$.addresses[*]",
                new JsonPathSegment.Property("addresses"),
                new JsonPathSegment.AnyArrayElement()
            ),
            Key: new TableKey(
                ConstraintName: "PK_StudentAddress",
                Columns:
                [
                    new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart),
                    new DbKeyColumn(new DbColumnName("Ordinal"), ColumnKind.Ordinal),
                ]
            ),
            Columns:
            [
                new DbColumnModel(
                    ColumnName: new DbColumnName("DocumentId"),
                    Kind: ColumnKind.ParentKeyPart,
                    ScalarType: new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("Ordinal"),
                    Kind: ColumnKind.Ordinal,
                    ScalarType: new RelationalScalarType(ScalarKind.Int32),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("City"),
                    Kind: ColumnKind.Scalar,
                    ScalarType: new RelationalScalarType(ScalarKind.String),
                    IsNullable: true,
                    SourceJsonPath: CreatePath(
                        "$.addresses[*].city",
                        new JsonPathSegment.Property("addresses"),
                        new JsonPathSegment.AnyArrayElement(),
                        new JsonPathSegment.Property("city")
                    ),
                    TargetResource: null
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("CityAlias"),
                    Kind: ColumnKind.Scalar,
                    ScalarType: new RelationalScalarType(ScalarKind.String),
                    IsNullable: true,
                    SourceJsonPath: CreatePath(
                        "$.addresses[*].city",
                        new JsonPathSegment.Property("addresses"),
                        new JsonPathSegment.AnyArrayElement(),
                        new JsonPathSegment.Property("city")
                    ),
                    TargetResource: null,
                    Storage: new ColumnStorage.UnifiedAlias(
                        CanonicalColumn: new DbColumnName("City"),
                        PresenceColumn: null
                    )
                ),
            ],
            Constraints: []
        );
    }

    private static RelationalResourceModel CreateSingleTableModelCoveringWriteValueSourceKinds()
    {
        var table = new DbTableModel(
            Table: new DbTableName(new DbSchemaName("edfi"), "StudentAddress"),
            JsonScope: CreatePath(
                "$.addresses[*]",
                new JsonPathSegment.Property("addresses"),
                new JsonPathSegment.AnyArrayElement()
            ),
            Key: new TableKey(
                ConstraintName: "PK_StudentAddress",
                Columns:
                [
                    new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart),
                    new DbKeyColumn(new DbColumnName("ParentAddressOrdinal"), ColumnKind.ParentKeyPart),
                    new DbKeyColumn(new DbColumnName("Ordinal"), ColumnKind.Ordinal),
                ]
            ),
            Columns:
            [
                new DbColumnModel(
                    ColumnName: new DbColumnName("DocumentId"),
                    Kind: ColumnKind.ParentKeyPart,
                    ScalarType: new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("ParentAddressOrdinal"),
                    Kind: ColumnKind.ParentKeyPart,
                    ScalarType: new RelationalScalarType(ScalarKind.Int32),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("Ordinal"),
                    Kind: ColumnKind.Ordinal,
                    ScalarType: new RelationalScalarType(ScalarKind.Int32),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("AddressScopeValue"),
                    Kind: ColumnKind.Scalar,
                    ScalarType: new RelationalScalarType(ScalarKind.String),
                    IsNullable: true,
                    SourceJsonPath: CreatePath(
                        "$.addresses[*]",
                        new JsonPathSegment.Property("addresses"),
                        new JsonPathSegment.AnyArrayElement()
                    ),
                    TargetResource: null
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("StreetNumber"),
                    Kind: ColumnKind.Scalar,
                    ScalarType: new RelationalScalarType(ScalarKind.String),
                    IsNullable: true,
                    SourceJsonPath: CreatePath(
                        "$.addresses[*].streetNumber",
                        new JsonPathSegment.Property("addresses"),
                        new JsonPathSegment.AnyArrayElement(),
                        new JsonPathSegment.Property("streetNumber")
                    ),
                    TargetResource: null
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("School_DocumentId"),
                    Kind: ColumnKind.DocumentFk,
                    ScalarType: new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: true,
                    SourceJsonPath: CreatePath(
                        "$.addresses[*].schoolReference",
                        new JsonPathSegment.Property("addresses"),
                        new JsonPathSegment.AnyArrayElement(),
                        new JsonPathSegment.Property("schoolReference")
                    ),
                    TargetResource: new QualifiedResourceName("Ed-Fi", "School")
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("ProgramTypeDescriptorId"),
                    Kind: ColumnKind.DescriptorFk,
                    ScalarType: new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: true,
                    SourceJsonPath: CreatePath(
                        "$.addresses[*].programTypeDescriptor",
                        new JsonPathSegment.Property("addresses"),
                        new JsonPathSegment.AnyArrayElement(),
                        new JsonPathSegment.Property("programTypeDescriptor")
                    ),
                    TargetResource: new QualifiedResourceName("Ed-Fi", "ProgramTypeDescriptor")
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("CanonicalProgramTypeCode"),
                    Kind: ColumnKind.Scalar,
                    ScalarType: new RelationalScalarType(ScalarKind.String),
                    IsNullable: true,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("ProgramTypeDescriptorIdAlias"),
                    Kind: ColumnKind.DescriptorFk,
                    ScalarType: new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: true,
                    SourceJsonPath: CreatePath(
                        "$.addresses[*].programTypeDescriptor",
                        new JsonPathSegment.Property("addresses"),
                        new JsonPathSegment.AnyArrayElement(),
                        new JsonPathSegment.Property("programTypeDescriptor")
                    ),
                    TargetResource: new QualifiedResourceName("Ed-Fi", "ProgramTypeDescriptor"),
                    Storage: new ColumnStorage.UnifiedAlias(
                        CanonicalColumn: new DbColumnName("ProgramTypeDescriptorId"),
                        PresenceColumn: null
                    )
                ),
            ],
            Constraints: []
        );

        return new RelationalResourceModel(
            Resource: new QualifiedResourceName("Ed-Fi", "StudentAddress"),
            PhysicalSchema: new DbSchemaName("edfi"),
            StorageKind: ResourceStorageKind.RelationalTables,
            Root: table,
            TablesInDependencyOrder: [table],
            DocumentReferenceBindings:
            [
                new DocumentReferenceBinding(
                    IsIdentityComponent: false,
                    ReferenceObjectPath: CreatePath(
                        "$.addresses[*].schoolReference",
                        new JsonPathSegment.Property("addresses"),
                        new JsonPathSegment.AnyArrayElement(),
                        new JsonPathSegment.Property("schoolReference")
                    ),
                    Table: table.Table,
                    FkColumn: new DbColumnName("School_DocumentId"),
                    TargetResource: new QualifiedResourceName("Ed-Fi", "School"),
                    IdentityBindings: []
                ),
            ],
            DescriptorEdgeSources:
            [
                new DescriptorEdgeSource(
                    IsIdentityComponent: false,
                    DescriptorValuePath: CreatePath(
                        "$.addresses[*].programTypeDescriptor",
                        new JsonPathSegment.Property("addresses"),
                        new JsonPathSegment.AnyArrayElement(),
                        new JsonPathSegment.Property("programTypeDescriptor")
                    ),
                    Table: table.Table,
                    FkColumn: new DbColumnName("ProgramTypeDescriptorId"),
                    DescriptorResource: new QualifiedResourceName("Ed-Fi", "ProgramTypeDescriptor")
                ),
            ]
        );
    }

    private static JsonPathExpression CreatePath(string canonical, params JsonPathSegment[] segments)
    {
        return new JsonPathExpression(canonical, segments);
    }

    private static string CreateWritePlanFingerprint(ResourceWritePlan plan)
    {
        return string.Join(
            "\n--TABLE--\n",
            plan.TablePlansInDependencyOrder.Select(static tablePlan =>
                string.Join(
                    "\n",
                    tablePlan.TableModel.Table.ToString(),
                    tablePlan.InsertSql,
                    tablePlan.UpdateSql ?? "<null>",
                    tablePlan.DeleteByParentSql ?? "<null>",
                    string.Join(
                        "|",
                        tablePlan.ColumnBindings.Select(binding =>
                            $"{binding.Column.ColumnName.Value}:{binding.ParameterName}:{binding.Source.GetType().Name}"
                        )
                    )
                )
            )
        );
    }

    private static ResourceReadPlan CreateRootOnlyReadPlanStub(RelationalResourceModel resourceModel)
    {
        return new ResourceReadPlan(
            Model: resourceModel,
            KeysetTable: KeysetTableConventions.GetKeysetTableContract(SqlDialect.Pgsql),
            TablePlansInDependencyOrder:
            [
                new TableReadPlan(TableModel: resourceModel.Root, SelectByKeysetSql: "SELECT 1;\n"),
            ],
            ReferenceIdentityProjectionPlansInDependencyOrder: [],
            DescriptorProjectionPlansInOrder: []
        );
    }
}

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
public class Given_ReadPlanCompiler : WritePlanCompilerTestBase
{
    private const string RuntimePlanCompilationFixturePath =
        "Fixtures/runtime-plan-compilation/ApiSchema.json";
    private static readonly QualifiedResourceName _rootOnlyFixtureResource = new("Ed-Fi", "School");
    private static readonly QualifiedResourceName _projectionFixtureResource = new("Ed-Fi", "Student");
    private static readonly QualifiedResourceName _multiTableFixtureResource = new(
        "Ed-Fi",
        "StudentAddressCollection"
    );
    private RelationalResourceModel _projectionResourceModel = null!;
    private RelationalResourceModel _rootOnlyResourceModel = null!;
    private RelationalResourceModel _resourceModel = null!;
    private ResourceReadPlan _pgsqlProjectionReadPlan = null!;
    private ResourceReadPlan _pgsqlRootOnlyReadPlan = null!;
    private ResourceReadPlan _pgsqlReadPlan = null!;
    private ResourceReadPlan _mssqlProjectionReadPlan = null!;
    private ResourceReadPlan _mssqlRootOnlyReadPlan = null!;
    private ResourceReadPlan _mssqlReadPlan = null!;

    [SetUp]
    public void SetUpReadPlanCompiler()
    {
        _projectionResourceModel = CreateProjectionMetadataResourceModel();
        _rootOnlyResourceModel = CreateRootOnlyResourceModel();
        _resourceModel = CreateMultiTableResourceModel();
        _pgsqlProjectionReadPlan = new ReadPlanCompiler(SqlDialect.Pgsql).Compile(_projectionResourceModel);
        _pgsqlRootOnlyReadPlan = new ReadPlanCompiler(SqlDialect.Pgsql).Compile(_rootOnlyResourceModel);
        _pgsqlReadPlan = new ReadPlanCompiler(SqlDialect.Pgsql).Compile(_resourceModel);
        _mssqlProjectionReadPlan = new ReadPlanCompiler(SqlDialect.Mssql).Compile(_projectionResourceModel);
        _mssqlRootOnlyReadPlan = new ReadPlanCompiler(SqlDialect.Mssql).Compile(_rootOnlyResourceModel);
        _mssqlReadPlan = new ReadPlanCompiler(SqlDialect.Mssql).Compile(_resourceModel);
    }

    [Test]
    public void It_should_compile_a_table_plan_for_every_table_in_dependency_order()
    {
        _pgsqlReadPlan
            .TablePlansInDependencyOrder.Select(static tablePlan => tablePlan.TableModel)
            .Should()
            .Equal(_resourceModel.TablesInDependencyOrder);
    }

    [Test]
    public void It_should_preserve_the_keyset_contract_and_emit_no_projection_plans_when_model_has_no_projection_metadata()
    {
        _pgsqlReadPlan.Model.Should().Be(_resourceModel);
        _pgsqlReadPlan
            .KeysetTable.Should()
            .Be(KeysetTableConventions.GetKeysetTableContract(SqlDialect.Pgsql));
        _pgsqlReadPlan.ReferenceIdentityProjectionPlansInDependencyOrder.Should().BeEmpty();
        _pgsqlReadPlan.DescriptorProjectionPlansInOrder.Should().BeEmpty();
    }

    [Test]
    public void It_should_compile_reference_identity_projection_metadata_from_document_reference_bindings()
    {
        AssertReferenceIdentityProjectionPlan(_pgsqlProjectionReadPlan);
        AssertReferenceIdentityProjectionPlan(_mssqlProjectionReadPlan);
    }

    [Test]
    public void It_should_compile_root_table_reference_identity_projection_bindings_in_model_order()
    {
        var readPlan = new ReadPlanCompiler(SqlDialect.Pgsql).Compile(
            CreateRootMultiBindingReferenceProjectionResourceModel()
        );

        readPlan.ReferenceIdentityProjectionPlansInDependencyOrder.Should().ContainSingle();

        var tablePlan = readPlan.ReferenceIdentityProjectionPlansInDependencyOrder.Single();
        tablePlan.Table.Should().Be(readPlan.Model.Root.Table);
        tablePlan.BindingsInOrder.Should().HaveCount(2);

        AssertReferenceIdentityProjectionBinding(
            tablePlan.BindingsInOrder[0],
            isIdentityComponent: true,
            referenceObjectPath: "$.schoolReference",
            targetResource: new QualifiedResourceName("Ed-Fi", "School"),
            fkColumnOrdinal: 1,
            ("$.schoolReference.schoolId", 2),
            ("$.schoolReference.schoolYear", 3)
        );
        AssertReferenceIdentityProjectionBinding(
            tablePlan.BindingsInOrder[1],
            isIdentityComponent: false,
            referenceObjectPath: "$.calendarReference",
            targetResource: new QualifiedResourceName("Ed-Fi", "Calendar"),
            fkColumnOrdinal: 4,
            ("$.calendarReference.calendarCode", 5)
        );
    }

    [Test]
    public void It_should_accept_valid_reference_identity_projection_binding_coverage_for_multi_binding_tables()
    {
        var readPlan = new ReadPlanCompiler(SqlDialect.Pgsql).Compile(
            CreateRootMultiBindingReferenceProjectionResourceModel()
        );

        var act = () =>
            ReadPlanProjectionContractValidator.ValidateOrThrow(
                readPlan,
                reason => new InvalidOperationException(reason)
            );

        act.Should().NotThrow();
    }

    [Test]
    public void It_should_reject_reference_identity_projection_binding_duplicates_that_omit_another_modeled_binding()
    {
        var readPlan = new ReadPlanCompiler(SqlDialect.Pgsql).Compile(
            CreateRootMultiBindingReferenceProjectionResourceModel()
        );
        var duplicatedReadPlan = CreateReadPlanWithDuplicatedReferenceProjectionBinding(
            readPlan,
            sourceIndex: 0,
            targetIndex: 1
        );

        var act = () =>
            ReadPlanProjectionContractValidator.ValidateOrThrow(
                duplicatedReadPlan,
                reason => new InvalidOperationException(reason)
            );

        var exception = act.Should().Throw<InvalidOperationException>().Which;
        exception.Message.Should().Contain("reference identity projection binding at index '1'");
        exception.Message.Should().Contain("$.schoolReference");
        exception.Message.Should().Contain("$.calendarReference");
    }

    [Test]
    public void It_should_reject_reference_identity_projection_binding_order_that_differs_from_document_reference_bindings()
    {
        var readPlan = new ReadPlanCompiler(SqlDialect.Pgsql).Compile(
            CreateRootMultiBindingReferenceProjectionResourceModel()
        );
        var reorderedReadPlan = CreateReadPlanWithSwappedReferenceProjectionBindings(readPlan);

        var act = () =>
            ReadPlanProjectionContractValidator.ValidateOrThrow(
                reorderedReadPlan,
                reason => new InvalidOperationException(reason)
            );

        var exception = act.Should().Throw<InvalidOperationException>().Which;
        exception.Message.Should().Contain("reference identity projection binding at index '0'");
        exception.Message.Should().Contain("$.calendarReference");
        exception.Message.Should().Contain("$.schoolReference");
    }

    [Test]
    public void It_should_group_reference_identity_projection_bindings_by_dependency_order_subset_when_bindings_are_interleaved()
    {
        var readPlan = new ReadPlanCompiler(SqlDialect.Pgsql).Compile(
            CreateInterleavedReferenceProjectionResourceModel(rootBindingFirst: false)
        );

        readPlan
            .ReferenceIdentityProjectionPlansInDependencyOrder.Select(static plan => plan.Table)
            .Should()
            .Equal(
                readPlan.Model.TablesInDependencyOrder[0].Table,
                readPlan.Model.TablesInDependencyOrder[1].Table
            );

        var rootTablePlan = readPlan.ReferenceIdentityProjectionPlansInDependencyOrder[0];
        rootTablePlan.BindingsInOrder.Should().ContainSingle();

        AssertReferenceIdentityProjectionBinding(
            rootTablePlan.BindingsInOrder[0],
            isIdentityComponent: true,
            referenceObjectPath: "$.schoolReference",
            targetResource: new QualifiedResourceName("Ed-Fi", "School"),
            fkColumnOrdinal: 1,
            ("$.schoolReference.schoolId", 2)
        );

        var childTablePlan = readPlan.ReferenceIdentityProjectionPlansInDependencyOrder[1];
        childTablePlan.BindingsInOrder.Should().HaveCount(2);

        AssertReferenceIdentityProjectionBinding(
            childTablePlan.BindingsInOrder[0],
            isIdentityComponent: false,
            referenceObjectPath: "$.addresses[*].calendarReference",
            targetResource: new QualifiedResourceName("Ed-Fi", "Calendar"),
            fkColumnOrdinal: 2,
            ("$.addresses[*].calendarReference.calendarCode", 3)
        );
        AssertReferenceIdentityProjectionBinding(
            childTablePlan.BindingsInOrder[1],
            isIdentityComponent: false,
            referenceObjectPath: "$.addresses[*].sessionReference",
            targetResource: new QualifiedResourceName("Ed-Fi", "Session"),
            fkColumnOrdinal: 4,
            ("$.addresses[*].sessionReference.sessionName", 5)
        );
    }

    [Test]
    public void It_should_compile_identical_multi_table_reference_projection_plans_across_repeated_compilation_and_cross_table_binding_interleavings()
    {
        var compiler = new ReadPlanCompiler(SqlDialect.Pgsql);

        var first = compiler.Compile(CreateInterleavedReferenceProjectionResourceModel(false));
        var second = compiler.Compile(CreateInterleavedReferenceProjectionResourceModel(false));
        var permuted = compiler.Compile(CreateInterleavedReferenceProjectionResourceModel(true));

        var firstFingerprint = CreateReadPlanFingerprint(first);
        var secondFingerprint = CreateReadPlanFingerprint(second);
        var permutedFingerprint = CreateReadPlanFingerprint(permuted);

        secondFingerprint.Should().Be(firstFingerprint);
        permutedFingerprint.Should().Be(firstFingerprint);
    }

    [Test]
    public void It_should_compile_key_unified_reference_identity_projection_bindings_against_unified_alias_column_ordinals()
    {
        var readPlan = new ReadPlanCompiler(SqlDialect.Pgsql).Compile(
            CreateKeyUnifiedReferenceProjectionResourceModel()
        );

        readPlan.ReferenceIdentityProjectionPlansInDependencyOrder.Should().ContainSingle();

        var tablePlan = readPlan.ReferenceIdentityProjectionPlansInDependencyOrder.Single();
        tablePlan.BindingsInOrder.Should().ContainSingle();

        var binding = tablePlan.BindingsInOrder.Single();

        AssertReferenceIdentityProjectionBinding(
            binding,
            isIdentityComponent: true,
            referenceObjectPath: "$.schoolReference",
            targetResource: new QualifiedResourceName("Ed-Fi", "School"),
            fkColumnOrdinal: 3,
            ("$.schoolReference.localSchoolYear", 5),
            ("$.schoolReference.schoolYear", 4)
        );

        var tableColumns = readPlan.TablePlansInDependencyOrder.Single().TableModel.Columns;
        tableColumns[binding.FkColumnOrdinal].ColumnName.Should().Be(new DbColumnName("School_DocumentId"));
        tableColumns[binding.FkColumnOrdinal].Kind.Should().Be(ColumnKind.DocumentFk);
        tableColumns[binding.FkColumnOrdinal].Storage.Should().BeOfType<ColumnStorage.Stored>();
        tableColumns[binding.IdentityFieldOrdinalsInOrder[0].ColumnOrdinal]
            .ColumnName.Should()
            .Be(new DbColumnName("SchoolYearSecondary"));
        tableColumns[binding.IdentityFieldOrdinalsInOrder[0].ColumnOrdinal]
            .Storage.Should()
            .BeOfType<ColumnStorage.UnifiedAlias>();
        tableColumns[binding.IdentityFieldOrdinalsInOrder[1].ColumnOrdinal]
            .ColumnName.Should()
            .Be(new DbColumnName("SchoolYearPrimary"));
        tableColumns[binding.IdentityFieldOrdinalsInOrder[1].ColumnOrdinal]
            .Storage.Should()
            .BeOfType<ColumnStorage.UnifiedAlias>();
    }

    [Test]
    public void It_should_emit_exact_pgsql_DescriptorProjection_sql_for_projection_metadata_resources()
    {
        AssertDescriptorProjectionPlan(
            _pgsqlProjectionReadPlan,
            """
            SELECT
                p."DescriptorId",
                d."Uri"
            FROM
                (
                    SELECT DISTINCT t0."AcademicSubjectDescriptorId" AS "DescriptorId"
                    FROM "edfi"."StudentProjection" t0
                    INNER JOIN "page" k ON t0."DocumentId" = k."DocumentId"
                    WHERE t0."AcademicSubjectDescriptorId" IS NOT NULL
                ) p
            INNER JOIN "dms"."Descriptor" d ON d."DocumentId" = p."DescriptorId"
            ORDER BY
                p."DescriptorId" ASC
            ;

            """
        );
    }

    [Test]
    public void It_should_emit_exact_mssql_DescriptorProjection_sql_for_projection_metadata_resources()
    {
        AssertDescriptorProjectionPlan(
            _mssqlProjectionReadPlan,
            """
            SELECT
                p.[DescriptorId],
                d.[Uri]
            FROM
                (
                    SELECT DISTINCT t0.[AcademicSubjectDescriptorId] AS [DescriptorId]
                    FROM [edfi].[StudentProjection] t0
                    INNER JOIN [#page] k ON t0.[DocumentId] = k.[DocumentId]
                    WHERE t0.[AcademicSubjectDescriptorId] IS NOT NULL
                ) p
            INNER JOIN [dms].[Descriptor] d ON d.[DocumentId] = p.[DescriptorId]
            ORDER BY
                p.[DescriptorId] ASC
            ;

            """
        );
    }

    [Test]
    public void It_should_preserve_all_logical_DescriptorProjection_sources_in_order_while_deduplicating_pgsql_sql_inputs_by_storage_column()
    {
        var readPlan = new ReadPlanCompiler(SqlDialect.Pgsql).Compile(
            CreateKeyUnifiedDescriptorProjectionResourceModel()
        );

        AssertKeyUnifiedDescriptorProjectionPlan(
            readPlan,
            """
            SELECT
                p."DescriptorId",
                d."Uri"
            FROM
                (
                    SELECT DISTINCT t0."SchoolYearTypeDescriptorIdCanonical" AS "DescriptorId"
                    FROM "edfi"."Student" t0
                    INNER JOIN "page" k ON t0."DocumentId" = k."DocumentId"
                    WHERE t0."SchoolYearTypeDescriptorIdCanonical" IS NOT NULL
                ) p
            INNER JOIN "dms"."Descriptor" d ON d."DocumentId" = p."DescriptorId"
            ORDER BY
                p."DescriptorId" ASC
            ;

            """
        );
    }

    [Test]
    public void It_should_preserve_all_logical_DescriptorProjection_sources_in_order_while_deduplicating_mssql_sql_inputs_by_storage_column()
    {
        var readPlan = new ReadPlanCompiler(SqlDialect.Mssql).Compile(
            CreateKeyUnifiedDescriptorProjectionResourceModel()
        );

        AssertKeyUnifiedDescriptorProjectionPlan(
            readPlan,
            """
            SELECT
                p.[DescriptorId],
                d.[Uri]
            FROM
                (
                    SELECT DISTINCT t0.[SchoolYearTypeDescriptorIdCanonical] AS [DescriptorId]
                    FROM [edfi].[Student] t0
                    INNER JOIN [#page] k ON t0.[DocumentId] = k.[DocumentId]
                    WHERE t0.[SchoolYearTypeDescriptorIdCanonical] IS NOT NULL
                ) p
            INNER JOIN [dms].[Descriptor] d ON d.[DocumentId] = p.[DescriptorId]
            ORDER BY
                p.[DescriptorId] ASC
            ;

            """
        );
    }

    [Test]
    public void It_should_accept_key_unified_projection_path_columns_while_descriptor_sql_still_deduplicates_by_storage_column()
    {
        var referenceReadPlan = new ReadPlanCompiler(SqlDialect.Pgsql).Compile(
            CreateKeyUnifiedReferenceProjectionResourceModel()
        );
        var descriptorReadPlan = new ReadPlanCompiler(SqlDialect.Pgsql).Compile(
            CreateKeyUnifiedDescriptorProjectionResourceModel()
        );

        var binding = referenceReadPlan
            .ReferenceIdentityProjectionPlansInDependencyOrder.Single()
            .BindingsInOrder.Single();

        AssertReferenceIdentityProjectionBinding(
            binding,
            isIdentityComponent: true,
            referenceObjectPath: "$.schoolReference",
            targetResource: new QualifiedResourceName("Ed-Fi", "School"),
            fkColumnOrdinal: 3,
            ("$.schoolReference.localSchoolYear", 5),
            ("$.schoolReference.schoolYear", 4)
        );

        AssertKeyUnifiedDescriptorProjectionPlan(
            descriptorReadPlan,
            """
            SELECT
                p."DescriptorId",
                d."Uri"
            FROM
                (
                    SELECT DISTINCT t0."SchoolYearTypeDescriptorIdCanonical" AS "DescriptorId"
                    FROM "edfi"."Student" t0
                    INNER JOIN "page" k ON t0."DocumentId" = k."DocumentId"
                    WHERE t0."SchoolYearTypeDescriptorIdCanonical" IS NOT NULL
                ) p
            INNER JOIN "dms"."Descriptor" d ON d."DocumentId" = p."DescriptorId"
            ORDER BY
                p."DescriptorId" ASC
            ;

            """
        );
    }

    [Test]
    public void It_should_order_descriptor_projection_sql_by_table_column_order_when_storage_only_canonical_columns_are_not_hydrated()
    {
        var model = CreateKeyUnifiedDescriptorProjectionResourceModelWithStoredDescriptorSource();
        var descriptorProjectionPlans = CompileDescriptorProjectionPlans(
            model,
            SqlDialect.Pgsql,
            CreateHydrationColumnOrdinalsExcludingStorageOnlyColumns(model.Root)
        );

        descriptorProjectionPlans.Should().ContainSingle();

        var descriptorProjectionPlan = descriptorProjectionPlans.Single();

        descriptorProjectionPlan
            .SelectByKeysetSql.Should()
            .Be(
                """
                SELECT
                    p."DescriptorId",
                    d."Uri"
                FROM
                    (
                        SELECT t0."SchoolYearTypeDescriptorIdCanonical" AS "DescriptorId"
                        FROM "edfi"."Student" t0
                        INNER JOIN "page" k ON t0."DocumentId" = k."DocumentId"
                        WHERE t0."SchoolYearTypeDescriptorIdCanonical" IS NOT NULL
                        UNION
                        SELECT t1."ProgramTypeDescriptorId" AS "DescriptorId"
                        FROM "edfi"."Student" t1
                        INNER JOIN "page" k ON t1."DocumentId" = k."DocumentId"
                        WHERE t1."ProgramTypeDescriptorId" IS NOT NULL
                    ) p
                INNER JOIN "dms"."Descriptor" d ON d."DocumentId" = p."DescriptorId"
                ORDER BY
                    p."DescriptorId" ASC
                ;

                """
            );
        descriptorProjectionPlan
            .SourcesInOrder.Select(static source => source.DescriptorValuePath.Canonical)
            .Should()
            .Equal(
                "$.localSchoolYearTypeDescriptor",
                "$.schoolYearTypeDescriptor",
                "$.programTypeDescriptor"
            );
        descriptorProjectionPlan
            .SourcesInOrder.Select(static source => source.DescriptorIdColumnOrdinal)
            .Should()
            .Equal(5, 4, 6);
    }

    [Test]
    public void It_should_accept_valid_descriptor_projection_source_coverage_for_key_unified_models()
    {
        var readPlan = new ReadPlanCompiler(SqlDialect.Pgsql).Compile(
            CreateKeyUnifiedDescriptorProjectionResourceModel()
        );

        var act = () =>
            ReadPlanProjectionContractValidator.ValidateOrThrow(
                readPlan,
                reason => new InvalidOperationException(reason)
            );

        act.Should().NotThrow();
    }

    [Test]
    public void It_should_reject_descriptor_projection_source_duplicates_that_omit_another_modeled_source()
    {
        var readPlan = new ReadPlanCompiler(SqlDialect.Pgsql).Compile(
            CreateKeyUnifiedDescriptorProjectionResourceModel()
        );
        var duplicatedReadPlan = CreateReadPlanWithDuplicatedDescriptorProjectionSource(
            readPlan,
            sourceIndex: 0,
            targetIndex: 1
        );

        var act = () =>
            ReadPlanProjectionContractValidator.ValidateOrThrow(
                duplicatedReadPlan,
                reason => new InvalidOperationException(reason)
            );

        var exception = act.Should().Throw<InvalidOperationException>().Which;
        exception
            .Message.Should()
            .Contain("descriptor projection source at plan index '0', source index '1'");
        exception.Message.Should().Contain("$.localSchoolYearTypeDescriptor");
        exception.Message.Should().Contain("$.schoolYearTypeDescriptor");
    }

    [Test]
    public void It_should_reject_descriptor_projection_source_order_that_differs_from_descriptor_edge_sources()
    {
        var readPlan = new ReadPlanCompiler(SqlDialect.Pgsql).Compile(
            CreateKeyUnifiedDescriptorProjectionResourceModel()
        );
        var reorderedReadPlan = CreateReadPlanWithSwappedDescriptorProjectionSources(readPlan);

        var act = () =>
            ReadPlanProjectionContractValidator.ValidateOrThrow(
                reorderedReadPlan,
                reason => new InvalidOperationException(reason)
            );

        var exception = act.Should().Throw<InvalidOperationException>().Which;
        exception
            .Message.Should()
            .Contain("descriptor projection source at plan index '0', source index '0'");
        exception.Message.Should().Contain("$.schoolYearTypeDescriptor");
        exception.Message.Should().Contain("$.localSchoolYearTypeDescriptor");
    }

    [Test]
    public void It_should_compile_a_single_root_only_table_plan_with_expected_keyset_contract()
    {
        _pgsqlRootOnlyReadPlan.Model.Should().Be(_rootOnlyResourceModel);
        _pgsqlRootOnlyReadPlan
            .KeysetTable.Should()
            .Be(KeysetTableConventions.GetKeysetTableContract(SqlDialect.Pgsql));
        _pgsqlRootOnlyReadPlan.TablePlansInDependencyOrder.Should().ContainSingle();
        _pgsqlRootOnlyReadPlan.ReferenceIdentityProjectionPlansInDependencyOrder.Should().BeEmpty();
        _pgsqlRootOnlyReadPlan.DescriptorProjectionPlansInOrder.Should().BeEmpty();
    }

    [Test]
    public void It_should_compile_identical_root_only_read_plans_across_repeated_compilation_and_fixture_resource_order_permutations()
    {
        var compiler = new ReadPlanCompiler(SqlDialect.Pgsql);

        var first = compiler.Compile(
            BuildFixtureResourceModel(_rootOnlyFixtureResource, SqlDialect.Pgsql, false)
        );
        var second = compiler.Compile(
            BuildFixtureResourceModel(_rootOnlyFixtureResource, SqlDialect.Pgsql, false)
        );
        var permuted = compiler.Compile(
            BuildFixtureResourceModel(_rootOnlyFixtureResource, SqlDialect.Pgsql, true)
        );

        var firstFingerprint = CreateReadPlanFingerprint(first);
        var secondFingerprint = CreateReadPlanFingerprint(second);
        var permutedFingerprint = CreateReadPlanFingerprint(permuted);

        secondFingerprint.Should().Be(firstFingerprint);
        permutedFingerprint.Should().Be(firstFingerprint);
    }

    [Test]
    public void It_should_compile_identical_projection_read_plans_across_repeated_compilation_and_fixture_resource_order_permutations()
    {
        var compiler = new ReadPlanCompiler(SqlDialect.Pgsql);

        var first = compiler.Compile(
            BuildFixtureResourceModel(_projectionFixtureResource, SqlDialect.Pgsql, false)
        );
        var second = compiler.Compile(
            BuildFixtureResourceModel(_projectionFixtureResource, SqlDialect.Pgsql, false)
        );
        var permuted = compiler.Compile(
            BuildFixtureResourceModel(_projectionFixtureResource, SqlDialect.Pgsql, true)
        );

        var firstFingerprint = CreateReadPlanFingerprint(first);
        var secondFingerprint = CreateReadPlanFingerprint(second);
        var permutedFingerprint = CreateReadPlanFingerprint(permuted);

        secondFingerprint.Should().Be(firstFingerprint);
        permutedFingerprint.Should().Be(firstFingerprint);
    }

    [Test]
    public void It_should_compile_identical_multi_table_read_plans_across_repeated_compilation_and_fixture_resource_order_permutations()
    {
        var compiler = new ReadPlanCompiler(SqlDialect.Pgsql);

        var first = compiler.Compile(
            BuildFixtureResourceModel(_multiTableFixtureResource, SqlDialect.Pgsql, false)
        );
        var second = compiler.Compile(
            BuildFixtureResourceModel(_multiTableFixtureResource, SqlDialect.Pgsql, false)
        );
        var permuted = compiler.Compile(
            BuildFixtureResourceModel(_multiTableFixtureResource, SqlDialect.Pgsql, true)
        );

        var firstFingerprint = CreateReadPlanFingerprint(first);
        var secondFingerprint = CreateReadPlanFingerprint(second);
        var permutedFingerprint = CreateReadPlanFingerprint(permuted);

        secondFingerprint.Should().Be(firstFingerprint);
        permutedFingerprint.Should().Be(firstFingerprint);
    }

    [Test]
    public void It_should_emit_select_list_and_order_by_columns_in_model_order_for_every_table_plan()
    {
        AssertSqlProjectionAndOrderingMatchesModel(_pgsqlReadPlan);
        AssertSqlProjectionAndOrderingMatchesModel(_mssqlReadPlan);
    }

    [Test]
    public void It_should_use_stable_root_table_non_root_table_and_keyset_aliases_across_dialects()
    {
        AssertStableAliases(_pgsqlRootOnlyReadPlan);
        AssertStableAliases(_mssqlRootOnlyReadPlan);
        AssertStableAliases(_pgsqlReadPlan);
        AssertStableAliases(_mssqlReadPlan);
    }

    [Test]
    public void It_should_emit_exact_pgsql_SelectByKeysetSql_for_root_only_tables()
    {
        AssertSelectByKeysetSql(
            _pgsqlRootOnlyReadPlan,
            "Student",
            """
            SELECT
                r."DocumentId",
                r."SchoolYear",
                r."LocalEducationAgencyId",
                r."SchoolYearAlias"
            FROM "edfi"."Student" r
            INNER JOIN "page" k ON r."DocumentId" = k."DocumentId"
            ORDER BY
                r."DocumentId" ASC
            ;

            """
        );
    }

    [Test]
    public void It_should_emit_exact_mssql_SelectByKeysetSql_for_root_only_tables()
    {
        AssertSelectByKeysetSql(
            _mssqlRootOnlyReadPlan,
            "Student",
            """
            SELECT
                r.[DocumentId],
                r.[SchoolYear],
                r.[LocalEducationAgencyId],
                r.[SchoolYearAlias]
            FROM [edfi].[Student] r
            INNER JOIN [#page] k ON r.[DocumentId] = k.[DocumentId]
            ORDER BY
                r.[DocumentId] ASC
            ;

            """
        );
    }

    [Test]
    public void It_should_emit_exact_pgsql_SelectByKeysetSql_for_root_child_nested_and_extension_tables()
    {
        AssertSelectByKeysetSql(
            _pgsqlReadPlan,
            "Student",
            """
            SELECT
                r."DocumentId",
                r."StudentUniqueId"
            FROM "edfi"."Student" r
            INNER JOIN "page" k ON r."DocumentId" = k."DocumentId"
            ORDER BY
                r."DocumentId" ASC
            ;

            """
        );

        AssertSelectByKeysetSql(
            _pgsqlReadPlan,
            "StudentAddress",
            """
            SELECT
                t."Student_DocumentId",
                t."Ordinal",
                t."City"
            FROM "edfi"."StudentAddress" t
            INNER JOIN "page" k ON t."Student_DocumentId" = k."DocumentId"
            ORDER BY
                t."Student_DocumentId" ASC,
                t."Ordinal" ASC
            ;

            """
        );

        AssertSelectByKeysetSql(
            _pgsqlReadPlan,
            "StudentAddressPeriod",
            """
            SELECT
                t."Student_DocumentId",
                t."AddressOrdinal",
                t."Ordinal",
                t."BeginDate"
            FROM "edfi"."StudentAddressPeriod" t
            INNER JOIN "page" k ON t."Student_DocumentId" = k."DocumentId"
            ORDER BY
                t."Student_DocumentId" ASC,
                t."AddressOrdinal" ASC,
                t."Ordinal" ASC
            ;

            """
        );

        AssertSelectByKeysetSql(
            _pgsqlReadPlan,
            "StudentExtension",
            """
            SELECT
                t."DocumentId",
                t."FavoriteColor"
            FROM "sample"."StudentExtension" t
            INNER JOIN "page" k ON t."DocumentId" = k."DocumentId"
            ORDER BY
                t."DocumentId" ASC
            ;

            """
        );
    }

    [Test]
    public void It_should_emit_exact_mssql_SelectByKeysetSql_for_root_child_nested_and_extension_tables()
    {
        AssertSelectByKeysetSql(
            _mssqlReadPlan,
            "Student",
            """
            SELECT
                r.[DocumentId],
                r.[StudentUniqueId]
            FROM [edfi].[Student] r
            INNER JOIN [#page] k ON r.[DocumentId] = k.[DocumentId]
            ORDER BY
                r.[DocumentId] ASC
            ;

            """
        );

        AssertSelectByKeysetSql(
            _mssqlReadPlan,
            "StudentAddress",
            """
            SELECT
                t.[Student_DocumentId],
                t.[Ordinal],
                t.[City]
            FROM [edfi].[StudentAddress] t
            INNER JOIN [#page] k ON t.[Student_DocumentId] = k.[DocumentId]
            ORDER BY
                t.[Student_DocumentId] ASC,
                t.[Ordinal] ASC
            ;

            """
        );

        AssertSelectByKeysetSql(
            _mssqlReadPlan,
            "StudentAddressPeriod",
            """
            SELECT
                t.[Student_DocumentId],
                t.[AddressOrdinal],
                t.[Ordinal],
                t.[BeginDate]
            FROM [edfi].[StudentAddressPeriod] t
            INNER JOIN [#page] k ON t.[Student_DocumentId] = k.[DocumentId]
            ORDER BY
                t.[Student_DocumentId] ASC,
                t.[AddressOrdinal] ASC,
                t.[Ordinal] ASC
            ;

            """
        );

        AssertSelectByKeysetSql(
            _mssqlReadPlan,
            "StudentExtension",
            """
            SELECT
                t.[DocumentId],
                t.[FavoriteColor]
            FROM [sample].[StudentExtension] t
            INNER JOIN [#page] k ON t.[DocumentId] = k.[DocumentId]
            ORDER BY
                t.[DocumentId] ASC
            ;

            """
        );
    }

    [Test]
    public void It_should_fail_fast_when_tables_in_dependency_order_is_empty()
    {
        var act = () =>
            new ReadPlanCompiler(SqlDialect.Pgsql).Compile(CreateModelWithNoTablesInDependencyOrder());

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                "Cannot compile read plan for resource 'Ed-Fi.Student': no tables were found in dependency order."
            );
    }

    [Test]
    public void It_should_fail_fast_when_resource_model_root_is_not_root_scope()
    {
        var act = () =>
            new ReadPlanCompiler(SqlDialect.Pgsql).Compile(CreateModelWithNonRootResourceModelRoot());

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                "Cannot compile read plan for resource 'Ed-Fi.Student': resourceModel.Root must have JsonScope '$', but was '$.shadow'."
            );
    }

    [Test]
    public void It_should_fail_fast_when_tables_in_dependency_order_has_no_root_scope_table()
    {
        var unsupportedModel = CreateSupportedMultiTableModelWithoutRootScopeTable();
        var act = () => new ReadPlanCompiler(SqlDialect.Pgsql).Compile(unsupportedModel);

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                "Cannot compile read plan for resource 'Ed-Fi.Student': expected exactly one root-scope table (JsonScope '$') in TablesInDependencyOrder, but found 0."
            );
    }

    [Test]
    public void It_should_fail_fast_when_tables_in_dependency_order_has_multiple_root_scope_tables()
    {
        var unsupportedModel = CreateSupportedMultiTableModelWithMultipleRootScopeTables();
        var act = () => new ReadPlanCompiler(SqlDialect.Pgsql).Compile(unsupportedModel);

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                "Cannot compile read plan for resource 'Ed-Fi.Student': expected exactly one root-scope table (JsonScope '$') in TablesInDependencyOrder, but found 2."
            );
    }

    [Test]
    public void It_should_fail_fast_when_root_scope_table_does_not_match_resource_model_root_table()
    {
        var unsupportedModel = CreateSupportedMultiTableModelWithMismatchedRootScopeTable();
        var act = () => new ReadPlanCompiler(SqlDialect.Pgsql).Compile(unsupportedModel);

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                "Cannot compile read plan for resource 'Ed-Fi.Student': root-scope table 'edfi.StudentShadow' does not match resourceModel.Root table 'edfi.Student'."
            );
    }

    [TestCaseSource(
        typeof(SharedInvalidKeyShapeTestCases),
        nameof(SharedInvalidKeyShapeTestCases.ForReadPlanCompiler)
    )]
    public void It_should_preserve_shared_key_shape_validation_messages_for_malformed_table_keys(
        Func<RelationalResourceModel> createModel,
        string expectedMessage
    )
    {
        var act = () => new ReadPlanCompiler(SqlDialect.Pgsql).Compile(createModel());

        act.Should().Throw<InvalidOperationException>().WithMessage(expectedMessage);
    }

    [Test]
    public void It_should_fail_fast_when_key_column_kind_is_not_parent_key_part_or_ordinal()
    {
        var act = () =>
            new ReadPlanCompiler(SqlDialect.Pgsql).Compile(CreateModelWithUnsupportedKeyColumnKind());

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                "Cannot compile read plan for 'edfi.Student': key column 'SchoolYear' has unsupported kind 'Scalar'. Supported key kinds are ParentKeyPart and Ordinal."
            );
    }

    [Test]
    public void It_should_fail_fast_when_document_reference_fk_column_does_not_resolve_to_a_hydration_select_list_ordinal()
    {
        var act = () =>
            new ReadPlanCompiler(SqlDialect.Pgsql).Compile(CreateModelWithMissingDocumentReferenceFkColumn());

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                "Cannot compile read plan for 'edfi.StudentAddress': document-reference binding '$.addresses[*].schoolReference' FK column 'MissingSchool_DocumentId' does not exist in hydration select-list columns."
            );
    }

    [Test]
    public void It_should_fail_fast_when_reference_identity_binding_column_does_not_resolve_to_a_hydration_select_list_ordinal()
    {
        var act = () =>
            new ReadPlanCompiler(SqlDialect.Pgsql).Compile(
                CreateModelWithMissingReferenceIdentityBindingColumn()
            );

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                "Cannot compile read plan for 'edfi.StudentAddress': reference-identity binding '$.addresses[*].schoolReference.schoolId' for reference '$.addresses[*].schoolReference' column 'MissingSchoolId' does not exist in hydration select-list columns."
            );
    }

    [Test]
    public void It_should_fail_fast_when_document_reference_binding_table_is_not_present_in_tables_in_dependency_order()
    {
        var act = () =>
            new ReadPlanCompiler(SqlDialect.Pgsql).Compile(
                CreateModelWithMissingDocumentReferenceBindingTable()
            );

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                "Cannot compile read plan for 'edfi.MissingStudentAddress': owning table is not present in TablesInDependencyOrder."
            );
    }

    [Test]
    public void It_should_fail_fast_when_document_reference_fk_column_source_json_path_does_not_match_reference_object_path()
    {
        var act = () =>
            new ReadPlanCompiler(SqlDialect.Pgsql).Compile(
                CreateModelWithMismatchedDocumentReferenceFkSourcePath()
            );

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                "Cannot compile read plan for 'edfi.StudentProjection': document-reference binding '$.schoolReference' FK column 'School_DocumentId' has DbColumnModel.SourceJsonPath '$.studentUniqueId', which does not match DocumentReferenceBinding.ReferenceObjectPath '$.schoolReference'."
            );
    }

    [Test]
    public void It_should_fail_fast_when_document_reference_fk_column_is_not_a_document_fk()
    {
        var act = () =>
            new ReadPlanCompiler(SqlDialect.Pgsql).Compile(CreateModelWithNonDocumentReferenceFkKind());

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                "Cannot compile read plan for 'edfi.StudentProjection': document-reference binding '$.schoolReference' FK column 'School_DocumentId' has kind 'Scalar'. Expected 'DocumentFk'."
            );
    }

    [Test]
    public void It_should_fail_fast_when_document_reference_fk_column_is_not_stored()
    {
        var act = () =>
            new ReadPlanCompiler(SqlDialect.Pgsql).Compile(
                CreateModelWithNonStoredDocumentReferenceFkColumn()
            );

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                "Cannot compile read plan for 'edfi.StudentProjection': document-reference binding '$.schoolReference' FK column 'School_DocumentId' is not stored."
            );
    }

    [Test]
    public void It_should_fail_fast_when_key_unified_reference_identity_binding_points_at_canonical_storage_column()
    {
        var act = () =>
            new ReadPlanCompiler(SqlDialect.Pgsql).Compile(
                CreateKeyUnifiedModelWithCanonicalReferenceIdentityBindingColumn()
            );

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                "Cannot compile read plan for 'edfi.Student': reference-identity binding '$.schoolReference.localSchoolYear' for reference '$.schoolReference' column 'SchoolYearCanonical' has DbColumnModel.SourceJsonPath '<null>', which does not match ReferenceIdentityBinding.ReferenceJsonPath '$.schoolReference.localSchoolYear'."
            );
    }

    [Test]
    public void It_should_fail_fast_when_descriptor_edge_source_fk_column_does_not_resolve_to_a_hydration_select_list_ordinal()
    {
        var act = () =>
            new ReadPlanCompiler(SqlDialect.Pgsql).Compile(CreateModelWithMissingDescriptorEdgeFkColumn());

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                "Cannot compile read plan for 'edfi.StudentAddress': descriptor edge source '$.addresses[*].programTypeDescriptor' FK column 'MissingProgramTypeDescriptorId' does not exist in hydration select-list columns."
            );
    }

    [Test]
    public void It_should_fail_fast_when_descriptor_edge_source_table_is_not_present_in_tables_in_dependency_order()
    {
        var act = () =>
            new ReadPlanCompiler(SqlDialect.Pgsql).Compile(CreateModelWithMissingDescriptorEdgeTable());

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                "Cannot compile read plan for 'edfi.MissingStudentAddress': owning table is not present in TablesInDependencyOrder."
            );
    }

    [Test]
    public void It_should_fail_fast_when_key_unified_descriptor_edge_source_points_at_canonical_storage_column()
    {
        var act = () =>
            new ReadPlanCompiler(SqlDialect.Pgsql).Compile(
                CreateKeyUnifiedModelWithCanonicalDescriptorEdgeBindingColumn()
            );

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                "Cannot compile read plan for 'edfi.Student': descriptor edge source '$.localSchoolYearTypeDescriptor' FK column 'SchoolYearTypeDescriptorIdCanonical' has DbColumnModel.SourceJsonPath '<null>', which does not match DescriptorEdgeSource.DescriptorValuePath '$.localSchoolYearTypeDescriptor'."
            );
    }

    [Test]
    public void It_should_fail_fast_when_descriptor_edge_source_fk_column_is_not_a_descriptor_fk()
    {
        var act = () =>
            new ReadPlanCompiler(SqlDialect.Pgsql).Compile(CreateModelWithNonDescriptorEdgeFkKind());

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                "Cannot compile descriptor projection plan for 'edfi.StudentProjection': descriptor edge source '$.academicSubjectDescriptor' FK column 'AcademicSubjectDescriptorId' has kind 'Scalar'. Expected 'DescriptorFk'."
            );
    }

    [Test]
    public void It_should_mark_shared_descriptor_storage_as_unsupported()
    {
        var unsupportedModel = _resourceModel with
        {
            StorageKind = ResourceStorageKind.SharedDescriptorTable,
        };

        ReadPlanCompiler.IsSupported(unsupportedModel).Should().BeFalse();

        var wasCompiled = new ReadPlanCompiler(SqlDialect.Pgsql).TryCompile(
            unsupportedModel,
            out var readPlan
        );

        wasCompiled.Should().BeFalse();
        readPlan.Should().BeNull();
    }

    private static void AssertSelectByKeysetSql(
        ResourceReadPlan readPlan,
        string tableName,
        string expectedSql
    )
    {
        readPlan
            .TablePlansInDependencyOrder.Single(tablePlan => tablePlan.TableModel.Table.Name == tableName)
            .SelectByKeysetSql.Should()
            .Be(expectedSql);
    }

    private static void AssertSqlProjectionAndOrderingMatchesModel(ResourceReadPlan readPlan)
    {
        foreach (var tablePlan in readPlan.TablePlansInDependencyOrder)
        {
            var expectedSelectList = tablePlan
                .TableModel.Columns.Select(static column => column.ColumnName.Value)
                .ToArray();
            var expectedOrderBy = tablePlan
                .TableModel.Key.Columns.Select(static column => column.ColumnName.Value)
                .ToArray();

            ReadPlanSqlShape
                .ExtractSelectedColumnNames(tablePlan.SelectByKeysetSql)
                .Should()
                .Equal(expectedSelectList);
            ReadPlanSqlShape
                .ExtractOrderByColumnNames(tablePlan.SelectByKeysetSql)
                .Should()
                .Equal(expectedOrderBy);
        }
    }

    private static void AssertStableAliases(ResourceReadPlan readPlan)
    {
        for (var index = 0; index < readPlan.TablePlansInDependencyOrder.Length; index++)
        {
            var tablePlan = readPlan.TablePlansInDependencyOrder[index];
            var expectedTableAlias =
                index == 0
                    ? PlanNamingConventions.GetFixedAlias(PlanSqlAliasRole.Root)
                    : PlanNamingConventions.GetFixedAlias(PlanSqlAliasRole.Table);

            ReadPlanSqlShape.ExtractFromAlias(tablePlan.SelectByKeysetSql).Should().Be(expectedTableAlias);
            ReadPlanSqlShape
                .ExtractJoinAlias(tablePlan.SelectByKeysetSql)
                .Should()
                .Be(PlanNamingConventions.GetFixedAlias(PlanSqlAliasRole.Keyset));
        }
    }

    private static ResourceReadPlan CreateReadPlanWithDuplicatedReferenceProjectionBinding(
        ResourceReadPlan readPlan,
        int sourceIndex,
        int targetIndex
    )
    {
        var projectionTablePlan = readPlan.ReferenceIdentityProjectionPlansInDependencyOrder.Single();
        var bindings = projectionTablePlan.BindingsInOrder.ToArray();

        bindings[targetIndex] = bindings[sourceIndex];

        return readPlan with
        {
            ReferenceIdentityProjectionPlansInDependencyOrder =
            [
                projectionTablePlan with
                {
                    BindingsInOrder = [.. bindings],
                },
            ],
        };
    }

    private static ResourceReadPlan CreateReadPlanWithSwappedReferenceProjectionBindings(
        ResourceReadPlan readPlan
    )
    {
        var projectionTablePlan = readPlan.ReferenceIdentityProjectionPlansInDependencyOrder.Single();
        var bindings = projectionTablePlan.BindingsInOrder.ToArray();
        var firstBinding = bindings[0];

        bindings[0] = bindings[1];
        bindings[1] = firstBinding;

        return readPlan with
        {
            ReferenceIdentityProjectionPlansInDependencyOrder =
            [
                projectionTablePlan with
                {
                    BindingsInOrder = [.. bindings],
                },
            ],
        };
    }

    private static ResourceReadPlan CreateReadPlanWithDuplicatedDescriptorProjectionSource(
        ResourceReadPlan readPlan,
        int sourceIndex,
        int targetIndex
    )
    {
        var descriptorProjectionPlan = readPlan.DescriptorProjectionPlansInOrder.Single();
        var sources = descriptorProjectionPlan.SourcesInOrder.ToArray();

        sources[targetIndex] = sources[sourceIndex];

        return readPlan with
        {
            DescriptorProjectionPlansInOrder =
            [
                descriptorProjectionPlan with
                {
                    SourcesInOrder = [.. sources],
                },
            ],
        };
    }

    private static ResourceReadPlan CreateReadPlanWithSwappedDescriptorProjectionSources(
        ResourceReadPlan readPlan
    )
    {
        var descriptorProjectionPlan = readPlan.DescriptorProjectionPlansInOrder.Single();
        var sources = descriptorProjectionPlan.SourcesInOrder.ToArray();
        var firstSource = sources[0];

        sources[0] = sources[1];
        sources[1] = firstSource;

        return readPlan with
        {
            DescriptorProjectionPlansInOrder =
            [
                descriptorProjectionPlan with
                {
                    SourcesInOrder = [.. sources],
                },
            ],
        };
    }

    private static RelationalResourceModel BuildFixtureResourceModel(
        QualifiedResourceName resourceName,
        SqlDialect dialect,
        bool reverseResourceSchemaOrder
    )
    {
        var modelSet = RuntimePlanFixtureModelSetBuilder.Build(
            RuntimePlanCompilationFixturePath,
            dialect,
            reverseResourceSchemaOrder
        );

        return modelSet
            .ConcreteResourcesInNameOrder.Single(resource => resource.ResourceKey.Resource == resourceName)
            .RelationalModel;
    }

    private void AssertReferenceIdentityProjectionPlan(ResourceReadPlan readPlan)
    {
        readPlan.ReferenceIdentityProjectionPlansInDependencyOrder.Should().ContainSingle();

        var tablePlan = readPlan.ReferenceIdentityProjectionPlansInDependencyOrder.Single();
        tablePlan.Table.Should().Be(_projectionResourceModel.Root.Table);
        tablePlan.BindingsInOrder.Should().ContainSingle();

        var binding = tablePlan.BindingsInOrder.Single();
        binding.IsIdentityComponent.Should().BeTrue();
        binding.ReferenceObjectPath.Canonical.Should().Be("$.schoolReference");
        binding.TargetResource.Should().Be(new QualifiedResourceName("Ed-Fi", "School"));
        binding.FkColumnOrdinal.Should().Be(2);
        binding
            .IdentityFieldOrdinalsInOrder.Select(static field => field.ReferenceJsonPath.Canonical)
            .Should()
            .Equal("$.schoolReference.schoolId", "$.schoolReference.schoolYear");
        binding.IdentityFieldOrdinalsInOrder.Select(static field => field.ColumnOrdinal).Should().Equal(3, 4);
    }

    private static void AssertReferenceIdentityProjectionBinding(
        ReferenceIdentityProjectionBinding binding,
        bool isIdentityComponent,
        string referenceObjectPath,
        QualifiedResourceName targetResource,
        int fkColumnOrdinal,
        params (string ReferenceJsonPath, int ColumnOrdinal)[] identityFieldOrdinals
    )
    {
        binding.IsIdentityComponent.Should().Be(isIdentityComponent);
        binding.ReferenceObjectPath.Canonical.Should().Be(referenceObjectPath);
        binding.TargetResource.Should().Be(targetResource);
        binding.FkColumnOrdinal.Should().Be(fkColumnOrdinal);
        binding
            .IdentityFieldOrdinalsInOrder.Select(static field => field.ReferenceJsonPath.Canonical)
            .Should()
            .Equal(identityFieldOrdinals.Select(static field => field.ReferenceJsonPath));
        binding
            .IdentityFieldOrdinalsInOrder.Select(static field => field.ColumnOrdinal)
            .Should()
            .Equal(identityFieldOrdinals.Select(static field => field.ColumnOrdinal));
    }

    private void AssertDescriptorProjectionPlan(ResourceReadPlan readPlan, string expectedSql)
    {
        readPlan.DescriptorProjectionPlansInOrder.Should().ContainSingle();

        var descriptorPlan = readPlan.DescriptorProjectionPlansInOrder.Single();
        descriptorPlan.SelectByKeysetSql.Should().Be(expectedSql);
        descriptorPlan.ResultShape.Should().Be(new DescriptorProjectionResultShape(0, 1));
        descriptorPlan.SourcesInOrder.Should().ContainSingle();

        var source = descriptorPlan.SourcesInOrder.Single();
        source.DescriptorValuePath.Canonical.Should().Be("$.academicSubjectDescriptor");
        source.Table.Should().Be(_projectionResourceModel.Root.Table);
        source
            .DescriptorResource.Should()
            .Be(new QualifiedResourceName("Ed-Fi", "AcademicSubjectDescriptor"));
        source.DescriptorIdColumnOrdinal.Should().Be(5);
    }

    private static void AssertKeyUnifiedDescriptorProjectionPlan(
        ResourceReadPlan readPlan,
        string expectedSql
    )
    {
        readPlan.DescriptorProjectionPlansInOrder.Should().ContainSingle();

        var descriptorPlan = readPlan.DescriptorProjectionPlansInOrder.Single();
        descriptorPlan.SelectByKeysetSql.Should().Be(expectedSql);
        descriptorPlan.ResultShape.Should().Be(new DescriptorProjectionResultShape(0, 1));
        descriptorPlan
            .SourcesInOrder.Select(static source => source.DescriptorValuePath.Canonical)
            .Should()
            .Equal("$.localSchoolYearTypeDescriptor", "$.schoolYearTypeDescriptor");
        descriptorPlan
            .SourcesInOrder.Select(static source => source.Table)
            .Should()
            .Equal(readPlan.Model.Root.Table, readPlan.Model.Root.Table);
        descriptorPlan
            .SourcesInOrder.Select(static source => source.DescriptorResource)
            .Should()
            .Equal(
                new QualifiedResourceName("Ed-Fi", "SchoolYearTypeDescriptor"),
                new QualifiedResourceName("Ed-Fi", "SchoolYearTypeDescriptor")
            );
        descriptorPlan
            .SourcesInOrder.Select(static source => source.DescriptorIdColumnOrdinal)
            .Should()
            .Equal(7, 6);
        descriptorPlan.SelectByKeysetSql.Should().NotContain("SchoolYearTypeDescriptorPrimary");
        descriptorPlan.SelectByKeysetSql.Should().NotContain("SchoolYearTypeDescriptorSecondary");
    }

    private static string CreateReadPlanFingerprint(ResourceReadPlan readPlan)
    {
        return NormalizedPlanDtoJson.EmitCanonicalJson(NormalizedPlanContractCodec.Encode(readPlan));
    }

    private static RelationalResourceModel CreateRootOnlyResourceModel()
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

    private static RelationalResourceModel CreateProjectionMetadataResourceModel()
    {
        var rootTable = new DbTableModel(
            Table: new DbTableName(new DbSchemaName("edfi"), "StudentProjection"),
            JsonScope: new JsonPathExpression("$", []),
            Key: new TableKey(
                ConstraintName: "PK_StudentProjection",
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
                    ColumnName: new DbColumnName("StudentUniqueId"),
                    Kind: ColumnKind.Scalar,
                    ScalarType: new RelationalScalarType(ScalarKind.String),
                    IsNullable: false,
                    SourceJsonPath: new JsonPathExpression(
                        "$.studentUniqueId",
                        [new JsonPathSegment.Property("studentUniqueId")]
                    ),
                    TargetResource: null
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("School_DocumentId"),
                    Kind: ColumnKind.DocumentFk,
                    ScalarType: new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: true,
                    SourceJsonPath: new JsonPathExpression(
                        "$.schoolReference",
                        [new JsonPathSegment.Property("schoolReference")]
                    ),
                    TargetResource: new QualifiedResourceName("Ed-Fi", "School")
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("School_RefSchoolId"),
                    Kind: ColumnKind.Scalar,
                    ScalarType: new RelationalScalarType(ScalarKind.Int32),
                    IsNullable: true,
                    SourceJsonPath: new JsonPathExpression(
                        "$.schoolReference.schoolId",
                        [
                            new JsonPathSegment.Property("schoolReference"),
                            new JsonPathSegment.Property("schoolId"),
                        ]
                    ),
                    TargetResource: null
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("School_RefSchoolYear"),
                    Kind: ColumnKind.Scalar,
                    ScalarType: new RelationalScalarType(ScalarKind.Int32),
                    IsNullable: true,
                    SourceJsonPath: new JsonPathExpression(
                        "$.schoolReference.schoolYear",
                        [
                            new JsonPathSegment.Property("schoolReference"),
                            new JsonPathSegment.Property("schoolYear"),
                        ]
                    ),
                    TargetResource: null
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("AcademicSubjectDescriptorId"),
                    Kind: ColumnKind.DescriptorFk,
                    ScalarType: new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: true,
                    SourceJsonPath: new JsonPathExpression(
                        "$.academicSubjectDescriptor",
                        [new JsonPathSegment.Property("academicSubjectDescriptor")]
                    ),
                    TargetResource: new QualifiedResourceName("Ed-Fi", "AcademicSubjectDescriptor")
                ),
            ],
            Constraints: []
        );

        return new RelationalResourceModel(
            Resource: new QualifiedResourceName("Ed-Fi", "StudentProjection"),
            PhysicalSchema: new DbSchemaName("edfi"),
            StorageKind: ResourceStorageKind.RelationalTables,
            Root: rootTable,
            TablesInDependencyOrder: [rootTable],
            DocumentReferenceBindings:
            [
                new DocumentReferenceBinding(
                    IsIdentityComponent: true,
                    ReferenceObjectPath: new JsonPathExpression(
                        "$.schoolReference",
                        [new JsonPathSegment.Property("schoolReference")]
                    ),
                    Table: rootTable.Table,
                    FkColumn: new DbColumnName("School_DocumentId"),
                    TargetResource: new QualifiedResourceName("Ed-Fi", "School"),
                    IdentityBindings:
                    [
                        new ReferenceIdentityBinding(
                            ReferenceJsonPath: new JsonPathExpression(
                                "$.schoolReference.schoolId",
                                [
                                    new JsonPathSegment.Property("schoolReference"),
                                    new JsonPathSegment.Property("schoolId"),
                                ]
                            ),
                            Column: new DbColumnName("School_RefSchoolId")
                        ),
                        new ReferenceIdentityBinding(
                            ReferenceJsonPath: new JsonPathExpression(
                                "$.schoolReference.schoolYear",
                                [
                                    new JsonPathSegment.Property("schoolReference"),
                                    new JsonPathSegment.Property("schoolYear"),
                                ]
                            ),
                            Column: new DbColumnName("School_RefSchoolYear")
                        ),
                    ]
                ),
            ],
            DescriptorEdgeSources:
            [
                new DescriptorEdgeSource(
                    IsIdentityComponent: false,
                    DescriptorValuePath: new JsonPathExpression(
                        "$.academicSubjectDescriptor",
                        [new JsonPathSegment.Property("academicSubjectDescriptor")]
                    ),
                    Table: rootTable.Table,
                    FkColumn: new DbColumnName("AcademicSubjectDescriptorId"),
                    DescriptorResource: new QualifiedResourceName("Ed-Fi", "AcademicSubjectDescriptor")
                ),
            ]
        );
    }

    private static RelationalResourceModel CreateKeyUnifiedDescriptorProjectionResourceModel()
    {
        var model = CreateRootOnlyModelWithReferenceSitePresence();

        return model with
        {
            DescriptorEdgeSources =
            [
                new DescriptorEdgeSource(
                    IsIdentityComponent: false,
                    DescriptorValuePath: new JsonPathExpression(
                        "$.localSchoolYearTypeDescriptor",
                        [new JsonPathSegment.Property("localSchoolYearTypeDescriptor")]
                    ),
                    Table: model.Root.Table,
                    FkColumn: new DbColumnName("SchoolYearTypeDescriptorSecondary"),
                    DescriptorResource: new QualifiedResourceName("Ed-Fi", "SchoolYearTypeDescriptor")
                ),
                new DescriptorEdgeSource(
                    IsIdentityComponent: false,
                    DescriptorValuePath: new JsonPathExpression(
                        "$.schoolYearTypeDescriptor",
                        [new JsonPathSegment.Property("schoolYearTypeDescriptor")]
                    ),
                    Table: model.Root.Table,
                    FkColumn: new DbColumnName("SchoolYearTypeDescriptorPrimary"),
                    DescriptorResource: new QualifiedResourceName("Ed-Fi", "SchoolYearTypeDescriptor")
                ),
            ],
        };
    }

    private static RelationalResourceModel CreateKeyUnifiedDescriptorProjectionResourceModelWithStoredDescriptorSource()
    {
        var model = CreateKeyUnifiedDescriptorProjectionResourceModel();
        var descriptorResource = new QualifiedResourceName("Ed-Fi", "ProgramTypeDescriptor");
        var programTypeDescriptorPath = CreatePath(
            "$.programTypeDescriptor",
            new JsonPathSegment.Property("programTypeDescriptor")
        );
        var rootTable = model.Root with
        {
            Columns =
            [
                .. model.Root.Columns,
                new DbColumnModel(
                    ColumnName: new DbColumnName("ProgramTypeDescriptorId"),
                    Kind: ColumnKind.DescriptorFk,
                    ScalarType: new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: true,
                    SourceJsonPath: programTypeDescriptorPath,
                    TargetResource: descriptorResource
                ),
            ],
        };

        return model with
        {
            Root = rootTable,
            TablesInDependencyOrder = [rootTable],
            DescriptorEdgeSources =
            [
                .. model.DescriptorEdgeSources,
                new DescriptorEdgeSource(
                    IsIdentityComponent: false,
                    DescriptorValuePath: programTypeDescriptorPath,
                    Table: rootTable.Table,
                    FkColumn: new DbColumnName("ProgramTypeDescriptorId"),
                    DescriptorResource: descriptorResource
                ),
            ],
        };
    }

    private static RelationalResourceModel CreateRootMultiBindingReferenceProjectionResourceModel()
    {
        var rootTable = new DbTableModel(
            Table: new DbTableName(new DbSchemaName("edfi"), "StudentReferenceProjection"),
            JsonScope: CreatePath("$"),
            Key: new TableKey(
                ConstraintName: "PK_StudentReferenceProjection",
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
                    ColumnName: new DbColumnName("School_DocumentId"),
                    Kind: ColumnKind.DocumentFk,
                    ScalarType: new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: true,
                    SourceJsonPath: CreatePath(
                        "$.schoolReference",
                        new JsonPathSegment.Property("schoolReference")
                    ),
                    TargetResource: new QualifiedResourceName("Ed-Fi", "School")
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("School_RefSchoolId"),
                    Kind: ColumnKind.Scalar,
                    ScalarType: new RelationalScalarType(ScalarKind.Int32),
                    IsNullable: true,
                    SourceJsonPath: CreatePath(
                        "$.schoolReference.schoolId",
                        new JsonPathSegment.Property("schoolReference"),
                        new JsonPathSegment.Property("schoolId")
                    ),
                    TargetResource: null
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("School_RefSchoolYear"),
                    Kind: ColumnKind.Scalar,
                    ScalarType: new RelationalScalarType(ScalarKind.Int32),
                    IsNullable: true,
                    SourceJsonPath: CreatePath(
                        "$.schoolReference.schoolYear",
                        new JsonPathSegment.Property("schoolReference"),
                        new JsonPathSegment.Property("schoolYear")
                    ),
                    TargetResource: null
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("Calendar_DocumentId"),
                    Kind: ColumnKind.DocumentFk,
                    ScalarType: new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: true,
                    SourceJsonPath: CreatePath(
                        "$.calendarReference",
                        new JsonPathSegment.Property("calendarReference")
                    ),
                    TargetResource: new QualifiedResourceName("Ed-Fi", "Calendar")
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("Calendar_RefCalendarCode"),
                    Kind: ColumnKind.Scalar,
                    ScalarType: new RelationalScalarType(ScalarKind.String, MaxLength: 50),
                    IsNullable: true,
                    SourceJsonPath: CreatePath(
                        "$.calendarReference.calendarCode",
                        new JsonPathSegment.Property("calendarReference"),
                        new JsonPathSegment.Property("calendarCode")
                    ),
                    TargetResource: null
                ),
            ],
            Constraints: []
        );

        return new RelationalResourceModel(
            Resource: new QualifiedResourceName("Ed-Fi", "StudentReferenceProjection"),
            PhysicalSchema: new DbSchemaName("edfi"),
            StorageKind: ResourceStorageKind.RelationalTables,
            Root: rootTable,
            TablesInDependencyOrder: [rootTable],
            DocumentReferenceBindings:
            [
                new DocumentReferenceBinding(
                    IsIdentityComponent: true,
                    ReferenceObjectPath: CreatePath(
                        "$.schoolReference",
                        new JsonPathSegment.Property("schoolReference")
                    ),
                    Table: rootTable.Table,
                    FkColumn: new DbColumnName("School_DocumentId"),
                    TargetResource: new QualifiedResourceName("Ed-Fi", "School"),
                    IdentityBindings:
                    [
                        new ReferenceIdentityBinding(
                            ReferenceJsonPath: CreatePath(
                                "$.schoolReference.schoolId",
                                new JsonPathSegment.Property("schoolReference"),
                                new JsonPathSegment.Property("schoolId")
                            ),
                            Column: new DbColumnName("School_RefSchoolId")
                        ),
                        new ReferenceIdentityBinding(
                            ReferenceJsonPath: CreatePath(
                                "$.schoolReference.schoolYear",
                                new JsonPathSegment.Property("schoolReference"),
                                new JsonPathSegment.Property("schoolYear")
                            ),
                            Column: new DbColumnName("School_RefSchoolYear")
                        ),
                    ]
                ),
                new DocumentReferenceBinding(
                    IsIdentityComponent: false,
                    ReferenceObjectPath: CreatePath(
                        "$.calendarReference",
                        new JsonPathSegment.Property("calendarReference")
                    ),
                    Table: rootTable.Table,
                    FkColumn: new DbColumnName("Calendar_DocumentId"),
                    TargetResource: new QualifiedResourceName("Ed-Fi", "Calendar"),
                    IdentityBindings:
                    [
                        new ReferenceIdentityBinding(
                            ReferenceJsonPath: CreatePath(
                                "$.calendarReference.calendarCode",
                                new JsonPathSegment.Property("calendarReference"),
                                new JsonPathSegment.Property("calendarCode")
                            ),
                            Column: new DbColumnName("Calendar_RefCalendarCode")
                        ),
                    ]
                ),
            ],
            DescriptorEdgeSources: []
        );
    }

    private static RelationalResourceModel CreateInterleavedReferenceProjectionResourceModel(
        bool rootBindingFirst
    )
    {
        var rootTable = new DbTableModel(
            Table: new DbTableName(new DbSchemaName("edfi"), "StudentReferenceGrouping"),
            JsonScope: CreatePath("$"),
            Key: new TableKey(
                ConstraintName: "PK_StudentReferenceGrouping",
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
                    ColumnName: new DbColumnName("School_DocumentId"),
                    Kind: ColumnKind.DocumentFk,
                    ScalarType: new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: true,
                    SourceJsonPath: CreatePath(
                        "$.schoolReference",
                        new JsonPathSegment.Property("schoolReference")
                    ),
                    TargetResource: new QualifiedResourceName("Ed-Fi", "School")
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("School_RefSchoolId"),
                    Kind: ColumnKind.Scalar,
                    ScalarType: new RelationalScalarType(ScalarKind.Int32),
                    IsNullable: true,
                    SourceJsonPath: CreatePath(
                        "$.schoolReference.schoolId",
                        new JsonPathSegment.Property("schoolReference"),
                        new JsonPathSegment.Property("schoolId")
                    ),
                    TargetResource: null
                ),
            ],
            Constraints: []
        );

        var childTable = new DbTableModel(
            Table: new DbTableName(new DbSchemaName("edfi"), "StudentAddressReferenceGrouping"),
            JsonScope: CreatePath(
                "$.addresses[*]",
                new JsonPathSegment.Property("addresses"),
                new JsonPathSegment.AnyArrayElement()
            ),
            Key: new TableKey(
                ConstraintName: "PK_StudentAddressReferenceGrouping",
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
                    ColumnName: new DbColumnName("Calendar_DocumentId"),
                    Kind: ColumnKind.DocumentFk,
                    ScalarType: new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: true,
                    SourceJsonPath: CreatePath(
                        "$.addresses[*].calendarReference",
                        new JsonPathSegment.Property("addresses"),
                        new JsonPathSegment.AnyArrayElement(),
                        new JsonPathSegment.Property("calendarReference")
                    ),
                    TargetResource: new QualifiedResourceName("Ed-Fi", "Calendar")
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("Calendar_RefCalendarCode"),
                    Kind: ColumnKind.Scalar,
                    ScalarType: new RelationalScalarType(ScalarKind.String, MaxLength: 50),
                    IsNullable: true,
                    SourceJsonPath: CreatePath(
                        "$.addresses[*].calendarReference.calendarCode",
                        new JsonPathSegment.Property("addresses"),
                        new JsonPathSegment.AnyArrayElement(),
                        new JsonPathSegment.Property("calendarReference"),
                        new JsonPathSegment.Property("calendarCode")
                    ),
                    TargetResource: null
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("Session_DocumentId"),
                    Kind: ColumnKind.DocumentFk,
                    ScalarType: new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: true,
                    SourceJsonPath: CreatePath(
                        "$.addresses[*].sessionReference",
                        new JsonPathSegment.Property("addresses"),
                        new JsonPathSegment.AnyArrayElement(),
                        new JsonPathSegment.Property("sessionReference")
                    ),
                    TargetResource: new QualifiedResourceName("Ed-Fi", "Session")
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("Session_RefSessionName"),
                    Kind: ColumnKind.Scalar,
                    ScalarType: new RelationalScalarType(ScalarKind.String, MaxLength: 128),
                    IsNullable: true,
                    SourceJsonPath: CreatePath(
                        "$.addresses[*].sessionReference.sessionName",
                        new JsonPathSegment.Property("addresses"),
                        new JsonPathSegment.AnyArrayElement(),
                        new JsonPathSegment.Property("sessionReference"),
                        new JsonPathSegment.Property("sessionName")
                    ),
                    TargetResource: null
                ),
            ],
            Constraints: []
        );

        var extensionTable = new DbTableModel(
            Table: new DbTableName(new DbSchemaName("sample"), "StudentReferenceGroupingExtension"),
            JsonScope: CreatePath(
                "$._ext.sample",
                new JsonPathSegment.Property("_ext"),
                new JsonPathSegment.Property("sample")
            ),
            Key: new TableKey(
                ConstraintName: "PK_StudentReferenceGroupingExtension",
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
                    ScalarType: new RelationalScalarType(ScalarKind.String, MaxLength: 32),
                    IsNullable: true,
                    SourceJsonPath: CreatePath(
                        "$._ext.sample.favoriteColor",
                        new JsonPathSegment.Property("_ext"),
                        new JsonPathSegment.Property("sample"),
                        new JsonPathSegment.Property("favoriteColor")
                    ),
                    TargetResource: null
                ),
            ],
            Constraints: []
        );

        var rootBinding = new DocumentReferenceBinding(
            IsIdentityComponent: true,
            ReferenceObjectPath: CreatePath(
                "$.schoolReference",
                new JsonPathSegment.Property("schoolReference")
            ),
            Table: rootTable.Table,
            FkColumn: new DbColumnName("School_DocumentId"),
            TargetResource: new QualifiedResourceName("Ed-Fi", "School"),
            IdentityBindings:
            [
                new ReferenceIdentityBinding(
                    ReferenceJsonPath: CreatePath(
                        "$.schoolReference.schoolId",
                        new JsonPathSegment.Property("schoolReference"),
                        new JsonPathSegment.Property("schoolId")
                    ),
                    Column: new DbColumnName("School_RefSchoolId")
                ),
            ]
        );
        var calendarBinding = new DocumentReferenceBinding(
            IsIdentityComponent: false,
            ReferenceObjectPath: CreatePath(
                "$.addresses[*].calendarReference",
                new JsonPathSegment.Property("addresses"),
                new JsonPathSegment.AnyArrayElement(),
                new JsonPathSegment.Property("calendarReference")
            ),
            Table: childTable.Table,
            FkColumn: new DbColumnName("Calendar_DocumentId"),
            TargetResource: new QualifiedResourceName("Ed-Fi", "Calendar"),
            IdentityBindings:
            [
                new ReferenceIdentityBinding(
                    ReferenceJsonPath: CreatePath(
                        "$.addresses[*].calendarReference.calendarCode",
                        new JsonPathSegment.Property("addresses"),
                        new JsonPathSegment.AnyArrayElement(),
                        new JsonPathSegment.Property("calendarReference"),
                        new JsonPathSegment.Property("calendarCode")
                    ),
                    Column: new DbColumnName("Calendar_RefCalendarCode")
                ),
            ]
        );
        var sessionBinding = new DocumentReferenceBinding(
            IsIdentityComponent: false,
            ReferenceObjectPath: CreatePath(
                "$.addresses[*].sessionReference",
                new JsonPathSegment.Property("addresses"),
                new JsonPathSegment.AnyArrayElement(),
                new JsonPathSegment.Property("sessionReference")
            ),
            Table: childTable.Table,
            FkColumn: new DbColumnName("Session_DocumentId"),
            TargetResource: new QualifiedResourceName("Ed-Fi", "Session"),
            IdentityBindings:
            [
                new ReferenceIdentityBinding(
                    ReferenceJsonPath: CreatePath(
                        "$.addresses[*].sessionReference.sessionName",
                        new JsonPathSegment.Property("addresses"),
                        new JsonPathSegment.AnyArrayElement(),
                        new JsonPathSegment.Property("sessionReference"),
                        new JsonPathSegment.Property("sessionName")
                    ),
                    Column: new DbColumnName("Session_RefSessionName")
                ),
            ]
        );

        var documentReferenceBindings = rootBindingFirst
            ? new[] { rootBinding, calendarBinding, sessionBinding }
            : new[] { calendarBinding, rootBinding, sessionBinding };

        return new RelationalResourceModel(
            Resource: new QualifiedResourceName("Ed-Fi", "StudentReferenceGrouping"),
            PhysicalSchema: new DbSchemaName("edfi"),
            StorageKind: ResourceStorageKind.RelationalTables,
            Root: rootTable,
            TablesInDependencyOrder: [rootTable, childTable, extensionTable],
            DocumentReferenceBindings: documentReferenceBindings,
            DescriptorEdgeSources: []
        );
    }

    private static RelationalResourceModel CreateKeyUnifiedReferenceProjectionResourceModel()
    {
        var schoolResource = new QualifiedResourceName("Ed-Fi", "School");
        var model = CreateRootOnlyModelWithReferenceGroupDocumentFkPresence(false);
        var primaryColumnName = new DbColumnName("SchoolYearPrimary");
        var secondaryColumnName = new DbColumnName("SchoolYearSecondary");

        DbColumnModel MapColumn(DbColumnModel column)
        {
            if (column.ColumnName.Equals(primaryColumnName))
            {
                return column with
                {
                    SourceJsonPath = CreatePath(
                        "$.schoolReference.schoolYear",
                        new JsonPathSegment.Property("schoolReference"),
                        new JsonPathSegment.Property("schoolYear")
                    ),
                };
            }

            if (column.ColumnName.Equals(secondaryColumnName))
            {
                return column with
                {
                    SourceJsonPath = CreatePath(
                        "$.schoolReference.localSchoolYear",
                        new JsonPathSegment.Property("schoolReference"),
                        new JsonPathSegment.Property("localSchoolYear")
                    ),
                };
            }

            return column;
        }

        var rootTable = model.Root with { Columns = model.Root.Columns.Select(MapColumn).ToArray() };

        return model with
        {
            Root = rootTable,
            TablesInDependencyOrder = [rootTable],
            DocumentReferenceBindings =
            [
                new DocumentReferenceBinding(
                    IsIdentityComponent: true,
                    ReferenceObjectPath: CreatePath(
                        "$.schoolReference",
                        new JsonPathSegment.Property("schoolReference")
                    ),
                    Table: rootTable.Table,
                    FkColumn: new DbColumnName("School_DocumentId"),
                    TargetResource: schoolResource,
                    IdentityBindings:
                    [
                        new ReferenceIdentityBinding(
                            ReferenceJsonPath: CreatePath(
                                "$.schoolReference.localSchoolYear",
                                new JsonPathSegment.Property("schoolReference"),
                                new JsonPathSegment.Property("localSchoolYear")
                            ),
                            Column: new DbColumnName("SchoolYearSecondary")
                        ),
                        new ReferenceIdentityBinding(
                            ReferenceJsonPath: CreatePath(
                                "$.schoolReference.schoolYear",
                                new JsonPathSegment.Property("schoolReference"),
                                new JsonPathSegment.Property("schoolYear")
                            ),
                            Column: new DbColumnName("SchoolYearPrimary")
                        ),
                    ]
                ),
            ],
        };
    }

    private static RelationalResourceModel CreateMultiTableResourceModel()
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
                    ColumnName: new DbColumnName("StudentUniqueId"),
                    Kind: ColumnKind.Scalar,
                    ScalarType: new RelationalScalarType(ScalarKind.String),
                    IsNullable: false,
                    SourceJsonPath: new JsonPathExpression(
                        "$.studentUniqueId",
                        [new JsonPathSegment.Property("studentUniqueId")]
                    ),
                    TargetResource: null
                ),
            ],
            Constraints: []
        );

        var childTable = new DbTableModel(
            Table: new DbTableName(new DbSchemaName("edfi"), "StudentAddress"),
            JsonScope: new JsonPathExpression(
                "$.addresses[*]",
                [new JsonPathSegment.Property("addresses"), new JsonPathSegment.AnyArrayElement()]
            ),
            Key: new TableKey(
                ConstraintName: "PK_StudentAddress",
                Columns:
                [
                    new DbKeyColumn(new DbColumnName("Student_DocumentId"), ColumnKind.ParentKeyPart),
                    new DbKeyColumn(new DbColumnName("Ordinal"), ColumnKind.Ordinal),
                ]
            ),
            Columns:
            [
                new DbColumnModel(
                    ColumnName: new DbColumnName("Student_DocumentId"),
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
                    IsNullable: false,
                    SourceJsonPath: new JsonPathExpression(
                        "$.addresses[*].city",
                        [
                            new JsonPathSegment.Property("addresses"),
                            new JsonPathSegment.AnyArrayElement(),
                            new JsonPathSegment.Property("city"),
                        ]
                    ),
                    TargetResource: null
                ),
            ],
            Constraints: []
        );

        var nestedTable = new DbTableModel(
            Table: new DbTableName(new DbSchemaName("edfi"), "StudentAddressPeriod"),
            JsonScope: new JsonPathExpression(
                "$.addresses[*].periods[*]",
                [
                    new JsonPathSegment.Property("addresses"),
                    new JsonPathSegment.AnyArrayElement(),
                    new JsonPathSegment.Property("periods"),
                    new JsonPathSegment.AnyArrayElement(),
                ]
            ),
            Key: new TableKey(
                ConstraintName: "PK_StudentAddressPeriod",
                Columns:
                [
                    new DbKeyColumn(new DbColumnName("Student_DocumentId"), ColumnKind.ParentKeyPart),
                    new DbKeyColumn(new DbColumnName("AddressOrdinal"), ColumnKind.ParentKeyPart),
                    new DbKeyColumn(new DbColumnName("Ordinal"), ColumnKind.Ordinal),
                ]
            ),
            Columns:
            [
                new DbColumnModel(
                    ColumnName: new DbColumnName("Student_DocumentId"),
                    Kind: ColumnKind.ParentKeyPart,
                    ScalarType: new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("AddressOrdinal"),
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
                    ColumnName: new DbColumnName("BeginDate"),
                    Kind: ColumnKind.Scalar,
                    ScalarType: new RelationalScalarType(ScalarKind.Date),
                    IsNullable: false,
                    SourceJsonPath: new JsonPathExpression(
                        "$.addresses[*].periods[*].beginDate",
                        [
                            new JsonPathSegment.Property("addresses"),
                            new JsonPathSegment.AnyArrayElement(),
                            new JsonPathSegment.Property("periods"),
                            new JsonPathSegment.AnyArrayElement(),
                            new JsonPathSegment.Property("beginDate"),
                        ]
                    ),
                    TargetResource: null
                ),
            ],
            Constraints: []
        );

        var extensionTable = new DbTableModel(
            Table: new DbTableName(new DbSchemaName("sample"), "StudentExtension"),
            JsonScope: new JsonPathExpression(
                "$._ext.sample",
                [new JsonPathSegment.Property("_ext"), new JsonPathSegment.Property("sample")]
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
                    SourceJsonPath: new JsonPathExpression(
                        "$._ext.sample.favoriteColor",
                        [
                            new JsonPathSegment.Property("_ext"),
                            new JsonPathSegment.Property("sample"),
                            new JsonPathSegment.Property("favoriteColor"),
                        ]
                    ),
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
            TablesInDependencyOrder: [rootTable, childTable, nestedTable, extensionTable],
            DocumentReferenceBindings: [],
            DescriptorEdgeSources: []
        );
    }

    private static RelationalResourceModel CreateModelWithNoTablesInDependencyOrder()
    {
        var model = CreateSupportedRootOnlyModel();

        return model with
        {
            TablesInDependencyOrder = [],
        };
    }

    private static RelationalResourceModel CreateModelWithNonRootResourceModelRoot()
    {
        var model = CreateSupportedRootOnlyModel();
        var rootTable = model.Root with
        {
            JsonScope = new JsonPathExpression("$.shadow", [new JsonPathSegment.Property("shadow")]),
        };

        return model with
        {
            Root = rootTable,
            TablesInDependencyOrder = [rootTable],
        };
    }

    private static RelationalResourceModel CreateModelWithUnsupportedKeyColumnKind()
    {
        var model = CreateSupportedRootOnlyModel();
        var rootTable = model.Root with
        {
            Key = new TableKey(
                ConstraintName: model.Root.Key.ConstraintName,
                Columns: [new DbKeyColumn(new DbColumnName("SchoolYear"), ColumnKind.Scalar)]
            ),
        };

        return model with
        {
            Root = rootTable,
            TablesInDependencyOrder = [rootTable],
        };
    }

    private static RelationalResourceModel CreateModelWithMissingDocumentReferenceFkColumn()
    {
        var model = CreateSingleTableModelCoveringWriteValueSourceKinds();
        var binding = model.DocumentReferenceBindings.Single() with
        {
            FkColumn = new DbColumnName("MissingSchool_DocumentId"),
        };

        return model with
        {
            DocumentReferenceBindings = [binding],
        };
    }

    private static RelationalResourceModel CreateModelWithMissingReferenceIdentityBindingColumn()
    {
        var model = CreateSingleTableModelCoveringWriteValueSourceKinds();
        var binding = model.DocumentReferenceBindings.Single() with
        {
            IdentityBindings =
            [
                new ReferenceIdentityBinding(
                    ReferenceJsonPath: CreatePath(
                        "$.addresses[*].schoolReference.schoolId",
                        new JsonPathSegment.Property("addresses"),
                        new JsonPathSegment.AnyArrayElement(),
                        new JsonPathSegment.Property("schoolReference"),
                        new JsonPathSegment.Property("schoolId")
                    ),
                    Column: new DbColumnName("MissingSchoolId")
                ),
            ],
        };

        return model with
        {
            DocumentReferenceBindings = [binding],
        };
    }

    private static RelationalResourceModel CreateModelWithMissingDocumentReferenceBindingTable()
    {
        var model = CreateSingleTableModelCoveringWriteValueSourceKinds();
        var binding = model.DocumentReferenceBindings.Single() with
        {
            Table = new DbTableName(new DbSchemaName("edfi"), "MissingStudentAddress"),
        };

        return model with
        {
            DocumentReferenceBindings = [binding],
        };
    }

    private static RelationalResourceModel CreateModelWithMismatchedDocumentReferenceFkSourcePath()
    {
        var model = CreateProjectionMetadataResourceModel();
        var fkColumn = new DbColumnName("School_DocumentId");
        var rootTable = model.Root with
        {
            Columns = model
                .Root.Columns.Select(column =>
                    column.ColumnName.Equals(fkColumn)
                        ? column with
                        {
                            SourceJsonPath = CreatePath(
                                "$.studentUniqueId",
                                new JsonPathSegment.Property("studentUniqueId")
                            ),
                        }
                        : column
                )
                .ToArray(),
        };

        return model with
        {
            Root = rootTable,
            TablesInDependencyOrder = [rootTable],
        };
    }

    private static RelationalResourceModel CreateModelWithNonDocumentReferenceFkKind()
    {
        var model = CreateProjectionMetadataResourceModel();
        var fkColumn = new DbColumnName("School_DocumentId");
        var rootTable = model.Root with
        {
            Columns = model
                .Root.Columns.Select(column =>
                    column.ColumnName.Equals(fkColumn) ? column with { Kind = ColumnKind.Scalar } : column
                )
                .ToArray(),
        };

        return model with
        {
            Root = rootTable,
            TablesInDependencyOrder = [rootTable],
        };
    }

    private static RelationalResourceModel CreateModelWithNonStoredDocumentReferenceFkColumn()
    {
        var model = CreateProjectionMetadataResourceModel();
        var fkColumn = new DbColumnName("School_DocumentId");
        var rootTable = model.Root with
        {
            Columns = model
                .Root.Columns.Select(column =>
                    column.ColumnName.Equals(fkColumn)
                        ? column with
                        {
                            Storage = new ColumnStorage.UnifiedAlias(
                                CanonicalColumn: new DbColumnName("DocumentId"),
                                PresenceColumn: null
                            ),
                        }
                        : column
                )
                .ToArray(),
        };

        return model with
        {
            Root = rootTable,
            TablesInDependencyOrder = [rootTable],
        };
    }

    private static RelationalResourceModel CreateModelWithMissingDescriptorEdgeFkColumn()
    {
        var model = CreateSingleTableModelCoveringWriteValueSourceKinds();
        var edgeSource = model.DescriptorEdgeSources.Single() with
        {
            FkColumn = new DbColumnName("MissingProgramTypeDescriptorId"),
        };

        return model with
        {
            DescriptorEdgeSources = [edgeSource],
        };
    }

    private static RelationalResourceModel CreateModelWithMissingDescriptorEdgeTable()
    {
        var model = CreateSingleTableModelCoveringWriteValueSourceKinds();
        var edgeSource = model.DescriptorEdgeSources.Single() with
        {
            Table = new DbTableName(new DbSchemaName("edfi"), "MissingStudentAddress"),
        };

        return model with
        {
            DescriptorEdgeSources = [edgeSource],
        };
    }

    private static RelationalResourceModel CreateModelWithNonDescriptorEdgeFkKind()
    {
        var model = CreateProjectionMetadataResourceModel();
        var descriptorColumnName = new DbColumnName("AcademicSubjectDescriptorId");
        var rootTable = model.Root with
        {
            Columns = model
                .Root.Columns.Select(column =>
                    column.ColumnName.Equals(descriptorColumnName)
                        ? column with
                        {
                            Kind = ColumnKind.Scalar,
                        }
                        : column
                )
                .ToArray(),
        };

        return model with
        {
            Root = rootTable,
            TablesInDependencyOrder = [rootTable],
        };
    }

    private static RelationalResourceModel CreateKeyUnifiedModelWithCanonicalReferenceIdentityBindingColumn()
    {
        var model = CreateKeyUnifiedReferenceProjectionResourceModel();
        var binding = model.DocumentReferenceBindings.Single();

        return model with
        {
            DocumentReferenceBindings =
            [
                binding with
                {
                    IdentityBindings =
                    [
                        new ReferenceIdentityBinding(
                            ReferenceJsonPath: CreatePath(
                                "$.schoolReference.localSchoolYear",
                                new JsonPathSegment.Property("schoolReference"),
                                new JsonPathSegment.Property("localSchoolYear")
                            ),
                            Column: new DbColumnName("SchoolYearCanonical")
                        ),
                        .. binding.IdentityBindings.Skip(1),
                    ],
                },
            ],
        };
    }

    private static RelationalResourceModel CreateKeyUnifiedModelWithCanonicalDescriptorEdgeBindingColumn()
    {
        var model = CreateKeyUnifiedDescriptorProjectionResourceModel();
        var localDescriptorEdge = model.DescriptorEdgeSources[0];

        return model with
        {
            DescriptorEdgeSources =
            [
                localDescriptorEdge with
                {
                    FkColumn = new DbColumnName("SchoolYearTypeDescriptorIdCanonical"),
                },
                .. model.DescriptorEdgeSources.Skip(1),
            ],
        };
    }

    private static IReadOnlyDictionary<
        DbTableName,
        IReadOnlyDictionary<DbColumnName, int>
    > CreateHydrationColumnOrdinalsExcludingStorageOnlyColumns(DbTableModel tableModel)
    {
        Dictionary<DbColumnName, int> columnOrdinals = [];
        var ordinal = 0;

        foreach (var column in tableModel.Columns)
        {
            if (column.Kind is ColumnKind.ParentKeyPart || column.SourceJsonPath is not null)
            {
                columnOrdinals.Add(column.ColumnName, ordinal);
                ordinal++;
            }
        }

        return new Dictionary<DbTableName, IReadOnlyDictionary<DbColumnName, int>>
        {
            [tableModel.Table] = columnOrdinals,
        };
    }

    private static IReadOnlyList<DescriptorProjectionPlan> CompileDescriptorProjectionPlans(
        RelationalResourceModel model,
        SqlDialect dialect,
        IReadOnlyDictionary<DbTableName, IReadOnlyDictionary<DbColumnName, int>> columnOrdinalsByTable
    )
    {
        var compilerType = typeof(ReadPlanCompiler).Assembly.GetType(
            "EdFi.DataManagementService.Backend.Plans.DescriptorProjectionPlanCompiler",
            throwOnError: true
        )!;
        var compiler = Activator.CreateInstance(compilerType, dialect)!;
        var compileMethod = compilerType.GetMethod(nameof(ReadPlanCompiler.Compile))!;

        return (IReadOnlyList<DescriptorProjectionPlan>)
            compileMethod.Invoke(
                compiler,
                [
                    model,
                    KeysetTableConventions.GetKeysetTableContract(dialect),
                    new Dictionary<DbTableName, DbTableModel> { [model.Root.Table] = model.Root },
                    columnOrdinalsByTable,
                ]
            )!;
    }
}

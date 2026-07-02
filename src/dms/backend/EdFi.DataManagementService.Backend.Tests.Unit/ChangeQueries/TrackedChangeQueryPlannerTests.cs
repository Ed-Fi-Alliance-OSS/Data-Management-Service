// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.RegularExpressions;
using EdFi.DataManagementService.Backend.ChangeQueries;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.External.Model;
using FakeItEasy;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit.ChangeQueries;

[TestFixture]
[Parallelizable]
public class Given_TrackedChangeQueryPlanner
{
    private static readonly DbSchemaName _dmsSchema = new("dms");
    private static readonly DbSchemaName _sourceSchema = new("edfi");
    private static readonly DbSchemaName _trackedSchema = new("tracked_changes_edfi");
    private static readonly DbTableName _descriptorTable = new(_dmsSchema, "Descriptor");
    private static readonly DbTableName _sourceTable = new(_sourceSchema, "School");
    private static readonly QualifiedResourceName _schoolResource = new("Ed-Fi", "School");
    private static readonly QualifiedResourceName _programTypeDescriptorResource = new(
        "Ed-Fi",
        "ProgramTypeDescriptor"
    );

    [Test]
    public void It_finds_required_system_columns_by_role()
    {
        var idColumn = SystemColumn(TrackedChangeSystemColumnRole.Id, "Id");
        var changeVersionColumn = SystemColumn(TrackedChangeSystemColumnRole.ChangeVersion, "ChangeVersion");
        var createdAtColumn = SystemColumn(TrackedChangeSystemColumnRole.CreatedAt, "CreatedAt");
        var table = CreateTrackedChangeTable(
            TrackedChangeTableKind.Resource,
            systemColumns: [idColumn, changeVersionColumn, createdAtColumn]
        );

        TrackedChangeSystemColumnInfo result = TrackedChangeQueryPlanner.RequireSystemColumn(
            table,
            TrackedChangeSystemColumnRole.ChangeVersion
        );

        result.Should().BeSameAs(changeVersionColumn);
    }

    [Test]
    public void It_uses_first_identity_new_column_as_the_tombstone_or_keychange_representative()
    {
        var securableOnlyColumn = ValueColumn(
            "EducationOrganizationId",
            "$.educationOrganizationReference.educationOrganizationId",
            TrackedChangeColumnRole.Scalar,
            TrackedChangeColumnOrigin.SecurableElement
        );
        var personDocumentIdColumn = ValueColumn(
            "Student_DocumentId",
            "$.studentReference.studentUniqueId",
            TrackedChangeColumnRole.PersonDocumentId,
            TrackedChangeColumnOrigin.Identity,
            personJoinName: "Student"
        );
        var schoolIdColumn = ValueColumn(
            "SchoolId",
            "$.schoolId",
            TrackedChangeColumnRole.Scalar,
            TrackedChangeColumnOrigin.Identity
        );
        var nameColumn = ValueColumn(
            "NameOfInstitution",
            "$.nameOfInstitution",
            TrackedChangeColumnRole.Scalar,
            TrackedChangeColumnOrigin.Identity
        );
        var table = CreateTrackedChangeTable(
            TrackedChangeTableKind.Resource,
            [securableOnlyColumn, personDocumentIdColumn, schoolIdColumn, nameColumn]
        );

        TrackedChangeColumnInfo result = TrackedChangeQueryPlanner.RequireRepresentativeIdentityColumn(table);

        result.Should().BeSameAs(schoolIdColumn);
        result.NewColumnName.Should().Be(new DbColumnName("NewSchoolId"));
    }

    [Test]
    public void It_marks_keychanges_empty_for_shared_descriptor()
    {
        AssertEmptyKeyChangesPlan(
            TrackedChangeTableKind.SharedDescriptor,
            totalCount: true,
            expectedTotalCount: 0
        );
        AssertEmptyKeyChangesPlan(
            TrackedChangeTableKind.SharedDescriptor,
            totalCount: false,
            expectedTotalCount: null
        );
    }

    [Test]
    public void It_marks_keychanges_empty_for_concrete_abstract()
    {
        AssertEmptyKeyChangesPlan(
            TrackedChangeTableKind.ConcreteAbstract,
            totalCount: true,
            expectedTotalCount: 0
        );
        AssertEmptyKeyChangesPlan(
            TrackedChangeTableKind.ConcreteAbstract,
            totalCount: false,
            expectedTotalCount: null
        );
    }

    [Test]
    public void It_builds_keychanges_sql_with_representative_new_value_predicate()
    {
        TrackedChangeColumnInfo schoolIdColumn = ValueColumn(
            "SchoolId",
            "$.schoolId",
            TrackedChangeColumnRole.Scalar,
            TrackedChangeColumnOrigin.Identity,
            canonicalStorageColumn: new DbColumnName("SchoolId")
        );
        ChangeQueryResponseField[] fields = [ScalarField("schoolId", schoolIdColumn)];
        TrackedChangeTableInfo table = CreateTrackedChangeTable(
            TrackedChangeTableKind.Resource,
            [schoolIdColumn]
        );
        IRelationalTrackedChangeQueryRequest request = CreateRequest(
            ChangeQueryEndpointOperation.KeyChanges,
            totalCount: false,
            trackedChangeTable: table,
            paginationParameters: new PaginationParameters(
                Limit: 15,
                Offset: 30,
                TotalCount: false,
                MaximumPageSize: 500
            ),
            changeVersionRange: new ChangeVersionRange(10L, 20L),
            resourceModel: CreateRegularResourceModel(RootColumn("SchoolId", "$.schoolId"))
        );
        var sut = new TrackedChangeQueryPlanner(SqlDialect.Pgsql);

        TrackedChangeQueryPlan plan = sut.Plan(request, fields, TrackedChangeAuthorizationSql.None);

        plan.Command.Should().NotBeNull();
        RelationalCommand command = plan.Command!;
        string sql = NormalizeSql(command.CommandText);
        plan.Fields.Should().Equal(fields);
        plan.IncludesTotalCount.Should().BeFalse();
        plan.IsEmpty.Should().BeFalse();
        plan.TotalCount.Should().BeNull();
        sql.Should().StartWith("WITH FilteredChanges AS");
        sql.Should().Contain("FROM \"tracked_changes_edfi\".\"School\" c");
        sql.Should().Contain("c.\"NewSchoolId\" IS NOT NULL");
        sql.Should().Contain("c.\"ChangeVersion\" >= @MinChangeVersion");
        sql.Should().Contain("c.\"ChangeVersion\" <= @MaxChangeVersion");
        sql.Should().NotContain("@MinChangeVersion IS NULL");
        sql.Should().NotContain("@MaxChangeVersion IS NULL");
        sql.Should().Contain("MIN(c.\"ChangeVersion\") AS \"__FirstChangeVersion\"");
        sql.Should().Contain("MAX(c.\"ChangeVersion\") AS \"__LastChangeVersion\"");
        sql.Should().Contain("GROUP BY c.\"Id\"");
        sql.Should().Contain("FROM ChangeWindow w");
        sql.Should().Contain("JOIN FilteredChanges firstChange");
        sql.Should().Contain("JOIN FilteredChanges lastChange");
        sql.Should().Contain("firstChange.\"Id\" AS \"__Id\"");
        sql.Should().Contain("w.\"__LastChangeVersion\" AS \"__ChangeVersion\"");
        sql.Should().Contain("firstChange.\"OldSchoolId\" AS \"schoolId__old\"");
        sql.Should().Contain("lastChange.\"NewSchoolId\" AS \"schoolId__new\"");
        sql.Should().Contain("ORDER BY w.\"__LastChangeVersion\" ASC");
        sql.Should().Contain("LIMIT @Limit OFFSET @Offset");
        AssertParameter(command, "@MinChangeVersion", 10L);
        AssertParameter(command, "@MaxChangeVersion", 20L);
        AssertParameter(command, "@Limit", 15L);
        AssertParameter(command, "@Offset", 30L);
    }

    [Test]
    public void It_collapses_multiple_keychanges_for_same_id_to_earliest_old_and_latest_new()
    {
        TrackedChangeColumnInfo schoolIdColumn = ValueColumn(
            "SchoolId",
            "$.schoolId",
            TrackedChangeColumnRole.Scalar,
            TrackedChangeColumnOrigin.Identity,
            canonicalStorageColumn: new DbColumnName("SchoolId")
        );
        TrackedChangeColumnInfo nameColumn = ValueColumn(
            "NameOfInstitution",
            "$.nameOfInstitution",
            TrackedChangeColumnRole.Scalar,
            TrackedChangeColumnOrigin.Identity,
            canonicalStorageColumn: new DbColumnName("NameOfInstitution")
        );
        ChangeQueryResponseField[] fields =
        [
            ScalarField("schoolId", schoolIdColumn),
            ScalarField("nameOfInstitution", nameColumn),
        ];
        TrackedChangeTableInfo table = CreateTrackedChangeTable(
            TrackedChangeTableKind.Resource,
            [schoolIdColumn, nameColumn]
        );
        IRelationalTrackedChangeQueryRequest request = CreateRequest(
            ChangeQueryEndpointOperation.KeyChanges,
            totalCount: false,
            trackedChangeTable: table,
            resourceModel: CreateRegularResourceModel(
                RootColumn("SchoolId", "$.schoolId"),
                RootColumn("NameOfInstitution", "$.nameOfInstitution")
            )
        );
        var sut = new TrackedChangeQueryPlanner(SqlDialect.Pgsql);

        TrackedChangeQueryPlan plan = sut.Plan(request, fields, TrackedChangeAuthorizationSql.None);

        plan.Command.Should().NotBeNull();
        string sql = NormalizeSql(plan.Command!.CommandText);
        sql.Should()
            .Contain(
                "JOIN FilteredChanges firstChange ON firstChange.\"Id\" = w.\"Id\" AND firstChange.\"ChangeVersion\" = w.\"__FirstChangeVersion\""
            );
        sql.Should()
            .Contain(
                "JOIN FilteredChanges lastChange ON lastChange.\"Id\" = w.\"Id\" AND lastChange.\"ChangeVersion\" = w.\"__LastChangeVersion\""
            );
        sql.Should().Contain("firstChange.\"OldSchoolId\" AS \"schoolId__old\"");
        sql.Should().Contain("lastChange.\"NewSchoolId\" AS \"schoolId__new\"");
        sql.Should().Contain("firstChange.\"OldNameOfInstitution\" AS \"nameOfInstitution__old\"");
        sql.Should().Contain("lastChange.\"NewNameOfInstitution\" AS \"nameOfInstitution__new\"");
    }

    [Test]
    public void It_counts_keychange_groups_when_total_count_is_requested()
    {
        TrackedChangeColumnInfo schoolIdColumn = ValueColumn(
            "SchoolId",
            "$.schoolId",
            TrackedChangeColumnRole.Scalar,
            TrackedChangeColumnOrigin.Identity,
            canonicalStorageColumn: new DbColumnName("SchoolId")
        );
        ChangeQueryResponseField[] fields = [ScalarField("schoolId", schoolIdColumn)];
        TrackedChangeTableInfo table = CreateTrackedChangeTable(
            TrackedChangeTableKind.Resource,
            [schoolIdColumn]
        );
        IRelationalTrackedChangeQueryRequest request = CreateRequest(
            ChangeQueryEndpointOperation.KeyChanges,
            totalCount: true,
            trackedChangeTable: table,
            resourceModel: CreateRegularResourceModel(RootColumn("SchoolId", "$.schoolId"))
        );
        var sut = new TrackedChangeQueryPlanner(SqlDialect.Pgsql);

        TrackedChangeQueryPlan plan = sut.Plan(request, fields, TrackedChangeAuthorizationSql.None);

        plan.Command.Should().NotBeNull();
        RelationalCommand command = plan.Command!;
        string sql = NormalizeSql(command.CommandText);
        plan.IncludesTotalCount.Should().BeTrue();
        sql.Should().StartWith("WITH FilteredChanges AS");
        sql.Should().Contain("SELECT COUNT(1) AS \"__TotalCount\" FROM ChangeWindow;");
        sql.Should().Contain("; WITH FilteredChanges AS");
        sql.IndexOf("SELECT COUNT(1) AS \"__TotalCount\"", StringComparison.Ordinal)
            .Should()
            .BeLessThan(sql.IndexOf("firstChange.\"Id\" AS \"__Id\"", StringComparison.Ordinal));
        sql.Should().Contain("GROUP BY c.\"Id\"");
        sql.Should().NotContain("@MinChangeVersion");
        sql.Should().NotContain("@MaxChangeVersion");
        AssertNoParameter(command, "@MinChangeVersion");
        AssertNoParameter(command, "@MaxChangeVersion");
        AssertParameter(command, "@Limit", 25L);
        AssertParameter(command, "@Offset", 0L);
    }

    [Test]
    public void It_projects_descriptor_fields_for_keychanges()
    {
        TrackedChangeColumnInfo descriptorNamespaceColumn = ValueColumn(
            "ProgramTypeDescriptor_Namespace",
            "$.programTypeDescriptor",
            TrackedChangeColumnRole.DescriptorNamespace,
            TrackedChangeColumnOrigin.Identity,
            descriptorJoinName: "ProgramTypeDescriptor"
        );
        TrackedChangeColumnInfo descriptorCodeValueColumn = ValueColumn(
            "ProgramTypeDescriptor_CodeValue",
            "$.programTypeDescriptor",
            TrackedChangeColumnRole.DescriptorCodeValue,
            TrackedChangeColumnOrigin.Identity,
            descriptorJoinName: "ProgramTypeDescriptor"
        );
        ChangeQueryResponseField[] fields =
        [
            DescriptorField("programTypeDescriptor", descriptorNamespaceColumn, descriptorCodeValueColumn),
        ];
        TrackedChangeTableInfo table = CreateTrackedChangeTable(
            TrackedChangeTableKind.Resource,
            [descriptorNamespaceColumn, descriptorCodeValueColumn]
        );
        IRelationalTrackedChangeQueryRequest request = CreateRequest(
            ChangeQueryEndpointOperation.KeyChanges,
            totalCount: false,
            trackedChangeTable: table,
            resourceModel: CreateRegularResourceModel(
                [RootColumn("ProgramTypeDescriptorId", "$.programTypeDescriptor", ColumnKind.DescriptorFk)],
                descriptorEdgeSources:
                [
                    new DescriptorEdgeSource(
                        IsIdentityComponent: true,
                        DescriptorValuePath: Path("$.programTypeDescriptor"),
                        Table: _sourceTable,
                        FkColumn: new DbColumnName("ProgramTypeDescriptorId"),
                        DescriptorResource: _programTypeDescriptorResource
                    ),
                ]
            )
        );
        var sut = new TrackedChangeQueryPlanner(SqlDialect.Pgsql);

        TrackedChangeQueryPlan plan = sut.Plan(request, fields, TrackedChangeAuthorizationSql.None);

        plan.Command.Should().NotBeNull();
        string sql = NormalizeSql(plan.Command!.CommandText);
        sql.Should().Contain("c.\"NewProgramTypeDescriptor_Namespace\" IS NOT NULL");
        sql.Should()
            .Contain("firstChange.\"OldProgramTypeDescriptor_Namespace\" AS \"programTypeDescriptor__old\"");
        sql.Should()
            .Contain(
                "firstChange.\"OldProgramTypeDescriptor_CodeValue\" AS \"programTypeDescriptor__oldCodeValue\""
            );
        sql.Should()
            .Contain("lastChange.\"NewProgramTypeDescriptor_Namespace\" AS \"programTypeDescriptor__new\"");
        sql.Should()
            .Contain(
                "lastChange.\"NewProgramTypeDescriptor_CodeValue\" AS \"programTypeDescriptor__newCodeValue\""
            );
    }

    [Test]
    public void It_uses_sql_server_quoting_and_paging_for_keychanges()
    {
        TrackedChangeColumnInfo schoolIdColumn = ValueColumn(
            "SchoolId",
            "$.schoolId",
            TrackedChangeColumnRole.Scalar,
            TrackedChangeColumnOrigin.Identity,
            canonicalStorageColumn: new DbColumnName("SchoolId")
        );
        ChangeQueryResponseField[] fields = [ScalarField("schoolId", schoolIdColumn)];
        TrackedChangeTableInfo table = CreateTrackedChangeTable(
            TrackedChangeTableKind.Resource,
            [schoolIdColumn]
        );
        IRelationalTrackedChangeQueryRequest request = CreateRequest(
            ChangeQueryEndpointOperation.KeyChanges,
            totalCount: false,
            trackedChangeTable: table,
            resourceModel: CreateRegularResourceModel(RootColumn("SchoolId", "$.schoolId"))
        );
        var sut = new TrackedChangeQueryPlanner(SqlDialect.Mssql);

        TrackedChangeQueryPlan plan = sut.Plan(request, fields, TrackedChangeAuthorizationSql.None);

        plan.Command.Should().NotBeNull();
        string sql = NormalizeSql(plan.Command!.CommandText);
        sql.Should().Contain("FROM [tracked_changes_edfi].[School] c");
        sql.Should().Contain("c.[NewSchoolId] IS NOT NULL");
        sql.Should().Contain("MIN(c.[ChangeVersion]) AS [__FirstChangeVersion]");
        sql.Should().Contain("MAX(c.[ChangeVersion]) AS [__LastChangeVersion]");
        sql.Should().Contain("GROUP BY c.[Id]");
        sql.Should()
            .Contain(
                "JOIN FilteredChanges firstChange ON firstChange.[Id] = w.[Id] AND firstChange.[ChangeVersion] = w.[__FirstChangeVersion]"
            );
        sql.Should().Contain("firstChange.[OldSchoolId] AS [schoolId__old]");
        sql.Should().Contain("lastChange.[NewSchoolId] AS [schoolId__new]");
        sql.Should()
            .Contain("ORDER BY w.[__LastChangeVersion] ASC OFFSET @Offset ROWS FETCH NEXT @Limit ROWS ONLY");
    }

    [Test]
    public void It_builds_delete_sql_with_tombstone_predicate_version_window_paging_and_total_count()
    {
        TrackedChangeColumnInfo schoolIdColumn = ValueColumn(
            "SchoolId",
            "$.schoolId",
            TrackedChangeColumnRole.Scalar,
            TrackedChangeColumnOrigin.Identity,
            canonicalStorageColumn: new DbColumnName("SchoolId")
        );
        ChangeQueryResponseField[] fields = [ScalarField("schoolId", schoolIdColumn)];
        TrackedChangeTableInfo table = CreateTrackedChangeTable(
            TrackedChangeTableKind.Resource,
            [schoolIdColumn]
        );
        IRelationalTrackedChangeQueryRequest request = CreateRequest(
            ChangeQueryEndpointOperation.Deletes,
            totalCount: true,
            trackedChangeTable: table,
            paginationParameters: new PaginationParameters(
                Limit: 15,
                Offset: 30,
                TotalCount: true,
                MaximumPageSize: 500
            ),
            changeVersionRange: new ChangeVersionRange(10L, 20L),
            resourceModel: CreateRegularResourceModel(RootColumn("SchoolId", "$.schoolId"))
        );
        var sut = new TrackedChangeQueryPlanner(SqlDialect.Pgsql);

        TrackedChangeQueryPlan plan = sut.Plan(request, fields, TrackedChangeAuthorizationSql.None);

        plan.Command.Should().NotBeNull();
        RelationalCommand command = plan.Command!;
        string sql = NormalizeSql(command.CommandText);
        plan.Fields.Should().Equal(fields);
        plan.IncludesTotalCount.Should().BeTrue();
        plan.IsEmpty.Should().BeFalse();
        plan.TotalCount.Should().BeNull();
        sql.Should().StartWith("SELECT COUNT(1) AS \"__TotalCount\"");
        sql.Should().Contain("FROM \"tracked_changes_edfi\".\"School\" c");
        sql.Should().Contain("c.\"NewSchoolId\" IS NULL");
        sql.Should().Contain("c.\"ChangeVersion\" >= @MinChangeVersion");
        sql.Should().Contain("c.\"ChangeVersion\" <= @MaxChangeVersion");
        sql.Should().NotContain("@MinChangeVersion IS NULL");
        sql.Should().NotContain("@MaxChangeVersion IS NULL");
        sql.Should().Contain("c.\"Id\" AS \"__Id\"");
        sql.Should().Contain("c.\"ChangeVersion\" AS \"__ChangeVersion\"");
        sql.Should().Contain("c.\"OldSchoolId\" AS \"schoolId__old\"");
        sql.Should().Contain("ORDER BY c.\"ChangeVersion\" ASC");
        sql.Should().Contain("LIMIT @Limit OFFSET @Offset");
        sql.IndexOf("SELECT COUNT(1) AS \"__TotalCount\"", StringComparison.Ordinal)
            .Should()
            .BeLessThan(sql.IndexOf("c.\"Id\" AS \"__Id\"", StringComparison.Ordinal));
        AssertParameter(command, "@MinChangeVersion", 10L);
        AssertParameter(command, "@MaxChangeVersion", 20L);
        AssertParameter(command, "@Limit", 15L);
        AssertParameter(command, "@Offset", 30L);
    }

    [Test]
    public void It_builds_delete_sql_without_total_count_when_not_requested()
    {
        TrackedChangeColumnInfo schoolIdColumn = ValueColumn(
            "SchoolId",
            "$.schoolId",
            TrackedChangeColumnRole.Scalar,
            TrackedChangeColumnOrigin.Identity,
            canonicalStorageColumn: new DbColumnName("SchoolId")
        );
        ChangeQueryResponseField[] fields = [ScalarField("schoolId", schoolIdColumn)];
        TrackedChangeTableInfo table = CreateTrackedChangeTable(
            TrackedChangeTableKind.Resource,
            [schoolIdColumn]
        );
        IRelationalTrackedChangeQueryRequest request = CreateRequest(
            ChangeQueryEndpointOperation.Deletes,
            totalCount: false,
            trackedChangeTable: table,
            resourceModel: CreateRegularResourceModel(RootColumn("SchoolId", "$.schoolId"))
        );
        var sut = new TrackedChangeQueryPlanner(SqlDialect.Pgsql);

        TrackedChangeQueryPlan plan = sut.Plan(request, fields, TrackedChangeAuthorizationSql.None);

        plan.Command.Should().NotBeNull();
        RelationalCommand command = plan.Command!;
        string sql = NormalizeSql(command.CommandText);
        plan.IncludesTotalCount.Should().BeFalse();
        sql.Should().StartWith("SELECT c.\"Id\" AS \"__Id\"");
        sql.Should().NotContain("__TotalCount");
        sql.Should().NotContain("@MinChangeVersion");
        sql.Should().NotContain("@MaxChangeVersion");
        AssertNoParameter(command, "@MinChangeVersion");
        AssertNoParameter(command, "@MaxChangeVersion");
        AssertParameter(command, "@Limit", 25L);
        AssertParameter(command, "@Offset", 0L);
    }

    [Test]
    public void It_omits_max_change_version_from_deletes_when_only_min_is_supplied()
    {
        TrackedChangeColumnInfo schoolIdColumn = ValueColumn(
            "SchoolId",
            "$.schoolId",
            TrackedChangeColumnRole.Scalar,
            TrackedChangeColumnOrigin.Identity,
            canonicalStorageColumn: new DbColumnName("SchoolId")
        );
        ChangeQueryResponseField[] fields = [ScalarField("schoolId", schoolIdColumn)];
        TrackedChangeTableInfo table = CreateTrackedChangeTable(
            TrackedChangeTableKind.Resource,
            [schoolIdColumn]
        );
        IRelationalTrackedChangeQueryRequest request = CreateRequest(
            ChangeQueryEndpointOperation.Deletes,
            totalCount: false,
            trackedChangeTable: table,
            changeVersionRange: new ChangeVersionRange(10L, null),
            resourceModel: CreateRegularResourceModel(RootColumn("SchoolId", "$.schoolId"))
        );
        var sut = new TrackedChangeQueryPlanner(SqlDialect.Pgsql);

        TrackedChangeQueryPlan plan = sut.Plan(request, fields, TrackedChangeAuthorizationSql.None);

        plan.Command.Should().NotBeNull();
        RelationalCommand command = plan.Command!;
        string sql = NormalizeSql(command.CommandText);
        sql.Should().Contain("c.\"ChangeVersion\" >= @MinChangeVersion");
        sql.Should().NotContain("@MaxChangeVersion");
        AssertParameter(command, "@MinChangeVersion", 10L);
        AssertNoParameter(command, "@MaxChangeVersion");
    }

    [Test]
    public void It_omits_min_change_version_from_keychanges_when_only_max_is_supplied()
    {
        TrackedChangeColumnInfo schoolIdColumn = ValueColumn(
            "SchoolId",
            "$.schoolId",
            TrackedChangeColumnRole.Scalar,
            TrackedChangeColumnOrigin.Identity,
            canonicalStorageColumn: new DbColumnName("SchoolId")
        );
        ChangeQueryResponseField[] fields = [ScalarField("schoolId", schoolIdColumn)];
        TrackedChangeTableInfo table = CreateTrackedChangeTable(
            TrackedChangeTableKind.Resource,
            [schoolIdColumn]
        );
        IRelationalTrackedChangeQueryRequest request = CreateRequest(
            ChangeQueryEndpointOperation.KeyChanges,
            totalCount: false,
            trackedChangeTable: table,
            changeVersionRange: new ChangeVersionRange(null, 20L),
            resourceModel: CreateRegularResourceModel(RootColumn("SchoolId", "$.schoolId"))
        );
        var sut = new TrackedChangeQueryPlanner(SqlDialect.Pgsql);

        TrackedChangeQueryPlan plan = sut.Plan(request, fields, TrackedChangeAuthorizationSql.None);

        plan.Command.Should().NotBeNull();
        RelationalCommand command = plan.Command!;
        string sql = NormalizeSql(command.CommandText);
        sql.Should().Contain("c.\"ChangeVersion\" <= @MaxChangeVersion");
        sql.Should().NotContain("@MinChangeVersion");
        AssertParameter(command, "@MaxChangeVersion", 20L);
        AssertNoParameter(command, "@MinChangeVersion");
    }

    [Test]
    public void It_uses_sql_server_offset_fetch_paging()
    {
        TrackedChangeColumnInfo schoolIdColumn = ValueColumn(
            "SchoolId",
            "$.schoolId",
            TrackedChangeColumnRole.Scalar,
            TrackedChangeColumnOrigin.Identity,
            canonicalStorageColumn: new DbColumnName("SchoolId")
        );
        ChangeQueryResponseField[] fields = [ScalarField("schoolId", schoolIdColumn)];
        TrackedChangeTableInfo table = CreateTrackedChangeTable(
            TrackedChangeTableKind.Resource,
            [schoolIdColumn]
        );
        IRelationalTrackedChangeQueryRequest request = CreateRequest(
            ChangeQueryEndpointOperation.Deletes,
            totalCount: false,
            trackedChangeTable: table,
            resourceModel: CreateRegularResourceModel(RootColumn("SchoolId", "$.schoolId"))
        );
        var sut = new TrackedChangeQueryPlanner(SqlDialect.Mssql);

        TrackedChangeQueryPlan plan = sut.Plan(request, fields, TrackedChangeAuthorizationSql.None);

        plan.Command.Should().NotBeNull();
        string sql = NormalizeSql(plan.Command!.CommandText);
        sql.Should().Contain("FROM [tracked_changes_edfi].[School] c");
        sql.Should().Contain("c.[NewSchoolId] IS NULL");
        sql.Should().Contain("c.[Id] AS [__Id]");
        sql.Should().Contain("c.[ChangeVersion] AS [__ChangeVersion]");
        sql.Should().Contain("c.[OldSchoolId] AS [schoolId__old]");
        sql.Should()
            .Contain("ORDER BY c.[ChangeVersion] ASC OFFSET @Offset ROWS FETCH NEXT @Limit ROWS ONLY");
    }

    [Test]
    public void It_suppresses_recreated_regular_resources_by_identity_join()
    {
        TrackedChangeColumnInfo schoolIdColumn = ValueColumn(
            "SchoolId",
            "$.schoolId",
            TrackedChangeColumnRole.Scalar,
            TrackedChangeColumnOrigin.Identity,
            canonicalStorageColumn: new DbColumnName("SchoolId")
        );
        ChangeQueryResponseField[] fields = [ScalarField("schoolId", schoolIdColumn)];
        TrackedChangeTableInfo table = CreateTrackedChangeTable(
            TrackedChangeTableKind.Resource,
            [schoolIdColumn]
        );
        IRelationalTrackedChangeQueryRequest request = CreateRequest(
            ChangeQueryEndpointOperation.Deletes,
            totalCount: false,
            trackedChangeTable: table,
            resourceModel: CreateRegularResourceModel(RootColumn("SchoolId", "$.schoolId"))
        );
        var sut = new TrackedChangeQueryPlanner(SqlDialect.Pgsql);

        TrackedChangeQueryPlan plan = sut.Plan(request, fields, TrackedChangeAuthorizationSql.None);

        plan.Command.Should().NotBeNull();
        string sql = NormalizeSql(plan.Command!.CommandText);
        sql.Should().Contain("LEFT JOIN \"edfi\".\"School\" live ON live.\"SchoolId\" = c.\"OldSchoolId\"");
        sql.Should().Contain("live.\"DocumentId\" IS NULL");
        sql.Should().NotContain("live.\"DocumentId\" = c.");
    }

    [Test]
    public void It_suppresses_recreated_regular_resources_with_descriptor_identity_by_resolved_descriptor_id()
    {
        TrackedChangeColumnInfo descriptorNamespaceColumn = ValueColumn(
            "ProgramTypeDescriptor_Namespace",
            "$.programTypeDescriptor",
            TrackedChangeColumnRole.DescriptorNamespace,
            TrackedChangeColumnOrigin.Identity,
            descriptorJoinName: "ProgramTypeDescriptor"
        );
        TrackedChangeColumnInfo descriptorCodeValueColumn = ValueColumn(
            "ProgramTypeDescriptor_CodeValue",
            "$.programTypeDescriptor",
            TrackedChangeColumnRole.DescriptorCodeValue,
            TrackedChangeColumnOrigin.Identity,
            descriptorJoinName: "ProgramTypeDescriptor"
        );
        ChangeQueryResponseField[] fields =
        [
            DescriptorField("programTypeDescriptor", descriptorNamespaceColumn, descriptorCodeValueColumn),
        ];
        TrackedChangeTableInfo table = CreateTrackedChangeTable(
            TrackedChangeTableKind.Resource,
            [descriptorNamespaceColumn, descriptorCodeValueColumn],
            descriptorJoins:
            [
                DescriptorJoin(
                    "ProgramTypeDescriptor",
                    "ProgramTypeDescriptorId",
                    _programTypeDescriptorResource
                ),
            ]
        );
        IRelationalTrackedChangeQueryRequest request = CreateRequest(
            ChangeQueryEndpointOperation.Deletes,
            totalCount: false,
            trackedChangeTable: table,
            resourceModel: CreateRegularResourceModel(
                [RootColumn("ProgramTypeDescriptorId", "$.programTypeDescriptor", ColumnKind.DescriptorFk)],
                descriptorEdgeSources:
                [
                    new DescriptorEdgeSource(
                        IsIdentityComponent: true,
                        DescriptorValuePath: Path("$.programTypeDescriptor"),
                        Table: _sourceTable,
                        FkColumn: new DbColumnName("ProgramTypeDescriptorId"),
                        DescriptorResource: _programTypeDescriptorResource
                    ),
                ]
            )
        );
        var sut = new TrackedChangeQueryPlanner(SqlDialect.Pgsql);

        TrackedChangeQueryPlan plan = sut.Plan(request, fields, TrackedChangeAuthorizationSql.None);

        plan.Command.Should().NotBeNull();
        string sql = NormalizeSql(plan.Command!.CommandText);
        sql.Should()
            .Contain(
                "LEFT JOIN \"dms\".\"Descriptor\" descriptor_0 ON descriptor_0.\"Discriminator\" IN (@DescriptorDiscriminator0, @DescriptorDiscriminatorQualified0) AND descriptor_0.\"Namespace\" = c.\"OldProgramTypeDescriptor_Namespace\" AND descriptor_0.\"CodeValue\" = c.\"OldProgramTypeDescriptor_CodeValue\""
            );
        sql.Should()
            .Contain(
                "LEFT JOIN \"edfi\".\"School\" live ON live.\"ProgramTypeDescriptorId\" = descriptor_0.\"DocumentId\""
            );
        AssertParameter(plan.Command, "@DescriptorDiscriminator0", "ProgramTypeDescriptor");
        AssertParameter(plan.Command, "@DescriptorDiscriminatorQualified0", "Ed-Fi:ProgramTypeDescriptor");
        sql.Should().Contain("live.\"DocumentId\" IS NULL");
        sql.Should()
            .NotContain("live.\"ProgramTypeDescriptorId\" = c.\"OldProgramTypeDescriptor_Namespace\"");
        sql.Should()
            .NotContain("live.\"ProgramTypeDescriptorId\" = c.\"OldProgramTypeDescriptor_CodeValue\"");
    }

    [Test]
    public void It_resolves_regular_resource_descriptor_identity_fk_from_descriptor_edge_when_tracked_join_is_absent()
    {
        TrackedChangeColumnInfo descriptorNamespaceColumn = ValueColumn(
            "ProgramTypeDescriptor_Namespace",
            "$.programTypeDescriptor",
            TrackedChangeColumnRole.DescriptorNamespace,
            TrackedChangeColumnOrigin.Identity,
            descriptorJoinName: "ProgramTypeDescriptor"
        );
        TrackedChangeColumnInfo descriptorCodeValueColumn = ValueColumn(
            "ProgramTypeDescriptor_CodeValue",
            "$.programTypeDescriptor",
            TrackedChangeColumnRole.DescriptorCodeValue,
            TrackedChangeColumnOrigin.Identity,
            descriptorJoinName: "ProgramTypeDescriptor"
        );
        ChangeQueryResponseField[] fields =
        [
            DescriptorField("programTypeDescriptor", descriptorNamespaceColumn, descriptorCodeValueColumn),
        ];
        TrackedChangeTableInfo table = CreateTrackedChangeTable(
            TrackedChangeTableKind.Resource,
            [descriptorNamespaceColumn, descriptorCodeValueColumn]
        );
        IRelationalTrackedChangeQueryRequest request = CreateRequest(
            ChangeQueryEndpointOperation.Deletes,
            totalCount: false,
            trackedChangeTable: table,
            resourceModel: CreateRegularResourceModel(
                [RootColumn("ProgramTypeDescriptorId", "$.programTypeDescriptor", ColumnKind.DescriptorFk)],
                descriptorEdgeSources:
                [
                    new DescriptorEdgeSource(
                        IsIdentityComponent: true,
                        DescriptorValuePath: Path("$.programTypeDescriptor"),
                        Table: _sourceTable,
                        FkColumn: new DbColumnName("ProgramTypeDescriptorId"),
                        DescriptorResource: _programTypeDescriptorResource
                    ),
                ]
            )
        );
        var sut = new TrackedChangeQueryPlanner(SqlDialect.Pgsql);

        TrackedChangeQueryPlan plan = sut.Plan(request, fields, TrackedChangeAuthorizationSql.None);

        plan.Command.Should().NotBeNull();
        string sql = NormalizeSql(plan.Command!.CommandText);
        sql.Should()
            .Contain(
                "LEFT JOIN \"edfi\".\"School\" live ON live.\"ProgramTypeDescriptorId\" = descriptor_0.\"DocumentId\""
            );
        AssertParameter(plan.Command, "@DescriptorDiscriminator0", "ProgramTypeDescriptor");
        AssertParameter(plan.Command, "@DescriptorDiscriminatorQualified0", "Ed-Fi:ProgramTypeDescriptor");
    }

    [Test]
    public void It_throws_contextual_error_when_descriptor_identity_fk_column_is_not_on_live_root_table()
    {
        TrackedChangeColumnInfo descriptorNamespaceColumn = ValueColumn(
            "ProgramTypeDescriptor_Namespace",
            "$.programTypeDescriptor",
            TrackedChangeColumnRole.DescriptorNamespace,
            TrackedChangeColumnOrigin.Identity,
            descriptorJoinName: "ProgramTypeDescriptor"
        );
        TrackedChangeColumnInfo descriptorCodeValueColumn = ValueColumn(
            "ProgramTypeDescriptor_CodeValue",
            "$.programTypeDescriptor",
            TrackedChangeColumnRole.DescriptorCodeValue,
            TrackedChangeColumnOrigin.Identity,
            descriptorJoinName: "ProgramTypeDescriptor"
        );
        ChangeQueryResponseField[] fields =
        [
            DescriptorField("programTypeDescriptor", descriptorNamespaceColumn, descriptorCodeValueColumn),
        ];
        TrackedChangeTableInfo table = CreateTrackedChangeTable(
            TrackedChangeTableKind.Resource,
            [descriptorNamespaceColumn, descriptorCodeValueColumn],
            descriptorJoins:
            [
                DescriptorJoin(
                    "ProgramTypeDescriptor",
                    "MissingProgramTypeDescriptorId",
                    _programTypeDescriptorResource
                ),
            ]
        );
        IRelationalTrackedChangeQueryRequest request = CreateRequest(
            ChangeQueryEndpointOperation.Deletes,
            totalCount: false,
            trackedChangeTable: table,
            resourceModel: CreateRegularResourceModel(
                [RootColumn("ProgramTypeDescriptorId", "$.programTypeDescriptor", ColumnKind.DescriptorFk)],
                descriptorEdgeSources:
                [
                    new DescriptorEdgeSource(
                        IsIdentityComponent: true,
                        DescriptorValuePath: Path("$.programTypeDescriptor"),
                        Table: _sourceTable,
                        FkColumn: new DbColumnName("ProgramTypeDescriptorId"),
                        DescriptorResource: _programTypeDescriptorResource
                    ),
                ]
            )
        );
        var sut = new TrackedChangeQueryPlanner(SqlDialect.Pgsql);

        Action act = () => sut.Plan(request, fields, TrackedChangeAuthorizationSql.None);

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                "Descriptor identity path '$.programTypeDescriptor' for resource 'Ed-Fi:School' "
                    + "resolved descriptor FK column 'MissingProgramTypeDescriptorId' for expected descriptor resource "
                    + "'Ed-Fi:ProgramTypeDescriptor', but that column was not found on live root table 'edfi.School'."
            );
    }

    [Test]
    public void It_filters_shared_descriptor_deletes_by_resource_discriminator()
    {
        TrackedChangeColumnInfo namespaceColumn = ValueColumn(
            "Namespace",
            "$.namespace",
            TrackedChangeColumnRole.Scalar,
            TrackedChangeColumnOrigin.Identity,
            canonicalStorageColumn: new DbColumnName("Namespace")
        );
        TrackedChangeColumnInfo codeValueColumn = ValueColumn(
            "CodeValue",
            "$.codeValue",
            TrackedChangeColumnRole.Scalar,
            TrackedChangeColumnOrigin.Identity,
            canonicalStorageColumn: new DbColumnName("CodeValue")
        );
        ChangeQueryResponseField[] fields =
        [
            ScalarField("namespace", namespaceColumn),
            ScalarField("codeValue", codeValueColumn),
        ];
        TrackedChangeTableInfo table = CreateTrackedChangeTable(
            TrackedChangeTableKind.SharedDescriptor,
            [namespaceColumn, codeValueColumn]
        );
        IRelationalTrackedChangeQueryRequest request = CreateRequest(
            ChangeQueryEndpointOperation.Deletes,
            totalCount: false,
            trackedChangeTable: table,
            resourceInfo: CreateResourceInfo(_programTypeDescriptorResource, isDescriptor: true),
            resourceModel: CreateSharedDescriptorResourceModel()
        );
        var sut = new TrackedChangeQueryPlanner(SqlDialect.Pgsql);

        TrackedChangeQueryPlan plan = sut.Plan(request, fields, TrackedChangeAuthorizationSql.None);

        plan.Command.Should().NotBeNull();
        RelationalCommand command = plan.Command!;
        string sql = NormalizeSql(command.CommandText);
        sql.Should().Contain("c.\"Discriminator\" IN (@Discriminator, @QualifiedDiscriminator)");
        AssertParameter(command, "@Discriminator", "ProgramTypeDescriptor");
        AssertParameter(command, "@QualifiedDiscriminator", "Ed-Fi:ProgramTypeDescriptor");
    }

    [Test]
    public void It_suppresses_recreated_descriptors_by_discriminator_namespace_and_code_value()
    {
        TrackedChangeColumnInfo namespaceColumn = ValueColumn(
            "Namespace",
            "$.namespace",
            TrackedChangeColumnRole.Scalar,
            TrackedChangeColumnOrigin.Identity,
            canonicalStorageColumn: new DbColumnName("Namespace")
        );
        TrackedChangeColumnInfo codeValueColumn = ValueColumn(
            "CodeValue",
            "$.codeValue",
            TrackedChangeColumnRole.Scalar,
            TrackedChangeColumnOrigin.Identity,
            canonicalStorageColumn: new DbColumnName("CodeValue")
        );
        ChangeQueryResponseField[] fields =
        [
            ScalarField("namespace", namespaceColumn),
            ScalarField("codeValue", codeValueColumn),
        ];
        TrackedChangeTableInfo table = CreateTrackedChangeTable(
            TrackedChangeTableKind.SharedDescriptor,
            [namespaceColumn, codeValueColumn]
        );
        IRelationalTrackedChangeQueryRequest request = CreateRequest(
            ChangeQueryEndpointOperation.Deletes,
            totalCount: false,
            trackedChangeTable: table,
            resourceInfo: CreateResourceInfo(_programTypeDescriptorResource, isDescriptor: true),
            resourceModel: CreateSharedDescriptorResourceModel()
        );
        var sut = new TrackedChangeQueryPlanner(SqlDialect.Pgsql);

        TrackedChangeQueryPlan plan = sut.Plan(request, fields, TrackedChangeAuthorizationSql.None);

        plan.Command.Should().NotBeNull();
        string sql = NormalizeSql(plan.Command!.CommandText);
        sql.Should()
            .Contain(
                "LEFT JOIN \"dms\".\"Descriptor\" live ON live.\"Discriminator\" IN (@Discriminator, @QualifiedDiscriminator) AND live.\"Namespace\" = c.\"OldNamespace\" AND live.\"CodeValue\" = c.\"OldCodeValue\""
            );
        sql.Should().Contain("live.\"DocumentId\" IS NULL");
    }

    [Test]
    public void It_throws_contextual_error_when_scalar_identity_canonical_storage_column_is_not_on_live_root_table()
    {
        TrackedChangeColumnInfo schoolIdColumn = ValueColumn(
            "SchoolId",
            "$.schoolId",
            TrackedChangeColumnRole.Scalar,
            TrackedChangeColumnOrigin.Identity,
            canonicalStorageColumn: new DbColumnName("MissingSchoolId")
        );
        ChangeQueryResponseField[] fields = [ScalarField("schoolId", schoolIdColumn)];
        TrackedChangeTableInfo table = CreateTrackedChangeTable(
            TrackedChangeTableKind.Resource,
            [schoolIdColumn]
        );
        IRelationalTrackedChangeQueryRequest request = CreateRequest(
            ChangeQueryEndpointOperation.Deletes,
            totalCount: false,
            trackedChangeTable: table,
            resourceModel: CreateRegularResourceModel(RootColumn("SchoolId", "$.schoolId"))
        );
        var sut = new TrackedChangeQueryPlanner(SqlDialect.Pgsql);

        Action act = () => sut.Plan(request, fields, TrackedChangeAuthorizationSql.None);

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                "Tracked path '$.schoolId' declares canonical storage column 'MissingSchoolId', "
                    + "but that column was not found on live root table 'edfi.School' for resource 'Ed-Fi:School'."
            );
    }

    [Test]
    public void It_injects_authorization_predicates_into_deletes_sql()
    {
        TrackedChangeColumnInfo schoolIdColumn = ValueColumn(
            "SchoolId",
            "$.schoolId",
            TrackedChangeColumnRole.Scalar,
            TrackedChangeColumnOrigin.Identity,
            canonicalStorageColumn: new DbColumnName("SchoolId")
        );
        ChangeQueryResponseField[] fields = [ScalarField("schoolId", schoolIdColumn)];
        TrackedChangeTableInfo table = CreateTrackedChangeTable(
            TrackedChangeTableKind.Resource,
            [schoolIdColumn]
        );
        IRelationalTrackedChangeQueryRequest request = CreateRequest(
            ChangeQueryEndpointOperation.Deletes,
            totalCount: false,
            trackedChangeTable: table,
            resourceModel: CreateRegularResourceModel(RootColumn("SchoolId", "$.schoolId"))
        );

        var authSql = new TrackedChangeAuthorizationSql(
            ["c.\"OldSchoolId\" IN (SELECT 1)"],
            [new RelationalParameter("@AuthP0", 7L)]
        );

        var sut = new TrackedChangeQueryPlanner(SqlDialect.Pgsql);
        TrackedChangeQueryPlan plan = sut.Plan(request, fields, authSql);

        string sql = NormalizeSql(plan.Command!.CommandText);
        sql.Should().Contain("c.\"OldSchoolId\" IN (SELECT 1)");
        plan.Command!.Parameters.Should().Contain(p => p.Name == "@AuthP0");
    }

    [Test]
    public void It_injects_authorization_predicates_into_keychanges_cte()
    {
        TrackedChangeColumnInfo schoolIdColumn = ValueColumn(
            "SchoolId",
            "$.schoolId",
            TrackedChangeColumnRole.Scalar,
            TrackedChangeColumnOrigin.Identity,
            canonicalStorageColumn: new DbColumnName("SchoolId")
        );
        ChangeQueryResponseField[] fields = [ScalarField("schoolId", schoolIdColumn)];
        TrackedChangeTableInfo table = CreateTrackedChangeTable(
            TrackedChangeTableKind.Resource,
            [schoolIdColumn]
        );
        IRelationalTrackedChangeQueryRequest request = CreateRequest(
            ChangeQueryEndpointOperation.KeyChanges,
            totalCount: false,
            trackedChangeTable: table,
            resourceModel: CreateRegularResourceModel(RootColumn("SchoolId", "$.schoolId"))
        );

        var authSql = new TrackedChangeAuthorizationSql(["c.\"OldSchoolId\" IN (SELECT 1)"], []);

        var sut = new TrackedChangeQueryPlanner(SqlDialect.Pgsql);
        string sql = NormalizeSql(sut.Plan(request, fields, authSql).Command!.CommandText);

        // Auth predicate must be inside the FilteredChanges CTE (before GROUP BY / paging).
        int filteredChangesIndex = sql.IndexOf("FilteredChanges AS", StringComparison.Ordinal);
        int authIndex = sql.IndexOf("c.\"OldSchoolId\" IN (SELECT 1)", StringComparison.Ordinal);
        int groupByIndex = sql.IndexOf("GROUP BY", StringComparison.Ordinal);
        authIndex.Should().BeGreaterThan(filteredChangesIndex);
        authIndex.Should().BeLessThan(groupByIndex);
    }

    private static void AssertEmptyKeyChangesPlan(
        TrackedChangeTableKind trackedChangeTableKind,
        bool totalCount,
        long? expectedTotalCount
    )
    {
        var sut = new TrackedChangeQueryPlanner(SqlDialect.Pgsql);
        IRelationalTrackedChangeQueryRequest request = CreateRequest(
            ChangeQueryEndpointOperation.KeyChanges,
            trackedChangeTableKind,
            totalCount
        );

        TrackedChangeQueryPlan plan = sut.Plan(request, [], TrackedChangeAuthorizationSql.None);

        plan.Command.Should().BeNull();
        plan.Fields.Should().BeEmpty();
        plan.IncludesTotalCount.Should().Be(totalCount);
        plan.IsEmpty.Should().BeTrue();
        plan.TotalCount.Should().Be(expectedTotalCount);
    }

    private static IRelationalTrackedChangeQueryRequest CreateRequest(
        ChangeQueryEndpointOperation operation,
        TrackedChangeTableKind trackedChangeTableKind,
        bool totalCount
    ) =>
        CreateRequest(
            operation,
            totalCount,
            trackedChangeTable: CreateTrackedChangeTable(trackedChangeTableKind)
        );

    private static IRelationalTrackedChangeQueryRequest CreateRequest(
        ChangeQueryEndpointOperation operation,
        bool totalCount,
        TrackedChangeTableInfo trackedChangeTable,
        PaginationParameters? paginationParameters = null,
        ChangeVersionRange? changeVersionRange = null,
        ResourceInfo? resourceInfo = null,
        ConcreteResourceModel? resourceModel = null
    )
    {
        var request = A.Fake<IRelationalTrackedChangeQueryRequest>();
        A.CallTo(() => request.Operation).Returns(operation);
        A.CallTo(() => request.ResourceInfo)
            .Returns(resourceInfo ?? CreateResourceInfo(_schoolResource, isDescriptor: false));
        A.CallTo(() => request.PaginationParameters)
            .Returns(
                paginationParameters
                    ?? new PaginationParameters(
                        Limit: 25,
                        Offset: 0,
                        TotalCount: totalCount,
                        MaximumPageSize: 500
                    )
            );
        A.CallTo(() => request.ChangeVersionRange).Returns(changeVersionRange ?? ChangeVersionRange.None);
        A.CallTo(() => request.ResourceModel).Returns(resourceModel ?? CreateRegularResourceModel());
        A.CallTo(() => request.TrackedChangeTable).Returns(trackedChangeTable);

        return request;
    }

    private static TrackedChangeTableInfo CreateTrackedChangeTable(
        TrackedChangeTableKind kind,
        IReadOnlyList<TrackedChangeColumnInfo>? valueColumns = null,
        IReadOnlyList<TrackedChangeSystemColumnInfo>? systemColumns = null,
        IReadOnlyList<TrackedChangeDescriptorJoinInfo>? descriptorJoins = null
    )
    {
        return new TrackedChangeTableInfo(
            Table: new DbTableName(
                _trackedSchema,
                kind is TrackedChangeTableKind.SharedDescriptor ? "Descriptor" : "School"
            ),
            Kind: kind,
            SourceTable: kind is TrackedChangeTableKind.SharedDescriptor ? _descriptorTable : _sourceTable,
            ValueColumnsInTableOrder: valueColumns ?? [],
            SystemColumns: systemColumns ?? DefaultSystemColumns(kind),
            PrimaryKeyColumns: [],
            DescriptorJoins: descriptorJoins ?? [],
            PersonJoins: []
        );
    }

    private static TrackedChangeSystemColumnInfo SystemColumn(
        TrackedChangeSystemColumnRole role,
        string columnName
    )
    {
        return new TrackedChangeSystemColumnInfo(
            role,
            new DbColumnName(columnName),
            new RelationalScalarType(ScalarKind.Int64),
            IsNullable: false,
            IsPrimaryKey: false
        );
    }

    private static TrackedChangeColumnInfo ValueColumn(
        string columnName,
        string sourceJsonPath,
        TrackedChangeColumnRole role,
        TrackedChangeColumnOrigin origin,
        DbColumnName? canonicalStorageColumn = null,
        string? descriptorJoinName = null,
        string? personJoinName = null
    )
    {
        return new TrackedChangeColumnInfo(
            OldColumnName: new DbColumnName($"Old{columnName}"),
            NewColumnName: new DbColumnName($"New{columnName}"),
            SourceJsonPath: sourceJsonPath,
            CanonicalStorageColumn: canonicalStorageColumn,
            IsOldColumnNullable: false,
            IsNewColumnNullable: true,
            ScalarType: new RelationalScalarType(ScalarKind.Int32),
            Role: role,
            Origin: origin,
            DescriptorJoinName: descriptorJoinName,
            PersonJoinName: personJoinName
        );
    }

    private static TrackedChangeDescriptorJoinInfo DescriptorJoin(
        string joinName,
        string sourceColumn,
        QualifiedResourceName descriptorResource
    ) => new(joinName, new DbColumnName(sourceColumn), descriptorResource);

    private static IReadOnlyList<TrackedChangeSystemColumnInfo> DefaultSystemColumns(
        TrackedChangeTableKind kind
    )
    {
        List<TrackedChangeSystemColumnInfo> columns =
        [
            SystemColumn(TrackedChangeSystemColumnRole.Id, "Id"),
            SystemColumn(TrackedChangeSystemColumnRole.ChangeVersion, "ChangeVersion"),
            SystemColumn(TrackedChangeSystemColumnRole.CreatedAt, "CreatedAt"),
        ];

        if (kind is TrackedChangeTableKind.SharedDescriptor)
        {
            columns.Add(SystemColumn(TrackedChangeSystemColumnRole.Discriminator, "Discriminator"));
        }

        return columns;
    }

    private static ChangeQueryResponseField ScalarField(
        string queryFieldName,
        TrackedChangeColumnInfo column
    ) =>
        new(
            queryFieldName,
            ChangeQueryResponseFieldKind.Scalar,
            column,
            column,
            OldDescriptorCodeValueColumn: null,
            NewDescriptorCodeValueColumn: null
        );

    private static ChangeQueryResponseField DescriptorField(
        string queryFieldName,
        TrackedChangeColumnInfo namespaceColumn,
        TrackedChangeColumnInfo codeValueColumn
    ) =>
        new(
            queryFieldName,
            ChangeQueryResponseFieldKind.Descriptor,
            namespaceColumn,
            namespaceColumn,
            codeValueColumn,
            codeValueColumn
        );

    private static ConcreteResourceModel CreateRegularResourceModel(params DbColumnModel[] rootColumns) =>
        CreateRegularResourceModel(rootColumns, descriptorEdgeSources: []);

    private static ConcreteResourceModel CreateRegularResourceModel(
        IReadOnlyList<DbColumnModel> rootColumns,
        IReadOnlyList<DescriptorEdgeSource> descriptorEdgeSources
    ) =>
        CreateResourceModel(
            _schoolResource,
            ResourceStorageKind.RelationalTables,
            _sourceTable,
            rootColumns,
            descriptorEdgeSources
        );

    private static ConcreteResourceModel CreateSharedDescriptorResourceModel() =>
        CreateResourceModel(
            _programTypeDescriptorResource,
            ResourceStorageKind.SharedDescriptorTable,
            _descriptorTable,
            [
                RootColumn("Namespace", "$.namespace"),
                RootColumn("CodeValue", "$.codeValue"),
                RootColumn("Discriminator", sourceJsonPath: null),
            ],
            descriptorEdgeSources: []
        );

    private static ConcreteResourceModel CreateResourceModel(
        QualifiedResourceName resource,
        ResourceStorageKind storageKind,
        DbTableName rootTableName,
        IReadOnlyList<DbColumnModel> rootColumns,
        IReadOnlyList<DescriptorEdgeSource> descriptorEdgeSources
    )
    {
        DbColumnModel documentIdColumn = RootColumn("DocumentId", sourceJsonPath: null);
        var rootModel = new DbTableModel(
            rootTableName,
            Path("$"),
            new TableKey($"PK_{rootTableName.Name}", []),
            Columns: [documentIdColumn, .. rootColumns],
            Constraints: []
        );
        var relationalModel = new RelationalResourceModel(
            resource,
            rootTableName.Schema,
            storageKind,
            rootModel,
            TablesInDependencyOrder: [rootModel],
            DocumentReferenceBindings: [],
            DescriptorEdgeSources: descriptorEdgeSources
        );

        return new ConcreteResourceModel(
            new ResourceKeyEntry(1, resource, ResourceVersion: "1.0", IsAbstractResource: false),
            storageKind,
            relationalModel
        );
    }

    private static DbColumnModel RootColumn(
        string columnName,
        string? sourceJsonPath,
        ColumnKind kind = ColumnKind.Scalar
    ) =>
        new(
            new DbColumnName(columnName),
            kind,
            new RelationalScalarType(ScalarKind.String),
            IsNullable: false,
            SourceJsonPath: sourceJsonPath is null ? null : Path(sourceJsonPath),
            TargetResource: null
        );

    private static JsonPathExpression Path(string canonical) => new(canonical, []);

    private static ResourceInfo CreateResourceInfo(QualifiedResourceName resource, bool isDescriptor) =>
        new(
            new ProjectName(resource.ProjectName),
            new ResourceName(resource.ResourceName),
            isDescriptor,
            new SemVer("5.0.0"),
            AllowIdentityUpdates: false
        );

    private static string NormalizeSql(string sql) => Regex.Replace(sql, @"\s+", " ").Trim();

    private static void AssertParameter(RelationalCommand command, string name, object? expectedValue)
    {
        RelationalParameter parameter = command.Parameters.Single(parameter => parameter.Name == name);
        parameter.Value.Should().Be(expectedValue);
    }

    private static void AssertNoParameter(RelationalCommand command, string name)
    {
        command.Parameters.Should().NotContain(parameter => parameter.Name == name);
    }
}

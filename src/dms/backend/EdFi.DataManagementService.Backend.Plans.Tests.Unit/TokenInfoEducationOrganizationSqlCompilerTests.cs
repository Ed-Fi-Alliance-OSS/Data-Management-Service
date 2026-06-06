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
[Parallelizable]
public class Given_TokenInfoEducationOrganizationSqlCompiler
{
    private static readonly DbSchemaName _edfiSchema = new("edfi");
    private static readonly DbSchemaName _sampleSchema = new("sample");
    private static readonly DbSchemaName _dmsSchema = new("dms");
    private static readonly QualifiedResourceName _educationOrganizationResource = new(
        "Ed-Fi",
        "EducationOrganization"
    );
    private static readonly QualifiedResourceName _schoolResource = new("Ed-Fi", "School");
    private static readonly QualifiedResourceName _localEducationAgencyResource = new(
        "Ed-Fi",
        "LocalEducationAgency"
    );
    private static readonly QualifiedResourceName _customEducationOrganizationResource = new(
        "Sample",
        "CustomEducationOrganization"
    );
    private static readonly QualifiedResourceName _studentResource = new("Ed-Fi", "Student");
    private static readonly QualifiedResourceName _gradeLevelDescriptorResource = new(
        "Ed-Fi",
        "GradeLevelDescriptor"
    );

    [Test]
    public void It_should_build_concrete_edorg_projection_arms_from_the_education_organization_union_view()
    {
        var plan = Compile(SqlDialect.Pgsql);

        plan.ProjectionArmsInOrder.Select(static arm => arm.Resource)
            .Should()
            .Equal(_schoolResource, _localEducationAgencyResource, _customEducationOrganizationResource);
        plan.ProjectionArmsInOrder[0]
            .Should()
            .Be(
                new TokenInfoEducationOrganizationProjectionArm(
                    _schoolResource,
                    new DbTableName(_edfiSchema, "School"),
                    new DbColumnName("SchoolId"),
                    new DbColumnName("NameOfInstitution"),
                    "Ed-Fi:School"
                )
            );
        plan.ProjectionArmsInOrder[2].Discriminator.Should().Be("Sample:CustomEducationOrganization");

        plan.EducationOrganizationSql.Should().Contain("FROM \"edfi\".\"School\" r");
        plan.EducationOrganizationSql.Should().Contain("FROM \"edfi\".\"LocalEducationAgency\" r");
        plan.EducationOrganizationSql.Should().Contain("FROM \"sample\".\"CustomEducationOrganization\" r");
        plan.EducationOrganizationSql.Should().Contain("r.\"SchoolId\" AS \"EducationOrganizationId\"");
        plan.EducationOrganizationSql.Should().Contain("'Ed-Fi:School' AS \"Discriminator\"");
        plan.EducationOrganizationSql.Should().NotContain("AS \"DocumentId\"");
        plan.EducationOrganizationSql.Should().NotContain("Student");
        plan.EducationOrganizationSql.Should().NotContain("GradeLevelDescriptor");
        plan.EducationOrganizationSql.Should().NotContain("EducationOrganization_View");
    }

    [Test]
    public void It_should_compile_hierarchy_sql_for_accessible_targets_ancestors_and_self_rows()
    {
        var plan = Compile(SqlDialect.Pgsql);

        plan.EducationOrganizationSql.Should().Contain("WITH concrete_edorg AS (");
        plan.EducationOrganizationSql.Should().Contain("claimed_edorg AS (");
        plan.EducationOrganizationSql.Should().Contain("accessible_targets AS (");
        plan.EducationOrganizationSql.Should().Contain("ancestor_links AS (");
        plan.EducationOrganizationSql.Should()
            .Contain("FROM \"auth\".\"EducationOrganizationIdToEducationOrganizationId\" h");
        plan.EducationOrganizationSql.Should()
            .Contain("h.\"SourceEducationOrganizationId\" = c.\"EducationOrganizationId\"");
        plan.EducationOrganizationSql.Should()
            .Contain("h.\"TargetEducationOrganizationId\" = a.\"EducationOrganizationId\"");
        plan.EducationOrganizationSql.Should()
            .Contain("a.\"EducationOrganizationId\" AS \"TargetEducationOrganizationId\"");
        plan.EducationOrganizationSql.Should()
            .Contain("a.\"EducationOrganizationId\" AS \"SourceEducationOrganizationId\"");
        plan.EducationOrganizationSql.Should()
            .Contain("ancestor.\"Discriminator\" AS \"AncestorDiscriminator\"");
        plan.EducationOrganizationSql.Should()
            .Contain("ancestor.\"EducationOrganizationId\" AS \"AncestorEducationOrganizationId\"");
        plan.EducationOrganizationSql.Should()
            .Contain("ORDER BY\n    target.\"EducationOrganizationId\" ASC,");

        plan.ParametersInOrder.Should()
            .Equal(
                new QuerySqlParameter(
                    QuerySqlParameterRole.Filter,
                    "ClaimEducationOrganizationIds",
                    QuerySqlParameterBinding.PgsqlArray
                )
            );
    }

    [Test]
    public void It_should_emit_sql_server_scalar_claim_filters_and_bracket_quoted_relations()
    {
        var plan = Compile(SqlDialect.Mssql, [222L, 111L]);

        plan.EducationOrganizationSql.Should().Contain("FROM [edfi].[School] r");
        plan.EducationOrganizationSql.Should()
            .Contain("[auth].[EducationOrganizationIdToEducationOrganizationId] h");
        plan.EducationOrganizationSql.Should()
            .Contain(
                "WHERE c.[EducationOrganizationId] IN (@ClaimEducationOrganizationIds_0, @ClaimEducationOrganizationIds_1)"
            );
        plan.ParametersInOrder.Select(static parameter => parameter.ParameterName)
            .Should()
            .Equal("ClaimEducationOrganizationIds_0", "ClaimEducationOrganizationIds_1");
        plan.ParametersInOrder.Should()
            .OnlyContain(static parameter => parameter.Binding.Kind == QuerySqlParameterBindingKind.Scalar);
    }

    [Test]
    public void It_should_emit_sql_server_structured_claim_filters_when_the_parameterization_requires_tvp()
    {
        var claimIds = Enumerable.Range(1, 2000).Select(static value => (long)value).ToArray();
        var plan = Compile(SqlDialect.Mssql, claimIds);

        plan.EducationOrganizationSql.Should()
            .Contain(
                "WHERE c.[EducationOrganizationId] IN (SELECT [Id] FROM @ClaimEducationOrganizationIds)"
            );
        plan.ParametersInOrder.Should()
            .Equal(
                new QuerySqlParameter(
                    QuerySqlParameterRole.Filter,
                    "ClaimEducationOrganizationIds",
                    QuerySqlParameterBinding.CreateMssqlStructured("dms.BigIntTable", "Id")
                )
            );
    }

    [Test]
    public void It_should_fail_fast_when_an_edorg_member_does_not_project_name_of_institution()
    {
        var compiler = new TokenInfoEducationOrganizationSqlCompiler(SqlDialect.Pgsql);
        var spec = CreateSpec(SqlDialect.Pgsql, [111L], includeSchoolNameOfInstitution: false);

        var act = () => compiler.Compile(spec);

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*Concrete EducationOrganization resource 'Ed-Fi.School'*$.nameOfInstitution*");
    }

    [Test]
    public void It_should_fail_fast_when_mapping_set_lacks_the_education_organization_union_view()
    {
        var compiler = new TokenInfoEducationOrganizationSqlCompiler(SqlDialect.Pgsql);
        var spec = CreateSpec(SqlDialect.Pgsql, [111L], includeEducationOrganizationUnionView: false);

        var act = () => compiler.Compile(spec);

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*does not contain an abstract 'EducationOrganization' union view*");
    }

    [Test]
    public void It_should_fail_fast_when_the_education_organization_union_view_has_no_projection_arms()
    {
        var compiler = new TokenInfoEducationOrganizationSqlCompiler(SqlDialect.Pgsql);
        var spec = CreateSpec(SqlDialect.Pgsql, [111L], includeEducationOrganizationUnionArms: false);

        var act = () => compiler.Compile(spec);

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                "*EducationOrganization union view '*' does not contain any concrete projection arms*"
            );
    }

    private static TokenInfoEducationOrganizationSqlPlan Compile(
        SqlDialect dialect,
        IReadOnlyList<long>? claimEducationOrganizationIds = null
    )
    {
        var compiler = new TokenInfoEducationOrganizationSqlCompiler(dialect);

        return compiler.Compile(CreateSpec(dialect, claimEducationOrganizationIds ?? [222L, 111L]));
    }

    private static TokenInfoEducationOrganizationSqlSpec CreateSpec(
        SqlDialect dialect,
        IReadOnlyList<long> claimEducationOrganizationIds,
        bool includeSchoolNameOfInstitution = true,
        bool includeEducationOrganizationUnionView = true,
        bool includeEducationOrganizationUnionArms = true
    )
    {
        return new TokenInfoEducationOrganizationSqlSpec(
            CreateMappingSet(
                dialect,
                includeSchoolNameOfInstitution,
                includeEducationOrganizationUnionView,
                includeEducationOrganizationUnionArms
            ),
            AuthorizationClaimEducationOrganizationIdParameterizationFactory.Create(
                dialect,
                claimEducationOrganizationIds,
                "ClaimEducationOrganizationIds"
            )
        );
    }

    private static MappingSet CreateMappingSet(
        SqlDialect dialect,
        bool includeSchoolNameOfInstitution = true,
        bool includeEducationOrganizationUnionView = true,
        bool includeEducationOrganizationUnionArms = true
    )
    {
        var resourceKeys = new[]
        {
            new ResourceKeyEntry(1, _educationOrganizationResource, "5.2.0", true),
            new ResourceKeyEntry(2, _schoolResource, "5.2.0", false),
            new ResourceKeyEntry(3, _localEducationAgencyResource, "5.2.0", false),
            new ResourceKeyEntry(4, _customEducationOrganizationResource, "1.0.0", false),
            new ResourceKeyEntry(5, _studentResource, "5.2.0", false),
            new ResourceKeyEntry(6, _gradeLevelDescriptorResource, "5.2.0", false),
        };
        var resourceKeysByResource = resourceKeys.ToDictionary(
            static resourceKey => resourceKey.Resource,
            static resourceKey => resourceKey
        );
        var concreteResources = new[]
        {
            CreateEdOrgConcreteResource(
                resourceKeysByResource[_schoolResource],
                _edfiSchema,
                "School",
                "SchoolId",
                "$.schoolId",
                includeSchoolNameOfInstitution
            ),
            CreateEdOrgConcreteResource(
                resourceKeysByResource[_localEducationAgencyResource],
                _edfiSchema,
                "LocalEducationAgency",
                "LocalEducationAgencyId",
                "$.localEducationAgencyId"
            ),
            CreateEdOrgConcreteResource(
                resourceKeysByResource[_customEducationOrganizationResource],
                _sampleSchema,
                "CustomEducationOrganization",
                "CustomEducationOrganizationId",
                "$.customEducationOrganizationId"
            ),
            CreateEdOrgConcreteResource(
                resourceKeysByResource[_studentResource],
                _edfiSchema,
                "Student",
                "StudentUniqueId",
                "$.studentUniqueId"
            ),
            CreateDescriptorResource(resourceKeysByResource[_gradeLevelDescriptorResource]),
        };
        var effectiveSchema = new EffectiveSchemaInfo(
            "5.2.0",
            "v1",
            "hash",
            (short)resourceKeys.Length,
            [1, 2, 3],
            [
                new SchemaComponentInfo("ed-fi", "Ed-Fi", "5.2.0", false, "edfi-hash"),
                new SchemaComponentInfo("sample", "Sample", "1.0.0", true, "sample-hash"),
            ],
            resourceKeys
        );
        IReadOnlyList<AbstractUnionViewInfo> abstractUnionViews = includeEducationOrganizationUnionView
            ?
            [
                CreateEducationOrganizationUnionView(
                    resourceKeysByResource[_educationOrganizationResource],
                    resourceKeysByResource,
                    includeEducationOrganizationUnionArms
                ),
            ]
            : [];
        var modelSet = new DerivedRelationalModelSet(
            effectiveSchema,
            dialect,
            [
                new ProjectSchemaInfo("ed-fi", "Ed-Fi", "5.2.0", false, _edfiSchema),
                new ProjectSchemaInfo("sample", "Sample", "1.0.0", true, _sampleSchema),
            ],
            concreteResources,
            [
                CreateEducationOrganizationIdentityTable(
                    resourceKeysByResource[_educationOrganizationResource]
                ),
            ],
            abstractUnionViews,
            [],
            []
        );

        return new MappingSet(
            new MappingSetKey("hash", dialect, "v1"),
            modelSet,
            new Dictionary<QualifiedResourceName, ResourceWritePlan>(),
            new Dictionary<QualifiedResourceName, ResourceReadPlan>(),
            resourceKeys.ToDictionary(
                static resourceKey => resourceKey.Resource,
                static resourceKey => resourceKey.ResourceKeyId
            ),
            resourceKeys.ToDictionary(
                static resourceKey => resourceKey.ResourceKeyId,
                static resourceKey => resourceKey
            ),
            new Dictionary<QualifiedResourceName, IReadOnlyList<ResolvedSecurableElementPath>>()
        );
    }

    private static ConcreteResourceModel CreateEdOrgConcreteResource(
        ResourceKeyEntry resourceKey,
        DbSchemaName schema,
        string tableName,
        string identityColumn,
        string identityPath,
        bool includeNameOfInstitution = true
    )
    {
        var columns = new List<DbColumnModel>
        {
            ScalarColumn(identityColumn, identityPath, ScalarKind.Int64),
        };

        if (includeNameOfInstitution)
        {
            columns.Add(ScalarColumn("NameOfInstitution", "$.nameOfInstitution"));
        }

        var rootTable = CreateRootTable(schema, tableName, columns);
        var model = new RelationalResourceModel(
            resourceKey.Resource,
            schema,
            ResourceStorageKind.RelationalTables,
            rootTable,
            [rootTable],
            [],
            []
        );

        return new ConcreteResourceModel(resourceKey, ResourceStorageKind.RelationalTables, model);
    }

    private static ConcreteResourceModel CreateDescriptorResource(ResourceKeyEntry resourceKey)
    {
        var rootTable = CreateRootTable(_dmsSchema, "Descriptor", []);
        var model = new RelationalResourceModel(
            resourceKey.Resource,
            _edfiSchema,
            ResourceStorageKind.SharedDescriptorTable,
            rootTable,
            [rootTable],
            [],
            []
        );

        return new ConcreteResourceModel(resourceKey, ResourceStorageKind.SharedDescriptorTable, model);
    }

    private static AbstractIdentityTableInfo CreateEducationOrganizationIdentityTable(
        ResourceKeyEntry resourceKey
    )
    {
        return new AbstractIdentityTableInfo(
            resourceKey,
            CreateRootTable(
                _edfiSchema,
                "EducationOrganizationIdentity",
                [
                    ScalarColumn("EducationOrganizationId", "$.educationOrganizationId", ScalarKind.Int64),
                    ScalarColumn("Discriminator", "$.discriminator"),
                ]
            )
        );
    }

    private static AbstractUnionViewInfo CreateEducationOrganizationUnionView(
        ResourceKeyEntry abstractResourceKey,
        IReadOnlyDictionary<QualifiedResourceName, ResourceKeyEntry> resourceKeysByResource,
        bool includeUnionArms = true
    )
    {
        var outputColumns = new[]
        {
            new AbstractUnionViewOutputColumn(
                new DbColumnName("DocumentId"),
                new RelationalScalarType(ScalarKind.Int64),
                null,
                null
            ),
            new AbstractUnionViewOutputColumn(
                new DbColumnName("EducationOrganizationId"),
                new RelationalScalarType(ScalarKind.Int64),
                Path("$.educationOrganizationId"),
                null
            ),
            new AbstractUnionViewOutputColumn(
                new DbColumnName("Discriminator"),
                new RelationalScalarType(ScalarKind.String, 256),
                null,
                null
            ),
        };
        IReadOnlyList<AbstractUnionViewArm> unionArms = includeUnionArms
            ?
            [
                CreateUnionArm(
                    resourceKeysByResource[_schoolResource],
                    _edfiSchema,
                    "School",
                    "SchoolId",
                    "Ed-Fi:School"
                ),
                CreateUnionArm(
                    resourceKeysByResource[_localEducationAgencyResource],
                    _edfiSchema,
                    "LocalEducationAgency",
                    "LocalEducationAgencyId",
                    "Ed-Fi:LocalEducationAgency"
                ),
                CreateUnionArm(
                    resourceKeysByResource[_customEducationOrganizationResource],
                    _sampleSchema,
                    "CustomEducationOrganization",
                    "CustomEducationOrganizationId",
                    "Sample:CustomEducationOrganization"
                ),
            ]
            : [];

        return new AbstractUnionViewInfo(
            abstractResourceKey,
            new DbTableName(_edfiSchema, "EducationOrganization_View"),
            outputColumns,
            unionArms
        );
    }

    private static AbstractUnionViewArm CreateUnionArm(
        ResourceKeyEntry concreteResourceKey,
        DbSchemaName schema,
        string tableName,
        string identityColumn,
        string discriminator
    )
    {
        return new AbstractUnionViewArm(
            concreteResourceKey,
            new DbTableName(schema, tableName),
            [
                new AbstractUnionViewProjectionExpression.SourceColumn(new DbColumnName("DocumentId")),
                new AbstractUnionViewProjectionExpression.SourceColumn(new DbColumnName(identityColumn)),
                new AbstractUnionViewProjectionExpression.StringLiteral(discriminator),
            ]
        );
    }

    private static DbTableModel CreateRootTable(
        DbSchemaName schema,
        string tableName,
        IReadOnlyList<DbColumnModel> extraColumns
    )
    {
        return new DbTableModel(
            new DbTableName(schema, tableName),
            Path("$"),
            new TableKey(
                $"PK_{tableName}",
                [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            [
                new DbColumnModel(
                    new DbColumnName("DocumentId"),
                    ColumnKind.ParentKeyPart,
                    new RelationalScalarType(ScalarKind.Int64),
                    false,
                    null,
                    null
                ),
                .. extraColumns,
            ],
            []
        );
    }

    private static DbColumnModel ScalarColumn(
        string columnName,
        string sourcePath,
        ScalarKind scalarKind = ScalarKind.String
    )
    {
        return new DbColumnModel(
            new DbColumnName(columnName),
            ColumnKind.Scalar,
            new RelationalScalarType(scalarKind),
            false,
            Path(sourcePath),
            null
        );
    }

    private static JsonPathExpression Path(string canonical)
    {
        return new JsonPathExpression(canonical, []);
    }
}

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
    public void It_should_emit_the_cte_chain_for_concrete_claimed_accessible_and_ancestor_rows()
    {
        var plan = Compile(SqlDialect.Pgsql);

        plan.EducationOrganizationSql.Should()
            .Contain(
                """
                WITH concrete_edorg AS (
                    SELECT
                        r."SchoolId" AS "EducationOrganizationId",
                        r."NameOfInstitution" AS "NameOfInstitution",
                        'Ed-Fi:School' AS "Discriminator"
                    FROM "edfi"."School" r
                    UNION ALL
                    SELECT
                        r."LocalEducationAgencyId" AS "EducationOrganizationId",
                        r."NameOfInstitution" AS "NameOfInstitution",
                        'Ed-Fi:LocalEducationAgency' AS "Discriminator"
                    FROM "edfi"."LocalEducationAgency" r
                    UNION ALL
                    SELECT
                        r."CustomEducationOrganizationId" AS "EducationOrganizationId",
                        r."NameOfInstitution" AS "NameOfInstitution",
                        'Sample:CustomEducationOrganization' AS "Discriminator"
                    FROM "sample"."CustomEducationOrganization" r
                ),
                claimed_edorg AS (
                    SELECT
                        c."EducationOrganizationId",
                        c."NameOfInstitution",
                        c."Discriminator"
                    FROM concrete_edorg c
                    WHERE c."EducationOrganizationId" = ANY(@ClaimEducationOrganizationIds)
                ),
                accessible_targets AS (
                    SELECT DISTINCT
                        h."TargetEducationOrganizationId" AS "EducationOrganizationId"
                    FROM "auth"."EducationOrganizationIdToEducationOrganizationId" h
                    INNER JOIN claimed_edorg c
                        ON h."SourceEducationOrganizationId" = c."EducationOrganizationId"
                    UNION
                    SELECT
                        c."EducationOrganizationId"
                    FROM claimed_edorg c
                ),
                ancestor_links AS (
                    SELECT
                        h."TargetEducationOrganizationId",
                        h."SourceEducationOrganizationId"
                    FROM "auth"."EducationOrganizationIdToEducationOrganizationId" h
                    INNER JOIN accessible_targets a
                        ON h."TargetEducationOrganizationId" = a."EducationOrganizationId"
                    UNION
                    SELECT
                        a."EducationOrganizationId" AS "TargetEducationOrganizationId",
                        a."EducationOrganizationId" AS "SourceEducationOrganizationId"
                    FROM accessible_targets a
                )
                SELECT
                """
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
    public void It_should_emit_the_final_result_select_with_target_and_ancestor_relationships()
    {
        var plan = Compile(SqlDialect.Pgsql);

        plan.EducationOrganizationSql.Should()
            .Contain(
                """
                SELECT
                    target."EducationOrganizationId" AS "EducationOrganizationId",
                    target."NameOfInstitution" AS "NameOfInstitution",
                    target."Discriminator" AS "Discriminator",
                    ancestor."Discriminator" AS "AncestorDiscriminator",
                    ancestor."EducationOrganizationId" AS "AncestorEducationOrganizationId"
                FROM accessible_targets a
                INNER JOIN concrete_edorg target
                    ON target."EducationOrganizationId" = a."EducationOrganizationId"
                INNER JOIN ancestor_links link
                    ON link."TargetEducationOrganizationId" = a."EducationOrganizationId"
                INNER JOIN concrete_edorg ancestor
                    ON ancestor."EducationOrganizationId" = link."SourceEducationOrganizationId"
                ORDER BY
                    target."EducationOrganizationId" ASC,
                    ancestor."EducationOrganizationId" ASC,
                    target."Discriminator" ASC,
                    ancestor."Discriminator" ASC;
                """
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

    [Test]
    public void It_should_fail_fast_when_mapping_set_model_dialect_differs_from_the_compiler()
    {
        var spec = CreateSpec(
            SqlDialect.Pgsql,
            static mappingSet =>
                mappingSet with
                {
                    Model = mappingSet.Model with { Dialect = SqlDialect.Mssql },
                }
        );

        AssertCompileThrows<ArgumentException>(
            spec,
            "*model dialect 'Mssql' does not match compiler dialect 'Pgsql'*"
        );
    }

    [Test]
    public void It_should_fail_fast_when_mapping_set_key_dialect_differs_from_the_compiler()
    {
        var spec = CreateSpec(
            SqlDialect.Pgsql,
            static mappingSet => mappingSet with { Key = mappingSet.Key with { Dialect = SqlDialect.Mssql } }
        );

        AssertCompileThrows<ArgumentException>(
            spec,
            "*key dialect 'Mssql' does not match compiler dialect 'Pgsql'*"
        );
    }

    [Test]
    public void It_should_fail_fast_when_mapping_set_has_duplicate_education_organization_union_views()
    {
        var spec = CreateSpec(
            SqlDialect.Pgsql,
            static mappingSet =>
                mappingSet with
                {
                    Model = mappingSet.Model with
                    {
                        AbstractUnionViewsInNameOrder =
                        [
                            .. mappingSet.Model.AbstractUnionViewsInNameOrder,
                            mappingSet.Model.AbstractUnionViewsInNameOrder.Single(),
                        ],
                    },
                }
        );

        AssertCompileThrows<InvalidOperationException>(
            spec,
            "*contains 2 abstract 'EducationOrganization' union views*"
        );
    }

    [Test]
    public void It_should_fail_fast_when_an_edorg_union_member_is_missing_from_concrete_resources()
    {
        var spec = CreateSpec(
            SqlDialect.Pgsql,
            static mappingSet =>
                mappingSet with
                {
                    Model = mappingSet.Model with
                    {
                        ConcreteResourcesInNameOrder = mappingSet
                            .Model.ConcreteResourcesInNameOrder.Where(static concrete =>
                                concrete.ResourceKey.Resource != _schoolResource
                            )
                            .ToArray(),
                    },
                }
        );

        AssertCompileThrows<InvalidOperationException>(
            spec,
            "*references concrete member 'Ed-Fi.School'*does not contain that concrete resource*"
        );
    }

    [Test]
    public void It_should_fail_fast_when_an_edorg_union_member_uses_non_relational_storage()
    {
        var spec = CreateSpec(
            SqlDialect.Pgsql,
            static mappingSet =>
                WithConcreteResource(
                    mappingSet,
                    _schoolResource,
                    static concrete =>
                        concrete with
                        {
                            StorageKind = ResourceStorageKind.SharedDescriptorTable,
                        }
                )
        );

        AssertCompileThrows<InvalidOperationException>(
            spec,
            "*member 'Ed-Fi.School' uses storage kind 'SharedDescriptorTable'*token_info requires relational-table storage*"
        );
    }

    [Test]
    public void It_should_fail_fast_when_the_edorg_union_view_lacks_a_discriminator_output()
    {
        var spec = CreateSpec(
            SqlDialect.Pgsql,
            static mappingSet =>
                WithEducationOrganizationUnionView(
                    mappingSet,
                    static view =>
                        view with
                        {
                            OutputColumnsInSelectOrder = view
                                .OutputColumnsInSelectOrder.Where(static column =>
                                    column.ColumnName.Value != "Discriminator"
                                )
                                .ToArray(),
                        }
                )
        );

        AssertCompileThrows<InvalidOperationException>(
            spec,
            "*does not expose output column 'Discriminator'*"
        );
    }

    [Test]
    public void It_should_fail_fast_when_the_edorg_union_view_has_duplicate_discriminator_outputs()
    {
        var spec = CreateSpec(
            SqlDialect.Pgsql,
            static mappingSet =>
                WithEducationOrganizationUnionView(
                    mappingSet,
                    static view =>
                        view with
                        {
                            OutputColumnsInSelectOrder =
                            [
                                .. view.OutputColumnsInSelectOrder,
                                view.OutputColumnsInSelectOrder.Single(static column =>
                                    column.ColumnName.Value == "Discriminator"
                                ),
                            ],
                        }
                )
        );

        AssertCompileThrows<InvalidOperationException>(
            spec,
            "*exposes duplicate output column 'Discriminator'*"
        );
    }

    [Test]
    public void It_should_fail_fast_when_the_edorg_union_view_lacks_an_identity_output()
    {
        var spec = CreateSpec(
            SqlDialect.Pgsql,
            static mappingSet =>
                WithEducationOrganizationUnionView(
                    mappingSet,
                    static view =>
                        view with
                        {
                            OutputColumnsInSelectOrder = view
                                .OutputColumnsInSelectOrder.Where(static column =>
                                    column.ColumnName.Value != "EducationOrganizationId"
                                )
                                .ToArray(),
                        }
                )
        );

        AssertCompileThrows<InvalidOperationException>(spec, "*does not expose an identity output column*");
    }

    [Test]
    public void It_should_fail_fast_when_the_edorg_union_view_has_duplicate_identity_outputs()
    {
        var spec = CreateSpec(
            SqlDialect.Pgsql,
            static mappingSet =>
                WithEducationOrganizationUnionView(
                    mappingSet,
                    static view =>
                        view with
                        {
                            OutputColumnsInSelectOrder =
                            [
                                .. view.OutputColumnsInSelectOrder,
                                new AbstractUnionViewOutputColumn(
                                    new DbColumnName("OtherEducationOrganizationId"),
                                    new RelationalScalarType(ScalarKind.Int64),
                                    Path("$.otherEducationOrganizationId"),
                                    null
                                ),
                            ],
                        }
                )
        );

        AssertCompileThrows<InvalidOperationException>(spec, "*exposes 2 identity output columns*");
    }

    [Test]
    public void It_should_fail_fast_when_an_edorg_union_arm_has_too_few_identity_projections()
    {
        var spec = CreateSpec(
            SqlDialect.Pgsql,
            static mappingSet =>
                WithFirstUnionArm(
                    mappingSet,
                    static arm =>
                        arm with
                        {
                            ProjectionExpressionsInSelectOrder = [arm.ProjectionExpressionsInSelectOrder[0]],
                        }
                )
        );

        AssertCompileThrows<InvalidOperationException>(
            spec,
            "*has too few projection expressions for output 'EducationOrganizationIdColumn'*"
        );
    }

    [Test]
    public void It_should_fail_fast_when_an_edorg_union_arm_identity_output_is_not_a_source_column()
    {
        var spec = CreateSpec(
            SqlDialect.Pgsql,
            static mappingSet =>
                WithFirstUnionArm(
                    mappingSet,
                    static arm =>
                        arm with
                        {
                            ProjectionExpressionsInSelectOrder =
                            [
                                arm.ProjectionExpressionsInSelectOrder[0],
                                new AbstractUnionViewProjectionExpression.StringLiteral("not-a-column"),
                                arm.ProjectionExpressionsInSelectOrder[2],
                            ],
                        }
                )
        );

        AssertCompileThrows<InvalidOperationException>(
            spec,
            "*must project output 'EducationOrganizationIdColumn' from a source column*"
        );
    }

    [Test]
    public void It_should_fail_fast_when_an_edorg_union_arm_has_too_few_discriminator_projections()
    {
        var spec = CreateSpec(
            SqlDialect.Pgsql,
            static mappingSet =>
                WithFirstUnionArm(
                    mappingSet,
                    static arm =>
                        arm with
                        {
                            ProjectionExpressionsInSelectOrder =
                            [
                                arm.ProjectionExpressionsInSelectOrder[0],
                                arm.ProjectionExpressionsInSelectOrder[1],
                            ],
                        }
                )
        );

        AssertCompileThrows<InvalidOperationException>(
            spec,
            "*has too few projection expressions for discriminator output*"
        );
    }

    [Test]
    public void It_should_fail_fast_when_an_edorg_union_arm_discriminator_output_is_not_a_literal()
    {
        var spec = CreateSpec(
            SqlDialect.Pgsql,
            static mappingSet =>
                WithFirstUnionArm(
                    mappingSet,
                    static arm =>
                        arm with
                        {
                            ProjectionExpressionsInSelectOrder =
                            [
                                arm.ProjectionExpressionsInSelectOrder[0],
                                arm.ProjectionExpressionsInSelectOrder[1],
                                new AbstractUnionViewProjectionExpression.SourceColumn(
                                    new DbColumnName("Discriminator")
                                ),
                            ],
                        }
                )
        );

        AssertCompileThrows<InvalidOperationException>(
            spec,
            "*must project discriminator from a string literal*"
        );
    }

    [Test]
    public void It_should_fail_fast_when_an_edorg_resource_has_duplicate_name_of_institution_columns()
    {
        var spec = CreateSpec(
            SqlDialect.Pgsql,
            static mappingSet =>
                WithConcreteResource(
                    mappingSet,
                    _schoolResource,
                    static concrete =>
                    {
                        var root = concrete.RelationalModel.Root with
                        {
                            Columns =
                            [
                                .. concrete.RelationalModel.Root.Columns,
                                ScalarColumn("AlternateNameOfInstitution", "$.nameOfInstitution"),
                            ],
                        };

                        return concrete with
                        {
                            RelationalModel = concrete.RelationalModel with
                            {
                                Root = root,
                                TablesInDependencyOrder = [root],
                            },
                        };
                    }
                )
        );

        AssertCompileThrows<InvalidOperationException>(
            spec,
            "*exposes duplicate root scalar columns for '$.nameOfInstitution'*"
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

    private static void AssertCompileThrows<TException>(
        TokenInfoEducationOrganizationSqlSpec spec,
        string expectedMessage
    )
        where TException : Exception
    {
        var compiler = new TokenInfoEducationOrganizationSqlCompiler(SqlDialect.Pgsql);
        var act = () => compiler.Compile(spec);

        act.Should().Throw<TException>().WithMessage(expectedMessage);
    }

    private static TokenInfoEducationOrganizationSqlSpec CreateSpec(
        SqlDialect dialect,
        Func<MappingSet, MappingSet> configureMappingSet
    )
    {
        return new TokenInfoEducationOrganizationSqlSpec(
            configureMappingSet(CreateMappingSet(dialect)),
            AuthorizationClaimEducationOrganizationIdParameterizationFactory.Create(
                dialect,
                [111L],
                "ClaimEducationOrganizationIds"
            )
        );
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

    private static MappingSet WithEducationOrganizationUnionView(
        MappingSet mappingSet,
        Func<AbstractUnionViewInfo, AbstractUnionViewInfo> configureUnionView
    )
    {
        return mappingSet with
        {
            Model = mappingSet.Model with
            {
                AbstractUnionViewsInNameOrder = mappingSet
                    .Model.AbstractUnionViewsInNameOrder.Select(view =>
                        view.AbstractResourceKey.Resource == _educationOrganizationResource
                            ? configureUnionView(view)
                            : view
                    )
                    .ToArray(),
            },
        };
    }

    private static MappingSet WithFirstUnionArm(
        MappingSet mappingSet,
        Func<AbstractUnionViewArm, AbstractUnionViewArm> configureArm
    )
    {
        return WithEducationOrganizationUnionView(
            mappingSet,
            view =>
            {
                var arms = view.UnionArmsInOrder.ToArray();
                arms[0] = configureArm(arms[0]);

                return view with
                {
                    UnionArmsInOrder = arms,
                };
            }
        );
    }

    private static MappingSet WithConcreteResource(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        Func<ConcreteResourceModel, ConcreteResourceModel> configureConcreteResource
    )
    {
        return mappingSet with
        {
            Model = mappingSet.Model with
            {
                ConcreteResourcesInNameOrder = mappingSet
                    .Model.ConcreteResourcesInNameOrder.Select(concrete =>
                        concrete.ResourceKey.Resource == resource
                            ? configureConcreteResource(concrete)
                            : concrete
                    )
                    .ToArray(),
            },
        };
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

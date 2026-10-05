// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using FluentAssertions;
using NUnit.Framework;
using Reason = EdFi.DataManagementService.Backend.External.EducationOrganizationProjectionMappingIncompatibilityReason;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

/// <summary>
/// The authoritative DS 5.2 mapping sets, compiled once for every projection compiler fixture.
/// </summary>
internal static class EducationOrganizationProjectionDs52MappingSets
{
    private static readonly Lazy<MappingSet> _pgsql = new(() =>
        Ds52FixtureHelper.BuildAndCompile(SqlDialect.Pgsql).MappingSet
    );
    private static readonly Lazy<MappingSet> _mssql = new(() =>
        Ds52FixtureHelper.BuildAndCompile(SqlDialect.Mssql).MappingSet
    );

    public static MappingSet For(SqlDialect dialect) =>
        dialect == SqlDialect.Pgsql ? _pgsql.Value : _mssql.Value;

    public static QualifiedResourceName Core(string resourceName) => new("Ed-Fi", resourceName);

    public static EducationOrganizationProjectionSqlPlan RequireCompiled(
        EducationOrganizationProjectionSqlCompilation compilation
    )
    {
        return compilation
            .Should()
            .BeOfType<EducationOrganizationProjectionSqlCompilation.Compiled>()
            .Subject.Plan;
    }
}

[TestFixture]
[Parallelizable]
public class Given_EducationOrganizationProjectionSqlCompiler_Over_The_Ds52_PostgreSQL_Model
{
    private EducationOrganizationProjectionSqlPlan _plan = null!;

    [SetUp]
    public void Setup()
    {
        _plan = EducationOrganizationProjectionDs52MappingSets.RequireCompiled(
            new EducationOrganizationProjectionSqlCompiler(SqlDialect.Pgsql).Compile(
                EducationOrganizationProjectionDs52MappingSets.For(SqlDialect.Pgsql)
            )
        );
    }

    [Test]
    public void It_selects_exactly_the_four_core_arms_of_the_nine()
    {
        _plan
            .ArmsInOrder.Select(static arm => arm.Resource)
            .Should()
            .Equal(
                EducationOrganizationProjectionDs52MappingSets.Core("StateEducationAgency"),
                EducationOrganizationProjectionDs52MappingSets.Core("EducationServiceCenter"),
                EducationOrganizationProjectionDs52MappingSets.Core("LocalEducationAgency"),
                EducationOrganizationProjectionDs52MappingSets.Core("School")
            );
    }

    [Test]
    public void It_keeps_each_local_education_agency_reference_in_its_own_slot()
    {
        _plan
            .ArmsInOrder[2]
            .Should()
            .Be(
                new EducationOrganizationProjectionArm(
                    EducationOrganizationProjectionDs52MappingSets.Core("LocalEducationAgency"),
                    new DbTableName(new DbSchemaName("edfi"), "LocalEducationAgency"),
                    "Ed-Fi:LocalEducationAgency",
                    new DbColumnName("LocalEducationAgencyId"),
                    new DbColumnName("NameOfInstitution"),
                    new DbColumnName("ShortNameOfInstitution"),
                    LocalEducationAgencyReferenceColumn: null,
                    ParentLocalEducationAgencyReferenceColumn: new DbColumnName(
                        "ParentLocalEducationAgency_LocalEducationAgencyId"
                    ),
                    EducationServiceCenterReferenceColumn: new DbColumnName(
                        "EducationServiceCenter_EducationServiceCenterId"
                    ),
                    StateEducationAgencyReferenceColumn: new DbColumnName(
                        "StateEducationAgency_StateEducationAgencyId"
                    )
                )
            );
    }

    [Test]
    public void It_returns_the_names_as_text()
    {
        _plan.NameEncoding.Should().Be(EducationOrganizationProjectionNameEncoding.Text);
    }

    [Test]
    public void It_binds_the_row_limit_as_a_limit_parameter()
    {
        _plan.RowLimitParameter.Should().Be(new QuerySqlParameter(QuerySqlParameterRole.Limit, "RowLimit"));
    }

    [Test]
    public void It_emits_the_complete_postgresql_statement()
    {
        _plan
            .Sql.Should()
            .Be(
                """
                SELECT
                    p."EducationOrganizationId",
                    p."Discriminator",
                    p."NameOfInstitution",
                    p."ShortNameOfInstitution",
                    p."LocalEducationAgencyReference",
                    p."ParentLocalEducationAgencyReference",
                    p."EducationServiceCenterReference",
                    p."StateEducationAgencyReference"
                FROM (
                    SELECT
                        r."StateEducationAgencyId" AS "EducationOrganizationId",
                        'Ed-Fi:StateEducationAgency' AS "Discriminator",
                        r."NameOfInstitution" AS "NameOfInstitution",
                        r."ShortNameOfInstitution" AS "ShortNameOfInstitution",
                        CAST(NULL AS bigint) AS "LocalEducationAgencyReference",
                        CAST(NULL AS bigint) AS "ParentLocalEducationAgencyReference",
                        CAST(NULL AS bigint) AS "EducationServiceCenterReference",
                        CAST(NULL AS bigint) AS "StateEducationAgencyReference"
                    FROM "edfi"."StateEducationAgency" r
                    UNION ALL
                    SELECT
                        r."EducationServiceCenterId" AS "EducationOrganizationId",
                        'Ed-Fi:EducationServiceCenter' AS "Discriminator",
                        r."NameOfInstitution" AS "NameOfInstitution",
                        r."ShortNameOfInstitution" AS "ShortNameOfInstitution",
                        CAST(NULL AS bigint) AS "LocalEducationAgencyReference",
                        CAST(NULL AS bigint) AS "ParentLocalEducationAgencyReference",
                        CAST(NULL AS bigint) AS "EducationServiceCenterReference",
                        r."StateEducationAgency_StateEducationAgencyId" AS "StateEducationAgencyReference"
                    FROM "edfi"."EducationServiceCenter" r
                    UNION ALL
                    SELECT
                        r."LocalEducationAgencyId" AS "EducationOrganizationId",
                        'Ed-Fi:LocalEducationAgency' AS "Discriminator",
                        r."NameOfInstitution" AS "NameOfInstitution",
                        r."ShortNameOfInstitution" AS "ShortNameOfInstitution",
                        CAST(NULL AS bigint) AS "LocalEducationAgencyReference",
                        r."ParentLocalEducationAgency_LocalEducationAgencyId" AS "ParentLocalEducationAgencyReference",
                        r."EducationServiceCenter_EducationServiceCenterId" AS "EducationServiceCenterReference",
                        r."StateEducationAgency_StateEducationAgencyId" AS "StateEducationAgencyReference"
                    FROM "edfi"."LocalEducationAgency" r
                    UNION ALL
                    SELECT
                        r."SchoolId" AS "EducationOrganizationId",
                        'Ed-Fi:School' AS "Discriminator",
                        r."NameOfInstitution" AS "NameOfInstitution",
                        r."ShortNameOfInstitution" AS "ShortNameOfInstitution",
                        r."LocalEducationAgency_LocalEducationAgencyId" AS "LocalEducationAgencyReference",
                        CAST(NULL AS bigint) AS "ParentLocalEducationAgencyReference",
                        CAST(NULL AS bigint) AS "EducationServiceCenterReference",
                        CAST(NULL AS bigint) AS "StateEducationAgencyReference"
                    FROM "edfi"."School" r
                ) p
                ORDER BY p."EducationOrganizationId" ASC
                LIMIT @RowLimit;

                """
            );
    }

    [Test]
    public void It_does_not_read_any_other_education_organization_type()
    {
        foreach (
            var excluded in new[]
            {
                "CommunityOrganization",
                "CommunityProvider",
                "EducationOrganizationNetwork",
                "OrganizationDepartment",
                "PostSecondaryInstitution",
                "EducationOrganization_View",
            }
        )
        {
            _plan.Sql.Should().NotContain(excluded);
        }
    }
}

[TestFixture]
[Parallelizable]
public class Given_EducationOrganizationProjectionSqlCompiler_Over_The_Ds52_SqlServer_Model
{
    private EducationOrganizationProjectionSqlPlan _plan = null!;

    [SetUp]
    public void Setup()
    {
        _plan = EducationOrganizationProjectionDs52MappingSets.RequireCompiled(
            new EducationOrganizationProjectionSqlCompiler(SqlDialect.Mssql).Compile(
                EducationOrganizationProjectionDs52MappingSets.For(SqlDialect.Mssql)
            )
        );
    }

    [Test]
    public void It_returns_the_names_as_their_stored_utf16_code_units()
    {
        _plan.NameEncoding.Should().Be(EducationOrganizationProjectionNameEncoding.Utf16LittleEndianBytes);
    }

    [Test]
    public void It_selects_the_same_four_core_arms_as_postgresql()
    {
        var pgsqlPlan = EducationOrganizationProjectionDs52MappingSets.RequireCompiled(
            new EducationOrganizationProjectionSqlCompiler(SqlDialect.Pgsql).Compile(
                EducationOrganizationProjectionDs52MappingSets.For(SqlDialect.Pgsql)
            )
        );

        _plan.ArmsInOrder.Should().Equal(pgsqlPlan.ArmsInOrder);
    }

    [Test]
    public void It_emits_the_complete_sql_server_statement()
    {
        _plan
            .Sql.Should()
            .Be(
                """
                SELECT TOP (@RowLimit)
                    p.[EducationOrganizationId],
                    p.[Discriminator],
                    p.[NameOfInstitution],
                    p.[ShortNameOfInstitution],
                    p.[LocalEducationAgencyReference],
                    p.[ParentLocalEducationAgencyReference],
                    p.[EducationServiceCenterReference],
                    p.[StateEducationAgencyReference]
                FROM (
                    SELECT
                        r.[StateEducationAgencyId] AS [EducationOrganizationId],
                        N'Ed-Fi:StateEducationAgency' AS [Discriminator],
                        CAST(CONVERT(nvarchar(max), r.[NameOfInstitution] COLLATE Latin1_General_100_BIN2) AS varbinary(max)) AS [NameOfInstitution],
                        CAST(CONVERT(nvarchar(max), r.[ShortNameOfInstitution] COLLATE Latin1_General_100_BIN2) AS varbinary(max)) AS [ShortNameOfInstitution],
                        CAST(NULL AS bigint) AS [LocalEducationAgencyReference],
                        CAST(NULL AS bigint) AS [ParentLocalEducationAgencyReference],
                        CAST(NULL AS bigint) AS [EducationServiceCenterReference],
                        CAST(NULL AS bigint) AS [StateEducationAgencyReference]
                    FROM [edfi].[StateEducationAgency] r
                    UNION ALL
                    SELECT
                        r.[EducationServiceCenterId] AS [EducationOrganizationId],
                        N'Ed-Fi:EducationServiceCenter' AS [Discriminator],
                        CAST(CONVERT(nvarchar(max), r.[NameOfInstitution] COLLATE Latin1_General_100_BIN2) AS varbinary(max)) AS [NameOfInstitution],
                        CAST(CONVERT(nvarchar(max), r.[ShortNameOfInstitution] COLLATE Latin1_General_100_BIN2) AS varbinary(max)) AS [ShortNameOfInstitution],
                        CAST(NULL AS bigint) AS [LocalEducationAgencyReference],
                        CAST(NULL AS bigint) AS [ParentLocalEducationAgencyReference],
                        CAST(NULL AS bigint) AS [EducationServiceCenterReference],
                        r.[StateEducationAgency_StateEducationAgencyId] AS [StateEducationAgencyReference]
                    FROM [edfi].[EducationServiceCenter] r
                    UNION ALL
                    SELECT
                        r.[LocalEducationAgencyId] AS [EducationOrganizationId],
                        N'Ed-Fi:LocalEducationAgency' AS [Discriminator],
                        CAST(CONVERT(nvarchar(max), r.[NameOfInstitution] COLLATE Latin1_General_100_BIN2) AS varbinary(max)) AS [NameOfInstitution],
                        CAST(CONVERT(nvarchar(max), r.[ShortNameOfInstitution] COLLATE Latin1_General_100_BIN2) AS varbinary(max)) AS [ShortNameOfInstitution],
                        CAST(NULL AS bigint) AS [LocalEducationAgencyReference],
                        r.[ParentLocalEducationAgency_LocalEducationAgencyId] AS [ParentLocalEducationAgencyReference],
                        r.[EducationServiceCenter_EducationServiceCenterId] AS [EducationServiceCenterReference],
                        r.[StateEducationAgency_StateEducationAgencyId] AS [StateEducationAgencyReference]
                    FROM [edfi].[LocalEducationAgency] r
                    UNION ALL
                    SELECT
                        r.[SchoolId] AS [EducationOrganizationId],
                        N'Ed-Fi:School' AS [Discriminator],
                        CAST(CONVERT(nvarchar(max), r.[NameOfInstitution] COLLATE Latin1_General_100_BIN2) AS varbinary(max)) AS [NameOfInstitution],
                        CAST(CONVERT(nvarchar(max), r.[ShortNameOfInstitution] COLLATE Latin1_General_100_BIN2) AS varbinary(max)) AS [ShortNameOfInstitution],
                        r.[LocalEducationAgency_LocalEducationAgencyId] AS [LocalEducationAgencyReference],
                        CAST(NULL AS bigint) AS [ParentLocalEducationAgencyReference],
                        CAST(NULL AS bigint) AS [EducationServiceCenterReference],
                        CAST(NULL AS bigint) AS [StateEducationAgencyReference]
                    FROM [edfi].[School] r
                ) p
                ORDER BY p.[EducationOrganizationId] ASC;

                """
            );
    }

    [Test]
    public void It_limits_rows_in_the_select_list_and_not_with_a_trailing_clause()
    {
        _plan.Sql.Should().StartWith("SELECT TOP (@RowLimit)\n");
        _plan.Sql.Should().NotContain("LIMIT");
        _plan.Sql.Should().NotContain("FETCH");
    }
}

[TestFixture]
[Parallelizable]
public class Given_EducationOrganizationProjectionSqlCompiler_With_Extension_Education_Organization_Arms
{
    private static readonly QualifiedResourceName _extensionSchool = new("Sample", "School");
    private static readonly QualifiedResourceName _extensionCustom = new(
        "Sample",
        "CustomEducationOrganization"
    );

    private EducationOrganizationProjectionSqlPlan _baselinePlan = null!;
    private EducationOrganizationProjectionSqlPlan _plan = null!;

    [SetUp]
    public void Setup()
    {
        var compiler = new EducationOrganizationProjectionSqlCompiler(SqlDialect.Pgsql);
        var mappingSet = EducationOrganizationProjectionDs52MappingSets.For(SqlDialect.Pgsql);

        _baselinePlan = EducationOrganizationProjectionDs52MappingSets.RequireCompiled(
            compiler.Compile(mappingSet)
        );

        // An extension project's arms, including one whose resource name equals a core type's: the
        // project name, not the resource name, makes a type core.
        _plan = EducationOrganizationProjectionDs52MappingSets.RequireCompiled(
            compiler.Compile(
                ProjectionMappingSetMutations.WithEducationOrganizationUnionView(
                    mappingSet,
                    view =>
                        view with
                        {
                            UnionArmsInOrder =
                            [
                                .. view.UnionArmsInOrder,
                                ProjectionMappingSetMutations.CloneArmAs(
                                    view,
                                    EducationOrganizationProjectionDs52MappingSets.Core("School"),
                                    _extensionSchool
                                ),
                                ProjectionMappingSetMutations.CloneArmAs(
                                    view,
                                    EducationOrganizationProjectionDs52MappingSets.Core("School"),
                                    _extensionCustom
                                ),
                            ],
                        }
                )
            )
        );
    }

    [Test]
    public void It_ignores_the_extension_arms()
    {
        _plan.ArmsInOrder.Should().Equal(_baselinePlan.ArmsInOrder);
    }

    [Test]
    public void It_emits_the_same_statement_as_without_them()
    {
        _plan.Sql.Should().Be(_baselinePlan.Sql);
        _plan.Sql.Should().NotContain("Sample");
    }
}

[TestFixture]
[Parallelizable]
public class Given_EducationOrganizationProjectionSqlCompiler_With_An_Incompatible_Mapping_Set
{
    private static readonly QualifiedResourceName _school =
        EducationOrganizationProjectionDs52MappingSets.Core("School");
    private static readonly QualifiedResourceName _localEducationAgency =
        EducationOrganizationProjectionDs52MappingSets.Core("LocalEducationAgency");
    private static readonly QualifiedResourceName _educationServiceCenter =
        EducationOrganizationProjectionDs52MappingSets.Core("EducationServiceCenter");

    private MappingSet _mappingSet = null!;
    private EducationOrganizationProjectionSqlCompiler _compiler = null!;

    [SetUp]
    public void Setup()
    {
        _mappingSet = EducationOrganizationProjectionDs52MappingSets.For(SqlDialect.Pgsql);
        _compiler = new EducationOrganizationProjectionSqlCompiler(SqlDialect.Pgsql);
    }

    [Test]
    public void It_reports_a_missing_union_view()
    {
        var mappingSet = _mappingSet with
        {
            Model = _mappingSet.Model with
            {
                AbstractUnionViewsInNameOrder =
                [
                    .. _mappingSet.Model.AbstractUnionViewsInNameOrder.Where(static view =>
                        view.AbstractResourceKey.Resource.ResourceName != "EducationOrganization"
                    ),
                ],
            },
        };

        AssertIncompatible(mappingSet, Reason.UnionViewMissing, resourceName: null);
    }

    [Test]
    public void It_reports_an_ambiguous_union_view()
    {
        var view = ProjectionMappingSetMutations.EducationOrganizationUnionView(_mappingSet);
        var mappingSet = _mappingSet with
        {
            Model = _mappingSet.Model with
            {
                AbstractUnionViewsInNameOrder = [.. _mappingSet.Model.AbstractUnionViewsInNameOrder, view],
            },
        };

        AssertIncompatible(mappingSet, Reason.UnionViewAmbiguous, resourceName: null);
    }

    [Test]
    public void It_reports_a_union_view_without_a_single_identity_output()
    {
        var mappingSet = ProjectionMappingSetMutations.WithEducationOrganizationUnionView(
            _mappingSet,
            view =>
                view with
                {
                    OutputColumnsInSelectOrder =
                    [
                        .. view.OutputColumnsInSelectOrder,
                        view.OutputColumnsInSelectOrder.Single(static column =>
                            column.ColumnName.Value == "EducationOrganizationId"
                        ) with
                        {
                            ColumnName = new DbColumnName("OtherId"),
                        },
                    ],
                }
        );

        AssertIncompatible(mappingSet, Reason.IdentityOutputUnresolved, resourceName: null);
    }

    [Test]
    public void It_reports_a_union_view_without_a_discriminator_output()
    {
        var mappingSet = ProjectionMappingSetMutations.WithEducationOrganizationUnionView(
            _mappingSet,
            view =>
                view with
                {
                    OutputColumnsInSelectOrder =
                    [
                        .. view.OutputColumnsInSelectOrder.Select(static column =>
                            column.ColumnName.Value == "Discriminator"
                                ? column with
                                {
                                    ColumnName = new DbColumnName("DocumentId"),
                                }
                                : column
                        ),
                    ],
                }
        );

        AssertIncompatible(mappingSet, Reason.DiscriminatorOutputUnresolved, resourceName: null);
    }

    [Test]
    public void It_reports_a_missing_core_arm_by_resource_name()
    {
        var mappingSet = ProjectionMappingSetMutations.WithEducationOrganizationUnionView(
            _mappingSet,
            view =>
                view with
                {
                    UnionArmsInOrder =
                    [
                        .. view.UnionArmsInOrder.Where(static arm =>
                            arm.ConcreteMemberResourceKey.Resource != _school
                        ),
                    ],
                }
        );

        AssertIncompatible(mappingSet, Reason.ArmMissing, "School");
    }

    [Test]
    public void It_reports_an_ambiguous_core_arm()
    {
        var mappingSet = ProjectionMappingSetMutations.WithEducationOrganizationUnionView(
            _mappingSet,
            view =>
                view with
                {
                    UnionArmsInOrder =
                    [
                        .. view.UnionArmsInOrder,
                        view.UnionArmsInOrder.Single(static arm =>
                            arm.ConcreteMemberResourceKey.Resource == _localEducationAgency
                        ),
                    ],
                }
        );

        AssertIncompatible(mappingSet, Reason.ArmAmbiguous, "LocalEducationAgency");
    }

    [Test]
    public void It_reports_a_core_arm_without_a_concrete_resource()
    {
        var mappingSet = _mappingSet with
        {
            Model = _mappingSet.Model with
            {
                ConcreteResourcesInNameOrder =
                [
                    .. _mappingSet.Model.ConcreteResourcesInNameOrder.Where(static concrete =>
                        concrete.ResourceKey.Resource != _educationServiceCenter
                    ),
                ],
            },
        };

        AssertIncompatible(mappingSet, Reason.ConcreteResourceMissing, "EducationServiceCenter");
    }

    [Test]
    public void It_reports_a_core_type_not_stored_in_relational_tables()
    {
        var mappingSet = ProjectionMappingSetMutations.WithConcreteResource(
            _mappingSet,
            _school,
            static concrete => concrete with { StorageKind = ResourceStorageKind.SharedDescriptorTable }
        );

        AssertIncompatible(mappingSet, Reason.ResourceNotRelational, "School");
    }

    [Test]
    public void It_reports_a_core_arm_whose_discriminator_is_not_the_expected_literal()
    {
        var mappingSet = ProjectionMappingSetMutations.WithArmProjection(
            _mappingSet,
            _school,
            "Discriminator",
            new AbstractUnionViewProjectionExpression.StringLiteral("Ed-Fi:Academy")
        );

        AssertIncompatible(mappingSet, Reason.DiscriminatorUnexpected, "School");
    }

    [Test]
    public void It_reports_a_core_arm_whose_identity_is_not_a_source_column()
    {
        var mappingSet = ProjectionMappingSetMutations.WithArmProjection(
            _mappingSet,
            _school,
            "EducationOrganizationId",
            new AbstractUnionViewProjectionExpression.StringLiteral("1")
        );

        AssertIncompatible(mappingSet, Reason.IdentityColumnUnresolved, "School");
    }

    [Test]
    public void It_reports_a_missing_name_column()
    {
        var mappingSet = ProjectionMappingSetMutations.WithRootColumns(
            _mappingSet,
            _school,
            static columns => columns.Where(static column => column.ColumnName.Value != "NameOfInstitution")
        );

        AssertIncompatible(mappingSet, Reason.ColumnMissing, "School");
    }

    [Test]
    public void It_reports_a_missing_short_name_column()
    {
        var mappingSet = ProjectionMappingSetMutations.WithRootColumns(
            _mappingSet,
            _educationServiceCenter,
            static columns =>
                columns.Where(static column => column.ColumnName.Value != "ShortNameOfInstitution")
        );

        AssertIncompatible(mappingSet, Reason.ColumnMissing, "EducationServiceCenter");
    }

    [Test]
    public void It_reports_two_columns_claiming_the_name_path()
    {
        var mappingSet = ProjectionMappingSetMutations.WithRootColumns(
            _mappingSet,
            _school,
            static columns =>
                columns.Concat(
                    columns
                        .Where(static column => column.ColumnName.Value == "NameOfInstitution")
                        .Select(static column => column with { ColumnName = new DbColumnName("OtherName") })
                )
        );

        AssertIncompatible(mappingSet, Reason.ColumnAmbiguous, "School");
    }

    [Test]
    public void It_reports_a_nullable_name_column()
    {
        var mappingSet = ProjectionMappingSetMutations.WithRootColumns(
            _mappingSet,
            _school,
            static columns =>
                columns.Select(static column =>
                    column.ColumnName.Value == "NameOfInstitution"
                        ? column with
                        {
                            IsNullable = true,
                        }
                        : column
                )
        );

        AssertIncompatible(mappingSet, Reason.ColumnTypeIncompatible, "School");
    }

    [Test]
    public void It_reports_a_reference_column_of_another_type()
    {
        var mappingSet = ProjectionMappingSetMutations.WithRootColumns(
            _mappingSet,
            _localEducationAgency,
            static columns =>
                columns.Select(static column =>
                    column.ColumnName.Value == "EducationServiceCenter_EducationServiceCenterId"
                        ? column with
                        {
                            ScalarType = new RelationalScalarType(ScalarKind.Int32),
                        }
                        : column
                )
        );

        AssertIncompatible(mappingSet, Reason.ColumnTypeIncompatible, "LocalEducationAgency");
    }

    [Test]
    public void It_reports_a_missing_binding_for_a_reference_precedence_would_not_select()
    {
        // The state agency is the lowest-precedence parent of a local education agency, but every
        // populated reference is validated, so its slot is required.
        var mappingSet = ProjectionMappingSetMutations.WithReferenceBindings(
            _mappingSet,
            _localEducationAgency,
            static bindings =>
                bindings.Where(static binding =>
                    binding.ReferenceObjectPath.Canonical != "$.stateEducationAgencyReference"
                )
        );

        AssertIncompatible(mappingSet, Reason.ReferenceBindingMissing, "LocalEducationAgency");
    }

    [Test]
    public void It_reports_an_ambiguous_reference_binding()
    {
        var mappingSet = ProjectionMappingSetMutations.WithReferenceBindings(
            _mappingSet,
            _school,
            static bindings =>
                bindings.Concat(
                    bindings.Where(static binding =>
                        binding.ReferenceObjectPath.Canonical == "$.localEducationAgencyReference"
                    )
                )
        );

        AssertIncompatible(mappingSet, Reason.ReferenceBindingAmbiguous, "School");
    }

    [Test]
    public void It_reports_a_reference_binding_to_another_resource()
    {
        var mappingSet = ProjectionMappingSetMutations.WithReferenceBindings(
            _mappingSet,
            _localEducationAgency,
            static bindings =>
                bindings.Select(static binding =>
                    binding.ReferenceObjectPath.Canonical == "$.parentLocalEducationAgencyReference"
                        ? binding with
                        {
                            TargetResource = _educationServiceCenter,
                        }
                        : binding
                )
        );

        AssertIncompatible(mappingSet, Reason.ReferenceBindingIncompatible, "LocalEducationAgency");
    }

    [Test]
    public void It_reports_a_reference_binding_that_stores_more_than_the_target_identifier()
    {
        var mappingSet = ProjectionMappingSetMutations.WithReferenceBindings(
            _mappingSet,
            _school,
            static bindings =>
                bindings.Select(static binding =>
                    binding.ReferenceObjectPath.Canonical == "$.localEducationAgencyReference"
                        ? binding with
                        {
                            IdentityBindings = [.. binding.IdentityBindings, .. binding.IdentityBindings],
                        }
                        : binding
                )
        );

        AssertIncompatible(mappingSet, Reason.ReferenceBindingIncompatible, "School");
    }

    [Test]
    public void It_throws_for_a_mapping_set_compiled_for_another_dialect()
    {
        var act = () => new EducationOrganizationProjectionSqlCompiler(SqlDialect.Mssql).Compile(_mappingSet);

        act.Should().Throw<ArgumentException>().WithMessage("*dialect*");
    }

    private void AssertIncompatible(MappingSet mappingSet, Reason reason, string? resourceName)
    {
        _compiler
            .Compile(mappingSet)
            .Should()
            .Be(
                new EducationOrganizationProjectionSqlCompilation.Incompatible(
                    new EducationOrganizationProjectionMappingIncompatibility(reason, resourceName)
                )
            );
    }
}

[TestFixture]
[Parallelizable]
public class Given_EducationOrganizationProjectionSetResult_Row_Cap
{
    private static readonly EducationOrganizationProjectionRow _row = new(
        1,
        "Ed-Fi:StateEducationAgency",
        "State",
        null,
        null,
        null,
        null,
        null
    );

    [Test]
    public void It_reads_one_row_more_than_the_cap()
    {
        EducationOrganizationProjectionSetResult.RowLimitFor(50_000).Should().Be(50_001);
    }

    [Test]
    public void It_returns_a_set_that_fills_the_cap_exactly()
    {
        IReadOnlyList<EducationOrganizationProjectionRow> rows =
        [
            _row,
            _row with
            {
                EducationOrganizationId = 2,
            },
        ];

        EducationOrganizationProjectionSetResult
            .FromCappedRows(rows, maxProjectionRows: 2)
            .Should()
            .Be(new EducationOrganizationProjectionSetResult.Set(rows));
    }

    [Test]
    public void It_returns_too_large_and_no_rows_for_one_row_over_the_cap()
    {
        IReadOnlyList<EducationOrganizationProjectionRow> rows =
        [
            _row,
            _row with
            {
                EducationOrganizationId = 2,
            },
            _row with
            {
                EducationOrganizationId = 3,
            },
        ];

        EducationOrganizationProjectionSetResult
            .FromCappedRows(rows, maxProjectionRows: 2)
            .Should()
            .Be(new EducationOrganizationProjectionSetResult.TooLarge(2));
    }

    [Test]
    public void It_returns_an_empty_set_for_an_empty_store()
    {
        EducationOrganizationProjectionSetResult
            .FromCappedRows([], maxProjectionRows: 1)
            .Should()
            .BeOfType<EducationOrganizationProjectionSetResult.Set>()
            .Which.Rows.Should()
            .BeEmpty();
    }

    [Test]
    public void It_rejects_a_cap_with_no_representable_row_limit()
    {
        var act = () => EducationOrganizationProjectionSetResult.RowLimitFor(int.MaxValue);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Test]
    public void It_rejects_a_cap_that_is_not_positive()
    {
        var act = () => EducationOrganizationProjectionSetResult.FromCappedRows([], maxProjectionRows: 0);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}

/// <summary>
/// Derives incompatible variants of a real mapping set, one element at a time.
/// </summary>
internal static class ProjectionMappingSetMutations
{
    public static AbstractUnionViewInfo EducationOrganizationUnionView(MappingSet mappingSet) =>
        mappingSet.Model.AbstractUnionViewsInNameOrder.Single(static view =>
            view.AbstractResourceKey.Resource == new QualifiedResourceName("Ed-Fi", "EducationOrganization")
        );

    public static MappingSet WithEducationOrganizationUnionView(
        MappingSet mappingSet,
        Func<AbstractUnionViewInfo, AbstractUnionViewInfo> mutate
    )
    {
        var original = EducationOrganizationUnionView(mappingSet);

        return mappingSet with
        {
            Model = mappingSet.Model with
            {
                AbstractUnionViewsInNameOrder =
                [
                    .. mappingSet.Model.AbstractUnionViewsInNameOrder.Select(view =>
                        ReferenceEquals(view, original) ? mutate(view) : view
                    ),
                ],
            },
        };
    }

    public static AbstractUnionViewArm CloneArmAs(
        AbstractUnionViewInfo view,
        QualifiedResourceName source,
        QualifiedResourceName target
    )
    {
        var arm = view.UnionArmsInOrder.Single(arm => arm.ConcreteMemberResourceKey.Resource == source);
        var discriminatorIndex = view
            .OutputColumnsInSelectOrder.Select((column, index) => (column, index))
            .Single(static entry => entry.column.ColumnName.Value == "Discriminator")
            .index;

        return arm with
        {
            ConcreteMemberResourceKey = arm.ConcreteMemberResourceKey with { Resource = target },
            FromTable = new DbTableName(
                new DbSchemaName(target.ProjectName.ToLowerInvariant()),
                target.ResourceName
            ),
            ProjectionExpressionsInSelectOrder =
            [
                .. arm.ProjectionExpressionsInSelectOrder.Select(
                    (expression, index) =>
                        index == discriminatorIndex
                            ? new AbstractUnionViewProjectionExpression.StringLiteral(
                                $"{target.ProjectName}:{target.ResourceName}"
                            )
                            : expression
                ),
            ],
        };
    }

    public static MappingSet WithArmProjection(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        string outputColumnName,
        AbstractUnionViewProjectionExpression replacement
    )
    {
        return WithEducationOrganizationUnionView(
            mappingSet,
            view =>
            {
                var outputIndex = view
                    .OutputColumnsInSelectOrder.Select((column, index) => (column, index))
                    .Single(entry => entry.column.ColumnName.Value == outputColumnName)
                    .index;

                return view with
                {
                    UnionArmsInOrder =
                    [
                        .. view.UnionArmsInOrder.Select(arm =>
                            arm.ConcreteMemberResourceKey.Resource == resource
                                ? arm with
                                {
                                    ProjectionExpressionsInSelectOrder =
                                    [
                                        .. arm.ProjectionExpressionsInSelectOrder.Select(
                                            (expression, index) =>
                                                index == outputIndex ? replacement : expression
                                        ),
                                    ],
                                }
                                : arm
                        ),
                    ],
                };
            }
        );
    }

    public static MappingSet WithConcreteResource(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        Func<ConcreteResourceModel, ConcreteResourceModel> mutate
    )
    {
        return mappingSet with
        {
            Model = mappingSet.Model with
            {
                ConcreteResourcesInNameOrder =
                [
                    .. mappingSet.Model.ConcreteResourcesInNameOrder.Select(concrete =>
                        concrete.ResourceKey.Resource == resource ? mutate(concrete) : concrete
                    ),
                ],
            },
        };
    }

    public static MappingSet WithRootColumns(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        Func<IReadOnlyList<DbColumnModel>, IEnumerable<DbColumnModel>> mutate
    )
    {
        return WithConcreteResource(
            mappingSet,
            resource,
            concrete =>
                concrete with
                {
                    RelationalModel = concrete.RelationalModel with
                    {
                        Root = concrete.RelationalModel.Root with
                        {
                            Columns = [.. mutate(concrete.RelationalModel.Root.Columns)],
                        },
                    },
                }
        );
    }

    public static MappingSet WithReferenceBindings(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        Func<IReadOnlyList<DocumentReferenceBinding>, IEnumerable<DocumentReferenceBinding>> mutate
    )
    {
        return WithConcreteResource(
            mappingSet,
            resource,
            concrete =>
                concrete with
                {
                    RelationalModel = concrete.RelationalModel with
                    {
                        DocumentReferenceBindings =
                        [
                            .. mutate(concrete.RelationalModel.DocumentReferenceBindings),
                        ],
                    },
                }
        );
    }
}

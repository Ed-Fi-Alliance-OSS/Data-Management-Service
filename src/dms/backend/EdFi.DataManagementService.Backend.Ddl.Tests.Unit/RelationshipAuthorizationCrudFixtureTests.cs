// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Tests.Common;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Ddl.Tests.Unit;

[TestFixture(SqlDialect.Pgsql)]
[TestFixture(SqlDialect.Mssql)]
public class Given_A_Relationship_Authorization_Crud_Synthetic_Fixture(SqlDialect dialect)
{
    private DerivedRelationalModelSet _modelSet = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var fixtureDirectory = FixturePathResolver.ResolveRepositoryRelativePath(
            TestContext.CurrentContext.TestDirectory,
            RelationshipAuthorizationCrudTestSupport.FixtureRelativePath
        );
        var effectiveSchemaSet = EffectiveSchemaFixtureLoader.LoadFromFixtureDirectory(fixtureDirectory);
        (_modelSet, _) = DdlPipelineHelpers.BuildDdlForDialect(effectiveSchemaSet, dialect, strict: false);
    }

    [Test]
    public void It_should_include_the_reusable_authorization_resources_for_single_record_coverage()
    {
        var resourceNames = _modelSet
            .ConcreteResourcesInNameOrder.Select(static resource => resource.ResourceKey.Resource)
            .ToArray();

        resourceNames
            .Should()
            .Contain([
                new QualifiedResourceName(
                    "Authz",
                    RelationshipAuthorizationCrudTestSupport.MultiRootEdOrgResourceName
                ),
                new QualifiedResourceName(
                    "Authz",
                    RelationshipAuthorizationCrudTestSupport.RootAndChildEdOrgResourceName
                ),
                new QualifiedResourceName(
                    "Authz",
                    RelationshipAuthorizationCrudTestSupport.ChildOnlyEdOrgResourceName
                ),
                new QualifiedResourceName(
                    "Authz",
                    RelationshipAuthorizationCrudTestSupport.NullableRootEdOrgResourceName
                ),
            ]);
    }

    [Test]
    public void It_should_keep_the_nullable_root_edorg_subject_column_nullable()
    {
        var nullableResource = RequireResource(
            new QualifiedResourceName(
                "Authz",
                RelationshipAuthorizationCrudTestSupport.NullableRootEdOrgResourceName
            )
        );
        var rootTable = nullableResource.RelationalModel.TablesInDependencyOrder.Single(static table =>
            table.IdentityMetadata.TableKind == DbTableKind.Root
        );
        var nullableEdOrgColumn = rootTable.Columns.Single(static column =>
            column.SourceJsonPath?.Canonical == "$.nullableSchoolId"
        );

        nullableEdOrgColumn.IsNullable.Should().BeTrue();
    }

    [Test]
    public void It_should_publish_reusable_strategy_scenarios_for_later_get_by_id_and_delete_tests()
    {
        RelationshipAuthorizationCrudTestSupport
            .StrategyScenarios.Select(static scenario => scenario.Name)
            .Should()
            .Equal(
                "supported-edorg-only",
                "no-further-authorization-required-only",
                "supported-edorg-only-plus-no-further-authorization-required",
                "supported-edorg-only-plus-known-unsupported",
                "security-configuration-plus-known-unsupported",
                "inverted-edorg-only"
            );
    }

    private ConcreteResourceModel RequireResource(QualifiedResourceName resourceName)
    {
        return _modelSet.ConcreteResourcesInNameOrder.Single(resource =>
            resource.ResourceKey.Resource == resourceName
        );
    }
}

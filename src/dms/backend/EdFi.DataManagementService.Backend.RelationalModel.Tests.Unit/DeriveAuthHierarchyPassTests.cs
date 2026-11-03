// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.RelationalModel;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit;

/// <summary>
/// Test fixture for auth hierarchy pass with leaf and hierarchical EdOrg entities.
/// </summary>
[TestFixture]
public class Given_Auth_Hierarchy_With_Leaf_And_Hierarchical_Entities
{
    private DerivedRelationalModelSet _result = default!;

    [SetUp]
    public void Setup()
    {
        var projectSchema = AuthHierarchyTestSchemaBuilder.BuildHierarchyProjectSchema();
        var project = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            projectSchema,
            isExtensionProject: false
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet([project]);
        var builder = new DerivedRelationalModelSetBuilder(
            AuthHierarchyTestSchemaBuilder.BuildPassesThroughAuthHierarchy()
        );

        _result = builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());
    }

    [Test]
    public void It_should_produce_non_null_auth_hierarchy()
    {
        _result.AuthEdOrgHierarchy.Should().NotBeNull();
    }

    [Test]
    public void It_should_include_both_entities_in_name_order()
    {
        _result
            .AuthEdOrgHierarchy!.EntitiesInNameOrder.Select(e => e.EntityName)
            .Should()
            .Equal("LocalEducationAgency", "StateEducationAgency");
    }

    [Test]
    public void It_should_mark_leaf_entity_with_no_parent_fks()
    {
        var sea = _result.AuthEdOrgHierarchy!.EntitiesInNameOrder.Single(e =>
            e.EntityName == "StateEducationAgency"
        );
        sea.ParentEdOrgFks.Should().BeEmpty();
    }

    [Test]
    public void It_should_mark_hierarchical_entity_with_parent_fks()
    {
        var lea = _result.AuthEdOrgHierarchy!.EntitiesInNameOrder.Single(e =>
            e.EntityName == "LocalEducationAgency"
        );
        lea.ParentEdOrgFks.Should().HaveCount(1);
        lea.ParentEdOrgFks[0]
            .DenormalizedParentIdColumn.Value.Should()
            .Be("StateEducationAgency_EducationOrganizationId");
    }

    [Test]
    public void It_should_create_two_triggers_for_leaf_entity()
    {
        var seaTriggers = _result.TriggersInCreateOrder.Where(t =>
            t.Parameters is TriggerKindParameters.AuthHierarchyMaintenance auth
            && auth.Entity.EntityName == "StateEducationAgency"
        );
        seaTriggers.Should().HaveCount(2);
        seaTriggers
            .Select(t => ((TriggerKindParameters.AuthHierarchyMaintenance)t.Parameters).TriggerEvent)
            .Should()
            .BeEquivalentTo([AuthHierarchyTriggerEvent.Insert, AuthHierarchyTriggerEvent.Delete]);
    }

    [Test]
    public void It_should_create_three_triggers_for_hierarchical_entity()
    {
        var leaTriggers = _result.TriggersInCreateOrder.Where(t =>
            t.Parameters is TriggerKindParameters.AuthHierarchyMaintenance auth
            && auth.Entity.EntityName == "LocalEducationAgency"
        );
        leaTriggers.Should().HaveCount(3);
        leaTriggers
            .Select(t => ((TriggerKindParameters.AuthHierarchyMaintenance)t.Parameters).TriggerEvent)
            .Should()
            .BeEquivalentTo([
                AuthHierarchyTriggerEvent.Insert,
                AuthHierarchyTriggerEvent.Update,
                AuthHierarchyTriggerEvent.Delete,
            ]);
    }

    [Test]
    public void It_should_add_target_covering_index()
    {
        var authIndexes = _result.IndexesInCreateOrder.Where(i => i.Table.Schema.Value == "auth");
        authIndexes.Should().HaveCount(1);
        authIndexes
            .Select(i => i.Name.Value)
            .Should()
            .Contain("IX_EducationOrganizationIdToEducationOrganizationId_Target");
    }

    [Test]
    public void It_should_include_columns_on_target_index()
    {
        var targetIndex = _result.IndexesInCreateOrder.Single(i =>
            i.Name.Value == "IX_EducationOrganizationIdToEducationOrganizationId_Target"
        );
        targetIndex.KeyColumns.Select(c => c.Value).Should().Equal("TargetEducationOrganizationId");
        targetIndex.IncludeColumns.Should().NotBeNull();
        targetIndex.IncludeColumns!.Select(c => c.Value).Should().Equal("SourceEducationOrganizationId");
    }
}

/// <summary>
/// Test fixture for auth hierarchy pass when no abstract EducationOrganization exists.
/// </summary>
[TestFixture]
public class Given_Auth_Hierarchy_Without_Abstract_EducationOrganization
{
    private DerivedRelationalModelSet _result = default!;

    [SetUp]
    public void Setup()
    {
        var projectSchema = AuthHierarchyTestSchemaBuilder.BuildNoEdOrgProjectSchema();
        var project = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            projectSchema,
            isExtensionProject: false
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet([project]);
        var builder = new DerivedRelationalModelSetBuilder(
            AuthHierarchyTestSchemaBuilder.BuildPassesThroughAuthHierarchy()
        );

        _result = builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());
    }

    [Test]
    public void It_should_produce_null_auth_hierarchy()
    {
        _result.AuthEdOrgHierarchy.Should().BeNull();
    }

    [Test]
    public void It_should_not_add_auth_triggers()
    {
        _result
            .TriggersInCreateOrder.Where(t => t.Parameters is TriggerKindParameters.AuthHierarchyMaintenance)
            .Should()
            .BeEmpty();
    }

    [Test]
    public void It_should_not_add_auth_indexes()
    {
        _result.IndexesInCreateOrder.Where(i => i.Table.Schema.Value == "auth").Should().BeEmpty();
    }
}

/// <summary>
/// Test fixture for auth hierarchy derivation when the abstract EducationOrganization identity table and
/// union view list a <c>ResourceKeyId</c> payload scalar ahead of the identity column.
/// </summary>
[TestFixture]
public class Given_Auth_Hierarchy_With_A_Payload_Scalar_Ahead_Of_The_Identity_Column
{
    private DerivedRelationalModelSet _result = default!;

    [SetUp]
    public void Setup()
    {
        var projectSchema = AuthHierarchyTestSchemaBuilder.BuildHierarchyProjectSchema();
        var project = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            projectSchema,
            isExtensionProject: false
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet([project]);
        var passes = AuthHierarchyTestSchemaBuilder.BuildPassesThroughAuthHierarchy().ToList();
        passes.Insert(passes.Count - 1, new PayloadScalarAheadOfIdentityColumnFixturePass());
        var builder = new DerivedRelationalModelSetBuilder(passes);

        _result = builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());
    }

    [Test]
    public void It_maps_each_member_to_its_identity_source_column()
    {
        _result
            .AuthEdOrgHierarchy!.EntitiesInNameOrder.Select(e => (e.EntityName, e.IdentityColumn.Value))
            .Should()
            .Equal(
                ("LocalEducationAgency", "EducationOrganizationId"),
                ("StateEducationAgency", "EducationOrganizationId")
            );
    }
}

/// <summary>
/// Fixture pass that inserts a <c>ResourceKeyId</c> payload scalar (no JSON path) directly after
/// <c>DocumentId</c> in the abstract EducationOrganization identity table and union view, with a per-arm
/// <see cref="AbstractUnionViewProjectionExpression.Int16Literal"/>.
/// </summary>
file sealed class PayloadScalarAheadOfIdentityColumnFixturePass : IRelationalModelSetPass
{
    private static readonly DbColumnName _resourceKeyIdColumn = new("ResourceKeyId");
    private static readonly RelationalScalarType _int16Type = new(ScalarKind.Int16);

    public void Execute(RelationalModelSetBuilderContext context)
    {
        var tableIndex = context.AbstractIdentityTablesInNameOrder.FindIndex(IsEducationOrganization);
        var table = context.AbstractIdentityTablesInNameOrder[tableIndex];
        var columnIndex = IndexAfterDocumentId(table.TableModel.Columns.Select(c => c.ColumnName));
        context.AbstractIdentityTablesInNameOrder[tableIndex] = table with
        {
            TableModel = table.TableModel with
            {
                Columns = InsertAt(
                    table.TableModel.Columns,
                    columnIndex,
                    new DbColumnModel(
                        _resourceKeyIdColumn,
                        ColumnKind.Scalar,
                        _int16Type,
                        IsNullable: false,
                        SourceJsonPath: null,
                        TargetResource: null
                    )
                ),
            },
        };

        var viewIndex = context.AbstractUnionViewsInNameOrder.FindIndex(view =>
            view.AbstractResourceKey.Resource.ResourceName == "EducationOrganization"
        );
        var view = context.AbstractUnionViewsInNameOrder[viewIndex];
        var outputIndex = IndexAfterDocumentId(view.OutputColumnsInSelectOrder.Select(c => c.ColumnName));
        context.AbstractUnionViewsInNameOrder[viewIndex] = view with
        {
            OutputColumnsInSelectOrder = InsertAt(
                view.OutputColumnsInSelectOrder,
                outputIndex,
                new AbstractUnionViewOutputColumn(
                    _resourceKeyIdColumn,
                    _int16Type,
                    SourceJsonPath: null,
                    TargetResource: null
                )
            ),
            UnionArmsInOrder =
            [
                .. view.UnionArmsInOrder.Select(arm =>
                    arm with
                    {
                        ProjectionExpressionsInSelectOrder = InsertAt(
                            arm.ProjectionExpressionsInSelectOrder,
                            outputIndex,
                            new AbstractUnionViewProjectionExpression.Int16Literal(
                                arm.ConcreteMemberResourceKey.ResourceKeyId
                            )
                        ),
                    }
                ),
            ],
        };
    }

    private static bool IsEducationOrganization(AbstractIdentityTableInfo table) =>
        table.AbstractResourceKey.Resource.ResourceName == "EducationOrganization";

    private static int IndexAfterDocumentId(IEnumerable<DbColumnName> columnNames) =>
        columnNames.ToList().IndexOf(RelationalNameConventions.DocumentIdColumnName) + 1;

    private static IReadOnlyList<T> InsertAt<T>(IReadOnlyList<T> items, int index, T item) =>
        [.. items.Take(index), item, .. items.Skip(index)];
}

/// <summary>
/// Schema builder for auth hierarchy pass tests.
/// </summary>
internal static class AuthHierarchyTestSchemaBuilder
{
    /// <summary>
    /// Build the standard pass list through auth hierarchy derivation.
    /// </summary>
    internal static IRelationalModelSetPass[] BuildPassesThroughAuthHierarchy()
    {
        return
        [
            new BaseTraversalAndDescriptorBindingPass(),
            new DescriptorResourceMappingPass(),
            new ExtensionTableDerivationPass(),
            new ReferenceBindingPass(),
            new KeyUnificationPass(),
            new AbstractIdentityTableAndUnionViewDerivationPass(),
            new ValidateUnifiedAliasMetadataPass(),
            new RootIdentityConstraintPass(),
            new ReferenceConstraintPass(),
            new ArrayUniquenessConstraintPass(),
            new DescriptorForeignKeyConstraintPass(),
            new ApplyConstraintDialectHashingPass(),
            new ValidateForeignKeyStorageInvariantPass(),
            new DeriveIndexInventoryPass(),
            new DeriveTriggerInventoryPass(),
            new DeriveAuthHierarchyPass(),
        ];
    }

    /// <summary>
    /// Build project schema with abstract EducationOrganization, a leaf entity
    /// (StateEducationAgency), and a hierarchical entity (LocalEducationAgency
    /// referencing StateEducationAgency).
    /// </summary>
    internal static JsonObject BuildHierarchyProjectSchema()
    {
        return new JsonObject
        {
            ["projectName"] = "Ed-Fi",
            ["projectEndpointName"] = "ed-fi",
            ["projectVersion"] = "5.0.0",
            ["abstractResources"] = new JsonObject
            {
                ["EducationOrganization"] = new JsonObject
                {
                    ["identityJsonPaths"] = new JsonArray { "$.educationOrganizationId" },
                },
            },
            ["resourceSchemas"] = new JsonObject
            {
                ["stateEducationAgencies"] = BuildStateEducationAgencySchema(),
                ["localEducationAgencies"] = BuildLocalEducationAgencySchema(),
            },
        };
    }

    /// <summary>
    /// Build project schema with no abstract EducationOrganization.
    /// </summary>
    internal static JsonObject BuildNoEdOrgProjectSchema()
    {
        return new JsonObject
        {
            ["projectName"] = "Ed-Fi",
            ["projectEndpointName"] = "ed-fi",
            ["projectVersion"] = "5.0.0",
            ["resourceSchemas"] = new JsonObject { ["schools"] = BuildSimpleSchoolSchema() },
        };
    }

    private static JsonObject BuildStateEducationAgencySchema()
    {
        return new JsonObject
        {
            ["resourceName"] = "StateEducationAgency",
            ["isDescriptor"] = false,
            ["isResourceExtension"] = false,
            ["isSubclass"] = true,
            ["superclassProjectName"] = "Ed-Fi",
            ["superclassResourceName"] = "EducationOrganization",
            ["allowIdentityUpdates"] = false,
            ["arrayUniquenessConstraints"] = new JsonArray(),
            ["identityJsonPaths"] = new JsonArray { "$.educationOrganizationId" },
            ["documentPathsMapping"] = new JsonObject
            {
                ["EducationOrganizationId"] = new JsonObject
                {
                    ["isReference"] = false,
                    ["path"] = "$.educationOrganizationId",
                },
            },
            ["jsonSchemaForInsert"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["educationOrganizationId"] = new JsonObject { ["type"] = "integer" },
                },
                ["required"] = new JsonArray("educationOrganizationId"),
            },
        };
    }

    private static JsonObject BuildLocalEducationAgencySchema()
    {
        return new JsonObject
        {
            ["resourceName"] = "LocalEducationAgency",
            ["isDescriptor"] = false,
            ["isResourceExtension"] = false,
            ["isSubclass"] = true,
            ["superclassProjectName"] = "Ed-Fi",
            ["superclassResourceName"] = "EducationOrganization",
            ["allowIdentityUpdates"] = false,
            ["arrayUniquenessConstraints"] = new JsonArray(),
            ["identityJsonPaths"] = new JsonArray { "$.educationOrganizationId" },
            ["documentPathsMapping"] = new JsonObject
            {
                ["EducationOrganizationId"] = new JsonObject
                {
                    ["isReference"] = false,
                    ["path"] = "$.educationOrganizationId",
                },
                ["StateEducationAgency"] = new JsonObject
                {
                    ["isReference"] = true,
                    ["isDescriptor"] = false,
                    ["isRequired"] = false,
                    ["projectName"] = "Ed-Fi",
                    ["resourceName"] = "StateEducationAgency",
                    ["referenceJsonPaths"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["identityJsonPath"] = "$.educationOrganizationId",
                            ["referenceJsonPath"] = "$.stateEducationAgencyReference.educationOrganizationId",
                        },
                    },
                },
            },
            ["jsonSchemaForInsert"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["educationOrganizationId"] = new JsonObject { ["type"] = "integer" },
                    ["stateEducationAgencyReference"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["educationOrganizationId"] = new JsonObject { ["type"] = "integer" },
                        },
                    },
                },
                ["required"] = new JsonArray("educationOrganizationId"),
            },
        };
    }

    private static JsonObject BuildSimpleSchoolSchema()
    {
        return new JsonObject
        {
            ["resourceName"] = "School",
            ["isDescriptor"] = false,
            ["isResourceExtension"] = false,
            ["isSubclass"] = false,
            ["allowIdentityUpdates"] = true,
            ["arrayUniquenessConstraints"] = new JsonArray(),
            ["identityJsonPaths"] = new JsonArray { "$.schoolId" },
            ["documentPathsMapping"] = new JsonObject
            {
                ["SchoolId"] = new JsonObject { ["isReference"] = false, ["path"] = "$.schoolId" },
            },
            ["jsonSchemaForInsert"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject { ["schoolId"] = new JsonObject { ["type"] = "integer" } },
                ["required"] = new JsonArray("schoolId"),
            },
        };
    }
}

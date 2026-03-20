// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.RelationalModel.Build;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit;

/// <summary>
/// Test fixture for semantic-identity precedence across base and extension project ordering.
/// </summary>
[TestFixture]
public class Given_A_Reference_Fallback_And_An_Aligned_Extension_Array_Uniqueness
{
    private DbTableModel _addressTableWithBaseProjectFirst = default!;
    private DbTableModel _addressTableWithExtensionProjectFirst = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        _addressTableWithBaseProjectFirst = BuildAddressTable("Sample", "sample");
        _addressTableWithExtensionProjectFirst = BuildAddressTable("Alpha", "alpha");
    }

    /// <summary>
    /// It should prefer array uniqueness over reference fallback regardless of project ordering.
    /// </summary>
    [Test]
    public void It_should_prefer_array_uniqueness_over_reference_fallback_regardless_of_project_order()
    {
        AssertUsesArrayUniqueness(_addressTableWithBaseProjectFirst);
        AssertUsesArrayUniqueness(_addressTableWithExtensionProjectFirst);
    }

    /// <summary>
    /// Builds the derived address table for one extension ordering scenario.
    /// </summary>
    private static DbTableModel BuildAddressTable(
        string extensionProjectName,
        string extensionProjectEndpointName
    )
    {
        var coreProjectSchema =
            ConstraintDerivationTestSchemaBuilder.BuildReferenceBackedCollectionProjectSchema();
        var extensionProjectSchema = BuildAlignedArrayUniquenessExtensionProjectSchema(
            extensionProjectName,
            extensionProjectEndpointName
        );
        var coreProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            coreProjectSchema,
            isExtensionProject: false
        );
        var extensionProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            extensionProjectSchema,
            isExtensionProject: true
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet([
            coreProject,
            extensionProject,
        ]);
        var builder = new DerivedRelationalModelSetBuilder(
            new IRelationalModelSetPass[]
            {
                new BaseTraversalAndDescriptorBindingPass(),
                new ReferenceBindingPass(),
                new RootIdentityConstraintPass(),
                new ReferenceConstraintPass(),
                new SemanticIdentityCompilationPass(),
                new ValidateCollectionSemanticIdentityPass(),
                new ArrayUniquenessConstraintPass(),
                new StableCollectionConstraintPass(),
            }
        );

        var result = builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());
        var busRouteModel = result
            .ConcreteResourcesInNameOrder.Single(model =>
                model.ResourceKey.Resource == new QualifiedResourceName("Ed-Fi", "BusRoute")
            )
            .RelationalModel;

        return busRouteModel.TablesInDependencyOrder.Single(table => table.Table.Name == "BusRouteAddress");
    }

    /// <summary>
    /// Asserts the compiled semantic identity and downstream uniqueness shape use the aligned AUC member set.
    /// </summary>
    private static void AssertUsesArrayUniqueness(DbTableModel addressTable)
    {
        var referenceFallbackUniqueColumns = new[] { "BusRoute_DocumentId", "AddressSchool_DocumentId" };

        addressTable
            .IdentityMetadata.SemanticIdentityBindings.Select(binding => binding.RelativePath.Canonical)
            .Should()
            .Equal("$.streetNumberName");

        addressTable
            .IdentityMetadata.SemanticIdentityBindings.Select(binding => binding.ColumnName.Value)
            .Should()
            .Equal("StreetNumberName");

        var semanticIdentityUniqueConstraint = ConstraintDerivationAssertionHelpers.FindUniqueConstraint(
            addressTable,
            "BusRoute_DocumentId",
            "StreetNumberName"
        );

        semanticIdentityUniqueConstraint
            .Columns.Select(column => column.Value)
            .Should()
            .Equal("BusRoute_DocumentId", "StreetNumberName");

        addressTable
            .Constraints.OfType<TableConstraint.Unique>()
            .Select(constraint => constraint.Columns.Select(column => column.Value).ToArray())
            .Should()
            .NotContain(columns => columns.SequenceEqual(referenceFallbackUniqueColumns));
    }

    /// <summary>
    /// Builds a resource extension schema whose AUC aligns back to the base collection scope.
    /// </summary>
    private static JsonObject BuildAlignedArrayUniquenessExtensionProjectSchema(
        string projectName,
        string projectEndpointName
    )
    {
        return new JsonObject
        {
            ["projectName"] = projectName,
            ["projectEndpointName"] = projectEndpointName,
            ["projectVersion"] = "1.0.0",
            ["resourceSchemas"] = new JsonObject
            {
                ["busRoutes"] = new JsonObject
                {
                    ["resourceName"] = "BusRoute",
                    ["isDescriptor"] = false,
                    ["isResourceExtension"] = true,
                    ["isSubclass"] = false,
                    ["allowIdentityUpdates"] = false,
                    ["arrayUniquenessConstraints"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["basePath"] = $"$._ext.{projectEndpointName}.addresses[*]",
                            ["paths"] = new JsonArray { "$.streetNumberName" },
                        },
                    },
                    ["identityJsonPaths"] = new JsonArray(),
                    ["documentPathsMapping"] = new JsonObject(),
                    ["jsonSchemaForInsert"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["_ext"] = new JsonObject
                            {
                                ["type"] = "object",
                                ["properties"] = new JsonObject
                                {
                                    [projectEndpointName] = new JsonObject
                                    {
                                        ["type"] = "object",
                                        ["properties"] = new JsonObject
                                        {
                                            ["addresses"] = new JsonObject
                                            {
                                                ["type"] = "array",
                                                ["items"] = new JsonObject
                                                {
                                                    ["type"] = "object",
                                                    ["properties"] = new JsonObject
                                                    {
                                                        ["streetNumberName"] = new JsonObject
                                                        {
                                                            ["type"] = "string",
                                                            ["maxLength"] = 20,
                                                        },
                                                    },
                                                },
                                            },
                                        },
                                    },
                                },
                            },
                        },
                    },
                },
            },
        };
    }
}

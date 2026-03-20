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

        addressTable
            .IdentityMetadata.SemanticIdentitySource.Should()
            .Be(CollectionSemanticIdentitySource.ArrayUniquenessConstraint);

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

/// <summary>
/// Test fixture for upgrading matching fallback provenance once an equivalent AUC is observed.
/// </summary>
[TestFixture]
public class Given_A_Seeded_Reference_Fallback_And_A_Matching_Array_Uniqueness
{
    private DbTableModel _addressTable = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var coreProjectSchema =
            ConstraintDerivationTestSchemaBuilder.BuildReferenceBackedCollectionProjectSchema();

        SemanticIdentityPrecedenceFixture.SetBusRouteArrayUniquenessConstraints(
            coreProjectSchema,
            SemanticIdentityPrecedenceFixture.BuildMatchingReferenceFallbackArrayUniquenessConstraints()
        );

        _addressTable = SemanticIdentityPrecedenceFixture.BuildBusRouteAddressTable(
            coreProjectSchema,
            new BaseTraversalAndDescriptorBindingPass(),
            new ReferenceBindingPass(),
            new RootIdentityConstraintPass(),
            new ReferenceConstraintPass(),
            new SeedSemanticIdentityPass(
                "BusRoute",
                "BusRouteAddress",
                SemanticIdentityPrecedenceFixture.BuildAddressSchoolFallbackBindings(),
                CollectionSemanticIdentitySource.ReferenceFallback
            ),
            new ArrayUniquenessConstraintPass()
        );
    }

    /// <summary>
    /// It should upgrade the authoritative source to AUC even when the bindings are identical.
    /// </summary>
    [Test]
    public void It_should_upgrade_matching_reference_fallback_provenance_to_array_uniqueness()
    {
        _addressTable
            .IdentityMetadata.SemanticIdentityBindings.Select(binding => binding.RelativePath.Canonical)
            .Should()
            .Equal("$.schoolReference.schoolId", "$.schoolReference.educationOrganizationId");

        _addressTable
            .IdentityMetadata.SemanticIdentityBindings.Select(binding => binding.ColumnName.Value)
            .Should()
            .Equal("AddressSchool_DocumentId", "AddressSchool_DocumentId");

        _addressTable
            .IdentityMetadata.SemanticIdentitySource.Should()
            .Be(CollectionSemanticIdentitySource.ArrayUniquenessConstraint);
    }
}

/// <summary>
/// Test fixture for the false-fallback ambiguity regression.
/// </summary>
[TestFixture]
public class Given_An_Earlier_Auc_That_Compiles_To_The_Reference_Fallback_Binding_Set
{
    private Action _build = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var coreProjectSchema =
            ConstraintDerivationTestSchemaBuilder.BuildReferenceBackedCollectionProjectSchema();

        SemanticIdentityPrecedenceFixture.SetBusRouteArrayUniquenessConstraints(
            coreProjectSchema,
            SemanticIdentityPrecedenceFixture.BuildAmbiguousFallbackLikeArrayUniquenessConstraints()
        );

        _build = () =>
        {
            SemanticIdentityPrecedenceFixture.BuildBusRouteAddressTable(
                coreProjectSchema,
                new BaseTraversalAndDescriptorBindingPass(),
                new ReferenceBindingPass(),
                new RootIdentityConstraintPass(),
                new ReferenceConstraintPass(),
                new SeedSemanticIdentityPass(
                    "BusRoute",
                    "BusRouteAddress",
                    SemanticIdentityPrecedenceFixture.BuildAddressSchoolFallbackBindings(),
                    CollectionSemanticIdentitySource.ArrayUniquenessConstraint
                ),
                new ArrayUniquenessConstraintPass()
            );
        };
    }

    /// <summary>
    /// It should fail as ambiguous instead of treating the existing AUC bindings as replaceable fallback.
    /// </summary>
    [Test]
    public void It_should_fail_with_ambiguous_array_uniqueness_instead_of_fallback_replacement()
    {
        var exception = _build.Should().Throw<InvalidOperationException>().Which;

        exception.Message.Should().Contain("Persisted multi-item scope");
        exception.Message.Should().Contain("$.addresses[*]");
        exception.Message.Should().Contain("Ed-Fi:BusRoute");
        exception.Message.Should().Contain("$.schoolReference.schoolId");
        exception.Message.Should().Contain("$.schoolReference.educationOrganizationId");
        exception.Message.Should().Contain("$.streetNumberName");
        exception.Message.Should().Contain("exactly one non-empty ordered binding set");
    }
}

file static class SemanticIdentityPrecedenceFixture
{
    internal static DbTableModel BuildBusRouteAddressTable(
        JsonObject coreProjectSchema,
        params IRelationalModelSetPass[] passes
    )
    {
        var coreProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            coreProjectSchema,
            isExtensionProject: false
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet([coreProject]);
        var builder = new DerivedRelationalModelSetBuilder(passes);
        var result = builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());
        var busRouteModel = result
            .ConcreteResourcesInNameOrder.Single(model =>
                model.ResourceKey.Resource == new QualifiedResourceName("Ed-Fi", "BusRoute")
            )
            .RelationalModel;

        return busRouteModel.TablesInDependencyOrder.Single(table => table.Table.Name == "BusRouteAddress");
    }

    internal static CollectionSemanticIdentityBinding[] BuildAddressSchoolFallbackBindings()
    {
        return
        [
            new CollectionSemanticIdentityBinding(
                JsonPathExpressionCompiler.Compile("$.schoolReference.schoolId"),
                new DbColumnName("AddressSchool_DocumentId")
            ),
            new CollectionSemanticIdentityBinding(
                JsonPathExpressionCompiler.Compile("$.schoolReference.educationOrganizationId"),
                new DbColumnName("AddressSchool_DocumentId")
            ),
        ];
    }

    internal static JsonArray BuildMatchingReferenceFallbackArrayUniquenessConstraints()
    {
        return new JsonArray
        {
            new JsonObject
            {
                ["paths"] = new JsonArray
                {
                    "$.addresses[*].schoolReference.schoolId",
                    "$.addresses[*].schoolReference.educationOrganizationId",
                },
            },
        };
    }

    internal static JsonArray BuildAmbiguousFallbackLikeArrayUniquenessConstraints()
    {
        return new JsonArray
        {
            new JsonObject
            {
                ["paths"] = new JsonArray
                {
                    "$.addresses[*].schoolReference.schoolId",
                    "$.addresses[*].schoolReference.educationOrganizationId",
                },
            },
            new JsonObject { ["paths"] = new JsonArray { "$.addresses[*].streetNumberName" } },
        };
    }

    internal static void SetBusRouteArrayUniquenessConstraints(
        JsonObject projectSchema,
        JsonArray arrayUniquenessConstraints
    )
    {
        var resourceSchemas =
            projectSchema["resourceSchemas"] as JsonObject
            ?? throw new InvalidOperationException("Expected 'resourceSchemas' to be a JSON object.");
        var busRoute =
            resourceSchemas["busRoutes"] as JsonObject
            ?? throw new InvalidOperationException("Expected 'busRoutes' to be a JSON object.");

        busRoute["arrayUniquenessConstraints"] = arrayUniquenessConstraints;
    }
}

file sealed class SeedSemanticIdentityPass(
    string resourceName,
    string tableName,
    IReadOnlyList<CollectionSemanticIdentityBinding> semanticIdentityBindings,
    CollectionSemanticIdentitySource semanticIdentitySource
) : IRelationalModelSetPass
{
    /// <summary>
    /// Execute pass.
    /// </summary>
    public void Execute(RelationalModelSetBuilderContext context)
    {
        for (var index = 0; index < context.ConcreteResourcesInNameOrder.Count; index++)
        {
            var concreteResource = context.ConcreteResourcesInNameOrder[index];

            if (
                !string.Equals(
                    concreteResource.ResourceKey.Resource.ResourceName,
                    resourceName,
                    StringComparison.Ordinal
                )
            )
            {
                continue;
            }

            var updatedTables = concreteResource
                .RelationalModel.TablesInDependencyOrder.Select(table =>
                    string.Equals(table.Table.Name, tableName, StringComparison.Ordinal)
                        ? table with
                        {
                            IdentityMetadata = table.IdentityMetadata with
                            {
                                SemanticIdentityBindings = semanticIdentityBindings.ToArray(),
                                SemanticIdentitySource = semanticIdentitySource,
                            },
                        }
                        : table
                )
                .ToArray();
            var updatedRoot = updatedTables.Single(table =>
                table.JsonScope.Equals(concreteResource.RelationalModel.Root.JsonScope)
            );
            var updatedModel = concreteResource.RelationalModel with
            {
                Root = updatedRoot,
                TablesInDependencyOrder = updatedTables,
            };

            context.ConcreteResourcesInNameOrder[index] = concreteResource with
            {
                RelationalModel = updatedModel,
            };
        }
    }
}

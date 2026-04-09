// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.RelationalModel;
using EdFi.DataManagementService.Backend.RelationalModel.Build;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit;

/// <summary>
/// Test fixture for non-reference-backed collection scopes without array uniqueness metadata.
/// </summary>
[TestFixture]
public class Given_A_Non_Reference_Backed_Collection_Without_Array_Uniqueness
{
    private Action _build = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var coreProjectSchema =
            ConstraintDerivationTestSchemaBuilder.BuildNestedArrayUniquenessProjectSchema();
        ClearArrayUniquenessConstraints(coreProjectSchema, "busRoutes");

        var schemaSet = CollectionSemanticIdentityValidationFixture.CreateSchemaSet(coreProjectSchema);
        var builder = CollectionSemanticIdentityValidationFixture.CreateStrictBuilder();

        _build = () => builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());
    }

    /// <summary>
    /// It should fail with the missing array uniqueness diagnostic.
    /// </summary>
    [Test]
    public void It_should_fail_with_the_missing_array_uniqueness_diagnostic()
    {
        var exception = _build.Should().Throw<InvalidOperationException>().Which;

        exception.Message.Should().Contain("Persisted multi-item scope");
        exception.Message.Should().Contain("$.addresses[*]");
        exception.Message.Should().Contain("Ed-Fi:BusRoute");
        exception.Message.Should().Contain("not reference-backed");
        exception.Message.Should().Contain("arrayUniquenessConstraints");
    }

    /// <summary>
    /// Clears array uniqueness constraints for one resource endpoint.
    /// </summary>
    private static void ClearArrayUniquenessConstraints(JsonObject projectSchema, string resourceEndpointName)
    {
        var resourceSchemas = RequireObject(projectSchema["resourceSchemas"], "resourceSchemas");
        var resourceSchema = RequireObject(resourceSchemas[resourceEndpointName], resourceEndpointName);
        resourceSchema["arrayUniquenessConstraints"] = new JsonArray();
    }

    /// <summary>
    /// Requires a JSON object node.
    /// </summary>
    private static JsonObject RequireObject(JsonNode? node, string path)
    {
        return node as JsonObject
            ?? throw new InvalidOperationException($"Expected '{path}' to be a JSON object.");
    }
}

/// <summary>
/// Test fixture for strict validation when the canonical ordinal column is absent.
/// </summary>
[TestFixture]
public class Given_A_Collection_Without_The_Canonical_Ordinal_Column_During_Strict_Validation
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
        var schemaSet = CollectionSemanticIdentityValidationFixture.CreateSchemaSet(coreProjectSchema);
        var builder = new DerivedRelationalModelSetBuilder([
            new BaseTraversalAndDescriptorBindingPass(),
            new ReferenceBindingPass(),
            new RewriteOrdinalColumnPass("BusRoute", "BusRouteAddress", new DbColumnName("ItemOrdinal")),
            new SemanticIdentityCompilationPass(),
            new ValidateCollectionSemanticIdentityPass(),
        ]);

        var result = builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());
        var busRouteModel = result
            .ConcreteResourcesInNameOrder.Single(model =>
                model.ResourceKey.Resource.ResourceName == "BusRoute"
            )
            .RelationalModel;

        _addressTable = busRouteModel.TablesInDependencyOrder.Single(table =>
            table.Table.Name == "BusRouteAddress"
        );
    }

    /// <summary>
    /// It should skip strict validation for tables without the canonical ordinal column.
    /// </summary>
    [Test]
    public void It_should_skip_strict_validation_for_tables_without_the_canonical_ordinal_column()
    {
        _addressTable.IdentityMetadata.SemanticIdentityBindings.Should().BeEmpty();
    }
}

/// <summary>
/// Test fixture for ambiguous array-uniqueness semantic identity.
/// </summary>
[TestFixture]
public class Given_A_Collection_With_Multiple_Applicable_Array_Uniqueness_Semantic_Identities
{
    private Action _build = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var coreProjectSchema = ConstraintDerivationTestSchemaBuilder.BuildArrayUniquenessProjectSchema();

        CollectionSemanticIdentityValidationFixture.SetArrayUniquenessConstraints(
            coreProjectSchema,
            "busRoutes",
            [
                new JsonObject { ["paths"] = new JsonArray { "$.addresses[*].streetNumberName" } },
                new JsonObject
                {
                    ["paths"] = new JsonArray
                    {
                        "$.addresses[*].schoolReference.schoolId",
                        "$.addresses[*].schoolReference.educationOrganizationId",
                    },
                },
            ]
        );

        var schemaSet = CollectionSemanticIdentityValidationFixture.CreateSchemaSet(coreProjectSchema);
        var builder = CollectionSemanticIdentityValidationFixture.CreateStrictBuilder();

        _build = () => builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());
    }

    /// <summary>
    /// It should fail with the ambiguous array-uniqueness diagnostic.
    /// </summary>
    [Test]
    public void It_should_fail_with_the_ambiguous_array_uniqueness_diagnostic()
    {
        var exception = _build.Should().Throw<InvalidOperationException>().Which;

        exception.Message.Should().Contain("Persisted multi-item scope");
        exception.Message.Should().Contain("$.addresses[*]");
        exception.Message.Should().Contain("Ed-Fi:BusRoute");
        exception.Message.Should().Contain("arrayUniquenessConstraints");
        exception.Message.Should().Contain("$.streetNumberName");
        exception.Message.Should().Contain("$.schoolReference.schoolId");
        exception.Message.Should().Contain("$.schoolReference.educationOrganizationId");
        exception.Message.Should().Contain("exactly one non-empty ordered binding set");
    }
}

/// <summary>
/// Test fixture for reference-backed collection scopes whose rewritten identity path crosses a descendant array.
/// </summary>
[TestFixture]
public class Given_A_Reference_Backed_Collection_With_A_Reference_Derived_Semantic_Identity_That_Crosses_A_Descendant_Array
{
    private Action _build = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var coreProjectSchema =
            ConstraintDerivationTestSchemaBuilder.BuildReferenceConstraintProjectSchemaWithChildReference();
        var schemaSet = CollectionSemanticIdentityValidationFixture.CreateSchemaSet(coreProjectSchema);
        var builder = CollectionSemanticIdentityValidationFixture.CreateStrictBuilder(
            new RewriteReferenceIdentityBindingsPass(
                "BusRoute",
                "$.addresses[*].schoolReference",
                [
                    JsonPathExpressionCompiler.Compile("$.addresses[*].periods[*].schoolReference.schoolId"),
                    JsonPathExpressionCompiler.Compile(
                        "$.addresses[*].periods[*].schoolReference.educationOrganizationId"
                    ),
                ]
            )
        );

        _build = () => builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());
    }

    /// <summary>
    /// It should fail fast with the shared descendant-array normalization diagnostic.
    /// </summary>
    [Test]
    public void It_should_fail_with_the_descendant_array_scope_relative_diagnostic()
    {
        var exception = _build.Should().Throw<InvalidOperationException>().Which;

        exception
            .Message.Should()
            .Be(
                "Cannot derive scope-relative semantic identity path for "
                    + "'$.addresses[*].periods[*].schoolReference.schoolId' under scope "
                    + "'$.addresses[*]': stripped path contains '[*]'."
            );
    }
}

/// <summary>
/// Test fixture for reference-backed collection scopes whose rewritten identity path falls outside the table scope.
/// </summary>
[TestFixture]
public class Given_A_Reference_Backed_Collection_With_A_Reference_Derived_Semantic_Identity_Outside_The_Table_Scope
{
    private Action _build = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var coreProjectSchema =
            ConstraintDerivationTestSchemaBuilder.BuildReferenceConstraintProjectSchemaWithChildReference();
        var schemaSet = CollectionSemanticIdentityValidationFixture.CreateSchemaSet(coreProjectSchema);
        var builder = CollectionSemanticIdentityValidationFixture.CreateStrictBuilder(
            new RewriteReferenceIdentityBindingsPass(
                "BusRoute",
                "$.addresses[*].schoolReference",
                [
                    JsonPathExpressionCompiler.Compile("$.contacts[*].schoolReference.schoolId"),
                    JsonPathExpressionCompiler.Compile(
                        "$.contacts[*].schoolReference.educationOrganizationId"
                    ),
                ]
            )
        );

        _build = () => builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());
    }

    /// <summary>
    /// It should fail fast with the shared non-prefix normalization diagnostic.
    /// </summary>
    [Test]
    public void It_should_fail_with_the_non_prefix_scope_relative_diagnostic()
    {
        var exception = _build.Should().Throw<InvalidOperationException>().Which;

        exception
            .Message.Should()
            .Be(
                "Cannot derive scope-relative semantic identity path for "
                    + "'$.contacts[*].schoolReference.schoolId': scope '$.addresses[*]' is not a prefix."
            );
    }
}

/// <summary>
/// Test fixture for ambiguous reference-derived semantic identity.
/// </summary>
[TestFixture]
public class Given_A_Reference_Backed_Collection_With_Ambiguous_Reference_Derived_Semantic_Identity
{
    private Action _build = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var coreProjectSchema =
            ConstraintDerivationTestSchemaBuilder.BuildReferenceConstraintProjectSchemaWithChildReference();
        var schemaSet = CollectionSemanticIdentityValidationFixture.CreateSchemaSet(coreProjectSchema);
        var builder = CollectionSemanticIdentityValidationFixture.CreateStrictBuilder(
            new DuplicateDocumentReferenceBindingPass("BusRoute", "$.addresses[*].schoolReference")
        );

        _build = () => builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());
    }

    /// <summary>
    /// It should fail with the ambiguous reference-backed diagnostic.
    /// </summary>
    [Test]
    public void It_should_fail_with_the_ambiguous_reference_backed_diagnostic()
    {
        var exception = _build.Should().Throw<InvalidOperationException>().Which;

        exception.Message.Should().Contain("Persisted multi-item scope");
        exception.Message.Should().Contain("$.addresses[*]");
        exception.Message.Should().Contain("Ed-Fi:BusRoute");
        exception.Message.Should().Contain("reference-backed");
        exception.Message.Should().Contain("exactly one qualifying");
        exception.Message.Should().Contain("found 2");
        exception.Message.Should().Contain("$.addresses[*].schoolReference");
        exception.Message.Should().Contain("exactly one non-empty ordered binding set");
    }
}

/// <summary>
/// Test fixture for unsupported persisted multi-item scopes.
/// </summary>
[TestFixture]
public class Given_An_Unsupported_Persisted_Multi_Item_Scope_Without_Semantic_Identity
{
    private Action _build = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var coreProjectSchema =
            ConstraintDerivationTestSchemaBuilder.BuildReferenceConstraintProjectSchemaWithChildReference();
        var schemaSet = CollectionSemanticIdentityValidationFixture.CreateSchemaSet(coreProjectSchema);
        var builder = CollectionSemanticIdentityValidationFixture.CreateStrictBuilder(
            new RewriteTableKindPass("BusRoute", "BusRouteAddress", DbTableKind.CollectionExtensionScope)
        );

        _build = () => builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());
    }

    /// <summary>
    /// It should fail with the unsupported-scope diagnostic.
    /// </summary>
    [Test]
    public void It_should_fail_with_the_unsupported_scope_diagnostic()
    {
        var exception = _build.Should().Throw<InvalidOperationException>().Which;

        exception.Message.Should().Contain("Unsupported persisted multi-item scope");
        exception.Message.Should().Contain("$.addresses[*]");
        exception.Message.Should().Contain("Ed-Fi:BusRoute");
        exception.Message.Should().Contain("CollectionExtensionScope");
        exception.Message.Should().Contain("Collection");
        exception.Message.Should().Contain("ExtensionCollection");
    }
}

/// <summary>
/// Fixture helpers for collection semantic identity validation tests.
/// </summary>
internal static class CollectionSemanticIdentityValidationFixture
{
    /// <summary>
    /// Creates an effective schema set for one core project schema.
    /// </summary>
    internal static EffectiveSchemaSet CreateSchemaSet(JsonObject coreProjectSchema)
    {
        var coreProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            coreProjectSchema,
            isExtensionProject: false
        );

        return EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet([coreProject]);
    }

    /// <summary>
    /// Creates a strict builder using the canonical pass order, with test-only passes inserted
    /// immediately before semantic-identity compilation.
    /// </summary>
    internal static DerivedRelationalModelSetBuilder CreateStrictBuilder(
        params IRelationalModelSetPass[] additionalPassesBeforeSemanticIdentityCompilation
    )
    {
        var strictPasses = RelationalModelSetPasses.CreateStrict().ToList();
        var semanticIdentityCompilationIndex = strictPasses.FindIndex(pass =>
            pass is SemanticIdentityCompilationPass
        );

        if (semanticIdentityCompilationIndex < 0)
        {
            throw new InvalidOperationException(
                "Strict relational-model pass order is missing SemanticIdentityCompilationPass."
            );
        }

        strictPasses.InsertRange(
            semanticIdentityCompilationIndex,
            additionalPassesBeforeSemanticIdentityCompilation
        );
        return new DerivedRelationalModelSetBuilder(strictPasses);
    }

    /// <summary>
    /// Replaces array uniqueness constraints for one resource endpoint.
    /// </summary>
    internal static void SetArrayUniquenessConstraints(
        JsonObject projectSchema,
        string resourceEndpointName,
        JsonArray arrayUniquenessConstraints
    )
    {
        var resourceSchemas = RequireObject(projectSchema["resourceSchemas"], "resourceSchemas");
        var resourceSchema = RequireObject(resourceSchemas[resourceEndpointName], resourceEndpointName);
        resourceSchema["arrayUniquenessConstraints"] = arrayUniquenessConstraints;
    }

    /// <summary>
    /// Requires a JSON object node.
    /// </summary>
    private static JsonObject RequireObject(JsonNode? node, string path)
    {
        return node as JsonObject
            ?? throw new InvalidOperationException($"Expected '{path}' to be a JSON object.");
    }
}

/// <summary>
/// Test-only set pass that appends an equivalent document-reference binding to mimic duplicate metadata.
/// </summary>
file sealed class DuplicateDocumentReferenceBindingPass(string resourceName, string referenceObjectPath)
    : IRelationalModelSetPass
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

            var bindingToDuplicate =
                concreteResource.RelationalModel.DocumentReferenceBindings.SingleOrDefault(binding =>
                    string.Equals(
                        binding.ReferenceObjectPath.Canonical,
                        referenceObjectPath,
                        StringComparison.Ordinal
                    )
                );

            if (bindingToDuplicate is null)
            {
                continue;
            }

            var updatedBindings = concreteResource
                .RelationalModel.DocumentReferenceBindings.Concat([bindingToDuplicate])
                .ToArray();
            var updatedModel = concreteResource.RelationalModel with
            {
                DocumentReferenceBindings = updatedBindings,
            };

            context.ConcreteResourcesInNameOrder[index] = concreteResource with
            {
                RelationalModel = updatedModel,
            };
        }
    }
}

/// <summary>
/// Test-only set pass that rewrites one table kind to an unsupported value.
/// </summary>
file sealed class RewriteTableKindPass(string resourceName, string tableName, DbTableKind tableKind)
    : IRelationalModelSetPass
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
                            IdentityMetadata = table.IdentityMetadata with { TableKind = tableKind },
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

/// <summary>
/// Test-only set pass that rewrites one table's canonical ordinal column name.
/// </summary>
file sealed class RewriteOrdinalColumnPass(
    string resourceName,
    string tableName,
    DbColumnName rewrittenColumnName
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
                            Columns = table
                                .Columns.Select(column =>
                                    column.Kind is ColumnKind.Ordinal
                                        ? column with
                                        {
                                            ColumnName = rewrittenColumnName,
                                        }
                                        : column
                                )
                                .ToArray(),
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

/// <summary>
/// Test-only set pass that rewrites one reference binding's identity paths to a non-qualifying scope.
/// </summary>
file sealed class RewriteReferenceIdentityBindingsPass(
    string resourceName,
    string referenceObjectPath,
    IReadOnlyList<JsonPathExpression> rewrittenReferenceJsonPaths
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

            var updatedBindings = concreteResource
                .RelationalModel.DocumentReferenceBindings.Select(binding =>
                {
                    if (
                        !string.Equals(
                            binding.ReferenceObjectPath.Canonical,
                            referenceObjectPath,
                            StringComparison.Ordinal
                        )
                    )
                    {
                        return binding;
                    }

                    var identityBindings = rewrittenReferenceJsonPaths
                        .Select(
                            (path, rewrittenIndex) =>
                                new ReferenceIdentityBinding(
                                    path,
                                    path,
                                    binding.IdentityBindings[rewrittenIndex].Column
                                )
                        )
                        .ToArray();

                    return binding with
                    {
                        IdentityBindings = identityBindings,
                    };
                })
                .ToArray();

            var updatedModel = concreteResource.RelationalModel with
            {
                DocumentReferenceBindings = updatedBindings,
            };

            context.ConcreteResourcesInNameOrder[index] = concreteResource with
            {
                RelationalModel = updatedModel,
            };
        }
    }
}

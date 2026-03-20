// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.RelationalModel;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit;

/// <summary>
/// Test fixture for reference-derived semantic identity compilation.
/// </summary>
[TestFixture]
public class Given_A_Reference_Backed_Collection_Without_Array_Uniqueness
{
    private DbTableModel _addressTable = default!;
    private IReadOnlyList<CollectionSemanticIdentityBinding> _observedBindings = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var coreProjectSchema =
            ConstraintDerivationTestSchemaBuilder.BuildReferenceConstraintProjectSchemaWithChildReference();
        var coreProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            coreProjectSchema,
            isExtensionProject: false
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet([coreProject]);
        List<CollectionSemanticIdentityBinding> observedBindings = [];
        var builder = new DerivedRelationalModelSetBuilder([
            new BaseTraversalAndDescriptorBindingPass(),
            new ReferenceBindingPass(),
            new SemanticIdentityCompilationPass(),
            new ObserveCollectionSemanticIdentityPass(observedBindings),
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
        _observedBindings = observedBindings.ToArray();
    }

    /// <summary>
    /// It should compile semantic identity from the single scope-local reference in declared order.
    /// </summary>
    [Test]
    public void It_should_compile_semantic_identity_from_the_scope_local_reference()
    {
        _addressTable
            .IdentityMetadata.SemanticIdentityBindings.Select(binding => binding.RelativePath.Canonical)
            .Should()
            .Equal("$.schoolReference.schoolId", "$.schoolReference.educationOrganizationId");

        _addressTable
            .IdentityMetadata.SemanticIdentityBindings.Select(binding => binding.ColumnName.Value)
            .Should()
            .Equal("School_DocumentId", "School_DocumentId");

        _addressTable
            .IdentityMetadata.SemanticIdentitySource.Should()
            .Be(CollectionSemanticIdentitySource.ReferenceFallback);
    }

    /// <summary>
    /// It should expose compiled semantic identity to downstream passes.
    /// </summary>
    [Test]
    public void It_should_expose_compiled_semantic_identity_to_downstream_passes()
    {
        _observedBindings
            .Select(binding => binding.RelativePath.Canonical)
            .Should()
            .Equal("$.schoolReference.schoolId", "$.schoolReference.educationOrganizationId");

        _observedBindings
            .Select(binding => binding.ColumnName.Value)
            .Should()
            .Equal("School_DocumentId", "School_DocumentId");
    }

    /// <summary>
    /// Observes compiled semantic identity after the compilation pass runs.
    /// </summary>
    private sealed class ObserveCollectionSemanticIdentityPass(
        List<CollectionSemanticIdentityBinding> observedBindings
    ) : IRelationalModelSetPass
    {
        /// <summary>
        /// Execute.
        /// </summary>
        public void Execute(RelationalModelSetBuilderContext context)
        {
            var busRouteModel = context.ConcreteResourcesInNameOrder.Single(model =>
                model.ResourceKey.Resource.ResourceName == "BusRoute"
            );
            var addressTable = busRouteModel.RelationalModel.TablesInDependencyOrder.Single(table =>
                table.Table.Name == "BusRouteAddress"
            );

            observedBindings.AddRange(addressTable.IdentityMetadata.SemanticIdentityBindings);
        }
    }
}

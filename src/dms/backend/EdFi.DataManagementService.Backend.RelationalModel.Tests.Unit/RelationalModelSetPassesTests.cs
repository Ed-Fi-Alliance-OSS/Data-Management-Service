// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.RelationalModel;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit;

/// <summary>
/// Test fixture for relational model set pass factories.
/// </summary>
[TestFixture]
public class Given_RelationalModelSet_Passes
{
    private IReadOnlyList<Type> _defaultPassTypes = default!;
    private IReadOnlyList<Type> _strictPassTypes = default!;
    private IReadOnlyList<Type> _secondDefaultPassTypes = default!;
    private IReadOnlyList<Type> _secondStrictPassTypes = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        _defaultPassTypes = RelationalModelSetPasses.CreateDefault().Select(pass => pass.GetType()).ToArray();
        _strictPassTypes = RelationalModelSetPasses.CreateStrict().Select(pass => pass.GetType()).ToArray();
        _secondDefaultPassTypes = RelationalModelSetPasses
            .CreateDefault()
            .Select(pass => pass.GetType())
            .ToArray();
        _secondStrictPassTypes = RelationalModelSetPasses
            .CreateStrict()
            .Select(pass => pass.GetType())
            .ToArray();
    }

    /// <summary>
    /// It should expose the canonical default pass order.
    /// </summary>
    [Test]
    public void It_should_expose_the_canonical_default_pass_order()
    {
        _defaultPassTypes
            .Should()
            .Equal(
                typeof(BaseTraversalAndDescriptorBindingPass),
                typeof(DescriptorResourceMappingPass),
                typeof(ExtensionTableDerivationPass),
                typeof(ReferenceBindingPass),
                typeof(KeyUnificationPass),
                typeof(AbstractIdentityTableAndUnionViewDerivationPass),
                typeof(ValidateUnifiedAliasMetadataPass),
                typeof(RootIdentityConstraintPass),
                typeof(TransitiveIdentityMutabilityPass),
                typeof(ReferenceConstraintPass),
                typeof(SemanticIdentityCompilationPass),
                typeof(ArrayUniquenessConstraintPass),
                typeof(StableCollectionConstraintPass),
                typeof(DescriptorForeignKeyConstraintPass),
                typeof(ApplyConstraintDialectHashingPass),
                typeof(ValidateForeignKeyStorageInvariantPass),
                typeof(DeriveIndexInventoryPass),
                typeof(DeriveTriggerInventoryPass),
                typeof(DeriveAuthHierarchyPass),
                typeof(DeriveAuthorizationIndexInventoryPass),
                typeof(ApplyDialectIdentifierShorteningPass),
                typeof(CanonicalizeOrderingPass)
            );
    }

    /// <summary>
    /// It should expose the canonical strict pass order.
    /// </summary>
    [Test]
    public void It_should_expose_the_canonical_strict_pass_order()
    {
        _strictPassTypes
            .Should()
            .Equal(
                typeof(BaseTraversalAndDescriptorBindingPass),
                typeof(DescriptorResourceMappingPass),
                typeof(ExtensionTableDerivationPass),
                typeof(ReferenceBindingPass),
                typeof(KeyUnificationPass),
                typeof(AbstractIdentityTableAndUnionViewDerivationPass),
                typeof(ValidateUnifiedAliasMetadataPass),
                typeof(RootIdentityConstraintPass),
                typeof(TransitiveIdentityMutabilityPass),
                typeof(ReferenceConstraintPass),
                typeof(SemanticIdentityCompilationPass),
                typeof(ValidateCollectionSemanticIdentityPass),
                typeof(ArrayUniquenessConstraintPass),
                typeof(StableCollectionConstraintPass),
                typeof(DescriptorForeignKeyConstraintPass),
                typeof(ApplyConstraintDialectHashingPass),
                typeof(ValidateForeignKeyStorageInvariantPass),
                typeof(DeriveIndexInventoryPass),
                typeof(DeriveTriggerInventoryPass),
                typeof(DeriveAuthHierarchyPass),
                typeof(DeriveAuthorizationIndexInventoryPass),
                typeof(ApplyDialectIdentifierShorteningPass),
                typeof(CanonicalizeOrderingPass)
            );
    }

    /// <summary>
    /// It should be stable across invocations.
    /// </summary>
    [Test]
    public void It_should_be_stable_across_invocations()
    {
        _defaultPassTypes.Should().Equal(_secondDefaultPassTypes);
        _strictPassTypes.Should().Equal(_secondStrictPassTypes);
    }

    /// <summary>
    /// It should keep strict semantic identity validation between compilation and downstream collection constraints.
    /// </summary>
    [Test]
    public void It_should_insert_strict_semantic_identity_validation_between_compilation_and_downstream_collection_constraint_passes()
    {
        var semanticIdentityCompilationIndex = _strictPassTypes
            .ToList()
            .IndexOf(typeof(SemanticIdentityCompilationPass));
        var semanticIdentityValidationIndex = _strictPassTypes
            .ToList()
            .IndexOf(typeof(ValidateCollectionSemanticIdentityPass));
        var arrayUniquenessIndex = _strictPassTypes.ToList().IndexOf(typeof(ArrayUniquenessConstraintPass));
        var stableCollectionIndex = _strictPassTypes.ToList().IndexOf(typeof(StableCollectionConstraintPass));

        semanticIdentityCompilationIndex.Should().BeGreaterThan(-1);
        semanticIdentityValidationIndex.Should().BeGreaterThan(-1);
        semanticIdentityCompilationIndex.Should().BeLessThan(semanticIdentityValidationIndex);
        semanticIdentityValidationIndex.Should().BeLessThan(arrayUniquenessIndex);
        semanticIdentityValidationIndex.Should().BeLessThan(stableCollectionIndex);
        semanticIdentityCompilationIndex.Should().BeLessThan(arrayUniquenessIndex);
        semanticIdentityCompilationIndex.Should().BeLessThan(stableCollectionIndex);
    }

    /// <summary>
    /// It should omit semantic identity validation from the default pipeline while preserving downstream ordering.
    /// </summary>
    [Test]
    public void It_should_omit_semantic_identity_validation_from_the_default_pipeline_while_preserving_downstream_ordering()
    {
        var semanticIdentityCompilationIndex = _defaultPassTypes
            .ToList()
            .IndexOf(typeof(SemanticIdentityCompilationPass));
        var semanticIdentityValidationIndex = _defaultPassTypes
            .ToList()
            .IndexOf(typeof(ValidateCollectionSemanticIdentityPass));
        var arrayUniquenessIndex = _defaultPassTypes.ToList().IndexOf(typeof(ArrayUniquenessConstraintPass));
        var stableCollectionIndex = _defaultPassTypes
            .ToList()
            .IndexOf(typeof(StableCollectionConstraintPass));

        semanticIdentityCompilationIndex.Should().BeGreaterThan(-1);
        semanticIdentityValidationIndex.Should().Be(-1);
        semanticIdentityCompilationIndex.Should().BeLessThan(arrayUniquenessIndex);
        semanticIdentityCompilationIndex.Should().BeLessThan(stableCollectionIndex);
    }
}

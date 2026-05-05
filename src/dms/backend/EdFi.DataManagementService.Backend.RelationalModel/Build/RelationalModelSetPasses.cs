// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.RelationalModel.Build;

/// <summary>
/// Provides factory methods for the canonical set-level relational model derivation passes.
/// </summary>
public static class RelationalModelSetPasses
{
    /// <summary>
    /// Creates the default ordered pass list for deriving a <see cref="DerivedRelationalModelSet"/>.
    /// </summary>
    /// <returns>The ordered pass list.</returns>
    public static IReadOnlyList<IRelationalModelSetPass> CreateDefault() =>
        CreatePasses(includeCollectionSemanticIdentityValidation: false);

    /// <summary>
    /// Creates the strict ordered pass list for deriving a <see cref="DerivedRelationalModelSet"/>.
    /// </summary>
    /// <returns>The ordered pass list.</returns>
    public static IReadOnlyList<IRelationalModelSetPass> CreateStrict() =>
        CreatePasses(includeCollectionSemanticIdentityValidation: true);

    private static IReadOnlyList<IRelationalModelSetPass> CreatePasses(
        bool includeCollectionSemanticIdentityValidation
    )
    {
        IReadOnlyList<IRelationalModelSetPass> collectionSemanticIdentityValidationPasses =
            includeCollectionSemanticIdentityValidation ? [new ValidateCollectionSemanticIdentityPass()] : [];

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
            new TransitiveIdentityMutabilityPass(),
            new ReferenceConstraintPass(),
            new SemanticIdentityCompilationPass(),
            .. collectionSemanticIdentityValidationPasses,
            new ArrayUniquenessConstraintPass(),
            new StableCollectionConstraintPass(),
            new DescriptorForeignKeyConstraintPass(),
            new ApplyConstraintDialectHashingPass(),
            new ValidateForeignKeyStorageInvariantPass(),
            // Index and trigger inventory passes must precede dialect shortening,
            // which rewrites all identifiers including those in the inventories.
            new DeriveIndexInventoryPass(),
            new DeriveTriggerInventoryPass(),
            // Auth hierarchy pass must follow trigger inventory so that concrete
            // resources, abstract identity tables, and union views are populated.
            new DeriveAuthHierarchyPass(),
            // Authorization index pass must run after DeriveAuthHierarchyPass so all
            // Authorization-classified entries are appended together, and before
            // ApplyDialectIdentifierShorteningPass / CanonicalizeOrderingPass so the new
            // indexes participate in dialect-aware shortening and canonical ordering.
            new DeriveAuthorizationIndexInventoryPass(
                throwOnMissingPaLiteral: includeCollectionSemanticIdentityValidation
            ),
            new ApplyDialectIdentifierShorteningPass(),
            new CanonicalizeOrderingPass(),
        ];
    }
}

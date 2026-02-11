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
    public static IReadOnlyList<IRelationalModelSetPass> CreateDefault()
    {
        IRelationalModelSetPass[] passes =
        [
            new BaseTraversalAndDescriptorBindingPass(),
            new DescriptorResourceMappingPass(),
            new ExtensionTableDerivationPass(),
            new ReferenceBindingPass(),
            new AbstractIdentityTableAndUnionViewDerivationPass(),
            new RootIdentityConstraintPass(),
            new ReferenceConstraintPass(),
            new ArrayUniquenessConstraintPass(),
            new ApplyConstraintDialectHashingPass(),
            new ApplyDialectIdentifierShorteningPass(),
            new CanonicalizeOrderingPass(),
        ];

        return passes;
    }
}

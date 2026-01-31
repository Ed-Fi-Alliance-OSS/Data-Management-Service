// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.RelationalModel;

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
            new BaseTraversalAndDescriptorBindingRelationalModelSetPass(),
            new ExtensionTableDerivationRelationalModelSetPass(),
            new AbstractIdentityTableDerivationRelationalModelSetPass(),
            new ReferenceBindingRelationalModelSetPass(),
            new ConstraintDerivationRelationalModelSetPass(),
        ];

        return passes;
    }
}

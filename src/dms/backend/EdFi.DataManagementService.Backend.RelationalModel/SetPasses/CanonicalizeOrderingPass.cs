// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.RelationalModel;

/// <summary>
/// Re-applies canonical ordering to concrete resource models after set-level mutation passes.
/// </summary>
public sealed class CanonicalizeOrderingPass : IRelationalModelSetPass
{
    /// <summary>
    /// Canonicalizes tables, document reference bindings, and descriptor edges for all concrete resources.
    /// </summary>
    /// <param name="context">The shared set-level builder context.</param>
    public void Execute(RelationalModelSetBuilderContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        for (var index = 0; index < context.ConcreteResourcesInNameOrder.Count; index++)
        {
            var resource = context.ConcreteResourcesInNameOrder[index];
            var canonicalModel = RelationalModelCanonicalization.CanonicalizeResourceModel(
                resource.RelationalModel
            );

            context.ConcreteResourcesInNameOrder[index] = resource with { RelationalModel = canonicalModel };
        }
    }
}

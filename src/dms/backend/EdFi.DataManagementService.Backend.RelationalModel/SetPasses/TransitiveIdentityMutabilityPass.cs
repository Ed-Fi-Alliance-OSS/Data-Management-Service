// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using static EdFi.DataManagementService.Backend.RelationalModel.Schema.RelationalModelSetSchemaHelpers;

namespace EdFi.DataManagementService.Backend.RelationalModel.SetPasses;

/// <summary>
/// Computes <see cref="Build.RelationalModelBuilderContext.TransitivelyAllowIdentityUpdates"/> for each
/// concrete resource by propagating identity mutability through part-of-identity reference edges.
/// </summary>
public sealed class TransitiveIdentityMutabilityPass : IRelationalModelSetPass
{
    /// <summary>
    /// Seeds transitive mutability from <c>AllowIdentityUpdates</c> and propagates through
    /// part-of-identity references until a fixed point is reached.
    /// </summary>
    public void Execute(RelationalModelSetBuilderContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Build resource name -> builder context map for all concrete (non-extension) resources.
        Dictionary<QualifiedResourceName, RelationalModelBuilderContext> builderContextsByResource = [];

        foreach (var resourceContext in context.EnumerateConcreteResourceSchemasInNameOrder())
        {
            if (IsResourceExtension(resourceContext))
            {
                continue;
            }

            var resource = new QualifiedResourceName(
                resourceContext.Project.ProjectSchema.ProjectName,
                resourceContext.ResourceName
            );
            var builderContext = context.GetOrCreateResourceBuilderContext(resourceContext);
            builderContextsByResource[resource] = builderContext;
        }

        // Seed: copy AllowIdentityUpdates into TransitivelyAllowIdentityUpdates.
        foreach (var builderContext in builderContextsByResource.Values)
        {
            builderContext.TransitivelyAllowIdentityUpdates = builderContext.AllowIdentityUpdates;
        }

        // Fixed-point propagation: if a resource has a part-of-identity reference to a
        // transitively-mutable target, mark it as transitively mutable too.
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (var (_, builderContext) in builderContextsByResource)
            {
                if (builderContext.TransitivelyAllowIdentityUpdates)
                {
                    continue;
                }

                foreach (var mapping in builderContext.DocumentReferenceMappings)
                {
                    if (!mapping.IsPartOfIdentity)
                    {
                        continue;
                    }

                    if (!builderContextsByResource.TryGetValue(
                            mapping.TargetResource,
                            out var targetBuilderContext
                        ) || !targetBuilderContext.TransitivelyAllowIdentityUpdates)
                    {
                        continue;
                    }

                    builderContext.TransitivelyAllowIdentityUpdates = true;
                    changed = true;
                    break;
                }
            }
        }
    }
}

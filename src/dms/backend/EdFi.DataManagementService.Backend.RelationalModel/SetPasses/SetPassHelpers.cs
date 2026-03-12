// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.RelationalModel.SetPasses;

/// <summary>
/// Shared helper types and methods used across multiple set-level derivation passes.
/// </summary>
internal static class SetPassHelpers
{
    /// <summary>
    /// Counts the number of array wildcard segments in the scope, used for depth-first ordering.
    /// </summary>
    internal static int CountArrayDepth(JsonPathExpression scope)
    {
        return scope.Segments.Count(segment => segment is JsonPathSegment.AnyArrayElement);
    }

    /// <summary>
    /// Builds the lookup of concrete base resources that resource extensions are allowed to target.
    /// Standalone resources from extension projects are excluded so they do not collide with core resource names.
    /// </summary>
    internal static Dictionary<string, List<TEntry>> BuildExtensionBaseResourceLookup<TEntry>(
        RelationalModelSetBuilderContext context,
        Func<int, ConcreteResourceModel, TEntry> entryFactory
    )
    {
        var standaloneExtensionResources = context
            .EnumerateConcreteResourceSchemasInNameOrder()
            .Where(resourceContext =>
                resourceContext.Project.ProjectSchema.IsExtensionProject
                && !EdFi.DataManagementService.Backend.RelationalModel.Schema.RelationalModelSetSchemaHelpers.IsResourceExtension(
                    resourceContext
                )
            )
            .Select(resourceContext => new QualifiedResourceName(
                resourceContext.Project.ProjectSchema.ProjectName,
                resourceContext.ResourceName
            ))
            .ToHashSet();

        Dictionary<string, List<TEntry>> lookup = new(StringComparer.Ordinal);

        for (var index = 0; index < context.ConcreteResourcesInNameOrder.Count; index++)
        {
            var resource = context.ConcreteResourcesInNameOrder[index].ResourceKey.Resource;

            if (standaloneExtensionResources.Contains(resource))
            {
                continue;
            }

            var resourceName = resource.ResourceName;

            if (!lookup.TryGetValue(resourceName, out var entries))
            {
                entries = [];
                lookup.Add(resourceName, entries);
            }

            entries.Add(entryFactory(index, context.ConcreteResourcesInNameOrder[index]));
        }

        return lookup;
    }
}

/// <summary>
/// Captures a concrete resource model and its index within the builder's canonical resource ordering.
/// Used by multiple set passes that need to track both the model and its positional index.
/// </summary>
internal sealed record BaseResourceEntry(int Index, ConcreteResourceModel Model);

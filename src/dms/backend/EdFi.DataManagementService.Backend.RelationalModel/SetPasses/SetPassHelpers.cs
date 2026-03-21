// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using static EdFi.DataManagementService.Backend.RelationalModel.Constraints.ConstraintDerivationHelpers;
using static EdFi.DataManagementService.Backend.RelationalModel.Schema.RelationalModelSetSchemaHelpers;

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

    /// <summary>
    /// Executes one shared mutation flow for concrete resources and resource extensions, resolving extension
    /// contributions back to the base resource model before applying accumulated mutations.
    /// </summary>
    internal static void ExecuteContributingResourceMutationPass(
        RelationalModelSetBuilderContext context,
        string failurePurpose,
        Func<RelationalModelBuilderContext, bool> shouldProcess,
        Action<
            ResourceMutation,
            RelationalResourceModel,
            RelationalModelBuilderContext,
            QualifiedResourceName
        > apply
    )
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(failurePurpose);
        ArgumentNullException.ThrowIfNull(shouldProcess);
        ArgumentNullException.ThrowIfNull(apply);

        var resourcesByKey = context
            .ConcreteResourcesInNameOrder.Select((model, index) => new ResourceEntry(index, model))
            .ToDictionary(entry => entry.Model.ResourceKey.Resource, entry => entry);
        var baseResourcesByName = BuildExtensionBaseResourceLookup(
            context,
            static (index, model) => new ResourceEntry(index, model)
        );
        Dictionary<QualifiedResourceName, ResourceMutation> mutations = [];

        foreach (var resourceContext in context.EnumerateConcreteResourceSchemasInNameOrder())
        {
            var resource = new QualifiedResourceName(
                resourceContext.Project.ProjectSchema.ProjectName,
                resourceContext.ResourceName
            );
            var builderContext = context.GetOrCreateResourceBuilderContext(resourceContext);

            if (!shouldProcess(builderContext))
            {
                continue;
            }

            var entry = ResolveContributingResourceEntry(
                resourceContext,
                resource,
                resourcesByKey,
                baseResourcesByName,
                failurePurpose
            );
            var targetResource = entry.Model.ResourceKey.Resource;
            var mutation = GetOrCreateMutation(targetResource, entry, mutations);

            apply(mutation, entry.Model.RelationalModel, builderContext, targetResource);
        }

        ApplyMutations(context, mutations);
    }

    /// <summary>
    /// Applies all accumulated resource mutations back to the shared builder context.
    /// </summary>
    internal static void ApplyMutations(
        RelationalModelSetBuilderContext context,
        IReadOnlyDictionary<QualifiedResourceName, ResourceMutation> mutations
    )
    {
        foreach (var mutation in mutations.Values)
        {
            if (!mutation.HasChanges)
            {
                continue;
            }

            var updatedModel = UpdateResourceModel(mutation.Entry.Model.RelationalModel, mutation);
            context.ConcreteResourcesInNameOrder[mutation.Entry.Index] = mutation.Entry.Model with
            {
                RelationalModel = updatedModel,
            };
        }
    }

    /// <summary>
    /// Resolves the concrete resource entry that should receive mutations for one contributing schema context.
    /// Resource extensions always contribute to their owning base resource model.
    /// </summary>
    private static ResourceEntry ResolveContributingResourceEntry(
        ConcreteResourceSchemaContext resourceContext,
        QualifiedResourceName resource,
        IReadOnlyDictionary<QualifiedResourceName, ResourceEntry> resourcesByKey,
        IReadOnlyDictionary<string, List<ResourceEntry>> baseResourcesByName,
        string failurePurpose
    )
    {
        if (IsResourceExtension(resourceContext))
        {
            return ResolveBaseResourceForExtension(
                resourceContext.ResourceName,
                resource,
                baseResourcesByName,
                static entry => entry.Model.ResourceKey.Resource
            );
        }

        if (resourcesByKey.TryGetValue(resource, out var entry))
        {
            return entry;
        }

        throw new InvalidOperationException(
            $"Concrete resource '{FormatResource(resource)}' was not found for {failurePurpose}."
        );
    }
}

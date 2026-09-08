// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend.Plans;

/// <summary>
/// Shared lookup helpers for concrete resource metadata in a compiled mapping set.
/// </summary>
public static class MappingSetResourceLookupExtensions
{
    private static readonly ConditionalWeakTable<
        DerivedRelationalModelSet,
        IReadOnlyDictionary<QualifiedResourceName, ConcreteResourceModel>
    > ConcreteResourceModelsByResourceByModelSet = new();

    /// <summary>
    /// Gets the concrete resource models keyed by qualified resource name from the model set's canonical
    /// resource list. Duplicate names keep the first model, matching <c>PersonJoinPathResolver.BuildResourceLookup</c>.
    /// </summary>
    public static IReadOnlyDictionary<
        QualifiedResourceName,
        ConcreteResourceModel
    > GetConcreteResourceModelsByResource(this DerivedRelationalModelSet modelSet)
    {
        ArgumentNullException.ThrowIfNull(modelSet);

        return ConcreteResourceModelsByResourceByModelSet.GetValue(
            modelSet,
            static staticModelSet =>
                BuildConcreteResourceModelsByResource(staticModelSet.ConcreteResourcesInNameOrder)
                    .ToFrozenDictionary()
        );
    }

    /// <summary>
    /// Builds a concrete resource lookup keyed by the canonical resource key identity. Duplicate
    /// names keep the first model, matching <c>PersonJoinPathResolver.BuildResourceLookup</c>.
    /// </summary>
    public static Dictionary<
        QualifiedResourceName,
        ConcreteResourceModel
    > BuildConcreteResourceModelsByResource(IReadOnlyList<ConcreteResourceModel> concreteResourcesInNameOrder)
    {
        ArgumentNullException.ThrowIfNull(concreteResourcesInNameOrder);

        var resourcesByName = new Dictionary<QualifiedResourceName, ConcreteResourceModel>(
            concreteResourcesInNameOrder.Count
        );

        foreach (var concreteResourceModel in concreteResourcesInNameOrder)
        {
            resourcesByName.TryAdd(concreteResourceModel.ResourceKey.Resource, concreteResourceModel);
        }

        return resourcesByName;
    }

    /// <summary>
    /// Attempts to resolve the concrete resource model from the mapping set's canonical resource list.
    /// </summary>
    public static bool TryGetConcreteResourceModel(
        this MappingSet mappingSet,
        QualifiedResourceName resource,
        [NotNullWhen(true)] out ConcreteResourceModel? model
    )
    {
        ArgumentNullException.ThrowIfNull(mappingSet);

        return mappingSet.Model.GetConcreteResourceModelsByResource().TryGetValue(resource, out model);
    }

    /// <summary>
    /// Resolves the concrete resource model from the mapping set's canonical resource list or throws a deterministic error.
    /// </summary>
    public static ConcreteResourceModel GetConcreteResourceModelOrThrow(
        this MappingSet mappingSet,
        QualifiedResourceName resource
    )
    {
        if (mappingSet.TryGetConcreteResourceModel(resource, out var model))
        {
            return model;
        }

        throw new KeyNotFoundException(
            $"Mapping set '{FormatMappingSetKey(mappingSet.Key)}' does not contain resource '{FormatResource(resource)}' in ConcreteResourcesInNameOrder."
        );
    }

    /// <summary>
    /// Formats a qualified resource name as <c>{ProjectName}.{ResourceName}</c> for diagnostics.
    /// </summary>
    public static string FormatResource(QualifiedResourceName resource)
    {
        return $"{resource.ProjectName}.{resource.ResourceName}";
    }

    /// <summary>
    /// Formats a mapping set key as <c>{EffectiveSchemaHash}/{Dialect}/{RelationalMappingVersion}</c> for diagnostics.
    /// </summary>
    public static string FormatMappingSetKey(MappingSetKey key)
    {
        return $"{key.EffectiveSchemaHash}/{key.Dialect}/{key.RelationalMappingVersion}";
    }
}

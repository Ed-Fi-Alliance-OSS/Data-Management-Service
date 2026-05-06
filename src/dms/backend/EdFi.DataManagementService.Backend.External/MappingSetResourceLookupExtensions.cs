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
        MappingSet,
        IReadOnlyDictionary<QualifiedResourceName, ConcreteResourceModel>
    > ConcreteResourceModelsByResource = new();

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

        var concreteResourcesByResource = ConcreteResourceModelsByResource.GetValue(
            mappingSet,
            static staticMappingSet =>
            {
                var resourcesByName = new Dictionary<QualifiedResourceName, ConcreteResourceModel>();

                foreach (var concreteResourceModel in staticMappingSet.Model.ConcreteResourcesInNameOrder)
                {
                    var candidateResource = concreteResourceModel.RelationalModel.Resource;

                    if (!resourcesByName.TryAdd(candidateResource, concreteResourceModel))
                    {
                        throw new InvalidOperationException(
                            $"Mapping set '{FormatMappingSetKey(staticMappingSet.Key)}' contains duplicate resource "
                                + $"'{FormatResource(candidateResource)}' in ConcreteResourcesInNameOrder."
                        );
                    }
                }

                return resourcesByName.ToFrozenDictionary();
            }
        );

        return concreteResourcesByResource.TryGetValue(resource, out model);
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

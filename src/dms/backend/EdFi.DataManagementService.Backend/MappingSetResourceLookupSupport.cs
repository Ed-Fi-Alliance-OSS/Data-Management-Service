// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend;

internal static class MappingSetResourceLookupSupport
{
    private static readonly ConditionalWeakTable<
        MappingSet,
        FrozenDictionary<QualifiedResourceName, ConcreteResourceModel>
    > ConcreteResourceModelsByResource = new();

    public static bool TryGetConcreteResourceModel(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        [NotNullWhen(true)] out ConcreteResourceModel? concreteResourceModel
    )
    {
        ArgumentNullException.ThrowIfNull(mappingSet);

        var concreteResourcesByResource = ConcreteResourceModelsByResource.GetValue(
            mappingSet,
            static staticMappingSet =>
            {
                Dictionary<QualifiedResourceName, ConcreteResourceModel> resourcesByResource = [];

                foreach (var concreteResourceModel in staticMappingSet.Model.ConcreteResourcesInNameOrder)
                {
                    var candidateResource = concreteResourceModel.ResourceKey.Resource;

                    if (!resourcesByResource.TryAdd(candidateResource, concreteResourceModel))
                    {
                        throw new InvalidOperationException(
                            $"Mapping set '{FormatMappingSetKey(staticMappingSet.Key)}' contains duplicate resource "
                                + $"'{FormatResource(candidateResource)}' in ConcreteResourcesInNameOrder."
                        );
                    }
                }

                return resourcesByResource.ToFrozenDictionary();
            }
        );

        if (concreteResourcesByResource.TryGetValue(resource, out concreteResourceModel))
        {
            return true;
        }

        concreteResourceModel = null;
        return false;
    }

    public static string FormatResource(QualifiedResourceName resource)
    {
        return $"{resource.ProjectName}.{resource.ResourceName}";
    }

    private static string FormatMappingSetKey(MappingSetKey key)
    {
        return $"{key.EffectiveSchemaHash}/{key.Dialect}/{key.RelationalMappingVersion}";
    }
}

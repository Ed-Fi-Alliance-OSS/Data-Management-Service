// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Frozen;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;

namespace EdFi.DataManagementService.Backend.Plans;

/// <summary>
/// Compiles a mapping set from a derived relational model set.
/// </summary>
public sealed class MappingSetCompiler
{
    /// <summary>
    /// Compiles a mapping set for one dialect-specific derived relational model set.
    /// </summary>
    public MappingSet Compile(DerivedRelationalModelSet modelSet)
    {
        ArgumentNullException.ThrowIfNull(modelSet);

        var key = new MappingSetKey(
            EffectiveSchemaHash: modelSet.EffectiveSchema.EffectiveSchemaHash,
            Dialect: modelSet.Dialect,
            RelationalMappingVersion: modelSet.EffectiveSchema.RelationalMappingVersion
        );

        var readPlanCompiler = new ReadPlanCompiler(modelSet.Dialect);
        var writePlanCompiler = new WritePlanCompiler(modelSet.Dialect);
        var queryCapabilityCompiler = new RelationalQueryCapabilityCompiler();

        var writePlansByResource = new Dictionary<QualifiedResourceName, ResourceWritePlan>();
        var readPlansByResource = new Dictionary<QualifiedResourceName, ResourceReadPlan>();
        var queryCapabilitiesByResource = new Dictionary<QualifiedResourceName, RelationalQueryCapability>();

        foreach (var concreteResourceModel in modelSet.ConcreteResourcesInNameOrder)
        {
            var resourceModel = concreteResourceModel.RelationalModel;
            var resourceStorageKind = concreteResourceModel.StorageKind;

            if (resourceStorageKind != resourceModel.StorageKind)
            {
                throw new InvalidOperationException(
                    $"Cannot compile mapping set: storage kind mismatch for resource '{resourceModel.Resource.ProjectName}.{resourceModel.Resource.ResourceName}' (concrete resource model: '{resourceStorageKind}', relational model: '{resourceModel.StorageKind}')."
                );
            }

            if (readPlanCompiler.TryCompile(resourceModel, out var readPlan))
            {
                if (!readPlansByResource.TryAdd(resourceModel.Resource, readPlan))
                {
                    throw new InvalidOperationException(
                        $"Cannot compile mapping set: duplicate read plan for resource '{resourceModel.Resource.ProjectName}.{resourceModel.Resource.ResourceName}'."
                    );
                }
            }

            if (
                !queryCapabilitiesByResource.TryAdd(
                    resourceModel.Resource,
                    queryCapabilityCompiler.Compile(concreteResourceModel)
                )
            )
            {
                throw new InvalidOperationException(
                    $"Cannot compile mapping set: duplicate query capability for resource '{resourceModel.Resource.ProjectName}.{resourceModel.Resource.ResourceName}'."
                );
            }

            if (resourceStorageKind is ResourceStorageKind.RelationalTables)
            {
                var writePlan = writePlanCompiler.Compile(resourceModel);

                if (!writePlansByResource.TryAdd(resourceModel.Resource, writePlan))
                {
                    throw new InvalidOperationException(
                        $"Cannot compile mapping set: duplicate write plan for resource '{resourceModel.Resource.ProjectName}.{resourceModel.Resource.ResourceName}'."
                    );
                }
            }
        }

        var securableElementPathsByResource =
            new Dictionary<QualifiedResourceName, IReadOnlyList<ResolvedSecurableElementPath>>();

        foreach (var concreteResource in modelSet.ConcreteResourcesInNameOrder)
        {
            if (concreteResource.SecurableElements.HasAny)
            {
                var paths = SecurableElementColumnPathResolver.ResolveAll(
                    concreteResource,
                    modelSet.ConcreteResourcesInNameOrder
                );
                if (paths.Count > 0)
                {
                    securableElementPathsByResource.Add(concreteResource.RelationalModel.Resource, paths);
                }
            }
        }

        var resourceKeyIdByResource = new Dictionary<QualifiedResourceName, short>();
        var resourceKeyById = new Dictionary<short, ResourceKeyEntry>();

        foreach (var resourceKeyEntry in modelSet.EffectiveSchema.ResourceKeysInIdOrder)
        {
            if (!resourceKeyIdByResource.TryAdd(resourceKeyEntry.Resource, resourceKeyEntry.ResourceKeyId))
            {
                throw new InvalidOperationException(
                    $"Cannot compile mapping set: duplicate resource key entry for resource '{resourceKeyEntry.Resource.ProjectName}.{resourceKeyEntry.Resource.ResourceName}'."
                );
            }

            if (!resourceKeyById.TryAdd(resourceKeyEntry.ResourceKeyId, resourceKeyEntry))
            {
                throw new InvalidOperationException(
                    $"Cannot compile mapping set: duplicate resource key id '{resourceKeyEntry.ResourceKeyId}'."
                );
            }
        }

        return new MappingSet(
            Key: key,
            Model: modelSet,
            WritePlansByResource: writePlansByResource.ToFrozenDictionary(),
            ReadPlansByResource: readPlansByResource.ToFrozenDictionary(),
            ResourceKeyIdByResource: resourceKeyIdByResource.ToFrozenDictionary(),
            ResourceKeyById: resourceKeyById.ToFrozenDictionary(),
            SecurableElementColumnPathsByResource: securableElementPathsByResource.ToFrozenDictionary()
        )
        {
            QueryCapabilitiesByResource = queryCapabilitiesByResource.ToFrozenDictionary(),
        };
    }
}

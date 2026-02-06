// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.RelationalModel;

/// <summary>
/// Detects descriptor resources across the effective schema set and applies the <c>SharedDescriptorTable</c> storage
/// kind with appropriate metadata.
/// </summary>
public sealed class DescriptorResourceMappingRelationalModelSetPass : IRelationalModelSetPass
{
    /// <summary>
    /// Executes descriptor resource detection and validation across all concrete resources in the effective schema set.
    /// </summary>
    /// <param name="context">The shared set-level builder context.</param>
    public void Execute(RelationalModelSetBuilderContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Build lookup dictionary once to avoid O(n × m) complexity
        var schemaLookup = context
            .EnumerateConcreteResourceSchemasInNameOrder()
            .ToDictionary(rc => (rc.Project.ProjectSchema.ProjectName, rc.ResourceName), rc => rc);

        var descriptorResources =
            new List<(int Index, QualifiedResourceName ResourceName, DescriptorMetadata Metadata)>();

        for (int i = 0; i < context.ConcreteResourcesInNameOrder.Count; i++)
        {
            var concreteResource = context.ConcreteResourcesInNameOrder[i];
            var resourceKey = concreteResource.ResourceKey;
            var qname = resourceKey.Resource;

            // Defensive check: If StorageKind is already SharedDescriptorTable, verify naming convention
            if (concreteResource.StorageKind == ResourceStorageKind.SharedDescriptorTable)
            {
                if (!qname.ResourceName.EndsWith("Descriptor", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Resource '{qname.ProjectName}.{qname.ResourceName}' has StorageKind.SharedDescriptorTable but does not follow the 'Descriptor' naming convention. "
                            + "This indicates an inconsistency in the ApiSchema.json file where isDescriptor=true but the resource name does not end with 'Descriptor'."
                    );
                }
            }

            if (!schemaLookup.TryGetValue((qname.ProjectName, qname.ResourceName), out var resourceContext))
            {
                continue;
            }

            var resourceSchema = resourceContext.ResourceSchema;

            if (!DescriptorSchemaValidator.IsDescriptorResource(resourceSchema))
            {
                continue;
            }

            var validation = DescriptorSchemaValidator.ValidateDescriptorSchema(resourceSchema);

            if (!validation.IsValid)
            {
                var errorMessage = string.Join("; ", validation.Errors);
                throw new InvalidOperationException(
                    $"Descriptor resource '{qname.ProjectName}.{qname.ResourceName}' is incompatible with dms.Descriptor contract: {errorMessage}"
                );
            }

            var metadata = BuildDescriptorMetadata();
            descriptorResources.Add((i, qname, metadata));
        }

        foreach (var (index, _, metadata) in descriptorResources)
        {
            var current = context.ConcreteResourcesInNameOrder[index];
            var updated = current with
            {
                StorageKind = ResourceStorageKind.SharedDescriptorTable,
                DescriptorMetadata = metadata,
            };
            context.ConcreteResourcesInNameOrder[index] = updated;
        }
    }

    /// <summary>
    /// Builds descriptor metadata using the canonical column contract and discriminator strategy.
    /// </summary>
    /// <returns>Descriptor metadata for the resource.</returns>
    private static DescriptorMetadata BuildDescriptorMetadata()
    {
        var columnContract = new DescriptorColumnContract(
            Namespace: new DbColumnName("Namespace"),
            CodeValue: new DbColumnName("CodeValue"),
            ShortDescription: new DbColumnName("ShortDescription"),
            Description: new DbColumnName("Description"),
            EffectiveBeginDate: new DbColumnName("EffectiveBeginDate"),
            EffectiveEndDate: new DbColumnName("EffectiveEndDate"),
            Discriminator: null
        );

        return new DescriptorMetadata(columnContract, DiscriminatorStrategy.ResourceKeyId);
    }
}

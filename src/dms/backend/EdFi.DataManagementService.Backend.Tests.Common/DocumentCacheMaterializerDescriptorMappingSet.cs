// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;

namespace EdFi.DataManagementService.Backend.Tests.Common;

internal static class DocumentCacheMaterializerDescriptorMappingSet
{
    private const short DescriptorResourceKeyId = 13;

    private static readonly QualifiedResourceName DescriptorResource = new("Ed-Fi", "SchoolTypeDescriptor");

    public static MappingSet Create(SqlDialect dialect)
    {
        var descriptorKey = new ResourceKeyEntry(DescriptorResourceKeyId, DescriptorResource, "1.0", false);
        var descriptorModel = new ConcreteResourceModel(
            descriptorKey,
            ResourceStorageKind.SharedDescriptorTable,
            CreateDescriptorRelationalModel()
        );
        var effectiveSchema = new EffectiveSchemaInfo(
            ApiSchemaFormatVersion: "1.0",
            RelationalMappingVersion: "v1",
            EffectiveSchemaHash: "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            ResourceKeyCount: 1,
            ResourceKeySeedHash: new byte[32],
            SchemaComponentsInEndpointOrder: [],
            ResourceKeysInIdOrder: [descriptorKey]
        );

        return new MappingSet(
            new MappingSetKey(effectiveSchema.EffectiveSchemaHash, dialect, "v1"),
            new DerivedRelationalModelSet(
                effectiveSchema,
                dialect,
                ProjectSchemasInEndpointOrder: [],
                ConcreteResourcesInNameOrder: [descriptorModel],
                AbstractIdentityTablesInNameOrder: [],
                AbstractUnionViewsInNameOrder: [],
                IndexesInCreateOrder: [],
                TriggersInCreateOrder: []
            ),
            WritePlansByResource: new Dictionary<QualifiedResourceName, ResourceWritePlan>(),
            ReadPlansByResource: new Dictionary<QualifiedResourceName, ResourceReadPlan>(),
            ResourceKeyIdByResource: new Dictionary<QualifiedResourceName, short>
            {
                [DescriptorResource] = DescriptorResourceKeyId,
            },
            ResourceKeyById: new Dictionary<short, ResourceKeyEntry>
            {
                [DescriptorResourceKeyId] = descriptorKey,
            },
            SecurableElementColumnPathsByResource: new Dictionary<
                QualifiedResourceName,
                IReadOnlyList<ResolvedSecurableElementPath>
            >()
        );
    }

    private static RelationalResourceModel CreateDescriptorRelationalModel()
    {
        var descriptorTable = new DbTableModel(
            new DbTableName(new DbSchemaName("dms"), "Descriptor"),
            new JsonPathExpression("$", []),
            new TableKey(
                "PK_Descriptor",
                [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            [
                new DbColumnModel(
                    new DbColumnName("DocumentId"),
                    ColumnKind.ParentKeyPart,
                    new RelationalScalarType(ScalarKind.Int64),
                    false,
                    null,
                    null
                ),
            ],
            []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                DbTableKind.Root,
                [new DbColumnName("DocumentId")],
                [new DbColumnName("DocumentId")],
                [],
                []
            ),
        };

        return new RelationalResourceModel(
            DescriptorResource,
            new DbSchemaName("dms"),
            ResourceStorageKind.SharedDescriptorTable,
            descriptorTable,
            [descriptorTable],
            [],
            []
        );
    }
}

// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;

namespace EdFi.DataManagementService.Backend.Tests.Common;

/// <summary>Focused authorization model with qualified descriptor types and independent key domains.</summary>
internal sealed class CompactDescriptorAuthorizationFixture
{
    public const long DocumentId = 5000000042L;
    public const int DescriptorId = 42;
    public static readonly QualifiedResourceName EdFiKind = new("Ed-Fi", "KindDescriptor");
    public static readonly QualifiedResourceName SampleKind = new("Sample", "KindDescriptor");
    public static readonly DbTableName DescriptorTable = new(new DbSchemaName("dms"), "Descriptor");
    public ConcreteResourceModel Subject { get; }
    public ConcreteResourceModel Intermediate { get; }
    public MappingSet MappingSet { get; }

    public CompactDescriptorAuthorizationFixture(SqlDialect dialect)
    {
        var root = Root(
            new(new("edfi"), "DescriptorOwner"),
            [
                new(new("DocumentId"), ColumnKind.ParentKeyPart, new(ScalarKind.Int64), false, null, null),
                new(
                    new("Kind_DescriptorId"),
                    ColumnKind.DescriptorFk,
                    new(ScalarKind.Int32),
                    true,
                    new("$.kindDescriptor", []),
                    EdFiKind
                ),
                new(
                    new("SampleKind_DescriptorId"),
                    ColumnKind.DescriptorFk,
                    new(ScalarKind.Int32),
                    true,
                    new("$.sampleKindDescriptor", []),
                    SampleKind
                ),
            ]
        );
        Subject = new(
            new(1, new("Ed-Fi", "DescriptorOwner"), "1.0", false),
            ResourceStorageKind.RelationalTables,
            new(
                new("Ed-Fi", "DescriptorOwner"),
                new("edfi"),
                ResourceStorageKind.RelationalTables,
                root,
                [root],
                [],
                [
                    new(true, new("$.kindDescriptor", []), root.Table, new("Kind_DescriptorId"), EdFiKind),
                    new(
                        true,
                        new("$.sampleKindDescriptor", []),
                        root.Table,
                        new("SampleKind_DescriptorId"),
                        SampleKind
                    ),
                ]
            )
        )
        {
            SecurableElements = new([], ["$.kindDescriptor"], [], [], []),
        };
        var intermediateRoot = Root(
            new(new("edfi"), "ReferencingOwner"),
            [
                new(new("DocumentId"), ColumnKind.ParentKeyPart, new(ScalarKind.Int64), false, null, null),
                new(
                    new("Owner_DocumentId"),
                    ColumnKind.DocumentFk,
                    new(ScalarKind.Int64),
                    false,
                    null,
                    Subject.ResourceKey.Resource
                ),
            ]
        );
        Intermediate = new(
            new(2, new("Ed-Fi", "ReferencingOwner"), "1.0", false),
            ResourceStorageKind.RelationalTables,
            new(
                new("Ed-Fi", "ReferencingOwner"),
                new("edfi"),
                ResourceStorageKind.RelationalTables,
                intermediateRoot,
                [intermediateRoot],
                [
                    new(
                        true,
                        new("$.ownerReference", []),
                        intermediateRoot.Table,
                        new("Owner_DocumentId"),
                        Subject.ResourceKey.Resource,
                        []
                    ),
                ],
                []
            )
        );
        ConcreteResourceModel[] resources =
        [
            Subject,
            Intermediate,
            Descriptor(101, EdFiKind),
            Descriptor(202, SampleKind),
        ];
        MappingSet = new(
            new("hash", dialect, "v3"),
            new(new("1.0", "1.0", "test", 0, [], [], []), dialect, [], resources, [], [], [], []),
            new Dictionary<QualifiedResourceName, ResourceWritePlan>(),
            new Dictionary<QualifiedResourceName, ResourceReadPlan>(),
            resources.ToDictionary(r => r.ResourceKey.Resource, r => r.ResourceKey.ResourceKeyId),
            resources.ToDictionary(r => r.ResourceKey.ResourceKeyId, r => r.ResourceKey),
            new Dictionary<QualifiedResourceName, IReadOnlyList<ResolvedSecurableElementPath>>()
        );
    }

    public static SupportedCustomViewAuthorizationStrategy Strategy(
        QualifiedResourceName basis,
        int index = 0
    ) => new(new($"{basis.ResourceName}WithAllowedRows", index), index, basis);

    public RootWriteRowBuffer RootRow(object? descriptorValue) =>
        new(
            new(
                Subject.RelationalModel.Root,
                "insert",
                "update",
                null,
                new(100, 3, 1000),
                [
                    .. Subject.RelationalModel.Root.Columns.Select(column => new WriteColumnBinding(
                        column,
                        new WriteValueSource.Scalar(new("$", []), column.ScalarType!),
                        column.ColumnName.Value
                    )),
                ],
                []
            ),
            [
                new FlattenedWriteValue.Literal(DocumentId),
                new FlattenedWriteValue.Literal(descriptorValue),
                new FlattenedWriteValue.Literal(73),
            ]
        );

    private static DbTableModel Root(DbTableName table, IReadOnlyList<DbColumnModel> columns) =>
        new(
            table,
            new("$", []),
            new($"PK_{table.Name}", [new(new("DocumentId"), ColumnKind.ParentKeyPart)]),
            columns,
            []
        )
        {
            IdentityMetadata = new(DbTableKind.Root, [new("DocumentId")], [new("DocumentId")], [], []),
        };

    private static ConcreteResourceModel Descriptor(short key, QualifiedResourceName resource)
    {
        var root = new DbTableModel(
            DescriptorTable,
            new("$", []),
            new("PK_Descriptor", [new(new("DescriptorId"), ColumnKind.Scalar)]),
            [
                new(new("DescriptorId"), ColumnKind.Scalar, new(ScalarKind.Int32), false, null, null),
                new(new("DocumentId"), ColumnKind.ParentKeyPart, new(ScalarKind.Int64), false, null, null),
                new(
                    new("Namespace"),
                    ColumnKind.Scalar,
                    new(ScalarKind.String, MaxLength: 255),
                    false,
                    null,
                    null
                ),
            ],
            []
        );
        return new(
            new(key, resource, "1.0", true),
            ResourceStorageKind.SharedDescriptorTable,
            new(resource, new("dms"), ResourceStorageKind.SharedDescriptorTable, root, [root], [], []),
            new(
                new(
                    new("Namespace"),
                    new("CodeValue"),
                    new("ShortDescription"),
                    new("Description"),
                    new("EffectiveBeginDate"),
                    new("EffectiveEndDate"),
                    null
                ),
                DiscriminatorStrategy.ResourceKeyId
            )
        );
    }
}

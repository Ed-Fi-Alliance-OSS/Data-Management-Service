// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend.Tests.Common;

/// <summary>History snapshots for two same-named descriptor types in different projects.</summary>
internal sealed class CompactDescriptorHistoryFixture
{
    public CompactDescriptorAuthorizationFixture Authorization { get; }
    public TrackedChangeTableInfo ResourceHistory { get; }
    public TrackedChangeTableInfo DescriptorHistory { get; }
    public MappingSet MappingSet { get; }

    public CompactDescriptorHistoryFixture(SqlDialect dialect)
    {
        Authorization = new(dialect);
        ResourceHistory = new(
            new(new("tracked_changes_edfi"), "DescriptorOwner"),
            TrackedChangeTableKind.Resource,
            Authorization.Subject.RelationalModel.Root.Table,
            [
                Value(
                    "Kind_Namespace",
                    "$.kindDescriptor",
                    TrackedChangeColumnRole.DescriptorNamespace,
                    "Kind"
                ),
                Value(
                    "Kind_CodeValue",
                    "$.kindDescriptor",
                    TrackedChangeColumnRole.DescriptorCodeValue,
                    "Kind"
                ),
                Value(
                    "SampleKind_Namespace",
                    "$.sampleKindDescriptor",
                    TrackedChangeColumnRole.DescriptorNamespace,
                    "SampleKind"
                ),
                Value(
                    "SampleKind_CodeValue",
                    "$.sampleKindDescriptor",
                    TrackedChangeColumnRole.DescriptorCodeValue,
                    "SampleKind"
                ),
            ],
            SystemColumns(),
            [new("ChangeVersion")],
            [
                new("Kind", new("Kind_DescriptorId"), CompactDescriptorAuthorizationFixture.EdFiKind),
                new(
                    "SampleKind",
                    new("SampleKind_DescriptorId"),
                    CompactDescriptorAuthorizationFixture.SampleKind
                ),
            ],
            []
        );
        DescriptorHistory = new(
            new(new("tracked_changes_edfi"), "Descriptor"),
            TrackedChangeTableKind.SharedDescriptor,
            CompactDescriptorAuthorizationFixture.DescriptorTable,
            [
                Value("Namespace", "$.namespace", TrackedChangeColumnRole.Scalar),
                Value("CodeValue", "$.codeValue", TrackedChangeColumnRole.Scalar),
            ],
            [
                .. SystemColumns(),
                new(TrackedChangeSystemColumnRole.ResourceKeyId, new("ResourceKeyId"), null, false, false),
            ],
            [new("ChangeVersion")],
            [],
            []
        );
        MappingSet = Authorization.MappingSet with
        {
            Model = Authorization.MappingSet.Model with
            {
                TrackedChangeTablesInNameOrder = [DescriptorHistory, ResourceHistory],
            },
        };
    }

    public ConcreteResourceModel Descriptor(QualifiedResourceName resource) =>
        MappingSet.Model.ConcreteResourcesInNameOrder.Single(model => model.ResourceKey.Resource == resource);

    private static TrackedChangeColumnInfo Value(
        string name,
        string path,
        TrackedChangeColumnRole role,
        string? join = null
    ) =>
        new(
            new($"Old{name}"),
            new($"New{name}"),
            path,
            null,
            false,
            true,
            new(ScalarKind.String, MaxLength: 255),
            role,
            TrackedChangeColumnOrigin.Identity,
            join
        );

    private static IReadOnlyList<TrackedChangeSystemColumnInfo> SystemColumns() =>
        [
            new(TrackedChangeSystemColumnRole.Id, new("Id"), null, false, false),
            new(
                TrackedChangeSystemColumnRole.ChangeVersion,
                new("ChangeVersion"),
                new(ScalarKind.Int64),
                false,
                true
            ),
            new(
                TrackedChangeSystemColumnRole.DocumentId,
                new("DocumentId"),
                new(ScalarKind.Int64),
                false,
                false
            ),
            new(
                TrackedChangeSystemColumnRole.CreatedAt,
                new("CreatedAt"),
                new(ScalarKind.DateTime),
                false,
                false
            ),
        ];
}

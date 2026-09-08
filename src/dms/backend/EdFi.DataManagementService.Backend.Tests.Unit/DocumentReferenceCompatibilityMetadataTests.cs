// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
public class Given_MappingSetDocumentReferenceCompatibilityExtensions
{
    private CompatibilityMetadataFixture _fixture = null!;

    [SetUp]
    public void Setup()
    {
        _fixture = CompatibilityMetadataFixture.Create();
    }

    [Test]
    public void It_resolves_concrete_targets_to_exactly_one_allowed_resource_key_id()
    {
        var metadata = _fixture.MappingSet.GetDocumentReferenceTargetMetadataOrThrow(
            new BaseResourceInfo(
                new ProjectName(_fixture.SchoolResource.ProjectName),
                new ResourceName(_fixture.SchoolResource.ResourceName),
                IsDescriptor: false
            )
        );

        metadata.TargetResource.Should().Be(_fixture.SchoolResource);
        metadata.AllowedResourceKeyIds.Should().Equal(20);
        metadata.AllowsResourceKeyId(20).Should().BeTrue();
        metadata.AllowsResourceKeyId(21).Should().BeFalse();
    }

    [Test]
    public void It_resolves_abstract_superclass_targets_to_allowed_concrete_member_resource_key_ids()
    {
        var metadata = _fixture.MappingSet.GetDocumentReferenceTargetMetadataOrThrow(
            _fixture.EducationOrganizationResource
        );

        metadata.TargetResource.Should().Be(_fixture.EducationOrganizationResource);
        metadata.AllowedResourceKeyIds.Should().Equal(22, 21, 20);
        metadata.AllowsResourceKeyId(22).Should().BeTrue();
        metadata.AllowsResourceKeyId(30).Should().BeFalse();
    }

    [Test]
    public void It_excludes_descriptor_resources_from_document_reference_compatibility_checks()
    {
        var act = () =>
            _fixture.MappingSet.GetDocumentReferenceTargetMetadataOrThrow(_fixture.DescriptorResource);

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                "Document-reference compatibility metadata lookup failed for target 'Ed-Fi.AcademicSubjectDescriptor' in mapping set 'test-hash/Pgsql/v1': "
                    + "target resource is a descriptor resource and is excluded from document-reference compatibility checks."
            );
    }

    [Test]
    public void It_fails_fast_when_abstract_target_union_view_metadata_is_missing()
    {
        var fixtureWithoutUnionView = CompatibilityMetadataFixture.Create(
            includeEducationOrganizationUnionView: false
        );

        var act = () =>
            fixtureWithoutUnionView.MappingSet.GetDocumentReferenceTargetMetadataOrThrow(
                fixtureWithoutUnionView.EducationOrganizationResource
            );

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                "Document-reference compatibility metadata lookup failed for target 'Ed-Fi.EducationOrganization' in mapping set 'test-hash/Pgsql/v1': "
                    + "target resource is abstract but AbstractUnionViewsInNameOrder does not contain a matching entry."
            );
    }

    private sealed record CompatibilityMetadataFixture(
        MappingSet MappingSet,
        QualifiedResourceName SchoolResource,
        QualifiedResourceName DescriptorResource,
        QualifiedResourceName EducationOrganizationResource
    )
    {
        public static CompatibilityMetadataFixture Create(bool includeEducationOrganizationUnionView = true)
        {
            var descriptorResource = new QualifiedResourceName("Ed-Fi", "AcademicSubjectDescriptor");
            var educationOrganizationResource = new QualifiedResourceName("Ed-Fi", "EducationOrganization");
            var educationServiceCenterResource = new QualifiedResourceName("Ed-Fi", "EducationServiceCenter");
            var localEducationAgencyResource = new QualifiedResourceName("Ed-Fi", "LocalEducationAgency");
            var schoolResource = new QualifiedResourceName("Ed-Fi", "School");
            var studentResource = new QualifiedResourceName("Ed-Fi", "Student");

            var descriptorKey = new ResourceKeyEntry(40, descriptorResource, "5.2.0", false);
            var educationOrganizationKey = new ResourceKeyEntry(
                30,
                educationOrganizationResource,
                "5.2.0",
                true
            );
            var educationServiceCenterKey = new ResourceKeyEntry(
                22,
                educationServiceCenterResource,
                "5.2.0",
                false
            );
            var localEducationAgencyKey = new ResourceKeyEntry(
                21,
                localEducationAgencyResource,
                "5.2.0",
                false
            );
            var schoolKey = new ResourceKeyEntry(20, schoolResource, "5.2.0", false);
            var studentKey = new ResourceKeyEntry(10, studentResource, "5.2.0", false);

            var effectiveSchema = new EffectiveSchemaInfo(
                ApiSchemaFormatVersion: "5.2",
                RelationalMappingVersion: "v1",
                EffectiveSchemaHash: "test-hash",
                ResourceKeyCount: 6,
                ResourceKeySeedHash: new byte[32],
                SchemaComponentsInEndpointOrder: [],
                ResourceKeysInIdOrder:
                [
                    studentKey,
                    schoolKey,
                    localEducationAgencyKey,
                    educationServiceCenterKey,
                    educationOrganizationKey,
                    descriptorKey,
                ]
            );

            var modelSet = new DerivedRelationalModelSet(
                EffectiveSchema: effectiveSchema,
                Dialect: SqlDialect.Pgsql,
                ProjectSchemasInEndpointOrder: [],
                ConcreteResourcesInNameOrder:
                [
                    new ConcreteResourceModel(
                        studentKey,
                        ResourceStorageKind.RelationalTables,
                        CreateRelationalResourceModel(studentResource, "Student")
                    ),
                    new ConcreteResourceModel(
                        schoolKey,
                        ResourceStorageKind.RelationalTables,
                        CreateRelationalResourceModel(schoolResource, "School")
                    ),
                    new ConcreteResourceModel(
                        localEducationAgencyKey,
                        ResourceStorageKind.RelationalTables,
                        CreateRelationalResourceModel(localEducationAgencyResource, "LocalEducationAgency")
                    ),
                    new ConcreteResourceModel(
                        educationServiceCenterKey,
                        ResourceStorageKind.RelationalTables,
                        CreateRelationalResourceModel(
                            educationServiceCenterResource,
                            "EducationServiceCenter"
                        )
                    ),
                    new ConcreteResourceModel(
                        descriptorKey,
                        ResourceStorageKind.SharedDescriptorTable,
                        CreateRelationalResourceModel(
                            descriptorResource,
                            "Descriptor",
                            ResourceStorageKind.SharedDescriptorTable
                        )
                    ),
                ],
                AbstractIdentityTablesInNameOrder: [],
                AbstractUnionViewsInNameOrder: includeEducationOrganizationUnionView
                    ?
                    [
                        new AbstractUnionViewInfo(
                            educationOrganizationKey,
                            new DbTableName(new DbSchemaName("edfi"), "EducationOrganization"),
                            [],
                            [
                                CreateUnionArm(educationServiceCenterKey, "EducationServiceCenter"),
                                CreateUnionArm(localEducationAgencyKey, "LocalEducationAgency"),
                                CreateUnionArm(schoolKey, "School"),
                            ]
                        ),
                    ]
                    : [],
                IndexesInCreateOrder: [],
                TriggersInCreateOrder: []
            );

            var resourceKeyIdByResource = effectiveSchema.ResourceKeysInIdOrder.ToDictionary(
                entry => entry.Resource,
                entry => entry.ResourceKeyId
            );
            var resourceKeyById = effectiveSchema.ResourceKeysInIdOrder.ToDictionary(
                entry => entry.ResourceKeyId,
                entry => entry
            );

            return new CompatibilityMetadataFixture(
                new MappingSet(
                    Key: new MappingSetKey("test-hash", SqlDialect.Pgsql, "v1"),
                    Model: modelSet,
                    WritePlansByResource: new Dictionary<QualifiedResourceName, ResourceWritePlan>(),
                    ReadPlansByResource: new Dictionary<QualifiedResourceName, ResourceReadPlan>(),
                    ResourceKeyIdByResource: resourceKeyIdByResource,
                    ResourceKeyById: resourceKeyById,
                    SecurableElementColumnPathsByResource: new Dictionary<
                        QualifiedResourceName,
                        IReadOnlyList<ResolvedSecurableElementPath>
                    >()
                ),
                SchoolResource: schoolResource,
                DescriptorResource: descriptorResource,
                EducationOrganizationResource: educationOrganizationResource
            );
        }

        private static RelationalResourceModel CreateRelationalResourceModel(
            QualifiedResourceName resource,
            string tableName,
            ResourceStorageKind storageKind = ResourceStorageKind.RelationalTables
        )
        {
            var rootTable = new DbTableModel(
                Table: new DbTableName(new DbSchemaName("edfi"), tableName),
                JsonScope: new JsonPathExpression("$", []),
                Key: new TableKey(
                    $"PK_{tableName}",
                    [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
                ),
                Columns:
                [
                    new DbColumnModel(
                        new DbColumnName("DocumentId"),
                        ColumnKind.ParentKeyPart,
                        new RelationalScalarType(ScalarKind.Int64),
                        IsNullable: false,
                        SourceJsonPath: null,
                        TargetResource: null
                    ),
                ],
                Constraints: []
            );

            return new RelationalResourceModel(
                Resource: resource,
                PhysicalSchema: new DbSchemaName("edfi"),
                StorageKind: storageKind,
                Root: rootTable,
                TablesInDependencyOrder: [rootTable],
                DocumentReferenceBindings: [],
                DescriptorEdgeSources: []
            );
        }

        private static AbstractUnionViewArm CreateUnionArm(
            ResourceKeyEntry concreteMemberResourceKey,
            string tableName
        ) =>
            new(
                ConcreteMemberResourceKey: concreteMemberResourceKey,
                FromTable: new DbTableName(new DbSchemaName("edfi"), tableName),
                ProjectionExpressionsInSelectOrder: []
            );
    }
}

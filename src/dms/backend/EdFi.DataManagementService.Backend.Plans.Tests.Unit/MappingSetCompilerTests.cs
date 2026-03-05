// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Frozen;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

[TestFixture]
public class Given_MappingSetCompiler
{
    [Test]
    public void It_should_compile_write_plans_for_all_relational_resources_while_read_plans_remain_thin_slice_gated()
    {
        var fixture = CreateMixedResourceFixture(SqlDialect.Pgsql);
        var compiler = new MappingSetCompiler();
        MappingSet mappingSet = null!;

        var act = () => mappingSet = compiler.Compile(fixture.ModelSet);

        act.Should().NotThrow();

        mappingSet.ReadPlansByResource.Should().ContainKey(fixture.SupportedResource);
        mappingSet.ReadPlansByResource.Should().ContainKey(fixture.KeyUnificationResource);
        mappingSet.ReadPlansByResource.Should().NotContainKey(fixture.ProjectionMetadataResource);
        mappingSet.ReadPlansByResource.Should().NotContainKey(fixture.NonRootOnlyResource);
        mappingSet.ReadPlansByResource.Should().NotContainKey(fixture.DescriptorResource);

        mappingSet.WritePlansByResource.Should().ContainKey(fixture.SupportedResource);
        mappingSet.WritePlansByResource.Should().ContainKey(fixture.ProjectionMetadataResource);
        mappingSet.WritePlansByResource.Should().ContainKey(fixture.KeyUnificationResource);
        mappingSet.WritePlansByResource.Should().ContainKey(fixture.NonRootOnlyResource);
        mappingSet.WritePlansByResource.Should().NotContainKey(fixture.DescriptorResource);
    }

    [Test]
    public void It_should_fail_when_concrete_resource_storage_kind_does_not_match_relational_model_storage_kind()
    {
        var fixture = CreateMixedResourceFixture(SqlDialect.Pgsql);
        var concreteResources = fixture.ModelSet.ConcreteResourcesInNameOrder.ToArray();
        concreteResources[2] = concreteResources[2] with
        {
            StorageKind = ResourceStorageKind.SharedDescriptorTable,
        };

        var mismatchedModelSet = fixture.ModelSet with { ConcreteResourcesInNameOrder = concreteResources };

        var act = () => new MappingSetCompiler().Compile(mismatchedModelSet);

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                $"Cannot compile mapping set: storage kind mismatch for resource '{fixture.SupportedResource.ProjectName}.{fixture.SupportedResource.ResourceName}' (concrete resource model: '{ResourceStorageKind.SharedDescriptorTable}', relational model: '{ResourceStorageKind.RelationalTables}')."
            );
    }

    [Test]
    public void It_should_build_mapping_set_key_and_resource_key_dictionaries_from_effective_schema_info()
    {
        var fixture = CreateMixedResourceFixture(SqlDialect.Mssql);
        var mappingSet = new MappingSetCompiler().Compile(fixture.ModelSet);

        mappingSet
            .Key.Should()
            .Be(
                new MappingSetKey(
                    EffectiveSchemaHash: fixture.ModelSet.EffectiveSchema.EffectiveSchemaHash,
                    Dialect: fixture.ModelSet.Dialect,
                    RelationalMappingVersion: fixture.ModelSet.EffectiveSchema.RelationalMappingVersion
                )
            );

        mappingSet.ResourceKeyIdByResource.Count.Should().Be(fixture.ResourceKeysInIdOrder.Count);
        mappingSet.ResourceKeyById.Count.Should().Be(fixture.ResourceKeysInIdOrder.Count);

        foreach (var resourceKeyEntry in fixture.ResourceKeysInIdOrder)
        {
            mappingSet
                .ResourceKeyIdByResource[resourceKeyEntry.Resource]
                .Should()
                .Be(resourceKeyEntry.ResourceKeyId);

            mappingSet.ResourceKeyById[resourceKeyEntry.ResourceKeyId].Should().Be(resourceKeyEntry);
        }
    }

    [Test]
    public void It_should_materialize_mapping_set_dictionaries_as_frozen_collections()
    {
        var fixture = CreateMixedResourceFixture(SqlDialect.Pgsql);
        var mappingSet = new MappingSetCompiler().Compile(fixture.ModelSet);

        mappingSet
            .WritePlansByResource.Should()
            .BeAssignableTo<FrozenDictionary<QualifiedResourceName, ResourceWritePlan>>();
        mappingSet
            .ReadPlansByResource.Should()
            .BeAssignableTo<FrozenDictionary<QualifiedResourceName, ResourceReadPlan>>();
        mappingSet
            .ResourceKeyIdByResource.Should()
            .BeAssignableTo<FrozenDictionary<QualifiedResourceName, short>>();
        mappingSet.ResourceKeyById.Should().BeAssignableTo<FrozenDictionary<short, ResourceKeyEntry>>();

        mappingSet
            .WritePlansByResource.Should()
            .NotBeAssignableTo<Dictionary<QualifiedResourceName, ResourceWritePlan>>();
        mappingSet
            .ReadPlansByResource.Should()
            .NotBeAssignableTo<Dictionary<QualifiedResourceName, ResourceReadPlan>>();
        mappingSet
            .ResourceKeyIdByResource.Should()
            .NotBeAssignableTo<Dictionary<QualifiedResourceName, short>>();
        mappingSet.ResourceKeyById.Should().NotBeAssignableTo<Dictionary<short, ResourceKeyEntry>>();

        var writePlans =
            (IDictionary<QualifiedResourceName, ResourceWritePlan>)mappingSet.WritePlansByResource;
        var readPlans = (IDictionary<QualifiedResourceName, ResourceReadPlan>)mappingSet.ReadPlansByResource;
        var resourceKeyIds = (IDictionary<QualifiedResourceName, short>)mappingSet.ResourceKeyIdByResource;
        var resourceKeys = (IDictionary<short, ResourceKeyEntry>)mappingSet.ResourceKeyById;

        var supportedResource = fixture.SupportedResource;
        var nonRootOnlyResource = fixture.NonRootOnlyResource;
        var supportedWritePlan = mappingSet.WritePlansByResource[supportedResource];
        var supportedReadPlan = mappingSet.ReadPlansByResource[supportedResource];
        var supportedResourceKeyId = mappingSet.ResourceKeyIdByResource[supportedResource];
        var supportedResourceKey = mappingSet.ResourceKeyById[supportedResourceKeyId];

        var actAddWritePlan = () => writePlans.Add(nonRootOnlyResource, supportedWritePlan);
        var actAddReadPlan = () => readPlans.Add(nonRootOnlyResource, supportedReadPlan);
        var actAddResourceKeyId = () => resourceKeyIds.Add(nonRootOnlyResource, supportedResourceKeyId);
        var actAddResourceKey = () => resourceKeys.Add(short.MaxValue, supportedResourceKey);

        actAddWritePlan.Should().Throw<NotSupportedException>();
        actAddReadPlan.Should().Throw<NotSupportedException>();
        actAddResourceKeyId.Should().Throw<NotSupportedException>();
        actAddResourceKey.Should().Throw<NotSupportedException>();
    }

    private static MappingSetCompilerFixture CreateMixedResourceFixture(SqlDialect dialect)
    {
        var descriptorResource = new QualifiedResourceName("Ed-Fi", "AcademicSubjectDescriptor");
        var keyUnificationResource = new QualifiedResourceName("Ed-Fi", "Program");
        var supportedResource = new QualifiedResourceName("Ed-Fi", "Student");
        var projectionMetadataResource = new QualifiedResourceName("Ed-Fi", "StudentProjection");
        var nonRootOnlyResource = new QualifiedResourceName("Ed-Fi", "StudentAddress");
        var abstractResource = new QualifiedResourceName("Ed-Fi", "EducationOrganization");

        var descriptorModel = CreateDescriptorModel(descriptorResource);
        var keyUnificationModel = CreateRootOnlyModelWithKeyUnification(keyUnificationResource, "Program");
        var supportedModel = CreateRootOnlyModel(supportedResource, "Student");
        var projectionMetadataModel = CreateRootOnlyModelWithDocumentReferenceBindings(
            projectionMetadataResource,
            "StudentProjection"
        );
        var nonRootOnlyModel = CreateNonRootOnlyModel(nonRootOnlyResource, "StudentAddress");

        var resourceKeysInIdOrder = new ResourceKeyEntry[]
        {
            new(100, descriptorResource, "5.2.0", false),
            new(101, keyUnificationResource, "5.2.0", false),
            new(102, supportedResource, "5.2.0", false),
            new(103, projectionMetadataResource, "5.2.0", false),
            new(104, nonRootOnlyResource, "5.2.0", false),
            new(105, abstractResource, "5.2.0", true),
        };

        var effectiveSchemaInfo = new EffectiveSchemaInfo(
            ApiSchemaFormatVersion: "5.2",
            RelationalMappingVersion: "v1",
            EffectiveSchemaHash: new string('a', 64),
            ResourceKeyCount: resourceKeysInIdOrder.Length,
            ResourceKeySeedHash: CreateResourceKeySeedHash(),
            SchemaComponentsInEndpointOrder:
            [
                new SchemaComponentInfo(
                    ProjectEndpointName: "ed-fi",
                    ProjectName: "Ed-Fi",
                    ProjectVersion: "5.2.0",
                    IsExtensionProject: false,
                    ProjectHash: new string('b', 64)
                ),
            ],
            ResourceKeysInIdOrder: resourceKeysInIdOrder
        );

        var modelSet = new DerivedRelationalModelSet(
            EffectiveSchema: effectiveSchemaInfo,
            Dialect: dialect,
            ProjectSchemasInEndpointOrder:
            [
                new ProjectSchemaInfo(
                    ProjectEndpointName: "ed-fi",
                    ProjectName: "Ed-Fi",
                    ProjectVersion: "5.2.0",
                    IsExtensionProject: false,
                    PhysicalSchema: new DbSchemaName("edfi")
                ),
            ],
            ConcreteResourcesInNameOrder:
            [
                new ConcreteResourceModel(
                    resourceKeysInIdOrder[0],
                    ResourceStorageKind.SharedDescriptorTable,
                    descriptorModel
                ),
                new ConcreteResourceModel(
                    resourceKeysInIdOrder[1],
                    ResourceStorageKind.RelationalTables,
                    keyUnificationModel
                ),
                new ConcreteResourceModel(
                    resourceKeysInIdOrder[2],
                    ResourceStorageKind.RelationalTables,
                    supportedModel
                ),
                new ConcreteResourceModel(
                    resourceKeysInIdOrder[3],
                    ResourceStorageKind.RelationalTables,
                    projectionMetadataModel
                ),
                new ConcreteResourceModel(
                    resourceKeysInIdOrder[4],
                    ResourceStorageKind.RelationalTables,
                    nonRootOnlyModel
                ),
            ],
            AbstractIdentityTablesInNameOrder: [],
            AbstractUnionViewsInNameOrder: [],
            IndexesInCreateOrder: [],
            TriggersInCreateOrder: []
        );

        return new MappingSetCompilerFixture(
            ModelSet: modelSet,
            SupportedResource: supportedResource,
            KeyUnificationResource: keyUnificationResource,
            ProjectionMetadataResource: projectionMetadataResource,
            NonRootOnlyResource: nonRootOnlyResource,
            DescriptorResource: descriptorResource,
            ResourceKeysInIdOrder: resourceKeysInIdOrder
        );
    }

    private static RelationalResourceModel CreateRootOnlyModel(
        QualifiedResourceName resource,
        string tableName
    )
    {
        var rootTable = CreateRootTable(
            schemaName: new DbSchemaName("edfi"),
            tableName,
            scalarColumnName: new DbColumnName("SchoolYear"),
            scalarSourcePathPropertyName: "schoolYear",
            scalarKind: ScalarKind.Int32
        );

        return new RelationalResourceModel(
            Resource: resource,
            PhysicalSchema: new DbSchemaName("edfi"),
            StorageKind: ResourceStorageKind.RelationalTables,
            Root: rootTable,
            TablesInDependencyOrder: [rootTable],
            DocumentReferenceBindings: [],
            DescriptorEdgeSources: []
        );
    }

    private static RelationalResourceModel CreateRootOnlyModelWithKeyUnification(
        QualifiedResourceName resource,
        string tableName
    )
    {
        var rootTable = new DbTableModel(
            Table: new DbTableName(new DbSchemaName("edfi"), tableName),
            JsonScope: new JsonPathExpression("$", []),
            Key: new TableKey(
                ConstraintName: $"PK_{tableName}",
                Columns: [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            Columns:
            [
                new DbColumnModel(
                    ColumnName: new DbColumnName("DocumentId"),
                    Kind: ColumnKind.ParentKeyPart,
                    ScalarType: new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("SchoolYearCanonical"),
                    Kind: ColumnKind.Scalar,
                    ScalarType: new RelationalScalarType(ScalarKind.Int32),
                    IsNullable: true,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("SchoolYear"),
                    Kind: ColumnKind.Scalar,
                    ScalarType: new RelationalScalarType(ScalarKind.Int32),
                    IsNullable: true,
                    SourceJsonPath: new JsonPathExpression(
                        "$.schoolYear",
                        [new JsonPathSegment.Property("schoolYear")]
                    ),
                    TargetResource: null,
                    Storage: new ColumnStorage.UnifiedAlias(
                        CanonicalColumn: new DbColumnName("SchoolYearCanonical"),
                        PresenceColumn: null
                    )
                ),
            ],
            Constraints: []
        )
        {
            KeyUnificationClasses =
            [
                new KeyUnificationClass(
                    CanonicalColumn: new DbColumnName("SchoolYearCanonical"),
                    MemberPathColumns: [new DbColumnName("SchoolYear")]
                ),
            ],
        };

        return new RelationalResourceModel(
            Resource: resource,
            PhysicalSchema: new DbSchemaName("edfi"),
            StorageKind: ResourceStorageKind.RelationalTables,
            Root: rootTable,
            TablesInDependencyOrder: [rootTable],
            DocumentReferenceBindings: [],
            DescriptorEdgeSources: []
        );
    }

    private static RelationalResourceModel CreateRootOnlyModelWithDocumentReferenceBindings(
        QualifiedResourceName resource,
        string tableName
    )
    {
        var model = CreateRootOnlyModel(resource, tableName);
        var binding = new DocumentReferenceBinding(
            IsIdentityComponent: false,
            ReferenceObjectPath: new JsonPathExpression(
                "$.schoolReference",
                [new JsonPathSegment.Property("schoolReference")]
            ),
            Table: model.Root.Table,
            FkColumn: new DbColumnName("SchoolYear"),
            TargetResource: new QualifiedResourceName("Ed-Fi", "School"),
            IdentityBindings:
            [
                new ReferenceIdentityBinding(
                    ReferenceJsonPath: new JsonPathExpression(
                        "$.schoolReference.schoolId",
                        [
                            new JsonPathSegment.Property("schoolReference"),
                            new JsonPathSegment.Property("schoolId"),
                        ]
                    ),
                    Column: new DbColumnName("SchoolYear")
                ),
            ]
        );

        return model with
        {
            DocumentReferenceBindings = [binding],
        };
    }

    private static RelationalResourceModel CreateNonRootOnlyModel(
        QualifiedResourceName resource,
        string rootTableName
    )
    {
        var rootTable = CreateRootTable(
            schemaName: new DbSchemaName("edfi"),
            rootTableName,
            scalarColumnName: new DbColumnName("StudentUniqueId"),
            scalarSourcePathPropertyName: "studentUniqueId",
            scalarKind: ScalarKind.String
        );
        var childTable = CreateChildTable("StudentAddressByType");

        return new RelationalResourceModel(
            Resource: resource,
            PhysicalSchema: new DbSchemaName("edfi"),
            StorageKind: ResourceStorageKind.RelationalTables,
            Root: rootTable,
            TablesInDependencyOrder: [rootTable, childTable],
            DocumentReferenceBindings: [],
            DescriptorEdgeSources: []
        );
    }

    private static RelationalResourceModel CreateDescriptorModel(QualifiedResourceName resource)
    {
        var rootTable = CreateRootTable(
            schemaName: new DbSchemaName("dms"),
            tableName: "Descriptor",
            scalarColumnName: new DbColumnName("CodeValue"),
            scalarSourcePathPropertyName: "codeValue",
            scalarKind: ScalarKind.String
        );

        return new RelationalResourceModel(
            Resource: resource,
            PhysicalSchema: new DbSchemaName("dms"),
            StorageKind: ResourceStorageKind.SharedDescriptorTable,
            Root: rootTable,
            TablesInDependencyOrder: [rootTable],
            DocumentReferenceBindings: [],
            DescriptorEdgeSources: []
        );
    }

    private static DbTableModel CreateRootTable(
        DbSchemaName schemaName,
        string tableName,
        DbColumnName scalarColumnName,
        string scalarSourcePathPropertyName,
        ScalarKind scalarKind
    )
    {
        return new DbTableModel(
            Table: new DbTableName(schemaName, tableName),
            JsonScope: new JsonPathExpression("$", []),
            Key: new TableKey(
                ConstraintName: $"PK_{tableName}",
                Columns: [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            Columns:
            [
                new DbColumnModel(
                    ColumnName: new DbColumnName("DocumentId"),
                    Kind: ColumnKind.ParentKeyPart,
                    ScalarType: new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    ColumnName: scalarColumnName,
                    Kind: ColumnKind.Scalar,
                    ScalarType: CreateScalarType(scalarKind),
                    IsNullable: false,
                    SourceJsonPath: new JsonPathExpression(
                        $"$.{scalarSourcePathPropertyName}",
                        [new JsonPathSegment.Property(scalarSourcePathPropertyName)]
                    ),
                    TargetResource: null
                ),
            ],
            Constraints: []
        );
    }

    private static RelationalScalarType CreateScalarType(ScalarKind scalarKind)
    {
        return scalarKind == ScalarKind.String
            ? new RelationalScalarType(scalarKind, MaxLength: 50)
            : new RelationalScalarType(scalarKind);
    }

    private static DbTableModel CreateChildTable(string tableName)
    {
        return new DbTableModel(
            Table: new DbTableName(new DbSchemaName("edfi"), tableName),
            JsonScope: new JsonPathExpression(
                "$.addresses[*]",
                [new JsonPathSegment.Property("addresses"), new JsonPathSegment.AnyArrayElement()]
            ),
            Key: new TableKey(
                ConstraintName: $"PK_{tableName}",
                Columns:
                [
                    new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart),
                    new DbKeyColumn(new DbColumnName("Ordinal"), ColumnKind.Ordinal),
                ]
            ),
            Columns:
            [
                new DbColumnModel(
                    ColumnName: new DbColumnName("DocumentId"),
                    Kind: ColumnKind.ParentKeyPart,
                    ScalarType: new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("Ordinal"),
                    Kind: ColumnKind.Ordinal,
                    ScalarType: new RelationalScalarType(ScalarKind.Int32),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
            ],
            Constraints: []
        );
    }

    private static byte[] CreateResourceKeySeedHash()
    {
        var resourceKeySeedHash = new byte[32];

        for (var index = 0; index < resourceKeySeedHash.Length; index++)
        {
            resourceKeySeedHash[index] = (byte)index;
        }

        return resourceKeySeedHash;
    }

    private sealed record MappingSetCompilerFixture(
        DerivedRelationalModelSet ModelSet,
        QualifiedResourceName SupportedResource,
        QualifiedResourceName KeyUnificationResource,
        QualifiedResourceName ProjectionMetadataResource,
        QualifiedResourceName NonRootOnlyResource,
        QualifiedResourceName DescriptorResource,
        IReadOnlyList<ResourceKeyEntry> ResourceKeysInIdOrder
    );
}

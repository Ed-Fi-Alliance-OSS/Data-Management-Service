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
    public void It_should_compile_read_and_write_plans_for_all_relational_resources_and_omit_shared_descriptor_resources()
    {
        var fixture = CreateMixedResourceFixture(SqlDialect.Pgsql);
        var compiler = new MappingSetCompiler();
        MappingSet mappingSet = null!;

        var act = () => mappingSet = compiler.Compile(fixture.ModelSet);

        act.Should().NotThrow();

        var relationalResources = fixture
            .RelationalResourceModels.Select(static model => model.Resource)
            .ToArray();
        var readPlanCompiler = new ReadPlanCompiler(fixture.ModelSet.Dialect);

        mappingSet.ReadPlansByResource.Keys.Should().BeEquivalentTo(relationalResources);
        mappingSet.WritePlansByResource.Keys.Should().BeEquivalentTo(relationalResources);
        mappingSet.ReadPlansByResource.Should().NotContainKey(fixture.DescriptorResource);
        mappingSet.WritePlansByResource.Should().NotContainKey(fixture.DescriptorResource);

        foreach (var resourceModel in fixture.RelationalResourceModels)
        {
            mappingSet
                .ReadPlansByResource[resourceModel.Resource]
                .Should()
                .BeEquivalentTo(readPlanCompiler.Compile(resourceModel));
        }

        mappingSet
            .ReadPlansByResource[fixture.ProjectionMetadataResource]
            .ReferenceIdentityProjectionPlansInDependencyOrder.Should()
            .NotBeEmpty();
        mappingSet
            .ReadPlansByResource[fixture.DescriptorEdgeResource]
            .DescriptorProjectionPlansInOrder.Should()
            .NotBeEmpty();

        mappingSet
            .ReadPlansByResource[fixture.NonRootOnlyResource]
            .TablePlansInDependencyOrder.Should()
            .HaveCount(2);
        mappingSet
            .ReadPlansByResource[fixture.ExtensionTableResource]
            .TablePlansInDependencyOrder.Should()
            .HaveCount(2);
    }

    [Test]
    public void It_should_fail_when_concrete_resource_storage_kind_does_not_match_relational_model_storage_kind()
    {
        var fixture = CreateMixedResourceFixture(SqlDialect.Pgsql);
        var concreteResources = fixture.ModelSet.ConcreteResourcesInNameOrder.ToArray();
        var supportedResourceIndex = Array.FindIndex(
            concreteResources,
            concreteResourceModel =>
                concreteResourceModel.RelationalModel.Resource == fixture.SupportedResource
        );

        supportedResourceIndex.Should().NotBe(-1);

        concreteResources[supportedResourceIndex] = concreteResources[supportedResourceIndex] with
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
        var extensionTableResource = new QualifiedResourceName("Ed-Fi", "StudentExtensionCarrier");
        var descriptorEdgeResource = new QualifiedResourceName("Ed-Fi", "StudentDescriptorEdge");
        var abstractResource = new QualifiedResourceName("Ed-Fi", "EducationOrganization");

        var descriptorModel = CreateDescriptorModel(descriptorResource);
        var keyUnificationModel = CreateRootOnlyModelWithKeyUnification(keyUnificationResource, "Program");
        var supportedModel = CreateRootOnlyModel(supportedResource, "Student");
        var projectionMetadataModel = CreateRootOnlyModelWithDocumentReferenceBindings(
            projectionMetadataResource,
            "StudentProjection"
        );
        var nonRootOnlyModel = CreateNonRootOnlyModel(nonRootOnlyResource, "StudentAddress");
        var extensionTableModel = CreateModelWithExtensionTable(
            extensionTableResource,
            "StudentExtensionCarrier"
        );
        var descriptorEdgeModel = CreateRootOnlyModelWithDescriptorEdgeSources(
            descriptorEdgeResource,
            "StudentDescriptorEdge"
        );
        RelationalResourceModel[] relationalResourceModels =
        [
            keyUnificationModel,
            supportedModel,
            projectionMetadataModel,
            nonRootOnlyModel,
            extensionTableModel,
            descriptorEdgeModel,
        ];

        var resourceKeysInIdOrder = new ResourceKeyEntry[]
        {
            new(100, descriptorResource, "5.2.0", false),
            new(101, keyUnificationResource, "5.2.0", false),
            new(102, supportedResource, "5.2.0", false),
            new(103, projectionMetadataResource, "5.2.0", false),
            new(104, nonRootOnlyResource, "5.2.0", false),
            new(105, extensionTableResource, "5.2.0", false),
            new(106, descriptorEdgeResource, "5.2.0", false),
            new(107, abstractResource, "5.2.0", true),
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
                new ConcreteResourceModel(
                    resourceKeysInIdOrder[5],
                    ResourceStorageKind.RelationalTables,
                    extensionTableModel
                ),
                new ConcreteResourceModel(
                    resourceKeysInIdOrder[6],
                    ResourceStorageKind.RelationalTables,
                    descriptorEdgeModel
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
            ExtensionTableResource: extensionTableResource,
            DescriptorEdgeResource: descriptorEdgeResource,
            DescriptorResource: descriptorResource,
            ResourceKeysInIdOrder: resourceKeysInIdOrder,
            RelationalResourceModels: relationalResourceModels
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
        var rootTable = model.Root with
        {
            Columns =
            [
                .. model.Root.Columns,
                new DbColumnModel(
                    ColumnName: new DbColumnName("School_DocumentId"),
                    Kind: ColumnKind.DocumentFk,
                    ScalarType: new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: true,
                    SourceJsonPath: new JsonPathExpression(
                        "$.schoolReference",
                        [new JsonPathSegment.Property("schoolReference")]
                    ),
                    TargetResource: new QualifiedResourceName("Ed-Fi", "School")
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("School_RefSchoolId"),
                    Kind: ColumnKind.Scalar,
                    ScalarType: new RelationalScalarType(ScalarKind.Int32),
                    IsNullable: true,
                    SourceJsonPath: new JsonPathExpression(
                        "$.schoolReference.schoolId",
                        [
                            new JsonPathSegment.Property("schoolReference"),
                            new JsonPathSegment.Property("schoolId"),
                        ]
                    ),
                    TargetResource: null
                ),
            ],
        };
        var binding = new DocumentReferenceBinding(
            IsIdentityComponent: false,
            ReferenceObjectPath: new JsonPathExpression(
                "$.schoolReference",
                [new JsonPathSegment.Property("schoolReference")]
            ),
            Table: rootTable.Table,
            FkColumn: new DbColumnName("School_DocumentId"),
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
                    Column: new DbColumnName("School_RefSchoolId")
                ),
            ]
        );

        return model with
        {
            Root = rootTable,
            TablesInDependencyOrder = [rootTable],
            DocumentReferenceBindings = [binding],
        };
    }

    private static RelationalResourceModel CreateRootOnlyModelWithDescriptorEdgeSources(
        QualifiedResourceName resource,
        string tableName
    )
    {
        var model = CreateRootOnlyModel(resource, tableName);
        var descriptorResource = new QualifiedResourceName("Ed-Fi", "AcademicSubjectDescriptor");
        var rootTable = model.Root with
        {
            Columns =
            [
                .. model.Root.Columns,
                new DbColumnModel(
                    ColumnName: new DbColumnName("AcademicSubjectDescriptorId"),
                    Kind: ColumnKind.DescriptorFk,
                    ScalarType: new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: true,
                    SourceJsonPath: new JsonPathExpression(
                        "$.academicSubjectDescriptor",
                        [new JsonPathSegment.Property("academicSubjectDescriptor")]
                    ),
                    TargetResource: descriptorResource
                ),
            ],
        };
        var descriptorEdgeSource = new DescriptorEdgeSource(
            IsIdentityComponent: false,
            DescriptorValuePath: new JsonPathExpression(
                "$.academicSubjectDescriptor",
                [new JsonPathSegment.Property("academicSubjectDescriptor")]
            ),
            Table: rootTable.Table,
            FkColumn: new DbColumnName("AcademicSubjectDescriptorId"),
            DescriptorResource: descriptorResource
        );

        return model with
        {
            Root = rootTable,
            TablesInDependencyOrder = [rootTable],
            DescriptorEdgeSources = [descriptorEdgeSource],
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

    private static RelationalResourceModel CreateModelWithExtensionTable(
        QualifiedResourceName resource,
        string rootTableName
    )
    {
        var rootTable = CreateRootTable(
            schemaName: new DbSchemaName("edfi"),
            rootTableName,
            scalarColumnName: new DbColumnName("SchoolYear"),
            scalarSourcePathPropertyName: "schoolYear",
            scalarKind: ScalarKind.Int32
        );
        var extensionTable = CreateRootScopeExtensionTable($"{rootTableName}Extension");

        return new RelationalResourceModel(
            Resource: resource,
            PhysicalSchema: new DbSchemaName("edfi"),
            StorageKind: ResourceStorageKind.RelationalTables,
            Root: rootTable,
            TablesInDependencyOrder: [rootTable, extensionTable],
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

    private static DbTableModel CreateRootScopeExtensionTable(string tableName)
    {
        return new DbTableModel(
            Table: new DbTableName(new DbSchemaName("sample"), tableName),
            JsonScope: new JsonPathExpression(
                "$._ext.sample",
                [new JsonPathSegment.Property("_ext"), new JsonPathSegment.Property("sample")]
            ),
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
                    ColumnName: new DbColumnName("FavoriteColor"),
                    Kind: ColumnKind.Scalar,
                    ScalarType: new RelationalScalarType(ScalarKind.String),
                    IsNullable: true,
                    SourceJsonPath: new JsonPathExpression(
                        "$._ext.sample.favoriteColor",
                        [
                            new JsonPathSegment.Property("_ext"),
                            new JsonPathSegment.Property("sample"),
                            new JsonPathSegment.Property("favoriteColor"),
                        ]
                    ),
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
        QualifiedResourceName ExtensionTableResource,
        QualifiedResourceName DescriptorEdgeResource,
        QualifiedResourceName DescriptorResource,
        IReadOnlyList<ResourceKeyEntry> ResourceKeysInIdOrder,
        IReadOnlyList<RelationalResourceModel> RelationalResourceModels
    );
}

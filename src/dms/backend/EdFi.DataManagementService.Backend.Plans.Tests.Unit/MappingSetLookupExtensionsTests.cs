// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

[TestFixture]
public class Given_MappingSetLookupExtensions
{
    private MappingSet _mappingSet = null!;
    private QualifiedResourceName _supportedResource;
    private QualifiedResourceName _descriptorResource;
    private QualifiedResourceName _nonRootOnlyResource;
    private QualifiedResourceName _keyUnificationResource;
    private QualifiedResourceName _projectionMetadataResource;

    [SetUp]
    public void Setup()
    {
        var fixture = CreateFixture();

        _mappingSet = fixture.MappingSet;
        _supportedResource = fixture.SupportedResource;
        _descriptorResource = fixture.DescriptorResource;
        _nonRootOnlyResource = fixture.NonRootOnlyResource;
        _keyUnificationResource = fixture.KeyUnificationResource;
        _projectionMetadataResource = fixture.ProjectionMetadataResource;
    }

    [Test]
    public void It_should_return_write_plan_when_present()
    {
        var expectedPlan = _mappingSet.WritePlansByResource[_supportedResource];
        var actualPlan = _mappingSet.GetWritePlanOrThrow(_supportedResource);

        actualPlan.Should().BeSameAs(expectedPlan);
    }

    [Test]
    public void It_should_return_read_plan_when_present()
    {
        var expectedPlan = _mappingSet.ReadPlansByResource[_supportedResource];
        var actualPlan = _mappingSet.GetReadPlanOrThrow(_supportedResource);

        actualPlan.Should().BeSameAs(expectedPlan);
    }

    [Test]
    public void It_should_throw_actionable_descriptor_message_for_omitted_write_plan()
    {
        var act = () => _mappingSet.GetWritePlanOrThrow(_descriptorResource);

        act.Should()
            .Throw<NotSupportedException>()
            .WithMessage(
                "Write plan for resource 'Ed-Fi.AcademicSubjectDescriptor' was intentionally omitted: "
                    + "storage kind 'SharedDescriptorTable' does not use thin-slice relational-table write plans. "
                    + "Next story: E07-S06 (06-descriptor-writes.md)."
            );
    }

    [Test]
    public void It_should_throw_actionable_descriptor_message_for_omitted_read_plan()
    {
        var act = () => _mappingSet.GetReadPlanOrThrow(_descriptorResource);

        act.Should()
            .Throw<NotSupportedException>()
            .WithMessage(
                "Read plan for resource 'Ed-Fi.AcademicSubjectDescriptor' was intentionally omitted: "
                    + "storage kind 'SharedDescriptorTable' does not use thin-slice relational-table hydration plans. "
                    + "Next story: E08-S05 (05-descriptor-endpoints.md)."
            );
    }

    [Test]
    public void It_should_treat_missing_non_root_only_relational_write_plan_as_internal_bug()
    {
        var act = () => _mappingSet.GetWritePlanOrThrow(_nonRootOnlyResource);

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                "*Ed-Fi.StudentAddress*mapping set*RelationalTables*internal compilation/selection bug*"
            );
    }

    [Test]
    public void It_should_throw_actionable_non_root_only_message_for_omitted_read_plan()
    {
        var act = () => _mappingSet.GetReadPlanOrThrow(_nonRootOnlyResource);

        act.Should()
            .Throw<NotSupportedException>()
            .WithMessage("*TablesInDependencyOrder.Count == 1*actual 2*E15-S05*");
    }

    [Test]
    public void It_should_throw_actionable_projection_metadata_message_for_omitted_read_plan()
    {
        var act = () => _mappingSet.GetReadPlanOrThrow(_projectionMetadataResource);

        act.Should()
            .Throw<NotSupportedException>()
            .WithMessage(
                "*requires reference-identity projection metadata*DocumentReferenceBindings count: 1*E15-S06*"
            );
    }

    [Test]
    public void It_should_treat_missing_key_unification_relational_write_plan_as_internal_bug()
    {
        var act = () => _mappingSet.GetWritePlanOrThrow(_keyUnificationResource);

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*Ed-Fi.Program*mapping set*RelationalTables*internal compilation/selection bug*");
    }

    [Test]
    public void It_should_throw_deterministic_error_for_unknown_resource_write_lookup()
    {
        var unknownResource = new QualifiedResourceName("Ed-Fi", "UnknownResource");
        var act = () => _mappingSet.GetWritePlanOrThrow(unknownResource);

        act.Should()
            .Throw<KeyNotFoundException>()
            .WithMessage("*does not contain resource*Ed-Fi.UnknownResource*");
    }

    [Test]
    public void It_should_throw_deterministic_error_for_unknown_resource_read_lookup()
    {
        var unknownResource = new QualifiedResourceName("Ed-Fi", "UnknownResource");
        var act = () => _mappingSet.GetReadPlanOrThrow(unknownResource);

        act.Should()
            .Throw<KeyNotFoundException>()
            .WithMessage("*does not contain resource*Ed-Fi.UnknownResource*");
    }

    private static MappingSetLookupFixture CreateFixture()
    {
        var descriptorResource = new QualifiedResourceName("Ed-Fi", "AcademicSubjectDescriptor");
        var keyUnificationResource = new QualifiedResourceName("Ed-Fi", "Program");
        var supportedResource = new QualifiedResourceName("Ed-Fi", "Student");
        var projectionMetadataResource = new QualifiedResourceName("Ed-Fi", "StudentProjection");
        var nonRootOnlyResource = new QualifiedResourceName("Ed-Fi", "StudentAddress");

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
        };

        var effectiveSchemaInfo = new EffectiveSchemaInfo(
            ApiSchemaFormatVersion: "5.2",
            RelationalMappingVersion: "v1",
            EffectiveSchemaHash: new string('f', 64),
            ResourceKeyCount: resourceKeysInIdOrder.Length,
            ResourceKeySeedHash: CreateResourceKeySeedHash(),
            SchemaComponentsInEndpointOrder:
            [
                new SchemaComponentInfo(
                    ProjectEndpointName: "ed-fi",
                    ProjectName: "Ed-Fi",
                    ProjectVersion: "5.2.0",
                    IsExtensionProject: false,
                    ProjectHash: new string('e', 64)
                ),
            ],
            ResourceKeysInIdOrder: resourceKeysInIdOrder
        );

        var modelSet = new DerivedRelationalModelSet(
            EffectiveSchema: effectiveSchemaInfo,
            Dialect: SqlDialect.Pgsql,
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

        var writePlan = CreateWritePlan(supportedModel);
        var projectionMetadataWritePlan = CreateWritePlan(projectionMetadataModel);
        var supportedReadPlan = CreateReadPlan(supportedModel);
        var keyUnificationReadPlan = CreateReadPlan(keyUnificationModel);

        var resourceKeyIdByResource = resourceKeysInIdOrder.ToDictionary(
            static keyEntry => keyEntry.Resource,
            static keyEntry => keyEntry.ResourceKeyId
        );
        var resourceKeyById = resourceKeysInIdOrder.ToDictionary(
            static keyEntry => keyEntry.ResourceKeyId,
            static keyEntry => keyEntry
        );

        return new MappingSetLookupFixture(
            MappingSet: new MappingSet(
                Key: new MappingSetKey(
                    EffectiveSchemaHash: effectiveSchemaInfo.EffectiveSchemaHash,
                    Dialect: modelSet.Dialect,
                    RelationalMappingVersion: effectiveSchemaInfo.RelationalMappingVersion
                ),
                Model: modelSet,
                WritePlansByResource: new Dictionary<QualifiedResourceName, ResourceWritePlan>
                {
                    [supportedResource] = writePlan,
                    [projectionMetadataResource] = projectionMetadataWritePlan,
                },
                ReadPlansByResource: new Dictionary<QualifiedResourceName, ResourceReadPlan>
                {
                    [supportedResource] = supportedReadPlan,
                    [keyUnificationResource] = keyUnificationReadPlan,
                },
                ResourceKeyIdByResource: resourceKeyIdByResource,
                ResourceKeyById: resourceKeyById
            ),
            SupportedResource: supportedResource,
            DescriptorResource: descriptorResource,
            NonRootOnlyResource: nonRootOnlyResource,
            KeyUnificationResource: keyUnificationResource,
            ProjectionMetadataResource: projectionMetadataResource
        );
    }

    private static ResourceWritePlan CreateWritePlan(RelationalResourceModel model)
    {
        var rootTable = model.Root;

        var tablePlan = new TableWritePlan(
            TableModel: rootTable,
            InsertSql: "INSERT;",
            UpdateSql: null,
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(
                MaxRowsPerBatch: 1000,
                ParametersPerRow: 1,
                MaxParametersPerCommand: 65535
            ),
            ColumnBindings:
            [
                new WriteColumnBinding(
                    Column: rootTable.Columns[0],
                    Source: new WriteValueSource.DocumentId(),
                    ParameterName: "documentId"
                ),
            ],
            KeyUnificationPlans: []
        );

        return new ResourceWritePlan(model, [tablePlan]);
    }

    private static ResourceReadPlan CreateReadPlan(RelationalResourceModel model)
    {
        return new ResourceReadPlan(
            Model: model,
            KeysetTable: KeysetTableConventions.GetKeysetTableContract(SqlDialect.Pgsql),
            TablePlansInDependencyOrder: [new TableReadPlan(model.Root, "SELECT;\n")],
            ReferenceIdentityProjectionPlansInDependencyOrder: [],
            DescriptorProjectionPlansInOrder: []
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
        var model = CreateRootOnlyModel(resource, tableName);
        var rootTableWithKeyUnification = model.Root with
        {
            KeyUnificationClasses =
            [
                new KeyUnificationClass(
                    CanonicalColumn: new DbColumnName("SchoolYear"),
                    MemberPathColumns: [new DbColumnName("SchoolYear")]
                ),
            ],
        };

        return model with
        {
            Root = rootTableWithKeyUnification,
            TablesInDependencyOrder = [rootTableWithKeyUnification],
        };
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

    private sealed record MappingSetLookupFixture(
        MappingSet MappingSet,
        QualifiedResourceName SupportedResource,
        QualifiedResourceName DescriptorResource,
        QualifiedResourceName NonRootOnlyResource,
        QualifiedResourceName KeyUnificationResource,
        QualifiedResourceName ProjectionMetadataResource
    );
}

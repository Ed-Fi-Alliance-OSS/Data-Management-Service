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
public class Given_MappingSetLookupExtensions
{
    private MappingSet _mappingSet = null!;
    private QualifiedResourceName _supportedResource;
    private QualifiedResourceName _descriptorResource;
    private QualifiedResourceName _descriptorEdgeResource;
    private QualifiedResourceName _multiDescriptorProjectionResource;
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
        _descriptorEdgeResource = fixture.DescriptorEdgeResource;
        _multiDescriptorProjectionResource = fixture.MultiDescriptorProjectionResource;
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
    public void It_should_return_read_plan_when_reference_identity_projection_metadata_was_compiled()
    {
        var actualPlan = _mappingSet.GetReadPlanOrThrow(_projectionMetadataResource);

        actualPlan.ReferenceIdentityProjectionPlansInDependencyOrder.Should().NotBeEmpty();
    }

    [Test]
    public void It_should_return_read_plan_when_descriptor_projection_metadata_was_compiled()
    {
        var actualPlan = _mappingSet.GetReadPlanOrThrow(_descriptorEdgeResource);

        actualPlan.DescriptorProjectionPlansInOrder.Should().NotBeEmpty();
    }

    [Test]
    public void It_should_return_read_plan_when_multiple_descriptor_projection_plans_are_present()
    {
        var actualPlan = _mappingSet.GetReadPlanOrThrow(_multiDescriptorProjectionResource);

        actualPlan
            .DescriptorProjectionPlansInOrder.Select(static plan => plan.SelectByKeysetSql)
            .Should()
            .Equal("SELECT descriptor_plan_0;\n", "SELECT descriptor_plan_1;\n");
        actualPlan
            .DescriptorProjectionPlansInOrder.SelectMany(static plan => plan.SourcesInOrder)
            .Select(static source => source.DescriptorValuePath.Canonical)
            .Should()
            .Equal("$.academicSubjectDescriptor", "$.gradeLevelDescriptor");
    }

    [Test]
    public void It_should_throw_actionable_descriptor_message_for_omitted_write_plan()
    {
        var act = () => _mappingSet.GetWritePlanOrThrow(_descriptorResource);

        act.Should()
            .Throw<NotSupportedException>()
            .WithMessage(
                "Write plan for resource 'Ed-Fi.AcademicSubjectDescriptor' was intentionally omitted: "
                    + "storage kind 'SharedDescriptorTable' uses the descriptor write path instead of compiled relational-table write plans. "
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
                    + "storage kind 'SharedDescriptorTable' uses the descriptor read path instead of compiled relational-table hydration plans. "
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
    public void It_should_treat_missing_non_root_only_relational_read_plan_as_internal_bug()
    {
        var act = () => _mappingSet.GetReadPlanOrThrow(_nonRootOnlyResource);

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                "*Ed-Fi.StudentAddress*mapping set*RelationalTables*compiled relational-table read plan*internal compilation/selection bug*"
            );
    }

    [Test]
    public void It_should_treat_missing_reference_identity_projection_metadata_on_a_compiled_read_plan_as_internal_bug()
    {
        var mappingSet = ReplaceReadPlan(
            _mappingSet,
            _projectionMetadataResource,
            CreateHydrationOnlyReadPlan(_mappingSet.ReadPlansByResource[_projectionMetadataResource].Model)
        );
        var act = () => mappingSet.GetReadPlanOrThrow(_projectionMetadataResource);

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                "*Ed-Fi.StudentProjection*mapping set*compiled relational-table read plan has invalid projection metadata*DocumentReferenceBindings are present while ReferenceIdentityProjectionPlansInDependencyOrder is empty*internal compilation/selection bug*"
            );
    }

    [Test]
    public void It_should_treat_missing_descriptor_projection_metadata_on_a_compiled_read_plan_as_internal_bug()
    {
        var mappingSet = ReplaceReadPlan(
            _mappingSet,
            _descriptorEdgeResource,
            CreateHydrationOnlyReadPlan(_mappingSet.ReadPlansByResource[_descriptorEdgeResource].Model)
        );
        var act = () => mappingSet.GetReadPlanOrThrow(_descriptorEdgeResource);

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                "*Ed-Fi.StudentDescriptorEdge*mapping set*compiled relational-table read plan has invalid projection metadata*DescriptorEdgeSources are present while DescriptorProjectionPlansInOrder is empty*internal compilation/selection bug*"
            );
    }

    [Test]
    public void It_should_treat_reference_identity_projection_tables_not_present_in_hydration_plans_as_internal_bug()
    {
        var readPlan = _mappingSet.ReadPlansByResource[_projectionMetadataResource];
        var projectionTablePlan = readPlan.ReferenceIdentityProjectionPlansInDependencyOrder.Single();
        var invalidReadPlan = readPlan with
        {
            ReferenceIdentityProjectionPlansInDependencyOrder =
            [
                projectionTablePlan with
                {
                    Table = new DbTableName(new DbSchemaName("edfi"), "MissingProjectionTable"),
                },
            ],
        };
        var mappingSet = ReplaceReadPlan(_mappingSet, _projectionMetadataResource, invalidReadPlan);
        var act = () => mappingSet.GetReadPlanOrThrow(_projectionMetadataResource);

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                "*Ed-Fi.StudentProjection*mapping set*compiled relational-table read plan has invalid projection metadata*reference identity projection table 'edfi.MissingProjectionTable' is not present in compiled table plans*internal compilation/selection bug*"
            );
    }

    [Test]
    public void It_should_treat_descriptor_projection_tables_not_present_in_hydration_plans_as_internal_bug()
    {
        var readPlan = _mappingSet.ReadPlansByResource[_descriptorEdgeResource];
        var descriptorPlan = readPlan.DescriptorProjectionPlansInOrder.Single();
        var descriptorSources = descriptorPlan.SourcesInOrder.ToArray();

        descriptorSources[0] = descriptorSources[0] with
        {
            Table = new DbTableName(new DbSchemaName("edfi"), "MissingProjectionTable"),
        };

        var invalidReadPlan = readPlan with
        {
            DescriptorProjectionPlansInOrder =
            [
                descriptorPlan with
                {
                    SourcesInOrder = [.. descriptorSources],
                },
            ],
        };
        var mappingSet = ReplaceReadPlan(_mappingSet, _descriptorEdgeResource, invalidReadPlan);
        var act = () => mappingSet.GetReadPlanOrThrow(_descriptorEdgeResource);

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                "*Ed-Fi.StudentDescriptorEdge*mapping set*compiled relational-table read plan has invalid projection metadata*descriptor projection plan at index '0' source '$.academicSubjectDescriptor' references table 'edfi.MissingProjectionTable' that is not present in compiled table plans*internal compilation/selection bug*"
            );
    }

    [Test]
    public void It_should_treat_invalid_descriptor_projection_result_shape_as_internal_bug()
    {
        var readPlan = _mappingSet.ReadPlansByResource[_descriptorEdgeResource];
        var descriptorPlan = readPlan.DescriptorProjectionPlansInOrder.Single();
        var invalidReadPlan = readPlan with
        {
            DescriptorProjectionPlansInOrder =
            [
                descriptorPlan with
                {
                    ResultShape = new DescriptorProjectionResultShape(DescriptorIdOrdinal: 1, UriOrdinal: 0),
                },
            ],
        };
        var mappingSet = ReplaceReadPlan(_mappingSet, _descriptorEdgeResource, invalidReadPlan);
        var act = () => mappingSet.GetReadPlanOrThrow(_descriptorEdgeResource);

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                "*Ed-Fi.StudentDescriptorEdge*mapping set*compiled relational-table read plan has invalid projection metadata*descriptor projection plan at index '0' result shape must expose DescriptorId at ordinal '0' and Uri at ordinal '1'*DescriptorId='1'*Uri='0'*internal compilation/selection bug*"
            );
    }

    [Test]
    public void It_should_treat_invalid_later_descriptor_projection_plan_as_internal_bug()
    {
        var readPlan = _mappingSet.ReadPlansByResource[_multiDescriptorProjectionResource];
        var invalidReadPlan = readPlan with
        {
            DescriptorProjectionPlansInOrder =
            [
                readPlan.DescriptorProjectionPlansInOrder[0],
                readPlan.DescriptorProjectionPlansInOrder[1] with
                {
                    ResultShape = new DescriptorProjectionResultShape(DescriptorIdOrdinal: 1, UriOrdinal: 0),
                },
            ],
        };
        var mappingSet = ReplaceReadPlan(_mappingSet, _multiDescriptorProjectionResource, invalidReadPlan);
        var act = () => mappingSet.GetReadPlanOrThrow(_multiDescriptorProjectionResource);

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                "*Ed-Fi.StudentDescriptorCollection*mapping set*compiled relational-table read plan has invalid projection metadata*descriptor projection plan at index '1' result shape must expose DescriptorId at ordinal '0' and Uri at ordinal '1'*DescriptorId='1'*Uri='0'*internal compilation/selection bug*"
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

    [Test]
    public void It_should_throw_deterministic_error_for_duplicate_concrete_resources()
    {
        var duplicateResource = _mappingSet.Model.ConcreteResourcesInNameOrder[2];
        var mappingSetWithDuplicateResource = _mappingSet with
        {
            Model = _mappingSet.Model with
            {
                ConcreteResourcesInNameOrder =
                [
                    .. _mappingSet.Model.ConcreteResourcesInNameOrder,
                    duplicateResource,
                ],
            },
        };

        var act = () => mappingSetWithDuplicateResource.GetWritePlanOrThrow(_descriptorResource);

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                $"Mapping set '{FormatMappingSetKey(mappingSetWithDuplicateResource.Key)}' contains duplicate resource "
                    + $"'{FormatResource(duplicateResource.RelationalModel.Resource)}' in ConcreteResourcesInNameOrder."
            );
    }

    [Test]
    public void It_should_match_legacy_scan_behavior_for_omitted_write_plan_lookup()
    {
        var unknownResource = new QualifiedResourceName("Ed-Fi", "UnknownResource");

        QualifiedResourceName[] resourcesToCompare =
        [
            _descriptorResource,
            _nonRootOnlyResource,
            _keyUnificationResource,
            unknownResource,
        ];

        foreach (var resource in resourcesToCompare)
        {
            var expectedException = CaptureException(() =>
                GetWritePlanOrThrowUsingLegacyConcreteResourceScan(_mappingSet, resource)
            );
            var actualException = CaptureException(() => _mappingSet.GetWritePlanOrThrow(resource));

            actualException.GetType().Should().Be(expectedException.GetType());
            actualException.Message.Should().Be(expectedException.Message);
        }
    }

    private static MappingSetLookupFixture CreateFixture()
    {
        var descriptorResource = new QualifiedResourceName("Ed-Fi", "AcademicSubjectDescriptor");
        var keyUnificationResource = new QualifiedResourceName("Ed-Fi", "Program");
        var supportedResource = new QualifiedResourceName("Ed-Fi", "Student");
        var projectionMetadataResource = new QualifiedResourceName("Ed-Fi", "StudentProjection");
        var descriptorEdgeResource = new QualifiedResourceName("Ed-Fi", "StudentDescriptorEdge");
        var multiDescriptorProjectionResource = new QualifiedResourceName(
            "Ed-Fi",
            "StudentDescriptorCollection"
        );
        var nonRootOnlyResource = new QualifiedResourceName("Ed-Fi", "StudentAddress");

        var descriptorModel = CreateDescriptorModel(descriptorResource);
        var keyUnificationModel = CreateRootOnlyModelWithKeyUnification(keyUnificationResource, "Program");
        var supportedModel = CreateRootOnlyModel(supportedResource, "Student");
        var projectionMetadataModel = CreateRootOnlyModelWithDocumentReferenceBindings(
            projectionMetadataResource,
            "StudentProjection"
        );
        var descriptorEdgeModel = CreateRootOnlyModelWithDescriptorEdgeSources(
            descriptorEdgeResource,
            "StudentDescriptorEdge"
        );
        var multiDescriptorProjectionModel = CreateRootOnlyModelWithMultipleDescriptorEdgeSources(
            multiDescriptorProjectionResource,
            "StudentDescriptorCollection"
        );
        var nonRootOnlyModel = CreateNonRootOnlyModel(nonRootOnlyResource, "StudentAddress");

        var resourceKeysInIdOrder = new ResourceKeyEntry[]
        {
            new(100, descriptorResource, "5.2.0", false),
            new(101, keyUnificationResource, "5.2.0", false),
            new(102, supportedResource, "5.2.0", false),
            new(103, projectionMetadataResource, "5.2.0", false),
            new(104, descriptorEdgeResource, "5.2.0", false),
            new(105, multiDescriptorProjectionResource, "5.2.0", false),
            new(106, nonRootOnlyResource, "5.2.0", false),
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
                    descriptorEdgeModel
                ),
                new ConcreteResourceModel(
                    resourceKeysInIdOrder[5],
                    ResourceStorageKind.RelationalTables,
                    multiDescriptorProjectionModel
                ),
                new ConcreteResourceModel(
                    resourceKeysInIdOrder[6],
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
        var projectionMetadataReadPlan = CreateReadPlan(projectionMetadataModel);
        var descriptorEdgeReadPlan = CreateReadPlan(descriptorEdgeModel);
        var multiDescriptorProjectionReadPlan = CreateSplitDescriptorProjectionReadPlan(
            multiDescriptorProjectionModel
        );

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
                    [projectionMetadataResource] = projectionMetadataReadPlan,
                    [descriptorEdgeResource] = descriptorEdgeReadPlan,
                    [multiDescriptorProjectionResource] = multiDescriptorProjectionReadPlan,
                },
                ResourceKeyIdByResource: resourceKeyIdByResource,
                ResourceKeyById: resourceKeyById
            ),
            SupportedResource: supportedResource,
            DescriptorResource: descriptorResource,
            DescriptorEdgeResource: descriptorEdgeResource,
            MultiDescriptorProjectionResource: multiDescriptorProjectionResource,
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
        return new ReadPlanCompiler(SqlDialect.Pgsql).Compile(model);
    }

    private static ResourceReadPlan CreateHydrationOnlyReadPlan(RelationalResourceModel model)
    {
        return new ResourceReadPlan(
            Model: model,
            KeysetTable: KeysetTableConventions.GetKeysetTableContract(SqlDialect.Pgsql),
            TablePlansInDependencyOrder: [new TableReadPlan(model.Root, "SELECT;\n")],
            ReferenceIdentityProjectionPlansInDependencyOrder: [],
            DescriptorProjectionPlansInOrder: []
        );
    }

    private static ResourceReadPlan CreateSplitDescriptorProjectionReadPlan(RelationalResourceModel model)
    {
        var compiledReadPlan = CreateReadPlan(model);
        var descriptorSources = compiledReadPlan
            .DescriptorProjectionPlansInOrder.SelectMany(static plan => plan.SourcesInOrder)
            .ToArray();

        return compiledReadPlan with
        {
            DescriptorProjectionPlansInOrder =
            [
                new DescriptorProjectionPlan(
                    SelectByKeysetSql: "SELECT descriptor_plan_0;\n",
                    ResultShape: new DescriptorProjectionResultShape(DescriptorIdOrdinal: 0, UriOrdinal: 1),
                    SourcesInOrder: [descriptorSources[0]]
                ),
                new DescriptorProjectionPlan(
                    SelectByKeysetSql: "SELECT descriptor_plan_1;\n",
                    ResultShape: new DescriptorProjectionResultShape(DescriptorIdOrdinal: 0, UriOrdinal: 1),
                    SourcesInOrder: [descriptorSources[1]]
                ),
            ],
        };
    }

    private static MappingSet ReplaceReadPlan(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        ResourceReadPlan readPlan
    )
    {
        var readPlansByResource = mappingSet.ReadPlansByResource.ToDictionary(
            entry => entry.Key,
            entry => entry.Value
        );
        readPlansByResource[resource] = readPlan;

        return mappingSet with
        {
            ReadPlansByResource = readPlansByResource.ToFrozenDictionary(),
        };
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

    private static RelationalResourceModel CreateRootOnlyModelWithMultipleDescriptorEdgeSources(
        QualifiedResourceName resource,
        string tableName
    )
    {
        var model = CreateRootOnlyModel(resource, tableName);
        var academicSubjectDescriptor = new QualifiedResourceName("Ed-Fi", "AcademicSubjectDescriptor");
        var gradeLevelDescriptor = new QualifiedResourceName("Ed-Fi", "GradeLevelDescriptor");
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
                    TargetResource: academicSubjectDescriptor
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("GradeLevelDescriptorId"),
                    Kind: ColumnKind.DescriptorFk,
                    ScalarType: new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: true,
                    SourceJsonPath: new JsonPathExpression(
                        "$.gradeLevelDescriptor",
                        [new JsonPathSegment.Property("gradeLevelDescriptor")]
                    ),
                    TargetResource: gradeLevelDescriptor
                ),
            ],
        };

        return model with
        {
            Root = rootTable,
            TablesInDependencyOrder = [rootTable],
            DescriptorEdgeSources =
            [
                new DescriptorEdgeSource(
                    IsIdentityComponent: false,
                    DescriptorValuePath: new JsonPathExpression(
                        "$.academicSubjectDescriptor",
                        [new JsonPathSegment.Property("academicSubjectDescriptor")]
                    ),
                    Table: rootTable.Table,
                    FkColumn: new DbColumnName("AcademicSubjectDescriptorId"),
                    DescriptorResource: academicSubjectDescriptor
                ),
                new DescriptorEdgeSource(
                    IsIdentityComponent: false,
                    DescriptorValuePath: new JsonPathExpression(
                        "$.gradeLevelDescriptor",
                        [new JsonPathSegment.Property("gradeLevelDescriptor")]
                    ),
                    Table: rootTable.Table,
                    FkColumn: new DbColumnName("GradeLevelDescriptorId"),
                    DescriptorResource: gradeLevelDescriptor
                ),
            ],
        };
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

    private static void GetWritePlanOrThrowUsingLegacyConcreteResourceScan(
        MappingSet mappingSet,
        QualifiedResourceName resource
    )
    {
        if (mappingSet.WritePlansByResource.TryGetValue(resource, out var writePlan))
        {
            _ = writePlan;
            return;
        }

        var concreteResourceModel = GetConcreteResourceModelOrThrowByLegacyScan(mappingSet, resource);

        if (concreteResourceModel.StorageKind == ResourceStorageKind.SharedDescriptorTable)
        {
            throw new NotSupportedException(
                $"Write plan for resource '{FormatResource(resource)}' was intentionally omitted: "
                    + $"storage kind '{ResourceStorageKind.SharedDescriptorTable}' uses the descriptor write path instead of compiled relational-table write plans. "
                    + "Next story: E07-S06 (06-descriptor-writes.md)."
            );
        }

        if (concreteResourceModel.StorageKind == ResourceStorageKind.RelationalTables)
        {
            throw new InvalidOperationException(
                $"Write plan lookup failed for resource '{FormatResource(resource)}' in mapping set "
                    + $"'{FormatMappingSetKey(mappingSet.Key)}': resource storage kind "
                    + $"'{ResourceStorageKind.RelationalTables}' should always have a compiled relational-table write plan, but no entry "
                    + "was found. This indicates an internal compilation/selection bug."
            );
        }

        throw new InvalidOperationException(
            $"Write plan lookup failed for resource '{FormatResource(resource)}' in mapping set "
                + $"'{FormatMappingSetKey(mappingSet.Key)}': storage kind '{concreteResourceModel.StorageKind}' "
                + "is not recognized."
        );
    }

    private static ConcreteResourceModel GetConcreteResourceModelOrThrowByLegacyScan(
        MappingSet mappingSet,
        QualifiedResourceName resource
    )
    {
        foreach (var concreteResourceModel in mappingSet.Model.ConcreteResourcesInNameOrder)
        {
            if (concreteResourceModel.RelationalModel.Resource.Equals(resource))
            {
                return concreteResourceModel;
            }
        }

        throw new KeyNotFoundException(
            $"Mapping set '{FormatMappingSetKey(mappingSet.Key)}' does not contain resource '{FormatResource(resource)}' in ConcreteResourcesInNameOrder."
        );
    }

    private static Exception CaptureException(Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            return exception;
        }

        throw new AssertionException("Expected the action to throw, but it completed successfully.");
    }

    private static string FormatResource(QualifiedResourceName resource)
    {
        return $"{resource.ProjectName}.{resource.ResourceName}";
    }

    private static string FormatMappingSetKey(MappingSetKey key)
    {
        return $"{key.EffectiveSchemaHash}/{key.Dialect}/{key.RelationalMappingVersion}";
    }

    private sealed record MappingSetLookupFixture(
        MappingSet MappingSet,
        QualifiedResourceName SupportedResource,
        QualifiedResourceName DescriptorResource,
        QualifiedResourceName DescriptorEdgeResource,
        QualifiedResourceName MultiDescriptorProjectionResource,
        QualifiedResourceName NonRootOnlyResource,
        QualifiedResourceName KeyUnificationResource,
        QualifiedResourceName ProjectionMetadataResource
    );
}

// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
[Parallelizable]
public class Given_RelationalWriteDatabaseFailureResultMapper
{
    private RecordingRelationalWriteExceptionClassifier _writeExceptionClassifier = null!;
    private RecordingRelationalWriteConstraintResolver _writeConstraintResolver = null!;
    private RelationalWriteDatabaseFailureResultMapper _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _writeExceptionClassifier = new RecordingRelationalWriteExceptionClassifier();
        _writeConstraintResolver = new RecordingRelationalWriteConstraintResolver();
        _sut = new RelationalWriteDatabaseFailureResultMapper(
            _writeExceptionClassifier,
            _writeConstraintResolver
        );
    }

    [Test]
    public void It_maps_late_document_reference_foreign_key_violations_to_missing_reference_failures()
    {
        var documentReference = RelationalAccessTestData.CreateDocumentReference(
            new ReferentialId(Guid.Parse("11111111-2222-3333-4444-555555555555")),
            "$.schoolReference"
        );
        var request = CreateRequest(
            RelationalWriteOperationKind.Post,
            documentReferences: [documentReference]
        );
        var exception = new StubDbException("foreign key violation");
        var violation = new RelationalWriteExceptionClassification.ForeignKeyConstraintViolation(
            "FK_School_SchoolReference"
        );
        _writeExceptionClassifier.ClassificationToReturn = violation;
        _writeConstraintResolver.ResolutionToReturn =
            new RelationalWriteConstraintResolution.RequestReference(
                violation.ConstraintName,
                RelationalWriteReferenceKind.Document,
                new JsonPathExpression(
                    "$.schoolReference",
                    [new JsonPathSegment.Property("schoolReference")]
                ),
                new QualifiedResourceName("Ed-Fi", "School")
            );

        var isMapped = _sut.TryBuild(request, exception, out var result);

        isMapped.Should().BeTrue();
        result
            .Should()
            .BeEquivalentTo(
                new RelationalWriteExecutorResult.Upsert(
                    new UpsertResult.UpsertFailureReference(
                        [
                            DocumentReferenceFailure.From(
                                documentReference,
                                DocumentReferenceFailureReason.Missing
                            ),
                        ],
                        []
                    )
                )
            );
        _writeExceptionClassifier.CapturedException.Should().BeSameAs(exception);
        _writeConstraintResolver.CapturedRequest!.Violation.Should().BeSameAs(violation);
        _writeConstraintResolver
            .CapturedRequest!.ReferenceResolutionRequest.Should()
            .BeSameAs(request.ReferenceResolutionRequest);
    }

    [Test]
    public void It_does_not_fabricate_reference_failures_without_a_matching_request_reference()
    {
        var request = CreateRequest(RelationalWriteOperationKind.Put);
        var violation = new RelationalWriteExceptionClassification.ForeignKeyConstraintViolation(
            "FK_School_SchoolReference"
        );
        _writeExceptionClassifier.ClassificationToReturn = violation;
        _writeConstraintResolver.ResolutionToReturn =
            new RelationalWriteConstraintResolution.RequestReference(
                violation.ConstraintName,
                RelationalWriteReferenceKind.Document,
                new JsonPathExpression(
                    "$.schoolReference",
                    [new JsonPathSegment.Property("schoolReference")]
                ),
                new QualifiedResourceName("Ed-Fi", "School")
            );

        var isMapped = _sut.TryBuild(request, new StubDbException("foreign key violation"), out var result);

        isMapped.Should().BeTrue();
        result
            .Should()
            .BeEquivalentTo(
                new RelationalWriteExecutorResult.Update(
                    new UpdateResult.UnknownFailure(
                        "Relational write failed for resource 'Ed-Fi.School' because the database reported a non-user-facing constraint violation."
                    )
                )
            );
    }

    [Test]
    public void It_maps_unrecognized_final_database_write_failures_to_unknown_failures()
    {
        var request = CreateRequest(RelationalWriteOperationKind.Post);
        _writeExceptionClassifier.ClassificationToReturn = RelationalWriteExceptionClassification
            .UnrecognizedWriteFailure
            .Instance;

        var isMapped = _sut.TryBuild(request, new StubDbException("provider write failure"), out var result);

        isMapped.Should().BeTrue();
        result
            .Should()
            .BeEquivalentTo(
                new RelationalWriteExecutorResult.Upsert(
                    new UpsertResult.UnknownFailure(
                        "Relational write failed for resource 'Ed-Fi.School' because the database reported an unrecognized final write failure."
                    )
                )
            );
        _writeConstraintResolver.ResolveCallCount.Should().Be(0);
    }

    [Test]
    public void It_leaves_unclassified_database_exceptions_unmapped()
    {
        var isMapped = _sut.TryBuild(
            CreateRequest(RelationalWriteOperationKind.Put),
            new StubDbException("deadlock"),
            out var result
        );

        isMapped.Should().BeFalse();
        result.Should().BeNull();
        _writeConstraintResolver.ResolveCallCount.Should().Be(0);
    }

    [Test]
    public void It_maps_abstract_education_organization_identity_unique_violations_to_identity_conflicts()
    {
        // Drive the real resolver through the mapper so this covers mapping
        // UX_EducationOrganizationIdentity_NK to the literal 409 identity-conflict result, with the
        // duplicate values taken from the concrete School request body rather than the abstract column.
        var classifier = new RecordingRelationalWriteExceptionClassifier
        {
            ClassificationToReturn = new RelationalWriteExceptionClassification.UniqueConstraintViolation(
                "UX_EducationOrganizationIdentity_NK"
            ),
        };
        var mapper = new RelationalWriteDatabaseFailureResultMapper(
            classifier,
            new RelationalWriteConstraintResolver()
        );
        var request = CreateSchoolAbstractIdentityRequest();

        var isMapped = mapper.TryBuild(request, new StubDbException("unique violation"), out var result);

        isMapped.Should().BeTrue();
        result
            .Should()
            .BeEquivalentTo(
                new RelationalWriteExecutorResult.Upsert(
                    new UpsertResult.UpsertFailureIdentityConflict(
                        new ResourceName("School"),
                        [new KeyValuePair<string, string>("schoolId", "155901")]
                    )
                )
            );
    }

    private static RelationalWriteExecutorRequest CreateRequest(
        RelationalWriteOperationKind operationKind,
        IReadOnlyList<DocumentReference>? documentReferences = null
    )
    {
        var rootWritePlan = Given_Relational_Write_Guarded_No_Op.CreateRootPlan();
        var resourceModel = CreateRelationalResourceModel(rootWritePlan.TableModel);
        var resourceWritePlan = new ResourceWritePlan(resourceModel, [rootWritePlan]);
        var mappingSet = CreateMappingSet(resourceModel, resourceWritePlan);
        var createDocumentUuid = new DocumentUuid(Guid.Parse("cccccccc-1111-2222-3333-dddddddddddd"));
        var updateDocumentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));

        return new RelationalWriteExecutorRequest(
            mappingSet,
            operationKind,
            operationKind == RelationalWriteOperationKind.Put
                ? new RelationalWriteTargetRequest.Put(updateDocumentUuid)
                : new RelationalWriteTargetRequest.Post(
                    new ReferentialId(Guid.Parse("99999999-8888-7777-6666-555555555555")),
                    createDocumentUuid
                ),
            resourceWritePlan,
            existingDocumentReadPlan: null,
            JsonNode.Parse("""{"schoolId":255901,"name":"Lincoln High"}""")!,
            allowIdentityUpdates: false,
            new TraceId("database-failure-result-mapper-test"),
            new ReferenceResolverRequest(
                mappingSet,
                resourceWritePlan.Model.Resource,
                documentReferences ?? [],
                DescriptorReferences: []
            ),
            operationKind == RelationalWriteOperationKind.Put
                ? new RelationalWriteTargetContext.ExistingDocument(345L, updateDocumentUuid, 44L)
                : new RelationalWriteTargetContext.CreateNew(createDocumentUuid)
        );
    }

    private static RelationalResourceModel CreateRelationalResourceModel(DbTableModel rootTable)
    {
        var resource = new QualifiedResourceName("Ed-Fi", "School");

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

    private static MappingSet CreateMappingSet(
        RelationalResourceModel resourceModel,
        ResourceWritePlan resourceWritePlan
    )
    {
        var resource = resourceModel.Resource;
        var resourceKey = new ResourceKeyEntry(1, resource, "1.0.0", false);

        return new MappingSet(
            Key: new MappingSetKey("schema-hash", SqlDialect.Pgsql, "v1"),
            Model: new DerivedRelationalModelSet(
                EffectiveSchema: new EffectiveSchemaInfo(
                    ApiSchemaFormatVersion: "1.0",
                    RelationalMappingVersion: "v1",
                    EffectiveSchemaHash: "schema-hash",
                    ResourceKeyCount: 1,
                    ResourceKeySeedHash: [1, 2, 3],
                    SchemaComponentsInEndpointOrder:
                    [
                        new SchemaComponentInfo("ed-fi", "Ed-Fi", "1.0.0", false, "component-hash"),
                    ],
                    ResourceKeysInIdOrder: [resourceKey]
                ),
                Dialect: SqlDialect.Pgsql,
                ProjectSchemasInEndpointOrder:
                [
                    new ProjectSchemaInfo("ed-fi", "Ed-Fi", "1.0.0", false, new DbSchemaName("edfi")),
                ],
                ConcreteResourcesInNameOrder:
                [
                    new ConcreteResourceModel(
                        resourceKey,
                        ResourceStorageKind.RelationalTables,
                        resourceModel
                    ),
                ],
                AbstractIdentityTablesInNameOrder: [],
                AbstractUnionViewsInNameOrder: [],
                IndexesInCreateOrder: [],
                TriggersInCreateOrder: []
            ),
            WritePlansByResource: new Dictionary<QualifiedResourceName, ResourceWritePlan>
            {
                [resource] = resourceWritePlan,
            },
            ReadPlansByResource: new Dictionary<QualifiedResourceName, ResourceReadPlan>(),
            ResourceKeyIdByResource: new Dictionary<QualifiedResourceName, short>
            {
                [resource] = resourceKey.ResourceKeyId,
            },
            ResourceKeyById: new Dictionary<short, ResourceKeyEntry>
            {
                [resourceKey.ResourceKeyId] = resourceKey,
            },
            SecurableElementColumnPathsByResource: new Dictionary<
                QualifiedResourceName,
                IReadOnlyList<ResolvedSecurableElementPath>
            >()
        );
    }

    // Dedicated, isolated builder for the abstract-identity end-to-end test. Kept separate from
    // CreateRequest/CreateMappingSet so the recording-stub tests stay simple and unchanged.
    private static RelationalWriteExecutorRequest CreateSchoolAbstractIdentityRequest()
    {
        var schoolResource = new QualifiedResourceName("Ed-Fi", "School");

        var schoolRootTable = new DbTableModel(
            new DbTableName(new DbSchemaName("edfi"), "School"),
            new JsonPathExpression("$", []),
            new TableKey(
                "PK_School",
                [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            [
                new DbColumnModel(
                    new DbColumnName("DocumentId"),
                    ColumnKind.ParentKeyPart,
                    null,
                    false,
                    null,
                    null,
                    new ColumnStorage.Stored()
                ),
                new DbColumnModel(
                    new DbColumnName("SchoolId"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Int32),
                    false,
                    new JsonPathExpression("$.schoolId", [new JsonPathSegment.Property("schoolId")]),
                    null,
                    new ColumnStorage.Stored()
                ),
            ],
            []
        );

        // Models edfi."EducationOrganizationIdentity" the way AbstractIdentityTableAndUnionViewDerivationPass
        // builds it: DocumentId primary key, the _NK natural-key unique over the projected identity column,
        // the _RefKey helper that also includes DocumentId, and the document FK.
        var educationOrganizationIdentityTable = new DbTableModel(
            new DbTableName(new DbSchemaName("edfi"), "EducationOrganizationIdentity"),
            new JsonPathExpression("$", []),
            new TableKey(
                "PK_EducationOrganizationIdentity",
                [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            [
                new DbColumnModel(
                    new DbColumnName("DocumentId"),
                    ColumnKind.ParentKeyPart,
                    new RelationalScalarType(ScalarKind.Int64),
                    false,
                    null,
                    null,
                    new ColumnStorage.Stored()
                ),
                new DbColumnModel(
                    new DbColumnName("EducationOrganizationId"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Int32),
                    false,
                    null,
                    null,
                    new ColumnStorage.Stored()
                ),
                new DbColumnModel(
                    new DbColumnName("Discriminator"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, 256),
                    false,
                    null,
                    null,
                    new ColumnStorage.Stored()
                ),
            ],
            [
                new TableConstraint.Unique(
                    "UX_EducationOrganizationIdentity_NK",
                    [new DbColumnName("EducationOrganizationId")]
                ),
                new TableConstraint.Unique(
                    "UX_EducationOrganizationIdentity_RefKey",
                    [new DbColumnName("EducationOrganizationId"), new DbColumnName("DocumentId")]
                ),
                new TableConstraint.ForeignKey(
                    "FK_EducationOrganizationIdentity_Document",
                    [new DbColumnName("DocumentId")],
                    new DbTableName(new DbSchemaName("dms"), "Document"),
                    [new DbColumnName("DocumentId")],
                    OnDelete: ReferentialAction.Cascade
                ),
            ]
        );

        var resourceModel = new RelationalResourceModel(
            schoolResource,
            new DbSchemaName("edfi"),
            ResourceStorageKind.RelationalTables,
            schoolRootTable,
            [schoolRootTable],
            [],
            []
        );

        var resourceWritePlan = new ResourceWritePlan(resourceModel, []);

        var schoolKey = new ResourceKeyEntry(1, schoolResource, "1.0.0", false);
        var educationOrganizationResource = new QualifiedResourceName("Ed-Fi", "EducationOrganization");
        var educationOrganizationKey = new ResourceKeyEntry(2, educationOrganizationResource, "1.0.0", true);

        var schoolReferentialIdentityTrigger = new DbTriggerInfo(
            new DbTriggerName("TR_School_ReferentialIdentity"),
            schoolRootTable.Table,
            [new DbColumnName("DocumentId")],
            [new DbColumnName("SchoolId")],
            new TriggerKindParameters.ReferentialIdentityMaintenance(
                schoolKey.ResourceKeyId,
                schoolResource.ProjectName,
                schoolResource.ResourceName,
                [
                    new IdentityElementMapping(
                        new DbColumnName("SchoolId"),
                        "$.schoolId",
                        new RelationalScalarType(ScalarKind.Int32)
                    ),
                ]
            )
        );

        var mappingSet = new MappingSet(
            new MappingSetKey("schema-hash", SqlDialect.Pgsql, "v1"),
            new DerivedRelationalModelSet(
                new EffectiveSchemaInfo(
                    "1.0",
                    "v1",
                    "schema-hash",
                    2,
                    [1, 2],
                    [new SchemaComponentInfo("ed-fi", "Ed-Fi", "1.0.0", false, "component-hash")],
                    [schoolKey, educationOrganizationKey]
                ),
                SqlDialect.Pgsql,
                [new ProjectSchemaInfo("ed-fi", "Ed-Fi", "1.0.0", false, new DbSchemaName("edfi"))],
                [new ConcreteResourceModel(schoolKey, ResourceStorageKind.RelationalTables, resourceModel)],
                [new AbstractIdentityTableInfo(educationOrganizationKey, educationOrganizationIdentityTable)],
                [],
                [],
                [schoolReferentialIdentityTrigger]
            ),
            new Dictionary<QualifiedResourceName, ResourceWritePlan> { [schoolResource] = resourceWritePlan },
            new Dictionary<QualifiedResourceName, ResourceReadPlan>(),
            new Dictionary<QualifiedResourceName, short>
            {
                [schoolResource] = schoolKey.ResourceKeyId,
                [educationOrganizationResource] = educationOrganizationKey.ResourceKeyId,
            },
            new Dictionary<short, ResourceKeyEntry>
            {
                [schoolKey.ResourceKeyId] = schoolKey,
                [educationOrganizationKey.ResourceKeyId] = educationOrganizationKey,
            },
            new Dictionary<QualifiedResourceName, IReadOnlyList<ResolvedSecurableElementPath>>()
        );

        var createDocumentUuid = new DocumentUuid(Guid.Parse("cccccccc-1111-2222-3333-dddddddddddd"));

        return new RelationalWriteExecutorRequest(
            mappingSet,
            RelationalWriteOperationKind.Post,
            new RelationalWriteTargetRequest.Post(
                new ReferentialId(Guid.Parse("99999999-8888-7777-6666-555555555555")),
                createDocumentUuid
            ),
            resourceWritePlan,
            existingDocumentReadPlan: null,
            JsonNode.Parse("""{"schoolId":155901,"nameOfInstitution":"School Test"}""")!,
            allowIdentityUpdates: false,
            new TraceId("abstract-identity-conflict-test"),
            new ReferenceResolverRequest(
                mappingSet,
                resourceWritePlan.Model.Resource,
                [],
                DescriptorReferences: []
            ),
            new RelationalWriteTargetContext.CreateNew(createDocumentUuid)
        );
    }

    private sealed class RecordingRelationalWriteExceptionClassifier : IRelationalWriteExceptionClassifier
    {
        public DbException? CapturedException { get; private set; }

        public RelationalWriteExceptionClassification? ClassificationToReturn { get; set; }

        public bool TryClassify(
            DbException exception,
            [NotNullWhen(true)] out RelationalWriteExceptionClassification? classification
        )
        {
            CapturedException = exception;
            classification = ClassificationToReturn;
            return classification is not null;
        }

        public bool IsForeignKeyViolation(DbException exception) => false;

        public bool IsUniqueConstraintViolation(DbException exception) => false;

        public bool IsTransientFailure(DbException exception) => false;
    }

    private sealed class RecordingRelationalWriteConstraintResolver : IRelationalWriteConstraintResolver
    {
        public int ResolveCallCount { get; private set; }

        public RelationalWriteConstraintResolutionRequest? CapturedRequest { get; private set; }

        public RelationalWriteConstraintResolution ResolutionToReturn { get; set; } =
            new RelationalWriteConstraintResolution.Unresolved("UNCONFIGURED");

        public RelationalWriteConstraintResolution Resolve(RelationalWriteConstraintResolutionRequest request)
        {
            ResolveCallCount++;
            CapturedRequest = request;
            return ResolutionToReturn;
        }
    }

    private sealed class StubDbException(string message) : DbException(message);
}

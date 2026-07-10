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
    public void It_maps_specific_IfNoneMatch_root_create_races_to_retryable_write_conflicts()
    {
        ConfigureRootNaturalKeyUniqueViolation();
        var request = CreateRequest(
            RelationalWriteOperationKind.Post,
            writePrecondition: new WritePrecondition.IfNoneMatch("\"5-client-tag\"")
        );

        var isMapped = _sut.TryBuild(request, new StubDbException("unique violation"), out var result);

        isMapped.Should().BeTrue();
        result
            .Should()
            .BeEquivalentTo(
                new RelationalWriteExecutorResult.Upsert(new UpsertResult.UpsertFailureWriteConflict())
            );
    }

    [Test]
    public void It_maps_wildcard_IfNoneMatch_root_create_races_to_retryable_write_conflicts()
    {
        ConfigureRootNaturalKeyUniqueViolation();
        var request = CreateRequest(
            RelationalWriteOperationKind.Post,
            writePrecondition: new WritePrecondition.IfNoneMatch("*", IsWildcard: true)
        );

        var isMapped = _sut.TryBuild(request, new StubDbException("unique violation"), out var result);

        isMapped.Should().BeTrue();
        result
            .Should()
            .BeEquivalentTo(
                new RelationalWriteExecutorResult.Upsert(new UpsertResult.UpsertFailureWriteConflict())
            );
    }

    [Test]
    public void It_retains_identity_conflicts_for_unguarded_root_create_races()
    {
        ConfigureRootNaturalKeyUniqueViolation();
        var request = CreateRequest(RelationalWriteOperationKind.Post);

        var isMapped = _sut.TryBuild(request, new StubDbException("unique violation"), out var result);

        isMapped.Should().BeTrue();
        result
            .Should()
            .BeEquivalentTo(
                new RelationalWriteExecutorResult.Upsert(
                    new UpsertResult.UpsertFailureIdentityConflict(
                        new ResourceName("School"),
                        [new KeyValuePair<string, string>("schoolId", "255901")]
                    )
                )
            );
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
        var request = CreateSchoolAbstractIdentityRequest(RelationalWriteOperationKind.Post);

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

    [Test]
    public void It_retains_abstract_identity_conflicts_for_IfNoneMatch_wildcard_creates()
    {
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
        var request = CreateSchoolAbstractIdentityRequest(
            RelationalWriteOperationKind.Post,
            new WritePrecondition.IfNoneMatch("*", IsWildcard: true)
        );

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

    [Test]
    public void It_retains_abstract_identity_conflicts_for_specific_IfNoneMatch_creates()
    {
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
        var request = CreateSchoolAbstractIdentityRequest(
            RelationalWriteOperationKind.Post,
            new WritePrecondition.IfNoneMatch("\"5-client-tag\"")
        );

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

    [Test]
    public void It_maps_abstract_education_organization_identity_unique_violations_on_put_to_identity_conflicts()
    {
        // The same abstract-identity natural-key violation on a PUT must surface the Update identity-conflict
        // result, with the duplicate values still taken from the concrete School request body.
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
        var request = CreateSchoolAbstractIdentityRequest(RelationalWriteOperationKind.Put);

        var isMapped = mapper.TryBuild(request, new StubDbException("unique violation"), out var result);

        isMapped.Should().BeTrue();
        result
            .Should()
            .BeEquivalentTo(
                new RelationalWriteExecutorResult.Update(
                    new UpdateResult.UpdateFailureIdentityConflict(
                        new ResourceName("School"),
                        [new KeyValuePair<string, string>("schoolId", "155901")]
                    )
                )
            );
    }

    [Test]
    public void It_reports_the_writing_subclass_identity_for_abstract_education_organization_identity_conflicts()
    {
        // A non-School EducationOrganization subclass colliding on the shared abstract identity table must
        // report its own identity element (localEducationAgencyId), proving the duplicate values are taken from
        // the concrete resource's referential-identity metadata rather than a hard-coded School identity.
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
        var request = CreateLocalEducationAgencyAbstractIdentityRequest();

        var isMapped = mapper.TryBuild(request, new StubDbException("unique violation"), out var result);

        isMapped.Should().BeTrue();
        result
            .Should()
            .BeEquivalentTo(
                new RelationalWriteExecutorResult.Upsert(
                    new UpsertResult.UpsertFailureIdentityConflict(
                        new ResourceName("LocalEducationAgency"),
                        [new KeyValuePair<string, string>("localEducationAgencyId", "155901")]
                    )
                )
            );
    }

    [Test]
    public void It_resolves_nested_reference_backed_identity_values_for_abstract_identity_conflicts()
    {
        // A reference-backed concrete identity element (a GeneralStudentProgramAssociation member's
        // $.programReference.educationOrganizationId) is addressed by a multi-segment JSONPath. The mapper
        // must read its duplicate value by walking the nested request body, and report it under the trailing
        // path segment (educationOrganizationId), proving the abstract-identity conflict path is not limited
        // to top-level scalar identities.
        var classifier = new RecordingRelationalWriteExceptionClassifier
        {
            ClassificationToReturn = new RelationalWriteExceptionClassification.UniqueConstraintViolation(
                AbstractIdentitySchoolTestData.GeneralStudentProgramAssociationNaturalKeyConstraintName
            ),
        };
        var mapper = new RelationalWriteDatabaseFailureResultMapper(
            classifier,
            new RelationalWriteConstraintResolver()
        );
        var request = CreateReferenceBackedSubclassAbstractIdentityRequest();

        var isMapped = mapper.TryBuild(request, new StubDbException("unique violation"), out var result);

        isMapped.Should().BeTrue();
        result
            .Should()
            .BeEquivalentTo(
                new RelationalWriteExecutorResult.Upsert(
                    new UpsertResult.UpsertFailureIdentityConflict(
                        new ResourceName("StudentProgramAssociation"),
                        [new KeyValuePair<string, string>("educationOrganizationId", "255901001")]
                    )
                )
            );
    }

    private static RelationalWriteExecutorRequest CreateRequest(
        RelationalWriteOperationKind operationKind,
        IReadOnlyList<DocumentReference>? documentReferences = null,
        WritePrecondition? writePrecondition = null
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
                : new RelationalWriteTargetContext.CreateNew(createDocumentUuid),
            writePrecondition: writePrecondition
        );
    }

    private void ConfigureRootNaturalKeyUniqueViolation()
    {
        const string constraintName = "UK_School_NaturalKey";
        _writeExceptionClassifier.ClassificationToReturn =
            new RelationalWriteExceptionClassification.UniqueConstraintViolation(constraintName);
        _writeConstraintResolver.ResolutionToReturn =
            new RelationalWriteConstraintResolution.RootNaturalKeyUnique(constraintName);
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
        var identityColumn = resourceModel.Root.Columns.Single(column =>
            column.SourceJsonPath?.Canonical == "$.schoolId"
        );
        var identityJsonPath =
            identityColumn.SourceJsonPath?.Canonical
            ?? throw new InvalidOperationException("Expected the School identity source path.");

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
                TriggersInCreateOrder:
                [
                    new DbTriggerInfo(
                        new DbTriggerName("TR_School_ReferentialIdentity"),
                        resourceModel.Root.Table,
                        [new DbColumnName("DocumentId")],
                        [identityColumn.ColumnName],
                        new TriggerKindParameters.ReferentialIdentityMaintenance(
                            resourceKey.ResourceKeyId,
                            resource.ProjectName,
                            resource.ResourceName,
                            [
                                new IdentityElementMapping(
                                    identityColumn.ColumnName,
                                    identityJsonPath,
                                    identityColumn.ScalarType
                                        ?? throw new InvalidOperationException(
                                            "Expected the School identity column to have a scalar type."
                                        )
                                ),
                            ]
                        )
                    ),
                ]
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

    // Dedicated, isolated builder for the abstract-identity end-to-end tests, parameterized by operation kind
    // so both the POST and PUT identity-conflict paths are covered. Shares its table shapes with the
    // constraint-resolver tests via AbstractIdentitySchoolTestData.
    private static RelationalWriteExecutorRequest CreateSchoolAbstractIdentityRequest(
        RelationalWriteOperationKind operationKind,
        WritePrecondition? writePrecondition = null
    )
    {
        var (writePlan, mappingSet) = AbstractIdentitySchoolTestData.BuildSchoolWriteModel();
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
            writePlan,
            existingDocumentReadPlan: null,
            JsonNode.Parse("""{"schoolId":155901,"nameOfInstitution":"School Test"}""")!,
            allowIdentityUpdates: false,
            new TraceId("abstract-identity-conflict-test"),
            new ReferenceResolverRequest(mappingSet, writePlan.Model.Resource, [], DescriptorReferences: []),
            operationKind == RelationalWriteOperationKind.Put
                ? new RelationalWriteTargetContext.ExistingDocument(345L, updateDocumentUuid, 44L)
                : new RelationalWriteTargetContext.CreateNew(createDocumentUuid),
            writePrecondition: writePrecondition
        );
    }

    // Builds a POST request for a non-School EducationOrganization subclass (LocalEducationAgency) so the
    // abstract-identity conflict path is exercised against a different concrete identity element.
    private static RelationalWriteExecutorRequest CreateLocalEducationAgencyAbstractIdentityRequest()
    {
        var (writePlan, mappingSet) = AbstractIdentitySchoolTestData.BuildLocalEducationAgencyWriteModel();
        var createDocumentUuid = new DocumentUuid(Guid.Parse("cccccccc-1111-2222-3333-dddddddddddd"));

        return new RelationalWriteExecutorRequest(
            mappingSet,
            RelationalWriteOperationKind.Post,
            new RelationalWriteTargetRequest.Post(
                new ReferentialId(Guid.Parse("99999999-8888-7777-6666-555555555555")),
                createDocumentUuid
            ),
            writePlan,
            existingDocumentReadPlan: null,
            JsonNode.Parse("""{"localEducationAgencyId":155901,"nameOfInstitution":"Grand Bend ISD"}""")!,
            allowIdentityUpdates: false,
            new TraceId("abstract-identity-conflict-lea-test"),
            new ReferenceResolverRequest(mappingSet, writePlan.Model.Resource, [], DescriptorReferences: []),
            new RelationalWriteTargetContext.CreateNew(createDocumentUuid)
        );
    }

    // Builds a POST request for a concrete GeneralStudentProgramAssociation member whose identity element is
    // reference-backed, so the mapper's nested-path identity-value resolution is exercised through the
    // abstract-identity conflict path. The body nests the duplicate value under $.programReference.
    private static RelationalWriteExecutorRequest CreateReferenceBackedSubclassAbstractIdentityRequest()
    {
        var (writePlan, mappingSet) = AbstractIdentitySchoolTestData.BuildReferenceBackedSubclassWriteModel();
        var createDocumentUuid = new DocumentUuid(Guid.Parse("cccccccc-1111-2222-3333-dddddddddddd"));

        return new RelationalWriteExecutorRequest(
            mappingSet,
            RelationalWriteOperationKind.Post,
            new RelationalWriteTargetRequest.Post(
                new ReferentialId(Guid.Parse("99999999-8888-7777-6666-555555555555")),
                createDocumentUuid
            ),
            writePlan,
            existingDocumentReadPlan: null,
            JsonNode.Parse(
                """{"programReference":{"educationOrganizationId":255901001},"beginDate":"2024-08-01"}"""
            )!,
            allowIdentityUpdates: false,
            new TraceId("abstract-identity-conflict-reference-backed-test"),
            new ReferenceResolverRequest(mappingSet, writePlan.Model.Resource, [], DescriptorReferences: []),
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

// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.External.Profile;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Profile;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit.Profile;

[TestFixture]
public class Given_No_Profile_Relational_Post
{
    private RelationalDocumentStoreRepository _sut = null!;
    private IRelationalWriteExecutor _writeExecutor = null!;
    private IRelationalWriteTargetLookupService _targetLookupService = null!;
    private IDescriptorWriteHandler _descriptorWriteHandler = null!;
    private UpsertResult _result = null!;
    private DocumentUuid _documentUuid;
    private RelationalWriteExecutorRequest? _capturedExecutorRequest;

    [SetUp]
    public async Task Setup()
    {
        const string committedEtag = "\"51\"";
        _writeExecutor = A.Fake<IRelationalWriteExecutor>();
        _targetLookupService = A.Fake<IRelationalWriteTargetLookupService>();
        _descriptorWriteHandler = A.Fake<IDescriptorWriteHandler>();

        _documentUuid = new DocumentUuid(Guid.NewGuid());
        var writePlan = AdapterFactoryTestFixtures.BuildRootOnlyPlan();
        var resourceInfo = OrchestrationTestHelpers.CreateResourceInfo();
        var readPlan = OrchestrationTestHelpers.CreateReadPlan(writePlan);
        var mappingSet = OrchestrationTestHelpers.CreateMappingSet(resourceInfo, writePlan, readPlan);
        var requestBody = JsonNode.Parse("""{"schoolId":255901}""")!;

        A.CallTo(() =>
                _targetLookupService.ResolveForPostAsync(
                    A<MappingSet>._,
                    A<QualifiedResourceName>._,
                    A<ReferentialId>._,
                    A<DocumentUuid>._,
                    A<CancellationToken>._
                )
            )
            .Returns(new RelationalWriteTargetLookupResult.CreateNew(_documentUuid));

        A.CallTo(() =>
                _writeExecutor.ExecuteAsync(A<RelationalWriteExecutorRequest>._, A<CancellationToken>._)
            )
            .ReturnsLazily(
                (RelationalWriteExecutorRequest req, CancellationToken _) =>
                {
                    _capturedExecutorRequest = req;
                    return new RelationalWriteExecutorResult.Upsert(
                        new UpsertResult.InsertSuccess(_documentUuid, committedEtag)
                    );
                }
            );

        _sut = new RelationalDocumentStoreRepository(
            NullLogger<RelationalDocumentStoreRepository>.Instance,
            _writeExecutor,
            _targetLookupService,
            _descriptorWriteHandler,
            A.Fake<IDocumentHydrator>(),
            A.Fake<IRelationalReadTargetLookupService>(),
            A.Fake<IRelationalReadMaterializer>(),
            A.Fake<IReadableProfileProjector>()
        );

        var upsertRequest = A.Fake<IRelationalUpsertRequest>();
        A.CallTo(() => upsertRequest.ResourceInfo).Returns(resourceInfo);
        A.CallTo(() => upsertRequest.MappingSet).Returns(mappingSet);
        A.CallTo(() => upsertRequest.DocumentInfo).Returns(OrchestrationTestHelpers.CreateDocumentInfo());
        A.CallTo(() => upsertRequest.DocumentUuid).Returns(_documentUuid);
        A.CallTo(() => upsertRequest.EdfiDoc).Returns(requestBody);
        A.CallTo(() => upsertRequest.TraceId).Returns(new TraceId("no-profile-trace"));
        A.CallTo(() => upsertRequest.BackendProfileWriteContext).Returns(null);

        _result = await _sut.UpsertDocument(upsertRequest);
    }

    [Test]
    public void It_routes_through_the_executor()
    {
        _result.Should().BeEquivalentTo(new UpsertResult.InsertSuccess(_documentUuid, "\"51\""));
        A.CallTo(() =>
                _targetLookupService.ResolveForPostAsync(
                    A<MappingSet>._,
                    A<QualifiedResourceName>._,
                    A<ReferentialId>._,
                    A<DocumentUuid>._,
                    A<CancellationToken>._
                )
            )
            .MustHaveHappenedOnceExactly();
        A.CallTo(() =>
                _writeExecutor.ExecuteAsync(A<RelationalWriteExecutorRequest>._, A<CancellationToken>._)
            )
            .MustHaveHappenedOnceExactly();
    }

    [Test]
    public void It_passes_edfi_doc_as_selected_body()
    {
        _capturedExecutorRequest.Should().NotBeNull();
        _capturedExecutorRequest!.SelectedBody.ToJsonString().Should().Be("""{"schoolId":255901}""");
    }

    [Test]
    public void It_does_not_set_profile_write_context()
    {
        _capturedExecutorRequest.Should().NotBeNull();
        _capturedExecutorRequest!.ProfileWriteContext.Should().BeNull();
    }
}

[TestFixture]
public class Given_No_Profile_Relational_Put
{
    private RelationalDocumentStoreRepository _sut = null!;
    private IRelationalWriteExecutor _writeExecutor = null!;
    private IRelationalWriteTargetLookupService _targetLookupService = null!;
    private IDescriptorWriteHandler _descriptorWriteHandler = null!;
    private UpdateResult _result = null!;
    private DocumentUuid _documentUuid;
    private RelationalWriteExecutorRequest? _capturedExecutorRequest;

    [SetUp]
    public async Task Setup()
    {
        const string committedEtag = "\"52\"";
        _writeExecutor = A.Fake<IRelationalWriteExecutor>();
        _targetLookupService = A.Fake<IRelationalWriteTargetLookupService>();
        _descriptorWriteHandler = A.Fake<IDescriptorWriteHandler>();

        _documentUuid = new DocumentUuid(Guid.NewGuid());
        var writePlan = AdapterFactoryTestFixtures.BuildRootOnlyPlan();
        var resourceInfo = OrchestrationTestHelpers.CreateResourceInfo();
        var readPlan = OrchestrationTestHelpers.CreateReadPlan(writePlan);
        var mappingSet = OrchestrationTestHelpers.CreateMappingSet(resourceInfo, writePlan, readPlan);
        var requestBody = JsonNode.Parse("""{"schoolId":255901}""")!;

        A.CallTo(() =>
                _targetLookupService.ResolveForPutAsync(
                    A<MappingSet>._,
                    A<QualifiedResourceName>._,
                    A<DocumentUuid>._,
                    A<CancellationToken>._
                )
            )
            .Returns(new RelationalWriteTargetLookupResult.ExistingDocument(123L, _documentUuid, 42L));

        A.CallTo(() =>
                _writeExecutor.ExecuteAsync(A<RelationalWriteExecutorRequest>._, A<CancellationToken>._)
            )
            .ReturnsLazily(
                (RelationalWriteExecutorRequest req, CancellationToken _) =>
                {
                    _capturedExecutorRequest = req;
                    return new RelationalWriteExecutorResult.Update(
                        new UpdateResult.UpdateSuccess(_documentUuid, committedEtag)
                    );
                }
            );

        _sut = new RelationalDocumentStoreRepository(
            NullLogger<RelationalDocumentStoreRepository>.Instance,
            _writeExecutor,
            _targetLookupService,
            _descriptorWriteHandler,
            A.Fake<IDocumentHydrator>(),
            A.Fake<IRelationalReadTargetLookupService>(),
            A.Fake<IRelationalReadMaterializer>(),
            A.Fake<IReadableProfileProjector>()
        );

        var updateRequest = A.Fake<IRelationalUpdateRequest>();
        A.CallTo(() => updateRequest.ResourceInfo).Returns(resourceInfo);
        A.CallTo(() => updateRequest.MappingSet).Returns(mappingSet);
        A.CallTo(() => updateRequest.DocumentInfo).Returns(OrchestrationTestHelpers.CreateDocumentInfo());
        A.CallTo(() => updateRequest.DocumentUuid).Returns(_documentUuid);
        A.CallTo(() => updateRequest.EdfiDoc).Returns(requestBody);
        A.CallTo(() => updateRequest.TraceId).Returns(new TraceId("no-profile-put-trace"));
        A.CallTo(() => updateRequest.BackendProfileWriteContext).Returns(null);

        _result = await _sut.UpdateDocumentById(updateRequest);
    }

    [Test]
    public void It_routes_through_the_executor()
    {
        _result.Should().BeEquivalentTo(new UpdateResult.UpdateSuccess(_documentUuid, "\"52\""));
        A.CallTo(() =>
                _targetLookupService.ResolveForPutAsync(
                    A<MappingSet>._,
                    A<QualifiedResourceName>._,
                    A<DocumentUuid>._,
                    A<CancellationToken>._
                )
            )
            .MustHaveHappenedOnceExactly();
        A.CallTo(() =>
                _writeExecutor.ExecuteAsync(A<RelationalWriteExecutorRequest>._, A<CancellationToken>._)
            )
            .MustHaveHappenedOnceExactly();
    }

    [Test]
    public void It_passes_edfi_doc_as_selected_body()
    {
        _capturedExecutorRequest.Should().NotBeNull();
        _capturedExecutorRequest!.SelectedBody.ToJsonString().Should().Be("""{"schoolId":255901}""");
    }

    [Test]
    public void It_does_not_set_profile_write_context()
    {
        _capturedExecutorRequest.Should().NotBeNull();
        _capturedExecutorRequest!.ProfileWriteContext.Should().BeNull();
    }
}

[TestFixture]
public class Given_A_Profiled_Relational_Post
{
    private RelationalDocumentStoreRepository _sut = null!;
    private IRelationalWriteExecutor _writeExecutor = null!;
    private IRelationalWriteTargetLookupService _targetLookupService = null!;
    private IDescriptorWriteHandler _descriptorWriteHandler = null!;
    private RelationalWriteExecutorRequest? _capturedExecutorRequest;

    [SetUp]
    public async Task Setup()
    {
        _writeExecutor = A.Fake<IRelationalWriteExecutor>();
        _targetLookupService = A.Fake<IRelationalWriteTargetLookupService>();
        _descriptorWriteHandler = A.Fake<IDescriptorWriteHandler>();

        var documentUuid = new DocumentUuid(Guid.NewGuid());
        var writePlan = AdapterFactoryTestFixtures.BuildRootOnlyPlan();
        var resourceInfo = OrchestrationTestHelpers.CreateResourceInfo();
        var readPlan = OrchestrationTestHelpers.CreateReadPlan(writePlan);
        var mappingSet = OrchestrationTestHelpers.CreateMappingSet(resourceInfo, writePlan, readPlan);
        var edfiDoc = JsonNode.Parse("""{"schoolId":255901,"nameOfInstitution":"Lincoln High"}""")!;
        var writableRequestBody = JsonNode.Parse("""{"schoolId":255901}""")!;

        A.CallTo(() =>
                _targetLookupService.ResolveForPostAsync(
                    A<MappingSet>._,
                    A<QualifiedResourceName>._,
                    A<ReferentialId>._,
                    A<DocumentUuid>._,
                    A<CancellationToken>._
                )
            )
            .Returns(new RelationalWriteTargetLookupResult.CreateNew(documentUuid));

        A.CallTo(() =>
                _writeExecutor.ExecuteAsync(A<RelationalWriteExecutorRequest>._, A<CancellationToken>._)
            )
            .ReturnsLazily(
                (RelationalWriteExecutorRequest req, CancellationToken _) =>
                {
                    _capturedExecutorRequest = req;
                    return new RelationalWriteExecutorResult.Upsert(
                        new UpsertResult.UnknownFailure(
                            "Profile-aware relational merge/persist pending DMS-1124."
                        )
                    );
                }
            );

        _sut = new RelationalDocumentStoreRepository(
            NullLogger<RelationalDocumentStoreRepository>.Instance,
            _writeExecutor,
            _targetLookupService,
            _descriptorWriteHandler,
            A.Fake<IDocumentHydrator>(),
            A.Fake<IRelationalReadTargetLookupService>(),
            A.Fake<IRelationalReadMaterializer>(),
            A.Fake<IReadableProfileProjector>()
        );

        var upsertRequest = A.Fake<IRelationalUpsertRequest>();
        A.CallTo(() => upsertRequest.ResourceInfo).Returns(resourceInfo);
        A.CallTo(() => upsertRequest.MappingSet).Returns(mappingSet);
        A.CallTo(() => upsertRequest.DocumentInfo).Returns(OrchestrationTestHelpers.CreateDocumentInfo());
        A.CallTo(() => upsertRequest.DocumentUuid).Returns(documentUuid);
        A.CallTo(() => upsertRequest.EdfiDoc).Returns(edfiDoc);
        A.CallTo(() => upsertRequest.TraceId).Returns(new TraceId("profile-post-trace"));
        A.CallTo(() => upsertRequest.BackendProfileWriteContext)
            .Returns(
                OrchestrationTestHelpers.CreateBackendProfileWriteContext(writePlan, writableRequestBody)
            );

        _ = await _sut.UpsertDocument(upsertRequest);
    }

    [Test]
    public void It_routes_through_the_executor()
    {
        A.CallTo(() =>
                _writeExecutor.ExecuteAsync(A<RelationalWriteExecutorRequest>._, A<CancellationToken>._)
            )
            .MustHaveHappenedOnceExactly();
    }

    [Test]
    public void It_passes_writable_request_body_as_selected_body()
    {
        _capturedExecutorRequest.Should().NotBeNull();
        var selectedBody = _capturedExecutorRequest!.SelectedBody;
        selectedBody.ToJsonString().Should().Be("""{"schoolId":255901}""");
        selectedBody
            .AsObject()
            .ContainsKey("nameOfInstitution")
            .Should()
            .BeFalse("hidden members must not leak from the original EdfiDoc into the executor");
    }

    [Test]
    public void It_threads_the_profile_write_context_to_the_executor()
    {
        _capturedExecutorRequest.Should().NotBeNull();
        _capturedExecutorRequest!.ProfileWriteContext.Should().NotBeNull();
        _capturedExecutorRequest.ProfileWriteContext!.ProfileName.Should().Be("test-profile");
    }
}

[TestFixture]
public class Given_A_Profiled_Relational_Put
{
    private RelationalDocumentStoreRepository _sut = null!;
    private IRelationalWriteExecutor _writeExecutor = null!;
    private IRelationalWriteTargetLookupService _targetLookupService = null!;
    private IDescriptorWriteHandler _descriptorWriteHandler = null!;
    private RelationalWriteExecutorRequest? _capturedExecutorRequest;

    [SetUp]
    public async Task Setup()
    {
        _writeExecutor = A.Fake<IRelationalWriteExecutor>();
        _targetLookupService = A.Fake<IRelationalWriteTargetLookupService>();
        _descriptorWriteHandler = A.Fake<IDescriptorWriteHandler>();

        var documentUuid = new DocumentUuid(Guid.NewGuid());
        var writePlan = AdapterFactoryTestFixtures.BuildRootOnlyPlan();
        var resourceInfo = OrchestrationTestHelpers.CreateResourceInfo();
        var readPlan = OrchestrationTestHelpers.CreateReadPlan(writePlan);
        var mappingSet = OrchestrationTestHelpers.CreateMappingSet(resourceInfo, writePlan, readPlan);
        var edfiDoc = JsonNode.Parse("""{"schoolId":255901,"nameOfInstitution":"Lincoln High"}""")!;
        var writableRequestBody = JsonNode.Parse("""{"schoolId":255901}""")!;

        A.CallTo(() =>
                _targetLookupService.ResolveForPutAsync(
                    A<MappingSet>._,
                    A<QualifiedResourceName>._,
                    A<DocumentUuid>._,
                    A<CancellationToken>._
                )
            )
            .Returns(new RelationalWriteTargetLookupResult.ExistingDocument(123L, documentUuid, 42L));

        A.CallTo(() =>
                _writeExecutor.ExecuteAsync(A<RelationalWriteExecutorRequest>._, A<CancellationToken>._)
            )
            .ReturnsLazily(
                (RelationalWriteExecutorRequest req, CancellationToken _) =>
                {
                    _capturedExecutorRequest = req;
                    return new RelationalWriteExecutorResult.Update(
                        new UpdateResult.UnknownFailure(
                            "Profile-aware relational merge/persist pending DMS-1124."
                        )
                    );
                }
            );

        _sut = new RelationalDocumentStoreRepository(
            NullLogger<RelationalDocumentStoreRepository>.Instance,
            _writeExecutor,
            _targetLookupService,
            _descriptorWriteHandler,
            A.Fake<IDocumentHydrator>(),
            A.Fake<IRelationalReadTargetLookupService>(),
            A.Fake<IRelationalReadMaterializer>(),
            A.Fake<IReadableProfileProjector>()
        );

        var updateRequest = A.Fake<IRelationalUpdateRequest>();
        A.CallTo(() => updateRequest.ResourceInfo).Returns(resourceInfo);
        A.CallTo(() => updateRequest.MappingSet).Returns(mappingSet);
        A.CallTo(() => updateRequest.DocumentInfo).Returns(OrchestrationTestHelpers.CreateDocumentInfo());
        A.CallTo(() => updateRequest.DocumentUuid).Returns(documentUuid);
        A.CallTo(() => updateRequest.EdfiDoc).Returns(edfiDoc);
        A.CallTo(() => updateRequest.TraceId).Returns(new TraceId("profile-put-trace"));
        A.CallTo(() => updateRequest.BackendProfileWriteContext)
            .Returns(
                OrchestrationTestHelpers.CreateBackendProfileWriteContext(writePlan, writableRequestBody)
            );

        _ = await _sut.UpdateDocumentById(updateRequest);
    }

    [Test]
    public void It_routes_through_the_executor()
    {
        A.CallTo(() =>
                _writeExecutor.ExecuteAsync(A<RelationalWriteExecutorRequest>._, A<CancellationToken>._)
            )
            .MustHaveHappenedOnceExactly();
    }

    [Test]
    public void It_passes_writable_request_body_as_selected_body()
    {
        _capturedExecutorRequest.Should().NotBeNull();
        var selectedBody = _capturedExecutorRequest!.SelectedBody;
        selectedBody.ToJsonString().Should().Be("""{"schoolId":255901}""");
        selectedBody
            .AsObject()
            .ContainsKey("nameOfInstitution")
            .Should()
            .BeFalse("hidden members must not leak from the original EdfiDoc into the executor");
    }

    [Test]
    public void It_threads_the_profile_write_context_to_the_executor()
    {
        _capturedExecutorRequest.Should().NotBeNull();
        _capturedExecutorRequest!.ProfileWriteContext.Should().NotBeNull();
        _capturedExecutorRequest.ProfileWriteContext!.ProfileName.Should().Be("test-profile");
    }
}

/// <summary>
/// Shared test data builders used by profile write orchestration fixtures.
/// </summary>
internal static class OrchestrationTestHelpers
{
    public static ResourceInfo CreateResourceInfo() =>
        new(
            ProjectName: new ProjectName("Ed-Fi"),
            ResourceName: new ResourceName("School"),
            IsDescriptor: false,
            ResourceVersion: new SemVer("1.0.0"),
            AllowIdentityUpdates: false,
            EducationOrganizationHierarchyInfo: new EducationOrganizationHierarchyInfo(
                false,
                default,
                default
            ),
            AuthorizationSecurableInfo: []
        );

    public static DocumentInfo CreateDocumentInfo() =>
        new(
            DocumentIdentity: new DocumentIdentity([
                new DocumentIdentityElement(new JsonPath("$.schoolId"), "255901"),
            ]),
            ReferentialId: new ReferentialId(Guid.NewGuid()),
            DocumentReferences: [],
            DocumentReferenceArrays: [],
            DescriptorReferences: [],
            SuperclassIdentity: null
        );

    public static ResolvedReferenceSet CreateResolvedReferenceSet() =>
        new(
            SuccessfulDocumentReferencesByPath: new Dictionary<JsonPath, ResolvedDocumentReference>(),
            SuccessfulDescriptorReferencesByPath: new Dictionary<JsonPath, ResolvedDescriptorReference>(),
            LookupsByReferentialId: new Dictionary<ReferentialId, ReferenceLookupSnapshot>(),
            InvalidDocumentReferences: [],
            InvalidDescriptorReferences: [],
            DocumentReferenceOccurrences: [],
            DescriptorReferenceOccurrences: []
        );

    public static BackendProfileWriteContext CreateBackendProfileWriteContext(
        ResourceWritePlan writePlan,
        JsonNode requestBody
    )
    {
        var scopeCatalog = CompiledScopeAdapterFactory.BuildFromWritePlan(writePlan);
        var rootScopeState = new RequestScopeState(
            Address: new ScopeInstanceAddress("$", []),
            Visibility: ProfileVisibilityKind.VisiblePresent,
            Creatable: true
        );

        return new BackendProfileWriteContext(
            Request: new ProfileAppliedWriteRequest(
                WritableRequestBody: requestBody,
                RootResourceCreatable: true,
                RequestScopeStates: [rootScopeState],
                VisibleRequestCollectionItems: []
            ),
            ProfileName: "test-profile",
            CompiledScopeCatalog: scopeCatalog,
            StoredStateProjectionInvoker: A.Fake<IStoredStateProjectionInvoker>()
        );
    }

    public static FlattenedWriteSet CreateFlattenedWriteSet(ResourceWritePlan writePlan)
    {
        var rootTablePlan = writePlan.TablePlansInDependencyOrder.First(p =>
            p.TableModel.IdentityMetadata.TableKind == DbTableKind.Root
        );

        return new FlattenedWriteSet(
            new RootWriteRowBuffer(
                rootTablePlan,
                [
                    FlattenedWriteValue.UnresolvedRootDocumentId.Instance,
                    new FlattenedWriteValue.Literal(255901),
                ]
            )
        );
    }

    public static ResourceReadPlan CreateReadPlan(ResourceWritePlan writePlan)
    {
        var rootTable = writePlan.Model.Root;

        return new ResourceReadPlan(
            writePlan.Model,
            KeysetTableConventions.GetKeysetTableContract(SqlDialect.Pgsql),
            [new TableReadPlan(rootTable, "select 1")],
            [],
            []
        );
    }

    public static MappingSet CreateMappingSet(
        ResourceInfo resourceInfo,
        ResourceWritePlan writePlan,
        ResourceReadPlan? readPlan = null
    )
    {
        var resourceKey = new ResourceKeyEntry(
            ResourceKeyId: 1,
            Resource: new QualifiedResourceName(
                resourceInfo.ProjectName.Value,
                resourceInfo.ResourceName.Value
            ),
            ResourceVersion: resourceInfo.ResourceVersion.Value,
            IsAbstractResource: false
        );

        var resourceModel = writePlan.Model;
        var derivedModelSet = new DerivedRelationalModelSet(
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
                new ConcreteResourceModel(resourceKey, resourceModel.StorageKind, resourceModel),
            ],
            AbstractIdentityTablesInNameOrder: [],
            AbstractUnionViewsInNameOrder: [],
            IndexesInCreateOrder: [],
            TriggersInCreateOrder: []
        );

        return new MappingSet(
            Key: new MappingSetKey("schema-hash", SqlDialect.Pgsql, "v1"),
            Model: derivedModelSet,
            WritePlansByResource: new Dictionary<QualifiedResourceName, ResourceWritePlan>
            {
                [resourceKey.Resource] = writePlan,
            },
            ReadPlansByResource: readPlan is not null
                ? new Dictionary<QualifiedResourceName, ResourceReadPlan>
                {
                    [resourceKey.Resource] = readPlan,
                }
                : new Dictionary<QualifiedResourceName, ResourceReadPlan>(),
            ResourceKeyIdByResource: new Dictionary<QualifiedResourceName, short>
            {
                [resourceKey.Resource] = resourceKey.ResourceKeyId,
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
}

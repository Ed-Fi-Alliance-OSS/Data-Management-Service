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
public class Given_NoProfileWriteBehavior
{
    private RelationalDocumentStoreRepository _sut = null!;
    private IRelationalWriteTargetContextResolver _targetContextResolver = null!;
    private IReferenceResolver _referenceResolver = null!;
    private IRelationalWriteFlattener _writeFlattener = null!;
    private IRelationalWriteTerminalStage _terminalStage = null!;
    private UpsertResult _result = null!;

    [SetUp]
    public async Task Setup()
    {
        _targetContextResolver = A.Fake<IRelationalWriteTargetContextResolver>();
        _referenceResolver = A.Fake<IReferenceResolver>();
        _writeFlattener = A.Fake<IRelationalWriteFlattener>();
        _terminalStage = A.Fake<IRelationalWriteTerminalStage>();

        var documentUuid = new DocumentUuid(Guid.NewGuid());
        var writePlan = AdapterFactoryTestFixtures.BuildRootOnlyPlan();
        var resourceInfo = OrchestrationTestHelpers.CreateResourceInfo();
        var mappingSet = OrchestrationTestHelpers.CreateMappingSet(resourceInfo, writePlan);
        var requestBody = JsonNode.Parse("""{"schoolId":255901}""")!;
        var flattenedWriteSet = CreateFlattenedWriteSet(writePlan);

        A.CallTo(() =>
                _targetContextResolver.ResolveForPostAsync(
                    A<MappingSet>._,
                    A<QualifiedResourceName>._,
                    A<ReferentialId>._,
                    A<DocumentUuid>._,
                    A<CancellationToken>._
                )
            )
            .Returns(
                Task.FromResult<RelationalWriteTargetContext>(
                    new RelationalWriteTargetContext.CreateNew(documentUuid)
                )
            );

        A.CallTo(() => _referenceResolver.ResolveAsync(A<ReferenceResolverRequest>._, A<CancellationToken>._))
            .Returns(Task.FromResult(CreateResolvedReferenceSet()));

        A.CallTo(() => _writeFlattener.Flatten(A<FlatteningInput>._)).Returns(flattenedWriteSet);

        A.CallTo(() =>
                _terminalStage.ExecuteAsync(A<RelationalWriteTerminalStageRequest>._, A<CancellationToken>._)
            )
            .Returns(
                Task.FromResult<RelationalWriteTerminalStageResult>(
                    new RelationalWriteTerminalStageResult.Upsert(
                        new UpsertResult.InsertSuccess(documentUuid)
                    )
                )
            );

        _sut = new RelationalDocumentStoreRepository(
            NullLogger<RelationalDocumentStoreRepository>.Instance,
            _targetContextResolver,
            _referenceResolver,
            _writeFlattener,
            _terminalStage
        );

        var upsertRequest = A.Fake<IRelationalUpsertRequest>();
        A.CallTo(() => upsertRequest.ResourceInfo).Returns(resourceInfo);
        A.CallTo(() => upsertRequest.MappingSet).Returns(mappingSet);
        A.CallTo(() => upsertRequest.DocumentInfo).Returns(OrchestrationTestHelpers.CreateDocumentInfo());
        A.CallTo(() => upsertRequest.DocumentUuid).Returns(documentUuid);
        A.CallTo(() => upsertRequest.EdfiDoc).Returns(requestBody);
        A.CallTo(() => upsertRequest.TraceId).Returns(new TraceId("no-profile-trace"));
        A.CallTo(() => upsertRequest.BackendProfileWriteContext).Returns(null);

        _result = await _sut.UpsertDocument(upsertRequest);
    }

    [Test]
    public void It_returns_insert_success()
    {
        _result.Should().BeOfType<UpsertResult.InsertSuccess>();
    }

    [Test]
    public void It_called_the_flattener()
    {
        A.CallTo(() => _writeFlattener.Flatten(A<FlatteningInput>._)).MustHaveHappenedOnceExactly();
    }

    [Test]
    public void It_called_the_terminal_stage()
    {
        A.CallTo(() =>
                _terminalStage.ExecuteAsync(A<RelationalWriteTerminalStageRequest>._, A<CancellationToken>._)
            )
            .MustHaveHappenedOnceExactly();
    }

    // ── Fixture-specific helpers ──────────────────────────────────────

    private static ResolvedReferenceSet CreateResolvedReferenceSet() =>
        new(
            SuccessfulDocumentReferencesByPath: new Dictionary<JsonPath, ResolvedDocumentReference>(),
            SuccessfulDescriptorReferencesByPath: new Dictionary<JsonPath, ResolvedDescriptorReference>(),
            LookupsByReferentialId: new Dictionary<ReferentialId, ReferenceLookupSnapshot>(),
            InvalidDocumentReferences: [],
            InvalidDescriptorReferences: [],
            DocumentReferenceOccurrences: [],
            DescriptorReferenceOccurrences: []
        );

    private static FlattenedWriteSet CreateFlattenedWriteSet(ResourceWritePlan writePlan)
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
}

[TestFixture]
public class Given_ProfileRootCreateRejectedWhenNonCreatable
{
    private RelationalDocumentStoreRepository _sut = null!;
    private IRelationalWriteTargetContextResolver _targetContextResolver = null!;
    private IReferenceResolver _referenceResolver = null!;
    private IRelationalWriteFlattener _writeFlattener = null!;
    private IRelationalWriteTerminalStage _terminalStage = null!;
    private UpsertResult _result = null!;

    [SetUp]
    public async Task Setup()
    {
        _targetContextResolver = A.Fake<IRelationalWriteTargetContextResolver>();
        _referenceResolver = A.Fake<IReferenceResolver>();
        _writeFlattener = A.Fake<IRelationalWriteFlattener>();
        _terminalStage = A.Fake<IRelationalWriteTerminalStage>();

        var documentUuid = new DocumentUuid(Guid.NewGuid());
        var writePlan = AdapterFactoryTestFixtures.BuildRootOnlyPlan();
        var resourceInfo = OrchestrationTestHelpers.CreateResourceInfo();
        var mappingSet = OrchestrationTestHelpers.CreateMappingSet(resourceInfo, writePlan);
        var requestBody = JsonNode.Parse("""{"schoolId":255901}""")!;

        // Build scope catalog from the write plan so contract validation passes
        var scopeCatalog = CompiledScopeAdapterFactory.BuildFromWritePlan(writePlan);

        // Build a ProfileAppliedWriteRequest with RootResourceCreatable = false
        // and request scope states that match the scope catalog (root scope "$")
        var rootScopeState = new RequestScopeState(
            Address: new ScopeInstanceAddress("$", []),
            Visibility: ProfileVisibilityKind.VisiblePresent,
            Creatable: false
        );

        var profileRequest = new ProfileAppliedWriteRequest(
            WritableRequestBody: requestBody,
            RootResourceCreatable: false,
            RequestScopeStates: [rootScopeState],
            VisibleRequestCollectionItems: []
        );

        var backendProfileWriteContext = new BackendProfileWriteContext(
            Request: profileRequest,
            ProfileName: "test-profile",
            CompiledScopeCatalog: scopeCatalog,
            StoredStateProjectionInvoker: A.Fake<IStoredStateProjectionInvoker>()
        );

        // Target context resolves to CreateNew so the root creatability guard fires
        A.CallTo(() =>
                _targetContextResolver.ResolveForPostAsync(
                    A<MappingSet>._,
                    A<QualifiedResourceName>._,
                    A<ReferentialId>._,
                    A<DocumentUuid>._,
                    A<CancellationToken>._
                )
            )
            .Returns(
                Task.FromResult<RelationalWriteTargetContext>(
                    new RelationalWriteTargetContext.CreateNew(documentUuid)
                )
            );

        _sut = new RelationalDocumentStoreRepository(
            NullLogger<RelationalDocumentStoreRepository>.Instance,
            _targetContextResolver,
            _referenceResolver,
            _writeFlattener,
            _terminalStage
        );

        var upsertRequest = A.Fake<IRelationalUpsertRequest>();
        A.CallTo(() => upsertRequest.ResourceInfo).Returns(resourceInfo);
        A.CallTo(() => upsertRequest.MappingSet).Returns(mappingSet);
        A.CallTo(() => upsertRequest.DocumentInfo).Returns(OrchestrationTestHelpers.CreateDocumentInfo());
        A.CallTo(() => upsertRequest.DocumentUuid).Returns(documentUuid);
        A.CallTo(() => upsertRequest.EdfiDoc).Returns(requestBody);
        A.CallTo(() => upsertRequest.TraceId).Returns(new TraceId("profile-reject-trace"));
        A.CallTo(() => upsertRequest.BackendProfileWriteContext).Returns(backendProfileWriteContext);

        _result = await _sut.UpsertDocument(upsertRequest);
    }

    [Test]
    public void It_returns_validation_failure()
    {
        _result.Should().BeOfType<UpsertResult.UpsertFailureValidation>();
    }

    [Test]
    public void It_mentions_root_resource_creation_in_the_failure_message()
    {
        var validationResult = (UpsertResult.UpsertFailureValidation)_result;
        validationResult
            .ValidationFailures.Should()
            .ContainSingle(f => f.Message.Contains("root resource", StringComparison.OrdinalIgnoreCase));
    }

    [Test]
    public void It_called_the_target_context_resolver()
    {
        A.CallTo(() =>
                _targetContextResolver.ResolveForPostAsync(
                    A<MappingSet>._,
                    A<QualifiedResourceName>._,
                    A<ReferentialId>._,
                    A<DocumentUuid>._,
                    A<CancellationToken>._
                )
            )
            .MustHaveHappenedOnceExactly();
    }

    [Test]
    public void It_did_not_call_the_flattener()
    {
        A.CallTo(() => _writeFlattener.Flatten(A<FlatteningInput>._)).MustNotHaveHappened();
    }

    [Test]
    public void It_did_not_call_the_terminal_stage()
    {
        A.CallTo(() =>
                _terminalStage.ExecuteAsync(A<RelationalWriteTerminalStageRequest>._, A<CancellationToken>._)
            )
            .MustNotHaveHappened();
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

    public static MappingSet CreateMappingSet(ResourceInfo resourceInfo, ResourceWritePlan writePlan)
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
            ReadPlansByResource: new Dictionary<QualifiedResourceName, ResourceReadPlan>(),
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

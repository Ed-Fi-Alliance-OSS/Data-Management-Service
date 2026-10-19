// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.External.Profile;
using EdFi.DataManagementService.Backend.Profile;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Profile;
using FakeItEasy;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

internal static class RelationalWriteMergeOrchestratorTestSupport
{
    private static readonly BaseResourceInfo _assessmentResourceInfo = new(
        new ProjectName("Ed-Fi"),
        new ResourceName("Assessment"),
        false
    );

    public sealed record Harness(
        RelationalWriteMergeOrchestrator Sut,
        RelationalWriteExecutorRequest Request,
        RelationalWriteExecutorRequest ProfileRequest
    );

    public static Harness CreateHarness()
    {
        var (baseWritePlan, mappingSet) = AbstractIdentitySchoolTestData.BuildSchoolWriteModel();
        var rootPlan = Given_Default_Relational_Write_Executor.CreateRootPlan();
        var writePlan = new ResourceWritePlan(baseWritePlan.Model, [rootPlan]);
        var documentUuid = new DocumentUuid(Guid.Parse("cccccccc-1111-2222-3333-dddddddddddd"));
        var body = JsonNode.Parse("""{"schoolId":155901}""")!;

        var flattener = A.Fake<IRelationalWriteFlattener>();
        A.CallTo(() => flattener.Flatten(A<FlatteningInput>._))
            .Returns(
                new FlattenedWriteSet(
                    new RootWriteRowBuffer(
                        rootPlan,
                        [
                            FlattenedWriteValue.UnresolvedRootDocumentId.Instance,
                            new FlattenedWriteValue.Literal(155901),
                            new FlattenedWriteValue.Literal("Lincoln High"),
                        ]
                    )
                )
            );

        var mergeResult = new RelationalWriteMergeResult([], supportsGuardedNoOp: true);
        var noProfileSynthesizer = A.Fake<IRelationalWriteNoProfileMergeSynthesizer>();
        A.CallTo(() => noProfileSynthesizer.Synthesize(A<RelationalWriteNoProfileMergeRequest>._))
            .Returns(mergeResult);
        var profileSynthesizer = A.Fake<IRelationalWriteProfileMergeSynthesizer>();
        A.CallTo(() => profileSynthesizer.Synthesize(A<RelationalWriteProfileMergeRequest>._))
            .Returns(ProfileMergeOutcome.Success(mergeResult));

        var request = new RelationalWriteExecutorRequest(
            mappingSet,
            RelationalWriteOperationKind.Post,
            new RelationalWriteTargetRequest.Post(
                new ReferentialId(Guid.Parse("99999999-8888-7777-6666-555555555555")),
                documentUuid
            ),
            writePlan,
            existingDocumentReadPlan: null,
            body,
            allowIdentityUpdates: false,
            new TraceId("merge-orchestrator-test"),
            new ReferenceResolverRequest(mappingSet, writePlan.Model.Resource, [], DescriptorReferences: []),
            new RelationalWriteTargetContext.CreateNew(documentUuid)
        );

        var profileRequest = request with
        {
            ProfileWriteContext = new BackendProfileWriteContext(
                Request: new ProfileAppliedWriteRequest(
                    WritableRequestBody: body,
                    RootResourceCreatable: true,
                    RequestScopeStates:
                    [
                        new RequestScopeState(
                            Address: new ScopeInstanceAddress("$", []),
                            Visibility: ProfileVisibilityKind.VisiblePresent,
                            Creatable: true
                        ),
                    ],
                    VisibleRequestCollectionItems: []
                ),
                ProfileName: "test-write-profile",
                CompiledScopeCatalog: CompiledScopeAdapterFactory.BuildFromWritePlan(writePlan),
                StoredStateProjectionInvoker: A.Fake<IStoredStateProjectionInvoker>()
            ),
        };

        return new Harness(
            new RelationalWriteMergeOrchestrator(
                flattener,
                A.Fake<IRelationalReadMaterializer>(),
                noProfileSynthesizer,
                profileSynthesizer
            ),
            request,
            profileRequest
        );
    }

    public static ResolvedReferenceSet CreateResolvedReferences(
        params DocumentReferenceFailure[] documentFailures
    ) =>
        new(
            SuccessfulDocumentReferencesByPath: new Dictionary<JsonPath, ResolvedDocumentReference>(),
            SuccessfulDescriptorReferencesByPath: new Dictionary<JsonPath, ResolvedDescriptorReference>(),
            DocumentReferences: new ResolvedDocumentReferenceMapFactory().Create([]),
            InvalidDocumentReferences: documentFailures,
            InvalidDescriptorReferences:
            [
                new DescriptorReferenceFailure(
                    new JsonPath("$.gradeLevelDescriptor"),
                    new BaseResourceInfo(
                        new ProjectName("Ed-Fi"),
                        new ResourceName("GradeLevelDescriptor"),
                        true
                    ),
                    new DocumentIdentity([
                        new DocumentIdentityElement(
                            DocumentIdentity.DescriptorIdentityJsonPath,
                            "uri://ed-fi.org/x#y"
                        ),
                    ]),
                    DescriptorReferenceFailureReason.Missing
                ),
            ]
        );

    public static DocumentReferenceFailure CreateDocumentFailure(
        string path,
        DocumentReferenceFailureReason reason
    ) =>
        DocumentReferenceFailure.From(
            new DocumentReference(
                _assessmentResourceInfo,
                new DocumentIdentity([
                    new DocumentIdentityElement(new JsonPath("$.assessmentIdentifier"), "A1"),
                ]),
                new JsonPath(path)
            ),
            reason
        );
}

[TestFixture]
public class Given_RelationalWriteMergeOrchestrator_with_missing_reference_deferral_enabled
{
    private RelationalWriteMergeResult _result = null!;

    [SetUp]
    public void Setup()
    {
        var harness = RelationalWriteMergeOrchestratorTestSupport.CreateHarness();
        var resolvedReferences = RelationalWriteMergeOrchestratorTestSupport.CreateResolvedReferences(
            RelationalWriteMergeOrchestratorTestSupport.CreateDocumentFailure(
                "$.assessmentReference",
                DocumentReferenceFailureReason.Missing
            ),
            RelationalWriteMergeOrchestratorTestSupport.CreateDocumentFailure(
                "$.sections[1].assessmentReference",
                DocumentReferenceFailureReason.Missing
            ),
            RelationalWriteMergeOrchestratorTestSupport.CreateDocumentFailure(
                "$.incompatibleReference",
                DocumentReferenceFailureReason.IncompatibleTargetType
            )
        );

        _result = harness
            .Sut.Resolve(
                harness.Request,
                harness.Request.TargetContext,
                currentState: null,
                resolvedReferences,
                allowMissingDocumentReferencesForPrecedence: true
            )
            .MergeResult!;
    }

    [Test]
    public void It_carries_exactly_the_missing_document_reference_paths()
    {
        _result
            .DeferredMissingDocumentReferencePaths.Should()
            .BeEquivalentTo("$.assessmentReference", "$.sections[1].assessmentReference");
    }

    [Test]
    public void It_does_not_include_non_missing_document_failures()
    {
        _result.DeferredMissingDocumentReferencePaths.Should().NotContain("$.incompatibleReference");
    }

    [Test]
    public void It_compares_paths_ordinally()
    {
        _result.DeferredMissingDocumentReferencePaths.Contains("$.ASSESSMENTREFERENCE").Should().BeFalse();
    }
}

[TestFixture]
public class Given_RelationalWriteMergeOrchestrator_with_missing_reference_deferral_disabled
{
    private RelationalWriteMergeResult _result = null!;

    [SetUp]
    public void Setup()
    {
        var harness = RelationalWriteMergeOrchestratorTestSupport.CreateHarness();
        var resolvedReferences = RelationalWriteMergeOrchestratorTestSupport.CreateResolvedReferences(
            RelationalWriteMergeOrchestratorTestSupport.CreateDocumentFailure(
                "$.assessmentReference",
                DocumentReferenceFailureReason.Missing
            )
        );

        _result = harness
            .Sut.Resolve(
                harness.Request,
                harness.Request.TargetContext,
                currentState: null,
                resolvedReferences,
                allowMissingDocumentReferencesForPrecedence: false
            )
            .MergeResult!;
    }

    [Test]
    public void It_carries_no_deferred_missing_document_reference_paths()
    {
        _result.DeferredMissingDocumentReferencePaths.Should().BeEmpty();
    }
}

[TestFixture]
public class Given_RelationalWriteMergeOrchestrator_with_a_profile_write_context
{
    private RelationalWriteMergeResult _result = null!;

    [SetUp]
    public void Setup()
    {
        var harness = RelationalWriteMergeOrchestratorTestSupport.CreateHarness();
        var resolvedReferences = RelationalWriteMergeOrchestratorTestSupport.CreateResolvedReferences(
            RelationalWriteMergeOrchestratorTestSupport.CreateDocumentFailure(
                "$.assessmentReference",
                DocumentReferenceFailureReason.Missing
            )
        );

        _result = harness
            .Sut.Resolve(
                harness.ProfileRequest,
                harness.ProfileRequest.TargetContext,
                currentState: null,
                resolvedReferences,
                allowMissingDocumentReferencesForPrecedence: true
            )
            .MergeResult!;
    }

    [Test]
    public void It_carries_no_deferred_missing_document_reference_paths()
    {
        _result.DeferredMissingDocumentReferencePaths.Should().BeEmpty();
    }
}

// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.External.Profile;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Backend.Profile;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Profile;
using FakeItEasy;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture("none", false)]
[TestFixture("no-authorization-required", false)]
[TestFixture("no-further-authorization-required", false)]
[TestFixture("empty-custom-view", false)]
[TestFixture("stored-custom-view", false)]
[TestFixture("paired-self-basis-update", false)]
[TestFixture("ownership-update", false)]
[TestFixture("namespace", true)]
[TestFixture("relationship", true)]
[TestFixture("no-claims", true)]
[TestFixture("custom-view", true)]
[TestFixture("self-basis-create", true)]
[TestFixture("ownership-create", true)]
public class Given_RelationalWriteMergeOrchestrator_with_a_retained_root_and_child_policy_failure(
    string disposition,
    bool deferFailure
)
{
    private RelationalWriteMergeBoundary _boundary = null!;
    private RelationalWriteMergedTableState _retainedRoot = null!;

    [SetUp]
    public void Setup()
    {
        var harness = RelationalWriteMergeOrchestratorTestSupport.CreateHarness();
        var request = harness.ProfileRequest;
        var rootPlan = request.WritePlan.TablePlansInDependencyOrder[0];
        var rootTable = rootPlan.TableModel.Table;
        var strategy = new ConfiguredAuthorizationStrategy("SchoolWithTestAuthorization", 0);
        var storedCheck = new SingleRecordCustomViewAuthorizationCheckSpec(
            strategy,
            0,
            CustomViewAuthorizationCheckValueSource.Stored,
            new DbTableName(new DbSchemaName("auth"), "SchoolWithTestAuthorization"),
            new DbColumnName("DocumentId"),
            [],
            new CustomViewAuthorizationCheckTarget.Stored(rootTable, new DbColumnName("DocumentId")),
            request.WritePlan.Model.Resource,
            ["SchoolId"],
            "Test hint"
        );
        var proposedCheck = storedCheck with
        {
            Index = 1,
            ValueSource = CustomViewAuthorizationCheckValueSource.Proposed,
            CheckTarget = new CustomViewAuthorizationCheckTarget.Proposed(
                rootTable,
                new CustomViewAuthorizationProposedValueBinding(
                    rootTable,
                    new DbColumnName("SchoolId"),
                    "SchoolId",
                    "schoolId"
                )
            ),
        };
        var ownershipFailure = new RelationalWriteExecutorResult.Upsert(
            new UpsertResult.UpsertFailureOwnershipNotAuthorized(
                new OwnershipAuthorizationFailure(
                    OwnershipAuthorizationFailureKind.OwnershipTokenMismatch,
                    0,
                    "OwnershipBased"
                )
            )
        );
        request = disposition switch
        {
            "no-authorization-required" => request with
            {
                ProposedRelationshipAuthorization =
                    new RelationshipAuthorizationResult.NoAuthorizationRequired([]),
            },
            "no-further-authorization-required" => request with
            {
                ProposedRelationshipAuthorization =
                    new RelationshipAuthorizationResult.NoFurtherAuthorizationRequired([]),
            },
            "empty-custom-view" => request with { CustomViewAuthorization = new([]) },
            "stored-custom-view" => request with { CustomViewAuthorization = new([storedCheck]) },
            "paired-self-basis-update" => request with
            {
                TargetContext = new RelationalWriteTargetContext.ExistingDocument(
                    1,
                    new DocumentUuid(Guid.NewGuid()),
                    1
                ),
                CustomViewAuthorization = new([
                    storedCheck,
                    proposedCheck with
                    {
                        CheckTarget = new CustomViewAuthorizationCheckTarget.ProposedSelfBasisUnavailable(
                            rootTable
                        ),
                    },
                ]),
            },
            "ownership-update" => request with
            {
                TargetContext = new RelationalWriteTargetContext.ExistingDocument(
                    1,
                    new DocumentUuid(Guid.NewGuid()),
                    1
                ),
                DeferredCreateOwnershipFailureResult = ownershipFailure,
            },
            "namespace" => request with
            {
                ProposedNamespaceAuthorization = new(
                    [
                        new NamespaceAuthorizationCheckSpec(
                            0,
                            NamespaceAuthorizationCheckValueSource.Proposed,
                            rootTable,
                            new DbColumnName("Name")
                        ),
                    ],
                    NamespacePrefixParameterizationFactory.Create(
                        SqlDialect.Pgsql,
                        ["uri://ed-fi.org/"],
                        "prefixes"
                    )
                ),
            },
            "relationship" => request with
            {
                ProposedRelationshipAuthorization =
                    Given_Default_Relational_Write_Executor.CreateProposedSchoolIdRelationshipAuthorization(
                        new RelationalWriteExecutorInput(
                            request.MappingSet,
                            request.OperationKind,
                            request.TargetRequest,
                            request.WritePlan,
                            request.ExistingDocumentReadPlan,
                            request.SelectedBody,
                            request.AllowIdentityUpdates,
                            request.TraceId,
                            request.ReferenceResolutionRequest
                        )
                    ),
            },
            "no-claims" => request with
            {
                ProposedRelationshipAuthorization = new RelationshipAuthorizationResult.NoClaims([], []),
            },
            "custom-view" => request with { CustomViewAuthorization = new([proposedCheck]) },
            "self-basis-create" => request with
            {
                CustomViewAuthorization = new([
                    proposedCheck with
                    {
                        CheckTarget = new CustomViewAuthorizationCheckTarget.ProposedSelfBasisUnavailable(
                            rootTable
                        ),
                    },
                ]),
            },
            "ownership-create" => request with { DeferredCreateOwnershipFailureResult = ownershipFailure },
            _ => request,
        };
        _retainedRoot = new RelationalWriteMergedTableState(rootPlan, [], []);
        A.CallTo(() => harness.ProfileSynthesizer.Synthesize(A<RelationalWriteProfileMergeRequest>._))
            .Returns(
                ProfileMergeOutcome.Reject(
                    new ProfileCreatabilityRejection("$.children[*]", "Not creatable")
                ) with
                {
                    AuthorizationRootTableState = _retainedRoot,
                }
            );

        _boundary = harness.Sut.Resolve(
            request,
            request.TargetContext,
            currentState: null,
            RelationalWriteMergeOrchestratorTestSupport.CreateResolvedReferences()
        );
    }

    [Test]
    public void It_defers_the_policy_failure_only_when_proposed_authorization_can_still_deny()
    {
        var failure = new RelationalWriteExecutorResult.Upsert(
            new UpsertResult.UpsertFailureProfileDataPolicy("test-write-profile")
        );
        _boundary
            .Should()
            .BeEquivalentTo(
                deferFailure
                    ? new RelationalWriteMergeBoundary(
                        new RelationalWriteMergeResult([_retainedRoot], supportsGuardedNoOp: false),
                        ImmediateResult: null,
                        DeferredProfileFailureResult: failure
                    )
                    : new RelationalWriteMergeBoundary(null, failure)
            );
    }
}

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
        RelationalWriteExecutorRequest ProfileRequest,
        IRelationalWriteFlattener Flattener,
        IRelationalWriteProfileMergeSynthesizer ProfileSynthesizer
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
                new DocumentIdentity([new DocumentIdentityElement(new JsonPath("$.schoolId"), "255901")]),
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
            profileRequest,
            flattener,
            profileSynthesizer
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
public class Given_RelationalWriteMergeOrchestrator_with_a_profile_write_context_and_missing_reference_deferral_enabled
{
    private RelationalWriteMergeOrchestratorTestSupport.Harness _harness = null!;
    private RelationalWriteMergeResult _result = null!;

    [SetUp]
    public void Setup()
    {
        _harness = RelationalWriteMergeOrchestratorTestSupport.CreateHarness();
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

        _result = _harness
            .Sut.Resolve(
                _harness.ProfileRequest,
                _harness.ProfileRequest.TargetContext,
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
        _result.DeferredMissingDocumentReferencePaths.Contains("$.ASSESSMENTREFERENCE").Should().BeFalse();
    }

    [Test]
    public void It_flattens_the_profile_writable_body_with_deferral_enabled()
    {
        A.CallTo(() =>
                _harness.Flattener.Flatten(
                    A<FlatteningInput>.That.Matches(input =>
                        input.AllowMissingDocumentReferencesForPrecedence
                    )
                )
            )
            .MustHaveHappenedOnceExactly();
    }

    [Test]
    public void It_builds_the_profile_merge_lookups_with_deferral_enabled()
    {
        A.CallTo(() =>
                _harness.ProfileSynthesizer.Synthesize(
                    A<RelationalWriteProfileMergeRequest>.That.Matches(request =>
                        request.AllowMissingDocumentReferencesForPrecedence
                    )
                )
            )
            .MustHaveHappenedOnceExactly();
    }
}

[TestFixture]
public class Given_RelationalWriteMergeOrchestrator_with_a_profile_write_context_and_missing_reference_deferral_disabled
{
    private RelationalWriteMergeOrchestratorTestSupport.Harness _harness = null!;
    private RelationalWriteMergeResult _result = null!;

    [SetUp]
    public void Setup()
    {
        _harness = RelationalWriteMergeOrchestratorTestSupport.CreateHarness();
        var resolvedReferences = RelationalWriteMergeOrchestratorTestSupport.CreateResolvedReferences(
            RelationalWriteMergeOrchestratorTestSupport.CreateDocumentFailure(
                "$.assessmentReference",
                DocumentReferenceFailureReason.Missing
            )
        );

        _result = _harness
            .Sut.Resolve(
                _harness.ProfileRequest,
                _harness.ProfileRequest.TargetContext,
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

    [Test]
    public void It_does_not_enable_deferral_for_the_profile_merge()
    {
        A.CallTo(() =>
                _harness.ProfileSynthesizer.Synthesize(
                    A<RelationalWriteProfileMergeRequest>.That.Matches(request =>
                        !request.AllowMissingDocumentReferencesForPrecedence
                    )
                )
            )
            .MustHaveHappenedOnceExactly();
    }
}

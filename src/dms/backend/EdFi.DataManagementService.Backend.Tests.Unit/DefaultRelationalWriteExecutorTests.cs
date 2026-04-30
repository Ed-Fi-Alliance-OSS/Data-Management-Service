// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.External.Profile;
using EdFi.DataManagementService.Backend.Profile;
using EdFi.DataManagementService.Backend.Tests.Unit.Profile;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Profile;
using FakeItEasy;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
[Parallelizable]
public class Given_Default_Relational_Write_Executor
{
    private RecordingRelationalWriteSessionFactory _writeSessionFactory = null!;
    private RecordingReferenceResolverAdapterFactory _referenceResolverAdapterFactory = null!;
    private RecordingRelationalWriteFlattener _writeFlattener = null!;
    private RecordingRelationalWriteCurrentStateLoader _currentStateLoader = null!;
    private RecordingRelationalCommittedRepresentationReader _committedRepresentationReader = null!;
    private RecordingRelationalWriteTargetLookupResolver _targetLookupResolver = null!;
    private RecordingRelationalWriteFreshnessChecker _writeFreshnessChecker = null!;
    private RecordingRelationalWriteNoProfileMergeSynthesizer _noProfileMergeSynthesizer = null!;
    private RecordingRelationalWriteProfileMergeSynthesizer _profileMergeSynthesizer = null!;
    private RecordingRelationalWriteNoProfilePersister _noProfilePersister = null!;
    private RecordingRelationalWriteExceptionClassifier _writeExceptionClassifier = null!;
    private RecordingRelationalWriteConstraintResolver _writeConstraintResolver = null!;
    private RecordingRelationalReadMaterializer _readMaterializer = null!;
    private DefaultRelationalWriteExecutor _sut = null!;

    [SetUp]
    public void Setup()
    {
        _writeSessionFactory = new RecordingRelationalWriteSessionFactory();
        _referenceResolverAdapterFactory = new RecordingReferenceResolverAdapterFactory();
        _writeFlattener = new RecordingRelationalWriteFlattener();
        _currentStateLoader = new RecordingRelationalWriteCurrentStateLoader();
        _committedRepresentationReader = new RecordingRelationalCommittedRepresentationReader();
        _targetLookupResolver = new RecordingRelationalWriteTargetLookupResolver();
        _writeFreshnessChecker = new RecordingRelationalWriteFreshnessChecker();
        _noProfileMergeSynthesizer = new RecordingRelationalWriteNoProfileMergeSynthesizer();
        _profileMergeSynthesizer = new RecordingRelationalWriteProfileMergeSynthesizer();
        _noProfilePersister = new RecordingRelationalWriteNoProfilePersister();
        _writeExceptionClassifier = new RecordingRelationalWriteExceptionClassifier();
        _writeConstraintResolver = new RecordingRelationalWriteConstraintResolver();
        _readMaterializer = new RecordingRelationalReadMaterializer();
        _sut = new DefaultRelationalWriteExecutor(
            _writeSessionFactory,
            _referenceResolverAdapterFactory,
            _writeFlattener,
            _currentStateLoader,
            _committedRepresentationReader,
            _targetLookupResolver,
            _writeFreshnessChecker,
            _noProfileMergeSynthesizer,
            _profileMergeSynthesizer,
            _noProfilePersister,
            _writeExceptionClassifier,
            _writeConstraintResolver,
            _readMaterializer
        );
    }

    [Test]
    public async Task It_resolves_references_through_the_attempt_scoped_session_before_flattening_post_requests()
    {
        var documentReferentialId = new ReferentialId(Guid.NewGuid());
        var descriptorReferentialId = new ReferentialId(Guid.NewGuid());
        var request = CreateRequest(
            RelationalWriteOperationKind.Post,
            documentReferences:
            [
                RelationalAccessTestData.CreateDocumentReference(documentReferentialId, "$.schoolReference"),
                RelationalAccessTestData.CreateDocumentReference(
                    documentReferentialId,
                    "$.educationOrganizationReference"
                ),
            ],
            descriptorReferences:
            [
                RelationalAccessTestData.CreateDescriptorReference(
                    descriptorReferentialId,
                    "uri://ed-fi.org/SchoolTypeDescriptor#Alternative",
                    "$.schoolTypeDescriptor"
                ),
            ]
        );
        _referenceResolverAdapterFactory.Adapter.LookupResults =
        [
            new ReferenceLookupResult(documentReferentialId, 101L, 1, 1, false, "$.schoolId=255901"),
            new ReferenceLookupResult(
                descriptorReferentialId,
                202L,
                13,
                13,
                true,
                "$.descriptor=uri://ed-fi.org/schooltypedescriptor#alternative"
            ),
        ];

        var result = await _sut.ExecuteAsync(request);

        result
            .Should()
            .BeEquivalentTo(
                new RelationalWriteExecutorResult.Upsert(
                    new UpsertResult.InsertSuccess(
                        new DocumentUuid(Guid.Parse("cccccccc-1111-2222-3333-dddddddddddd")),
                        ExpectedEtag(request)
                    ),
                    RelationalWriteExecutorAttemptOutcome.AppliedWrite.Instance
                )
            );
        result.AttemptOutcome.Should().Be(RelationalWriteExecutorAttemptOutcome.AppliedWrite.Instance);
        _writeSessionFactory.CreateAsyncCallCount.Should().Be(1);
        _referenceResolverAdapterFactory.CreateAdapterCallCount.Should().Be(0);
        _referenceResolverAdapterFactory.CreateSessionAdapterCallCount.Should().Be(1);
        _referenceResolverAdapterFactory
            .CapturedConnection.Should()
            .BeSameAs(_writeSessionFactory.Session.Connection);
        _referenceResolverAdapterFactory
            .CapturedTransaction.Should()
            .BeSameAs(_writeSessionFactory.Session.Transaction);
        _referenceResolverAdapterFactory.Adapter.Requests.Should().ContainSingle();
        _referenceResolverAdapterFactory.Adapter.Requests[0].Lookups.Should().HaveCount(2);
        _writeFlattener.FlattenCallCount.Should().Be(1);
        _writeFlattener.CapturedInput.Should().NotBeNull();
        _writeFlattener.CapturedInput!.OperationKind.Should().Be(request.OperationKind);
        _writeFlattener
            .CapturedInput.TargetContext.Should()
            .BeEquivalentTo(
                new RelationalWriteTargetContext.CreateNew(
                    ((RelationalWriteTargetRequest.Post)request.TargetRequest).CandidateDocumentUuid
                )
            );
        _writeFlattener.CapturedInput.WritePlan.Should().BeSameAs(request.WritePlan);
        _writeFlattener.CapturedInput.SelectedBody.Should().BeSameAs(request.SelectedBody);
        _writeFlattener.CapturedInput.ResolvedReferences.DocumentReferenceOccurrences.Should().HaveCount(2);
        _writeFlattener
            .CapturedInput.ResolvedReferences.DescriptorReferenceOccurrences.Should()
            .ContainSingle();
        _writeFlattener
            .CapturedInput.ResolvedReferences.SuccessfulDocumentReferencesByPath.Keys.Should()
            .BeEquivalentTo([
                new JsonPath("$.schoolReference"),
                new JsonPath("$.educationOrganizationReference"),
            ]);
        _writeFlattener
            .CapturedInput.ResolvedReferences.SuccessfulDescriptorReferencesByPath.Keys.Should()
            .BeEquivalentTo([new JsonPath("$.schoolTypeDescriptor")]);
        _currentStateLoader.LoadCallCount.Should().Be(0);
        _noProfileMergeSynthesizer.SynthesizeCallCount.Should().Be(1);
        _noProfilePersister.TryPersistCallCount.Should().Be(1);
        _noProfileMergeSynthesizer.CapturedRequest.Should().NotBeNull();
        _noProfileMergeSynthesizer.CapturedRequest!.WritePlan.Should().BeSameAs(request.WritePlan);
        _noProfileMergeSynthesizer.CapturedRequest!.CurrentState.Should().BeNull();
        _targetLookupResolver.ResolveForPostCallCount.Should().Be(1);
        _targetLookupResolver.CapturedWriteSession.Should().NotBeNull();
        _targetLookupResolver
            .CapturedWriteSession!.Connection.Should()
            .BeSameAs(_writeSessionFactory.Session.Connection);
        _targetLookupResolver
            .CapturedWriteSession!.Transaction.Should()
            .BeSameAs(_writeSessionFactory.Session.Transaction);
        _writeSessionFactory.Session.CommitCallCount.Should().Be(1);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(0);
        _writeSessionFactory.Session.DisposeCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_short_circuits_reference_failures_before_flattening()
    {
        var documentReference = RelationalAccessTestData.CreateDocumentReference(
            new ReferentialId(Guid.NewGuid()),
            "$.schoolReference"
        );
        var request = CreateRequest(
            RelationalWriteOperationKind.Post,
            documentReferences: [documentReference]
        );

        var result = await _sut.ExecuteAsync(request);

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
        _writeFlattener.FlattenCallCount.Should().Be(0);
        _currentStateLoader.LoadCallCount.Should().Be(0);
        _noProfileMergeSynthesizer.SynthesizeCallCount.Should().Be(0);
        _targetLookupResolver.ResolveForPostCallCount.Should().Be(0);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
        _writeSessionFactory.Session.DisposeCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_loads_current_state_once_for_existing_document_requests()
    {
        var request = CreateRequest(
            RelationalWriteOperationKind.Put,
            selectedBody: JsonNode.Parse("""{"name":"Lincoln High Updated","schoolId":255901}""")!
        );
        _currentStateLoader.ResultToReturn = new RelationalWriteCurrentState(
            new DocumentMetadataRow(
                345L,
                Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"),
                44L,
                44L,
                new DateTimeOffset(2026, 4, 2, 12, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 4, 2, 12, 0, 0, TimeSpan.Zero)
            ),
            [
                new HydratedTableRows(
                    request.WritePlan.Model.Root,
                    [
                        [345L, 255901, "Lincoln High"],
                    ]
                ),
            ],
            []
        );
        _noProfileMergeSynthesizer.ResultToReturn = CreateMergeResult(
            request.WritePlan.TablePlansInDependencyOrder[0],
            currentSchoolId: 255901,
            mergedSchoolId: 255901,
            currentName: "Lincoln High",
            mergedName: "Lincoln High Updated"
        );

        var result = await _sut.ExecuteAsync(request);

        result
            .Should()
            .BeEquivalentTo(
                new RelationalWriteExecutorResult.Update(
                    new UpdateResult.UpdateSuccess(
                        new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb")),
                        ExpectedEtag(request)
                    ),
                    RelationalWriteExecutorAttemptOutcome.AppliedWrite.Instance
                )
            );
        result.AttemptOutcome.Should().Be(RelationalWriteExecutorAttemptOutcome.AppliedWrite.Instance);
        _writeFlattener.FlattenCallCount.Should().Be(1);
        _currentStateLoader.LoadCallCount.Should().Be(1);
        _currentStateLoader.CapturedRequest.Should().NotBeNull();
        _currentStateLoader.CapturedRequest!.ReadPlan.Should().BeSameAs(request.ExistingDocumentReadPlan);
        _currentStateLoader.CapturedRequest!.TargetContext.DocumentId.Should().Be(345L);
        _currentStateLoader.CapturedWriteSession.Should().BeSameAs(_writeSessionFactory.Session);
        _noProfileMergeSynthesizer.SynthesizeCallCount.Should().Be(1);
        _noProfilePersister.TryPersistCallCount.Should().Be(1);
        _noProfileMergeSynthesizer.CapturedRequest.Should().NotBeNull();
        _noProfileMergeSynthesizer
            .CapturedRequest!.CurrentState.Should()
            .BeSameAs(_currentStateLoader.ResultToReturn);
        _targetLookupResolver.CapturedWriteSession.Should().BeNull();
        _writeFreshnessChecker.IsCurrentCallCount.Should().Be(0);
        _writeSessionFactory.Session.CommitCallCount.Should().Be(1);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(0);
        _writeSessionFactory.Session.DisposeCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_reads_and_returns_the_committed_external_response_etag_before_commit_for_applied_writes()
    {
        var request = CreateRequest(
            RelationalWriteOperationKind.Post,
            selectedBody: JsonNode.Parse("""{"name":"Lincoln High"}""")!
        );
        var persistedTarget = new RelationalWritePersistResult(
            910L,
            ((RelationalWriteTargetContext.CreateNew)request.TargetContext).DocumentUuid
        );
        var committedResponse = CreateCommittedExternalResponse(
            persistedTarget,
            JsonNode.Parse("""{"name":"Lincoln High","schoolId":255901}""")!
        );

        _noProfilePersister.ResultToReturn = persistedTarget;
        _committedRepresentationReader.ResultToReturn = committedResponse;

        var result = await _sut.ExecuteAsync(request);

        result
            .Should()
            .BeEquivalentTo(
                new RelationalWriteExecutorResult.Upsert(
                    new UpsertResult.InsertSuccess(
                        persistedTarget.DocumentUuid,
                        ExpectedCommittedResponseEtag(committedResponse)
                    ),
                    RelationalWriteExecutorAttemptOutcome.AppliedWrite.Instance
                )
            );
        result
            .Should()
            .BeOfType<RelationalWriteExecutorResult.Upsert>()
            .Which.Result.Should()
            .BeOfType<UpsertResult.InsertSuccess>()
            .Which.ETag.Should()
            .NotBe(ExpectedSelectedBodyEtag(request));
        _committedRepresentationReader.ReadCallCount.Should().Be(1);
        _committedRepresentationReader.CapturedRequest.Should().BeEquivalentTo(request);
        _committedRepresentationReader.CapturedPersistedTarget.Should().BeEquivalentTo(persistedTarget);
        _committedRepresentationReader.CapturedWriteSession.Should().BeSameAs(_writeSessionFactory.Session);
        _committedRepresentationReader.CommitCallCountObservedDuringRead.Should().Be(0);
        _writeSessionFactory.Session.Commands.Should().BeEmpty();
        _writeSessionFactory.Session.CommitCallCount.Should().Be(1);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(0);
    }

    [Test]
    public async Task It_revalidates_create_new_post_requests_inside_the_write_session_before_persisting()
    {
        var existingDocumentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));
        var request = CreateRequest(RelationalWriteOperationKind.Post);
        var existingTargetContext = new RelationalWriteTargetContext.ExistingDocument(
            345L,
            existingDocumentUuid,
            45L
        );

        _targetLookupResolver.PostResults.Enqueue(
            new RelationalWriteTargetLookupResult.ExistingDocument(345L, existingDocumentUuid, 45L)
        );
        _currentStateLoader.ResultToReturn = CreateCurrentState(
            request with
            {
                TargetContext = existingTargetContext,
            },
            45L
        );
        _noProfileMergeSynthesizer.ResultToReturn = CreateMergeResult(
            request.WritePlan.TablePlansInDependencyOrder[0],
            currentSchoolId: 255901,
            mergedSchoolId: 255901,
            currentName: "Lincoln High",
            mergedName: "Lincoln High Updated"
        );

        var result = await _sut.ExecuteAsync(request);

        result
            .Should()
            .BeEquivalentTo(
                new RelationalWriteExecutorResult.Upsert(
                    new UpsertResult.UpdateSuccess(existingDocumentUuid, ExpectedEtag(request)),
                    RelationalWriteExecutorAttemptOutcome.AppliedWrite.Instance
                )
            );
        _targetLookupResolver.ResolveForPostCallCount.Should().Be(1);
        _targetLookupResolver.CapturedWriteSession.Should().NotBeNull();
        _targetLookupResolver
            .CapturedWriteSession!.Connection.Should()
            .BeSameAs(_writeSessionFactory.Session.Connection);
        _targetLookupResolver
            .CapturedWriteSession!.Transaction.Should()
            .BeSameAs(_writeSessionFactory.Session.Transaction);
        _currentStateLoader.LoadCallCount.Should().Be(1);
        _currentStateLoader.CapturedRequest.Should().NotBeNull();
        _currentStateLoader.CapturedRequest!.TargetContext.Should().BeEquivalentTo(existingTargetContext);
        _writeFlattener.CapturedInput.Should().NotBeNull();
        _writeFlattener.CapturedInput!.TargetContext.Should().BeEquivalentTo(existingTargetContext);
        _noProfilePersister.CapturedRequest.Should().NotBeNull();
        _noProfilePersister.CapturedRequest!.TargetContext.Should().BeEquivalentTo(existingTargetContext);
        _writeSessionFactory.Session.CommitCallCount.Should().Be(1);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(0);
    }

    [Test]
    public async Task It_short_circuits_unchanged_put_requests_as_guarded_no_ops()
    {
        var request = CreateRequest(
            RelationalWriteOperationKind.Put,
            selectedBody: JsonNode.Parse("""{"name":"Lincoln High"}""")!
        );
        var persistedTarget = new RelationalWritePersistResult(
            345L,
            new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"))
        );
        var committedResponse = CreateCommittedExternalResponse(
            persistedTarget,
            JsonNode.Parse("""{"name":"Lincoln High","schoolId":255901}""")!
        );
        _committedRepresentationReader.ResultToReturn = committedResponse;

        var result = await _sut.ExecuteAsync(request);

        result
            .Should()
            .BeEquivalentTo(
                new RelationalWriteExecutorResult.Update(
                    new UpdateResult.UpdateSuccess(
                        persistedTarget.DocumentUuid,
                        ExpectedCommittedResponseEtag(committedResponse)
                    ),
                    RelationalWriteExecutorAttemptOutcome.GuardedNoOp.Instance
                )
            );
        result.AttemptOutcome.Should().Be(RelationalWriteExecutorAttemptOutcome.GuardedNoOp.Instance);
        result
            .Should()
            .BeOfType<RelationalWriteExecutorResult.Update>()
            .Which.Result.Should()
            .BeOfType<UpdateResult.UpdateSuccess>()
            .Which.ETag.Should()
            .NotBe(ExpectedSelectedBodyEtag(request));
        _currentStateLoader.LoadCallCount.Should().Be(1);
        _noProfileMergeSynthesizer.SynthesizeCallCount.Should().Be(1);
        _noProfilePersister.TryPersistCallCount.Should().Be(0);
        _committedRepresentationReader.ReadCallCount.Should().Be(1);
        _committedRepresentationReader.CapturedPersistedTarget.Should().BeEquivalentTo(persistedTarget);
        _committedRepresentationReader.CapturedWriteSession.Should().BeSameAs(_writeSessionFactory.Session);
        _committedRepresentationReader.CommitCallCountObservedDuringRead.Should().Be(0);
        _writeFreshnessChecker.IsCurrentCallCount.Should().Be(1);
        _writeFreshnessChecker.CapturedRequest.Should().NotBeNull();
        _writeFreshnessChecker.CapturedRequest!.TargetRequest.Should().BeEquivalentTo(request.TargetRequest);
        _writeFreshnessChecker
            .CapturedTargetContext.Should()
            .BeEquivalentTo(
                new RelationalWriteTargetContext.ExistingDocument(
                    345L,
                    new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb")),
                    44L
                )
            );
        _writeFreshnessChecker.CapturedWriteSession.Should().BeSameAs(_writeSessionFactory.Session);
        _writeSessionFactory.Session.CommitCallCount.Should().Be(1);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(0);
        _writeSessionFactory.Session.DisposeCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_short_circuits_unchanged_post_as_update_requests_as_guarded_no_ops()
    {
        var request = CreateRequest(
            RelationalWriteOperationKind.Post,
            targetContext: new RelationalWriteTargetContext.ExistingDocument(
                345L,
                new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb")),
                44L
            )
        );

        var result = await _sut.ExecuteAsync(request);

        result
            .Should()
            .BeEquivalentTo(
                new RelationalWriteExecutorResult.Upsert(
                    new UpsertResult.UpdateSuccess(
                        new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb")),
                        ExpectedEtag(request)
                    ),
                    RelationalWriteExecutorAttemptOutcome.GuardedNoOp.Instance
                )
            );
        result.AttemptOutcome.Should().Be(RelationalWriteExecutorAttemptOutcome.GuardedNoOp.Instance);
        _currentStateLoader.LoadCallCount.Should().Be(1);
        _noProfileMergeSynthesizer.SynthesizeCallCount.Should().Be(1);
        _noProfilePersister.TryPersistCallCount.Should().Be(0);
        _committedRepresentationReader.ReadCallCount.Should().Be(1);
        _writeFreshnessChecker.IsCurrentCallCount.Should().Be(1);
        _targetLookupResolver.ResolveForPostCallCount.Should().Be(0);
        _writeSessionFactory.Session.CommitCallCount.Should().Be(1);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(0);
        _writeSessionFactory.Session.DisposeCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_uses_the_session_loaded_content_version_when_guarding_unchanged_put_requests()
    {
        var request = CreateRequest(RelationalWriteOperationKind.Put);
        _currentStateLoader.ResultToReturn = CreateCurrentState(request, 45L);
        _writeFreshnessChecker.IsCurrentEvaluator = static targetContext =>
            targetContext.ObservedContentVersion == 45L;

        var result = await _sut.ExecuteAsync(request);

        result
            .Should()
            .BeEquivalentTo(
                new RelationalWriteExecutorResult.Update(
                    new UpdateResult.UpdateSuccess(
                        new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb")),
                        ExpectedEtag(request)
                    ),
                    RelationalWriteExecutorAttemptOutcome.GuardedNoOp.Instance
                )
            );
        _writeFlattener
            .CapturedInput!.TargetContext.Should()
            .BeEquivalentTo(
                new RelationalWriteTargetContext.ExistingDocument(
                    345L,
                    new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb")),
                    45L
                )
            );
        _writeFreshnessChecker
            .CapturedRequest!.TargetContext.Should()
            .BeEquivalentTo(
                new RelationalWriteTargetContext.ExistingDocument(
                    345L,
                    new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb")),
                    45L
                )
            );
        _writeFreshnessChecker
            .CapturedTargetContext.Should()
            .BeEquivalentTo(
                new RelationalWriteTargetContext.ExistingDocument(
                    345L,
                    new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb")),
                    45L
                )
            );
        _committedRepresentationReader.ReadCallCount.Should().Be(1);
        _writeSessionFactory.Session.CommitCallCount.Should().Be(1);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(0);
    }

    [Test]
    public async Task It_uses_the_session_loaded_content_version_when_guarding_unchanged_post_as_update_requests()
    {
        var request = CreateRequest(
            RelationalWriteOperationKind.Post,
            targetContext: new RelationalWriteTargetContext.ExistingDocument(
                345L,
                new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb")),
                44L
            )
        );
        _currentStateLoader.ResultToReturn = CreateCurrentState(request, 45L);
        _writeFreshnessChecker.IsCurrentEvaluator = static targetContext =>
            targetContext.ObservedContentVersion == 45L;

        var result = await _sut.ExecuteAsync(request);

        result
            .Should()
            .BeEquivalentTo(
                new RelationalWriteExecutorResult.Upsert(
                    new UpsertResult.UpdateSuccess(
                        new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb")),
                        ExpectedEtag(request)
                    ),
                    RelationalWriteExecutorAttemptOutcome.GuardedNoOp.Instance
                )
            );
        _writeFlattener
            .CapturedInput!.TargetContext.Should()
            .BeEquivalentTo(
                new RelationalWriteTargetContext.ExistingDocument(
                    345L,
                    new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb")),
                    45L
                )
            );
        _writeFreshnessChecker
            .CapturedRequest!.TargetContext.Should()
            .BeEquivalentTo(
                new RelationalWriteTargetContext.ExistingDocument(
                    345L,
                    new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb")),
                    45L
                )
            );
        _writeFreshnessChecker
            .CapturedTargetContext.Should()
            .BeEquivalentTo(
                new RelationalWriteTargetContext.ExistingDocument(
                    345L,
                    new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb")),
                    45L
                )
            );
        _committedRepresentationReader.ReadCallCount.Should().Be(1);
        _writeSessionFactory.Session.CommitCallCount.Should().Be(1);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(0);
    }

    [Test]
    public async Task It_returns_a_guarded_no_op_for_unchanged_sql_server_date_and_time_writes()
    {
        var request = CreateRequest(
            RelationalWriteOperationKind.Put,
            rootWritePlan: CreateDateAndTimeRootPlan(),
            selectedBody: JsonNode.Parse("""{"sessionDate":"2026-08-20","startTime":"14:05:07"}""")!,
            dialect: SqlDialect.Mssql
        );
        _writeFlattener.ResultToReturn = new FlattenedWriteSet(
            new RootWriteRowBuffer(
                request.WritePlan.TablePlansInDependencyOrder.Single(),
                [
                    new FlattenedWriteValue.Literal(345L),
                    new FlattenedWriteValue.Literal(new DateOnly(2026, 8, 20)),
                    new FlattenedWriteValue.Literal(new TimeOnly(14, 5, 7)),
                ]
            )
        );
        _currentStateLoader.ResultToReturn = new RelationalWriteCurrentState(
            new DocumentMetadataRow(
                345L,
                Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"),
                44L,
                44L,
                new DateTimeOffset(2026, 4, 2, 12, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 4, 2, 12, 0, 0, TimeSpan.Zero)
            ),
            [
                new HydratedTableRows(
                    request.WritePlan.Model.Root,
                    [
                        [
                            345L,
                            new DateTime(2026, 8, 20, 0, 0, 0, DateTimeKind.Unspecified),
                            new TimeSpan(14, 5, 7),
                        ],
                    ]
                ),
            ],
            []
        );
        _sut = new DefaultRelationalWriteExecutor(
            _writeSessionFactory,
            _referenceResolverAdapterFactory,
            _writeFlattener,
            _currentStateLoader,
            _committedRepresentationReader,
            _targetLookupResolver,
            _writeFreshnessChecker,
            new RelationalWriteNoProfileMergeSynthesizer(),
            _profileMergeSynthesizer,
            _noProfilePersister,
            _writeExceptionClassifier,
            _writeConstraintResolver,
            _readMaterializer
        );

        var result = await _sut.ExecuteAsync(request);

        result
            .Should()
            .BeEquivalentTo(
                new RelationalWriteExecutorResult.Update(
                    new UpdateResult.UpdateSuccess(
                        new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb")),
                        ExpectedEtag(request)
                    ),
                    RelationalWriteExecutorAttemptOutcome.GuardedNoOp.Instance
                )
            );
        _currentStateLoader.LoadCallCount.Should().Be(1);
        _writeFreshnessChecker.IsCurrentCallCount.Should().Be(1);
        _noProfilePersister.TryPersistCallCount.Should().Be(0);
        _committedRepresentationReader.ReadCallCount.Should().Be(1);
        _writeSessionFactory.Session.CommitCallCount.Should().Be(1);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(0);
    }

    [Test]
    public async Task It_returns_not_exists_when_the_existing_put_target_disappears_before_current_state_load()
    {
        var request = CreateRequest(RelationalWriteOperationKind.Put);
        _currentStateLoader.ReturnMissingTarget = true;

        var result = await _sut.ExecuteAsync(request);

        result
            .Should()
            .BeEquivalentTo(
                new RelationalWriteExecutorResult.Update(new UpdateResult.UpdateFailureNotExists())
            );
        _referenceResolverAdapterFactory.CreateSessionAdapterCallCount.Should().Be(1);
        _writeFlattener.FlattenCallCount.Should().Be(0);
        _currentStateLoader.LoadCallCount.Should().Be(1);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
        _writeSessionFactory.Session.DisposeCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_re_evaluates_post_as_update_as_create_when_the_existing_target_disappears_before_current_state_load()
    {
        var request = CreateRequest(
            RelationalWriteOperationKind.Post,
            targetContext: new RelationalWriteTargetContext.ExistingDocument(
                345L,
                new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb")),
                44L
            )
        );
        var candidateDocumentUuid = (
            (RelationalWriteTargetRequest.Post)request.TargetRequest
        ).CandidateDocumentUuid;
        _targetLookupResolver.PostResults.Enqueue(
            new RelationalWriteTargetLookupResult.CreateNew(candidateDocumentUuid)
        );
        _currentStateLoader.ReturnMissingTarget = true;
        var result = await _sut.ExecuteAsync(request);

        result
            .Should()
            .BeEquivalentTo(
                new RelationalWriteExecutorResult.Upsert(
                    new UpsertResult.InsertSuccess(candidateDocumentUuid, ExpectedEtag(request)),
                    RelationalWriteExecutorAttemptOutcome.AppliedWrite.Instance
                )
            );
        _targetLookupResolver.ResolveForPostCallCount.Should().Be(1);
        _currentStateLoader.LoadCallCount.Should().Be(1);
        _writeFlattener
            .CapturedInput!.TargetContext.Should()
            .BeEquivalentTo(new RelationalWriteTargetContext.CreateNew(candidateDocumentUuid));
        _writeSessionFactory.Session.CommitCallCount.Should().Be(1);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(0);
    }

    [Test]
    public async Task It_reuses_the_same_write_session_when_post_target_re_evaluation_loads_current_state_again()
    {
        var existingDocumentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));
        var request = CreateRequest(
            RelationalWriteOperationKind.Post,
            targetContext: new RelationalWriteTargetContext.ExistingDocument(345L, existingDocumentUuid, 44L)
        );

        _currentStateLoader.QueuedResults.Enqueue(null);
        _targetLookupResolver.PostResults.Enqueue(
            new RelationalWriteTargetLookupResult.ExistingDocument(345L, existingDocumentUuid, 45L)
        );
        _currentStateLoader.QueuedResults.Enqueue(
            new RelationalWriteCurrentState(
                new DocumentMetadataRow(
                    345L,
                    existingDocumentUuid.Value,
                    45L,
                    45L,
                    new DateTimeOffset(2026, 4, 2, 12, 0, 0, TimeSpan.Zero),
                    new DateTimeOffset(2026, 4, 2, 12, 0, 0, TimeSpan.Zero)
                ),
                [
                    new HydratedTableRows(
                        request.WritePlan.Model.Root,
                        [
                            [345L, 255901, "Lincoln High"],
                        ]
                    ),
                ],
                []
            )
        );
        _noProfileMergeSynthesizer.ResultToReturn = CreateMergeResult(
            request.WritePlan.TablePlansInDependencyOrder[0],
            currentSchoolId: 255901,
            mergedSchoolId: 255901,
            currentName: "Lincoln High",
            mergedName: "Lincoln High Updated"
        );

        var result = await _sut.ExecuteAsync(request);

        result
            .Should()
            .BeEquivalentTo(
                new RelationalWriteExecutorResult.Upsert(
                    new UpsertResult.UpdateSuccess(existingDocumentUuid, ExpectedEtag(request)),
                    RelationalWriteExecutorAttemptOutcome.AppliedWrite.Instance
                )
            );
        _currentStateLoader.LoadCallCount.Should().Be(2);
        _currentStateLoader.CapturedRequests.Should().HaveCount(2);
        _currentStateLoader.CapturedRequests[0].TargetContext.ObservedContentVersion.Should().Be(44L);
        _currentStateLoader.CapturedRequests[1].TargetContext.ObservedContentVersion.Should().Be(45L);
        _currentStateLoader.CapturedWriteSessions.Should().HaveCount(2);
        _currentStateLoader
            .CapturedWriteSessions.Should()
            .OnlyContain(writeSession => ReferenceEquals(writeSession, _writeSessionFactory.Session));
        _targetLookupResolver.ResolveForPostCallCount.Should().Be(1);
        _targetLookupResolver.CapturedWriteSession.Should().NotBeNull();
        _currentStateLoader
            .CapturedRequests[1]
            .TargetContext.Should()
            .BeEquivalentTo(
                new RelationalWriteTargetContext.ExistingDocument(345L, existingDocumentUuid, 45L)
            );
        _writeFlattener
            .CapturedInput!.TargetContext.Should()
            .BeEquivalentTo(
                new RelationalWriteTargetContext.ExistingDocument(345L, existingDocumentUuid, 45L)
            );
        _targetLookupResolver
            .CapturedWriteSession!.Connection.Should()
            .BeSameAs(_writeSessionFactory.Session.Connection);
        _targetLookupResolver
            .CapturedWriteSession!.Transaction.Should()
            .BeSameAs(_writeSessionFactory.Session.Transaction);
        _writeSessionFactory.Session.CommitCallCount.Should().Be(1);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(0);
    }

    [Test]
    public async Task It_returns_write_conflict_when_post_target_still_cannot_load_after_re_evaluation()
    {
        var request = CreateRequest(
            RelationalWriteOperationKind.Post,
            targetContext: new RelationalWriteTargetContext.ExistingDocument(
                345L,
                new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb")),
                44L
            )
        );
        var existingDocumentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));
        _targetLookupResolver.PostResults.Enqueue(
            new RelationalWriteTargetLookupResult.ExistingDocument(345L, existingDocumentUuid, 45L)
        );
        _currentStateLoader.ReturnMissingTarget = true;

        var result = await _sut.ExecuteAsync(request);

        result
            .Should()
            .BeEquivalentTo(
                new RelationalWriteExecutorResult.Upsert(new UpsertResult.UpsertFailureWriteConflict())
            );
        _targetLookupResolver.ResolveForPostCallCount.Should().Be(1);
        _currentStateLoader.LoadCallCount.Should().Be(2);
        _writeFlattener.FlattenCallCount.Should().Be(0);
        _writeSessionFactory.Session.CommitCallCount.Should().Be(0);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_returns_a_stale_no_op_compare_outcome_when_guarded_freshness_is_lost()
    {
        var request = CreateRequest(RelationalWriteOperationKind.Put);
        _writeFreshnessChecker.IsCurrentResult = false;

        var result = await _sut.ExecuteAsync(request);

        result
            .Should()
            .BeEquivalentTo(
                new RelationalWriteExecutorResult.Update(
                    new UpdateResult.UpdateFailureWriteConflict(),
                    RelationalWriteExecutorAttemptOutcome.StaleNoOpCompare.Instance
                )
            );
        result.AttemptOutcome.Should().Be(RelationalWriteExecutorAttemptOutcome.StaleNoOpCompare.Instance);
        _currentStateLoader.LoadCallCount.Should().Be(1);
        _noProfileMergeSynthesizer.SynthesizeCallCount.Should().Be(1);
        _noProfilePersister.TryPersistCallCount.Should().Be(0);
        _writeFreshnessChecker.IsCurrentCallCount.Should().Be(1);
        _writeSessionFactory.Session.CommitCallCount.Should().Be(0);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
        _writeSessionFactory.Session.DisposeCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_returns_insert_success_when_non_collection_create_dml_is_applied()
    {
        var request = CreateRequest(RelationalWriteOperationKind.Post);
        var result = await _sut.ExecuteAsync(request);

        result
            .Should()
            .BeEquivalentTo(
                new RelationalWriteExecutorResult.Upsert(
                    new UpsertResult.InsertSuccess(
                        new DocumentUuid(Guid.Parse("cccccccc-1111-2222-3333-dddddddddddd")),
                        ExpectedEtag(request)
                    ),
                    RelationalWriteExecutorAttemptOutcome.AppliedWrite.Instance
                )
            );
        result.AttemptOutcome.Should().Be(RelationalWriteExecutorAttemptOutcome.AppliedWrite.Instance);
        _noProfilePersister.TryPersistCallCount.Should().Be(1);
        _noProfilePersister.CapturedRequest.Should().NotBeNull();
        _noProfilePersister.CapturedRequest!.TargetRequest.Should().BeEquivalentTo(request.TargetRequest);
        _noProfilePersister.CapturedWriteSession.Should().BeSameAs(_writeSessionFactory.Session);
        _writeSessionFactory.Session.CommitCallCount.Should().Be(1);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(0);
        _writeSessionFactory.Session.DisposeCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_rejects_create_persistence_when_the_committed_target_uuid_changes()
    {
        var request = CreateRequest(RelationalWriteOperationKind.Post);
        _noProfilePersister.ResultToReturn = new RelationalWritePersistResult(
            910L,
            new DocumentUuid(Guid.Parse("eeeeeeee-1111-2222-3333-ffffffffffff"))
        );

        var act = () => _sut.ExecuteAsync(request);

        await act.Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("*but persistence returned committed uuid*");
        _noProfilePersister.TryPersistCallCount.Should().Be(1);
        _writeSessionFactory.Session.CommitCallCount.Should().Be(0);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_returns_update_success_when_non_collection_put_dml_is_applied()
    {
        var request = CreateRequest(RelationalWriteOperationKind.Put);
        _noProfileMergeSynthesizer.ResultToReturn = CreateMergeResult(
            request.WritePlan.TablePlansInDependencyOrder[0],
            currentSchoolId: 255901,
            mergedSchoolId: 255901,
            currentName: "Lincoln High",
            mergedName: "Lincoln High Updated"
        );
        var result = await _sut.ExecuteAsync(request);

        result
            .Should()
            .BeEquivalentTo(
                new RelationalWriteExecutorResult.Update(
                    new UpdateResult.UpdateSuccess(
                        new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb")),
                        ExpectedEtag(request)
                    ),
                    RelationalWriteExecutorAttemptOutcome.AppliedWrite.Instance
                )
            );
        result.AttemptOutcome.Should().Be(RelationalWriteExecutorAttemptOutcome.AppliedWrite.Instance);
        _noProfilePersister.TryPersistCallCount.Should().Be(1);
        _noProfilePersister.CapturedRequest.Should().NotBeNull();
        _noProfilePersister.CapturedRequest!.TargetRequest.Should().BeEquivalentTo(request.TargetRequest);
        _noProfilePersister.CapturedWriteSession.Should().BeSameAs(_writeSessionFactory.Session);
        _writeFreshnessChecker.IsCurrentCallCount.Should().Be(0);
        _writeSessionFactory.Session.CommitCallCount.Should().Be(1);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(0);
        _writeSessionFactory.Session.DisposeCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_rejects_post_as_update_persistence_when_the_committed_target_document_id_changes()
    {
        var existingDocumentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));
        var request = CreateRequest(
            RelationalWriteOperationKind.Post,
            targetContext: new RelationalWriteTargetContext.ExistingDocument(345L, existingDocumentUuid, 44L)
        );
        _currentStateLoader.ResultToReturn = CreateCurrentState(request, 45L);
        _noProfileMergeSynthesizer.ResultToReturn = CreateMergeResult(
            request.WritePlan.TablePlansInDependencyOrder[0],
            currentSchoolId: 255901,
            mergedSchoolId: 255901,
            currentName: "Lincoln High",
            mergedName: "Lincoln High Updated"
        );
        _noProfilePersister.ResultToReturn = new RelationalWritePersistResult(999L, existingDocumentUuid);

        var act = () => _sut.ExecuteAsync(request);

        await act.Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("*different committed target identity*");
        _currentStateLoader.LoadCallCount.Should().Be(1);
        _noProfilePersister.TryPersistCallCount.Should().Be(1);
        _writeSessionFactory.Session.CommitCallCount.Should().Be(0);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_rejects_put_persistence_when_the_committed_target_document_id_changes()
    {
        var request = CreateRequest(RelationalWriteOperationKind.Put);
        _noProfileMergeSynthesizer.ResultToReturn = CreateMergeResult(
            request.WritePlan.TablePlansInDependencyOrder[0],
            currentSchoolId: 255901,
            mergedSchoolId: 255901,
            currentName: "Lincoln High",
            mergedName: "Lincoln High Updated"
        );
        _noProfilePersister.ResultToReturn = new RelationalWritePersistResult(
            999L,
            new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"))
        );

        var act = () => _sut.ExecuteAsync(request);

        await act.Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("*different committed target identity*");
        _currentStateLoader.LoadCallCount.Should().Be(1);
        _noProfilePersister.TryPersistCallCount.Should().Be(1);
        _writeSessionFactory.Session.CommitCallCount.Should().Be(0);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_allows_identity_stable_existing_document_writes_to_continue_to_the_pending_executor_path()
    {
        var request = CreateRequest(RelationalWriteOperationKind.Put);
        _noProfileMergeSynthesizer.ResultToReturn = CreateMergeResult(
            request.WritePlan.TablePlansInDependencyOrder[0],
            currentSchoolId: 255901,
            mergedSchoolId: 255901,
            currentName: "Lincoln High",
            mergedName: "Lincoln High Updated"
        );

        var result = await _sut.ExecuteAsync(request);

        result
            .Should()
            .BeEquivalentTo(
                new RelationalWriteExecutorResult.Update(
                    new UpdateResult.UpdateSuccess(
                        new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb")),
                        ExpectedEtag(request)
                    ),
                    RelationalWriteExecutorAttemptOutcome.AppliedWrite.Instance
                )
            );
        result.AttemptOutcome.Should().Be(RelationalWriteExecutorAttemptOutcome.AppliedWrite.Instance);
        _currentStateLoader.LoadCallCount.Should().Be(1);
        _noProfileMergeSynthesizer.SynthesizeCallCount.Should().Be(1);
        _noProfilePersister.TryPersistCallCount.Should().Be(1);
        _writeFreshnessChecker.IsCurrentCallCount.Should().Be(0);
        _writeSessionFactory.Session.CommitCallCount.Should().Be(1);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(0);
        _writeSessionFactory.Session.DisposeCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_rolls_back_when_non_collection_persistence_throws()
    {
        var request = CreateRequest(RelationalWriteOperationKind.Put);
        _noProfileMergeSynthesizer.ResultToReturn = CreateMergeResult(
            request.WritePlan.TablePlansInDependencyOrder[0],
            currentSchoolId: 255901,
            mergedSchoolId: 255901,
            currentName: "Lincoln High",
            mergedName: "Lincoln High Updated"
        );
        _noProfilePersister.ExceptionToThrow = new InvalidOperationException("boom");

        var act = () => _sut.ExecuteAsync(request);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("boom");
        _noProfilePersister.TryPersistCallCount.Should().Be(1);
        _writeSessionFactory.Session.CommitCallCount.Should().Be(0);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
        _writeSessionFactory.Session.DisposeCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_maps_root_natural_key_unique_violations_to_upsert_identity_conflicts()
    {
        var request = CreateRequest(
            RelationalWriteOperationKind.Post,
            selectedBody: JsonNode.Parse("""{"schoolId":255901,"name":"Lincoln High"}""")!
        );
        _noProfileMergeSynthesizer.ResultToReturn = CreateMergeResult(
            request.WritePlan.TablePlansInDependencyOrder[0],
            currentSchoolId: 255901,
            mergedSchoolId: 255901,
            currentName: "Lincoln High",
            mergedName: "Lincoln High Updated"
        );
        _noProfilePersister.ExceptionToThrow = new StubDbException("duplicate key");
        _writeExceptionClassifier.ClassificationToReturn =
            new RelationalWriteExceptionClassification.UniqueConstraintViolation("UK_School_NaturalKey");
        _writeConstraintResolver.ResolutionToReturn =
            new RelationalWriteConstraintResolution.RootNaturalKeyUnique("UK_School_NaturalKey");

        var result = await _sut.ExecuteAsync(request);

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
        _writeExceptionClassifier.TryClassifyCallCount.Should().Be(1);
        _writeConstraintResolver.ResolveCallCount.Should().Be(1);
        _writeSessionFactory.Session.CommitCallCount.Should().Be(0);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_maps_root_natural_key_unique_violations_raised_on_commit_to_update_identity_conflicts()
    {
        var request = CreateRequest(
            RelationalWriteOperationKind.Put,
            selectedBody: JsonNode.Parse("""{"schoolId":255901,"name":"Lincoln High"}""")!
        );
        _noProfileMergeSynthesizer.ResultToReturn = CreateMergeResult(
            request.WritePlan.TablePlansInDependencyOrder[0],
            currentSchoolId: 255901,
            mergedSchoolId: 255901,
            currentName: "Lincoln High",
            mergedName: "Lincoln High Updated"
        );
        _writeSessionFactory.Session.CommitExceptionToThrow = new StubDbException("duplicate key");
        _writeExceptionClassifier.ClassificationToReturn =
            new RelationalWriteExceptionClassification.UniqueConstraintViolation("UK_School_NaturalKey");
        _writeConstraintResolver.ResolutionToReturn =
            new RelationalWriteConstraintResolution.RootNaturalKeyUnique("UK_School_NaturalKey");

        var result = await _sut.ExecuteAsync(request);

        result
            .Should()
            .BeEquivalentTo(
                new RelationalWriteExecutorResult.Update(
                    new UpdateResult.UpdateFailureIdentityConflict(
                        new ResourceName("School"),
                        [new KeyValuePair<string, string>("schoolId", "255901")]
                    )
                )
            );
        _noProfilePersister.TryPersistCallCount.Should().Be(1);
        _writeSessionFactory.Session.CommitCallCount.Should().Be(1);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_maps_known_document_reference_foreign_key_violations_to_reference_failures()
    {
        var invalidReference = RelationalAccessTestData.CreateDocumentReference(
            new ReferentialId(Guid.NewGuid()),
            "$.schoolReference"
        );
        var request = CreateRequest(
            RelationalWriteOperationKind.Post,
            documentReferences: [invalidReference]
        );
        _noProfileMergeSynthesizer.ResultToReturn = CreateMergeResult(
            request.WritePlan.TablePlansInDependencyOrder[0],
            currentSchoolId: 255901,
            mergedSchoolId: 255901,
            currentName: "Lincoln High",
            mergedName: "Lincoln High Updated"
        );
        _noProfilePersister.ExceptionToThrow = new StubDbException("foreign key violation");
        _writeExceptionClassifier.ClassificationToReturn =
            new RelationalWriteExceptionClassification.ForeignKeyConstraintViolation(
                "FK_School_SchoolReference"
            );
        _writeConstraintResolver.ResolutionToReturn =
            new RelationalWriteConstraintResolution.RequestReference(
                "FK_School_SchoolReference",
                RelationalWriteReferenceKind.Document,
                new JsonPathExpression(
                    "$.schoolReference",
                    [new JsonPathSegment.Property("schoolReference")]
                ),
                new QualifiedResourceName(
                    invalidReference.ResourceInfo.ProjectName.Value,
                    invalidReference.ResourceInfo.ResourceName.Value
                )
            );

        var result = await _sut.ExecuteAsync(request);

        result
            .Should()
            .BeEquivalentTo(
                new RelationalWriteExecutorResult.Upsert(
                    new UpsertResult.UpsertFailureReference(
                        [
                            DocumentReferenceFailure.From(
                                invalidReference,
                                DocumentReferenceFailureReason.Missing
                            ),
                        ],
                        []
                    )
                )
            );
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_maps_known_descriptor_reference_foreign_key_violations_to_reference_failures()
    {
        var invalidReference = RelationalAccessTestData.CreateDescriptorReference(
            new ReferentialId(Guid.NewGuid()),
            "uri://ed-fi.org/SchoolTypeDescriptor#Alternative",
            "$.schoolTypeDescriptor"
        );
        var request = CreateRequest(
            RelationalWriteOperationKind.Put,
            descriptorReferences: [invalidReference]
        );
        _noProfileMergeSynthesizer.ResultToReturn = CreateMergeResult(
            request.WritePlan.TablePlansInDependencyOrder[0],
            currentSchoolId: 255901,
            mergedSchoolId: 255901,
            currentName: "Lincoln High",
            mergedName: "Lincoln High Updated"
        );
        _noProfilePersister.ExceptionToThrow = new StubDbException("foreign key violation");
        _writeExceptionClassifier.ClassificationToReturn =
            new RelationalWriteExceptionClassification.ForeignKeyConstraintViolation(
                "FK_School_SchoolTypeDescriptor"
            );
        _writeConstraintResolver.ResolutionToReturn =
            new RelationalWriteConstraintResolution.RequestReference(
                "FK_School_SchoolTypeDescriptor",
                RelationalWriteReferenceKind.Descriptor,
                new JsonPathExpression(
                    "$.schoolTypeDescriptor",
                    [new JsonPathSegment.Property("schoolTypeDescriptor")]
                ),
                new QualifiedResourceName(
                    invalidReference.ResourceInfo.ProjectName.Value,
                    invalidReference.ResourceInfo.ResourceName.Value
                )
            );

        var result = await _sut.ExecuteAsync(request);

        result
            .Should()
            .BeEquivalentTo(
                new RelationalWriteExecutorResult.Update(
                    new UpdateResult.UpdateFailureReference(
                        [],
                        [DescriptorReferenceFailureClassifier.Missing(invalidReference)]
                    )
                )
            );
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_maps_resolved_request_reference_with_no_matching_request_reference_to_unknown_failure()
    {
        // The compiled model resolves the FK to a named request-facing reference path, but the
        // ReferenceResolutionRequest carries no reference at that path (e.g. a race or an assembly
        // mismatch at the middleware tier). The executor cannot produce a reference failure without
        // a concrete DocumentReference/DescriptorReference to attach it to, so it falls through to
        // the Unresolved arm and emits a deterministic UnknownFailure.
        var request = CreateRequest(RelationalWriteOperationKind.Post);
        _noProfileMergeSynthesizer.ResultToReturn = CreateMergeResult(
            request.WritePlan.TablePlansInDependencyOrder[0],
            currentSchoolId: 255901,
            mergedSchoolId: 255901,
            currentName: "Lincoln High",
            mergedName: "Lincoln High Updated"
        );
        _noProfilePersister.ExceptionToThrow = new StubDbException("foreign key violation");
        _writeExceptionClassifier.ClassificationToReturn =
            new RelationalWriteExceptionClassification.ForeignKeyConstraintViolation(
                "FK_School_SchoolRef_2ba9f31f84"
            );
        // Resolver says this FK maps to "$.schoolReference" on School, but the request carries
        // no document references at that path.
        _writeConstraintResolver.ResolutionToReturn =
            new RelationalWriteConstraintResolution.RequestReference(
                "FK_School_SchoolRef_2ba9f31f84",
                RelationalWriteReferenceKind.Document,
                new JsonPathExpression(
                    "$.schoolReference",
                    [new JsonPathSegment.Property("schoolReference")]
                ),
                new QualifiedResourceName("Ed-Fi", "School")
            );

        var result = await _sut.ExecuteAsync(request);

        result
            .Should()
            .BeEquivalentTo(
                new RelationalWriteExecutorResult.Upsert(
                    new UpsertResult.UnknownFailure(
                        "Relational write failed for resource 'Ed-Fi.School' because the database reported a non-user-facing constraint violation."
                    )
                )
            );
        _writeConstraintResolver.ResolveCallCount.Should().Be(1);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_maps_unresolved_constraint_violations_to_unknown_failures()
    {
        var request = CreateRequest(RelationalWriteOperationKind.Put);
        _noProfileMergeSynthesizer.ResultToReturn = CreateMergeResult(
            request.WritePlan.TablePlansInDependencyOrder[0],
            currentSchoolId: 255901,
            mergedSchoolId: 255901,
            currentName: "Lincoln High",
            mergedName: "Lincoln High Updated"
        );
        _noProfilePersister.ExceptionToThrow = new StubDbException("structural constraint violation");
        _writeExceptionClassifier.ClassificationToReturn =
            new RelationalWriteExceptionClassification.ForeignKeyConstraintViolation(
                "FK_School_InternalParent"
            );
        _writeConstraintResolver.ResolutionToReturn = new RelationalWriteConstraintResolution.Unresolved(
            "FK_School_InternalParent"
        );

        var result = await _sut.ExecuteAsync(request);

        result
            .Should()
            .BeEquivalentTo(
                new RelationalWriteExecutorResult.Update(
                    new UpdateResult.UnknownFailure(
                        "Relational write failed for resource 'Ed-Fi.School' because the database reported a non-user-facing constraint violation."
                    )
                )
            );
        _writeConstraintResolver.ResolveCallCount.Should().Be(1);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_maps_unrecognized_final_db_write_failures_to_unknown_failures()
    {
        var request = CreateRequest(RelationalWriteOperationKind.Post);
        _noProfileMergeSynthesizer.ResultToReturn = CreateMergeResult(
            request.WritePlan.TablePlansInDependencyOrder[0],
            currentSchoolId: 255901,
            mergedSchoolId: 255901,
            currentName: "Lincoln High",
            mergedName: "Lincoln High Updated"
        );
        _noProfilePersister.ExceptionToThrow = new StubDbException("provider write failure");
        _writeExceptionClassifier.ClassificationToReturn = RelationalWriteExceptionClassification
            .UnrecognizedWriteFailure
            .Instance;

        var result = await _sut.ExecuteAsync(request);

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
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_rethrows_db_exceptions_that_the_classifier_does_not_claim()
    {
        var request = CreateRequest(RelationalWriteOperationKind.Put);
        _noProfileMergeSynthesizer.ResultToReturn = CreateMergeResult(
            request.WritePlan.TablePlansInDependencyOrder[0],
            currentSchoolId: 255901,
            mergedSchoolId: 255901,
            currentName: "Lincoln High",
            mergedName: "Lincoln High Updated"
        );
        _noProfilePersister.ExceptionToThrow = new StubDbException("deadlock");

        var act = () => _sut.ExecuteAsync(request);

        await act.Should().ThrowAsync<StubDbException>().WithMessage("deadlock");
        _writeExceptionClassifier.TryClassifyCallCount.Should().Be(1);
        _writeConstraintResolver.ResolveCallCount.Should().Be(0);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_rethrows_exception_classifier_failures_during_db_exception_mapping()
    {
        var request = CreateRequest(RelationalWriteOperationKind.Put);
        _noProfileMergeSynthesizer.ResultToReturn = CreateMergeResult(
            request.WritePlan.TablePlansInDependencyOrder[0],
            currentSchoolId: 255901,
            mergedSchoolId: 255901,
            currentName: "Lincoln High",
            mergedName: "Lincoln High Updated"
        );
        _noProfilePersister.ExceptionToThrow = new StubDbException("provider write failure");
        _writeExceptionClassifier.ExceptionToThrow = new InvalidOperationException("classifier bug");

        var act = () => _sut.ExecuteAsync(request);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("classifier bug");
        _writeExceptionClassifier.TryClassifyCallCount.Should().Be(1);
        _writeConstraintResolver.ResolveCallCount.Should().Be(0);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_rethrows_constraint_resolution_failures_during_db_exception_mapping()
    {
        var request = CreateRequest(RelationalWriteOperationKind.Post);
        _noProfileMergeSynthesizer.ResultToReturn = CreateMergeResult(
            request.WritePlan.TablePlansInDependencyOrder[0],
            currentSchoolId: 255901,
            mergedSchoolId: 255901,
            currentName: "Lincoln High",
            mergedName: "Lincoln High Updated"
        );
        _noProfilePersister.ExceptionToThrow = new StubDbException("foreign key violation");
        _writeExceptionClassifier.ClassificationToReturn =
            new RelationalWriteExceptionClassification.ForeignKeyConstraintViolation(
                "FK_School_InternalParent"
            );
        _writeConstraintResolver.ExceptionToThrow = new InvalidOperationException("resolver bug");

        var act = () => _sut.ExecuteAsync(request);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("resolver bug");
        _writeExceptionClassifier.TryClassifyCallCount.Should().Be(1);
        _writeConstraintResolver.ResolveCallCount.Should().Be(1);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_returns_immutable_identity_failure_when_existing_document_identity_changes_and_updates_are_disallowed()
    {
        var request = CreateRequest(RelationalWriteOperationKind.Put);
        _noProfileMergeSynthesizer.ResultToReturn = CreateMergeResult(
            request.WritePlan.TablePlansInDependencyOrder[0],
            currentSchoolId: 255901,
            mergedSchoolId: 255902
        );

        var result = await _sut.ExecuteAsync(request);

        result
            .Should()
            .BeEquivalentTo(
                new RelationalWriteExecutorResult.Update(
                    new UpdateResult.UpdateFailureImmutableIdentity(
                        "Identifying values for the School resource cannot be changed. Delete and recreate the resource item instead."
                    )
                )
            );
        _currentStateLoader.LoadCallCount.Should().Be(1);
        _noProfileMergeSynthesizer.SynthesizeCallCount.Should().Be(1);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
        _writeSessionFactory.Session.DisposeCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_returns_immutable_identity_failure_for_post_as_update_when_existing_document_identity_changes_and_updates_are_disallowed()
    {
        var request = CreateRequest(
            RelationalWriteOperationKind.Post,
            targetContext: new RelationalWriteTargetContext.ExistingDocument(
                345L,
                new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb")),
                44L
            )
        );
        _currentStateLoader.ResultToReturn = new RelationalWriteCurrentState(
            new DocumentMetadataRow(
                345L,
                Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"),
                44L,
                44L,
                new DateTimeOffset(2026, 4, 2, 12, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 4, 2, 12, 0, 0, TimeSpan.Zero)
            ),
            [
                new HydratedTableRows(
                    request.WritePlan.Model.Root,
                    [
                        [345L, 255901, "Lincoln High"],
                    ]
                ),
            ],
            []
        );
        _noProfileMergeSynthesizer.ResultToReturn = CreateMergeResult(
            request.WritePlan.TablePlansInDependencyOrder[0],
            currentSchoolId: 255901,
            mergedSchoolId: 255902
        );

        var result = await _sut.ExecuteAsync(request);

        result
            .Should()
            .BeEquivalentTo(
                new RelationalWriteExecutorResult.Upsert(
                    new UpsertResult.UpsertFailureImmutableIdentity(
                        "Identifying values for the School resource cannot be changed. Delete and recreate the resource item instead."
                    )
                )
            );
        _currentStateLoader.LoadCallCount.Should().Be(1);
        _noProfileMergeSynthesizer.SynthesizeCallCount.Should().Be(1);
        _noProfilePersister.TryPersistCallCount.Should().Be(0);
        _writeSessionFactory.Session.CommitCallCount.Should().Be(0);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
        _writeSessionFactory.Session.DisposeCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_returns_not_yet_supported_failure_when_existing_document_identity_changes_and_updates_are_allowed()
    {
        var request = CreateRequest(RelationalWriteOperationKind.Put, allowIdentityUpdates: true);
        _noProfileMergeSynthesizer.ResultToReturn = CreateMergeResult(
            request.WritePlan.TablePlansInDependencyOrder[0],
            currentSchoolId: 255901,
            mergedSchoolId: 255902
        );

        var result = await _sut.ExecuteAsync(request);

        result
            .Should()
            .BeEquivalentTo(
                new RelationalWriteExecutorResult.Update(
                    new UpdateResult.UnknownFailure(
                        "Relational existing-document writes do not yet support identity-changing operations for resource 'Ed-Fi.School' when allowIdentityUpdates=true. "
                            + "Keep the identity projection stable until the strict identity-maintenance work lands."
                    )
                )
            );
        _currentStateLoader.LoadCallCount.Should().Be(1);
        _noProfileMergeSynthesizer.SynthesizeCallCount.Should().Be(1);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
        _writeSessionFactory.Session.DisposeCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_maps_reference_derived_scalar_validation_failures_for_post_requests()
    {
        var request = CreateRequest(RelationalWriteOperationKind.Post);
        var validationFailure = new WriteValidationFailure(
            new JsonPath("$.schoolReference.schoolYear"),
            "Column 'School_RefSchoolYear' on table 'edfi.ProgramReferenceDerived' expected scalar kind 'Int32' at path '$.schoolReference.schoolYear', but resolved reference-derived raw value 'not-a-number' could not be converted."
        );
        _writeFlattener.ExceptionToThrow = new RelationalWriteRequestValidationException([validationFailure]);

        var result = await _sut.ExecuteAsync(request);

        result
            .Should()
            .BeEquivalentTo(
                new RelationalWriteExecutorResult.Upsert(
                    new UpsertResult.UpsertFailureValidation([validationFailure])
                )
            );
        _referenceResolverAdapterFactory.CreateSessionAdapterCallCount.Should().Be(1);
        _writeFlattener.FlattenCallCount.Should().Be(1);
        _currentStateLoader.LoadCallCount.Should().Be(0);
        _noProfileMergeSynthesizer.SynthesizeCallCount.Should().Be(0);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
        _writeSessionFactory.Session.DisposeCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_maps_nested_reference_derived_scalar_validation_failures_for_put_requests()
    {
        var request = CreateRequest(RelationalWriteOperationKind.Put);
        var validationFailure = new WriteValidationFailure(
            new JsonPath("$.addresses[0].periods[0].schoolReference.active"),
            "Column 'School_RefIsActive' on table 'edfi.StudentNestedReferenceDerivedPeriod' expected scalar kind 'Boolean' at path '$.addresses[0].periods[0].schoolReference.active', but resolved reference-derived raw value 'not-a-bool' could not be converted."
        );
        _writeFlattener.ExceptionToThrow = new RelationalWriteRequestValidationException([validationFailure]);

        var result = await _sut.ExecuteAsync(request);

        result
            .Should()
            .BeEquivalentTo(
                new RelationalWriteExecutorResult.Update(
                    new UpdateResult.UpdateFailureValidation([validationFailure])
                )
            );
        _referenceResolverAdapterFactory.CreateSessionAdapterCallCount.Should().Be(1);
        _writeFlattener.FlattenCallCount.Should().Be(1);
        _currentStateLoader.LoadCallCount.Should().Be(1);
        _noProfileMergeSynthesizer.SynthesizeCallCount.Should().Be(0);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
        _writeSessionFactory.Session.DisposeCallCount.Should().Be(1);
    }

    [Test]
    public void It_requires_target_requests_to_match_operation_kind()
    {
        var writePlan = CreateRootPlan();
        var resourceModel = CreateRelationalResourceModel(writePlan.TableModel);
        var resourceWritePlan = new ResourceWritePlan(resourceModel, [writePlan]);
        var mappingSet = CreateMappingSet(resourceModel);

        var act = () =>
            new RelationalWriteExecutorRequest(
                mappingSet,
                RelationalWriteOperationKind.Put,
                new RelationalWriteTargetRequest.Post(
                    new ReferentialId(Guid.NewGuid()),
                    new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"))
                ),
                resourceWritePlan,
                CreateReadPlan(resourceModel),
                JsonNode.Parse("""{"name":"Lincoln High"}""")!,
                false,
                new TraceId("write-executor-test"),
                new ReferenceResolverRequest(mappingSet, resourceWritePlan.Model.Resource, [], []),
                targetContext: new RelationalWriteTargetContext.ExistingDocument(
                    345L,
                    new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb")),
                    44L
                )
            );

        act.Should().Throw<ArgumentException>().WithParameterName("targetRequest");
    }

    [Test]
    public async Task It_passes_fence_for_root_attached_SeparateTableNonCollection_create_new()
    {
        // Slice 3 widens the fence: root-attached separate-table scopes
        // (DbTableKind.RootExtension) are allowed to reach the profile merge synthesizer.
        var writableBody = JsonNode.Parse("""{"schoolId":255901}""")!;
        var rootPlan = CreateRootPlan();
        var extensionTableModel = AdapterFactoryTestFixtures.BuildRootExtensionTableModel();
        var extensionPlan = AdapterFactoryTestFixtures.BuildRootExtensionTableWritePlan(extensionTableModel);
        var resourceModel = new RelationalResourceModel(
            Resource: new QualifiedResourceName("Ed-Fi", "School"),
            PhysicalSchema: new DbSchemaName("edfi"),
            StorageKind: ResourceStorageKind.RelationalTables,
            Root: rootPlan.TableModel,
            TablesInDependencyOrder: [rootPlan.TableModel, extensionTableModel],
            DocumentReferenceBindings: [],
            DescriptorEdgeSources: []
        );
        var resourceWritePlan = new ResourceWritePlan(resourceModel, [rootPlan, extensionPlan]);
        var scopeCatalog = CompiledScopeAdapterFactory.BuildFromWritePlan(resourceWritePlan);
        var profileContext = new BackendProfileWriteContext(
            Request: new ProfileAppliedWriteRequest(
                WritableRequestBody: writableBody,
                RootResourceCreatable: true,
                RequestScopeStates:
                [
                    new RequestScopeState(
                        Address: new ScopeInstanceAddress("$", []),
                        Visibility: ProfileVisibilityKind.VisiblePresent,
                        Creatable: true
                    ),
                    new RequestScopeState(
                        Address: new ScopeInstanceAddress("$._ext.sample", []),
                        Visibility: ProfileVisibilityKind.VisiblePresent,
                        Creatable: true
                    ),
                ],
                VisibleRequestCollectionItems: []
            ),
            ProfileName: "test-write-profile",
            CompiledScopeCatalog: scopeCatalog,
            StoredStateProjectionInvoker: A.Fake<IStoredStateProjectionInvoker>()
        );

        // Multi-table plan: flattener's default .Single() fallback would throw, so
        // pre-configure a root-only FlattenedWriteSet shape (the profile synthesizer
        // only consumes the root row).
        _writeFlattener.ResultToReturn = new FlattenedWriteSet(
            new RootWriteRowBuffer(
                rootPlan,
                [
                    FlattenedWriteValue.UnresolvedRootDocumentId.Instance,
                    new FlattenedWriteValue.Literal(255901),
                    new FlattenedWriteValue.Literal("Lincoln High"),
                ]
            )
        );

        var baseRequest = CreateRequest(RelationalWriteOperationKind.Post, selectedBody: writableBody);
        var request = baseRequest with
        {
            WritePlan = resourceWritePlan,
            ProfileWriteContext = profileContext,
        };

        var result = await _sut.ExecuteAsync(request);

        _writeFlattener
            .FlattenCallCount.Should()
            .Be(1, "root-attached separate-table plans must flatten once the fence widens");
        _profileMergeSynthesizer
            .SynthesizeCallCount.Should()
            .Be(1, "profile merge must run for root-attached SeparateTableNonCollection plans");
        _profileMergeSynthesizer.CapturedRequest.Should().NotBeNull();
        _profileMergeSynthesizer.CapturedRequest!.WritePlan.Should().BeSameAs(resourceWritePlan);
        _noProfileMergeSynthesizer
            .SynthesizeCallCount.Should()
            .Be(0, "no-profile merge must not run when profile context is present");
        _noProfilePersister
            .TryPersistCallCount.Should()
            .Be(1, "persister must receive the profile merge result once fence passes");
        _writeSessionFactory.Session.CommitCallCount.Should().Be(1);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(0);

        var upsertResult = result.Should().BeOfType<RelationalWriteExecutorResult.Upsert>().Subject;
        upsertResult.Result.Should().BeOfType<UpsertResult.InsertSuccess>();
    }

    [Test]
    public async Task It_fences_profiled_create_new_for_collection_aligned_SeparateTableNonCollection()
    {
        // Slice 3 keeps the fence for collection-aligned separate-table scopes
        // (DbTableKind.CollectionExtensionScope, e.g. $.addresses[*]._ext.sample).
        // Required family still classifies as SeparateTableNonCollection, but the
        // executor checks the request's exercised scopes against their owner table's
        // kind — fencing only when an exercised scope's owner is CollectionExtensionScope.
        var writableBody = JsonNode.Parse("""{"schoolId":255901}""")!;
        // Slim helper-based plans keep the topology shape minimal; slice classification
        // only reads table kinds + JSON scopes, not row content.
        var rootPlan = FenceTestPlans.RootTablePlan();
        var collectionScopePlan = FenceTestPlans.CreateTablePlan(
            "$.addresses[*]._ext.sample",
            "AddressesExtSample",
            DbTableKind.CollectionExtensionScope
        );
        var resourceModel = new RelationalResourceModel(
            Resource: new QualifiedResourceName("Ed-Fi", "School"),
            PhysicalSchema: new DbSchemaName("edfi"),
            StorageKind: ResourceStorageKind.RelationalTables,
            Root: rootPlan.TableModel,
            TablesInDependencyOrder: [rootPlan.TableModel, collectionScopePlan.TableModel],
            DocumentReferenceBindings: [],
            DescriptorEdgeSources: []
        );
        var resourceWritePlan = new ResourceWritePlan(resourceModel, [rootPlan, collectionScopePlan]);
        var scopeCatalog = CompiledScopeAdapterFactory.BuildFromWritePlan(resourceWritePlan);
        var profileContext = new BackendProfileWriteContext(
            Request: new ProfileAppliedWriteRequest(
                WritableRequestBody: writableBody,
                RootResourceCreatable: true,
                RequestScopeStates:
                [
                    new RequestScopeState(
                        Address: new ScopeInstanceAddress("$", []),
                        Visibility: ProfileVisibilityKind.VisiblePresent,
                        Creatable: true
                    ),
                    new RequestScopeState(
                        Address: new ScopeInstanceAddress("$.addresses[*]._ext.sample", []),
                        Visibility: ProfileVisibilityKind.VisibleAbsent,
                        Creatable: true
                    ),
                ],
                VisibleRequestCollectionItems: []
            ),
            ProfileName: "test-write-profile",
            CompiledScopeCatalog: scopeCatalog,
            StoredStateProjectionInvoker: A.Fake<IStoredStateProjectionInvoker>()
        );

        var baseRequest = CreateRequest(RelationalWriteOperationKind.Post, selectedBody: writableBody);
        var request = baseRequest with
        {
            WritePlan = resourceWritePlan,
            ProfileWriteContext = profileContext,
        };

        var result = await _sut.ExecuteAsync(request);

        _writeFlattener
            .FlattenCallCount.Should()
            .Be(0, "flattener must not be called — collection-aligned scopes stay fenced");
        _profileMergeSynthesizer
            .SynthesizeCallCount.Should()
            .Be(0, "profile merge must not run for collection-aligned SeparateTableNonCollection");
        _noProfilePersister.TryPersistCallCount.Should().Be(0);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
        _writeSessionFactory.Session.CommitCallCount.Should().Be(0);

        var upsertResult = result.Should().BeOfType<RelationalWriteExecutorResult.Upsert>().Subject;
        upsertResult
            .Result.Should()
            .BeOfType<UpsertResult.UnknownFailure>()
            .Which.FailureMessage.Should()
            .Contain("SeparateTableNonCollection");
    }

    // Per-family fence tests for TopLevelCollection and NestedAndExtensionCollections
    // are covered at the classifier level by ProfileSliceFenceClassifierTests
    // (e.g. Given_ProfileSliceFenceClassifier_with_top_level_collection_in_request,
    // Given_ProfileSliceFenceClassifier_with_nested_collection_in_request). At the
    // executor level the fence is a single switch expression whose default branch
    // (`_ => false`) covers all families other than RootTableOnly /
    // SeparateTableNonCollection — so the CollectionExtensionScope fence test above
    // and the classifier tests together give full coverage without requiring the
    // full contract-validator plumbing for VisibleRequestCollectionItem round-trips.

    [Test]
    public async Task Given_Executor_fence_passes_for_mixed_plan_when_request_only_exercises_root_attached_scope()
    {
        // Mixed plan: Root + RootExtension + CollectionExtensionScope. The current
        // profiled request only exercises the root-attached $._ext.sample scope; the
        // collection-aligned scope is in the plan but unused for this request. Fence
        // must PASS — Task 5 explicitly supports mixed plans whose exercised scopes are
        // all in-slice. The previous plan-wide fence was too coarse and rejected these.
        var writableBody = JsonNode.Parse("""{"schoolId":255901}""")!;
        var rootPlan = CreateRootPlan();
        var extensionTableModel = AdapterFactoryTestFixtures.BuildRootExtensionTableModel();
        var extensionPlan = AdapterFactoryTestFixtures.BuildRootExtensionTableWritePlan(extensionTableModel);
        var collectionScopePlan = FenceTestPlans.CreateTablePlan(
            "$.addresses[*]._ext.sample",
            "AddressesExtSample",
            DbTableKind.CollectionExtensionScope
        );
        var resourceModel = new RelationalResourceModel(
            Resource: new QualifiedResourceName("Ed-Fi", "School"),
            PhysicalSchema: new DbSchemaName("edfi"),
            StorageKind: ResourceStorageKind.RelationalTables,
            Root: rootPlan.TableModel,
            TablesInDependencyOrder:
            [
                rootPlan.TableModel,
                extensionTableModel,
                collectionScopePlan.TableModel,
            ],
            DocumentReferenceBindings: [],
            DescriptorEdgeSources: []
        );
        var resourceWritePlan = new ResourceWritePlan(
            resourceModel,
            [rootPlan, extensionPlan, collectionScopePlan]
        );
        var scopeCatalog = CompiledScopeAdapterFactory.BuildFromWritePlan(resourceWritePlan);
        var profileContext = new BackendProfileWriteContext(
            Request: new ProfileAppliedWriteRequest(
                WritableRequestBody: writableBody,
                RootResourceCreatable: true,
                RequestScopeStates:
                [
                    new RequestScopeState(
                        Address: new ScopeInstanceAddress("$", []),
                        Visibility: ProfileVisibilityKind.VisiblePresent,
                        Creatable: true
                    ),
                    new RequestScopeState(
                        Address: new ScopeInstanceAddress("$._ext.sample", []),
                        Visibility: ProfileVisibilityKind.VisiblePresent,
                        Creatable: true
                    ),
                ],
                VisibleRequestCollectionItems: []
            ),
            ProfileName: "test-write-profile",
            CompiledScopeCatalog: scopeCatalog,
            StoredStateProjectionInvoker: A.Fake<IStoredStateProjectionInvoker>()
        );

        // Pre-configure a root-only flattened write set: profile synthesizer consumes the
        // root row, and the fence gate runs before flattening in the executor.
        _writeFlattener.ResultToReturn = new FlattenedWriteSet(
            new RootWriteRowBuffer(
                rootPlan,
                [
                    FlattenedWriteValue.UnresolvedRootDocumentId.Instance,
                    new FlattenedWriteValue.Literal(255901),
                    new FlattenedWriteValue.Literal("Lincoln High"),
                ]
            )
        );

        var baseRequest = CreateRequest(RelationalWriteOperationKind.Post, selectedBody: writableBody);
        var request = baseRequest with
        {
            WritePlan = resourceWritePlan,
            ProfileWriteContext = profileContext,
        };

        var result = await _sut.ExecuteAsync(request);

        _writeFlattener
            .FlattenCallCount.Should()
            .Be(1, "mixed plans with only in-slice exercised scopes must reach flattening");
        _profileMergeSynthesizer
            .SynthesizeCallCount.Should()
            .Be(1, "profile merge must run when the unused collection-aligned table is not exercised");
        _noProfilePersister
            .TryPersistCallCount.Should()
            .Be(1, "persister must receive the profile merge result once fence passes");
        _writeSessionFactory.Session.CommitCallCount.Should().Be(1);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(0);

        var upsertResult = result.Should().BeOfType<RelationalWriteExecutorResult.Upsert>().Subject;
        upsertResult.Result.Should().BeOfType<UpsertResult.InsertSuccess>();
    }

    [Test]
    public async Task Given_Executor_fence_fences_when_request_exercises_collection_aligned_scope_in_mixed_plan()
    {
        // Same mixed plan shape (Root + RootExtension + CollectionExtensionScope), but
        // this time the request exercises the collection-aligned scope. Fence must FAIL
        // even though a supported root-attached scope is also in the plan — the exercised
        // collection-aligned scope is what the fence now targets.
        var writableBody = JsonNode.Parse("""{"schoolId":255901}""")!;
        var rootPlan = CreateRootPlan();
        var extensionTableModel = AdapterFactoryTestFixtures.BuildRootExtensionTableModel();
        var extensionPlan = AdapterFactoryTestFixtures.BuildRootExtensionTableWritePlan(extensionTableModel);
        var collectionScopePlan = FenceTestPlans.CreateTablePlan(
            "$.addresses[*]._ext.sample",
            "AddressesExtSample",
            DbTableKind.CollectionExtensionScope
        );
        var resourceModel = new RelationalResourceModel(
            Resource: new QualifiedResourceName("Ed-Fi", "School"),
            PhysicalSchema: new DbSchemaName("edfi"),
            StorageKind: ResourceStorageKind.RelationalTables,
            Root: rootPlan.TableModel,
            TablesInDependencyOrder:
            [
                rootPlan.TableModel,
                extensionTableModel,
                collectionScopePlan.TableModel,
            ],
            DocumentReferenceBindings: [],
            DescriptorEdgeSources: []
        );
        var resourceWritePlan = new ResourceWritePlan(
            resourceModel,
            [rootPlan, extensionPlan, collectionScopePlan]
        );
        var scopeCatalog = CompiledScopeAdapterFactory.BuildFromWritePlan(resourceWritePlan);
        var profileContext = new BackendProfileWriteContext(
            Request: new ProfileAppliedWriteRequest(
                WritableRequestBody: writableBody,
                RootResourceCreatable: true,
                RequestScopeStates:
                [
                    new RequestScopeState(
                        Address: new ScopeInstanceAddress("$", []),
                        Visibility: ProfileVisibilityKind.VisiblePresent,
                        Creatable: true
                    ),
                    new RequestScopeState(
                        Address: new ScopeInstanceAddress("$._ext.sample", []),
                        Visibility: ProfileVisibilityKind.VisiblePresent,
                        Creatable: true
                    ),
                    new RequestScopeState(
                        Address: new ScopeInstanceAddress("$.addresses[*]._ext.sample", []),
                        Visibility: ProfileVisibilityKind.VisibleAbsent,
                        Creatable: true
                    ),
                ],
                VisibleRequestCollectionItems: []
            ),
            ProfileName: "test-write-profile",
            CompiledScopeCatalog: scopeCatalog,
            StoredStateProjectionInvoker: A.Fake<IStoredStateProjectionInvoker>()
        );

        var baseRequest = CreateRequest(RelationalWriteOperationKind.Post, selectedBody: writableBody);
        var request = baseRequest with
        {
            WritePlan = resourceWritePlan,
            ProfileWriteContext = profileContext,
        };

        var result = await _sut.ExecuteAsync(request);

        _writeFlattener
            .FlattenCallCount.Should()
            .Be(0, "fence must fire before flattening when a collection-aligned scope is exercised");
        _profileMergeSynthesizer
            .SynthesizeCallCount.Should()
            .Be(0, "profile merge must not run when the exercised scope is collection-aligned");
        _noProfilePersister.TryPersistCallCount.Should().Be(0);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
        _writeSessionFactory.Session.CommitCallCount.Should().Be(0);

        var upsertResult = result.Should().BeOfType<RelationalWriteExecutorResult.Upsert>().Subject;
        upsertResult
            .Result.Should()
            .BeOfType<UpsertResult.UnknownFailure>()
            .Which.FailureMessage.Should()
            .Contain("SeparateTableNonCollection");
    }

    [Test]
    public async Task Given_Executor_fence_passes_for_mixed_plan_when_collection_aligned_scope_is_only_a_hidden_request_scope()
    {
        // Same mixed plan shape (Root + RootExtension + CollectionExtensionScope). The
        // request exercises the visible root-attached $._ext.sample scope AND also carries
        // a Hidden request scope state for $.addresses[*]._ext.sample. Per
        // ProfileSliceFenceClassifier.ClassifyForCreateNew, hidden request-side scopes are
        // preserve-only and do NOT escalate the slice family — required family is still
        // SeparateTableNonCollection (driven by the visible root-attached scope). The fence
        // must PASS: hidden collection-aligned request scopes must not count as exercised,
        // matching the classifier's visibility rule.
        var writableBody = JsonNode.Parse("""{"schoolId":255901}""")!;
        var rootPlan = CreateRootPlan();
        var extensionTableModel = AdapterFactoryTestFixtures.BuildRootExtensionTableModel();
        var extensionPlan = AdapterFactoryTestFixtures.BuildRootExtensionTableWritePlan(extensionTableModel);
        var collectionScopePlan = FenceTestPlans.CreateTablePlan(
            "$.addresses[*]._ext.sample",
            "AddressesExtSample",
            DbTableKind.CollectionExtensionScope
        );
        var resourceModel = new RelationalResourceModel(
            Resource: new QualifiedResourceName("Ed-Fi", "School"),
            PhysicalSchema: new DbSchemaName("edfi"),
            StorageKind: ResourceStorageKind.RelationalTables,
            Root: rootPlan.TableModel,
            TablesInDependencyOrder:
            [
                rootPlan.TableModel,
                extensionTableModel,
                collectionScopePlan.TableModel,
            ],
            DocumentReferenceBindings: [],
            DescriptorEdgeSources: []
        );
        var resourceWritePlan = new ResourceWritePlan(
            resourceModel,
            [rootPlan, extensionPlan, collectionScopePlan]
        );
        var scopeCatalog = CompiledScopeAdapterFactory.BuildFromWritePlan(resourceWritePlan);
        var profileContext = new BackendProfileWriteContext(
            Request: new ProfileAppliedWriteRequest(
                WritableRequestBody: writableBody,
                RootResourceCreatable: true,
                RequestScopeStates:
                [
                    new RequestScopeState(
                        Address: new ScopeInstanceAddress("$", []),
                        Visibility: ProfileVisibilityKind.VisiblePresent,
                        Creatable: true
                    ),
                    new RequestScopeState(
                        Address: new ScopeInstanceAddress("$._ext.sample", []),
                        Visibility: ProfileVisibilityKind.VisiblePresent,
                        Creatable: true
                    ),
                    new RequestScopeState(
                        Address: new ScopeInstanceAddress("$.addresses[*]._ext.sample", []),
                        Visibility: ProfileVisibilityKind.Hidden,
                        Creatable: true
                    ),
                ],
                VisibleRequestCollectionItems: []
            ),
            ProfileName: "test-write-profile",
            CompiledScopeCatalog: scopeCatalog,
            StoredStateProjectionInvoker: A.Fake<IStoredStateProjectionInvoker>()
        );

        // Pre-configure a root-only flattened write set: profile synthesizer consumes the
        // root row, and the fence gate runs before flattening in the executor.
        _writeFlattener.ResultToReturn = new FlattenedWriteSet(
            new RootWriteRowBuffer(
                rootPlan,
                [
                    FlattenedWriteValue.UnresolvedRootDocumentId.Instance,
                    new FlattenedWriteValue.Literal(255901),
                    new FlattenedWriteValue.Literal("Lincoln High"),
                ]
            )
        );

        var baseRequest = CreateRequest(RelationalWriteOperationKind.Post, selectedBody: writableBody);
        var request = baseRequest with
        {
            WritePlan = resourceWritePlan,
            ProfileWriteContext = profileContext,
        };

        var result = await _sut.ExecuteAsync(request);

        _writeFlattener
            .FlattenCallCount.Should()
            .Be(1, "hidden collection-aligned request scopes must not trigger the fence");
        _profileMergeSynthesizer
            .SynthesizeCallCount.Should()
            .Be(1, "profile merge must run when the collection-aligned scope is only hidden on request");
        _noProfilePersister
            .TryPersistCallCount.Should()
            .Be(1, "persister must receive the profile merge result once fence passes");
        _writeSessionFactory.Session.CommitCallCount.Should().Be(1);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(0);

        var upsertResult = result.Should().BeOfType<RelationalWriteExecutorResult.Upsert>().Subject;
        upsertResult.Result.Should().BeOfType<UpsertResult.InsertSuccess>();
    }

    [Test]
    public async Task It_rejects_profiled_create_new_when_root_is_not_creatable()
    {
        var writableBody = JsonNode.Parse("""{"schoolId":255901}""")!;
        var rootPlan = CreateRootPlan();
        var resourceModel = CreateRelationalResourceModel(rootPlan.TableModel);
        var resourceWritePlan = new ResourceWritePlan(resourceModel, [rootPlan]);
        var scopeCatalog = CompiledScopeAdapterFactory.BuildFromWritePlan(resourceWritePlan);
        var profileContext = new BackendProfileWriteContext(
            Request: new ProfileAppliedWriteRequest(
                WritableRequestBody: writableBody,
                RootResourceCreatable: false,
                RequestScopeStates:
                [
                    new RequestScopeState(
                        Address: new ScopeInstanceAddress("$", []),
                        Visibility: ProfileVisibilityKind.VisiblePresent,
                        Creatable: false
                    ),
                ],
                VisibleRequestCollectionItems: []
            ),
            ProfileName: "test-write-profile",
            CompiledScopeCatalog: scopeCatalog,
            StoredStateProjectionInvoker: A.Fake<IStoredStateProjectionInvoker>()
        );

        var request = CreateRequest(RelationalWriteOperationKind.Post, selectedBody: writableBody) with
        {
            ProfileWriteContext = profileContext,
        };

        var result = await _sut.ExecuteAsync(request);

        _writeFlattener.FlattenCallCount.Should().Be(0, "flattener must not be called");
        _readMaterializer.MaterializeCallCount.Should().Be(0, "materializer must not be called");
        _currentStateLoader.LoadCallCount.Should().Be(0, "current-state must not be loaded for create-new");
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
        _writeSessionFactory.Session.CommitCallCount.Should().Be(0);

        var upsertResult = result.Should().BeOfType<RelationalWriteExecutorResult.Upsert>().Subject;
        upsertResult
            .Result.Should()
            .BeOfType<UpsertResult.UpsertFailureProfileDataPolicy>()
            .Which.ProfileName.Should()
            .Be("test-write-profile");
    }

    [Test]
    public async Task It_passes_fence_for_root_attached_SeparateTableNonCollection_put_existing_document()
    {
        // Slice 3: widened fence lets profiled PUT requests with root-attached
        // separate-table scopes reach the synthesizer after stored-state projection.
        var writableBody = JsonNode.Parse("""{"schoolId":255901}""")!;
        var rootPlan = CreateRootPlan();
        var extensionTableModel = AdapterFactoryTestFixtures.BuildRootExtensionTableModel();
        var extensionPlan = AdapterFactoryTestFixtures.BuildRootExtensionTableWritePlan(extensionTableModel);
        var resourceModel = new RelationalResourceModel(
            Resource: new QualifiedResourceName("Ed-Fi", "School"),
            PhysicalSchema: new DbSchemaName("edfi"),
            StorageKind: ResourceStorageKind.RelationalTables,
            Root: rootPlan.TableModel,
            TablesInDependencyOrder: [rootPlan.TableModel, extensionTableModel],
            DocumentReferenceBindings: [],
            DescriptorEdgeSources: []
        );
        var resourceWritePlan = new ResourceWritePlan(resourceModel, [rootPlan, extensionPlan]);
        var scopeCatalog = CompiledScopeAdapterFactory.BuildFromWritePlan(resourceWritePlan);

        var storedStateProjectionInvoker = A.Fake<IStoredStateProjectionInvoker>();
        var profileRequest = new ProfileAppliedWriteRequest(
            WritableRequestBody: writableBody,
            RootResourceCreatable: true,
            RequestScopeStates:
            [
                new RequestScopeState(
                    Address: new ScopeInstanceAddress("$", []),
                    Visibility: ProfileVisibilityKind.VisiblePresent,
                    Creatable: true
                ),
                new RequestScopeState(
                    Address: new ScopeInstanceAddress("$._ext.sample", []),
                    Visibility: ProfileVisibilityKind.VisiblePresent,
                    Creatable: true
                ),
            ],
            VisibleRequestCollectionItems: []
        );
        var projectedWritableBody = writableBody.DeepClone();
        var projectedProfileRequest = new ProfileAppliedWriteRequest(
            WritableRequestBody: projectedWritableBody,
            RootResourceCreatable: true,
            RequestScopeStates:
            [
                new RequestScopeState(
                    Address: new ScopeInstanceAddress("$", []),
                    Visibility: ProfileVisibilityKind.VisiblePresent,
                    Creatable: true
                ),
                new RequestScopeState(
                    Address: new ScopeInstanceAddress("$._ext.sample", []),
                    Visibility: ProfileVisibilityKind.VisiblePresent,
                    Creatable: true
                ),
            ],
            VisibleRequestCollectionItems: []
        );
        var expectedAppliedWriteContext = new ProfileAppliedWriteContext(
            Request: projectedProfileRequest,
            VisibleStoredBody: JsonNode.Parse("""{"schoolId":255901}""")!,
            StoredScopeStates:
            [
                new StoredScopeState(
                    Address: new ScopeInstanceAddress("$", []),
                    Visibility: ProfileVisibilityKind.VisiblePresent,
                    HiddenMemberPaths: []
                ),
                new StoredScopeState(
                    Address: new ScopeInstanceAddress("$._ext.sample", []),
                    Visibility: ProfileVisibilityKind.VisiblePresent,
                    HiddenMemberPaths: []
                ),
            ],
            VisibleStoredCollectionRows: []
        );

        A.CallTo(() =>
                storedStateProjectionInvoker.ProjectStoredState(
                    A<JsonNode>._,
                    A<ProfileAppliedWriteRequest>._,
                    A<IReadOnlyList<CompiledScopeDescriptor>>._
                )
            )
            .Returns(expectedAppliedWriteContext);

        var profileContext = new BackendProfileWriteContext(
            Request: profileRequest,
            ProfileName: "test-write-profile",
            CompiledScopeCatalog: scopeCatalog,
            StoredStateProjectionInvoker: storedStateProjectionInvoker
        );

        // Seed current state so the existing-document path loads without re-evaluating.
        var existingTargetContext = new RelationalWriteTargetContext.ExistingDocument(
            345L,
            new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb")),
            44L
        );
        _currentStateLoader.ResultToReturn = CreateCurrentState(
            CreateRequest(
                RelationalWriteOperationKind.Put,
                selectedBody: writableBody,
                targetContext: existingTargetContext
            ),
            contentVersion: 44L
        );

        // Multi-table plan: pre-configure a root-only flattened write set.
        _writeFlattener.ResultToReturn = new FlattenedWriteSet(
            new RootWriteRowBuffer(
                rootPlan,
                [
                    new FlattenedWriteValue.Literal(345L),
                    new FlattenedWriteValue.Literal(255901),
                    new FlattenedWriteValue.Literal("Lincoln High"),
                ]
            )
        );

        // Provide a merge result with a current root row so the identity stability guard
        // can verify the targeted document persists without rekeying.
        _profileMergeSynthesizer.ResultToReturn = new RelationalWriteMergeResult(
            [
                new RelationalWriteMergedTableState(
                    rootPlan,
                    [CreateRootTableRow(345L, 255901, "Lincoln High")],
                    [CreateRootTableRow(345L, 255901, "Lincoln High")]
                ),
            ],
            supportsGuardedNoOp: false
        );

        var baseRequest = CreateRequest(
            RelationalWriteOperationKind.Put,
            selectedBody: writableBody,
            targetContext: existingTargetContext
        );
        var request = baseRequest with
        {
            WritePlan = resourceWritePlan,
            ProfileWriteContext = profileContext,
        };

        var result = await _sut.ExecuteAsync(request);

        _readMaterializer
            .MaterializeCallCount.Should()
            .Be(1, "materializer must be called for existing-document reconstitution");
        A.CallTo(() =>
                storedStateProjectionInvoker.ProjectStoredState(
                    A<JsonNode>._,
                    A<ProfileAppliedWriteRequest>._,
                    A<IReadOnlyList<CompiledScopeDescriptor>>._
                )
            )
            .MustHaveHappenedOnceExactly();
        _writeFlattener
            .FlattenCallCount.Should()
            .Be(1, "flattener must run for root-attached separate-table plans once fence widens");
        _profileMergeSynthesizer
            .SynthesizeCallCount.Should()
            .Be(1, "profile merge must run for root-attached SeparateTableNonCollection updates");
        profileRequest.Should().NotBeSameAs(projectedProfileRequest);
        profileRequest.WritableRequestBody.Should().NotBeSameAs(projectedWritableBody);
        _writeFlattener.CapturedInput.Should().NotBeNull();
        _writeFlattener.CapturedInput!.SelectedBody.Should().BeSameAs(projectedWritableBody);
        _profileMergeSynthesizer
            .CapturedRequest!.WritableRequestBody.Should()
            .BeSameAs(projectedWritableBody);
        _profileMergeSynthesizer.CapturedRequest!.ProfileRequest.Should().BeSameAs(projectedProfileRequest);
        _profileMergeSynthesizer.CapturedRequest!.ProfileAppliedContext.Should().NotBeNull();
        _noProfilePersister
            .TryPersistCallCount.Should()
            .Be(1, "persister must receive the profile merge result");
        _writeSessionFactory.Session.CommitCallCount.Should().Be(1);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(0);
        _currentStateLoader.CapturedRequest!.IncludeDescriptorProjection.Should().BeTrue();

        var updateResult = result.Should().BeOfType<RelationalWriteExecutorResult.Update>().Subject;
        updateResult.Result.Should().BeOfType<UpdateResult.UpdateSuccess>();
    }

    [Test]
    public async Task It_returns_typed_profile_data_policy_failure_when_separate_table_scope_creatability_is_false_for_post()
    {
        // Slice 3: when a profiled POST creates a new document but the request marks a
        // separate-table scope as non-creatable, the synthesizer returns
        // ProfileMergeOutcome.Reject and the executor maps that to
        // UpsertFailureProfileDataPolicy — the typed creatability failure.
        var writableBody = JsonNode.Parse("""{"schoolId":255901}""")!;
        var rootPlan = CreateRootPlan();
        var extensionTableModel = AdapterFactoryTestFixtures.BuildRootExtensionTableModel();
        var extensionPlan = AdapterFactoryTestFixtures.BuildRootExtensionTableWritePlan(extensionTableModel);
        var resourceModel = new RelationalResourceModel(
            Resource: new QualifiedResourceName("Ed-Fi", "School"),
            PhysicalSchema: new DbSchemaName("edfi"),
            StorageKind: ResourceStorageKind.RelationalTables,
            Root: rootPlan.TableModel,
            TablesInDependencyOrder: [rootPlan.TableModel, extensionTableModel],
            DocumentReferenceBindings: [],
            DescriptorEdgeSources: []
        );
        var resourceWritePlan = new ResourceWritePlan(resourceModel, [rootPlan, extensionPlan]);
        var scopeCatalog = CompiledScopeAdapterFactory.BuildFromWritePlan(resourceWritePlan);
        var profileContext = new BackendProfileWriteContext(
            Request: new ProfileAppliedWriteRequest(
                WritableRequestBody: writableBody,
                RootResourceCreatable: true,
                RequestScopeStates:
                [
                    new RequestScopeState(
                        Address: new ScopeInstanceAddress("$", []),
                        Visibility: ProfileVisibilityKind.VisiblePresent,
                        Creatable: true
                    ),
                    new RequestScopeState(
                        Address: new ScopeInstanceAddress("$._ext.sample", []),
                        Visibility: ProfileVisibilityKind.VisiblePresent,
                        Creatable: false
                    ),
                ],
                VisibleRequestCollectionItems: []
            ),
            ProfileName: "test-write-profile",
            CompiledScopeCatalog: scopeCatalog,
            StoredStateProjectionInvoker: A.Fake<IStoredStateProjectionInvoker>()
        );

        _writeFlattener.ResultToReturn = new FlattenedWriteSet(
            new RootWriteRowBuffer(
                rootPlan,
                [
                    FlattenedWriteValue.UnresolvedRootDocumentId.Instance,
                    new FlattenedWriteValue.Literal(255901),
                    new FlattenedWriteValue.Literal("Lincoln High"),
                ]
            )
        );

        _profileMergeSynthesizer.RejectionToReturn = new ProfileCreatabilityRejection(
            "$._ext.sample",
            "Creatability=false on separate-table scope."
        );

        var baseRequest = CreateRequest(RelationalWriteOperationKind.Post, selectedBody: writableBody);
        var request = baseRequest with
        {
            WritePlan = resourceWritePlan,
            ProfileWriteContext = profileContext,
        };

        var result = await _sut.ExecuteAsync(request);

        _profileMergeSynthesizer.SynthesizeCallCount.Should().Be(1);
        _noProfilePersister
            .TryPersistCallCount.Should()
            .Be(0, "persister must not run when synthesizer rejects");
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
        _writeSessionFactory.Session.CommitCallCount.Should().Be(0);

        var upsertResult = result.Should().BeOfType<RelationalWriteExecutorResult.Upsert>().Subject;
        upsertResult
            .Result.Should()
            .BeOfType<UpsertResult.UpsertFailureProfileDataPolicy>()
            .Which.ProfileName.Should()
            .Be("test-write-profile");
    }

    // The full "Creatable gates create-new only, not matched updates" invariant is covered
    // by the pair of tests: this test exercises the matched-update half; the companion test
    // It_returns_typed_profile_data_policy_failure_when_separate_table_scope_creatability_is_false_for_post
    // exercises the new-create rejection half.
    [Test]
    public async Task It_allows_matched_update_when_separate_table_scope_creatability_is_false()
    {
        // Invariant: Creatable gates create-new only, not matched updates.
        // Same profile (Creatable=false on $._ext.sample) + existing stored row →
        // synthesizer returns Success, executor persists and returns UpdateSuccess.
        var writableBody = JsonNode.Parse("""{"schoolId":255901}""")!;
        var rootPlan = CreateRootPlan();
        var extensionTableModel = AdapterFactoryTestFixtures.BuildRootExtensionTableModel();
        var extensionPlan = AdapterFactoryTestFixtures.BuildRootExtensionTableWritePlan(extensionTableModel);
        var resourceModel = new RelationalResourceModel(
            Resource: new QualifiedResourceName("Ed-Fi", "School"),
            PhysicalSchema: new DbSchemaName("edfi"),
            StorageKind: ResourceStorageKind.RelationalTables,
            Root: rootPlan.TableModel,
            TablesInDependencyOrder: [rootPlan.TableModel, extensionTableModel],
            DocumentReferenceBindings: [],
            DescriptorEdgeSources: []
        );
        var resourceWritePlan = new ResourceWritePlan(resourceModel, [rootPlan, extensionPlan]);
        var scopeCatalog = CompiledScopeAdapterFactory.BuildFromWritePlan(resourceWritePlan);

        var storedStateProjectionInvoker = A.Fake<IStoredStateProjectionInvoker>();
        var profileRequest = new ProfileAppliedWriteRequest(
            WritableRequestBody: writableBody,
            RootResourceCreatable: true,
            RequestScopeStates:
            [
                new RequestScopeState(
                    Address: new ScopeInstanceAddress("$", []),
                    Visibility: ProfileVisibilityKind.VisiblePresent,
                    Creatable: true
                ),
                // Same non-creatable separate-table scope as the POST rejection test —
                // existing stored row makes this a matched update, which is allowed.
                new RequestScopeState(
                    Address: new ScopeInstanceAddress("$._ext.sample", []),
                    Visibility: ProfileVisibilityKind.VisiblePresent,
                    Creatable: false
                ),
            ],
            VisibleRequestCollectionItems: []
        );
        var projectedContext = new ProfileAppliedWriteContext(
            Request: profileRequest,
            VisibleStoredBody: JsonNode.Parse("""{"schoolId":255901}""")!,
            StoredScopeStates:
            [
                new StoredScopeState(
                    Address: new ScopeInstanceAddress("$", []),
                    Visibility: ProfileVisibilityKind.VisiblePresent,
                    HiddenMemberPaths: []
                ),
                new StoredScopeState(
                    Address: new ScopeInstanceAddress("$._ext.sample", []),
                    Visibility: ProfileVisibilityKind.VisiblePresent,
                    HiddenMemberPaths: []
                ),
            ],
            VisibleStoredCollectionRows: []
        );

        A.CallTo(() =>
                storedStateProjectionInvoker.ProjectStoredState(
                    A<JsonNode>._,
                    A<ProfileAppliedWriteRequest>._,
                    A<IReadOnlyList<CompiledScopeDescriptor>>._
                )
            )
            .Returns(projectedContext);

        var profileContext = new BackendProfileWriteContext(
            Request: profileRequest,
            ProfileName: "test-write-profile",
            CompiledScopeCatalog: scopeCatalog,
            StoredStateProjectionInvoker: storedStateProjectionInvoker
        );

        var existingTargetContext = new RelationalWriteTargetContext.ExistingDocument(
            345L,
            new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb")),
            44L
        );
        _currentStateLoader.ResultToReturn = CreateCurrentState(
            CreateRequest(
                RelationalWriteOperationKind.Put,
                selectedBody: writableBody,
                targetContext: existingTargetContext
            ),
            contentVersion: 44L
        );

        _writeFlattener.ResultToReturn = new FlattenedWriteSet(
            new RootWriteRowBuffer(
                rootPlan,
                [
                    new FlattenedWriteValue.Literal(345L),
                    new FlattenedWriteValue.Literal(255901),
                    new FlattenedWriteValue.Literal("Lincoln High"),
                ]
            )
        );

        // The synthesizer does NOT reject: matched update on an existing visible-present
        // separate-table scope is allowed, independent of Creatable.
        _profileMergeSynthesizer.ResultToReturn = new RelationalWriteMergeResult(
            [
                new RelationalWriteMergedTableState(
                    rootPlan,
                    [CreateRootTableRow(345L, 255901, "Lincoln High")],
                    [CreateRootTableRow(345L, 255901, "Lincoln High")]
                ),
            ],
            supportsGuardedNoOp: false
        );

        var baseRequest = CreateRequest(
            RelationalWriteOperationKind.Put,
            selectedBody: writableBody,
            targetContext: existingTargetContext
        );
        var request = baseRequest with
        {
            WritePlan = resourceWritePlan,
            ProfileWriteContext = profileContext,
        };

        var result = await _sut.ExecuteAsync(request);

        _profileMergeSynthesizer
            .SynthesizeCallCount.Should()
            .Be(1, "matched update must reach the synthesizer even with Creatable=false");
        _noProfilePersister
            .TryPersistCallCount.Should()
            .Be(1, "matched update must persist when synthesizer returns Success");
        _writeSessionFactory.Session.CommitCallCount.Should().Be(1);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(0);

        var updateResult = result.Should().BeOfType<RelationalWriteExecutorResult.Update>().Subject;
        updateResult.Result.Should().BeOfType<UpdateResult.UpdateSuccess>();
    }

    [Test]
    public async Task It_rejects_profiled_create_new_with_contract_mismatch()
    {
        var writableBody = JsonNode.Parse("""{"schoolId":255901}""")!;
        var rootPlan = CreateRootPlan();
        var resourceModel = CreateRelationalResourceModel(rootPlan.TableModel);
        var resourceWritePlan = new ResourceWritePlan(resourceModel, [rootPlan]);
        var scopeCatalog = CompiledScopeAdapterFactory.BuildFromWritePlan(resourceWritePlan);
        var profileContext = new BackendProfileWriteContext(
            Request: new ProfileAppliedWriteRequest(
                WritableRequestBody: writableBody,
                RootResourceCreatable: true,
                RequestScopeStates:
                [
                    new RequestScopeState(
                        Address: new ScopeInstanceAddress("$", []),
                        Visibility: ProfileVisibilityKind.VisiblePresent,
                        Creatable: true
                    ),
                    new RequestScopeState(
                        Address: new ScopeInstanceAddress("$.unknownScope", []),
                        Visibility: ProfileVisibilityKind.VisiblePresent,
                        Creatable: true
                    ),
                ],
                VisibleRequestCollectionItems: []
            ),
            ProfileName: "test-write-profile",
            CompiledScopeCatalog: scopeCatalog,
            StoredStateProjectionInvoker: A.Fake<IStoredStateProjectionInvoker>()
        );

        var request = CreateRequest(RelationalWriteOperationKind.Post, selectedBody: writableBody) with
        {
            ProfileWriteContext = profileContext,
        };

        var result = await _sut.ExecuteAsync(request);

        _writeFlattener.FlattenCallCount.Should().Be(0);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
        _writeSessionFactory.Session.CommitCallCount.Should().Be(0);

        var upsertResult = result.Should().BeOfType<RelationalWriteExecutorResult.Upsert>().Subject;
        var failureMessage = upsertResult
            .Result.Should()
            .BeOfType<UpsertResult.UnknownFailure>()
            .Subject.FailureMessage;
        failureMessage.Should().Contain("contract mismatch");
        failureMessage.Should().NotContain("not yet supported");
    }

    [Test]
    public async Task It_does_not_invoke_materializer_for_no_profile_writes()
    {
        var request = CreateRequest(RelationalWriteOperationKind.Post);

        var result = await _sut.ExecuteAsync(request);

        result.Should().BeOfType<RelationalWriteExecutorResult.Upsert>();
        _readMaterializer
            .MaterializeCallCount.Should()
            .Be(0, "materializer must not be called when no profile context is present");
    }

    [Test]
    public async Task It_synthesizes_profile_merge_for_multi_table_plan_when_runtime_shape_is_root_only()
    {
        // A multi-table compiled plan (root + separate-table extension) whose profile metadata
        // leaves non-root scopes out of the request surface still classifies as RootTableOnly.
        // The profile merge synthesizer handles the root table; the persister leaves the
        // extension table untouched because it is absent from the produced merge result.
        var writableBody = JsonNode.Parse("""{"schoolId":255901}""")!;
        var rootPlan = CreateRootPlan();
        var extensionTableModel = AdapterFactoryTestFixtures.BuildRootExtensionTableModel();
        var extensionPlan = AdapterFactoryTestFixtures.BuildRootExtensionTableWritePlan(extensionTableModel);
        var resourceModel = new RelationalResourceModel(
            Resource: new QualifiedResourceName("Ed-Fi", "School"),
            PhysicalSchema: new DbSchemaName("edfi"),
            StorageKind: ResourceStorageKind.RelationalTables,
            Root: rootPlan.TableModel,
            TablesInDependencyOrder: [rootPlan.TableModel, extensionTableModel],
            DocumentReferenceBindings: [],
            DescriptorEdgeSources: []
        );
        var resourceWritePlan = new ResourceWritePlan(resourceModel, [rootPlan, extensionPlan]);
        var scopeCatalog = CompiledScopeAdapterFactory.BuildFromWritePlan(resourceWritePlan);
        var profileContext = new BackendProfileWriteContext(
            Request: new ProfileAppliedWriteRequest(
                WritableRequestBody: writableBody,
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
            CompiledScopeCatalog: scopeCatalog,
            StoredStateProjectionInvoker: A.Fake<IStoredStateProjectionInvoker>()
        );

        var baseRequest = CreateRequest(RelationalWriteOperationKind.Post, selectedBody: writableBody);
        var request = baseRequest with
        {
            WritePlan = resourceWritePlan,
            ProfileWriteContext = profileContext,
        };

        // Pre-configure the flattener: for multi-table plans the default fallback uses .Single()
        // on the plan's table list. The profile merge synthesizer only uses the root row, so a
        // root-only FlattenedWriteSet is the correct handoff shape for Slice 2 regardless of
        // how many tables the compiled plan carries.
        _writeFlattener.ResultToReturn = new FlattenedWriteSet(
            new RootWriteRowBuffer(
                rootPlan,
                [
                    FlattenedWriteValue.UnresolvedRootDocumentId.Instance,
                    new FlattenedWriteValue.Literal(255901),
                    new FlattenedWriteValue.Literal("Lincoln High"),
                ]
            )
        );

        _profileMergeSynthesizer.ResultToReturn = new RelationalWriteMergeResult(
            [
                new RelationalWriteMergedTableState(
                    rootPlan,
                    [],
                    [
                        new RelationalWriteMergedTableRow(
                            [
                                FlattenedWriteValue.UnresolvedRootDocumentId.Instance,
                                new FlattenedWriteValue.Literal(255901),
                                new FlattenedWriteValue.Literal("Lincoln High"),
                            ],
                            [
                                FlattenedWriteValue.UnresolvedRootDocumentId.Instance,
                                new FlattenedWriteValue.Literal(255901),
                                new FlattenedWriteValue.Literal("Lincoln High"),
                            ]
                        ),
                    ]
                ),
            ],
            supportsGuardedNoOp: false
        );

        var result = await _sut.ExecuteAsync(request);

        _writeFlattener
            .FlattenCallCount.Should()
            .Be(1, "the profile path must flatten once classification passes");
        _profileMergeSynthesizer
            .SynthesizeCallCount.Should()
            .Be(1, "the profile synthesizer must run for root-only runtime shapes even on multi-table plans");
        _profileMergeSynthesizer.CapturedRequest.Should().NotBeNull();
        _profileMergeSynthesizer.CapturedRequest!.WritePlan.Should().BeSameAs(resourceWritePlan);
        _noProfileMergeSynthesizer
            .SynthesizeCallCount.Should()
            .Be(0, "the no-profile synthesizer must not run for profiled writes");
        _noProfilePersister
            .TryPersistCallCount.Should()
            .Be(1, "the persister must receive the profile merge result");
        _noProfilePersister.CapturedMergeResult.Should().BeSameAs(_profileMergeSynthesizer.ResultToReturn);
        _writeSessionFactory.Session.CommitCallCount.Should().Be(1);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(0);

        var upsertResult = result.Should().BeOfType<RelationalWriteExecutorResult.Upsert>().Subject;
        upsertResult.Result.Should().BeOfType<UpsertResult.InsertSuccess>();
        result.AttemptOutcome.Should().Be(RelationalWriteExecutorAttemptOutcome.AppliedWrite.Instance);
    }

    [Test]
    public async Task It_synthesizes_profile_merge_for_root_table_only_create_new_request()
    {
        var writableBody = JsonNode.Parse("""{"schoolId":255901}""")!;
        var rootPlan = CreateRootPlan();
        var resourceModel = CreateRelationalResourceModel(rootPlan.TableModel);
        var resourceWritePlan = new ResourceWritePlan(resourceModel, [rootPlan]);
        var scopeCatalog = CompiledScopeAdapterFactory.BuildFromWritePlan(resourceWritePlan);
        var profileContext = new BackendProfileWriteContext(
            Request: new ProfileAppliedWriteRequest(
                WritableRequestBody: writableBody,
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
            CompiledScopeCatalog: scopeCatalog,
            StoredStateProjectionInvoker: A.Fake<IStoredStateProjectionInvoker>()
        );

        var request = CreateRequest(RelationalWriteOperationKind.Post, selectedBody: writableBody) with
        {
            ProfileWriteContext = profileContext,
        };

        _profileMergeSynthesizer.ResultToReturn = new RelationalWriteMergeResult(
            [
                new RelationalWriteMergedTableState(
                    rootPlan,
                    [],
                    [
                        new RelationalWriteMergedTableRow(
                            [
                                FlattenedWriteValue.UnresolvedRootDocumentId.Instance,
                                new FlattenedWriteValue.Literal(255901),
                                new FlattenedWriteValue.Literal("Lincoln High"),
                            ],
                            [
                                FlattenedWriteValue.UnresolvedRootDocumentId.Instance,
                                new FlattenedWriteValue.Literal(255901),
                                new FlattenedWriteValue.Literal("Lincoln High"),
                            ]
                        ),
                    ]
                ),
            ],
            supportsGuardedNoOp: false
        );

        var result = await _sut.ExecuteAsync(request);

        _writeFlattener
            .FlattenCallCount.Should()
            .Be(1, "the profile path must flatten before invoking the profile synthesizer");
        _profileMergeSynthesizer
            .SynthesizeCallCount.Should()
            .Be(1, "the profile synthesizer must be invoked when the Slice 2 gates pass");
        _profileMergeSynthesizer.CapturedRequest.Should().NotBeNull();
        _profileMergeSynthesizer.CapturedRequest!.WritePlan.Should().BeSameAs(request.WritePlan);
        _profileMergeSynthesizer.CapturedRequest!.WritableRequestBody.Should().BeSameAs(writableBody);
        _profileMergeSynthesizer.CapturedRequest!.CurrentState.Should().BeNull();
        _profileMergeSynthesizer.CapturedRequest!.ProfileRequest.Should().BeSameAs(profileContext.Request);
        _profileMergeSynthesizer.CapturedRequest!.ProfileAppliedContext.Should().BeNull();
        _noProfileMergeSynthesizer
            .SynthesizeCallCount.Should()
            .Be(0, "the no-profile synthesizer must not run for profiled writes");
        _noProfilePersister
            .TryPersistCallCount.Should()
            .Be(1, "the persister must receive the profile merge result");
        _noProfilePersister.CapturedMergeResult.Should().BeSameAs(_profileMergeSynthesizer.ResultToReturn);
        _writeSessionFactory.Session.CommitCallCount.Should().Be(1);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(0);

        var upsertResult = result.Should().BeOfType<RelationalWriteExecutorResult.Upsert>().Subject;
        upsertResult.Result.Should().BeOfType<UpsertResult.InsertSuccess>();
        result.AttemptOutcome.Should().Be(RelationalWriteExecutorAttemptOutcome.AppliedWrite.Instance);
    }

    [Test]
    public async Task It_skips_if_match_pre_check_when_etag_is_absent_for_put_requests()
    {
        var request = CreateRequest(RelationalWriteOperationKind.Put);

        var result = await _sut.ExecuteAsync(request);

        result
            .Should()
            .BeOfType<RelationalWriteExecutorResult.Update>()
            .Which.Result.Should()
            .BeOfType<UpdateResult.UpdateSuccess>();
        _committedRepresentationReader
            .ReadCallCount.Should()
            .Be(1, "no pre-check read should occur when If-Match header is absent");
        _writeSessionFactory.Session.CommitCallCount.Should().Be(1);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(0);
    }

    [Test]
    public async Task It_proceeds_when_if_match_etag_matches_committed_etag_for_put_requests()
    {
        var baseRequest = CreateRequest(RelationalWriteOperationKind.Put);
        var matchingEtag = ExpectedEtag(baseRequest);
        var request = baseRequest with { IfMatchEtag = matchingEtag };

        var result = await _sut.ExecuteAsync(request);

        result
            .Should()
            .BeOfType<RelationalWriteExecutorResult.Update>()
            .Which.Result.Should()
            .BeOfType<UpdateResult.UpdateSuccess>();
        _committedRepresentationReader
            .ReadCallCount.Should()
            .Be(1, "pre-check read is reused on the guarded-no-op path; no second database round-trip");
        _writeSessionFactory
            .Session.Commands.Should()
            .ContainSingle("a row-level lock must be acquired before the If-Match ETag read");
        _writeSessionFactory
            .Session.Commands[0]
            .CommandText.Should()
            .Contain("FOR UPDATE", "PostgreSQL row lock must be requested in the pre-check SELECT");
        _writeSessionFactory.Session.CommitCallCount.Should().Be(1);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(0);
    }

    [Test]
    public async Task It_issues_document_row_lock_before_etag_read_on_changed_write_path()
    {
        // Arrange: PUT with matching If-Match on a changed-write (not no-op) path.
        var baseRequest = CreateRequest(RelationalWriteOperationKind.Put);
        var matchingEtag = ExpectedEtag(baseRequest);
        var request = baseRequest with { IfMatchEtag = matchingEtag };
        _currentStateLoader.ResultToReturn = CreateCurrentState(request, 44L);
        _noProfileMergeSynthesizer.ResultToReturn = CreateMergeResult(
            request.WritePlan.TablePlansInDependencyOrder[0],
            currentSchoolId: 255901,
            mergedSchoolId: 255901,
            currentName: "Lincoln High",
            mergedName: "Lincoln High Updated"
        );

        var result = await _sut.ExecuteAsync(request);

        result
            .Should()
            .BeOfType<RelationalWriteExecutorResult.Update>()
            .Which.Result.Should()
            .BeOfType<UpdateResult.UpdateSuccess>();
        _writeSessionFactory
            .Session.Commands.Should()
            .ContainSingle(
                "a row-level lock must be acquired on the changed-write path before the ETag read"
            );
        _writeSessionFactory
            .Session.Commands[0]
            .CommandText.Should()
            .Contain("FOR UPDATE", "PostgreSQL dialect must use FOR UPDATE to lock the document row");
        _committedRepresentationReader
            .ReadCallCount.Should()
            .Be(2, "pre-check read (ETag comparison) + post-write read (response ETag) must each occur once");
        _noProfilePersister.TryPersistCallCount.Should().Be(1, "the changed write must be persisted");
        _writeSessionFactory.Session.CommitCallCount.Should().Be(1);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(0);
    }

    [Test]
    public async Task It_returns_update_failure_etag_mismatch_when_if_match_does_not_match_put_etag()
    {
        var request = CreateRequest(RelationalWriteOperationKind.Put) with
        {
            IfMatchEtag = "\"stale-client-etag\"",
        };

        var result = await _sut.ExecuteAsync(request);

        result
            .Should()
            .BeEquivalentTo(
                new RelationalWriteExecutorResult.Update(new UpdateResult.UpdateFailureETagMisMatch())
            );
        _committedRepresentationReader
            .ReadCallCount.Should()
            .Be(1, "the pre-check issues exactly one read before returning the mismatch result");
        _writeSessionFactory
            .Session.Commands.Should()
            .ContainSingle("row lock must be acquired even before the mismatch is detected");
        _noProfileMergeSynthesizer
            .SynthesizeCallCount.Should()
            .Be(0, "merge must not run after an ETag mismatch");
        _noProfilePersister.TryPersistCallCount.Should().Be(0, "persist must not run after an ETag mismatch");
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
        _writeSessionFactory.Session.CommitCallCount.Should().Be(0);
    }

    [Test]
    public async Task It_returns_update_failure_etag_mismatch_when_the_locked_if_match_row_disappears()
    {
        var request = CreateRequest(RelationalWriteOperationKind.Put) with
        {
            IfMatchEtag = ExpectedEtag(CreateRequest(RelationalWriteOperationKind.Put)),
        };
        _writeSessionFactory.Session.ScalarResultToReturn = null;

        var result = await _sut.ExecuteAsync(request);

        result
            .Should()
            .BeEquivalentTo(
                new RelationalWriteExecutorResult.Update(new UpdateResult.UpdateFailureETagMisMatch())
            );
        _committedRepresentationReader
            .ReadCallCount.Should()
            .Be(0, "the executor must stop after the lock probe reports that the row is gone");
        _currentStateLoader
            .LoadCallCount.Should()
            .Be(0, "no committed-state read should occur after the row disappears");
        _noProfileMergeSynthesizer
            .SynthesizeCallCount.Should()
            .Be(0, "merge must not run after the row disappears during the If-Match pre-check");
        _noProfilePersister
            .TryPersistCallCount.Should()
            .Be(0, "persist must not run after the row disappears");
        _writeSessionFactory
            .Session.Commands.Should()
            .ContainSingle("the executor must issue the row-lock command before observing the missing row");
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
        _writeSessionFactory.Session.CommitCallCount.Should().Be(0);
    }

    [Test]
    public async Task It_returns_upsert_failure_etag_mismatch_when_if_match_does_not_match_post_as_update_etag()
    {
        var request = CreateRequest(
            RelationalWriteOperationKind.Post,
            targetContext: new RelationalWriteTargetContext.ExistingDocument(
                345L,
                new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb")),
                44L
            )
        ) with
        {
            IfMatchEtag = "\"stale-client-etag\"",
        };

        var result = await _sut.ExecuteAsync(request);

        result
            .Should()
            .BeEquivalentTo(
                new RelationalWriteExecutorResult.Upsert(new UpsertResult.UpsertFailureETagMisMatch())
            );
        _committedRepresentationReader
            .ReadCallCount.Should()
            .Be(1, "the pre-check issues exactly one read before returning the mismatch result");
        _noProfileMergeSynthesizer
            .SynthesizeCallCount.Should()
            .Be(0, "merge must not run after an ETag mismatch");
        _noProfilePersister.TryPersistCallCount.Should().Be(0, "persist must not run after an ETag mismatch");
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
        _writeSessionFactory.Session.CommitCallCount.Should().Be(0);
    }

    [Test]
    public async Task It_returns_etag_mismatch_when_if_match_wildcard_is_sent()
    {
        // Wildcard is not supported. The executor treats the literal star as a regular ETag value;
        // no real ETag will ever equal it, so the precondition always fails.
        var request = CreateRequest(RelationalWriteOperationKind.Put) with
        {
            IfMatchEtag = "*",
        };

        var result = await _sut.ExecuteAsync(request);

        result
            .Should()
            .BeEquivalentTo(
                new RelationalWriteExecutorResult.Update(new UpdateResult.UpdateFailureETagMisMatch()),
                "If-Match: * is not supported; the literal \"*\" never matches a real ETag"
            );
        _committedRepresentationReader
            .ReadCallCount.Should()
            .Be(1, "CheckIfMatchEtagAsync reads the committed doc before rejecting the wildcard");
        _writeSessionFactory
            .Session.Commands.Should()
            .HaveCount(1, "CheckIfMatchEtagAsync issues one FOR UPDATE row-lock command");
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
        _writeSessionFactory.Session.CommitCallCount.Should().Be(0);
    }

    [Test]
    public async Task It_skips_if_match_pre_check_for_create_new_post_requests_even_when_etag_is_supplied()
    {
        var request = CreateRequest(RelationalWriteOperationKind.Post) with
        {
            IfMatchEtag = "\"any-etag-value\"",
        };

        var result = await _sut.ExecuteAsync(request);

        result
            .Should()
            .BeOfType<RelationalWriteExecutorResult.Upsert>()
            .Which.Result.Should()
            .BeOfType<UpsertResult.InsertSuccess>();
        _committedRepresentationReader
            .ReadCallCount.Should()
            .Be(1, "only the final ETag readback should occur; no pre-check for create-new");
        _writeSessionFactory
            .Session.Commands.Should()
            .BeEmpty("no row lock should be issued for the CreateNew POST path");
        _writeSessionFactory.Session.CommitCallCount.Should().Be(1);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(0);
    }

    [Test]
    public async Task It_returns_etag_mismatch_when_if_match_wildcard_is_sent_for_post_as_update_requests()
    {
        // Wildcard is not supported on the post-as-update path either.
        // The literal star never matches a real ETag hash, so 412 is always returned.
        var request = CreateRequest(
            RelationalWriteOperationKind.Post,
            targetContext: new RelationalWriteTargetContext.ExistingDocument(
                345L,
                new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb")),
                44L
            )
        ) with
        {
            IfMatchEtag = "*",
        };

        var result = await _sut.ExecuteAsync(request);

        result
            .Should()
            .BeEquivalentTo(
                new RelationalWriteExecutorResult.Upsert(new UpsertResult.UpsertFailureETagMisMatch()),
                "If-Match: * is not supported; the literal \"*\" never matches a real ETag"
            );
        _committedRepresentationReader
            .ReadCallCount.Should()
            .Be(1, "CheckIfMatchEtagAsync reads the committed doc before rejecting the wildcard");
        _writeSessionFactory
            .Session.Commands.Should()
            .HaveCount(1, "CheckIfMatchEtagAsync issues one FOR UPDATE row-lock command");
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
        _writeSessionFactory.Session.CommitCallCount.Should().Be(0);
    }

    [Test]
    public async Task It_does_not_invoke_reference_resolver_when_if_match_mismatches()
    {
        var request = CreateRequest(RelationalWriteOperationKind.Put) with
        {
            IfMatchEtag = "\"stale-client-etag\"",
        };

        var result = await _sut.ExecuteAsync(request);

        result
            .Should()
            .BeEquivalentTo(
                new RelationalWriteExecutorResult.Update(new UpdateResult.UpdateFailureETagMisMatch())
            );
        _referenceResolverAdapterFactory
            .CreateSessionAdapterCallCount.Should()
            .Be(0, "reference resolver must not be invoked when If-Match pre-check returns a mismatch");
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
        _writeSessionFactory.Session.CommitCallCount.Should().Be(0);
    }

    [Test]
    public async Task It_returns_upsert_failure_etag_mismatch_when_post_target_flips_to_existing_and_if_match_is_stale()
    {
        // POST request starts as CreateNew (early pre-check is skipped); the in-session resolver
        // discovers the document already exists and flips the target to ExistingDocument.
        // The post-flip If-Match recheck must then catch the stale ETag and return a mismatch.
        var existingDocumentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));
        var request = CreateRequest(RelationalWriteOperationKind.Post) with
        {
            IfMatchEtag = "\"stale-client-etag\"",
        };
        _targetLookupResolver.PostResults.Enqueue(
            new RelationalWriteTargetLookupResult.ExistingDocument(345L, existingDocumentUuid, 44L)
        );

        var result = await _sut.ExecuteAsync(request);

        result
            .Should()
            .BeEquivalentTo(
                new RelationalWriteExecutorResult.Upsert(new UpsertResult.UpsertFailureETagMisMatch())
            );
        _committedRepresentationReader
            .ReadCallCount.Should()
            .Be(1, "post-flip If-Match check reads committed representation exactly once");
        _noProfileMergeSynthesizer
            .SynthesizeCallCount.Should()
            .Be(0, "merge must not run after a post-flip ETag mismatch");
        _noProfilePersister
            .TryPersistCallCount.Should()
            .Be(0, "persist must not run after a post-flip ETag mismatch");
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
        _writeSessionFactory.Session.CommitCallCount.Should().Be(0);
    }

    [Test]
    public async Task It_caches_committed_doc_and_proceeds_when_post_target_flips_to_existing_and_if_match_matches()
    {
        // POST starts as CreateNew; in-session resolver flips target to ExistingDocument.
        // If-Match matches → write proceeds (no-op here due to default synthesizer) using
        // the cached committed representation without an extra DB round-trip.
        var existingDocumentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));
        var baseRequest = CreateRequest(RelationalWriteOperationKind.Post);
        var request = baseRequest with { IfMatchEtag = ExpectedEtag(baseRequest) };
        _targetLookupResolver.PostResults.Enqueue(
            new RelationalWriteTargetLookupResult.ExistingDocument(345L, existingDocumentUuid, 44L)
        );

        var result = await _sut.ExecuteAsync(request);

        result
            .Should()
            .BeOfType<RelationalWriteExecutorResult.Upsert>()
            .Which.Result.Should()
            .BeOfType<UpsertResult.UpdateSuccess>(
                "post-flip with matching If-Match succeeds as a no-op update"
            );
        _committedRepresentationReader
            .ReadCallCount.Should()
            .Be(
                1,
                "the post-flip check reads committed representation once; the no-op path reuses cachedCommittedDoc"
            );
        _writeSessionFactory.Session.CommitCallCount.Should().Be(1);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(0);
    }

    [Test]
    public async Task It_returns_etag_mismatch_when_lock_query_finds_no_row_for_put_requests()
    {
        // Race: the target was resolved as ExistingDocument before the write session opened,
        // but by the time the row-lock SELECT runs inside the session the document is gone
        // (concurrent DELETE or rolled-back insert). ExecuteScalarAsync returns null.
        // The executor must short-circuit and return the ETag mismatch result without
        // attempting committed-representation rehydration (which would throw because no
        // row exists to read back).
        var request = CreateRequest(RelationalWriteOperationKind.Put) with
        {
            IfMatchEtag = "\"client-etag-for-gone-doc\"",
        };
        _writeSessionFactory.Session.ScalarResultToReturn = null;

        var result = await _sut.ExecuteAsync(request);

        result
            .Should()
            .BeEquivalentTo(
                new RelationalWriteExecutorResult.Update(new UpdateResult.UpdateFailureETagMisMatch())
            );
        _committedRepresentationReader
            .ReadCallCount.Should()
            .Be(0, "committed readback must be skipped when the lock query finds no row");
        _writeSessionFactory
            .Session.Commands.Should()
            .ContainSingle("the row-lock SELECT must still be issued even though the row is absent");
        _writeSessionFactory
            .Session.Commands[0]
            .CommandText.Should()
            .Contain("FOR UPDATE", "PostgreSQL dialect must use FOR UPDATE in the lock SELECT");
        _noProfileMergeSynthesizer
            .SynthesizeCallCount.Should()
            .Be(0, "merge must not run when the row-lock finds no row");
        _noProfilePersister
            .TryPersistCallCount.Should()
            .Be(0, "persist must not run when the row-lock finds no row");
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
        _writeSessionFactory.Session.CommitCallCount.Should().Be(0);
    }

    [Test]
    public async Task It_returns_upsert_etag_mismatch_when_lock_query_finds_no_row_for_post_as_update_requests()
    {
        // Same race as the PUT variant but triggered on a POST-as-update path where the
        // target is already resolved as ExistingDocument before the executor is called.
        var request = CreateRequest(
            RelationalWriteOperationKind.Post,
            targetContext: new RelationalWriteTargetContext.ExistingDocument(
                345L,
                new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb")),
                44L
            )
        ) with
        {
            IfMatchEtag = "\"client-etag-for-gone-doc\"",
        };
        _writeSessionFactory.Session.ScalarResultToReturn = null;

        var result = await _sut.ExecuteAsync(request);

        result
            .Should()
            .BeEquivalentTo(
                new RelationalWriteExecutorResult.Upsert(new UpsertResult.UpsertFailureETagMisMatch())
            );
        _committedRepresentationReader
            .ReadCallCount.Should()
            .Be(0, "committed readback must be skipped when the lock query finds no row");
        _noProfileMergeSynthesizer
            .SynthesizeCallCount.Should()
            .Be(0, "merge must not run when the row-lock finds no row");
        _noProfilePersister
            .TryPersistCallCount.Should()
            .Be(0, "persist must not run when the row-lock finds no row");
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
        _writeSessionFactory.Session.CommitCallCount.Should().Be(0);
    }

    private static RelationalWriteExecutorRequest CreateRequest(
        RelationalWriteOperationKind operationKind,
        bool allowIdentityUpdates = false,
        IReadOnlyList<DocumentReference>? documentReferences = null,
        IReadOnlyList<DescriptorReference>? descriptorReferences = null,
        RelationalWriteTargetContext? targetContext = null,
        TableWritePlan? rootWritePlan = null,
        JsonNode? selectedBody = null,
        SqlDialect dialect = SqlDialect.Pgsql
    )
    {
        var resolvedRootWritePlan = rootWritePlan ?? CreateRootPlan();
        var resourceModel = CreateRelationalResourceModel(resolvedRootWritePlan.TableModel);
        var resourceWritePlan = new ResourceWritePlan(resourceModel, [resolvedRootWritePlan]);
        var mappingSet = CreateMappingSet(resourceModel, [resolvedRootWritePlan], dialect);
        var createDocumentUuid = new DocumentUuid(Guid.Parse("cccccccc-1111-2222-3333-dddddddddddd"));
        var updateDocumentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));
        var resolvedTargetContext =
            targetContext
            ?? (
                operationKind == RelationalWriteOperationKind.Put
                    ? new RelationalWriteTargetContext.ExistingDocument(345L, updateDocumentUuid, 44L)
                    : new RelationalWriteTargetContext.CreateNew(createDocumentUuid)
            );

        return new RelationalWriteExecutorRequest(
            mappingSet,
            operationKind,
            operationKind == RelationalWriteOperationKind.Put
                ? new RelationalWriteTargetRequest.Put(updateDocumentUuid)
                : new RelationalWriteTargetRequest.Post(
                    new ReferentialId(Guid.NewGuid()),
                    createDocumentUuid
                ),
            resourceWritePlan,
            CreateReadPlan(resourceModel, dialect),
            selectedBody ?? JsonNode.Parse("""{"name":"Lincoln High"}""")!,
            allowIdentityUpdates,
            new TraceId("write-executor-test"),
            new ReferenceResolverRequest(
                mappingSet,
                resourceWritePlan.Model.Resource,
                documentReferences ?? [],
                descriptorReferences ?? []
            ),
            targetContext: resolvedTargetContext
        );
    }

    private static string ExpectedEtag(RelationalWriteExecutorRequest request) =>
        RelationalApiMetadataFormatter.FormatEtag(request.SelectedBody);

    private static string ExpectedSelectedBodyEtag(RelationalWriteExecutorRequest request) =>
        RelationalApiMetadataFormatter.FormatEtag(request.SelectedBody);

    private static string ExpectedCommittedResponseEtag(JsonNode committedResponse) =>
        RelationalApiMetadataFormatter.FormatEtag(committedResponse);

    private static JsonNode CreateCommittedExternalResponse(
        RelationalWritePersistResult persistedTarget,
        JsonNode materializedBody
    )
    {
        var committedResponse = materializedBody.DeepClone();

        committedResponse.Should().BeOfType<JsonObject>();
        var committedObject = (JsonObject)committedResponse;
        committedObject["id"] = persistedTarget.DocumentUuid.Value.ToString();
        committedObject["_lastModifiedDate"] = "2026-04-02T12:00:00Z";
        committedObject["_etag"] = ExpectedCommittedResponseEtag(committedObject);

        return committedObject;
    }

    private static MappingSet CreateMappingSet(
        RelationalResourceModel resourceModel,
        IReadOnlyList<TableWritePlan>? tableWritePlans = null,
        SqlDialect dialect = SqlDialect.Pgsql
    )
    {
        var resolvedTableWritePlans = tableWritePlans ?? [CreateRootPlan()];
        var resource = resourceModel.Resource;
        var resourceKey = new ResourceKeyEntry(1, resource, "1.0.0", false);
        var descriptorResource = new QualifiedResourceName("Ed-Fi", "SchoolTypeDescriptor");
        var descriptorKey = new ResourceKeyEntry(13, descriptorResource, "1.0.0", true);
        var identityColumns = resourceModel
            .Root.Columns.Where(columnModel => columnModel.Kind == ColumnKind.Scalar)
            .Take(1)
            .ToArray();

        return new MappingSet(
            Key: new MappingSetKey("schema-hash", dialect, "v1"),
            Model: new DerivedRelationalModelSet(
                EffectiveSchema: new EffectiveSchemaInfo(
                    ApiSchemaFormatVersion: "1.0",
                    RelationalMappingVersion: "v1",
                    EffectiveSchemaHash: "schema-hash",
                    ResourceKeyCount: 2,
                    ResourceKeySeedHash: [1, 2, 3],
                    SchemaComponentsInEndpointOrder:
                    [
                        new SchemaComponentInfo("ed-fi", "Ed-Fi", "1.0.0", false, "component-hash"),
                    ],
                    ResourceKeysInIdOrder: [resourceKey, descriptorKey]
                ),
                Dialect: dialect,
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
                        new DbTriggerName("TR_School_DocumentStamping"),
                        resourceModel.Root.Table,
                        [new DbColumnName("DocumentId")],
                        identityColumns.Select(columnModel => columnModel.ColumnName).ToArray(),
                        new TriggerKindParameters.DocumentStamping()
                    ),
                    new DbTriggerInfo(
                        new DbTriggerName("TR_School_ReferentialIdentity"),
                        resourceModel.Root.Table,
                        [new DbColumnName("DocumentId")],
                        identityColumns.Select(columnModel => columnModel.ColumnName).ToArray(),
                        new TriggerKindParameters.ReferentialIdentityMaintenance(
                            resourceKey.ResourceKeyId,
                            resource.ProjectName,
                            resource.ResourceName,
                            identityColumns
                                .Select(columnModel => new IdentityElementMapping(
                                    columnModel.ColumnName,
                                    columnModel.SourceJsonPath?.Canonical
                                        ?? throw new InvalidOperationException(
                                            "Expected a root identity source path."
                                        ),
                                    columnModel.ScalarType
                                        ?? throw new InvalidOperationException(
                                            "Expected a root identity scalar type."
                                        )
                                ))
                                .ToArray()
                        )
                    ),
                ]
            ),
            WritePlansByResource: new Dictionary<QualifiedResourceName, ResourceWritePlan>
            {
                [resource] = new ResourceWritePlan(resourceModel, resolvedTableWritePlans),
            },
            ReadPlansByResource: new Dictionary<QualifiedResourceName, ResourceReadPlan>(),
            ResourceKeyIdByResource: new Dictionary<QualifiedResourceName, short>
            {
                [resource] = resourceKey.ResourceKeyId,
                [descriptorResource] = descriptorKey.ResourceKeyId,
            },
            ResourceKeyById: new Dictionary<short, ResourceKeyEntry>
            {
                [resourceKey.ResourceKeyId] = resourceKey,
                [descriptorKey.ResourceKeyId] = descriptorKey,
            },
            SecurableElementColumnPathsByResource: new Dictionary<
                QualifiedResourceName,
                IReadOnlyList<ResolvedSecurableElementPath>
            >()
        );
    }

    private static ResourceReadPlan CreateReadPlan(
        RelationalResourceModel resourceModel,
        SqlDialect dialect = SqlDialect.Pgsql
    )
    {
        var selectColumns = string.Join(
            ", ",
            resourceModel.Root.Columns.Select(column => QuoteIdentifier(column.ColumnName.Value, dialect))
        );
        var selectSql =
            $"select {selectColumns} from {QuoteIdentifier(resourceModel.Root.Table.Schema.Value, dialect)}."
            + $"{QuoteIdentifier(resourceModel.Root.Table.Name, dialect)}";

        return new ResourceReadPlan(
            resourceModel,
            KeysetTableConventions.GetKeysetTableContract(dialect),
            [new TableReadPlan(resourceModel.Root, selectSql)],
            [],
            []
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

    private static TableWritePlan CreateRootPlan()
    {
        var tableModel = new DbTableModel(
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
                    new JsonPathExpression("$.schoolId", []),
                    null,
                    new ColumnStorage.Stored()
                ),
                new DbColumnModel(
                    new DbColumnName("Name"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 75),
                    false,
                    new JsonPathExpression("$.name", []),
                    null,
                    new ColumnStorage.Stored()
                ),
            ],
            []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                DbTableKind.Root,
                [new DbColumnName("DocumentId")],
                [new DbColumnName("DocumentId")],
                [],
                []
            ),
        };

        return new TableWritePlan(
            tableModel,
            InsertSql: "insert into edfi.\"School\" values (@DocumentId, @SchoolId, @Name)",
            UpdateSql: "update edfi.\"School\" set \"SchoolId\" = @SchoolId, \"Name\" = @Name where \"DocumentId\" = @DocumentId",
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(100, 3, 1000),
            ColumnBindings:
            [
                new WriteColumnBinding(
                    tableModel.Columns[0],
                    new WriteValueSource.DocumentId(),
                    "DocumentId"
                ),
                new WriteColumnBinding(
                    tableModel.Columns[1],
                    new WriteValueSource.Scalar(
                        new JsonPathExpression("$.schoolId", []),
                        new RelationalScalarType(ScalarKind.Int32)
                    ),
                    "SchoolId"
                ),
                new WriteColumnBinding(
                    tableModel.Columns[2],
                    new WriteValueSource.Scalar(
                        new JsonPathExpression("$.name", []),
                        new RelationalScalarType(ScalarKind.String, MaxLength: 75)
                    ),
                    "Name"
                ),
            ],
            KeyUnificationPlans: []
        );
    }

    private static TableWritePlan CreateDateAndTimeRootPlan()
    {
        var tableModel = new DbTableModel(
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
                    new DbColumnName("SessionDate"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Date),
                    false,
                    new JsonPathExpression("$.sessionDate", [new JsonPathSegment.Property("sessionDate")]),
                    null,
                    new ColumnStorage.Stored()
                ),
                new DbColumnModel(
                    new DbColumnName("StartTime"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Time),
                    false,
                    new JsonPathExpression("$.startTime", [new JsonPathSegment.Property("startTime")]),
                    null,
                    new ColumnStorage.Stored()
                ),
            ],
            []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                DbTableKind.Root,
                [new DbColumnName("DocumentId")],
                [new DbColumnName("DocumentId")],
                [],
                []
            ),
        };

        return new TableWritePlan(
            tableModel,
            InsertSql: "insert into edfi.\"School\" values (@DocumentId, @SessionDate, @StartTime)",
            UpdateSql: "update edfi.\"School\" set \"SessionDate\" = @SessionDate, \"StartTime\" = @StartTime where \"DocumentId\" = @DocumentId",
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(100, 3, 1000),
            ColumnBindings:
            [
                new WriteColumnBinding(
                    tableModel.Columns[0],
                    new WriteValueSource.DocumentId(),
                    "DocumentId"
                ),
                new WriteColumnBinding(
                    tableModel.Columns[1],
                    new WriteValueSource.Scalar(
                        new JsonPathExpression(
                            "$.sessionDate",
                            [new JsonPathSegment.Property("sessionDate")]
                        ),
                        new RelationalScalarType(ScalarKind.Date)
                    ),
                    "SessionDate"
                ),
                new WriteColumnBinding(
                    tableModel.Columns[2],
                    new WriteValueSource.Scalar(
                        new JsonPathExpression("$.startTime", [new JsonPathSegment.Property("startTime")]),
                        new RelationalScalarType(ScalarKind.Time)
                    ),
                    "StartTime"
                ),
            ],
            KeyUnificationPlans: []
        );
    }

    private static string QuoteIdentifier(string identifier, SqlDialect dialect) =>
        dialect == SqlDialect.Mssql ? $"[{identifier}]" : $"\"{identifier}\"";

    private sealed class RecordingReferenceResolverAdapterFactory : IReferenceResolverAdapterFactory
    {
        public RecordingReferenceResolverAdapter Adapter { get; } = new();

        public DbConnection? CapturedConnection { get; private set; }

        public DbTransaction? CapturedTransaction { get; private set; }

        public int CreateAdapterCallCount { get; private set; }

        public int CreateSessionAdapterCallCount { get; private set; }

        public IReferenceResolverAdapter CreateAdapter()
        {
            CreateAdapterCallCount++;
            return Adapter;
        }

        public IReferenceResolverAdapter CreateSessionAdapter(
            DbConnection connection,
            DbTransaction transaction
        )
        {
            CreateSessionAdapterCallCount++;
            CapturedConnection = connection;
            CapturedTransaction = transaction;
            return Adapter;
        }
    }

    private sealed class RecordingRelationalWriteFreshnessChecker : IRelationalWriteFreshnessChecker
    {
        public int IsCurrentCallCount { get; private set; }

        public RelationalWriteExecutorRequest? CapturedRequest { get; private set; }

        public RelationalWriteTargetContext.ExistingDocument? CapturedTargetContext { get; private set; }

        public IRelationalWriteSession? CapturedWriteSession { get; private set; }

        public bool IsCurrentResult { get; set; } = true;

        public Func<RelationalWriteTargetContext.ExistingDocument, bool>? IsCurrentEvaluator { get; set; }

        public Task<bool> IsCurrentAsync(
            RelationalWriteExecutorRequest request,
            RelationalWriteTargetContext.ExistingDocument targetContext,
            IRelationalWriteSession writeSession,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            IsCurrentCallCount++;
            CapturedRequest = request;
            CapturedTargetContext = targetContext;
            CapturedWriteSession = writeSession;

            return Task.FromResult(IsCurrentEvaluator?.Invoke(targetContext) ?? IsCurrentResult);
        }
    }

    private sealed class RecordingReferenceResolverAdapter : IReferenceResolverAdapter
    {
        public List<ReferenceLookupRequest> Requests { get; } = [];

        public IReadOnlyList<ReferenceLookupResult> LookupResults { get; set; } = [];

        public Task<IReadOnlyList<ReferenceLookupResult>> ResolveAsync(
            ReferenceLookupRequest request,
            CancellationToken cancellationToken = default
        )
        {
            Requests.Add(request);
            return Task.FromResult(LookupResults);
        }
    }

    private sealed class RecordingRelationalWriteFlattener : IRelationalWriteFlattener
    {
        public int FlattenCallCount { get; private set; }

        public FlatteningInput? CapturedInput { get; private set; }

        public Exception? ExceptionToThrow { get; set; }

        public FlattenedWriteSet? ResultToReturn { get; set; }

        public FlattenedWriteSet Flatten(FlatteningInput flatteningInput)
        {
            FlattenCallCount++;
            CapturedInput = flatteningInput;

            if (ExceptionToThrow is not null)
            {
                throw ExceptionToThrow;
            }

            return ResultToReturn
                ?? new FlattenedWriteSet(
                    new RootWriteRowBuffer(
                        flatteningInput.WritePlan.TablePlansInDependencyOrder.Single(),
                        [
                            flatteningInput.OperationKind == RelationalWriteOperationKind.Put
                                ? new FlattenedWriteValue.Literal(345L)
                                : FlattenedWriteValue.UnresolvedRootDocumentId.Instance,
                            new FlattenedWriteValue.Literal(255901),
                            new FlattenedWriteValue.Literal("Lincoln High"),
                        ]
                    )
                );
        }
    }

    private sealed class RecordingRelationalWriteTargetLookupResolver : IRelationalWriteTargetLookupResolver
    {
        public int ResolveForPostCallCount { get; private set; }

        public IRelationalWriteSession? CapturedWriteSession { get; private set; }

        public Queue<RelationalWriteTargetLookupResult> PostResults { get; } = [];

        public Task<RelationalWriteTargetLookupResult> ResolveForPostAsync(
            MappingSet mappingSet,
            QualifiedResourceName resource,
            ReferentialId referentialId,
            DocumentUuid candidateDocumentUuid,
            DbConnection connection,
            DbTransaction transaction,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            ResolveForPostCallCount++;
            CapturedWriteSession = new CapturedRelationalWriteSession(connection, transaction);

            return Task.FromResult(
                PostResults.Count > 0
                    ? PostResults.Dequeue()
                    : new RelationalWriteTargetLookupResult.CreateNew(candidateDocumentUuid)
            );
        }

        private sealed class CapturedRelationalWriteSession(
            DbConnection connection,
            DbTransaction transaction
        ) : IRelationalWriteSession
        {
            public DbConnection Connection { get; } = connection;

            public DbTransaction Transaction { get; } = transaction;

            public DbCommand CreateCommand(RelationalCommand command) => throw new NotSupportedException();

            public Task CommitAsync(CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();

            public Task RollbackAsync(CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingRelationalWriteCurrentStateLoader : IRelationalWriteCurrentStateLoader
    {
        public int LoadCallCount { get; private set; }

        public RelationalWriteCurrentStateLoadRequest? CapturedRequest { get; private set; }

        public List<RelationalWriteCurrentStateLoadRequest> CapturedRequests { get; } = [];

        public IRelationalWriteSession? CapturedWriteSession { get; private set; }

        public List<IRelationalWriteSession> CapturedWriteSessions { get; } = [];

        public RelationalWriteCurrentState? ResultToReturn { get; set; }

        public Queue<RelationalWriteCurrentState?> QueuedResults { get; } = [];

        public bool ReturnMissingTarget { get; set; }

        public Task<RelationalWriteCurrentState?> LoadAsync(
            RelationalWriteCurrentStateLoadRequest request,
            IRelationalWriteSession writeSession,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            LoadCallCount++;
            CapturedRequest = request;
            CapturedRequests.Add(request);
            CapturedWriteSession = writeSession;
            CapturedWriteSessions.Add(writeSession);

            if (QueuedResults.Count > 0)
            {
                return Task.FromResult(QueuedResults.Dequeue());
            }

            if (ReturnMissingTarget)
            {
                return Task.FromResult<RelationalWriteCurrentState?>(null);
            }

            return Task.FromResult<RelationalWriteCurrentState?>(
                ResultToReturn
                    ?? new RelationalWriteCurrentState(
                        new DocumentMetadataRow(
                            request.TargetContext.DocumentId,
                            request.TargetContext.DocumentUuid.Value,
                            request.TargetContext.ObservedContentVersion,
                            request.TargetContext.ObservedContentVersion,
                            DateTimeOffset.UnixEpoch,
                            DateTimeOffset.UnixEpoch
                        ),
                        [
                            new HydratedTableRows(
                                request.ReadPlan.Model.Root,
                                [
                                    [345L, 255901, "Lincoln High"],
                                ]
                            ),
                        ],
                        []
                    )
            );
        }
    }

    private sealed class RecordingRelationalCommittedRepresentationReader
        : IRelationalCommittedRepresentationReader
    {
        public int ReadCallCount { get; private set; }

        public RelationalWriteExecutorRequest? CapturedRequest { get; private set; }

        public RelationalWritePersistResult? CapturedPersistedTarget { get; private set; }

        public IRelationalWriteSession? CapturedWriteSession { get; private set; }

        public int? CommitCallCountObservedDuringRead { get; private set; }

        public JsonNode? ResultToReturn { get; set; }

        public Task<JsonNode> ReadAsync(
            RelationalWriteExecutorRequest request,
            RelationalWritePersistResult persistedTarget,
            IRelationalWriteSession writeSession,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadCallCount++;
            CapturedRequest = request;
            CapturedPersistedTarget = persistedTarget;
            CapturedWriteSession = writeSession;
            CommitCallCountObservedDuringRead = (
                writeSession as RecordingRelationalWriteSession
            )?.CommitCallCount;

            return Task.FromResult(
                ResultToReturn ?? CreateCommittedExternalResponse(persistedTarget, request.SelectedBody)
            );
        }
    }

    private sealed class RecordingRelationalWriteNoProfileMergeSynthesizer
        : IRelationalWriteNoProfileMergeSynthesizer
    {
        public int SynthesizeCallCount { get; private set; }

        public RelationalWriteNoProfileMergeRequest? CapturedRequest { get; private set; }

        public RelationalWriteMergeResult? ResultToReturn { get; set; }

        public RelationalWriteMergeResult Synthesize(RelationalWriteNoProfileMergeRequest request)
        {
            SynthesizeCallCount++;
            CapturedRequest = request;

            return ResultToReturn
                ?? new RelationalWriteMergeResult(
                    [
                        new RelationalWriteMergedTableState(
                            request.WritePlan.TablePlansInDependencyOrder[0],
                            [
                                new RelationalWriteMergedTableRow(
                                    request.FlattenedWriteSet.RootRow.Values,
                                    request.FlattenedWriteSet.RootRow.Values
                                ),
                            ],
                            [
                                new RelationalWriteMergedTableRow(
                                    request.FlattenedWriteSet.RootRow.Values,
                                    request.FlattenedWriteSet.RootRow.Values
                                ),
                            ]
                        ),
                    ],
                    supportsGuardedNoOp: true
                );
        }
    }

    private sealed class RecordingRelationalWriteProfileMergeSynthesizer
        : IRelationalWriteProfileMergeSynthesizer
    {
        public int SynthesizeCallCount { get; private set; }

        public RelationalWriteProfileMergeRequest? CapturedRequest { get; private set; }

        public RelationalWriteMergeResult? ResultToReturn { get; set; }

        public ProfileCreatabilityRejection? RejectionToReturn { get; set; }

        public ProfileMergeOutcome Synthesize(RelationalWriteProfileMergeRequest request)
        {
            SynthesizeCallCount++;
            CapturedRequest = request;

            if (RejectionToReturn is not null)
            {
                return ProfileMergeOutcome.Reject(RejectionToReturn);
            }

            return ProfileMergeOutcome.Success(
                ResultToReturn
                    ?? new RelationalWriteMergeResult(
                        [
                            new RelationalWriteMergedTableState(
                                request.WritePlan.TablePlansInDependencyOrder[0],
                                [],
                                [
                                    new RelationalWriteMergedTableRow(
                                        request.FlattenedWriteSet.RootRow.Values,
                                        request.FlattenedWriteSet.RootRow.Values
                                    ),
                                ]
                            ),
                        ],
                        supportsGuardedNoOp: false
                    )
            );
        }
    }

    private sealed class RecordingRelationalWriteNoProfilePersister : IRelationalWritePersister
    {
        public int TryPersistCallCount { get; private set; }

        public RelationalWriteExecutorRequest? CapturedRequest { get; private set; }

        public RelationalWriteMergeResult? CapturedMergeResult { get; private set; }

        public IRelationalWriteSession? CapturedWriteSession { get; private set; }

        public Exception? ExceptionToThrow { get; set; }

        public RelationalWritePersistResult? ResultToReturn { get; set; }

        public Task<RelationalWritePersistResult> PersistAsync(
            RelationalWriteExecutorRequest request,
            RelationalWriteMergeResult mergeResult,
            IRelationalWriteSession writeSession,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            TryPersistCallCount++;
            CapturedRequest = request;
            CapturedMergeResult = mergeResult;
            CapturedWriteSession = writeSession;

            if (ExceptionToThrow is not null)
            {
                throw ExceptionToThrow;
            }

            return Task.FromResult(ResultToReturn ?? CreateDefaultResult(request));
        }

        private static RelationalWritePersistResult CreateDefaultResult(
            RelationalWriteExecutorRequest request
        ) =>
            request.TargetContext switch
            {
                RelationalWriteTargetContext.CreateNew(var documentUuid) => new(910L, documentUuid),
                RelationalWriteTargetContext.ExistingDocument(var documentId, var documentUuid, _) => new(
                    documentId,
                    documentUuid
                ),
                _ => throw new ArgumentOutOfRangeException(nameof(request), request, null),
            };
    }

    private static RelationalWriteMergeResult CreateMergeResult(
        TableWritePlan rootTableWritePlan,
        int currentSchoolId,
        int mergedSchoolId,
        string currentName = "Lincoln High",
        string mergedName = "Lincoln High"
    ) =>
        new(
            [
                new RelationalWriteMergedTableState(
                    rootTableWritePlan,
                    [CreateRootTableRow(345L, currentSchoolId, currentName)],
                    [CreateRootTableRow(345L, mergedSchoolId, mergedName)]
                ),
            ],
            supportsGuardedNoOp: true
        );

    private static RelationalWriteCurrentState CreateCurrentState(
        RelationalWriteExecutorRequest request,
        long contentVersion,
        string schoolName = "Lincoln High"
    )
    {
        var targetContext =
            request.TargetContext as RelationalWriteTargetContext.ExistingDocument
            ?? throw new InvalidOperationException("Expected an existing-document target context.");

        return new RelationalWriteCurrentState(
            new DocumentMetadataRow(
                targetContext.DocumentId,
                targetContext.DocumentUuid.Value,
                contentVersion,
                contentVersion,
                new DateTimeOffset(2026, 4, 2, 12, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 4, 2, 12, 0, 0, TimeSpan.Zero)
            ),
            [
                new HydratedTableRows(
                    request.WritePlan.Model.Root,
                    [
                        [targetContext.DocumentId, 255901, schoolName],
                    ]
                ),
            ],
            []
        );
    }

    private static RelationalWriteMergedTableRow CreateRootTableRow(
        long documentId,
        int schoolId,
        string name
    ) =>
        new(
            [
                new FlattenedWriteValue.Literal(documentId),
                new FlattenedWriteValue.Literal(schoolId),
                new FlattenedWriteValue.Literal(name),
            ],
            [
                new FlattenedWriteValue.Literal(documentId),
                new FlattenedWriteValue.Literal(schoolId),
                new FlattenedWriteValue.Literal(name),
            ]
        );

    private sealed class RecordingRelationalWriteSessionFactory : IRelationalWriteSessionFactory
    {
        public RecordingRelationalWriteSession Session { get; } = new();

        public int CreateAsyncCallCount { get; private set; }

        public Task<IRelationalWriteSession> CreateAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CreateAsyncCallCount++;
            return Task.FromResult<IRelationalWriteSession>(Session);
        }
    }

    private sealed class RecordingRelationalWriteSession : IRelationalWriteSession
    {
        public RecordingRelationalWriteSession()
        {
            Connection = new StubDbConnection();
            Transaction = new StubDbTransaction(Connection);
        }

        public DbConnection Connection { get; }

        public DbTransaction Transaction { get; }

        public List<RelationalCommand> Commands { get; } = [];

        public int CommitCallCount { get; private set; }

        public int RollbackCallCount { get; private set; }

        public int DisposeCallCount { get; private set; }

        public Exception? CommitExceptionToThrow { get; set; }

        public object? ScalarResultToReturn { get; set; } = 45L;

        public DbCommand CreateCommand(RelationalCommand command)
        {
            Commands.Add(command);
            return new RecordingDbCommand(ScalarResultToReturn);
        }

        public Task CommitAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CommitCallCount++;

            if (CommitExceptionToThrow is not null)
            {
                throw CommitExceptionToThrow;
            }

            return Task.CompletedTask;
        }

        public Task RollbackAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RollbackCallCount++;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCallCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingDbCommand(object? scalarResult) : DbCommand
    {
        protected override DbConnection? DbConnection { get; set; }

        protected override DbParameterCollection DbParameterCollection { get; } =
            new StubDbParameterCollection();

        protected override DbTransaction? DbTransaction { get; set; }

        [AllowNull]
        public override string CommandText { get; set; } = string.Empty;

        public override int CommandTimeout { get; set; }

        public override CommandType CommandType { get; set; }

        public override bool DesignTimeVisible { get; set; }

        public override UpdateRowSource UpdatedRowSource { get; set; }

        public override void Cancel() { }

        public override int ExecuteNonQuery() => throw new NotSupportedException();

        public override object? ExecuteScalar() => scalarResult;

        public override void Prepare() { }

        protected override DbParameter CreateDbParameter() => new StubDbParameter();

        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) =>
            throw new NotSupportedException();

        protected override Task<DbDataReader> ExecuteDbDataReaderAsync(
            CommandBehavior behavior,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public override Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(scalarResult);
        }
    }

    private sealed class StubDbParameterCollection : DbParameterCollection
    {
        public override int Count => 0;

        public override object SyncRoot => this;

        public override int Add(object value) => 0;

        public override void AddRange(Array values) { }

        public override void Clear() { }

        public override bool Contains(object value) => false;

        public override bool Contains(string value) => false;

        public override void CopyTo(Array array, int index) { }

        public override System.Collections.IEnumerator GetEnumerator() =>
            Array.Empty<object>().GetEnumerator();

        protected override DbParameter GetParameter(int index) => throw new IndexOutOfRangeException();

        protected override DbParameter GetParameter(string parameterName) =>
            throw new IndexOutOfRangeException();

        public override int IndexOf(object value) => -1;

        public override int IndexOf(string parameterName) => -1;

        public override void Insert(int index, object value) { }

        public override void Remove(object value) { }

        public override void RemoveAt(int index) { }

        public override void RemoveAt(string parameterName) { }

        protected override void SetParameter(int index, DbParameter value) { }

        protected override void SetParameter(string parameterName, DbParameter value) { }
    }

    private sealed class StubDbParameter : DbParameter
    {
        public override DbType DbType { get; set; }

        public override ParameterDirection Direction { get; set; }

        public override bool IsNullable { get; set; }

        [AllowNull]
        public override string ParameterName { get; set; } = string.Empty;

        [AllowNull]
        public override string SourceColumn { get; set; } = string.Empty;

        public override object? Value { get; set; }

        public override bool SourceColumnNullMapping { get; set; }

        public override int Size { get; set; }

        public override void ResetDbType() { }
    }

    private sealed class StubDbConnection : DbConnection
    {
        [AllowNull]
        public override string ConnectionString { get; set; } = string.Empty;

        public override string Database => "stub";

        public override string DataSource => "stub";

        public override string ServerVersion => "1.0";

        public override ConnectionState State => ConnectionState.Open;

        public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();

        public override void Close() { }

        public override void Open() { }

        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) =>
            throw new NotSupportedException();

        protected override DbCommand CreateDbCommand() => throw new NotSupportedException();
    }

    private sealed class StubDbTransaction(DbConnection connection) : DbTransaction
    {
        public override IsolationLevel IsolationLevel => IsolationLevel.ReadCommitted;

        protected override DbConnection DbConnection => connection;

        public override void Commit() => throw new NotSupportedException();

        public override void Rollback() => throw new NotSupportedException();
    }

    private sealed class RecordingRelationalWriteExceptionClassifier : IRelationalWriteExceptionClassifier
    {
        public int TryClassifyCallCount { get; private set; }

        public DbException? CapturedException { get; private set; }

        public Exception? ExceptionToThrow { get; set; }

        public RelationalWriteExceptionClassification? ClassificationToReturn { get; set; }

        public bool TryClassify(
            DbException exception,
            [NotNullWhen(true)] out RelationalWriteExceptionClassification? classification
        )
        {
            TryClassifyCallCount++;
            CapturedException = exception;

            if (ExceptionToThrow is not null)
            {
                throw ExceptionToThrow;
            }

            classification = ClassificationToReturn;
            return classification is not null;
        }

        public bool IsForeignKeyViolation(DbException exception) =>
            ClassificationToReturn is RelationalWriteExceptionClassification.ForeignKeyConstraintViolation;

        public bool IsUniqueConstraintViolation(DbException exception) =>
            ClassificationToReturn is RelationalWriteExceptionClassification.UniqueConstraintViolation;

        public bool IsTransientFailure(DbException exception) => false;
    }

    private sealed class RecordingRelationalWriteConstraintResolver : IRelationalWriteConstraintResolver
    {
        public int ResolveCallCount { get; private set; }

        public RelationalWriteConstraintResolutionRequest? CapturedRequest { get; private set; }

        public Exception? ExceptionToThrow { get; set; }

        public RelationalWriteConstraintResolution ResolutionToReturn { get; set; } =
            new RelationalWriteConstraintResolution.Unresolved("UNCONFIGURED");

        public RelationalWriteConstraintResolution Resolve(RelationalWriteConstraintResolutionRequest request)
        {
            ResolveCallCount++;
            CapturedRequest = request;

            if (ExceptionToThrow is not null)
            {
                throw ExceptionToThrow;
            }

            return ResolutionToReturn;
        }
    }

    private sealed class RecordingRelationalReadMaterializer : IRelationalReadMaterializer
    {
        public int MaterializeCallCount { get; private set; }
        public RelationalReadMaterializationRequest? CapturedRequest { get; private set; }
        public JsonNode ResultToReturn { get; set; } = JsonNode.Parse("""{"reconstituted":true}""")!;

        public JsonNode Materialize(RelationalReadMaterializationRequest request)
        {
            MaterializeCallCount++;
            CapturedRequest = request;
            return ResultToReturn;
        }

        public IReadOnlyList<MaterializedDocument> MaterializePage(
            RelationalReadPageMaterializationRequest request
        )
        {
            ArgumentNullException.ThrowIfNull(request);

            return
            [
                .. request.HydratedPage.DocumentMetadata.Select(documentMetadata => new MaterializedDocument(
                    documentMetadata,
                    ResultToReturn.DeepClone()
                )),
            ];
        }
    }

    // ── Slice 4 executor fence routing tests ────────────────────────────────

    [Test]
    public async Task Given_Executor_for_TopLevelCollection_family_with_root_inlined_scope_passes_fence()
    {
        // Slice 4 composes with earlier slices: a top-level collection row stream plus
        // a root-hosted inlined scope must still reach the profile merge synthesizer.
        var writableBody = JsonNode.Parse("""{"schoolId":255901}""")!;
        // Use CreateRootPlan() which has the proper 3-column shape (DocumentId, SchoolId, Name)
        // so FlattenedWriteSet can provide matching values for all ColumnBindings.
        var rootPlan = CreateRootPlan();
        var collectionPlan = FenceTestPlans.CreateCollectionTablePlan(
            "$.addresses[*]",
            "Addresses",
            DbTableKind.Collection
        );
        var resourceModel = new RelationalResourceModel(
            Resource: new QualifiedResourceName("Ed-Fi", "School"),
            PhysicalSchema: new DbSchemaName("edfi"),
            StorageKind: ResourceStorageKind.RelationalTables,
            Root: rootPlan.TableModel,
            TablesInDependencyOrder: [rootPlan.TableModel, collectionPlan.TableModel],
            DocumentReferenceBindings: [],
            DescriptorEdgeSources: []
        );
        var resourceWritePlan = new ResourceWritePlan(resourceModel, [rootPlan, collectionPlan]);
        var scopeCatalog = CompiledScopeAdapterFactory.BuildFromWritePlan(
            resourceWritePlan,
            [("$.profileScope", ScopeKind.NonCollection)]
        );
        var collectionRowAddress = new CollectionRowAddress(
            "$.addresses[*]",
            new ScopeInstanceAddress("$", []),
            []
        );
        var profileContext = new BackendProfileWriteContext(
            Request: new ProfileAppliedWriteRequest(
                WritableRequestBody: writableBody,
                RootResourceCreatable: true,
                RequestScopeStates:
                [
                    new RequestScopeState(
                        Address: new ScopeInstanceAddress("$", []),
                        Visibility: ProfileVisibilityKind.VisiblePresent,
                        Creatable: true
                    ),
                    new RequestScopeState(
                        Address: new ScopeInstanceAddress("$.profileScope", []),
                        Visibility: ProfileVisibilityKind.VisiblePresent,
                        Creatable: true
                    ),
                ],
                VisibleRequestCollectionItems:
                [
                    new VisibleRequestCollectionItem(collectionRowAddress, Creatable: true, "$.addresses[0]"),
                ]
            ),
            ProfileName: "test-write-profile",
            CompiledScopeCatalog: scopeCatalog,
            StoredStateProjectionInvoker: A.Fake<IStoredStateProjectionInvoker>()
        );

        _writeFlattener.ResultToReturn = new FlattenedWriteSet(
            new RootWriteRowBuffer(
                rootPlan,
                [
                    FlattenedWriteValue.UnresolvedRootDocumentId.Instance,
                    new FlattenedWriteValue.Literal(255901),
                    new FlattenedWriteValue.Literal("Lincoln High"),
                ]
            )
        );

        var baseRequest = CreateRequest(RelationalWriteOperationKind.Post, selectedBody: writableBody);
        var request = baseRequest with
        {
            WritePlan = resourceWritePlan,
            ProfileWriteContext = profileContext,
        };

        var result = await _sut.ExecuteAsync(request);

        _writeFlattener
            .FlattenCallCount.Should()
            .Be(1, "TopLevelCollection without collection-aligned separate-table scope must reach flattener");
        _profileMergeSynthesizer
            .SynthesizeCallCount.Should()
            .Be(1, "profile merge must run once the Slice 4 fence passes");
        _noProfilePersister.TryPersistCallCount.Should().Be(1);
        _writeSessionFactory.Session.CommitCallCount.Should().Be(1);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(0);

        var upsertResult = result.Should().BeOfType<RelationalWriteExecutorResult.Upsert>().Subject;
        upsertResult.Result.Should().BeOfType<UpsertResult.InsertSuccess>();
    }

    [Test]
    public async Task Given_Executor_for_TopLevelCollection_family_with_collection_descendant_inlined_scope_still_fences()
    {
        // Slice 4 supports the table-backed collection row stream, not inlined descendant
        // scope metadata stored inside that row. Those descendants remain a later-slice shape.
        var writableBody = JsonNode.Parse("""{"schoolId":255901,"addresses":[{"city":"Austin"}]}""")!;
        var rootPlan = FenceTestPlans.RootTablePlan();
        var collectionPlan = FenceTestPlans.CreateCollectionTablePlan(
            "$.addresses[*]",
            "Addresses",
            DbTableKind.Collection
        );
        var resourceModel = new RelationalResourceModel(
            Resource: new QualifiedResourceName("Ed-Fi", "School"),
            PhysicalSchema: new DbSchemaName("edfi"),
            StorageKind: ResourceStorageKind.RelationalTables,
            Root: rootPlan.TableModel,
            TablesInDependencyOrder: [rootPlan.TableModel, collectionPlan.TableModel],
            DocumentReferenceBindings: [],
            DescriptorEdgeSources: []
        );
        var resourceWritePlan = new ResourceWritePlan(resourceModel, [rootPlan, collectionPlan]);
        var scopeCatalog = CompiledScopeAdapterFactory.BuildFromWritePlan(
            resourceWritePlan,
            [("$.addresses[*].mileInfo", ScopeKind.NonCollection)]
        );
        var addressInstance = new AncestorCollectionInstance(
            JsonScope: "$.addresses[*]",
            SemanticIdentityInOrder: []
        );
        var profileContext = new BackendProfileWriteContext(
            Request: new ProfileAppliedWriteRequest(
                WritableRequestBody: writableBody,
                RootResourceCreatable: true,
                RequestScopeStates:
                [
                    new RequestScopeState(
                        Address: new ScopeInstanceAddress("$", []),
                        Visibility: ProfileVisibilityKind.VisiblePresent,
                        Creatable: true
                    ),
                    new RequestScopeState(
                        Address: new ScopeInstanceAddress("$.addresses[*].mileInfo", [addressInstance]),
                        Visibility: ProfileVisibilityKind.VisiblePresent,
                        Creatable: true
                    ),
                ],
                VisibleRequestCollectionItems: []
            ),
            ProfileName: "test-write-profile",
            CompiledScopeCatalog: scopeCatalog,
            StoredStateProjectionInvoker: A.Fake<IStoredStateProjectionInvoker>()
        );

        var baseRequest = CreateRequest(RelationalWriteOperationKind.Post, selectedBody: writableBody);
        var request = baseRequest with
        {
            WritePlan = resourceWritePlan,
            ProfileWriteContext = profileContext,
        };

        var result = await _sut.ExecuteAsync(request);

        _writeFlattener
            .FlattenCallCount.Should()
            .Be(0, "collection-descendant inlined scopes remain fenced before flattening");
        _profileMergeSynthesizer.SynthesizeCallCount.Should().Be(0);
        _noProfilePersister.TryPersistCallCount.Should().Be(0);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
        _writeSessionFactory.Session.CommitCallCount.Should().Be(0);

        var upsertResult = result.Should().BeOfType<RelationalWriteExecutorResult.Upsert>().Subject;
        upsertResult
            .Result.Should()
            .BeOfType<UpsertResult.UnknownFailure>()
            .Which.FailureMessage.Should()
            .Contain("TopLevelCollection");
    }

    [Test]
    public async Task Given_Executor_for_TopLevelCollection_family_with_collection_extension_scope_still_fences()
    {
        // Slice 4 preserves Slice 3's guard: when a request maxed at TopLevelCollection also
        // exercises a CollectionExtensionScope, the fence must still reject.
        var writableBody = JsonNode.Parse("""{"schoolId":255901}""")!;
        var rootPlan = FenceTestPlans.RootTablePlan();
        var collectionPlan = FenceTestPlans.CreateCollectionTablePlan(
            "$.addresses[*]",
            "Addresses",
            DbTableKind.Collection
        );
        var collectionExtPlan = FenceTestPlans.CreateTablePlan(
            "$.addresses[*]._ext.sample",
            "AddressesExtSample",
            DbTableKind.CollectionExtensionScope
        );
        var resourceModel = new RelationalResourceModel(
            Resource: new QualifiedResourceName("Ed-Fi", "School"),
            PhysicalSchema: new DbSchemaName("edfi"),
            StorageKind: ResourceStorageKind.RelationalTables,
            Root: rootPlan.TableModel,
            TablesInDependencyOrder:
            [
                rootPlan.TableModel,
                collectionPlan.TableModel,
                collectionExtPlan.TableModel,
            ],
            DocumentReferenceBindings: [],
            DescriptorEdgeSources: []
        );
        var resourceWritePlan = new ResourceWritePlan(
            resourceModel,
            [rootPlan, collectionPlan, collectionExtPlan]
        );
        var scopeCatalog = CompiledScopeAdapterFactory.BuildFromWritePlan(resourceWritePlan);
        var collectionRowAddress = new CollectionRowAddress(
            "$.addresses[*]",
            new ScopeInstanceAddress("$", []),
            []
        );
        var profileContext = new BackendProfileWriteContext(
            Request: new ProfileAppliedWriteRequest(
                WritableRequestBody: writableBody,
                RootResourceCreatable: true,
                RequestScopeStates:
                [
                    new RequestScopeState(
                        Address: new ScopeInstanceAddress("$", []),
                        Visibility: ProfileVisibilityKind.VisiblePresent,
                        Creatable: true
                    ),
                    // $.addresses[*]._ext.sample is a CollectionExtensionScope whose
                    // ancestor chain must include the $.addresses[*] collection instance
                    // so the contract validator does not reject before the fence runs.
                    new RequestScopeState(
                        Address: new ScopeInstanceAddress(
                            "$.addresses[*]._ext.sample",
                            [
                                new AncestorCollectionInstance(
                                    JsonScope: "$.addresses[*]",
                                    SemanticIdentityInOrder: []
                                ),
                            ]
                        ),
                        Visibility: ProfileVisibilityKind.VisibleAbsent,
                        Creatable: true
                    ),
                ],
                VisibleRequestCollectionItems:
                [
                    new VisibleRequestCollectionItem(collectionRowAddress, Creatable: true, "$.addresses[0]"),
                ]
            ),
            ProfileName: "test-write-profile",
            CompiledScopeCatalog: scopeCatalog,
            StoredStateProjectionInvoker: A.Fake<IStoredStateProjectionInvoker>()
        );

        var baseRequest = CreateRequest(RelationalWriteOperationKind.Post, selectedBody: writableBody);
        var request = baseRequest with
        {
            WritePlan = resourceWritePlan,
            ProfileWriteContext = profileContext,
        };

        var result = await _sut.ExecuteAsync(request);

        _writeFlattener
            .FlattenCallCount.Should()
            .Be(0, "fence must fire before flattening when TopLevelCollection + CollectionExtensionScope");
        _profileMergeSynthesizer
            .SynthesizeCallCount.Should()
            .Be(0, "profile merge must not run when Slice 3 guard triggers inside Slice 4 family");
        _noProfilePersister.TryPersistCallCount.Should().Be(0);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
        _writeSessionFactory.Session.CommitCallCount.Should().Be(0);

        var upsertResult = result.Should().BeOfType<RelationalWriteExecutorResult.Upsert>().Subject;
        upsertResult
            .Result.Should()
            .BeOfType<UpsertResult.UnknownFailure>()
            .Which.FailureMessage.Should()
            .Contain("TopLevelCollection");
    }

    [Test]
    public async Task Given_Executor_for_NestedAndExtensionCollections_still_fences()
    {
        // Regression: NestedAndExtensionCollections must remain fenced after Slice 4.
        var writableBody = JsonNode.Parse("""{"schoolId":255901}""")!;
        var rootPlan = FenceTestPlans.RootTablePlan();
        var parentCollectionPlan = FenceTestPlans.CreateCollectionTablePlan(
            "$.addresses[*]",
            "Addresses",
            DbTableKind.Collection
        );
        var nestedCollectionPlan = FenceTestPlans.CreateCollectionTablePlan(
            "$.addresses[*].periods[*]",
            "AddressPeriods",
            DbTableKind.Collection
        );
        var resourceModel = new RelationalResourceModel(
            Resource: new QualifiedResourceName("Ed-Fi", "School"),
            PhysicalSchema: new DbSchemaName("edfi"),
            StorageKind: ResourceStorageKind.RelationalTables,
            Root: rootPlan.TableModel,
            TablesInDependencyOrder:
            [
                rootPlan.TableModel,
                parentCollectionPlan.TableModel,
                nestedCollectionPlan.TableModel,
            ],
            DocumentReferenceBindings: [],
            DescriptorEdgeSources: []
        );
        var resourceWritePlan = new ResourceWritePlan(
            resourceModel,
            [rootPlan, parentCollectionPlan, nestedCollectionPlan]
        );
        var scopeCatalog = CompiledScopeAdapterFactory.BuildFromWritePlan(resourceWritePlan);
        var parentAddressInstance = new AncestorCollectionInstance(
            JsonScope: "$.addresses[*]",
            SemanticIdentityInOrder: []
        );
        var nestedRowAddress = new CollectionRowAddress(
            "$.addresses[*].periods[*]",
            new ScopeInstanceAddress("$.addresses[*]", [parentAddressInstance]),
            []
        );
        var profileContext = new BackendProfileWriteContext(
            Request: new ProfileAppliedWriteRequest(
                WritableRequestBody: writableBody,
                RootResourceCreatable: true,
                RequestScopeStates:
                [
                    new RequestScopeState(
                        Address: new ScopeInstanceAddress("$", []),
                        Visibility: ProfileVisibilityKind.VisiblePresent,
                        Creatable: true
                    ),
                ],
                VisibleRequestCollectionItems:
                [
                    new VisibleRequestCollectionItem(
                        nestedRowAddress,
                        Creatable: true,
                        "$.addresses[0].periods[0]"
                    ),
                ]
            ),
            ProfileName: "test-write-profile",
            CompiledScopeCatalog: scopeCatalog,
            StoredStateProjectionInvoker: A.Fake<IStoredStateProjectionInvoker>()
        );

        var baseRequest = CreateRequest(RelationalWriteOperationKind.Post, selectedBody: writableBody);
        var request = baseRequest with
        {
            WritePlan = resourceWritePlan,
            ProfileWriteContext = profileContext,
        };

        var result = await _sut.ExecuteAsync(request);

        _writeFlattener
            .FlattenCallCount.Should()
            .Be(0, "fence must fire before flattening for NestedAndExtensionCollections");
        _profileMergeSynthesizer
            .SynthesizeCallCount.Should()
            .Be(0, "profile merge must not run for nested/extension collections");
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
        _writeSessionFactory.Session.CommitCallCount.Should().Be(0);

        var upsertResult = result.Should().BeOfType<RelationalWriteExecutorResult.Upsert>().Subject;
        upsertResult
            .Result.Should()
            .BeOfType<UpsertResult.UnknownFailure>()
            .Which.FailureMessage.Should()
            .Contain("NestedAndExtensionCollections");
    }

    [Test]
    public async Task Given_Executor_for_TopLevelCollection_family_with_reference_backed_semantic_identity_passes_fence()
    {
        // Slice 4 must NOT gate on SemanticIdentitySource — a collection table whose identity
        // comes from a reference-derived fallback (ReferenceFallback) is still a plain
        // DbTableKind.Collection table and must pass the TopLevelCollectionFenceGate.
        var writableBody = JsonNode.Parse("""{"schoolId":255901}""")!;
        var rootPlan = CreateRootPlan();
        var collectionPlan = FenceTestPlans.CreateCollectionTablePlanWithReferenceBackedIdentity(
            "$.programs[*]",
            "Programs"
        );
        var resourceModel = new RelationalResourceModel(
            Resource: new QualifiedResourceName("Ed-Fi", "School"),
            PhysicalSchema: new DbSchemaName("edfi"),
            StorageKind: ResourceStorageKind.RelationalTables,
            Root: rootPlan.TableModel,
            TablesInDependencyOrder: [rootPlan.TableModel, collectionPlan.TableModel],
            DocumentReferenceBindings: [],
            DescriptorEdgeSources: []
        );
        var resourceWritePlan = new ResourceWritePlan(resourceModel, [rootPlan, collectionPlan]);
        var scopeCatalog = CompiledScopeAdapterFactory.BuildFromWritePlan(
            resourceWritePlan,
            [("$.profileScope", ScopeKind.NonCollection)]
        );
        // SemanticIdentityInOrder carries one part whose RelativePath matches the binding
        // declared in the collection plan's identity metadata ($.programReference.programId).
        // SemanticIdentityInOrder uses scope-relative paths (no "$." prefix) — these must
        // match the compiled SemanticIdentityRelativePathsInOrder produced by
        // CompiledScopeAdapterFactory.BuildSemanticIdentityPaths, which strips the scope prefix.
        // For binding path "$.programReference.programId" under scope "$.programs[*]", the
        // compiled relative path is "programReference.programId".
        var collectionRowAddress = new CollectionRowAddress(
            "$.programs[*]",
            new ScopeInstanceAddress("$", []),
            [
                new SemanticIdentityPart(
                    "programReference.programId",
                    System.Text.Json.Nodes.JsonValue.Create(100L),
                    IsPresent: true
                ),
            ]
        );
        var profileContext = new BackendProfileWriteContext(
            Request: new ProfileAppliedWriteRequest(
                WritableRequestBody: writableBody,
                RootResourceCreatable: true,
                RequestScopeStates:
                [
                    new RequestScopeState(
                        Address: new ScopeInstanceAddress("$", []),
                        Visibility: ProfileVisibilityKind.VisiblePresent,
                        Creatable: true
                    ),
                    new RequestScopeState(
                        Address: new ScopeInstanceAddress("$.profileScope", []),
                        Visibility: ProfileVisibilityKind.VisiblePresent,
                        Creatable: true
                    ),
                ],
                VisibleRequestCollectionItems:
                [
                    new VisibleRequestCollectionItem(collectionRowAddress, Creatable: true, "$.programs[0]"),
                ]
            ),
            ProfileName: "test-write-profile",
            CompiledScopeCatalog: scopeCatalog,
            StoredStateProjectionInvoker: A.Fake<IStoredStateProjectionInvoker>()
        );

        _writeFlattener.ResultToReturn = new FlattenedWriteSet(
            new RootWriteRowBuffer(
                rootPlan,
                [
                    FlattenedWriteValue.UnresolvedRootDocumentId.Instance,
                    new FlattenedWriteValue.Literal(255901),
                    new FlattenedWriteValue.Literal("Lincoln High"),
                ]
            )
        );

        var baseRequest = CreateRequest(RelationalWriteOperationKind.Post, selectedBody: writableBody);
        var request = baseRequest with
        {
            WritePlan = resourceWritePlan,
            ProfileWriteContext = profileContext,
        };

        var result = await _sut.ExecuteAsync(request);

        _writeFlattener
            .FlattenCallCount.Should()
            .Be(
                1,
                "reference-backed semantic identity must NOT trigger the fence — fence passes to flattener"
            );
        _profileMergeSynthesizer
            .SynthesizeCallCount.Should()
            .Be(1, "profile merge synthesizer must be called after fence passes");
        _noProfilePersister.TryPersistCallCount.Should().Be(1);
        _writeSessionFactory.Session.CommitCallCount.Should().Be(1);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(0);

        var upsertResult = result.Should().BeOfType<RelationalWriteExecutorResult.Upsert>().Subject;
        upsertResult.Result.Should().BeOfType<UpsertResult.InsertSuccess>();
    }

    private sealed class StubDbException(string message) : DbException(message);

    /// <summary>
    /// Dedicated fixture for the combined "If-Match present and matches + request is a no-op" path.
    /// Verifies that the committed representation is read exactly once — the pre-check caches it
    /// and the guarded no-op reuses the cache, eliminating the second DB round-trip (P3-01).
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_If_Match_present_and_request_is_a_no_op
    {
        private RecordingRelationalWriteSessionFactory _writeSessionFactory = null!;
        private RecordingReferenceResolverAdapterFactory _referenceResolverAdapterFactory = null!;
        private RecordingRelationalWriteFlattener _writeFlattener = null!;
        private RecordingRelationalWriteCurrentStateLoader _currentStateLoader = null!;
        private RecordingRelationalCommittedRepresentationReader _committedRepresentationReader = null!;
        private RecordingRelationalWriteTargetLookupResolver _targetLookupResolver = null!;
        private RecordingRelationalWriteFreshnessChecker _writeFreshnessChecker = null!;
        private RecordingRelationalWriteNoProfileMergeSynthesizer _noProfileMergeSynthesizer = null!;
        private RecordingRelationalWriteProfileMergeSynthesizer _profileMergeSynthesizer = null!;
        private RecordingRelationalWriteNoProfilePersister _noProfilePersister = null!;
        private RecordingRelationalWriteExceptionClassifier _writeExceptionClassifier = null!;
        private RecordingRelationalWriteConstraintResolver _writeConstraintResolver = null!;
        private RecordingRelationalReadMaterializer _readMaterializer = null!;
        private DefaultRelationalWriteExecutor _sut = null!;

        [SetUp]
        public void Setup()
        {
            _writeSessionFactory = new RecordingRelationalWriteSessionFactory();
            _referenceResolverAdapterFactory = new RecordingReferenceResolverAdapterFactory();
            _writeFlattener = new RecordingRelationalWriteFlattener();
            _currentStateLoader = new RecordingRelationalWriteCurrentStateLoader();
            _committedRepresentationReader = new RecordingRelationalCommittedRepresentationReader();
            _targetLookupResolver = new RecordingRelationalWriteTargetLookupResolver();
            _writeFreshnessChecker = new RecordingRelationalWriteFreshnessChecker();
            _noProfileMergeSynthesizer = new RecordingRelationalWriteNoProfileMergeSynthesizer();
            _profileMergeSynthesizer = new RecordingRelationalWriteProfileMergeSynthesizer();
            _noProfilePersister = new RecordingRelationalWriteNoProfilePersister();
            _writeExceptionClassifier = new RecordingRelationalWriteExceptionClassifier();
            _writeConstraintResolver = new RecordingRelationalWriteConstraintResolver();
            _readMaterializer = new RecordingRelationalReadMaterializer();
            _sut = new DefaultRelationalWriteExecutor(
                _writeSessionFactory,
                _referenceResolverAdapterFactory,
                _writeFlattener,
                _currentStateLoader,
                _committedRepresentationReader,
                _targetLookupResolver,
                _writeFreshnessChecker,
                _noProfileMergeSynthesizer,
                _profileMergeSynthesizer,
                _noProfilePersister,
                _writeExceptionClassifier,
                _writeConstraintResolver,
                _readMaterializer
            );
        }

        [Test]
        public async Task It_reads_committed_representation_exactly_once()
        {
            // Arrange: existing-document PUT with If-Match value matching the committed ETag.
            // The default merge synthesizer returns PreviousValues == CurrentValues → guarded no-op.
            var baseRequest = CreateRequest(RelationalWriteOperationKind.Put);
            var matchingEtag = ExpectedEtag(baseRequest);
            var request = baseRequest with { IfMatchEtag = matchingEtag };

            // Act
            var result = await _sut.ExecuteAsync(request);

            // Assert: result is a successful no-op update
            result
                .Should()
                .BeOfType<RelationalWriteExecutorResult.Update>()
                .Which.Result.Should()
                .BeOfType<UpdateResult.UpdateSuccess>();

            // Assert: ReadAsync is invoked exactly once — from CheckIfMatchEtagAsync.
            // The guarded no-op code reuses the cached representation without issuing a
            // second DB round-trip (equivalent to MustHaveHappenedOnceExactly() in FakeItEasy).
            _committedRepresentationReader
                .ReadCallCount.Should()
                .Be(
                    1,
                    "the pre-check caches the committed doc; the guarded no-op must not trigger a second read"
                );

            // Assert: no write was persisted (this is a no-op path)
            _noProfilePersister.TryPersistCallCount.Should().Be(0);

            // Assert: session committed (not rolled back) for the successful no-op
            _writeSessionFactory.Session.CommitCallCount.Should().Be(1);
            _writeSessionFactory.Session.RollbackCallCount.Should().Be(0);
        }
    }

    /// <summary>
    /// Verifies that a PUT with a stale If-Match ETag — where the staleness arose because a
    /// referenced entity's identity changed and ON UPDATE CASCADE bumped identity-column values
    /// in the stored document — is rejected with the ETag mismatch result (HTTP 412).
    /// The check is representation-sensitive: any change to the canonical document body,
    /// including dependency-identity propagation, changes the ETag hash and invalidates the
    /// client's cached precondition value.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_PUT_if_match_stale_after_dependency_identity_change
    {
        private RecordingRelationalWriteSessionFactory _writeSessionFactory = null!;
        private RecordingReferenceResolverAdapterFactory _referenceResolverAdapterFactory = null!;
        private RecordingRelationalWriteFlattener _writeFlattener = null!;
        private RecordingRelationalWriteCurrentStateLoader _currentStateLoader = null!;
        private RecordingRelationalCommittedRepresentationReader _committedRepresentationReader = null!;
        private RecordingRelationalWriteTargetLookupResolver _targetLookupResolver = null!;
        private RecordingRelationalWriteFreshnessChecker _writeFreshnessChecker = null!;
        private RecordingRelationalWriteNoProfileMergeSynthesizer _noProfileMergeSynthesizer = null!;
        private RecordingRelationalWriteProfileMergeSynthesizer _profileMergeSynthesizer = null!;
        private RecordingRelationalWriteNoProfilePersister _noProfilePersister = null!;
        private RecordingRelationalWriteExceptionClassifier _writeExceptionClassifier = null!;
        private RecordingRelationalWriteConstraintResolver _writeConstraintResolver = null!;
        private RecordingRelationalReadMaterializer _readMaterializer = null!;
        private DefaultRelationalWriteExecutor _sut = null!;

        [SetUp]
        public void Setup()
        {
            _writeSessionFactory = new RecordingRelationalWriteSessionFactory();
            _referenceResolverAdapterFactory = new RecordingReferenceResolverAdapterFactory();
            _writeFlattener = new RecordingRelationalWriteFlattener();
            _currentStateLoader = new RecordingRelationalWriteCurrentStateLoader();
            _committedRepresentationReader = new RecordingRelationalCommittedRepresentationReader();
            _targetLookupResolver = new RecordingRelationalWriteTargetLookupResolver();
            _writeFreshnessChecker = new RecordingRelationalWriteFreshnessChecker();
            _noProfileMergeSynthesizer = new RecordingRelationalWriteNoProfileMergeSynthesizer();
            _profileMergeSynthesizer = new RecordingRelationalWriteProfileMergeSynthesizer();
            _noProfilePersister = new RecordingRelationalWriteNoProfilePersister();
            _writeExceptionClassifier = new RecordingRelationalWriteExceptionClassifier();
            _writeConstraintResolver = new RecordingRelationalWriteConstraintResolver();
            _readMaterializer = new RecordingRelationalReadMaterializer();
            _sut = new DefaultRelationalWriteExecutor(
                _writeSessionFactory,
                _referenceResolverAdapterFactory,
                _writeFlattener,
                _currentStateLoader,
                _committedRepresentationReader,
                _targetLookupResolver,
                _writeFreshnessChecker,
                _noProfileMergeSynthesizer,
                _profileMergeSynthesizer,
                _noProfilePersister,
                _writeExceptionClassifier,
                _writeConstraintResolver,
                _readMaterializer
            );
        }

        [Test]
        public async Task It_returns_update_failure_etag_mismatch()
        {
            // Client cached the ETag for a document when its parent schoolId was 255901.
            // A subsequent identity change on the parent entity caused ON UPDATE CASCADE to
            // propagate schoolId=255902 into the stored document row, altering the canonical
            // representation and therefore the ETag hash.
            var preCascadeBody = JsonNode.Parse("""{"name":"Lincoln High","schoolId":255901}""")!;
            var staleEtag = RelationalApiMetadataFormatter.FormatEtag(preCascadeBody);

            // Post-cascade: the committed document now carries the updated schoolId.
            var postCascadeBody = JsonNode.Parse("""{"name":"Lincoln High","schoolId":255902}""")!;
            var persistedTarget = new RelationalWritePersistResult(
                345L,
                new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"))
            );
            _committedRepresentationReader.ResultToReturn = CreateCommittedExternalResponse(
                persistedTarget,
                postCascadeBody
            );

            var request = CreateRequest(RelationalWriteOperationKind.Put) with { IfMatchEtag = staleEtag };

            var result = await _sut.ExecuteAsync(request);

            result
                .Should()
                .BeEquivalentTo(
                    new RelationalWriteExecutorResult.Update(new UpdateResult.UpdateFailureETagMisMatch())
                );
            _committedRepresentationReader
                .ReadCallCount.Should()
                .Be(
                    1,
                    "the pre-check reads the committed representation exactly once before returning the mismatch"
                );
            _noProfilePersister
                .TryPersistCallCount.Should()
                .Be(0, "no write must be issued when the dependency-identity-changed ETag mismatches");
            _noProfileMergeSynthesizer
                .SynthesizeCallCount.Should()
                .Be(0, "merge must not run after the ETag mismatch");
            _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
            _writeSessionFactory.Session.CommitCallCount.Should().Be(0);
        }
    }

    /// <summary>
    /// Verifies that a PUT whose If-Match header matches the committed ETag but whose no-op
    /// decision becomes stale (freshness lost) produces a StaleNoOpCompare outcome rather than
    /// GuardedNoOp. The If-Match presence must not mask the stale-no-op signal: the client's
    /// write conflict must propagate correctly so the retry loop can re-attempt with fresh data.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_PUT_if_match_no_op_becomes_stale
    {
        private RecordingRelationalWriteSessionFactory _writeSessionFactory = null!;
        private RecordingReferenceResolverAdapterFactory _referenceResolverAdapterFactory = null!;
        private RecordingRelationalWriteFlattener _writeFlattener = null!;
        private RecordingRelationalWriteCurrentStateLoader _currentStateLoader = null!;
        private RecordingRelationalCommittedRepresentationReader _committedRepresentationReader = null!;
        private RecordingRelationalWriteTargetLookupResolver _targetLookupResolver = null!;
        private RecordingRelationalWriteFreshnessChecker _writeFreshnessChecker = null!;
        private RecordingRelationalWriteNoProfileMergeSynthesizer _noProfileMergeSynthesizer = null!;
        private RecordingRelationalWriteProfileMergeSynthesizer _profileMergeSynthesizer = null!;
        private RecordingRelationalWriteNoProfilePersister _noProfilePersister = null!;
        private RecordingRelationalWriteExceptionClassifier _writeExceptionClassifier = null!;
        private RecordingRelationalWriteConstraintResolver _writeConstraintResolver = null!;
        private RecordingRelationalReadMaterializer _readMaterializer = null!;
        private DefaultRelationalWriteExecutor _sut = null!;

        [SetUp]
        public void Setup()
        {
            _writeSessionFactory = new RecordingRelationalWriteSessionFactory();
            _referenceResolverAdapterFactory = new RecordingReferenceResolverAdapterFactory();
            _writeFlattener = new RecordingRelationalWriteFlattener();
            _currentStateLoader = new RecordingRelationalWriteCurrentStateLoader();
            _committedRepresentationReader = new RecordingRelationalCommittedRepresentationReader();
            _targetLookupResolver = new RecordingRelationalWriteTargetLookupResolver();
            _writeFreshnessChecker = new RecordingRelationalWriteFreshnessChecker();
            _noProfileMergeSynthesizer = new RecordingRelationalWriteNoProfileMergeSynthesizer();
            _profileMergeSynthesizer = new RecordingRelationalWriteProfileMergeSynthesizer();
            _noProfilePersister = new RecordingRelationalWriteNoProfilePersister();
            _writeExceptionClassifier = new RecordingRelationalWriteExceptionClassifier();
            _writeConstraintResolver = new RecordingRelationalWriteConstraintResolver();
            _readMaterializer = new RecordingRelationalReadMaterializer();
            _sut = new DefaultRelationalWriteExecutor(
                _writeSessionFactory,
                _referenceResolverAdapterFactory,
                _writeFlattener,
                _currentStateLoader,
                _committedRepresentationReader,
                _targetLookupResolver,
                _writeFreshnessChecker,
                _noProfileMergeSynthesizer,
                _profileMergeSynthesizer,
                _noProfilePersister,
                _writeExceptionClassifier,
                _writeConstraintResolver,
                _readMaterializer
            );
        }

        [Test]
        public async Task It_returns_stale_no_op_compare_outcome_not_guarded_no_op()
        {
            // Arrange: PUT with a matching If-Match so CheckIfMatchEtagAsync passes.
            // The default merge synthesizer returns identical rows (no-op candidate).
            // The freshness checker reports the no-op decision is stale — the content
            // version observed in the write session no longer matches the committed row.
            var baseRequest = CreateRequest(RelationalWriteOperationKind.Put);
            var matchingEtag = ExpectedEtag(baseRequest);
            var request = baseRequest with { IfMatchEtag = matchingEtag };
            _writeFreshnessChecker.IsCurrentResult = false;

            var result = await _sut.ExecuteAsync(request);

            result
                .Should()
                .BeEquivalentTo(
                    new RelationalWriteExecutorResult.Update(
                        new UpdateResult.UpdateFailureWriteConflict(),
                        RelationalWriteExecutorAttemptOutcome.StaleNoOpCompare.Instance
                    )
                );
            result
                .AttemptOutcome.Should()
                .Be(RelationalWriteExecutorAttemptOutcome.StaleNoOpCompare.Instance);
            _noProfilePersister
                .TryPersistCallCount.Should()
                .Be(0, "no write must be issued on a stale no-op path");
            _writeFreshnessChecker
                .IsCurrentCallCount.Should()
                .Be(1, "the freshness checker must run exactly once on the guarded no-op path");
            _writeSessionFactory.Session.CommitCallCount.Should().Be(0);
            _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
        }
    }

    /// <summary>
    /// Verifies that If-Match: * is not supported: the executor treats "*" as a literal ETag
    /// value, acquires a row-level lock, reads the committed representation, and rejects the
    /// request with UpdateFailureETagMisMatch because no real ETag will ever equal the
    /// literal string "*".
    ///
    /// This change was introduced to remove RFC 7232 wildcard support. Clients must supply
    /// the actual ETag they received from a prior GET response.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_PUT_if_match_wildcard_returns_precondition_failed
    {
        private RecordingRelationalWriteSessionFactory _writeSessionFactory = null!;
        private RecordingReferenceResolverAdapterFactory _referenceResolverAdapterFactory = null!;
        private RecordingRelationalWriteFlattener _writeFlattener = null!;
        private RecordingRelationalWriteCurrentStateLoader _currentStateLoader = null!;
        private RecordingRelationalCommittedRepresentationReader _committedRepresentationReader = null!;
        private RecordingRelationalWriteTargetLookupResolver _targetLookupResolver = null!;
        private RecordingRelationalWriteFreshnessChecker _writeFreshnessChecker = null!;
        private RecordingRelationalWriteNoProfileMergeSynthesizer _noProfileMergeSynthesizer = null!;
        private RecordingRelationalWriteProfileMergeSynthesizer _profileMergeSynthesizer = null!;
        private RecordingRelationalWriteNoProfilePersister _noProfilePersister = null!;
        private RecordingRelationalWriteExceptionClassifier _writeExceptionClassifier = null!;
        private RecordingRelationalWriteConstraintResolver _writeConstraintResolver = null!;
        private RecordingRelationalReadMaterializer _readMaterializer = null!;
        private DefaultRelationalWriteExecutor _sut = null!;

        [SetUp]
        public void Setup()
        {
            _writeSessionFactory = new RecordingRelationalWriteSessionFactory();
            _referenceResolverAdapterFactory = new RecordingReferenceResolverAdapterFactory();
            _writeFlattener = new RecordingRelationalWriteFlattener();
            _currentStateLoader = new RecordingRelationalWriteCurrentStateLoader();
            _committedRepresentationReader = new RecordingRelationalCommittedRepresentationReader();
            _targetLookupResolver = new RecordingRelationalWriteTargetLookupResolver();
            _writeFreshnessChecker = new RecordingRelationalWriteFreshnessChecker();
            _noProfileMergeSynthesizer = new RecordingRelationalWriteNoProfileMergeSynthesizer();
            _profileMergeSynthesizer = new RecordingRelationalWriteProfileMergeSynthesizer();
            _noProfilePersister = new RecordingRelationalWriteNoProfilePersister();
            _writeExceptionClassifier = new RecordingRelationalWriteExceptionClassifier();
            _writeConstraintResolver = new RecordingRelationalWriteConstraintResolver();
            _readMaterializer = new RecordingRelationalReadMaterializer();
            _sut = new DefaultRelationalWriteExecutor(
                _writeSessionFactory,
                _referenceResolverAdapterFactory,
                _writeFlattener,
                _currentStateLoader,
                _committedRepresentationReader,
                _targetLookupResolver,
                _writeFreshnessChecker,
                _noProfileMergeSynthesizer,
                _profileMergeSynthesizer,
                _noProfilePersister,
                _writeExceptionClassifier,
                _writeConstraintResolver,
                _readMaterializer
            );
        }

        [Test]
        public async Task It_returns_update_failure_etag_mismatch()
        {
            // Arrange: existing-document PUT with If-Match: * — wildcard is no longer supported.
            // CheckIfMatchEtagAsync compares "*" against the committed ETag (a base64 SHA256 hash),
            // which never equals the literal string "*", so the precondition always fails.
            var request = CreateRequest(
                RelationalWriteOperationKind.Put,
                targetContext: new RelationalWriteTargetContext.ExistingDocument(
                    345L,
                    new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb")),
                    44L
                )
            ) with
            {
                IfMatchEtag = "*",
            };

            // Act
            var result = await _sut.ExecuteAsync(request);

            // Assert: 412 Precondition Failed — wildcard is rejected.
            result
                .Should()
                .BeEquivalentTo(
                    new RelationalWriteExecutorResult.Update(new UpdateResult.UpdateFailureETagMisMatch()),
                    "If-Match: * is not supported; the literal \"*\" never matches a real ETag"
                );

            // Assert: exactly one ReadAsync call from CheckIfMatchEtagAsync (row lock acquired,
            // committed doc read, then mismatch returned).
            _committedRepresentationReader
                .ReadCallCount.Should()
                .Be(1, "CheckIfMatchEtagAsync reads the committed doc before comparing the ETag");

            // Assert: no write was attempted after the mismatch.
            _noProfilePersister.TryPersistCallCount.Should().Be(0);

            // Assert: session was rolled back, not committed.
            _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
            _writeSessionFactory.Session.CommitCallCount.Should().Be(0);

            // Assert: a row-lock SELECT was issued by CheckIfMatchEtagAsync.
            _writeSessionFactory
                .Session.Commands.Should()
                .HaveCount(1, "CheckIfMatchEtagAsync issues one FOR UPDATE row-lock command");
        }
    }

    /// <summary>
    /// Regression guard for peer-review-02 finding 1: a client that receives an ETag from a
    /// readable-profile GET must be able to use that ETag in a subsequent PUT If-Match header
    /// without receiving 412. The committed-representation reader applies profile projection
    /// and refreshes the ETag; CheckIfMatchEtagAsync compares the incoming If-Match value
    /// against the projected (filtered) ETag, not the full-resource ETag.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_profiled_write_put_if_match_equals_projected_etag
    {
        private RecordingRelationalWriteSessionFactory _writeSessionFactory = null!;
        private RecordingReferenceResolverAdapterFactory _referenceResolverAdapterFactory = null!;
        private RecordingRelationalWriteFlattener _writeFlattener = null!;
        private RecordingRelationalWriteCurrentStateLoader _currentStateLoader = null!;
        private RecordingRelationalCommittedRepresentationReader _committedRepresentationReader = null!;
        private RecordingRelationalWriteTargetLookupResolver _targetLookupResolver = null!;
        private RecordingRelationalWriteFreshnessChecker _writeFreshnessChecker = null!;
        private RecordingRelationalWriteNoProfileMergeSynthesizer _noProfileMergeSynthesizer = null!;
        private RecordingRelationalWriteProfileMergeSynthesizer _profileMergeSynthesizer = null!;
        private RecordingRelationalWriteNoProfilePersister _noProfilePersister = null!;
        private RecordingRelationalWriteExceptionClassifier _writeExceptionClassifier = null!;
        private RecordingRelationalWriteConstraintResolver _writeConstraintResolver = null!;
        private RecordingRelationalReadMaterializer _readMaterializer = null!;
        private DefaultRelationalWriteExecutor _sut = null!;

        [SetUp]
        public void Setup()
        {
            _writeSessionFactory = new RecordingRelationalWriteSessionFactory();
            _referenceResolverAdapterFactory = new RecordingReferenceResolverAdapterFactory();
            _writeFlattener = new RecordingRelationalWriteFlattener();
            _currentStateLoader = new RecordingRelationalWriteCurrentStateLoader();
            _committedRepresentationReader = new RecordingRelationalCommittedRepresentationReader();
            _targetLookupResolver = new RecordingRelationalWriteTargetLookupResolver();
            _writeFreshnessChecker = new RecordingRelationalWriteFreshnessChecker();
            _noProfileMergeSynthesizer = new RecordingRelationalWriteNoProfileMergeSynthesizer();
            _profileMergeSynthesizer = new RecordingRelationalWriteProfileMergeSynthesizer();
            _noProfilePersister = new RecordingRelationalWriteNoProfilePersister();
            _writeExceptionClassifier = new RecordingRelationalWriteExceptionClassifier();
            _writeConstraintResolver = new RecordingRelationalWriteConstraintResolver();
            _readMaterializer = new RecordingRelationalReadMaterializer();
            _sut = new DefaultRelationalWriteExecutor(
                _writeSessionFactory,
                _referenceResolverAdapterFactory,
                _writeFlattener,
                _currentStateLoader,
                _committedRepresentationReader,
                _targetLookupResolver,
                _writeFreshnessChecker,
                _noProfileMergeSynthesizer,
                _profileMergeSynthesizer,
                _noProfilePersister,
                _writeExceptionClassifier,
                _writeConstraintResolver,
                _readMaterializer
            );
        }

        [Test]
        public async Task It_returns_update_success_when_if_match_equals_projected_not_full_etag()
        {
            // Full-resource body includes "webSite"; the readable profile excludes it,
            // so the projected ETag differs from the full-resource ETag.
            var fullBody = JsonNode.Parse("""{"name":"Lincoln High","webSite":"https://example.com"}""")!;
            var fullEtag = RelationalApiMetadataFormatter.FormatEtag(fullBody);
            var projectedBody = JsonNode.Parse("""{"name":"Lincoln High"}""")!;
            var projectedEtag = RelationalApiMetadataFormatter.FormatEtag(projectedBody);

            projectedEtag.Should().NotBe(fullEtag, "read-profile excludes 'webSite' so ETags must differ");

            // Simulate what RelationalCommittedRepresentationReader returns after profile
            // projection: a filtered document with the refreshed projected ETag.
            var persistedTarget = new RelationalWritePersistResult(
                345L,
                new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"))
            );
            _committedRepresentationReader.ResultToReturn = CreateCommittedExternalResponse(
                persistedTarget,
                projectedBody
            );

            // Client supplies If-Match equal to the projected ETag (e.g. obtained from a
            // profile-constrained GET). The executor must accept the match and proceed.
            var request = CreateRequest(
                RelationalWriteOperationKind.Put,
                targetContext: new RelationalWriteTargetContext.ExistingDocument(
                    345L,
                    new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb")),
                    44L
                )
            ) with
            {
                IfMatchEtag = projectedEtag,
            };

            var result = await _sut.ExecuteAsync(request);

            result
                .Should()
                .BeOfType<RelationalWriteExecutorResult.Update>(
                    "profile-projected ETag must be accepted without triggering a 412 mismatch"
                );
            ((RelationalWriteExecutorResult.Update)result)
                .Result.Should()
                .BeOfType<UpdateResult.UpdateSuccess>(
                    "If-Match matching the projected ETag must allow the write to succeed"
                );
            _committedRepresentationReader
                .ReadCallCount.Should()
                .Be(1, "committed representation read must occur exactly once for the ETag pre-check");
            _noProfilePersister
                .TryPersistCallCount.Should()
                .Be(0, "guarded no-op path: no write DML issued for identical merge rows");
            _writeSessionFactory.Session.CommitCallCount.Should().Be(1);
            _writeSessionFactory.Session.RollbackCallCount.Should().Be(0);
        }
    }

    /// <summary>
    /// A client that supplies the full-resource ETag as If-Match must receive 412 when an
    /// active readable profile changes the committed representation, causing the projected
    /// ETag to differ from the full-resource ETag. The pre-check compares only against the
    /// projected ETag returned by the committed-representation reader.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_profiled_write_put_full_etag_rejected_when_reader_returns_projected_etag
    {
        private RecordingRelationalWriteSessionFactory _writeSessionFactory = null!;
        private RecordingReferenceResolverAdapterFactory _referenceResolverAdapterFactory = null!;
        private RecordingRelationalWriteFlattener _writeFlattener = null!;
        private RecordingRelationalWriteCurrentStateLoader _currentStateLoader = null!;
        private RecordingRelationalCommittedRepresentationReader _committedRepresentationReader = null!;
        private RecordingRelationalWriteTargetLookupResolver _targetLookupResolver = null!;
        private RecordingRelationalWriteFreshnessChecker _writeFreshnessChecker = null!;
        private RecordingRelationalWriteNoProfileMergeSynthesizer _noProfileMergeSynthesizer = null!;
        private RecordingRelationalWriteProfileMergeSynthesizer _profileMergeSynthesizer = null!;
        private RecordingRelationalWriteNoProfilePersister _noProfilePersister = null!;
        private RecordingRelationalWriteExceptionClassifier _writeExceptionClassifier = null!;
        private RecordingRelationalWriteConstraintResolver _writeConstraintResolver = null!;
        private RecordingRelationalReadMaterializer _readMaterializer = null!;
        private DefaultRelationalWriteExecutor _sut = null!;

        [SetUp]
        public void Setup()
        {
            _writeSessionFactory = new RecordingRelationalWriteSessionFactory();
            _referenceResolverAdapterFactory = new RecordingReferenceResolverAdapterFactory();
            _writeFlattener = new RecordingRelationalWriteFlattener();
            _currentStateLoader = new RecordingRelationalWriteCurrentStateLoader();
            _committedRepresentationReader = new RecordingRelationalCommittedRepresentationReader();
            _targetLookupResolver = new RecordingRelationalWriteTargetLookupResolver();
            _writeFreshnessChecker = new RecordingRelationalWriteFreshnessChecker();
            _noProfileMergeSynthesizer = new RecordingRelationalWriteNoProfileMergeSynthesizer();
            _profileMergeSynthesizer = new RecordingRelationalWriteProfileMergeSynthesizer();
            _noProfilePersister = new RecordingRelationalWriteNoProfilePersister();
            _writeExceptionClassifier = new RecordingRelationalWriteExceptionClassifier();
            _writeConstraintResolver = new RecordingRelationalWriteConstraintResolver();
            _readMaterializer = new RecordingRelationalReadMaterializer();
            _sut = new DefaultRelationalWriteExecutor(
                _writeSessionFactory,
                _referenceResolverAdapterFactory,
                _writeFlattener,
                _currentStateLoader,
                _committedRepresentationReader,
                _targetLookupResolver,
                _writeFreshnessChecker,
                _noProfileMergeSynthesizer,
                _profileMergeSynthesizer,
                _noProfilePersister,
                _writeExceptionClassifier,
                _writeConstraintResolver,
                _readMaterializer
            );
        }

        [Test]
        public async Task It_returns_etag_mismatch_when_if_match_equals_full_etag_not_projected_etag()
        {
            // Full-resource body; full ETag covers all fields including "webSite".
            var fullBody = JsonNode.Parse("""{"name":"Lincoln High","webSite":"https://example.com"}""")!;
            var fullEtag = RelationalApiMetadataFormatter.FormatEtag(fullBody);
            // Projected body has "webSite" excluded; projected ETag differs from fullEtag.
            var projectedBody = JsonNode.Parse("""{"name":"Lincoln High"}""")!;
            var projectedEtag = RelationalApiMetadataFormatter.FormatEtag(projectedBody);

            projectedEtag.Should().NotBe(fullEtag, "projected ETag must differ from full ETag");

            // Committed reader returns the profile-projected document with projectedEtag.
            var persistedTarget = new RelationalWritePersistResult(
                345L,
                new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"))
            );
            _committedRepresentationReader.ResultToReturn = CreateCommittedExternalResponse(
                persistedTarget,
                projectedBody
            );

            // Client supplies the full-resource ETag (e.g. from a non-profile GET) — this
            // does NOT match the projected ETag returned by the reader → 412 mismatch.
            var request = CreateRequest(
                RelationalWriteOperationKind.Put,
                targetContext: new RelationalWriteTargetContext.ExistingDocument(
                    345L,
                    new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb")),
                    44L
                )
            ) with
            {
                IfMatchEtag = fullEtag,
            };

            var result = await _sut.ExecuteAsync(request);

            result
                .Should()
                .BeEquivalentTo(
                    new RelationalWriteExecutorResult.Update(new UpdateResult.UpdateFailureETagMisMatch()),
                    "full-resource ETag must be rejected (412) when the reader returns a different projected ETag"
                );
            _committedRepresentationReader
                .ReadCallCount.Should()
                .Be(1, "committed representation read must occur once for the failed ETag pre-check");
            _noProfilePersister
                .TryPersistCallCount.Should()
                .Be(0, "no write must be issued after ETag mismatch");
            _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
            _writeSessionFactory.Session.CommitCallCount.Should().Be(0);
        }
    }

    /// <summary>
    /// On guarded no-op success, the result ETag must be the projected ETag (from the
    /// committed-representation reader), not the full-resource body ETag.  This ensures
    /// the client's subsequent If-Match header is consistent with the ETag it will receive
    /// on the next profile-constrained GET.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_profiled_write_put_guarded_no_op_returns_projected_etag
    {
        private RecordingRelationalWriteSessionFactory _writeSessionFactory = null!;
        private RecordingReferenceResolverAdapterFactory _referenceResolverAdapterFactory = null!;
        private RecordingRelationalWriteFlattener _writeFlattener = null!;
        private RecordingRelationalWriteCurrentStateLoader _currentStateLoader = null!;
        private RecordingRelationalCommittedRepresentationReader _committedRepresentationReader = null!;
        private RecordingRelationalWriteTargetLookupResolver _targetLookupResolver = null!;
        private RecordingRelationalWriteFreshnessChecker _writeFreshnessChecker = null!;
        private RecordingRelationalWriteNoProfileMergeSynthesizer _noProfileMergeSynthesizer = null!;
        private RecordingRelationalWriteProfileMergeSynthesizer _profileMergeSynthesizer = null!;
        private RecordingRelationalWriteNoProfilePersister _noProfilePersister = null!;
        private RecordingRelationalWriteExceptionClassifier _writeExceptionClassifier = null!;
        private RecordingRelationalWriteConstraintResolver _writeConstraintResolver = null!;
        private RecordingRelationalReadMaterializer _readMaterializer = null!;
        private DefaultRelationalWriteExecutor _sut = null!;

        [SetUp]
        public void Setup()
        {
            _writeSessionFactory = new RecordingRelationalWriteSessionFactory();
            _referenceResolverAdapterFactory = new RecordingReferenceResolverAdapterFactory();
            _writeFlattener = new RecordingRelationalWriteFlattener();
            _currentStateLoader = new RecordingRelationalWriteCurrentStateLoader();
            _committedRepresentationReader = new RecordingRelationalCommittedRepresentationReader();
            _targetLookupResolver = new RecordingRelationalWriteTargetLookupResolver();
            _writeFreshnessChecker = new RecordingRelationalWriteFreshnessChecker();
            _noProfileMergeSynthesizer = new RecordingRelationalWriteNoProfileMergeSynthesizer();
            _profileMergeSynthesizer = new RecordingRelationalWriteProfileMergeSynthesizer();
            _noProfilePersister = new RecordingRelationalWriteNoProfilePersister();
            _writeExceptionClassifier = new RecordingRelationalWriteExceptionClassifier();
            _writeConstraintResolver = new RecordingRelationalWriteConstraintResolver();
            _readMaterializer = new RecordingRelationalReadMaterializer();
            _sut = new DefaultRelationalWriteExecutor(
                _writeSessionFactory,
                _referenceResolverAdapterFactory,
                _writeFlattener,
                _currentStateLoader,
                _committedRepresentationReader,
                _targetLookupResolver,
                _writeFreshnessChecker,
                _noProfileMergeSynthesizer,
                _profileMergeSynthesizer,
                _noProfilePersister,
                _writeExceptionClassifier,
                _writeConstraintResolver,
                _readMaterializer
            );
        }

        [Test]
        public async Task It_returns_projected_etag_in_guarded_no_op_success_result()
        {
            // Profile-projected body excludes "webSite"; projected ETag differs from full ETag.
            var projectedBody = JsonNode.Parse("""{"name":"Lincoln High"}""")!;
            var projectedEtag = RelationalApiMetadataFormatter.FormatEtag(projectedBody);
            var fullBody = JsonNode.Parse("""{"name":"Lincoln High","webSite":"https://example.com"}""")!;
            var fullEtag = RelationalApiMetadataFormatter.FormatEtag(fullBody);

            projectedEtag.Should().NotBe(fullEtag, "test setup: projected ETag must differ from full ETag");

            // Committed reader returns the profile-projected document. The guarded no-op
            // success result must carry this projected ETag, not the full-resource body ETag.
            var persistedTarget = new RelationalWritePersistResult(
                345L,
                new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"))
            );
            _committedRepresentationReader.ResultToReturn = CreateCommittedExternalResponse(
                persistedTarget,
                projectedBody
            );

            // If-Match matches the projected ETag so CheckIfMatchEtagAsync passes and
            // caches the projected committed doc. The guarded no-op reuses the cached doc.
            var request = CreateRequest(
                RelationalWriteOperationKind.Put,
                targetContext: new RelationalWriteTargetContext.ExistingDocument(
                    345L,
                    new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb")),
                    44L
                )
            ) with
            {
                IfMatchEtag = projectedEtag,
            };

            var result = await _sut.ExecuteAsync(request);

            var updateResult = result.Should().BeOfType<RelationalWriteExecutorResult.Update>().Subject;
            updateResult.Result.Should().BeOfType<UpdateResult.UpdateSuccess>();
            var successResult = (UpdateResult.UpdateSuccess)updateResult.Result;
            successResult
                .ETag.Should()
                .Be(
                    projectedEtag,
                    "guarded no-op success must return the projected ETag so the client can use it in subsequent If-Match headers"
                );
            successResult.ETag.Should().NotBe(fullEtag, "full-resource ETag must not leak into the result");
            _committedRepresentationReader
                .ReadCallCount.Should()
                .Be(
                    1,
                    "pre-check reads the committed doc once and caches it; guarded no-op reuses the cache"
                );
            _noProfilePersister.TryPersistCallCount.Should().Be(0, "guarded no-op issues no write DML");
            _writeSessionFactory.Session.CommitCallCount.Should().Be(1);
        }
    }
}

/// <summary>
/// File-local write-plan builders for slice-3 executor fence tests.
/// Mirrors <c>ProfileSliceFenceClassifierTestHelpers</c> (which is <c>file</c>-scoped
/// and therefore not visible to this test file).
/// </summary>
file static class FenceTestPlans
{
    private static readonly DbSchemaName _schema = new("edfi");

    public static TableWritePlan RootTablePlan() => CreateTablePlan("$", "School", DbTableKind.Root);

    public static TableWritePlan CreateCollectionTablePlan(
        string jsonScope,
        string tableName,
        DbTableKind tableKind
    )
    {
        var collectionKeyColumn = new DbColumnModel(
            ColumnName: new DbColumnName("CollectionItemId"),
            Kind: ColumnKind.CollectionKey,
            ScalarType: null,
            IsNullable: false,
            SourceJsonPath: null,
            TargetResource: null
        );
        var parentKeyColumn = new DbColumnModel(
            ColumnName: new DbColumnName("ParentDocumentId"),
            Kind: ColumnKind.ParentKeyPart,
            ScalarType: null,
            IsNullable: false,
            SourceJsonPath: null,
            TargetResource: null
        );
        var ordinalColumn = new DbColumnModel(
            ColumnName: new DbColumnName("Ordinal"),
            Kind: ColumnKind.Ordinal,
            ScalarType: null,
            IsNullable: false,
            SourceJsonPath: null,
            TargetResource: null
        );

        var columns = new DbColumnModel[] { collectionKeyColumn, parentKeyColumn, ordinalColumn };

        var tableModel = new DbTableModel(
            Table: new DbTableName(_schema, tableName),
            JsonScope: new JsonPathExpression(jsonScope, []),
            Key: new TableKey(
                "PK_" + tableName,
                [new DbKeyColumn(new DbColumnName("CollectionItemId"), ColumnKind.CollectionKey)]
            ),
            Columns: columns,
            Constraints: []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                TableKind: tableKind,
                PhysicalRowIdentityColumns: [new DbColumnName("CollectionItemId")],
                RootScopeLocatorColumns: [new DbColumnName("ParentDocumentId")],
                ImmediateParentScopeLocatorColumns: [new DbColumnName("ParentDocumentId")],
                SemanticIdentityBindings: []
            ),
        };

        return new TableWritePlan(
            TableModel: tableModel,
            InsertSql: $"INSERT INTO edfi.\"{tableName}\" VALUES (@CollectionItemId, @ParentDocumentId, @Ordinal)",
            UpdateSql: null,
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(1000, columns.Length, 65535),
            ColumnBindings:
            [
                new WriteColumnBinding(
                    collectionKeyColumn,
                    new WriteValueSource.Precomputed(),
                    "CollectionItemId"
                ),
                new WriteColumnBinding(
                    parentKeyColumn,
                    new WriteValueSource.DocumentId(),
                    "ParentDocumentId"
                ),
                new WriteColumnBinding(ordinalColumn, new WriteValueSource.Ordinal(), "Ordinal"),
            ],
            KeyUnificationPlans: [],
            CollectionMergePlan: new CollectionMergePlan(
                SemanticIdentityBindings: [],
                StableRowIdentityBindingIndex: 0,
                UpdateByStableRowIdentitySql: $"UPDATE edfi.\"{tableName}\" SET \"Ordinal\" = @Ordinal WHERE \"CollectionItemId\" = @CollectionItemId",
                DeleteByStableRowIdentitySql: $"DELETE FROM edfi.\"{tableName}\" WHERE \"CollectionItemId\" = @CollectionItemId",
                OrdinalBindingIndex: 2,
                CompareBindingIndexesInOrder: [2]
            ),
            CollectionKeyPreallocationPlan: new CollectionKeyPreallocationPlan(
                new DbColumnName("CollectionItemId"),
                0
            )
        );
    }

    public static TableWritePlan CreateTablePlan(string jsonScope, string tableName, DbTableKind tableKind)
    {
        var docIdColumn = new DbColumnModel(
            ColumnName: new DbColumnName("DocumentId"),
            Kind: ColumnKind.ParentKeyPart,
            ScalarType: null,
            IsNullable: false,
            SourceJsonPath: null,
            TargetResource: null
        );

        var tableModel = new DbTableModel(
            Table: new DbTableName(_schema, tableName),
            JsonScope: new JsonPathExpression(jsonScope, []),
            Key: new TableKey(
                "PK_" + tableName,
                [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            Columns: [docIdColumn],
            Constraints: []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                TableKind: tableKind,
                PhysicalRowIdentityColumns: [new DbColumnName("DocumentId")],
                RootScopeLocatorColumns: [new DbColumnName("DocumentId")],
                ImmediateParentScopeLocatorColumns: [],
                SemanticIdentityBindings: []
            ),
        };

        return new TableWritePlan(
            TableModel: tableModel,
            InsertSql: $"INSERT INTO edfi.\"{tableName}\" VALUES (@DocumentId)",
            UpdateSql: null,
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(1000, 1, 65535),
            ColumnBindings:
            [
                new WriteColumnBinding(docIdColumn, new WriteValueSource.DocumentId(), "DocumentId"),
            ],
            KeyUnificationPlans: []
        );
    }

    /// <summary>
    /// Builds a minimal top-level collection plan whose semantic identity comes from a
    /// reference-derived fallback column (<see cref="ColumnKind.DocumentFk"/>), with
    /// <see cref="CollectionSemanticIdentitySource.ReferenceFallback"/> recorded on the
    /// <see cref="DbTableIdentityMetadata"/>. Used to prove that the executor fence does
    /// NOT gate on semantic-identity source — a reference-backed collection must still
    /// pass <c>TopLevelCollectionFenceGate</c>.
    /// </summary>
    public static TableWritePlan CreateCollectionTablePlanWithReferenceBackedIdentity(
        string jsonScope,
        string tableName
    )
    {
        var collectionKeyColumn = new DbColumnModel(
            ColumnName: new DbColumnName("CollectionItemId"),
            Kind: ColumnKind.CollectionKey,
            ScalarType: null,
            IsNullable: false,
            SourceJsonPath: null,
            TargetResource: null
        );
        var parentKeyColumn = new DbColumnModel(
            ColumnName: new DbColumnName("ParentDocumentId"),
            Kind: ColumnKind.ParentKeyPart,
            ScalarType: null,
            IsNullable: false,
            SourceJsonPath: null,
            TargetResource: null
        );
        var ordinalColumn = new DbColumnModel(
            ColumnName: new DbColumnName("Ordinal"),
            Kind: ColumnKind.Ordinal,
            ScalarType: null,
            IsNullable: false,
            SourceJsonPath: null,
            TargetResource: null
        );
        // Reference-derived FK column — the semantic identity for this collection is the
        // document-id of the referenced entity (e.g. programReference → Program).
        var referenceFkColumn = new DbColumnModel(
            ColumnName: new DbColumnName("ProgramDocumentId"),
            Kind: ColumnKind.DocumentFk,
            ScalarType: null,
            IsNullable: false,
            SourceJsonPath: new JsonPathExpression("$.programReference.programId", []),
            TargetResource: null
        );

        var columns = new DbColumnModel[]
        {
            collectionKeyColumn,
            parentKeyColumn,
            ordinalColumn,
            referenceFkColumn,
        };

        // SemanticIdentityBinding points the relative path to the FK storage column.
        var semanticIdentityBinding = new CollectionSemanticIdentityBinding(
            RelativePath: new JsonPathExpression("$.programReference.programId", []),
            ColumnName: new DbColumnName("ProgramDocumentId")
        );

        var tableModel = new DbTableModel(
            Table: new DbTableName(_schema, tableName),
            JsonScope: new JsonPathExpression(jsonScope, []),
            Key: new TableKey(
                "PK_" + tableName,
                [new DbKeyColumn(new DbColumnName("CollectionItemId"), ColumnKind.CollectionKey)]
            ),
            Columns: columns,
            Constraints: []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                TableKind: DbTableKind.Collection,
                PhysicalRowIdentityColumns: [new DbColumnName("CollectionItemId")],
                RootScopeLocatorColumns: [new DbColumnName("ParentDocumentId")],
                ImmediateParentScopeLocatorColumns: [new DbColumnName("ParentDocumentId")],
                SemanticIdentityBindings: [semanticIdentityBinding]
            )
            {
                SemanticIdentitySource = CollectionSemanticIdentitySource.ReferenceFallback,
            },
        };

        // CollectionMergePlan.SemanticIdentityBindings binds the FK column at binding index 3
        // (fourth entry in ColumnBindings: key=0, parent=1, ordinal=2, fk=3).
        return new TableWritePlan(
            TableModel: tableModel,
            InsertSql: $"INSERT INTO edfi.\"{tableName}\" VALUES (@CollectionItemId, @ParentDocumentId, @Ordinal, @ProgramDocumentId)",
            UpdateSql: null,
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(1000, columns.Length, 65535),
            ColumnBindings:
            [
                new WriteColumnBinding(
                    collectionKeyColumn,
                    new WriteValueSource.Precomputed(),
                    "CollectionItemId"
                ),
                new WriteColumnBinding(
                    parentKeyColumn,
                    new WriteValueSource.DocumentId(),
                    "ParentDocumentId"
                ),
                new WriteColumnBinding(ordinalColumn, new WriteValueSource.Ordinal(), "Ordinal"),
                new WriteColumnBinding(
                    referenceFkColumn,
                    new WriteValueSource.Precomputed(),
                    "ProgramDocumentId"
                ),
            ],
            KeyUnificationPlans: [],
            CollectionMergePlan: new CollectionMergePlan(
                SemanticIdentityBindings:
                [
                    new CollectionMergeSemanticIdentityBinding(
                        RelativePath: new JsonPathExpression("$.programReference.programId", []),
                        BindingIndex: 3
                    ),
                ],
                StableRowIdentityBindingIndex: 0,
                UpdateByStableRowIdentitySql: $"UPDATE edfi.\"{tableName}\" SET \"Ordinal\" = @Ordinal WHERE \"CollectionItemId\" = @CollectionItemId",
                DeleteByStableRowIdentitySql: $"DELETE FROM edfi.\"{tableName}\" WHERE \"CollectionItemId\" = @CollectionItemId",
                OrdinalBindingIndex: 2,
                CompareBindingIndexesInOrder: [2]
            ),
            CollectionKeyPreallocationPlan: new CollectionKeyPreallocationPlan(
                new DbColumnName("CollectionItemId"),
                0
            )
        );
    }
}

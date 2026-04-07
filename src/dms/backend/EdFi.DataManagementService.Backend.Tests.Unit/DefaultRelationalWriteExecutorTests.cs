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
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
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
    private RecordingRelationalWriteTargetLookupResolver _targetLookupResolver = null!;
    private RecordingRelationalWriteFreshnessChecker _writeFreshnessChecker = null!;
    private RecordingRelationalWriteNoProfileMergeSynthesizer _noProfileMergeSynthesizer = null!;
    private RecordingRelationalWriteNoProfilePersister _noProfilePersister = null!;
    private DefaultRelationalWriteExecutor _sut = null!;

    [SetUp]
    public void Setup()
    {
        _writeSessionFactory = new RecordingRelationalWriteSessionFactory();
        _referenceResolverAdapterFactory = new RecordingReferenceResolverAdapterFactory();
        _writeFlattener = new RecordingRelationalWriteFlattener();
        _currentStateLoader = new RecordingRelationalWriteCurrentStateLoader();
        _targetLookupResolver = new RecordingRelationalWriteTargetLookupResolver();
        _writeFreshnessChecker = new RecordingRelationalWriteFreshnessChecker();
        _noProfileMergeSynthesizer = new RecordingRelationalWriteNoProfileMergeSynthesizer();
        _noProfilePersister = new RecordingRelationalWriteNoProfilePersister();
        _sut = new DefaultRelationalWriteExecutor(
            _writeSessionFactory,
            _referenceResolverAdapterFactory,
            _writeFlattener,
            _currentStateLoader,
            _targetLookupResolver,
            _writeFreshnessChecker,
            _noProfileMergeSynthesizer,
            _noProfilePersister
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
                        new DocumentUuid(Guid.Parse("cccccccc-1111-2222-3333-dddddddddddd"))
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
        var request = CreateRequest(RelationalWriteOperationKind.Put);
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
            ]
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
                        new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"))
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
                    new UpsertResult.UpdateSuccess(existingDocumentUuid),
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
        var request = CreateRequest(RelationalWriteOperationKind.Put);

        var result = await _sut.ExecuteAsync(request);

        result
            .Should()
            .BeEquivalentTo(
                new RelationalWriteExecutorResult.Update(
                    new UpdateResult.UpdateSuccess(
                        new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"))
                    ),
                    RelationalWriteExecutorAttemptOutcome.GuardedNoOp.Instance
                )
            );
        result.AttemptOutcome.Should().Be(RelationalWriteExecutorAttemptOutcome.GuardedNoOp.Instance);
        _currentStateLoader.LoadCallCount.Should().Be(1);
        _noProfileMergeSynthesizer.SynthesizeCallCount.Should().Be(1);
        _noProfilePersister.TryPersistCallCount.Should().Be(0);
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
                        new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"))
                    ),
                    RelationalWriteExecutorAttemptOutcome.GuardedNoOp.Instance
                )
            );
        result.AttemptOutcome.Should().Be(RelationalWriteExecutorAttemptOutcome.GuardedNoOp.Instance);
        _currentStateLoader.LoadCallCount.Should().Be(1);
        _noProfileMergeSynthesizer.SynthesizeCallCount.Should().Be(1);
        _noProfilePersister.TryPersistCallCount.Should().Be(0);
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
                        new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"))
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
                        new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"))
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
            ]
        );
        _sut = new DefaultRelationalWriteExecutor(
            _writeSessionFactory,
            _referenceResolverAdapterFactory,
            _writeFlattener,
            _currentStateLoader,
            _targetLookupResolver,
            _writeFreshnessChecker,
            new RelationalWriteNoProfileMergeSynthesizer(),
            _noProfilePersister
        );

        var result = await _sut.ExecuteAsync(request);

        result
            .Should()
            .BeEquivalentTo(
                new RelationalWriteExecutorResult.Update(
                    new UpdateResult.UpdateSuccess(
                        new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"))
                    ),
                    RelationalWriteExecutorAttemptOutcome.GuardedNoOp.Instance
                )
            );
        _currentStateLoader.LoadCallCount.Should().Be(1);
        _writeFreshnessChecker.IsCurrentCallCount.Should().Be(1);
        _noProfilePersister.TryPersistCallCount.Should().Be(0);
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
                    new UpsertResult.InsertSuccess(candidateDocumentUuid),
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
                ]
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
                    new UpsertResult.UpdateSuccess(existingDocumentUuid),
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
                        new DocumentUuid(Guid.Parse("cccccccc-1111-2222-3333-dddddddddddd"))
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
                        new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"))
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
                        new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"))
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
            ]
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
    public async Task It_maps_flattener_validation_failures_for_put_requests()
    {
        var request = CreateRequest(RelationalWriteOperationKind.Put);
        var validationFailure = new WriteValidationFailure(
            new JsonPath("$.schoolYear"),
            "expected scalar kind 'Int32'"
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
        var identityProjectionColumns = resourceModel
            .Root.Columns.Where(columnModel => columnModel.Kind == ColumnKind.Scalar)
            .Select(columnModel => columnModel.ColumnName)
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
                        identityProjectionColumns,
                        new TriggerKindParameters.DocumentStamping()
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
                        ]
                    )
            );
        }
    }

    private sealed class RecordingRelationalWriteNoProfileMergeSynthesizer
        : IRelationalWriteNoProfileMergeSynthesizer
    {
        public int SynthesizeCallCount { get; private set; }

        public RelationalWriteNoProfileMergeRequest? CapturedRequest { get; private set; }

        public RelationalWriteNoProfileMergeResult? ResultToReturn { get; set; }

        public RelationalWriteNoProfileMergeResult Synthesize(RelationalWriteNoProfileMergeRequest request)
        {
            SynthesizeCallCount++;
            CapturedRequest = request;

            return ResultToReturn
                ?? new RelationalWriteNoProfileMergeResult([
                    new RelationalWriteNoProfileTableState(
                        request.WritePlan.TablePlansInDependencyOrder[0],
                        [
                            new RelationalWriteNoProfileTableRow(
                                request.FlattenedWriteSet.RootRow.Values,
                                request.FlattenedWriteSet.RootRow.Values
                            ),
                        ],
                        [
                            new RelationalWriteNoProfileTableRow(
                                request.FlattenedWriteSet.RootRow.Values,
                                request.FlattenedWriteSet.RootRow.Values
                            ),
                        ]
                    ),
                ]);
        }
    }

    private sealed class RecordingRelationalWriteNoProfilePersister : IRelationalWriteNoProfilePersister
    {
        public int TryPersistCallCount { get; private set; }

        public RelationalWriteExecutorRequest? CapturedRequest { get; private set; }

        public RelationalWriteNoProfileMergeResult? CapturedMergeResult { get; private set; }

        public IRelationalWriteSession? CapturedWriteSession { get; private set; }

        public Exception? ExceptionToThrow { get; set; }

        public Task PersistAsync(
            RelationalWriteExecutorRequest request,
            RelationalWriteNoProfileMergeResult mergeResult,
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

            return Task.CompletedTask;
        }
    }

    private static RelationalWriteNoProfileMergeResult CreateMergeResult(
        TableWritePlan rootTableWritePlan,
        int currentSchoolId,
        int mergedSchoolId,
        string currentName = "Lincoln High",
        string mergedName = "Lincoln High"
    ) =>
        new([
            new RelationalWriteNoProfileTableState(
                rootTableWritePlan,
                [CreateRootTableRow(345L, currentSchoolId, currentName)],
                [CreateRootTableRow(345L, mergedSchoolId, mergedName)]
            ),
        ]);

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
            ]
        );
    }

    private static RelationalWriteNoProfileTableRow CreateRootTableRow(
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

        public int CommitCallCount { get; private set; }

        public int RollbackCallCount { get; private set; }

        public int DisposeCallCount { get; private set; }

        public DbCommand CreateCommand(RelationalCommand command) => throw new NotSupportedException();

        public Task CommitAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CommitCallCount++;
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
}

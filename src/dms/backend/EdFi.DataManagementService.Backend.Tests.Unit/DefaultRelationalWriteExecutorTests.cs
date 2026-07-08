// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.Etag;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.External.Profile;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Backend.Profile;
using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Backend.Tests.Unit.Profile;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.External.Security;
using EdFi.DataManagementService.Core.Profile;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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
    private RecordingRelationalCurrentEtagPreconditionChecker _currentEtagPreconditionChecker = null!;
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
        _currentEtagPreconditionChecker = new RecordingRelationalCurrentEtagPreconditionChecker();
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
            _currentEtagPreconditionChecker,
            _targetLookupResolver,
            _writeFreshnessChecker,
            _noProfileMergeSynthesizer,
            _profileMergeSynthesizer,
            _noProfilePersister,
            _writeExceptionClassifier,
            _writeConstraintResolver,
            _readMaterializer,
            new ServedEtagComposer(),
            new IfMatchEvaluator(),
            Options.Create(new ResourceLinksOptions())
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
                        ComposedWriteResultEtag(77L)
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
        _writeFlattener.CapturedInput.AllowMissingDocumentReferencesForPrecedence.Should().BeFalse();
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
    public async Task It_short_circuits_descriptor_reference_failures_before_flattening()
    {
        var descriptorReference = RelationalAccessTestData.CreateDescriptorReference(
            new ReferentialId(Guid.NewGuid()),
            "uri://ed-fi.org/SchoolTypeDescriptor#Alternative",
            "$.schoolTypeDescriptor"
        );
        var request = CreateRequest(
            RelationalWriteOperationKind.Post,
            descriptorReferences: [descriptorReference]
        );

        var result = await _sut.ExecuteAsync(request);

        result
            .Should()
            .BeEquivalentTo(
                new RelationalWriteExecutorResult.Upsert(
                    new UpsertResult.UpsertFailureReference(
                        [],
                        [DescriptorReferenceFailureClassifier.Missing(descriptorReference)]
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
    public async Task It_short_circuits_descriptor_reference_failures_before_mixed_missing_document_references()
    {
        var descriptorReference = RelationalAccessTestData.CreateDescriptorReference(
            new ReferentialId(Guid.NewGuid()),
            "uri://ed-fi.org/SchoolTypeDescriptor#Alternative",
            "$.schoolTypeDescriptor"
        );
        var documentReference = RelationalAccessTestData.CreateDocumentReference(
            new ReferentialId(Guid.NewGuid()),
            "$.schoolReference"
        );
        var request = CreateRequest(
            RelationalWriteOperationKind.Post,
            documentReferences: [documentReference],
            descriptorReferences: [descriptorReference]
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
                        [DescriptorReferenceFailureClassifier.Missing(descriptorReference)]
                    )
                )
            );
        _writeFlattener.FlattenCallCount.Should().Be(0);
        _noProfileMergeSynthesizer.SynthesizeCallCount.Should().Be(0);
        _noProfilePersister.TryPersistCallCount.Should().Be(0);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_short_circuits_non_missing_document_reference_failures_before_flattening()
    {
        var referentialId = new ReferentialId(Guid.NewGuid());
        var documentReference = RelationalAccessTestData.CreateDocumentReference(
            referentialId,
            "$.schoolReference"
        );
        var request = CreateRequest(
            RelationalWriteOperationKind.Post,
            documentReferences: [documentReference]
        );
        _referenceResolverAdapterFactory.Adapter.LookupResults =
        [
            new ReferenceLookupResult(referentialId, 202L, 12, 12, false, "$.schoolId=255901"),
        ];

        var result = await _sut.ExecuteAsync(request);

        result
            .Should()
            .BeEquivalentTo(
                new RelationalWriteExecutorResult.Upsert(
                    new UpsertResult.UpsertFailureReference(
                        [
                            DocumentReferenceFailure.From(
                                documentReference,
                                DocumentReferenceFailureReason.IncompatibleTargetType
                            ),
                        ],
                        []
                    )
                )
            );
        _writeFlattener.FlattenCallCount.Should().Be(0);
        _noProfileMergeSynthesizer.SynthesizeCallCount.Should().Be(0);
        _noProfilePersister.AuthorizeProposedRelationshipCallCount.Should().Be(0);
        _noProfilePersister.TryPersistCallCount.Should().Be(0);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_defers_missing_document_reference_failures_until_after_no_profile_merge()
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
        _writeFlattener.FlattenCallCount.Should().Be(1);
        _writeFlattener.CapturedInput.Should().NotBeNull();
        _writeFlattener.CapturedInput!.AllowMissingDocumentReferencesForPrecedence.Should().BeTrue();
        _noProfileMergeSynthesizer.SynthesizeCallCount.Should().Be(1);
        _targetLookupResolver.ResolveForPostCallCount.Should().Be(1);
        _noProfilePersister.TryPersistCallCount.Should().Be(0);
        _writeSessionFactory.Session.CommitCallCount.Should().Be(0);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_returns_deferred_missing_document_reference_failures_before_guarded_no_op_success()
    {
        var documentReference = RelationalAccessTestData.CreateDocumentReference(
            new ReferentialId(Guid.NewGuid()),
            "$.schoolReference"
        );
        var request = CreateRequest(
            RelationalWriteOperationKind.Put,
            documentReferences: [documentReference]
        );

        var result = await _sut.ExecuteAsync(request);

        result
            .Should()
            .BeEquivalentTo(
                new RelationalWriteExecutorResult.Update(
                    new UpdateResult.UpdateFailureReference(
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
        _writeFreshnessChecker.IsCurrentCallCount.Should().Be(0);
        _noProfilePersister.TryPersistCallCount.Should().Be(0);
        _writeSessionFactory.Session.CommitCallCount.Should().Be(0);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_keeps_profile_missing_document_reference_failures_immediate()
    {
        var documentReference = RelationalAccessTestData.CreateDocumentReference(
            new ReferentialId(Guid.NewGuid()),
            "$.schoolReference"
        );
        var writableBody = JsonNode.Parse("""{"name":"Lincoln High"}""")!;
        var request = CreateRequest(
            RelationalWriteOperationKind.Post,
            documentReferences: [documentReference],
            selectedBody: writableBody
        );

        var result = await _sut.ExecuteAsync(
            request with
            {
                ProfileWriteContext = BuildVisiblePresentRootProfileWriteContext(
                    writableBody,
                    request.WritePlan
                ),
            }
        );

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
        _profileMergeSynthesizer.SynthesizeCallCount.Should().Be(0);
        _targetLookupResolver.ResolveForPostCallCount.Should().Be(0);
        _noProfilePersister.TryPersistCallCount.Should().Be(0);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_returns_proposed_relationship_authorization_failure_before_deferred_missing_reference()
    {
        var documentReference = RelationalAccessTestData.CreateDocumentReference(
            new ReferentialId(Guid.NewGuid()),
            "$.schoolReference"
        );
        var request = CreateRequest(
            RelationalWriteOperationKind.Put,
            documentReferences: [documentReference]
        );
        var relationshipFailure = CreateProposedSchoolIdRelationshipFailure(request);
        _noProfilePersister.ProposedAuthorizationExceptionToThrow =
            new RelationalWriteRelationshipAuthorizationNotAuthorizedException(relationshipFailure);

        var result = await _sut.ExecuteAsync(
            request with
            {
                ProposedRelationshipAuthorization = CreateProposedSchoolIdRelationshipAuthorization(request),
            }
        );

        var updateResult = result.Should().BeOfType<RelationalWriteExecutorResult.Update>().Subject;
        updateResult
            .Result.Should()
            .BeOfType<UpdateResult.UpdateFailureRelationshipNotAuthorized>()
            .Which.RelationshipFailure.Should()
            .BeSameAs(relationshipFailure);
        _currentStateLoader.LoadCallCount.Should().Be(1);
        _writeFlattener.FlattenCallCount.Should().Be(1);
        _noProfileMergeSynthesizer.SynthesizeCallCount.Should().Be(1);
        _noProfilePersister.AuthorizeProposedRelationshipCallCount.Should().Be(1);
        _noProfilePersister.TryPersistCallCount.Should().Be(0);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_returns_immutable_identity_failure_before_deferred_missing_reference()
    {
        var documentReference = RelationalAccessTestData.CreateDocumentReference(
            new ReferentialId(Guid.NewGuid()),
            "$.schoolReference"
        );
        var request = CreateRequest(
            RelationalWriteOperationKind.Put,
            documentReferences: [documentReference],
            selectedBody: JsonNode.Parse("""{"schoolId":255902,"name":"Lincoln High Updated"}""")!
        );
        _noProfileMergeSynthesizer.ResultToReturn = CreateMergeResult(
            request.WritePlan.TablePlansInDependencyOrder[0],
            currentSchoolId: 255901,
            mergedSchoolId: 255902,
            currentName: "Lincoln High",
            mergedName: "Lincoln High Updated"
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
        _writeFlattener.CapturedInput!.AllowMissingDocumentReferencesForPrecedence.Should().BeTrue();
        _currentStateLoader.LoadCallCount.Should().Be(1);
        _noProfileMergeSynthesizer.SynthesizeCallCount.Should().Be(1);
        _noProfilePersister.AuthorizeProposedRelationshipCallCount.Should().Be(0);
        _noProfilePersister.TryPersistCallCount.Should().Be(0);
        _writeSessionFactory.Session.CommitCallCount.Should().Be(0);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_forces_create_new_post_proposed_relationship_authorization_before_deferred_missing_reference()
    {
        var documentReference = RelationalAccessTestData.CreateDocumentReference(
            new ReferentialId(Guid.NewGuid()),
            "$.schoolReference"
        );
        var request = CreateRequest(
            RelationalWriteOperationKind.Post,
            documentReferences: [documentReference]
        );

        var result = await _sut.ExecuteAsync(
            request with
            {
                ProposedRelationshipAuthorization = CreateProposedSchoolIdRelationshipAuthorization(request),
            }
        );

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
        _targetLookupResolver.ResolveForPostCallCount.Should().Be(1);
        _writeFlattener.FlattenCallCount.Should().Be(1);
        _noProfileMergeSynthesizer.SynthesizeCallCount.Should().Be(1);
        _noProfilePersister.AuthorizeProposedRelationshipCallCount.Should().Be(1);
        _noProfilePersister.TryPersistCallCount.Should().Be(0);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_returns_proposed_namespace_authorization_failure_before_deferred_missing_reference()
    {
        var payload = NamespaceAuthorizationAuth1FailurePayloadCodec.Encode(
            new NamespaceAuthorizationAuth1FailurePayload(
                0,
                NamespaceAuthorizationAuth1FailureKind.NamespaceMismatch
            )
        );
        UseNamespaceProviderFailureExtractor(payload);
        _writeSessionFactory.Session.RelationshipAuthorizationCommandExecutor =
            new ThrowingRelationalCommandExecutor(SqlDialect.Pgsql, new StubDbException("namespace AUTH1"));
        var documentReference = RelationalAccessTestData.CreateDocumentReference(
            new ReferentialId(Guid.NewGuid()),
            "$.educationOrganizationReference"
        );
        var rootPlan = CreateNamespaceRootPlan();
        var request = CreateRequest(
            RelationalWriteOperationKind.Post,
            documentReferences: [documentReference],
            rootWritePlan: rootPlan,
            selectedBody: JsonNode.Parse("""{"namespace":"uri://other.org/Survey"}""")!
        );
        _writeFlattener.ResultToReturn = new FlattenedWriteSet(
            new RootWriteRowBuffer(
                rootPlan,
                [
                    FlattenedWriteValue.UnresolvedRootDocumentId.Instance,
                    new FlattenedWriteValue.Literal("uri://other.org/Survey"),
                ]
            )
        );

        var result = await _sut.ExecuteAsync(
            request with
            {
                ProposedNamespaceAuthorization = CreateProposedNamespaceAuthorization(),
            }
        );

        var notAuthorized = result
            .Should()
            .BeOfType<RelationalWriteExecutorResult.Upsert>()
            .Which.Result.Should()
            .BeOfType<UpsertResult.UpsertFailureNamespaceNotAuthorized>()
            .Subject;
        notAuthorized
            .NamespaceFailure.FailureKind.Should()
            .Be(NamespaceAuthorizationFailureKind.NamespaceMismatch);
        notAuthorized
            .NamespaceFailure.ValueSource.Should()
            .Be(NamespaceAuthorizationFailureValueSource.Proposed);
        _writeFlattener.CapturedInput!.AllowMissingDocumentReferencesForPrecedence.Should().BeTrue();
        _noProfileMergeSynthesizer.SynthesizeCallCount.Should().Be(1);
        _noProfilePersister.AuthorizeProposedRelationshipCallCount.Should().Be(0);
        _noProfilePersister.TryPersistCallCount.Should().Be(0);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_authorizes_stored_relationship_values_for_existing_put_before_reference_resolution()
    {
        var descriptorReference = RelationalAccessTestData.CreateDescriptorReference(
            new ReferentialId(Guid.NewGuid()),
            "uri://ed-fi.org/SchoolTypeDescriptor#Alternative",
            "$.schoolTypeDescriptor"
        );
        var request = CreateRequest(
            RelationalWriteOperationKind.Put,
            descriptorReferences: [descriptorReference]
        );

        var result = await _sut.ExecuteAsync(
            request with
            {
                StoredRelationshipAuthorization = CreateStoredSchoolIdRelationshipAuthorization(request),
            }
        );

        result
            .Should()
            .BeEquivalentTo(
                new RelationalWriteExecutorResult.Update(
                    new UpdateResult.UpdateFailureReference(
                        [],
                        [DescriptorReferenceFailureClassifier.Missing(descriptorReference)]
                    )
                )
            );
        _writeSessionFactory.Session.CreateCommandExecutorCallCount.Should().Be(1);
        _writeSessionFactory.Session.RelationshipAuthorizationCommands.Should().ContainSingle();
        _referenceResolverAdapterFactory.CreateSessionAdapterCallCount.Should().Be(1);
        _currentStateLoader.LoadCallCount.Should().Be(0);
        _writeFlattener.FlattenCallCount.Should().Be(0);
        _noProfilePersister.TryPersistCallCount.Should().Be(0);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_uses_provider_parameter_configurator_for_stored_relationship_authorization_inside_the_write_session()
    {
        var parameterConfigurator = new RecordingRelationalParameterConfigurator();
        _sut = new DefaultRelationalWriteExecutor(
            _writeSessionFactory,
            _referenceResolverAdapterFactory,
            _writeFlattener,
            _currentStateLoader,
            _currentEtagPreconditionChecker,
            _targetLookupResolver,
            _writeFreshnessChecker,
            _noProfileMergeSynthesizer,
            _profileMergeSynthesizer,
            _noProfilePersister,
            _writeExceptionClassifier,
            _writeConstraintResolver,
            _readMaterializer,
            new ServedEtagComposer(),
            new IfMatchEvaluator(),
            Options.Create(new ResourceLinksOptions()),
            parameterConfigurator
        );
        var documentReference = RelationalAccessTestData.CreateDocumentReference(
            new ReferentialId(Guid.NewGuid()),
            "$.schoolReference"
        );
        var request = CreateRequest(
            RelationalWriteOperationKind.Put,
            documentReferences: [documentReference],
            dialect: SqlDialect.Mssql
        );
        var storedAuthorization = CreateStoredSchoolIdRelationshipAuthorization(request) with
        {
            ClaimEducationOrganizationIdParameterization =
                AuthorizationClaimEducationOrganizationIdParameterizationFactory.Create(
                    SqlDialect.Mssql,
                    Enumerable.Range(1, 2000).Select(static id => (long)id).ToArray(),
                    RelationalAuthorizationParameterNameConstants.ClaimEducationOrganizationIds
                ),
        };

        var result = await _sut.ExecuteAsync(
            request with
            {
                StoredRelationshipAuthorization = storedAuthorization,
            }
        );

        result.Should().BeOfType<RelationalWriteExecutorResult.Update>();
        var command = _writeSessionFactory
            .Session.RelationshipAuthorizationCommands.Should()
            .ContainSingle()
            .Subject;
        var claimParameter = command
            .Parameters.Should()
            .ContainSingle(static parameter => parameter.Name == "@ClaimEducationOrganizationIds")
            .Subject;
        claimParameter.Value.Should().BeOfType<DataTable>().Which.Rows.Should().HaveCount(2000);
        claimParameter.ConfigureParameter.Should().NotBeNull();

        claimParameter.ConfigureParameter!(new StubDbParameter());

        parameterConfigurator.CapturedParameters.Should().ContainSingle();
        parameterConfigurator
            .CapturedParameters[0]
            .Binding.Should()
            .BeEquivalentTo(QuerySqlParameterBinding.CreateMssqlStructured("dms.BigIntTable", "Id"));
    }

    [Test]
    public async Task It_uses_provider_failure_extractor_for_stored_relationship_authorization_inside_the_write_session()
    {
        var auth1Payload = RelationshipAuthorizationAuth1FailurePayloadCodec.Encode(
            new RelationshipAuthorizationAuth1FailurePayload(
                0,
                [
                    new RelationshipAuthorizationAuth1SubjectFailure(
                        0,
                        0,
                        RelationshipAuthorizationAuth1SubjectFailureKind.NoRelationship
                    ),
                ]
            )
        );
        var providerFailureExtractor = new StubRelationshipAuthorizationProviderFailureExtractor(
            RelationshipAuthorizationAuth1FailurePayloadCodec.ProviderFailureCode,
            auth1Payload
        );
        _sut = new DefaultRelationalWriteExecutor(
            _writeSessionFactory,
            _referenceResolverAdapterFactory,
            _writeFlattener,
            _currentStateLoader,
            _currentEtagPreconditionChecker,
            _targetLookupResolver,
            _writeFreshnessChecker,
            _noProfileMergeSynthesizer,
            _profileMergeSynthesizer,
            _noProfilePersister,
            _writeExceptionClassifier,
            _writeConstraintResolver,
            _readMaterializer,
            new ServedEtagComposer(),
            new IfMatchEvaluator(),
            Options.Create(new ResourceLinksOptions()),
            relationshipAuthorizationProviderFailureExtractor: providerFailureExtractor
        );
        _writeSessionFactory.Session.RelationshipAuthorizationCommandExecutor =
            new ThrowingRelationalCommandExecutor(SqlDialect.Pgsql, new StubDbException("AUTH1 failed"));
        var request = CreateRequest(RelationalWriteOperationKind.Put);

        var result = await _sut.ExecuteAsync(
            request with
            {
                StoredRelationshipAuthorization = CreateStoredSchoolIdRelationshipAuthorization(request),
            }
        );

        var updateResult = result.Should().BeOfType<RelationalWriteExecutorResult.Update>().Subject;
        var relationshipFailure = updateResult
            .Result.Should()
            .BeOfType<UpdateResult.UpdateFailureRelationshipNotAuthorized>()
            .Which.RelationshipFailure;
        relationshipFailure.ValueSource.Should().Be(RelationshipAuthorizationFailureValueSource.Stored);
        relationshipFailure
            .FailedStrategies.Should()
            .ContainSingle()
            .Which.FailedSubjects.Should()
            .ContainSingle()
            .Which.FailureKind.Should()
            .Be(RelationshipAuthorizationSubjectFailureKind.NoRelationship);
        providerFailureExtractor.ExtractCallCount.Should().Be(1);
        _referenceResolverAdapterFactory.CreateSessionAdapterCallCount.Should().Be(0);
        _currentStateLoader.LoadCallCount.Should().Be(0);
        _noProfilePersister.TryPersistCallCount.Should().Be(0);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_returns_security_configuration_when_stored_relationship_auth1_payload_is_invalid()
    {
        var providerFailureExtractor = new StubRelationshipAuthorizationProviderFailureExtractor(
            RelationshipAuthorizationAuth1FailurePayloadCodec.ProviderFailureCode,
            "2|0|1|0:0:n"
        );
        var logger = new RecordingLogger<DefaultRelationalWriteExecutor>();
        _sut = new DefaultRelationalWriteExecutor(
            _writeSessionFactory,
            _referenceResolverAdapterFactory,
            _writeFlattener,
            _currentStateLoader,
            _currentEtagPreconditionChecker,
            _targetLookupResolver,
            _writeFreshnessChecker,
            _noProfileMergeSynthesizer,
            _profileMergeSynthesizer,
            _noProfilePersister,
            _writeExceptionClassifier,
            _writeConstraintResolver,
            _readMaterializer,
            new ServedEtagComposer(),
            new IfMatchEvaluator(),
            Options.Create(new ResourceLinksOptions()),
            relationshipAuthorizationProviderFailureExtractor: providerFailureExtractor,
            logger: logger
        );
        _writeSessionFactory.Session.RelationshipAuthorizationCommandExecutor =
            new ThrowingRelationalCommandExecutor(SqlDialect.Pgsql, new StubDbException("AUTH1 failed"));
        var request = CreateRequest(RelationalWriteOperationKind.Put);

        var result = await _sut.ExecuteAsync(
            request with
            {
                StoredRelationshipAuthorization = CreateStoredSchoolIdRelationshipAuthorization(request),
            }
        );

        var updateResult = result.Should().BeOfType<RelationalWriteExecutorResult.Update>().Subject;
        var securityConfigurationFailure = updateResult
            .Result.Should()
            .BeOfType<UpdateResult.UpdateFailureSecurityConfiguration>()
            .Subject;
        securityConfigurationFailure
            .Errors.Should()
            .Equal(
                RelationshipAuthorizationSecurityConfigurationFailureMessages.InvalidFailurePayloadSecurityConfigurationError
            )
            .And.NotContain(error => error.Contains("2|0|1|0:0:n", StringComparison.Ordinal))
            .And.NotContain(error => error.Contains("AUTH1 failed", StringComparison.Ordinal));
        securityConfigurationFailure
            .Diagnostics.Should()
            .ContainSingle()
            .Which.ProviderOrPlannerFailureKind.Should()
            .Be("RelationshipAuthorization.Auth1.PayloadParseFailed");
        var logRecord = logger.Records.Should().ContainSingle().Subject;
        logRecord.Level.Should().Be(LogLevel.Error);
        logRecord.Message.Should().Contain("Dialect: Pgsql");
        logRecord.Message.Should().Contain("ExpectedEmittedAuth1Index: 0");
        logRecord.Message.Should().Contain("ProviderErrorCode: AUTH1");
        logRecord.Message.Should().Contain("ProviderMessageFragment: 2|0|1|0:0:n");
        logRecord.Message.Should().Contain("MappingFailureCategory: PayloadParseFailed");
        providerFailureExtractor.ExtractCallCount.Should().Be(1);
        _referenceResolverAdapterFactory.CreateSessionAdapterCallCount.Should().Be(0);
        _currentStateLoader.LoadCallCount.Should().Be(0);
        _noProfilePersister.TryPersistCallCount.Should().Be(0);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_locks_existing_put_target_before_returning_stored_relationship_no_claims()
    {
        var documentReference = RelationalAccessTestData.CreateDocumentReference(
            new ReferentialId(Guid.NewGuid()),
            "$.schoolReference"
        );
        var request = CreateRequest(
            RelationalWriteOperationKind.Put,
            documentReferences: [documentReference]
        );

        var result = await _sut.ExecuteAsync(
            request with
            {
                StoredRelationshipAuthorization = CreateStoredSchoolIdNoClaimsAuthorization(request),
            }
        );

        var updateResult = result.Should().BeOfType<RelationalWriteExecutorResult.Update>().Subject;
        var notAuthorized = updateResult
            .Result.Should()
            .BeOfType<UpdateResult.UpdateFailureRelationshipNotAuthorized>()
            .Subject;
        notAuthorized
            .RelationshipFailure.ValueSource.Should()
            .Be(RelationshipAuthorizationFailureValueSource.Stored);
        notAuthorized.RelationshipFailure.ClaimEducationOrganizationIds.Should().BeEmpty();
        notAuthorized
            .RelationshipFailure.FailedStrategies.Should()
            .ContainSingle()
            .Which.FailedSubjects.Should()
            .ContainSingle()
            .Which.FailureKind.Should()
            .Be(RelationshipAuthorizationSubjectFailureKind.NoRelationship);
        _referenceResolverAdapterFactory.CreateSessionAdapterCallCount.Should().Be(0);
        _writeSessionFactory.Session.Commands.Should().ContainSingle();
        _writeSessionFactory.Session.CreateCommandExecutorCallCount.Should().Be(0);
        _currentStateLoader.LoadCallCount.Should().Be(0);
        _writeFlattener.FlattenCallCount.Should().Be(0);
        _noProfilePersister.TryPersistCallCount.Should().Be(0);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_returns_not_exists_when_put_target_disappears_before_stored_relationship_no_claims()
    {
        var documentReference = RelationalAccessTestData.CreateDocumentReference(
            new ReferentialId(Guid.NewGuid()),
            "$.schoolReference"
        );
        var request = CreateRequest(
            RelationalWriteOperationKind.Put,
            documentReferences: [documentReference]
        );
        _writeSessionFactory.Session.ScalarResultToReturn = null;

        var result = await _sut.ExecuteAsync(
            request with
            {
                StoredRelationshipAuthorization = CreateStoredSchoolIdNoClaimsAuthorization(request),
            }
        );

        result
            .Should()
            .BeEquivalentTo(
                new RelationalWriteExecutorResult.Update(new UpdateResult.UpdateFailureNotExists())
            );
        _referenceResolverAdapterFactory.CreateSessionAdapterCallCount.Should().Be(0);
        _writeSessionFactory.Session.Commands.Should().ContainSingle();
        _writeSessionFactory.Session.CreateCommandExecutorCallCount.Should().Be(0);
        _currentStateLoader.LoadCallCount.Should().Be(0);
        _writeFlattener.FlattenCallCount.Should().Be(0);
        _noProfilePersister.TryPersistCallCount.Should().Be(0);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_returns_stored_relationship_no_claims_before_put_profile_failures()
    {
        var writableBody = JsonNode.Parse("""{"schoolId":255901}""")!;
        var request = CreateRequest(RelationalWriteOperationKind.Put, selectedBody: writableBody);
        _profileMergeSynthesizer.ExceptionToThrow = new ProfilePlannerContractMismatchException(
            jsonScope: "$.addresses[*]",
            invariantName: "reverse stored coverage",
            message: "profile merge should not run before stored authorization"
        );

        var result = await _sut.ExecuteAsync(
            request with
            {
                ProfileWriteContext = BuildVisiblePresentRootProfileWriteContext(
                    writableBody,
                    request.WritePlan
                ),
                StoredRelationshipAuthorization = CreateStoredSchoolIdNoClaimsAuthorization(request),
            }
        );

        var updateResult = result.Should().BeOfType<RelationalWriteExecutorResult.Update>().Subject;
        updateResult
            .Result.Should()
            .BeOfType<UpdateResult.UpdateFailureRelationshipNotAuthorized>()
            .Which.RelationshipFailure.ValueSource.Should()
            .Be(RelationshipAuthorizationFailureValueSource.Stored);
        _referenceResolverAdapterFactory.CreateSessionAdapterCallCount.Should().Be(0);
        _writeSessionFactory.Session.Commands.Should().ContainSingle();
        _currentStateLoader.LoadCallCount.Should().Be(0);
        _readMaterializer.MaterializeCallCount.Should().Be(0);
        _profileMergeSynthesizer.SynthesizeCallCount.Should().Be(0);
        _noProfilePersister.TryPersistCallCount.Should().Be(0);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
    }

    [TestCase(RelationalWriteOperationKind.Put)]
    [TestCase(RelationalWriteOperationKind.Post)]
    public async Task It_returns_stored_relationship_no_claims_before_proposed_authorization_for_existing_updates(
        RelationalWriteOperationKind operationKind
    )
    {
        var existingDocumentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));
        var documentReference = RelationalAccessTestData.CreateDocumentReference(
            new ReferentialId(Guid.NewGuid()),
            "$.schoolReference"
        );
        var request = CreateRequest(
            operationKind,
            documentReferences: [documentReference],
            targetContext: new RelationalWriteTargetContext.ExistingDocument(345L, existingDocumentUuid, 44L)
        );
        if (operationKind is RelationalWriteOperationKind.Post)
        {
            _targetLookupResolver.PostResults.Enqueue(
                new RelationalWriteTargetLookupResult.ExistingDocument(345L, existingDocumentUuid, 44L)
            );
        }

        var result = await _sut.ExecuteAsync(
            request with
            {
                StoredRelationshipAuthorization = CreateStoredSchoolIdNoClaimsAuthorization(request),
                ProposedRelationshipAuthorization = CreateProposedSchoolIdRelationshipAuthorization(request),
            }
        );

        var relationshipFailure = operationKind switch
        {
            RelationalWriteOperationKind.Put => result
                .Should()
                .BeOfType<RelationalWriteExecutorResult.Update>()
                .Subject.Result.Should()
                .BeOfType<UpdateResult.UpdateFailureRelationshipNotAuthorized>()
                .Subject.RelationshipFailure,
            RelationalWriteOperationKind.Post => result
                .Should()
                .BeOfType<RelationalWriteExecutorResult.Upsert>()
                .Subject.Result.Should()
                .BeOfType<UpsertResult.UpsertFailureRelationshipNotAuthorized>()
                .Subject.RelationshipFailure,
            _ => throw new ArgumentOutOfRangeException(nameof(operationKind), operationKind, null),
        };
        relationshipFailure.ValueSource.Should().Be(RelationshipAuthorizationFailureValueSource.Stored);
        relationshipFailure.ClaimEducationOrganizationIds.Should().BeEmpty();
        relationshipFailure
            .FailedStrategies.Should()
            .ContainSingle()
            .Which.FailedSubjects.Should()
            .ContainSingle()
            .Which.FailureKind.Should()
            .Be(RelationshipAuthorizationSubjectFailureKind.NoRelationship);
        _referenceResolverAdapterFactory.CreateSessionAdapterCallCount.Should().Be(0);
        _writeSessionFactory.Session.Commands.Should().ContainSingle();
        _writeSessionFactory.Session.CreateCommandExecutorCallCount.Should().Be(0);
        _currentStateLoader.LoadCallCount.Should().Be(0);
        _writeFlattener.FlattenCallCount.Should().Be(0);
        _noProfilePersister.AuthorizeProposedRelationshipCallCount.Should().Be(0);
        _noProfilePersister.TryPersistCallCount.Should().Be(0);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_authorizes_stored_relationship_values_for_existing_post_before_reference_resolution()
    {
        var existingDocumentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));
        var descriptorReference = RelationalAccessTestData.CreateDescriptorReference(
            new ReferentialId(Guid.NewGuid()),
            "uri://ed-fi.org/SchoolTypeDescriptor#Alternative",
            "$.schoolTypeDescriptor"
        );
        var request = CreateRequest(
            RelationalWriteOperationKind.Post,
            descriptorReferences: [descriptorReference],
            targetContext: new RelationalWriteTargetContext.ExistingDocument(345L, existingDocumentUuid, 44L)
        );
        _targetLookupResolver.PostResults.Enqueue(
            new RelationalWriteTargetLookupResult.ExistingDocument(345L, existingDocumentUuid, 44L)
        );

        var result = await _sut.ExecuteAsync(
            request with
            {
                StoredRelationshipAuthorization = CreateStoredSchoolIdRelationshipAuthorization(request),
            }
        );

        result
            .Should()
            .BeEquivalentTo(
                new RelationalWriteExecutorResult.Upsert(
                    new UpsertResult.UpsertFailureReference(
                        [],
                        [DescriptorReferenceFailureClassifier.Missing(descriptorReference)]
                    )
                )
            );
        _targetLookupResolver.ResolveForPostCallCount.Should().Be(1);
        _writeSessionFactory.Session.CreateCommandExecutorCallCount.Should().Be(1);
        _writeSessionFactory.Session.RelationshipAuthorizationCommands.Should().ContainSingle();
        _referenceResolverAdapterFactory.CreateSessionAdapterCallCount.Should().Be(1);
        _currentStateLoader.LoadCallCount.Should().Be(0);
        _writeFlattener.FlattenCallCount.Should().Be(0);
        _noProfilePersister.TryPersistCallCount.Should().Be(0);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
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
                        ComposedWriteResultEtag(77L)
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
            ((RelationalWriteTargetContext.CreateNew)request.TargetContext).DocumentUuid,
            77L
        );
        _noProfilePersister.ResultToReturn = persistedTarget;

        var result = await _sut.ExecuteAsync(request);

        result
            .Should()
            .BeEquivalentTo(
                new RelationalWriteExecutorResult.Upsert(
                    new UpsertResult.InsertSuccess(
                        persistedTarget.DocumentUuid,
                        ComposedWriteResultEtag(persistedTarget.ContentVersion)
                    ),
                    RelationalWriteExecutorAttemptOutcome.AppliedWrite.Instance
                )
            );
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
                    new UpsertResult.UpdateSuccess(existingDocumentUuid, ComposedWriteResultEtag(77L)),
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
        // The guarded no-op path composes the write-result etag from the target's
        // ObservedContentVersion (44L, from the default Put ExistingDocument target context built by
        // CreateRequest), not from any persister-produced stamp — no persister runs on this path.

        var result = await _sut.ExecuteAsync(request);

        result
            .Should()
            .BeEquivalentTo(
                new RelationalWriteExecutorResult.Update(
                    new UpdateResult.UpdateSuccess(
                        persistedTarget.DocumentUuid,
                        ComposedWriteResultEtag(44L)
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
                        new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb")),
                        ComposedWriteResultEtag(44L)
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
                        new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb")),
                        ComposedWriteResultEtag(45L)
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
                        new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb")),
                        ComposedWriteResultEtag(45L)
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
            ],
            []
        );
        _sut = new DefaultRelationalWriteExecutor(
            _writeSessionFactory,
            _referenceResolverAdapterFactory,
            _writeFlattener,
            _currentStateLoader,
            _currentEtagPreconditionChecker,
            _targetLookupResolver,
            _writeFreshnessChecker,
            new RelationalWriteNoProfileMergeSynthesizer(),
            _profileMergeSynthesizer,
            _noProfilePersister,
            _writeExceptionClassifier,
            _writeConstraintResolver,
            _readMaterializer,
            new ServedEtagComposer(),
            new IfMatchEvaluator(),
            Options.Create(new ResourceLinksOptions())
        );

        var result = await _sut.ExecuteAsync(request);

        result
            .Should()
            .BeEquivalentTo(
                new RelationalWriteExecutorResult.Update(
                    new UpdateResult.UpdateSuccess(
                        new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb")),
                        ComposedWriteResultEtag(44L)
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
    public async Task It_returns_if_match_failure_for_a_wildcard_put_when_the_target_is_missing()
    {
        // RFC 7232 If-Match: * requires the target to exist; against a missing PUT target the
        // wildcard yields 412 (ETag mismatch) rather than 404 (not exists). The precondition checker
        // returning null signals the target was absent under the If-Match precondition.
        var request = CreateRequest(
            RelationalWriteOperationKind.Put,
            writePrecondition: new WritePrecondition.IfMatch("some-wrong-value", IsWildcard: true)
        );

        var result = await _sut.ExecuteAsync(request);

        // UpdateFailureNotExists and UpdateFailureETagMisMatch are both memberless records, so
        // BeEquivalentTo cannot tell them apart; assert on the concrete inner result type instead.
        // The target is missing, so the reason is TargetDoesNotExist rather than a Concurrency mismatch.
        result
            .Should()
            .BeOfType<RelationalWriteExecutorResult.Update>()
            .Which.Result.Should()
            .BeOfType<UpdateResult.UpdateFailureETagMisMatch>()
            .Which.Reason.Should()
            .Be(ETagPreconditionFailureReason.TargetDoesNotExist);
        _currentEtagPreconditionChecker.CheckCallCount.Should().Be(1);
        _writeFlattener.FlattenCallCount.Should().Be(0);
        _currentStateLoader.LoadCallCount.Should().Be(0);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
        _writeSessionFactory.Session.DisposeCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_returns_not_exists_for_a_non_wildcard_put_when_the_target_is_missing_under_if_match()
    {
        // Regression guard: a non-wildcard If-Match against a missing PUT target still returns 404.
        var request = CreateRequest(
            RelationalWriteOperationKind.Put,
            writePrecondition: new WritePrecondition.IfMatch("\"stale-etag\"")
        );

        var result = await _sut.ExecuteAsync(request);

        result
            .Should()
            .BeOfType<RelationalWriteExecutorResult.Update>()
            .Which.Result.Should()
            .BeOfType<UpdateResult.UpdateFailureNotExists>();
        _currentEtagPreconditionChecker.CheckCallCount.Should().Be(1);
        _writeFlattener.FlattenCallCount.Should().Be(0);
        _currentStateLoader.LoadCallCount.Should().Be(0);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
        _writeSessionFactory.Session.DisposeCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_returns_if_match_failure_for_put_before_reference_resolution_when_the_current_etag_mismatches()
    {
        var documentReference = RelationalAccessTestData.CreateDocumentReference(
            new ReferentialId(Guid.NewGuid()),
            "$.schoolReference"
        );
        var request = CreateRequest(
            RelationalWriteOperationKind.Put,
            documentReferences: [documentReference],
            writePrecondition: new WritePrecondition.IfMatch("\"stale-etag\"")
        );
        _currentEtagPreconditionChecker.ResultToReturn = CreatePreconditionCheckResult(
            request,
            isMatch: false,
            currentEtag: "\"current-etag\"",
            contentVersion: 45L
        );

        var result = await _sut.ExecuteAsync(request);

        // The target exists but its current etag does not match the specific-tag If-Match precondition,
        // so the reason is Concurrency rather than TargetDoesNotExist.
        result
            .Should()
            .BeOfType<RelationalWriteExecutorResult.Update>()
            .Which.Result.Should()
            .BeOfType<UpdateResult.UpdateFailureETagMisMatch>()
            .Which.Reason.Should()
            .Be(ETagPreconditionFailureReason.Concurrency);
        _currentEtagPreconditionChecker.CheckCallCount.Should().Be(1);
        _referenceResolverAdapterFactory.CreateSessionAdapterCallCount.Should().Be(0);
        _writeFlattener.FlattenCallCount.Should().Be(0);
        _currentStateLoader.LoadCallCount.Should().Be(0);
        _noProfilePersister.TryPersistCallCount.Should().Be(0);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
        _writeSessionFactory.Session.DisposeCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_returns_if_match_failure_for_post_as_update_before_reference_resolution_when_the_current_etag_mismatches()
    {
        var existingDocumentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));
        var documentReference = RelationalAccessTestData.CreateDocumentReference(
            new ReferentialId(Guid.NewGuid()),
            "$.schoolReference"
        );
        var request = CreateRequest(
            RelationalWriteOperationKind.Post,
            documentReferences: [documentReference],
            targetContext: new RelationalWriteTargetContext.ExistingDocument(345L, existingDocumentUuid, 44L),
            writePrecondition: new WritePrecondition.IfMatch("\"stale-etag\"")
        );
        _targetLookupResolver.PostResults.Enqueue(
            new RelationalWriteTargetLookupResult.ExistingDocument(345L, existingDocumentUuid, 44L)
        );
        _currentEtagPreconditionChecker.ResultToReturn = CreatePreconditionCheckResult(
            request,
            isMatch: false,
            currentEtag: "\"current-etag\"",
            contentVersion: 45L
        );

        var result = await _sut.ExecuteAsync(request);

        result
            .Should()
            .BeEquivalentTo(
                new RelationalWriteExecutorResult.Upsert(new UpsertResult.UpsertFailureETagMisMatch())
            );
        _targetLookupResolver.ResolveForPostCallCount.Should().Be(1);
        _currentEtagPreconditionChecker.CheckCallCount.Should().Be(1);
        _referenceResolverAdapterFactory.CreateSessionAdapterCallCount.Should().Be(0);
        _writeFlattener.FlattenCallCount.Should().Be(0);
        _currentStateLoader.LoadCallCount.Should().Be(0);
        _noProfilePersister.TryPersistCallCount.Should().Be(0);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
        _writeSessionFactory.Session.DisposeCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_returns_if_match_failure_when_advisory_post_as_update_re_resolves_as_create_new()
    {
        var advisoryDocumentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));
        var request = CreateRequest(
            RelationalWriteOperationKind.Post,
            targetContext: new RelationalWriteTargetContext.ExistingDocument(345L, advisoryDocumentUuid, 44L),
            writePrecondition: new WritePrecondition.IfMatch("\"stale-etag\"")
        );
        var candidateDocumentUuid = (
            (RelationalWriteTargetRequest.Post)request.TargetRequest
        ).CandidateDocumentUuid;
        _targetLookupResolver.PostResults.Enqueue(
            new RelationalWriteTargetLookupResult.CreateNew(candidateDocumentUuid)
        );
        _currentEtagPreconditionChecker.ResultToReturn = CreatePreconditionCheckResult(
            request,
            isMatch: true,
            currentEtag: "\"stale-etag\"",
            contentVersion: 44L
        );

        var result = await _sut.ExecuteAsync(request);

        // Re-resolving the advisory POST target as CreateNew means there is no current representation
        // to satisfy the If-Match precondition against, so the reason is TargetDoesNotExist rather than
        // a Concurrency mismatch.
        result
            .Should()
            .BeOfType<RelationalWriteExecutorResult.Upsert>()
            .Which.Result.Should()
            .BeOfType<UpsertResult.UpsertFailureETagMisMatch>()
            .Which.Reason.Should()
            .Be(ETagPreconditionFailureReason.TargetDoesNotExist);
        _targetLookupResolver.ResolveForPostCallCount.Should().Be(1);
        _targetLookupResolver.CapturedWriteSession.Should().NotBeNull();
        _targetLookupResolver
            .CapturedWriteSession!.Connection.Should()
            .BeSameAs(_writeSessionFactory.Session.Connection);
        _targetLookupResolver
            .CapturedWriteSession!.Transaction.Should()
            .BeSameAs(_writeSessionFactory.Session.Transaction);
        _currentEtagPreconditionChecker.CheckCallCount.Should().Be(0);
        _referenceResolverAdapterFactory.CreateSessionAdapterCallCount.Should().Be(0);
        _writeFlattener.FlattenCallCount.Should().Be(0);
        _noProfilePersister.TryPersistCallCount.Should().Be(0);
        _writeSessionFactory.Session.CommitCallCount.Should().Be(0);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
        _writeSessionFactory.Session.DisposeCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_returns_if_match_failure_for_post_when_authoritative_target_resolution_proves_a_new_insert()
    {
        var request = CreateRequest(
            RelationalWriteOperationKind.Post,
            writePrecondition: new WritePrecondition.IfMatch("\"stale-etag\"")
        );

        var result = await _sut.ExecuteAsync(request);

        // Authoritative target resolution proving a new insert means there is no current
        // representation to satisfy the If-Match precondition against, so the reason is
        // TargetDoesNotExist rather than a Concurrency mismatch.
        result
            .Should()
            .BeOfType<RelationalWriteExecutorResult.Upsert>()
            .Which.Result.Should()
            .BeOfType<UpsertResult.UpsertFailureETagMisMatch>()
            .Which.Reason.Should()
            .Be(ETagPreconditionFailureReason.TargetDoesNotExist);
        _targetLookupResolver.ResolveForPostCallCount.Should().Be(1);
        _currentEtagPreconditionChecker.CheckCallCount.Should().Be(0);
        _referenceResolverAdapterFactory.CreateSessionAdapterCallCount.Should().Be(0);
        _writeFlattener.FlattenCallCount.Should().Be(0);
        _noProfilePersister.TryPersistCallCount.Should().Be(0);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
        _writeSessionFactory.Session.DisposeCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_applies_a_changed_put_when_if_match_exactly_matches_the_current_etag()
    {
        var request = CreateRequest(
            RelationalWriteOperationKind.Put,
            selectedBody: JsonNode.Parse("""{"schoolId":255901,"name":"Lincoln High Updated"}""")!,
            writePrecondition: new WritePrecondition.IfMatch("\"current-etag\"")
        );
        _currentEtagPreconditionChecker.ResultToReturn = CreatePreconditionCheckResult(
            request,
            isMatch: true,
            currentEtag: "\"current-etag\"",
            contentVersion: 45L
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
                        ComposedWriteResultEtag(77L)
                    ),
                    RelationalWriteExecutorAttemptOutcome.AppliedWrite.Instance
                )
            );
        _currentEtagPreconditionChecker.CheckCallCount.Should().Be(1);
        _referenceResolverAdapterFactory.CreateSessionAdapterCallCount.Should().Be(1);
        _currentStateLoader.LoadCallCount.Should().Be(0);
        _noProfileMergeSynthesizer.CapturedRequest.Should().NotBeNull();
        _noProfileMergeSynthesizer
            .CapturedRequest!.CurrentState.Should()
            .BeSameAs(_currentEtagPreconditionChecker.ResultToReturn!.CurrentState);
        _noProfilePersister.TryPersistCallCount.Should().Be(1);
        _writeSessionFactory.Session.CommitCallCount.Should().Be(1);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(0);
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
                    new UpsertResult.InsertSuccess(candidateDocumentUuid, ComposedWriteResultEtag(77L)),
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
                    new UpsertResult.UpdateSuccess(existingDocumentUuid, ComposedWriteResultEtag(77L)),
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
    public async Task It_returns_if_match_failure_with_a_stale_no_op_compare_outcome_when_guarded_freshness_is_lost()
    {
        var request = CreateRequest(
            RelationalWriteOperationKind.Put,
            writePrecondition: new WritePrecondition.IfMatch("\"current-etag\"")
        );
        _currentEtagPreconditionChecker.ResultToReturn = CreatePreconditionCheckResult(
            request,
            isMatch: true,
            currentEtag: "\"current-etag\"",
            contentVersion: 45L
        );
        _writeFreshnessChecker.IsCurrentResult = false;

        var result = await _sut.ExecuteAsync(request);

        result
            .Should()
            .BeEquivalentTo(
                new RelationalWriteExecutorResult.Update(
                    new UpdateResult.UpdateFailureETagMisMatch(),
                    RelationalWriteExecutorAttemptOutcome.StaleNoOpCompare.Instance
                )
            );
        result.AttemptOutcome.Should().Be(RelationalWriteExecutorAttemptOutcome.StaleNoOpCompare.Instance);
        _currentEtagPreconditionChecker.CheckCallCount.Should().Be(1);
        _currentStateLoader.LoadCallCount.Should().Be(0);
        _noProfileMergeSynthesizer.SynthesizeCallCount.Should().Be(1);
        _noProfilePersister.TryPersistCallCount.Should().Be(0);
        _writeFreshnessChecker.IsCurrentCallCount.Should().Be(1);
        _writeSessionFactory.Session.CommitCallCount.Should().Be(0);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
        _writeSessionFactory.Session.DisposeCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_returns_write_conflict_not_if_match_failure_for_wildcard_stale_no_op_compare()
    {
        // A wildcard If-Match (*) is an existence precondition, not a concurrency check. When a
        // guarded no-op goes stale but the row still exists, the wildcard must follow the
        // no-precondition path (write-conflict/retry) exactly like the None precondition case above,
        // NOT surface an ETag mismatch (412) as a specific-tag If-Match would.
        var request = CreateRequest(
            RelationalWriteOperationKind.Put,
            writePrecondition: new WritePrecondition.IfMatch("*", IsWildcard: true)
        );
        _currentEtagPreconditionChecker.ResultToReturn = CreatePreconditionCheckResult(
            request,
            isMatch: true,
            currentEtag: "\"current-etag\"",
            contentVersion: 45L
        );
        _writeFreshnessChecker.IsCurrentResult = false;

        var result = await _sut.ExecuteAsync(request);

        var update = result.Should().BeOfType<RelationalWriteExecutorResult.Update>().Which;
        update.Result.Should().BeOfType<UpdateResult.UpdateFailureWriteConflict>();
        update.Result.Should().NotBeOfType<UpdateResult.UpdateFailureETagMisMatch>();
        result.AttemptOutcome.Should().Be(RelationalWriteExecutorAttemptOutcome.StaleNoOpCompare.Instance);
        _currentEtagPreconditionChecker.CheckCallCount.Should().Be(1);
        _noProfilePersister.TryPersistCallCount.Should().Be(0);
        _writeFreshnessChecker.IsCurrentCallCount.Should().Be(1);
        _writeSessionFactory.Session.CommitCallCount.Should().Be(0);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
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
                        ComposedWriteResultEtag(77L)
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
                        ComposedWriteResultEtag(77L)
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
                        ComposedWriteResultEtag(77L)
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

    [TestCase(RelationalWriteOperationKind.Put)]
    [TestCase(RelationalWriteOperationKind.Post)]
    public async Task It_returns_immutable_identity_failure_before_proposed_relationship_authorization_for_existing_updates(
        RelationalWriteOperationKind operationKind
    )
    {
        var existingDocumentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));
        var request = CreateRequest(
            operationKind,
            selectedBody: JsonNode.Parse("""{"schoolId":255902,"name":"Lincoln High Updated"}""")!,
            targetContext: new RelationalWriteTargetContext.ExistingDocument(345L, existingDocumentUuid, 44L)
        );
        if (operationKind is RelationalWriteOperationKind.Post)
        {
            _targetLookupResolver.PostResults.Enqueue(
                new RelationalWriteTargetLookupResult.ExistingDocument(345L, existingDocumentUuid, 44L)
            );
        }

        var proposedAuthorization = CreateProposedSchoolIdRelationshipAuthorization(request);
        var relationshipFailure = CreateProposedRelationshipFailure(
            proposedAuthorization,
            new RelationshipAuthorizationAuth1SubjectFailure(
                0,
                0,
                RelationshipAuthorizationAuth1SubjectFailureKind.NoRelationship
            )
        );
        _noProfilePersister.ProposedAuthorizationExceptionToThrow =
            new RelationalWriteRelationshipAuthorizationNotAuthorizedException(relationshipFailure);
        _noProfileMergeSynthesizer.ResultToReturn = CreateMergeResult(
            request.WritePlan.TablePlansInDependencyOrder[0],
            currentSchoolId: 255901,
            mergedSchoolId: 255902,
            currentName: "Lincoln High",
            mergedName: "Lincoln High Updated"
        );

        var result = await _sut.ExecuteAsync(
            request with
            {
                StoredRelationshipAuthorization = CreateStoredSchoolIdRelationshipAuthorization(request),
                ProposedRelationshipAuthorization = proposedAuthorization,
            }
        );

        const string expectedFailureMessage =
            "Identifying values for the School resource cannot be changed. Delete and recreate the resource item instead.";
        switch (operationKind)
        {
            case RelationalWriteOperationKind.Put:
                result
                    .Should()
                    .BeEquivalentTo(
                        new RelationalWriteExecutorResult.Update(
                            new UpdateResult.UpdateFailureImmutableIdentity(expectedFailureMessage)
                        )
                    );
                break;

            case RelationalWriteOperationKind.Post:
                result
                    .Should()
                    .BeEquivalentTo(
                        new RelationalWriteExecutorResult.Upsert(
                            new UpsertResult.UpsertFailureImmutableIdentity(expectedFailureMessage)
                        )
                    );
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(operationKind), operationKind, null);
        }

        _writeSessionFactory.Session.CreateCommandExecutorCallCount.Should().Be(1);
        _writeSessionFactory.Session.RelationshipAuthorizationCommands.Should().ContainSingle();
        _currentStateLoader.LoadCallCount.Should().Be(1);
        _noProfileMergeSynthesizer.SynthesizeCallCount.Should().Be(1);
        _noProfilePersister.AuthorizeProposedRelationshipCallCount.Should().Be(0);
        _noProfilePersister.TryPersistCallCount.Should().Be(0);
        _writeSessionFactory.Session.CommitCallCount.Should().Be(0);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
    }

    [TestCase(RelationalWriteOperationKind.Put)]
    [TestCase(RelationalWriteOperationKind.Post)]
    public async Task It_returns_immutable_identity_failure_before_proposed_namespace_authorization_for_existing_updates(
        RelationalWriteOperationKind operationKind
    )
    {
        var payload = NamespaceAuthorizationAuth1FailurePayloadCodec.Encode(
            new NamespaceAuthorizationAuth1FailurePayload(
                0,
                NamespaceAuthorizationAuth1FailureKind.NamespaceMismatch
            )
        );
        UseNamespaceProviderFailureExtractor(payload);
        _writeSessionFactory.Session.RelationshipAuthorizationCommandExecutor =
            new ThrowingRelationalCommandExecutor(SqlDialect.Pgsql, new StubDbException("namespace AUTH1"));
        var existingDocumentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));
        var request = CreateRequest(
            operationKind,
            selectedBody: JsonNode.Parse("""{"schoolId":255902,"name":"Lincoln High Updated"}""")!,
            targetContext: new RelationalWriteTargetContext.ExistingDocument(345L, existingDocumentUuid, 44L)
        );
        if (operationKind is RelationalWriteOperationKind.Post)
        {
            _targetLookupResolver.PostResults.Enqueue(
                new RelationalWriteTargetLookupResult.ExistingDocument(345L, existingDocumentUuid, 44L)
            );
        }

        _noProfileMergeSynthesizer.ResultToReturn = CreateMergeResult(
            request.WritePlan.TablePlansInDependencyOrder[0],
            currentSchoolId: 255901,
            mergedSchoolId: 255902,
            currentName: "Lincoln High",
            mergedName: "Lincoln High Updated"
        );
        var rootTable = request.WritePlan.TablePlansInDependencyOrder[0].TableModel.Table;
        var namespaceAuth = new RelationalWriteNamespaceAuthorization(
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
                "namespacePrefixes"
            )
        );

        var result = await _sut.ExecuteAsync(request with { ProposedNamespaceAuthorization = namespaceAuth });

        const string expectedFailureMessage =
            "Identifying values for the School resource cannot be changed. Delete and recreate the resource item instead.";
        switch (operationKind)
        {
            case RelationalWriteOperationKind.Put:
                result
                    .Should()
                    .BeEquivalentTo(
                        new RelationalWriteExecutorResult.Update(
                            new UpdateResult.UpdateFailureImmutableIdentity(expectedFailureMessage)
                        )
                    );
                break;

            case RelationalWriteOperationKind.Post:
                result
                    .Should()
                    .BeEquivalentTo(
                        new RelationalWriteExecutorResult.Upsert(
                            new UpsertResult.UpsertFailureImmutableIdentity(expectedFailureMessage)
                        )
                    );
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(operationKind), operationKind, null);
        }

        _writeSessionFactory.Session.CreateCommandExecutorCallCount.Should().Be(0);
        _writeSessionFactory.Session.RelationshipAuthorizationCommands.Should().BeEmpty();
        _currentStateLoader.LoadCallCount.Should().Be(1);
        _noProfileMergeSynthesizer.SynthesizeCallCount.Should().Be(1);
        _noProfilePersister.TryPersistCallCount.Should().Be(0);
        _writeSessionFactory.Session.CommitCallCount.Should().Be(0);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_proceeds_past_identity_stability_fence_when_existing_document_identity_changes_and_updates_are_allowed()
    {
        var request = CreateRequest(
            RelationalWriteOperationKind.Put,
            allowIdentityUpdates: true,
            selectedBody: JsonNode.Parse("""{"name":"Lincoln High","schoolId":255902}""")!
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
                new RelationalWriteExecutorResult.Update(
                    new UpdateResult.UpdateSuccess(
                        new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb")),
                        ComposedWriteResultEtag(77L)
                    ),
                    RelationalWriteExecutorAttemptOutcome.AppliedWrite.Instance
                )
            );
        _currentStateLoader.LoadCallCount.Should().Be(1);
        _noProfileMergeSynthesizer.SynthesizeCallCount.Should().Be(1);
        _noProfilePersister.TryPersistCallCount.Should().Be(1);
        _writeSessionFactory.Session.CommitCallCount.Should().Be(1);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(0);
        _writeSessionFactory.Session.DisposeCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_maps_unique_violations_for_identity_changing_updates_to_update_identity_conflicts()
    {
        var request = CreateRequest(
            RelationalWriteOperationKind.Put,
            allowIdentityUpdates: true,
            selectedBody: JsonNode.Parse("""{"name":"Lincoln High","schoolId":255902}""")!
        );
        _noProfileMergeSynthesizer.ResultToReturn = CreateMergeResult(
            request.WritePlan.TablePlansInDependencyOrder[0],
            currentSchoolId: 255901,
            mergedSchoolId: 255902
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
                new RelationalWriteExecutorResult.Update(
                    new UpdateResult.UpdateFailureIdentityConflict(
                        new ResourceName("School"),
                        [new KeyValuePair<string, string>("schoolId", "255902")]
                    )
                )
            );
        _noProfilePersister.TryPersistCallCount.Should().Be(1);
        _writeExceptionClassifier.TryClassifyCallCount.Should().Be(1);
        _writeConstraintResolver.ResolveCallCount.Should().Be(1);
        _writeSessionFactory.Session.CommitCallCount.Should().Be(0);
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
    public async Task It_runs_profile_merge_for_root_attached_separate_table_create_new()
    {
        // Root-attached separate-table scopes (DbTableKind.RootExtension) proceed through
        // flattening and profile merge synthesis after profile contract validation.
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
            .Be(1, "root-attached separate-table plans must flatten for profile writes");
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
            .Be(1, "persister must receive the profile merge result");
        _writeSessionFactory.Session.CommitCallCount.Should().Be(1);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(0);

        var upsertResult = result.Should().BeOfType<RelationalWriteExecutorResult.Upsert>().Subject;
        upsertResult.Result.Should().BeOfType<UpsertResult.InsertSuccess>();
    }

    [Test]
    public async Task It_passes_profiled_create_new_for_collection_aligned_SeparateTableNonCollection()
    {
        // Collection-aligned separate-table scopes (DbTableKind.CollectionExtensionScope,
        // e.g. $.addresses[*]._ext.sample) proceed through profile merge synthesis.
        var writableBody = JsonNode.Parse("""{"schoolId":255901}""")!;
        // Slim helper-based plans keep the topology shape minimal; slice classification
        // only reads table kinds + JSON scopes, not row content.
        var rootPlan = ProfileRoutingTestPlans.RootTablePlan();
        var collectionScopePlan = ProfileRoutingTestPlans.CreateTablePlan(
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
        _writeFlattener.ResultToReturn = new FlattenedWriteSet(
            new RootWriteRowBuffer(rootPlan, [FlattenedWriteValue.UnresolvedRootDocumentId.Instance])
        );

        var result = await _sut.ExecuteAsync(request);

        _writeFlattener
            .FlattenCallCount.Should()
            .Be(1, "collection-aligned separate-table scopes must reach flattening");
        _profileMergeSynthesizer
            .SynthesizeCallCount.Should()
            .Be(1, "profile merge handles collection-aligned SeparateTableNonCollection scopes");
        _profileMergeSynthesizer.CapturedRequest.Should().NotBeNull();
        _profileMergeSynthesizer.CapturedRequest!.WritePlan.Should().BeSameAs(resourceWritePlan);
        _noProfilePersister.TryPersistCallCount.Should().Be(1);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(0);
        _writeSessionFactory.Session.CommitCallCount.Should().Be(1);

        var upsertResult = result.Should().BeOfType<RelationalWriteExecutorResult.Upsert>().Subject;
        upsertResult.Result.Should().BeOfType<UpsertResult.InsertSuccess>();
    }

    [Test]
    public async Task Given_Mixed_plan_when_request_only_exercises_root_attached_scope_runs_profile_merge()
    {
        // Mixed plan: Root + RootExtension + CollectionExtensionScope. The current
        // profiled request only exercises the root-attached $._ext.sample scope; the
        // collection-aligned scope is in the plan but unused for this request. The
        // merge synthesizer must leave unused non-root-extension tables untouched.
        var writableBody = JsonNode.Parse("""{"schoolId":255901}""")!;
        var rootPlan = CreateRootPlan();
        var extensionTableModel = AdapterFactoryTestFixtures.BuildRootExtensionTableModel();
        var extensionPlan = AdapterFactoryTestFixtures.BuildRootExtensionTableWritePlan(extensionTableModel);
        var collectionScopePlan = ProfileRoutingTestPlans.CreateTablePlan(
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
        // root row and leaves the unused collection-aligned table untouched.
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
            .Be(1, "persister must receive the profile merge result");
        _writeSessionFactory.Session.CommitCallCount.Should().Be(1);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(0);

        var upsertResult = result.Should().BeOfType<RelationalWriteExecutorResult.Upsert>().Subject;
        upsertResult.Result.Should().BeOfType<UpsertResult.InsertSuccess>();
    }

    [Test]
    public async Task Given_Executor_passes_when_request_exercises_collection_aligned_scope_in_mixed_plan()
    {
        // Same mixed plan shape (Root + RootExtension + CollectionExtensionScope), but
        // this time the request exercises the collection-aligned scope. Slice 5 CP3
        // allows that scope to flow through with the supported root-attached scope.
        var writableBody = JsonNode.Parse("""{"schoolId":255901}""")!;
        var rootPlan = CreateRootPlan();
        var extensionTableModel = AdapterFactoryTestFixtures.BuildRootExtensionTableModel();
        var extensionPlan = AdapterFactoryTestFixtures.BuildRootExtensionTableWritePlan(extensionTableModel);
        var collectionScopePlan = ProfileRoutingTestPlans.CreateTablePlan(
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

        var result = await _sut.ExecuteAsync(request);

        _writeFlattener.FlattenCallCount.Should().Be(1, "collection-aligned scopes must reach flattening");
        _profileMergeSynthesizer
            .SynthesizeCallCount.Should()
            .Be(1, "profile merge must run when the exercised scope is collection-aligned");
        _noProfilePersister.TryPersistCallCount.Should().Be(1);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(0);
        _writeSessionFactory.Session.CommitCallCount.Should().Be(1);

        var upsertResult = result.Should().BeOfType<RelationalWriteExecutorResult.Upsert>().Subject;
        upsertResult.Result.Should().BeOfType<UpsertResult.InsertSuccess>();
    }

    [Test]
    public async Task Given_Mixed_plan_when_collection_aligned_scope_is_only_hidden_on_request_runs_profile_merge()
    {
        // Same mixed plan shape (Root + RootExtension + CollectionExtensionScope). The
        // request exercises the visible root-attached $._ext.sample scope AND also carries
        // a Hidden request scope state for $.addresses[*]._ext.sample. Hidden request-side
        // scopes are preserve-only, so the executor continues into flattening and profile
        // merge for the visible root-attached scope.
        var writableBody = JsonNode.Parse("""{"schoolId":255901}""")!;
        var rootPlan = CreateRootPlan();
        var extensionTableModel = AdapterFactoryTestFixtures.BuildRootExtensionTableModel();
        var extensionPlan = AdapterFactoryTestFixtures.BuildRootExtensionTableWritePlan(extensionTableModel);
        var collectionScopePlan = ProfileRoutingTestPlans.CreateTablePlan(
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
        // root row and leaves the hidden request-side collection-aligned scope untouched.
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
            .Be(1, "hidden collection-aligned request scopes do not block flattening");
        _profileMergeSynthesizer
            .SynthesizeCallCount.Should()
            .Be(1, "profile merge must run when the collection-aligned scope is only hidden on request");
        _noProfilePersister
            .TryPersistCallCount.Should()
            .Be(1, "persister must receive the profile merge result");
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
    public async Task It_runs_profile_merge_for_root_attached_separate_table_put_existing_document()
    {
        // Profiled PUT requests with root-attached separate-table scopes reach the
        // synthesizer after stored-state projection.
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
            .Be(1, "flattener must run for root-attached separate-table profile writes");
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
        updateResult
            .Result.Should()
            .BeOfType<UpdateResult.UpdateSuccess>()
            .Which.ETag.Should()
            .Be(ComposedWriteResultEtag(77L, "test-write-profile"));
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
    public async Task It_shapes_planner_contract_mismatch_as_profile_contract_mismatch_result()
    {
        // The planner-driven profile merge synthesizer raises a fail-closed
        // ProfilePlannerContractMismatchException when Core hands the backend planner a
        // profile/scope combination that the compiled scope catalog cannot satisfy. The
        // executor must catch that narrowly-typed exception and shape it the same way as
        // the upfront ProfileWriteContractValidator failure path: an UnknownFailure whose
        // message starts with "Profile write contract mismatch:". The session must be
        // rolled back, no persistence may occur, and the failure must NOT propagate as a
        // generic InvalidOperationException through the executor's outer catch.
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

        _profileMergeSynthesizer.ExceptionToThrow = new ProfilePlannerContractMismatchException(
            jsonScope: "$.addresses[*]",
            invariantName: "reverse stored coverage",
            message: "VisibleStoredCollectionRow for scope '$.addresses[*]' with identity "
                + "$.addressId=\"A1\" has no matching current row. "
                + "Planner invariant violated: reverse stored coverage."
        );

        var request = CreateRequest(RelationalWriteOperationKind.Post, selectedBody: writableBody) with
        {
            WritePlan = resourceWritePlan,
            ProfileWriteContext = profileContext,
        };

        var result = await _sut.ExecuteAsync(request);

        _profileMergeSynthesizer.SynthesizeCallCount.Should().Be(1);
        _noProfilePersister.TryPersistCallCount.Should().Be(0);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
        _writeSessionFactory.Session.CommitCallCount.Should().Be(0);

        var upsertResult = result.Should().BeOfType<RelationalWriteExecutorResult.Upsert>().Subject;
        var failureMessage = upsertResult
            .Result.Should()
            .BeOfType<UpsertResult.UnknownFailure>()
            .Subject.FailureMessage;
        failureMessage.Should().StartWith("Profile write contract mismatch:");
        failureMessage.Should().Contain("$.addresses[*]");
        failureMessage.Should().Contain("reverse stored coverage");
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
        var persistedTarget = new RelationalWritePersistResult(
            910L,
            ((RelationalWriteTargetContext.CreateNew)request.TargetContext).DocumentUuid,
            77L
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
        _noProfilePersister.ResultToReturn = persistedTarget;

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
        upsertResult
            .Result.Should()
            .BeOfType<UpsertResult.InsertSuccess>()
            .Which.ETag.Should()
            .Be(ComposedWriteResultEtag(persistedTarget.ContentVersion, "test-write-profile"));
        result.AttemptOutcome.Should().Be(RelationalWriteExecutorAttemptOutcome.AppliedWrite.Instance);
    }

    [Test]
    public async Task It_short_circuits_unchanged_profiled_put_requests_as_guarded_no_ops()
    {
        var writableBody = JsonNode.Parse("""{"name":"Lincoln High"}""")!;
        var baseRequest = CreateRequest(RelationalWriteOperationKind.Put, selectedBody: writableBody);
        var profileContext = BuildVisiblePresentRootProfileWriteContext(writableBody, baseRequest.WritePlan);
        var request = baseRequest with { ProfileWriteContext = profileContext };

        var rootPlan = request.WritePlan.TablePlansInDependencyOrder[0];
        var sampleRow = new RelationalWriteMergedTableRow(
            values:
            [
                FlattenedWriteValue.UnresolvedRootDocumentId.Instance,
                new FlattenedWriteValue.Literal(255901),
                new FlattenedWriteValue.Literal("Lincoln High"),
            ],
            comparableValues:
            [
                FlattenedWriteValue.UnresolvedRootDocumentId.Instance,
                new FlattenedWriteValue.Literal(255901),
                new FlattenedWriteValue.Literal("Lincoln High"),
            ]
        );
        _profileMergeSynthesizer.ResultToReturn = new RelationalWriteMergeResult(
            [new RelationalWriteMergedTableState(rootPlan, [sampleRow], [sampleRow])],
            supportsGuardedNoOp: true
        );

        var result = await _sut.ExecuteAsync(request);

        result
            .Should()
            .BeOfType<RelationalWriteExecutorResult.Update>()
            .Which.Result.Should()
            .BeOfType<UpdateResult.UpdateSuccess>()
            .Which.ETag.Should()
            .Be(ComposedWriteResultEtag(44L, "test-write-profile"));
        result.AttemptOutcome.Should().Be(RelationalWriteExecutorAttemptOutcome.GuardedNoOp.Instance);
        _profileMergeSynthesizer.SynthesizeCallCount.Should().Be(1);
        _noProfileMergeSynthesizer.SynthesizeCallCount.Should().Be(0);
        _noProfilePersister.TryPersistCallCount.Should().Be(0);
        _writeFreshnessChecker.IsCurrentCallCount.Should().Be(1);
        _writeSessionFactory.Session.CommitCallCount.Should().Be(1);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(0);
    }

    [Test]
    public async Task It_short_circuits_unchanged_profiled_post_as_update_requests_as_guarded_no_ops()
    {
        var writableBody = JsonNode.Parse("""{"name":"Lincoln High"}""")!;
        var existingTarget = new RelationalWriteTargetContext.ExistingDocument(
            345L,
            new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb")),
            44L
        );
        var baseRequest = CreateRequest(
            RelationalWriteOperationKind.Post,
            targetContext: existingTarget,
            selectedBody: writableBody
        );
        var profileContext = BuildVisiblePresentRootProfileWriteContext(writableBody, baseRequest.WritePlan);
        var request = baseRequest with { ProfileWriteContext = profileContext };

        var rootPlan = request.WritePlan.TablePlansInDependencyOrder[0];
        var sampleRow = new RelationalWriteMergedTableRow(
            values:
            [
                FlattenedWriteValue.UnresolvedRootDocumentId.Instance,
                new FlattenedWriteValue.Literal(255901),
                new FlattenedWriteValue.Literal("Lincoln High"),
            ],
            comparableValues:
            [
                FlattenedWriteValue.UnresolvedRootDocumentId.Instance,
                new FlattenedWriteValue.Literal(255901),
                new FlattenedWriteValue.Literal("Lincoln High"),
            ]
        );
        _profileMergeSynthesizer.ResultToReturn = new RelationalWriteMergeResult(
            [new RelationalWriteMergedTableState(rootPlan, [sampleRow], [sampleRow])],
            supportsGuardedNoOp: true
        );

        var result = await _sut.ExecuteAsync(request);

        result
            .Should()
            .BeOfType<RelationalWriteExecutorResult.Upsert>()
            .Which.Result.Should()
            .BeOfType<UpsertResult.UpdateSuccess>()
            .Which.ETag.Should()
            .Be(ComposedWriteResultEtag(44L, "test-write-profile"));
        result.AttemptOutcome.Should().Be(RelationalWriteExecutorAttemptOutcome.GuardedNoOp.Instance);
        _profileMergeSynthesizer.SynthesizeCallCount.Should().Be(1);
        _noProfilePersister.TryPersistCallCount.Should().Be(0);
        _writeFreshnessChecker.IsCurrentCallCount.Should().Be(1);
        _writeSessionFactory.Session.CommitCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_returns_stale_no_op_write_conflict_for_profiled_put_when_freshness_is_lost()
    {
        var writableBody = JsonNode.Parse("""{"name":"Lincoln High"}""")!;
        var baseRequest = CreateRequest(RelationalWriteOperationKind.Put, selectedBody: writableBody);
        var profileContext = BuildVisiblePresentRootProfileWriteContext(writableBody, baseRequest.WritePlan);
        var request = baseRequest with { ProfileWriteContext = profileContext };

        var rootPlan = request.WritePlan.TablePlansInDependencyOrder[0];
        var sampleRow = new RelationalWriteMergedTableRow(
            values:
            [
                FlattenedWriteValue.UnresolvedRootDocumentId.Instance,
                new FlattenedWriteValue.Literal(255901),
                new FlattenedWriteValue.Literal("Lincoln High"),
            ],
            comparableValues:
            [
                FlattenedWriteValue.UnresolvedRootDocumentId.Instance,
                new FlattenedWriteValue.Literal(255901),
                new FlattenedWriteValue.Literal("Lincoln High"),
            ]
        );
        _profileMergeSynthesizer.ResultToReturn = new RelationalWriteMergeResult(
            [new RelationalWriteMergedTableState(rootPlan, [sampleRow], [sampleRow])],
            supportsGuardedNoOp: true
        );
        _writeFreshnessChecker.IsCurrentEvaluator = static _ => false;

        var result = await _sut.ExecuteAsync(request);

        result
            .Should()
            .BeOfType<RelationalWriteExecutorResult.Update>()
            .Which.Result.Should()
            .BeOfType<UpdateResult.UpdateFailureWriteConflict>();
        result.AttemptOutcome.Should().Be(RelationalWriteExecutorAttemptOutcome.StaleNoOpCompare.Instance);
        _noProfilePersister.TryPersistCallCount.Should().Be(0);
        _writeFreshnessChecker.IsCurrentCallCount.Should().Be(1);
        _writeSessionFactory.Session.CommitCallCount.Should().Be(0);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_returns_stale_no_op_write_conflict_for_profiled_post_as_update_when_freshness_is_lost()
    {
        var writableBody = JsonNode.Parse("""{"name":"Lincoln High"}""")!;
        var existingTarget = new RelationalWriteTargetContext.ExistingDocument(
            345L,
            new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb")),
            44L
        );
        var baseRequest = CreateRequest(
            RelationalWriteOperationKind.Post,
            targetContext: existingTarget,
            selectedBody: writableBody
        );
        var profileContext = BuildVisiblePresentRootProfileWriteContext(writableBody, baseRequest.WritePlan);
        var request = baseRequest with { ProfileWriteContext = profileContext };

        var rootPlan = request.WritePlan.TablePlansInDependencyOrder[0];
        var sampleRow = new RelationalWriteMergedTableRow(
            values:
            [
                FlattenedWriteValue.UnresolvedRootDocumentId.Instance,
                new FlattenedWriteValue.Literal(255901),
                new FlattenedWriteValue.Literal("Lincoln High"),
            ],
            comparableValues:
            [
                FlattenedWriteValue.UnresolvedRootDocumentId.Instance,
                new FlattenedWriteValue.Literal(255901),
                new FlattenedWriteValue.Literal("Lincoln High"),
            ]
        );
        _profileMergeSynthesizer.ResultToReturn = new RelationalWriteMergeResult(
            [new RelationalWriteMergedTableState(rootPlan, [sampleRow], [sampleRow])],
            supportsGuardedNoOp: true
        );
        _writeFreshnessChecker.IsCurrentEvaluator = static _ => false;

        var result = await _sut.ExecuteAsync(request);

        result
            .Should()
            .BeOfType<RelationalWriteExecutorResult.Upsert>()
            .Which.Result.Should()
            .BeOfType<UpsertResult.UpsertFailureWriteConflict>();
        result.AttemptOutcome.Should().Be(RelationalWriteExecutorAttemptOutcome.StaleNoOpCompare.Instance);
        _noProfilePersister.TryPersistCallCount.Should().Be(0);
        _writeFreshnessChecker.IsCurrentCallCount.Should().Be(1);
        _writeSessionFactory.Session.CommitCallCount.Should().Be(0);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_returns_if_match_failure_for_profiled_stale_post_as_update_no_op_compares()
    {
        var writableBody = JsonNode.Parse("""{"name":"Lincoln High"}""")!;
        var existingTarget = new RelationalWriteTargetContext.ExistingDocument(
            345L,
            new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb")),
            44L
        );
        var baseRequest = CreateRequest(
            RelationalWriteOperationKind.Post,
            targetContext: existingTarget,
            selectedBody: writableBody,
            writePrecondition: new WritePrecondition.IfMatch("\"current-etag\"")
        );
        var profileContext = BuildVisiblePresentRootProfileWriteContext(writableBody, baseRequest.WritePlan);
        var request = baseRequest with { ProfileWriteContext = profileContext };
        _targetLookupResolver.PostResults.Enqueue(
            new RelationalWriteTargetLookupResult.ExistingDocument(
                existingTarget.DocumentId,
                existingTarget.DocumentUuid,
                existingTarget.ObservedContentVersion
            )
        );
        _currentEtagPreconditionChecker.ResultToReturn = CreatePreconditionCheckResult(
            request,
            isMatch: true,
            currentEtag: "\"current-etag\"",
            contentVersion: 45L
        );

        var rootPlan = request.WritePlan.TablePlansInDependencyOrder[0];
        var sampleRow = new RelationalWriteMergedTableRow(
            values:
            [
                FlattenedWriteValue.UnresolvedRootDocumentId.Instance,
                new FlattenedWriteValue.Literal(255901),
                new FlattenedWriteValue.Literal("Lincoln High"),
            ],
            comparableValues:
            [
                FlattenedWriteValue.UnresolvedRootDocumentId.Instance,
                new FlattenedWriteValue.Literal(255901),
                new FlattenedWriteValue.Literal("Lincoln High"),
            ]
        );
        _profileMergeSynthesizer.ResultToReturn = new RelationalWriteMergeResult(
            [new RelationalWriteMergedTableState(rootPlan, [sampleRow], [sampleRow])],
            supportsGuardedNoOp: true
        );
        _writeFreshnessChecker.IsCurrentEvaluator = static _ => false;

        var result = await _sut.ExecuteAsync(request);

        result
            .Should()
            .BeEquivalentTo(
                new RelationalWriteExecutorResult.Upsert(
                    new UpsertResult.UpsertFailureETagMisMatch(),
                    RelationalWriteExecutorAttemptOutcome.StaleNoOpCompare.Instance
                )
            );
        result.AttemptOutcome.Should().Be(RelationalWriteExecutorAttemptOutcome.StaleNoOpCompare.Instance);
        _currentEtagPreconditionChecker.CheckCallCount.Should().Be(1);
        _noProfilePersister.TryPersistCallCount.Should().Be(0);
        _writeFreshnessChecker.IsCurrentCallCount.Should().Be(1);
        _writeSessionFactory.Session.CommitCallCount.Should().Be(0);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_falls_through_to_persister_for_profiled_put_when_merge_is_not_a_no_op_candidate()
    {
        var writableBody = JsonNode.Parse("""{"name":"Lincoln High"}""")!;
        var baseRequest = CreateRequest(RelationalWriteOperationKind.Put, selectedBody: writableBody);
        var profileContext = BuildVisiblePresentRootProfileWriteContext(writableBody, baseRequest.WritePlan);
        var request = baseRequest with { ProfileWriteContext = profileContext };

        var rootPlan = request.WritePlan.TablePlansInDependencyOrder[0];
        var currentRow = new RelationalWriteMergedTableRow(
            values:
            [
                FlattenedWriteValue.UnresolvedRootDocumentId.Instance,
                new FlattenedWriteValue.Literal(255901),
                new FlattenedWriteValue.Literal("Old"),
            ],
            comparableValues:
            [
                FlattenedWriteValue.UnresolvedRootDocumentId.Instance,
                new FlattenedWriteValue.Literal(255901),
                new FlattenedWriteValue.Literal("Old"),
            ]
        );
        var mergedRow = new RelationalWriteMergedTableRow(
            values:
            [
                FlattenedWriteValue.UnresolvedRootDocumentId.Instance,
                new FlattenedWriteValue.Literal(255901),
                new FlattenedWriteValue.Literal("New"),
            ],
            comparableValues:
            [
                FlattenedWriteValue.UnresolvedRootDocumentId.Instance,
                new FlattenedWriteValue.Literal(255901),
                new FlattenedWriteValue.Literal("New"),
            ]
        );
        _profileMergeSynthesizer.ResultToReturn = new RelationalWriteMergeResult(
            [new RelationalWriteMergedTableState(rootPlan, [currentRow], [mergedRow])],
            supportsGuardedNoOp: true
        );

        var result = await _sut.ExecuteAsync(request);

        result.AttemptOutcome.Should().Be(RelationalWriteExecutorAttemptOutcome.AppliedWrite.Instance);
        _noProfilePersister.TryPersistCallCount.Should().Be(1);
        _writeFreshnessChecker.IsCurrentCallCount.Should().Be(0);
    }

    [Test]
    public async Task It_attaches_proposed_relationship_authorization_values_from_finalized_no_profile_root_row()
    {
        var request = CreateRequest(
            RelationalWriteOperationKind.Post,
            selectedBody: JsonNode.Parse("""{"schoolId":111111,"name":"Raw"}""")!
        );
        var rootPlan = request.WritePlan.TablePlansInDependencyOrder[0];
        _writeFlattener.ResultToReturn = new FlattenedWriteSet(
            new RootWriteRowBuffer(
                rootPlan,
                [
                    FlattenedWriteValue.UnresolvedRootDocumentId.Instance,
                    new FlattenedWriteValue.Literal(222222),
                    new FlattenedWriteValue.Literal("From row buffer"),
                ]
            )
        );

        var result = await _sut.ExecuteAsync(
            request with
            {
                ProposedRelationshipAuthorization = CreateProposedSchoolIdRelationshipAuthorization(request),
            }
        );

        result
            .Should()
            .BeOfType<RelationalWriteExecutorResult.Upsert>()
            .Which.Result.Should()
            .BeOfType<UpsertResult.InsertSuccess>();
        _noProfilePersister.AuthorizeProposedRelationshipCallCount.Should().Be(0);
        _noProfilePersister.TryPersistCallCount.Should().Be(1);
        _noProfilePersister.CapturedMergeResult.Should().NotBeNull();

        var runtimeCheck = _noProfilePersister
            .CapturedMergeResult!
            .ProposedRelationshipAuthorizationRuntimeCheck;
        runtimeCheck.Should().NotBeNull();
        runtimeCheck!.Strategies.Should().ContainSingle();
        runtimeCheck.Strategies[0].StrategyOrdinal.Should().Be(0);
        runtimeCheck.Strategies[0].Subjects.Should().ContainSingle();
        runtimeCheck.Strategies[0].Subjects[0].SubjectOrdinal.Should().Be(0);
        GetSubjectRuntimeValue(runtimeCheck.Strategies[0].Subjects[0]).Should().Be(222222);
        runtimeCheck.Strategies[0].Subjects[0].Binding.BindingIndex.Should().Be(1);
        runtimeCheck
            .ClaimEducationOrganizationIdParameterization.ClaimEducationOrganizationIds.Should()
            .Equal(1234L);
    }

    [Test]
    public async Task It_reads_proposed_relationship_authorization_values_from_profile_merged_root_row()
    {
        var rawBody = JsonNode.Parse("""{"schoolId":111111,"name":"Raw"}""")!;
        var writableBody = JsonNode.Parse("""{"schoolId":333333,"name":"Writable"}""")!;
        var baseRequest = CreateRequest(RelationalWriteOperationKind.Post, selectedBody: rawBody);
        var profileContext = BuildVisiblePresentRootProfileWriteContext(writableBody, baseRequest.WritePlan);
        var rootPlan = baseRequest.WritePlan.TablePlansInDependencyOrder[0];
        _writeFlattener.ResultToReturn = new FlattenedWriteSet(
            new RootWriteRowBuffer(
                rootPlan,
                [
                    FlattenedWriteValue.UnresolvedRootDocumentId.Instance,
                    new FlattenedWriteValue.Literal(333333),
                    new FlattenedWriteValue.Literal("Writable"),
                ]
            )
        );
        var mergedRootRow = new RelationalWriteMergedTableRow(
            values:
            [
                FlattenedWriteValue.UnresolvedRootDocumentId.Instance,
                new FlattenedWriteValue.Literal(444444),
                new FlattenedWriteValue.Literal("Merged"),
            ],
            comparableValues:
            [
                FlattenedWriteValue.UnresolvedRootDocumentId.Instance,
                new FlattenedWriteValue.Literal(444444),
                new FlattenedWriteValue.Literal("Merged"),
            ]
        );
        _profileMergeSynthesizer.ResultToReturn = new RelationalWriteMergeResult(
            [new RelationalWriteMergedTableState(rootPlan, [], [mergedRootRow])],
            supportsGuardedNoOp: false
        );

        var result = await _sut.ExecuteAsync(
            baseRequest with
            {
                ProfileWriteContext = profileContext,
                ProposedRelationshipAuthorization = CreateProposedSchoolIdRelationshipAuthorization(
                    baseRequest
                ),
            }
        );

        result
            .Should()
            .BeOfType<RelationalWriteExecutorResult.Upsert>()
            .Which.Result.Should()
            .BeOfType<UpsertResult.InsertSuccess>();
        _writeFlattener.CapturedInput.Should().NotBeNull();
        _writeFlattener.CapturedInput!.SelectedBody.Should().BeSameAs(writableBody);
        _profileMergeSynthesizer.CapturedRequest.Should().NotBeNull();
        _profileMergeSynthesizer
            .CapturedRequest!.ProfileRequest.WritableRequestBody.Should()
            .BeSameAs(writableBody);

        var runtimeCheck = _noProfilePersister
            .CapturedMergeResult!
            .ProposedRelationshipAuthorizationRuntimeCheck;
        runtimeCheck.Should().NotBeNull();
        GetSubjectRuntimeValue(runtimeCheck!.Strategies[0].Subjects[0]).Should().Be(444444);
    }

    [TestCase(RelationalWriteOperationKind.Put)]
    [TestCase(RelationalWriteOperationKind.Post)]
    public async Task It_reads_proposed_relationship_authorization_values_from_profile_merged_existing_update_root_row(
        RelationalWriteOperationKind operationKind
    )
    {
        var rawBody = JsonNode.Parse("""{"schoolId":111111,"name":"Raw"}""")!;
        var writableBody = JsonNode.Parse("""{"schoolId":333333,"name":"Writable"}""")!;
        var existingDocumentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));
        var baseRequest = CreateRequest(
            operationKind,
            selectedBody: rawBody,
            targetContext: new RelationalWriteTargetContext.ExistingDocument(345L, existingDocumentUuid, 44L)
        );
        var profileContext = BuildVisiblePresentRootProfileWriteContext(writableBody, baseRequest.WritePlan);
        var rootPlan = baseRequest.WritePlan.TablePlansInDependencyOrder[0];
        _writeFlattener.ResultToReturn = new FlattenedWriteSet(
            new RootWriteRowBuffer(
                rootPlan,
                [
                    operationKind == RelationalWriteOperationKind.Put
                        ? new FlattenedWriteValue.Literal(345L)
                        : FlattenedWriteValue.UnresolvedRootDocumentId.Instance,
                    new FlattenedWriteValue.Literal(333333),
                    new FlattenedWriteValue.Literal("Writable"),
                ]
            )
        );
        var mergedRootRow = new RelationalWriteMergedTableRow(
            values:
            [
                new FlattenedWriteValue.Literal(345L),
                new FlattenedWriteValue.Literal(444444),
                new FlattenedWriteValue.Literal("Merged"),
            ],
            comparableValues:
            [
                new FlattenedWriteValue.Literal(345L),
                new FlattenedWriteValue.Literal(444444),
                new FlattenedWriteValue.Literal("Merged"),
            ]
        );
        _profileMergeSynthesizer.ResultToReturn = new RelationalWriteMergeResult(
            [
                new RelationalWriteMergedTableState(
                    rootPlan,
                    [CreateRootTableRow(345L, 444444, "Stored Hidden")],
                    [mergedRootRow]
                ),
            ],
            supportsGuardedNoOp: false
        );

        var result = await _sut.ExecuteAsync(
            baseRequest with
            {
                ProfileWriteContext = profileContext,
                ProposedRelationshipAuthorization = CreateProposedSchoolIdRelationshipAuthorization(
                    baseRequest
                ),
            }
        );

        switch (operationKind)
        {
            case RelationalWriteOperationKind.Put:
                result
                    .Should()
                    .BeOfType<RelationalWriteExecutorResult.Update>()
                    .Which.Result.Should()
                    .BeOfType<UpdateResult.UpdateSuccess>();
                break;

            case RelationalWriteOperationKind.Post:
                result
                    .Should()
                    .BeOfType<RelationalWriteExecutorResult.Upsert>()
                    .Which.Result.Should()
                    .BeOfType<UpsertResult.UpdateSuccess>();
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(operationKind), operationKind, null);
        }

        _writeFlattener.CapturedInput.Should().NotBeNull();
        _writeFlattener.CapturedInput!.SelectedBody.Should().BeSameAs(writableBody);
        _profileMergeSynthesizer.CapturedRequest.Should().NotBeNull();
        _profileMergeSynthesizer
            .CapturedRequest!.ProfileRequest.WritableRequestBody.Should()
            .BeSameAs(writableBody);
        _noProfilePersister.AuthorizeProposedRelationshipCallCount.Should().Be(1);

        var runtimeCheck = _noProfilePersister
            .CapturedMergeResult!
            .ProposedRelationshipAuthorizationRuntimeCheck;
        runtimeCheck.Should().NotBeNull();
        GetSubjectRuntimeValue(runtimeCheck!.Strategies[0].Subjects[0]).Should().Be(444444);
    }

    [Test]
    public async Task It_returns_relationship_authorization_failure_for_missing_proposed_root_values_from_authorization_sql()
    {
        var request = CreateRequest(RelationalWriteOperationKind.Post);
        var authorization = CreateProposedSchoolIdRelationshipAuthorization(request);
        var relationshipFailure = CreateProposedRelationshipFailure(
            authorization,
            new RelationshipAuthorizationAuth1SubjectFailure(
                0,
                0,
                RelationshipAuthorizationAuth1SubjectFailureKind.ProposedValueMissing
            )
        );
        _noProfilePersister.ExceptionToThrow =
            new RelationalWriteRelationshipAuthorizationNotAuthorizedException(relationshipFailure);
        var rootPlan = request.WritePlan.TablePlansInDependencyOrder[0];
        _writeFlattener.ResultToReturn = new FlattenedWriteSet(
            new RootWriteRowBuffer(
                rootPlan,
                [
                    FlattenedWriteValue.UnresolvedRootDocumentId.Instance,
                    new FlattenedWriteValue.Literal(null),
                    new FlattenedWriteValue.Literal("Lincoln High"),
                ]
            )
        );

        var result = await _sut.ExecuteAsync(
            request with
            {
                ProposedRelationshipAuthorization = authorization,
            }
        );

        var upsertResult = result.Should().BeOfType<RelationalWriteExecutorResult.Upsert>().Subject;
        var notAuthorized = upsertResult
            .Result.Should()
            .BeOfType<UpsertResult.UpsertFailureRelationshipNotAuthorized>()
            .Subject;

        notAuthorized
            .RelationshipFailure.ValueSource.Should()
            .Be(RelationshipAuthorizationFailureValueSource.Proposed);
        notAuthorized
            .RelationshipFailure.ClaimEducationOrganizationIds.Select(static id => id.Value)
            .Should()
            .Equal(1234L);
        notAuthorized.RelationshipFailure.FailedStrategies.Should().ContainSingle();
        notAuthorized.RelationshipFailure.FailedStrategies[0].FailedSubjects.Should().ContainSingle();
        notAuthorized
            .RelationshipFailure.FailedStrategies[0]
            .FailedSubjects[0]
            .FailureKind.Should()
            .Be(RelationshipAuthorizationSubjectFailureKind.ProposedValueMissing);
        notAuthorized
            .RelationshipFailure.FailedStrategies[0]
            .FailedSubjects[0]
            .RootBinding.ColumnName.Should()
            .Be("SchoolId");
        notAuthorized
            .RelationshipFailure.FailedStrategies[0]
            .FailedSubjects[0]
            .SecurableElements.Should()
            .ContainSingle()
            .Which.ReadableName.Should()
            .Be("SchoolId");
        _noProfileMergeSynthesizer.SynthesizeCallCount.Should().Be(1);
        _noProfilePersister.CapturedMergeResult.Should().NotBeNull();
        GetSubjectRuntimeValue(
                _noProfilePersister
                    .CapturedMergeResult!
                    .ProposedRelationshipAuthorizationRuntimeCheck!
                    .Strategies[0]
                    .Subjects[0]
            )
            .Should()
            .BeNull();
        _noProfilePersister.AuthorizeProposedRelationshipCallCount.Should().Be(0);
        _noProfilePersister.TryPersistCallCount.Should().Be(1);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_returns_security_configuration_failure_for_invalid_proposed_relationship_auth1_payloads()
    {
        var request = CreateRequest(RelationalWriteOperationKind.Post);
        _noProfilePersister.ExceptionToThrow =
            new RelationalWriteInvalidRelationshipAuthorizationFailureException(
                RelationshipAuthorizationSecurityConfigurationFailureMessages.InvalidFailurePayloadSecurityConfigurationError
            );

        var result = await _sut.ExecuteAsync(
            request with
            {
                ProposedRelationshipAuthorization = CreateProposedSchoolIdRelationshipAuthorization(request),
            }
        );

        var upsertResult = result.Should().BeOfType<RelationalWriteExecutorResult.Upsert>().Subject;
        var securityConfigurationFailure = upsertResult
            .Result.Should()
            .BeOfType<UpsertResult.UpsertFailureSecurityConfiguration>()
            .Subject;
        securityConfigurationFailure
            .Errors.Should()
            .Equal(
                RelationshipAuthorizationSecurityConfigurationFailureMessages.InvalidFailurePayloadSecurityConfigurationError
            );
        _noProfilePersister.TryPersistCallCount.Should().Be(1);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
    }

    [TestCase(RelationalWriteOperationKind.Post)]
    [TestCase(RelationalWriteOperationKind.Put)]
    public async Task It_returns_security_configuration_failure_for_invalid_proposed_relationship_authorization_plans(
        RelationalWriteOperationKind operationKind
    )
    {
        var request = CreateRequest(operationKind);
        var authorization = CreateProposedSchoolIdRelationshipAuthorization(request) with
        {
            ClaimEducationOrganizationIdParameterization = null,
        };

        var result = await _sut.ExecuteAsync(
            request with
            {
                ProposedRelationshipAuthorization = authorization,
            }
        );

        const string expectedFailureMessage =
            "Proposed relationship authorization produced executable checks without claim EducationOrganizationId parameterization.";

        switch (operationKind)
        {
            case RelationalWriteOperationKind.Post:
                result
                    .Should()
                    .BeOfType<RelationalWriteExecutorResult.Upsert>()
                    .Which.Result.Should()
                    .BeOfType<UpsertResult.UpsertFailureSecurityConfiguration>()
                    .Which.Errors.Should()
                    .Equal(expectedFailureMessage);
                break;

            case RelationalWriteOperationKind.Put:
                result
                    .Should()
                    .BeOfType<RelationalWriteExecutorResult.Update>()
                    .Which.Result.Should()
                    .BeOfType<UpdateResult.UpdateFailureSecurityConfiguration>()
                    .Which.Errors.Should()
                    .Equal(expectedFailureMessage);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(operationKind), operationKind, null);
        }

        _noProfilePersister.AuthorizeProposedRelationshipCallCount.Should().Be(0);
        _noProfilePersister.TryPersistCallCount.Should().Be(0);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_returns_mixed_missing_and_no_relationship_failure_metadata_from_authorization_sql()
    {
        var request = CreateRequest(RelationalWriteOperationKind.Post);
        var authorization = CreateTwoSingleSubjectStrategyRelationshipAuthorization(request);
        var relationshipFailure = CreateProposedRelationshipFailure(
            authorization,
            new RelationshipAuthorizationAuth1SubjectFailure(
                0,
                0,
                RelationshipAuthorizationAuth1SubjectFailureKind.ProposedValueMissing
            ),
            new RelationshipAuthorizationAuth1SubjectFailure(
                1,
                0,
                RelationshipAuthorizationAuth1SubjectFailureKind.NoRelationship
            )
        );
        _noProfilePersister.ExceptionToThrow =
            new RelationalWriteRelationshipAuthorizationNotAuthorizedException(relationshipFailure);
        var rootPlan = request.WritePlan.TablePlansInDependencyOrder[0];
        _writeFlattener.ResultToReturn = new FlattenedWriteSet(
            new RootWriteRowBuffer(
                rootPlan,
                [
                    FlattenedWriteValue.UnresolvedRootDocumentId.Instance,
                    new FlattenedWriteValue.Literal(null),
                    new FlattenedWriteValue.Literal("Lincoln High"),
                ]
            )
        );

        var result = await _sut.ExecuteAsync(
            request with
            {
                ProposedRelationshipAuthorization = authorization,
            }
        );

        var upsertResult = result.Should().BeOfType<RelationalWriteExecutorResult.Upsert>().Subject;
        var notAuthorized = upsertResult
            .Result.Should()
            .BeOfType<UpsertResult.UpsertFailureRelationshipNotAuthorized>()
            .Subject;

        notAuthorized.RelationshipFailure.FailedStrategies.Should().HaveCount(2);
        notAuthorized
            .RelationshipFailure.FailedStrategies.Select(static strategy => strategy.ConfiguredStrategyIndex)
            .Should()
            .Equal(0, 1);
        notAuthorized
            .RelationshipFailure.FailedStrategies.SelectMany(static strategy => strategy.FailedSubjects)
            .Select(static subject => subject.FailureKind)
            .Should()
            .Equal(
                RelationshipAuthorizationSubjectFailureKind.ProposedValueMissing,
                RelationshipAuthorizationSubjectFailureKind.NoRelationship
            );
        notAuthorized
            .RelationshipFailure.FailedStrategies.SelectMany(static strategy => strategy.FailedSubjects)
            .Select(static subject => subject.RootBinding.ColumnName)
            .Should()
            .Equal("SchoolId", "Name");
        _noProfileMergeSynthesizer.SynthesizeCallCount.Should().Be(1);
        _noProfilePersister.CapturedMergeResult.Should().NotBeNull();
        _noProfilePersister
            .CapturedMergeResult!.ProposedRelationshipAuthorizationRuntimeCheck!.Strategies.SelectMany(
                static strategy => strategy.Subjects
            )
            .Select(GetSubjectRuntimeValue)
            .Should()
            .Equal(new object?[] { null, "Lincoln High" });
        _noProfilePersister.AuthorizeProposedRelationshipCallCount.Should().Be(0);
        _noProfilePersister.TryPersistCallCount.Should().Be(1);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_returns_relationship_authorization_failure_from_create_persistence_without_committed_readback()
    {
        var request = CreateRequest(RelationalWriteOperationKind.Post);
        var relationshipFailure = CreateProposedSchoolIdRelationshipFailure(request);
        _noProfilePersister.ExceptionToThrow =
            new RelationalWriteRelationshipAuthorizationNotAuthorizedException(relationshipFailure);

        var result = await _sut.ExecuteAsync(
            request with
            {
                ProposedRelationshipAuthorization = CreateProposedSchoolIdRelationshipAuthorization(request),
            }
        );

        var upsertResult = result.Should().BeOfType<RelationalWriteExecutorResult.Upsert>().Subject;
        var notAuthorized = upsertResult
            .Result.Should()
            .BeOfType<UpsertResult.UpsertFailureRelationshipNotAuthorized>()
            .Subject;
        notAuthorized.RelationshipFailure.Should().BeSameAs(relationshipFailure);
        _noProfilePersister.TryPersistCallCount.Should().Be(1);
        _writeSessionFactory.Session.CommitCallCount.Should().Be(0);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_returns_relationship_authorization_failure_for_create_new_if_match_before_etag_mismatch()
    {
        var request = CreateRequest(
            RelationalWriteOperationKind.Post,
            writePrecondition: new WritePrecondition.IfMatch("\"stale-etag\"")
        );
        var relationshipFailure = CreateProposedSchoolIdRelationshipFailure(request);
        _noProfilePersister.ProposedAuthorizationExceptionToThrow =
            new RelationalWriteRelationshipAuthorizationNotAuthorizedException(relationshipFailure);

        var result = await _sut.ExecuteAsync(
            request with
            {
                ProposedRelationshipAuthorization = CreateProposedSchoolIdRelationshipAuthorization(request),
            }
        );

        var upsertResult = result.Should().BeOfType<RelationalWriteExecutorResult.Upsert>().Subject;
        upsertResult
            .Result.Should()
            .BeOfType<UpsertResult.UpsertFailureRelationshipNotAuthorized>()
            .Which.RelationshipFailure.Should()
            .BeSameAs(relationshipFailure);
        _targetLookupResolver.ResolveForPostCallCount.Should().Be(1);
        _currentEtagPreconditionChecker.CheckCallCount.Should().Be(0);
        _writeFlattener.FlattenCallCount.Should().Be(1);
        _noProfileMergeSynthesizer.SynthesizeCallCount.Should().Be(1);
        _noProfilePersister.AuthorizeProposedRelationshipCallCount.Should().Be(1);
        _noProfilePersister.TryPersistCallCount.Should().Be(0);
        _writeSessionFactory.Session.CommitCallCount.Should().Be(0);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_returns_if_match_failure_for_create_new_after_successful_proposed_relationship_authorization()
    {
        var request = CreateRequest(
            RelationalWriteOperationKind.Post,
            writePrecondition: new WritePrecondition.IfMatch("\"stale-etag\"")
        );

        var result = await _sut.ExecuteAsync(
            request with
            {
                ProposedRelationshipAuthorization = CreateProposedSchoolIdRelationshipAuthorization(request),
            }
        );

        // The deferred If-Match check runs against a CreateNew target, which has no current
        // representation, so the reason is TargetDoesNotExist rather than a Concurrency mismatch.
        result
            .Should()
            .BeOfType<RelationalWriteExecutorResult.Upsert>()
            .Which.Result.Should()
            .BeOfType<UpsertResult.UpsertFailureETagMisMatch>()
            .Which.Reason.Should()
            .Be(ETagPreconditionFailureReason.TargetDoesNotExist);
        _targetLookupResolver.ResolveForPostCallCount.Should().Be(1);
        _currentEtagPreconditionChecker.CheckCallCount.Should().Be(0);
        _writeFlattener.FlattenCallCount.Should().Be(1);
        _noProfileMergeSynthesizer.SynthesizeCallCount.Should().Be(1);
        _noProfilePersister.AuthorizeProposedRelationshipCallCount.Should().Be(1);
        _noProfilePersister.TryPersistCallCount.Should().Be(0);
        _writeSessionFactory.Session.CommitCallCount.Should().Be(0);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_returns_relationship_authorization_failure_for_existing_post_before_not_implemented_staging()
    {
        var existingDocumentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));
        var request = CreateRequest(
            RelationalWriteOperationKind.Post,
            targetContext: new RelationalWriteTargetContext.ExistingDocument(345L, existingDocumentUuid, 44L)
        );
        var relationshipFailure = CreateProposedSchoolIdRelationshipFailure(request);
        _noProfilePersister.ProposedAuthorizationExceptionToThrow =
            new RelationalWriteRelationshipAuthorizationNotAuthorizedException(relationshipFailure);

        var result = await _sut.ExecuteAsync(
            request with
            {
                ProposedRelationshipAuthorization = CreateProposedSchoolIdRelationshipAuthorization(request),
            }
        );

        var upsertResult = result.Should().BeOfType<RelationalWriteExecutorResult.Upsert>().Subject;
        upsertResult
            .Result.Should()
            .BeOfType<UpsertResult.UpsertFailureRelationshipNotAuthorized>()
            .Which.RelationshipFailure.Should()
            .BeSameAs(relationshipFailure);
        _currentStateLoader.LoadCallCount.Should().Be(1);
        _noProfilePersister.AuthorizeProposedRelationshipCallCount.Should().Be(1);
        _writeFreshnessChecker.IsCurrentCallCount.Should().Be(0);
        _noProfilePersister.TryPersistCallCount.Should().Be(0);
        _writeSessionFactory.Session.CommitCallCount.Should().Be(0);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_applies_existing_post_after_successful_proposed_relationship_authorization()
    {
        var existingDocumentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));
        var request = CreateRequest(
            RelationalWriteOperationKind.Post,
            selectedBody: JsonNode.Parse("""{"schoolId":255901,"name":"Lincoln High Updated"}""")!,
            targetContext: new RelationalWriteTargetContext.ExistingDocument(345L, existingDocumentUuid, 44L)
        );
        _noProfileMergeSynthesizer.ResultToReturn = CreateMergeResult(
            request.WritePlan.TablePlansInDependencyOrder[0],
            currentSchoolId: 255901,
            mergedSchoolId: 255901,
            currentName: "Lincoln High",
            mergedName: "Lincoln High Updated"
        );

        var result = await _sut.ExecuteAsync(
            request with
            {
                ProposedRelationshipAuthorization = CreateProposedSchoolIdRelationshipAuthorization(request),
            }
        );

        var upsertResult = result.Should().BeOfType<RelationalWriteExecutorResult.Upsert>().Subject;
        var updateSuccess = upsertResult.Result.Should().BeOfType<UpsertResult.UpdateSuccess>().Subject;
        updateSuccess.ExistingDocumentUuid.Should().Be(existingDocumentUuid);
        updateSuccess.ETag.Should().Be(ComposedWriteResultEtag(77L));
        _currentStateLoader.LoadCallCount.Should().Be(1);
        _noProfilePersister.AuthorizeProposedRelationshipCallCount.Should().Be(1);
        GetSubjectRuntimeValue(
                _noProfilePersister
                    .CapturedMergeResult!
                    .ProposedRelationshipAuthorizationRuntimeCheck!
                    .Strategies[0]
                    .Subjects[0]
            )
            .Should()
            .Be(255901);
        _writeFreshnessChecker.IsCurrentCallCount.Should().Be(0);
        _noProfilePersister.TryPersistCallCount.Should().Be(1);
        _writeSessionFactory.Session.CommitCallCount.Should().Be(1);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(0);
    }

    [Test]
    public async Task It_authorizes_a_post_create_when_the_finalized_proposed_namespace_matches()
    {
        var request = CreateNamespacePostCreateRequest("uri://ed-fi.org/Survey");

        var result = await _sut.ExecuteAsync(request);

        result
            .Should()
            .BeOfType<RelationalWriteExecutorResult.Upsert>()
            .Which.Result.Should()
            .BeOfType<UpsertResult.InsertSuccess>();
        _noProfilePersister.TryPersistCallCount.Should().Be(1);
        _writeSessionFactory.Session.CommitCallCount.Should().Be(1);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(0);
    }

    [Test]
    public async Task It_binds_the_finalized_merged_namespace_value_and_not_the_request_body()
    {
        var request = CreateNamespacePostCreateRequest(
            mergedNamespace: "uri://ed-fi.org/Survey",
            selectedBody: JsonNode.Parse("""{"namespace":"uri://request-body-ignored/"}""")!
        );

        await _sut.ExecuteAsync(request);

        var namespaceCommand = _writeSessionFactory
            .Session.RelationshipAuthorizationCommands.Should()
            .ContainSingle()
            .Subject;
        namespaceCommand
            .Parameters.Single(parameter => parameter.Name == "@proposedNamespace")
            .Value.Should()
            .Be("uri://ed-fi.org/Survey");
    }

    [Test]
    public async Task It_returns_namespace_not_authorized_and_does_not_persist_on_a_proposed_mismatch()
    {
        var payload = NamespaceAuthorizationAuth1FailurePayloadCodec.Encode(
            new NamespaceAuthorizationAuth1FailurePayload(
                0,
                NamespaceAuthorizationAuth1FailureKind.NamespaceMismatch
            )
        );
        UseNamespaceProviderFailureExtractor(payload);
        _writeSessionFactory.Session.RelationshipAuthorizationCommandExecutor =
            new ThrowingRelationalCommandExecutor(SqlDialect.Pgsql, new StubDbException("namespace AUTH1"));
        var request = CreateNamespacePostCreateRequest("uri://other.org/Survey");

        var result = await _sut.ExecuteAsync(request);

        var notAuthorized = result
            .Should()
            .BeOfType<RelationalWriteExecutorResult.Upsert>()
            .Which.Result.Should()
            .BeOfType<UpsertResult.UpsertFailureNamespaceNotAuthorized>()
            .Subject;
        notAuthorized
            .NamespaceFailure.FailureKind.Should()
            .Be(NamespaceAuthorizationFailureKind.NamespaceMismatch);
        notAuthorized
            .NamespaceFailure.ValueSource.Should()
            .Be(NamespaceAuthorizationFailureValueSource.Proposed);
        _noProfilePersister.TryPersistCallCount.Should().Be(0);
        _writeSessionFactory.Session.CommitCallCount.Should().Be(0);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_returns_namespace_not_authorized_when_the_proposed_namespace_is_missing()
    {
        var payload = NamespaceAuthorizationAuth1FailurePayloadCodec.Encode(
            new NamespaceAuthorizationAuth1FailurePayload(
                0,
                NamespaceAuthorizationAuth1FailureKind.ProposedNamespaceMissing
            )
        );
        UseNamespaceProviderFailureExtractor(payload);
        _writeSessionFactory.Session.RelationshipAuthorizationCommandExecutor =
            new ThrowingRelationalCommandExecutor(SqlDialect.Pgsql, new StubDbException("namespace AUTH1"));
        var request = CreateNamespacePostCreateRequest(mergedNamespace: null);

        var result = await _sut.ExecuteAsync(request);

        var notAuthorized = result
            .Should()
            .BeOfType<RelationalWriteExecutorResult.Upsert>()
            .Which.Result.Should()
            .BeOfType<UpsertResult.UpsertFailureNamespaceNotAuthorized>()
            .Subject;
        notAuthorized
            .NamespaceFailure.FailureKind.Should()
            .Be(NamespaceAuthorizationFailureKind.ProposedNamespaceMissing);
        _noProfilePersister.TryPersistCallCount.Should().Be(0);
    }

    [Test]
    public async Task It_fails_closed_to_security_configuration_when_the_namespace_auth1_payload_cannot_be_mapped()
    {
        // An emitted index with no matching planned check is unmappable; fail closed as a
        // security-configuration error (matching relationship authorization) rather than allow.
        UseNamespaceProviderFailureExtractor("ns1|9|m");
        _writeSessionFactory.Session.RelationshipAuthorizationCommandExecutor =
            new ThrowingRelationalCommandExecutor(SqlDialect.Pgsql, new StubDbException("namespace AUTH1"));
        var request = CreateNamespacePostCreateRequest("uri://ed-fi.org/Survey");

        var result = await _sut.ExecuteAsync(request);

        result
            .Should()
            .BeOfType<RelationalWriteExecutorResult.Upsert>()
            .Which.Result.Should()
            .BeOfType<UpsertResult.UpsertFailureSecurityConfiguration>();
        _noProfilePersister.TryPersistCallCount.Should().Be(0);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_fails_closed_to_security_configuration_when_the_proposed_namespace_plan_cannot_be_reconciled_with_the_root_row()
    {
        // The planned namespace column has no binding in the finalized root row, so proposed-value
        // extraction returns InvalidAuthorizationPlan. The write must fail closed as a
        // security-configuration error (matching the proposed relationship sibling and the read-path
        // namespace mapping), not a generic unknown failure.
        var rootPlan = CreateNamespaceRootPlan();
        var request = CreateRequest(
            RelationalWriteOperationKind.Post,
            rootWritePlan: rootPlan,
            selectedBody: JsonNode.Parse("""{"namespace":"uri://ed-fi.org/Survey"}""")!
        );
        _writeFlattener.ResultToReturn = new FlattenedWriteSet(
            new RootWriteRowBuffer(
                rootPlan,
                [
                    FlattenedWriteValue.UnresolvedRootDocumentId.Instance,
                    new FlattenedWriteValue.Literal("uri://ed-fi.org/Survey"),
                ]
            )
        );
        var unreconcilableNamespaceAuth = new RelationalWriteNamespaceAuthorization(
            [
                new NamespaceAuthorizationCheckSpec(
                    0,
                    NamespaceAuthorizationCheckValueSource.Proposed,
                    _namespaceRootTable,
                    new DbColumnName("NotABoundColumn")
                ),
            ],
            NamespacePrefixParameterizationFactory.Create(
                SqlDialect.Pgsql,
                ["uri://ed-fi.org/"],
                "namespacePrefixes"
            )
        );

        var result = await _sut.ExecuteAsync(
            request with
            {
                ProposedNamespaceAuthorization = unreconcilableNamespaceAuth,
            }
        );

        result
            .Should()
            .BeOfType<RelationalWriteExecutorResult.Upsert>()
            .Which.Result.Should()
            .BeOfType<UpsertResult.UpsertFailureSecurityConfiguration>();
        _noProfilePersister.TryPersistCallCount.Should().Be(0);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_runs_the_proposed_namespace_check_for_an_existing_target_post()
    {
        var payload = NamespaceAuthorizationAuth1FailurePayloadCodec.Encode(
            new NamespaceAuthorizationAuth1FailurePayload(
                0,
                NamespaceAuthorizationAuth1FailureKind.NamespaceMismatch
            )
        );
        UseNamespaceProviderFailureExtractor(payload);
        _writeSessionFactory.Session.RelationshipAuthorizationCommandExecutor =
            new ThrowingRelationalCommandExecutor(SqlDialect.Pgsql, new StubDbException("namespace AUTH1"));
        var existingDocumentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));
        var rootPlan = CreateNamespaceRootPlan();
        var request = CreateRequest(
            RelationalWriteOperationKind.Post,
            rootWritePlan: rootPlan,
            targetContext: new RelationalWriteTargetContext.ExistingDocument(345L, existingDocumentUuid, 44L)
        );
        _writeFlattener.ResultToReturn = new FlattenedWriteSet(
            new RootWriteRowBuffer(
                rootPlan,
                [
                    FlattenedWriteValue.UnresolvedRootDocumentId.Instance,
                    new FlattenedWriteValue.Literal("uri://other.org/Survey"),
                ]
            )
        );

        var result = await _sut.ExecuteAsync(
            request with
            {
                ProposedNamespaceAuthorization = CreateProposedNamespaceAuthorization(),
            }
        );

        // The proposed namespace check now runs for an existing target rather than failing closed.
        var notAuthorized = result
            .Should()
            .BeOfType<RelationalWriteExecutorResult.Upsert>()
            .Which.Result.Should()
            .BeOfType<UpsertResult.UpsertFailureNamespaceNotAuthorized>()
            .Subject;
        notAuthorized
            .NamespaceFailure.ValueSource.Should()
            .Be(NamespaceAuthorizationFailureValueSource.Proposed);
        _noProfilePersister.TryPersistCallCount.Should().Be(0);
        _writeSessionFactory.Session.CommitCallCount.Should().Be(0);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_returns_proposed_namespace_failure_before_proposed_relationship_failure_for_existing_put()
    {
        var payload = NamespaceAuthorizationAuth1FailurePayloadCodec.Encode(
            new NamespaceAuthorizationAuth1FailurePayload(
                0,
                NamespaceAuthorizationAuth1FailureKind.NamespaceMismatch
            )
        );
        UseNamespaceProviderFailureExtractor(payload);
        _writeSessionFactory.Session.RelationshipAuthorizationCommandExecutor =
            new ThrowingRelationalCommandExecutor(SqlDialect.Pgsql, new StubDbException("namespace AUTH1"));
        var request = CreateRequest(
            RelationalWriteOperationKind.Put,
            selectedBody: JsonNode.Parse("""{"schoolId":255901,"name":"Lincoln High"}""")!
        );
        _noProfileMergeSynthesizer.ResultToReturn = CreateMergeResult(
            request.WritePlan.TablePlansInDependencyOrder[0],
            currentSchoolId: 255901,
            mergedSchoolId: 255901,
            currentName: "Lincoln High",
            mergedName: "Lincoln High"
        );
        // The persister throws if proposed relationship authorization is reached; a regression to
        // relationship-before-namespace would surface the relationship failure instead of the
        // namespace failure asserted below.
        var relationshipFailure = CreateProposedSchoolIdRelationshipFailure(request);
        _noProfilePersister.ProposedAuthorizationExceptionToThrow =
            new RelationalWriteRelationshipAuthorizationNotAuthorizedException(relationshipFailure);
        var rootTable = request.WritePlan.TablePlansInDependencyOrder[0].TableModel.Table;
        var namespaceAuth = new RelationalWriteNamespaceAuthorization(
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
                "namespacePrefixes"
            )
        );

        var result = await _sut.ExecuteAsync(
            request with
            {
                ProposedNamespaceAuthorization = namespaceAuth,
                ProposedRelationshipAuthorization = CreateProposedSchoolIdRelationshipAuthorization(request),
            }
        );

        var notAuthorized = result
            .Should()
            .BeOfType<RelationalWriteExecutorResult.Update>()
            .Which.Result.Should()
            .BeOfType<UpdateResult.UpdateFailureNamespaceNotAuthorized>()
            .Subject;
        notAuthorized
            .NamespaceFailure.ValueSource.Should()
            .Be(NamespaceAuthorizationFailureValueSource.Proposed);
        _noProfilePersister.AuthorizeProposedRelationshipCallCount.Should().Be(0);
        _noProfilePersister.TryPersistCallCount.Should().Be(0);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_returns_namespace_not_authorized_before_if_match_precondition_for_a_post_create()
    {
        var payload = NamespaceAuthorizationAuth1FailurePayloadCodec.Encode(
            new NamespaceAuthorizationAuth1FailurePayload(
                0,
                NamespaceAuthorizationAuth1FailureKind.NamespaceMismatch
            )
        );
        UseNamespaceProviderFailureExtractor(payload);
        _writeSessionFactory.Session.RelationshipAuthorizationCommandExecutor =
            new ThrowingRelationalCommandExecutor(SqlDialect.Pgsql, new StubDbException("namespace AUTH1"));
        // A stale If-Match alongside a namespace denial must yield the namespace 403, not a 412.
        var request = CreateNamespacePostCreateRequest(
            "uri://other.org/Survey",
            writePrecondition: new WritePrecondition.IfMatch("\"stale-etag\"")
        );

        var result = await _sut.ExecuteAsync(request);

        result
            .Should()
            .BeOfType<RelationalWriteExecutorResult.Upsert>()
            .Which.Result.Should()
            .BeOfType<UpsertResult.UpsertFailureNamespaceNotAuthorized>();
        _noProfilePersister.TryPersistCallCount.Should().Be(0);
    }

    [Test]
    public async Task It_returns_stored_namespace_not_authorized_before_if_match_precondition_for_a_put()
    {
        var payload = NamespaceAuthorizationAuth1FailurePayloadCodec.Encode(
            new NamespaceAuthorizationAuth1FailurePayload(
                0,
                NamespaceAuthorizationAuth1FailureKind.NamespaceMismatch
            )
        );
        UseNamespaceProviderFailureExtractor(payload);
        _writeSessionFactory.Session.RelationshipAuthorizationCommandExecutor =
            new ThrowingRelationalCommandExecutor(SqlDialect.Pgsql, new StubDbException("namespace AUTH1"));
        // A stale If-Match on a PUT must lose to a stored namespace denial evaluated in the locked boundary.
        var request = CreateRequest(
            RelationalWriteOperationKind.Put,
            writePrecondition: new WritePrecondition.IfMatch("\"stale-etag\"")
        );

        var result = await _sut.ExecuteAsync(
            request with
            {
                StoredNamespaceAuthorization = CreateStoredNamespaceAuthorization(),
            }
        );

        var notAuthorized = result
            .Should()
            .BeOfType<RelationalWriteExecutorResult.Update>()
            .Which.Result.Should()
            .BeOfType<UpdateResult.UpdateFailureNamespaceNotAuthorized>()
            .Subject;
        notAuthorized
            .NamespaceFailure.ValueSource.Should()
            .Be(NamespaceAuthorizationFailureValueSource.Stored);
        _currentEtagPreconditionChecker.CheckCallCount.Should().Be(0);
        _noProfilePersister.TryPersistCallCount.Should().Be(0);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_returns_stored_namespace_not_authorized_before_if_match_precondition_for_a_post_as_update()
    {
        var payload = NamespaceAuthorizationAuth1FailurePayloadCodec.Encode(
            new NamespaceAuthorizationAuth1FailurePayload(
                0,
                NamespaceAuthorizationAuth1FailureKind.NamespaceMismatch
            )
        );
        UseNamespaceProviderFailureExtractor(payload);
        _writeSessionFactory.Session.RelationshipAuthorizationCommandExecutor =
            new ThrowingRelationalCommandExecutor(SqlDialect.Pgsql, new StubDbException("namespace AUTH1"));
        // The POST resolves to an existing target in-session; the stored namespace denial in the locked
        // boundary must win over the stale If-Match precondition.
        var existingDocumentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));
        _targetLookupResolver.PostResults.Enqueue(
            new RelationalWriteTargetLookupResult.ExistingDocument(345L, existingDocumentUuid, 44L)
        );
        var request = CreateRequest(
            RelationalWriteOperationKind.Post,
            writePrecondition: new WritePrecondition.IfMatch("\"stale-etag\"")
        );

        var result = await _sut.ExecuteAsync(
            request with
            {
                StoredNamespaceAuthorization = CreateStoredNamespaceAuthorization(),
            }
        );

        var notAuthorized = result
            .Should()
            .BeOfType<RelationalWriteExecutorResult.Upsert>()
            .Which.Result.Should()
            .BeOfType<UpsertResult.UpsertFailureNamespaceNotAuthorized>()
            .Subject;
        notAuthorized
            .NamespaceFailure.ValueSource.Should()
            .Be(NamespaceAuthorizationFailureValueSource.Stored);
        _noProfilePersister.TryPersistCallCount.Should().Be(0);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_returns_proposed_namespace_not_authorized_before_relationship_no_claims_for_a_post_create()
    {
        var payload = NamespaceAuthorizationAuth1FailurePayloadCodec.Encode(
            new NamespaceAuthorizationAuth1FailurePayload(
                0,
                NamespaceAuthorizationAuth1FailureKind.NamespaceMismatch
            )
        );
        UseNamespaceProviderFailureExtractor(payload);
        _writeSessionFactory.Session.RelationshipAuthorizationCommandExecutor =
            new ThrowingRelationalCommandExecutor(SqlDialect.Pgsql, new StubDbException("namespace AUTH1"));
        // Mixed POST-create: NamespaceBased AND-composes ahead of the relationship OR-group, so an
        // unauthorized proposed namespace must surface over the deferred relationship NoClaims denial.
        var request = CreateNamespacePostCreateRequest("uri://other.org/Survey");

        var result = await _sut.ExecuteAsync(
            request with
            {
                ProposedRelationshipAuthorization = CreateNamespaceRootNoClaimsAuthorization(request),
            }
        );

        var notAuthorized = result
            .Should()
            .BeOfType<RelationalWriteExecutorResult.Upsert>()
            .Which.Result.Should()
            .BeOfType<UpsertResult.UpsertFailureNamespaceNotAuthorized>()
            .Subject;
        notAuthorized
            .NamespaceFailure.ValueSource.Should()
            .Be(NamespaceAuthorizationFailureValueSource.Proposed);
        _noProfilePersister.AuthorizeProposedRelationshipCallCount.Should().Be(0);
        _noProfilePersister.TryPersistCallCount.Should().Be(0);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_returns_relationship_no_claims_after_the_proposed_namespace_authorizes_for_a_post_create()
    {
        // The proposed namespace check authorizes (no AUTH1 raised), so the relationship NoClaims denial
        // that POST preflight deferred — rather than short-circuiting — now surfaces from the relationship
        // orchestrator that runs after the namespace orchestrator.
        var request = CreateNamespacePostCreateRequest("uri://ed-fi.org/Survey");

        var result = await _sut.ExecuteAsync(
            request with
            {
                ProposedRelationshipAuthorization = CreateNamespaceRootNoClaimsAuthorization(request),
            }
        );

        var relationshipFailure = result
            .Should()
            .BeOfType<RelationalWriteExecutorResult.Upsert>()
            .Which.Result.Should()
            .BeOfType<UpsertResult.UpsertFailureRelationshipNotAuthorized>()
            .Subject.RelationshipFailure;
        relationshipFailure.ClaimEducationOrganizationIds.Should().BeEmpty();
        relationshipFailure
            .FailedStrategies.Should()
            .ContainSingle()
            .Which.FailedSubjects.Should()
            .ContainSingle()
            .Which.FailureKind.Should()
            .Be(RelationshipAuthorizationSubjectFailureKind.NoRelationship);
        _noProfilePersister.AuthorizeProposedRelationshipCallCount.Should().Be(0);
        _noProfilePersister.TryPersistCallCount.Should().Be(0);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
    }

    private RelationalWriteExecutorRequest CreateNamespacePostCreateRequest(
        string? mergedNamespace,
        JsonNode? selectedBody = null,
        WritePrecondition? writePrecondition = null
    )
    {
        var rootPlan = CreateNamespaceRootPlan();
        var request = CreateRequest(
            RelationalWriteOperationKind.Post,
            rootWritePlan: rootPlan,
            selectedBody: selectedBody ?? JsonNode.Parse("""{"namespace":"uri://ed-fi.org/Survey"}""")!,
            writePrecondition: writePrecondition
        );
        _writeFlattener.ResultToReturn = new FlattenedWriteSet(
            new RootWriteRowBuffer(
                rootPlan,
                [
                    FlattenedWriteValue.UnresolvedRootDocumentId.Instance,
                    new FlattenedWriteValue.Literal(mergedNamespace),
                ]
            )
        );

        return request with
        {
            ProposedNamespaceAuthorization = CreateProposedNamespaceAuthorization(),
        };
    }

    private void UseNamespaceProviderFailureExtractor(string providerMessage)
    {
        _sut = new DefaultRelationalWriteExecutor(
            _writeSessionFactory,
            _referenceResolverAdapterFactory,
            _writeFlattener,
            _currentStateLoader,
            _currentEtagPreconditionChecker,
            _targetLookupResolver,
            _writeFreshnessChecker,
            _noProfileMergeSynthesizer,
            _profileMergeSynthesizer,
            _noProfilePersister,
            _writeExceptionClassifier,
            _writeConstraintResolver,
            _readMaterializer,
            new ServedEtagComposer(),
            new IfMatchEvaluator(),
            Options.Create(new ResourceLinksOptions()),
            relationshipAuthorizationProviderFailureExtractor: new StubRelationshipAuthorizationProviderFailureExtractor(
                NamespaceAuthorizationAuth1FailurePayloadCodec.ProviderFailureCode,
                providerMessage
            )
        );
    }

    [Test]
    public async Task It_authorizes_proposed_relationship_values_for_existing_put_before_persist()
    {
        var request = CreateRequest(
            RelationalWriteOperationKind.Put,
            selectedBody: JsonNode.Parse("""{"schoolId":255901,"name":"Lincoln High Updated"}""")!
        );
        _noProfileMergeSynthesizer.ResultToReturn = CreateMergeResult(
            request.WritePlan.TablePlansInDependencyOrder[0],
            currentSchoolId: 255901,
            mergedSchoolId: 255901,
            currentName: "Lincoln High",
            mergedName: "Lincoln High Updated"
        );

        var result = await _sut.ExecuteAsync(
            request with
            {
                StoredRelationshipAuthorization = CreateStoredSchoolIdRelationshipAuthorization(request),
                ProposedRelationshipAuthorization = CreateProposedSchoolIdRelationshipAuthorization(request),
            }
        );

        var updateResult = result.Should().BeOfType<RelationalWriteExecutorResult.Update>().Subject;
        var updateSuccess = updateResult.Result.Should().BeOfType<UpdateResult.UpdateSuccess>().Subject;
        updateSuccess
            .ExistingDocumentUuid.Should()
            .Be(new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb")));
        updateSuccess.ETag.Should().Be(ComposedWriteResultEtag(77L));
        _writeSessionFactory.Session.CreateCommandExecutorCallCount.Should().Be(1);
        _currentStateLoader.LoadCallCount.Should().Be(1);
        _noProfilePersister.AuthorizeProposedRelationshipCallCount.Should().Be(1);
        GetSubjectRuntimeValue(
                _noProfilePersister
                    .CapturedMergeResult!
                    .ProposedRelationshipAuthorizationRuntimeCheck!
                    .Strategies[0]
                    .Subjects[0]
            )
            .Should()
            .Be(255901);
        _noProfilePersister.TryPersistCallCount.Should().Be(1);
        _writeSessionFactory.Session.CommitCallCount.Should().Be(1);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(0);
    }

    [Test]
    public async Task It_returns_relationship_authorization_failure_for_existing_put_proposed_authorization()
    {
        var request = CreateRequest(
            RelationalWriteOperationKind.Put,
            allowIdentityUpdates: true,
            selectedBody: JsonNode.Parse("""{"schoolId":333333,"name":"Lincoln High Updated"}""")!
        );
        var relationshipFailure = CreateProposedSchoolIdRelationshipFailure(request);
        _noProfilePersister.ProposedAuthorizationExceptionToThrow =
            new RelationalWriteRelationshipAuthorizationNotAuthorizedException(relationshipFailure);
        _noProfileMergeSynthesizer.ResultToReturn = CreateMergeResult(
            request.WritePlan.TablePlansInDependencyOrder[0],
            currentSchoolId: 255901,
            mergedSchoolId: 333333,
            currentName: "Lincoln High",
            mergedName: "Lincoln High Updated"
        );

        var result = await _sut.ExecuteAsync(
            request with
            {
                StoredRelationshipAuthorization = CreateStoredSchoolIdRelationshipAuthorization(request),
                ProposedRelationshipAuthorization = CreateProposedSchoolIdRelationshipAuthorization(request),
            }
        );

        var updateResult = result.Should().BeOfType<RelationalWriteExecutorResult.Update>().Subject;
        updateResult
            .Result.Should()
            .BeOfType<UpdateResult.UpdateFailureRelationshipNotAuthorized>()
            .Which.RelationshipFailure.Should()
            .BeSameAs(relationshipFailure);
        _noProfilePersister.AuthorizeProposedRelationshipCallCount.Should().Be(1);
        _writeFreshnessChecker.IsCurrentCallCount.Should().Be(0);
        _noProfilePersister.TryPersistCallCount.Should().Be(0);
        _writeSessionFactory.Session.CommitCallCount.Should().Be(0);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_returns_people_proposed_relationship_failure_metadata_for_existing_put_without_persisting()
    {
        var request = CreateRequest(
            RelationalWriteOperationKind.Put,
            allowIdentityUpdates: true,
            selectedBody: JsonNode.Parse("""{"schoolId":333333,"name":"Lincoln High Updated"}""")!
        );
        var proposedAuthorization = CreateTransitivePeopleProposedRelationshipAuthorization(request);
        var relationshipFailure = CreateProposedRelationshipFailure(
            proposedAuthorization,
            new RelationshipAuthorizationAuth1SubjectFailure(
                0,
                0,
                RelationshipAuthorizationAuth1SubjectFailureKind.NoRelationship
            )
        );
        _noProfilePersister.ProposedAuthorizationExceptionToThrow =
            new RelationalWriteRelationshipAuthorizationNotAuthorizedException(relationshipFailure);
        _noProfileMergeSynthesizer.ResultToReturn = CreateMergeResult(
            request.WritePlan.TablePlansInDependencyOrder[0],
            currentSchoolId: 255901,
            mergedSchoolId: 333333,
            currentName: "Lincoln High",
            mergedName: "Lincoln High Updated"
        );

        var result = await _sut.ExecuteAsync(
            request with
            {
                StoredRelationshipAuthorization = CreateStoredSchoolIdRelationshipAuthorization(request),
                ProposedRelationshipAuthorization = proposedAuthorization,
            }
        );

        var updateResult = result.Should().BeOfType<RelationalWriteExecutorResult.Update>().Subject;
        var notAuthorized = updateResult
            .Result.Should()
            .BeOfType<UpdateResult.UpdateFailureRelationshipNotAuthorized>()
            .Subject;
        notAuthorized
            .RelationshipFailure.ValueSource.Should()
            .Be(RelationshipAuthorizationFailureValueSource.Proposed);
        notAuthorized.RelationshipFailure.Should().BeSameAs(relationshipFailure);
        var failedSubject = notAuthorized
            .RelationshipFailure.FailedStrategies.Should()
            .ContainSingle()
            .Subject.FailedSubjects.Should()
            .ContainSingle()
            .Subject;
        failedSubject.FailureKind.Should().Be(RelationshipAuthorizationSubjectFailureKind.NoRelationship);
        failedSubject.AuthObject.Name.Should().Be("auth.EducationOrganizationIdToStudentDocumentId");
        failedSubject.AuthObject.SubjectValueColumn.Should().Be("Student_DocumentId");
        failedSubject
            .SecurableElements.Should()
            .ContainSingle()
            .Which.Should()
            .BeEquivalentTo(
                new RelationshipAuthorizationSecurableElement(
                    "Student",
                    "$.studentReference.studentUniqueId",
                    "StudentUniqueId"
                )
            );
        failedSubject.PersonSubject.Should().NotBeNull();
        failedSubject.PersonSubject!.PathKind.Should().Be("TransitiveJoinPath");
        failedSubject.PersonSubject.ProposedAnchor.Should().NotBeNull();
        failedSubject.PersonSubject.ProposedAnchor!.Kind.Should().Be("FirstHop");
        failedSubject.PersonSubject.ProposedAnchor.Binding.ColumnName.Should().Be("SchoolId");
        _noProfilePersister.AuthorizeProposedRelationshipCallCount.Should().Be(1);
        _writeFreshnessChecker.IsCurrentCallCount.Should().Be(0);
        _noProfilePersister.TryPersistCallCount.Should().Be(0);
        _writeSessionFactory.Session.CommitCallCount.Should().Be(0);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_returns_proposed_relationship_authorization_failure_for_put_before_guarded_no_op_success()
    {
        var request = CreateRequest(RelationalWriteOperationKind.Put);
        var relationshipFailure = CreateProposedSchoolIdRelationshipFailure(request);
        _noProfilePersister.ProposedAuthorizationExceptionToThrow =
            new RelationalWriteRelationshipAuthorizationNotAuthorizedException(relationshipFailure);

        var result = await _sut.ExecuteAsync(
            request with
            {
                StoredRelationshipAuthorization = CreateStoredSchoolIdRelationshipAuthorization(request),
                ProposedRelationshipAuthorization = CreateProposedSchoolIdRelationshipAuthorization(request),
            }
        );

        var updateResult = result.Should().BeOfType<RelationalWriteExecutorResult.Update>().Subject;
        updateResult
            .Result.Should()
            .BeOfType<UpdateResult.UpdateFailureRelationshipNotAuthorized>()
            .Which.RelationshipFailure.Should()
            .BeSameAs(relationshipFailure);
        _noProfilePersister.AuthorizeProposedRelationshipCallCount.Should().Be(1);
        _writeFreshnessChecker.IsCurrentCallCount.Should().Be(0);
        _noProfilePersister.TryPersistCallCount.Should().Be(0);
        _writeSessionFactory.Session.CommitCallCount.Should().Be(0);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_returns_stale_put_if_match_before_deferred_missing_reference_when_proposed_authorization_is_required()
    {
        var documentReference = RelationalAccessTestData.CreateDocumentReference(
            new ReferentialId(Guid.NewGuid()),
            "$.schoolReference"
        );
        var request = CreateRequest(
            RelationalWriteOperationKind.Put,
            documentReferences: [documentReference],
            writePrecondition: new WritePrecondition.IfMatch("\"stale-etag\"")
        );
        _currentEtagPreconditionChecker.ResultToReturn = CreatePreconditionCheckResult(
            request,
            isMatch: false,
            currentEtag: "\"current-etag\"",
            contentVersion: 45L
        );

        var result = await _sut.ExecuteAsync(
            request with
            {
                StoredRelationshipAuthorization = CreateStoredSchoolIdRelationshipAuthorization(request),
                ProposedRelationshipAuthorization = CreateProposedSchoolIdRelationshipAuthorization(request),
            }
        );

        result
            .Should()
            .BeEquivalentTo(
                new RelationalWriteExecutorResult.Update(new UpdateResult.UpdateFailureETagMisMatch())
            );
        _currentEtagPreconditionChecker.CheckCallCount.Should().Be(0);
        _currentStateLoader.LoadCallCount.Should().Be(1);
        _writeFlattener.FlattenCallCount.Should().Be(1);
        _noProfilePersister.AuthorizeProposedRelationshipCallCount.Should().Be(1);
        _noProfilePersister.TryPersistCallCount.Should().Be(0);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_returns_proposed_relationship_authorization_failure_for_put_before_stale_if_match()
    {
        var request = CreateRequest(
            RelationalWriteOperationKind.Put,
            writePrecondition: new WritePrecondition.IfMatch("\"stale-etag\"")
        );
        var relationshipFailure = CreateProposedSchoolIdRelationshipFailure(request);
        _currentEtagPreconditionChecker.ResultToReturn = CreatePreconditionCheckResult(
            request,
            isMatch: false,
            currentEtag: "\"current-etag\"",
            contentVersion: 45L
        );
        _noProfilePersister.ProposedAuthorizationExceptionToThrow =
            new RelationalWriteRelationshipAuthorizationNotAuthorizedException(relationshipFailure);

        var result = await _sut.ExecuteAsync(
            request with
            {
                StoredRelationshipAuthorization = CreateStoredSchoolIdRelationshipAuthorization(request),
                ProposedRelationshipAuthorization = CreateProposedSchoolIdRelationshipAuthorization(request),
            }
        );

        var updateResult = result.Should().BeOfType<RelationalWriteExecutorResult.Update>().Subject;
        updateResult
            .Result.Should()
            .BeOfType<UpdateResult.UpdateFailureRelationshipNotAuthorized>()
            .Which.RelationshipFailure.Should()
            .BeSameAs(relationshipFailure);
        _currentEtagPreconditionChecker.CheckCallCount.Should().Be(0);
        _currentStateLoader.LoadCallCount.Should().Be(1);
        _noProfilePersister.AuthorizeProposedRelationshipCallCount.Should().Be(1);
        _readMaterializer.MaterializeCallCount.Should().Be(0);
        _writeFreshnessChecker.IsCurrentCallCount.Should().Be(0);
        _noProfilePersister.TryPersistCallCount.Should().Be(0);
        _writeSessionFactory.Session.CommitCallCount.Should().Be(0);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_returns_stale_put_if_match_after_successful_proposed_relationship_authorization()
    {
        var request = CreateRequest(
            RelationalWriteOperationKind.Put,
            writePrecondition: new WritePrecondition.IfMatch("\"stale-etag\"")
        );
        _readMaterializer.ResultToReturn = JsonNode.Parse("""{"schoolId":255901,"name":"Lincoln High"}""")!;

        var result = await _sut.ExecuteAsync(
            request with
            {
                StoredRelationshipAuthorization = CreateStoredSchoolIdRelationshipAuthorization(request),
                ProposedRelationshipAuthorization = CreateProposedSchoolIdRelationshipAuthorization(request),
            }
        );

        // The deferred If-Match check runs against a loaded current state, so a mismatch is a
        // Concurrency reason rather than TargetDoesNotExist.
        result
            .Should()
            .BeOfType<RelationalWriteExecutorResult.Update>()
            .Which.Result.Should()
            .BeOfType<UpdateResult.UpdateFailureETagMisMatch>()
            .Which.Reason.Should()
            .Be(ETagPreconditionFailureReason.Concurrency);
        _currentEtagPreconditionChecker.CheckCallCount.Should().Be(0);
        _currentStateLoader.LoadCallCount.Should().Be(1);
        _noProfilePersister.AuthorizeProposedRelationshipCallCount.Should().Be(1);
        // If-Match now composes the current etag from ContentVersion; it no longer materializes.
        _readMaterializer.MaterializeCallCount.Should().Be(0);
        _writeSessionFactory.Session.Commands.Should().ContainSingle();
        _writeFreshnessChecker.IsCurrentCallCount.Should().Be(0);
        _noProfilePersister.TryPersistCallCount.Should().Be(0);
        _writeSessionFactory.Session.CommitCallCount.Should().Be(0);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_returns_proposed_relationship_authorization_failure_for_post_as_update_before_stale_if_match()
    {
        var existingDocumentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));
        var request = CreateRequest(
            RelationalWriteOperationKind.Post,
            targetContext: new RelationalWriteTargetContext.ExistingDocument(345L, existingDocumentUuid, 44L),
            writePrecondition: new WritePrecondition.IfMatch("\"stale-etag\"")
        );
        _targetLookupResolver.PostResults.Enqueue(
            new RelationalWriteTargetLookupResult.ExistingDocument(345L, existingDocumentUuid, 44L)
        );
        var relationshipFailure = CreateProposedSchoolIdRelationshipFailure(request);
        _currentEtagPreconditionChecker.ResultToReturn = CreatePreconditionCheckResult(
            request,
            isMatch: false,
            currentEtag: "\"current-etag\"",
            contentVersion: 45L
        );
        _noProfilePersister.ProposedAuthorizationExceptionToThrow =
            new RelationalWriteRelationshipAuthorizationNotAuthorizedException(relationshipFailure);

        var result = await _sut.ExecuteAsync(
            request with
            {
                ProposedRelationshipAuthorization = CreateProposedSchoolIdRelationshipAuthorization(request),
            }
        );

        var upsertResult = result.Should().BeOfType<RelationalWriteExecutorResult.Upsert>().Subject;
        upsertResult
            .Result.Should()
            .BeOfType<UpsertResult.UpsertFailureRelationshipNotAuthorized>()
            .Which.RelationshipFailure.Should()
            .BeSameAs(relationshipFailure);
        _currentEtagPreconditionChecker.CheckCallCount.Should().Be(0);
        _currentStateLoader.LoadCallCount.Should().Be(1);
        _noProfilePersister.AuthorizeProposedRelationshipCallCount.Should().Be(1);
        _readMaterializer.MaterializeCallCount.Should().Be(0);
        _writeFreshnessChecker.IsCurrentCallCount.Should().Be(0);
        _noProfilePersister.TryPersistCallCount.Should().Be(0);
        _writeSessionFactory.Session.CommitCallCount.Should().Be(0);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_returns_stale_post_as_update_if_match_after_successful_proposed_relationship_authorization()
    {
        var existingDocumentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));
        var request = CreateRequest(
            RelationalWriteOperationKind.Post,
            targetContext: new RelationalWriteTargetContext.ExistingDocument(345L, existingDocumentUuid, 44L),
            writePrecondition: new WritePrecondition.IfMatch("\"stale-etag\"")
        );
        _targetLookupResolver.PostResults.Enqueue(
            new RelationalWriteTargetLookupResult.ExistingDocument(345L, existingDocumentUuid, 44L)
        );
        _readMaterializer.ResultToReturn = JsonNode.Parse("""{"schoolId":255901,"name":"Lincoln High"}""")!;

        var result = await _sut.ExecuteAsync(
            request with
            {
                ProposedRelationshipAuthorization = CreateProposedSchoolIdRelationshipAuthorization(request),
            }
        );

        result
            .Should()
            .BeEquivalentTo(
                new RelationalWriteExecutorResult.Upsert(new UpsertResult.UpsertFailureETagMisMatch())
            );
        _currentEtagPreconditionChecker.CheckCallCount.Should().Be(0);
        _currentStateLoader.LoadCallCount.Should().Be(1);
        _noProfilePersister.AuthorizeProposedRelationshipCallCount.Should().Be(1);
        // If-Match now composes the current etag from ContentVersion; it no longer materializes.
        _readMaterializer.MaterializeCallCount.Should().Be(0);
        _writeFreshnessChecker.IsCurrentCallCount.Should().Be(0);
        _noProfilePersister.TryPersistCallCount.Should().Be(0);
        _writeSessionFactory.Session.CommitCallCount.Should().Be(0);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_selects_create_new_post_relationship_plan_before_reference_resolution()
    {
        var documentReference = RelationalAccessTestData.CreateDocumentReference(
            new ReferentialId(Guid.NewGuid()),
            "$.studentReference"
        );
        var request = CreateRequest(
            RelationalWriteOperationKind.Post,
            documentReferences: [documentReference]
        );
        var createNewFailure = new RelationalWriteExecutorResult.Upsert(
            new UpsertResult.UpsertFailureSecurityConfiguration([
                "create-new self person DocumentId unavailable",
            ])
        );

        var result = await _sut.ExecuteAsync(
            request with
            {
                PostRelationshipAuthorizationPlans = CreatePostRelationshipAuthorizationPlans(
                    createNewImmediateResult: createNewFailure
                ),
            }
        );

        result.Should().BeSameAs(createNewFailure);
        _targetLookupResolver.ResolveForPostCallCount.Should().Be(1);
        _referenceResolverAdapterFactory.CreateSessionAdapterCallCount.Should().Be(0);
        _currentStateLoader.LoadCallCount.Should().Be(0);
        _writeFlattener.FlattenCallCount.Should().Be(0);
        _noProfilePersister.AuthorizeProposedRelationshipCallCount.Should().Be(0);
        _noProfilePersister.TryPersistCallCount.Should().Be(0);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_selects_existing_resource_post_relationship_plan_for_post_as_update_self_person_subjects()
    {
        var existingDocumentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));
        var request = CreateRequest(RelationalWriteOperationKind.Post);
        _targetLookupResolver.PostResults.Enqueue(
            new RelationalWriteTargetLookupResult.ExistingDocument(345L, existingDocumentUuid, 44L)
        );
        var existingResourceProposedAuthorization = CreateSelfPeopleExistingTargetRelationshipAuthorization(
            request,
            SecurableElementKind.Student
        );
        var createNewFailure = new RelationalWriteExecutorResult.Upsert(
            new UpsertResult.UpsertFailureSecurityConfiguration(["create-new plan should not be selected"])
        );

        var result = await _sut.ExecuteAsync(
            request with
            {
                PostRelationshipAuthorizationPlans = CreatePostRelationshipAuthorizationPlans(
                    existingResourceProposedAuthorization: existingResourceProposedAuthorization,
                    createNewImmediateResult: createNewFailure
                ),
            }
        );

        result
            .Should()
            .BeOfType<RelationalWriteExecutorResult.Upsert>()
            .Which.Result.Should()
            .BeOfType<UpsertResult.UpdateSuccess>();
        result.AttemptOutcome.Should().Be(RelationalWriteExecutorAttemptOutcome.GuardedNoOp.Instance);
        _targetLookupResolver.ResolveForPostCallCount.Should().Be(1);
        _currentStateLoader.LoadCallCount.Should().Be(1);
        _noProfilePersister.AuthorizeProposedRelationshipCallCount.Should().Be(1);
        _noProfilePersister.TryPersistCallCount.Should().Be(0);

        var runtimeSubject = _noProfilePersister
            .CapturedMergeResult!.ProposedRelationshipAuthorizationRuntimeCheck!.Strategies.Should()
            .ContainSingle()
            .Subject.Subjects.Should()
            .ContainSingle()
            .Subject;
        GetSubjectRuntimeValue(runtimeSubject).Should().Be(345L);
        runtimeSubject
            .Subject.PersonMetadata!.ProposedAnchor!.Kind.Should()
            .Be(RelationshipAuthorizationPersonProposedAnchorKind.ExistingTargetDocumentId);
    }

    [Test]
    public async Task It_checks_guarded_no_op_only_after_proposed_relationship_authorization_and_matching_if_match()
    {
        const long currentContentVersion = 44L;
        var request = CreateRequest(RelationalWriteOperationKind.Put);
        _currentStateLoader.ResultToReturn = CreateCurrentState(request, currentContentVersion);
        request = request with
        {
            WritePrecondition = new WritePrecondition.IfMatch(
                ComposedCurrentEtag(request, currentContentVersion)
            ),
        };

        var result = await _sut.ExecuteAsync(
            request with
            {
                StoredRelationshipAuthorization = CreateStoredSchoolIdRelationshipAuthorization(request),
                ProposedRelationshipAuthorization = CreateProposedSchoolIdRelationshipAuthorization(request),
            }
        );

        result
            .Should()
            .BeEquivalentTo(
                new RelationalWriteExecutorResult.Update(
                    new UpdateResult.UpdateSuccess(
                        new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb")),
                        ComposedWriteResultEtag(44L)
                    ),
                    RelationalWriteExecutorAttemptOutcome.GuardedNoOp.Instance
                )
            );
        _currentEtagPreconditionChecker.CheckCallCount.Should().Be(0);
        _currentStateLoader.LoadCallCount.Should().Be(1);
        _noProfilePersister.AuthorizeProposedRelationshipCallCount.Should().Be(1);
        // If-Match now composes the current etag from ContentVersion; it no longer materializes.
        _readMaterializer.MaterializeCallCount.Should().Be(0);
        _writeFreshnessChecker.IsCurrentCallCount.Should().Be(1);
        _noProfilePersister.TryPersistCallCount.Should().Be(0);
        _writeSessionFactory.Session.CommitCallCount.Should().Be(1);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(0);
    }

    [Test]
    public void It_preserves_strategy_and_subject_order_in_extracted_proposed_runtime_check()
    {
        var request = CreateRequest(RelationalWriteOperationKind.Post);
        var rootPlan = request.WritePlan.TablePlansInDependencyOrder[0];
        var rootRow = new RootWriteRowBuffer(
            rootPlan,
            [
                FlattenedWriteValue.UnresolvedRootDocumentId.Instance,
                new FlattenedWriteValue.Literal(255901),
                new FlattenedWriteValue.Literal("Lincoln High"),
            ]
        );

        var result = RelationshipAuthorizationProposedValueExtractor.Extract(
            CreateTwoStrategyTwoSubjectRelationshipAuthorization(request),
            rootRow,
            emittedAuth1Index: 0
        );

        var ready = result
            .Should()
            .BeOfType<ProposedRelationshipAuthorizationExtractionResult.Ready>()
            .Subject;
        ready.RuntimeCheck.Strategies.Should().HaveCount(2);
        ready
            .RuntimeCheck.Strategies.Select(static strategy => strategy.StrategyOrdinal)
            .Should()
            .Equal(0, 1);
        ready
            .RuntimeCheck.Strategies.Select(static strategy => strategy.CheckSpec.RelationshipLocalOrder)
            .Should()
            .Equal(0, 1);
        ready
            .RuntimeCheck.Strategies.Should()
            .AllSatisfy(strategy =>
            {
                strategy.Subjects.Should().HaveCount(2);
                strategy.Subjects.Select(static subject => subject.SubjectOrdinal).Should().Equal(0, 1);
                strategy.Subjects.Select(GetSubjectRuntimeValue).Should().Equal(255901, "Lincoln High");
            });
    }

    [Test]
    public void It_exposes_transitive_people_proposed_values_as_first_hop_anchors()
    {
        var request = CreateRequest(RelationalWriteOperationKind.Post);
        var rootPlan = request.WritePlan.TablePlansInDependencyOrder[0];
        var rootRow = new RootWriteRowBuffer(
            rootPlan,
            [
                FlattenedWriteValue.UnresolvedRootDocumentId.Instance,
                new FlattenedWriteValue.Literal(255901),
                new FlattenedWriteValue.Literal("Lincoln High"),
            ]
        );

        var result = RelationshipAuthorizationProposedValueExtractor.Extract(
            CreateTransitivePeopleProposedRelationshipAuthorization(request),
            rootRow,
            emittedAuth1Index: 0
        );

        var ready = result
            .Should()
            .BeOfType<ProposedRelationshipAuthorizationExtractionResult.Ready>()
            .Subject;
        var runtimeSubject = ready
            .RuntimeCheck.Strategies.Should()
            .ContainSingle()
            .Subject.Subjects.Should()
            .ContainSingle()
            .Subject;
        var anchorValue = runtimeSubject
            .RuntimeValue.Should()
            .BeOfType<ProposedRelationshipAuthorizationRuntimeValue.TransitivePeopleFirstHopAnchorValue>()
            .Subject;
        anchorValue.Value.Should().Be(255901);
        runtimeSubject.Binding.Table.Should().Be(rootPlan.TableModel.Table);
        runtimeSubject.Binding.Column.Value.Should().Be("SchoolId");
        runtimeSubject.Subject.Table.ToString().Should().Be("edfi.StudentSchoolAssociation");
        runtimeSubject.Subject.Column.Should().Be(AuthNames.StudentDocumentId);

        runtimeSubject.Subject.PersonMetadata.Should().NotBeNull();
        var personMetadata = runtimeSubject.Subject.PersonMetadata!;
        personMetadata
            .Path.Kind.Should()
            .Be(RelationshipAuthorizationPersonSubjectPathKind.TransitiveJoinPath);
        personMetadata.Path.Steps.Should().HaveCount(2);
        personMetadata.Path.Steps[0].SourceTable.Should().Be(rootPlan.TableModel.Table);
        personMetadata.Path.Steps[0].SourceColumnName.Value.Should().Be("SchoolId");
        personMetadata.Path.Steps[^1].SourceTable.ToString().Should().Be("edfi.StudentSchoolAssociation");
        personMetadata.Path.Steps[^1].SourceColumnName.Should().Be(AuthNames.StudentDocumentId);
        personMetadata.ProposedAnchor.Should().NotBeNull();
        personMetadata
            .ProposedAnchor!.Kind.Should()
            .Be(RelationshipAuthorizationPersonProposedAnchorKind.FirstHop);
        personMetadata.ProposedAnchor.Binding.Column.Value.Should().Be("SchoolId");
    }

    [TestCaseSource(nameof(SelfPersonExistingTargetCases))]
    public void It_allows_RelationshipAuthorizationProposedValueExtractor_to_bind_existing_target_document_ids_for_self_people(
        SecurableElementKind securableElementKind
    )
    {
        var request = CreateRequest(RelationalWriteOperationKind.Put);
        var rootPlan = request.WritePlan.TablePlansInDependencyOrder[0];
        var rootRow = new RootWriteRowBuffer(
            rootPlan,
            [
                FlattenedWriteValue.UnresolvedRootDocumentId.Instance,
                new FlattenedWriteValue.Literal(255901),
                new FlattenedWriteValue.Literal("Lincoln High"),
            ]
        );

        var result = RelationshipAuthorizationProposedValueExtractor.Extract(
            CreateSelfPeopleExistingTargetRelationshipAuthorization(request, securableElementKind),
            rootRow,
            emittedAuth1Index: 0,
            targetContext: new RelationalWriteTargetContext.ExistingDocument(
                98765L,
                new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"))
            )
        );

        var ready = result
            .Should()
            .BeOfType<ProposedRelationshipAuthorizationExtractionResult.Ready>()
            .Subject;
        var runtimeSubject = ready
            .RuntimeCheck.Strategies.Should()
            .ContainSingle()
            .Subject.Subjects.Should()
            .ContainSingle()
            .Subject;
        GetSubjectRuntimeValue(runtimeSubject).Should().Be(98765L);
        runtimeSubject.Binding.Table.Should().Be(rootPlan.TableModel.Table);
        runtimeSubject.Binding.Column.Value.Should().Be("DocumentId");
        runtimeSubject.Subject.PersonMetadata.Should().NotBeNull();
        runtimeSubject
            .Subject.PersonMetadata!.Path.Kind.Should()
            .Be(RelationshipAuthorizationPersonSubjectPathKind.SelfRootDocumentId);
        runtimeSubject
            .Subject.PersonMetadata.ProposedAnchor!.Kind.Should()
            .Be(RelationshipAuthorizationPersonProposedAnchorKind.ExistingTargetDocumentId);
    }

    [Test]
    public void It_preserves_missing_and_present_proposed_values_for_or_strategies()
    {
        var request = CreateRequest(RelationalWriteOperationKind.Post);
        var rootPlan = request.WritePlan.TablePlansInDependencyOrder[0];
        var rootRow = new RootWriteRowBuffer(
            rootPlan,
            [
                FlattenedWriteValue.UnresolvedRootDocumentId.Instance,
                new FlattenedWriteValue.Literal(null),
                new FlattenedWriteValue.Literal("Lincoln High"),
            ]
        );

        var result = RelationshipAuthorizationProposedValueExtractor.Extract(
            CreateTwoSingleSubjectStrategyRelationshipAuthorization(request),
            rootRow,
            emittedAuth1Index: 0
        );

        var ready = result
            .Should()
            .BeOfType<ProposedRelationshipAuthorizationExtractionResult.Ready>()
            .Subject;
        ready.RuntimeCheck.Strategies.Should().HaveCount(2);
        ready
            .RuntimeCheck.Strategies.Select(static strategy => strategy.StrategyOrdinal)
            .Should()
            .Equal(0, 1);
        ready
            .RuntimeCheck.Strategies.SelectMany(static strategy => strategy.Subjects)
            .Select(GetSubjectRuntimeValue)
            .Should()
            .Equal(new object?[] { null, "Lincoln High" });
    }

    [Test]
    public void It_preserves_missing_values_in_multi_subject_strategy()
    {
        var request = CreateRequest(RelationalWriteOperationKind.Post);
        var rootPlan = request.WritePlan.TablePlansInDependencyOrder[0];
        var rootRow = new RootWriteRowBuffer(
            rootPlan,
            [
                FlattenedWriteValue.UnresolvedRootDocumentId.Instance,
                new FlattenedWriteValue.Literal(null),
                new FlattenedWriteValue.Literal("Lincoln High"),
            ]
        );

        var result = RelationshipAuthorizationProposedValueExtractor.Extract(
            CreateSingleStrategyTwoSubjectRelationshipAuthorization(request),
            rootRow,
            emittedAuth1Index: 0
        );

        var ready = result
            .Should()
            .BeOfType<ProposedRelationshipAuthorizationExtractionResult.Ready>()
            .Subject;
        ready.RuntimeCheck.Strategies.Should().ContainSingle();
        ready
            .RuntimeCheck.Strategies[0]
            .Subjects.Select(GetSubjectRuntimeValue)
            .Should()
            .Equal(new object?[] { null, "Lincoln High" });
    }

    [Test]
    public void It_preserves_null_runtime_values_when_every_or_strategy_is_incomplete()
    {
        var request = CreateRequest(RelationalWriteOperationKind.Post);
        var rootPlan = request.WritePlan.TablePlansInDependencyOrder[0];
        var rootRow = new RootWriteRowBuffer(
            rootPlan,
            [
                FlattenedWriteValue.UnresolvedRootDocumentId.Instance,
                new FlattenedWriteValue.Literal(null),
                new FlattenedWriteValue.Literal(null),
            ]
        );

        var result = RelationshipAuthorizationProposedValueExtractor.Extract(
            CreateTwoSingleSubjectStrategyRelationshipAuthorization(request),
            rootRow,
            emittedAuth1Index: 0
        );

        var ready = result
            .Should()
            .BeOfType<ProposedRelationshipAuthorizationExtractionResult.Ready>()
            .Subject;
        ready
            .RuntimeCheck.Strategies.Select(static strategy => strategy.StrategyOrdinal)
            .Should()
            .Equal(0, 1);
        ready
            .RuntimeCheck.Strategies.SelectMany(static strategy => strategy.Subjects)
            .Select(GetSubjectRuntimeValue)
            .Should()
            .Equal(new object?[] { null, null });
    }

    [TestCaseSource(nameof(MissingProposedValueCases))]
    public void It_maps_unbound_proposed_runtime_values_to_null_parameters(FlattenedWriteValue missingValue)
    {
        var request = CreateRequest(RelationalWriteOperationKind.Post);
        var rootPlan = request.WritePlan.TablePlansInDependencyOrder[0];
        var rootRow = new RootWriteRowBuffer(
            rootPlan,
            [
                FlattenedWriteValue.UnresolvedRootDocumentId.Instance,
                missingValue,
                new FlattenedWriteValue.Literal("Lincoln High"),
            ]
        );

        var result = RelationshipAuthorizationProposedValueExtractor.Extract(
            CreateProposedSchoolIdRelationshipAuthorization(request),
            rootRow,
            emittedAuth1Index: 0
        );

        var ready = result
            .Should()
            .BeOfType<ProposedRelationshipAuthorizationExtractionResult.Ready>()
            .Subject;
        GetSubjectRuntimeValue(ready.RuntimeCheck.Strategies[0].Subjects[0]).Should().BeNull();
    }

    private static IEnumerable<TestCaseData> MissingProposedValueCases()
    {
        yield return new TestCaseData(new FlattenedWriteValue.Literal(null)).SetName("null literal");
        yield return new TestCaseData(new FlattenedWriteValue.Literal(DBNull.Value)).SetName(
            "DBNull literal"
        );
        yield return new TestCaseData(FlattenedWriteValue.UnresolvedRootDocumentId.Instance).SetName(
            "unresolved root document id"
        );
    }

    private static object? GetSubjectRuntimeValue(
        ProposedRelationshipAuthorizationRuntimeSubject runtimeSubject
    ) =>
        runtimeSubject.RuntimeValue switch
        {
            ProposedRelationshipAuthorizationRuntimeValue.SubjectValue subjectValue => subjectValue.Value,
            _ => throw new InvalidOperationException(
                $"Expected an authorization subject runtime value, but found '{runtimeSubject.RuntimeValue.GetType().Name}'."
            ),
        };

    private static IEnumerable<TestCaseData> SelfPersonExistingTargetCases()
    {
        yield return new TestCaseData(SecurableElementKind.Student).SetName("Student");
        yield return new TestCaseData(SecurableElementKind.Contact).SetName("Contact");
        yield return new TestCaseData(SecurableElementKind.Staff).SetName("Staff");
    }

    private static string GetSelfPersonJsonPath(SecurableElementKind securableElementKind) =>
        securableElementKind switch
        {
            SecurableElementKind.Student => "$.studentUniqueId",
            SecurableElementKind.Contact => "$.contactUniqueId",
            SecurableElementKind.Staff => "$.staffUniqueId",
            _ => throw new ArgumentOutOfRangeException(
                nameof(securableElementKind),
                securableElementKind,
                "Unsupported self person relationship authorization kind."
            ),
        };

    private static RelationshipAuthorizationResult.Authorized CreateProposedSchoolIdRelationshipAuthorization(
        RelationalWriteExecutorRequest request
    )
    {
        var rootPlan = request.WritePlan.TablePlansInDependencyOrder[0];
        var schoolIdBinding = rootPlan
            .ColumnBindings.Select(static (binding, index) => (binding, index))
            .Single(static entry => entry.binding.Column.ColumnName.Value == "SchoolId");
        var subject = new RelationshipAuthorizationSubject(
            request.WritePlan.Model.Resource,
            rootPlan.TableModel.Table,
            schoolIdBinding.binding.Column.ColumnName,
            RelationshipAuthorizationAuthObject.CreateEdOrgHierarchy(
                RelationshipAuthorizationHierarchyDirection.Normal
            ),
            [
                new RelationshipAuthorizationSubjectContributor(
                    SecurableElementKind.EducationOrganization,
                    "$.schoolId",
                    "SchoolId"
                ),
            ]
        );
        var checkSpec = new RelationshipAuthorizationCheckSpec(
            new ConfiguredAuthorizationStrategy(
                AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly,
                0
            ),
            0,
            RelationshipAuthorizationHierarchyDirection.Normal,
            RelationshipAuthorizationValueSource.Proposed,
            [subject],
            new RelationshipAuthorizationCheckTarget.Proposed(
                rootPlan.TableModel.Table,
                [
                    new RelationshipAuthorizationProposedValueBinding(
                        rootPlan.TableModel.Table,
                        schoolIdBinding.binding.Column.ColumnName,
                        schoolIdBinding.index,
                        schoolIdBinding.binding.Column.ColumnName.Value,
                        schoolIdBinding.binding.ParameterName
                    ),
                ]
            )
        );

        return new RelationshipAuthorizationResult.Authorized(
            [checkSpec],
            AuthorizationClaimEducationOrganizationIdParameterizationFactory.Create(
                request.MappingSet.Key.Dialect,
                [1234L],
                RelationalAuthorizationParameterNameConstants.ClaimEducationOrganizationIds
            )
        );
    }

    private static RelationshipAuthorizationResult.Authorized CreateTransitivePeopleProposedRelationshipAuthorization(
        RelationalWriteExecutorRequest request
    )
    {
        var rootPlan = request.WritePlan.TablePlansInDependencyOrder[0];
        var rootTable = rootPlan.TableModel.Table;
        var schoolIdBinding = rootPlan
            .ColumnBindings.Select(static (binding, index) => (binding, index))
            .Single(static entry => entry.binding.Column.ColumnName.Value == "SchoolId");
        var schoolIdColumn = schoolIdBinding.binding.Column.ColumnName;
        var studentSchoolAssociationTable = new DbTableName(
            new DbSchemaName("edfi"),
            "StudentSchoolAssociation"
        );
        var studentTable = new DbTableName(new DbSchemaName("edfi"), "Student");
        var proposedBinding = new RelationshipAuthorizationProposedValueBinding(
            rootTable,
            schoolIdColumn,
            schoolIdBinding.index,
            schoolIdColumn.Value,
            schoolIdBinding.binding.ParameterName
        );
        var personPath = new RelationshipAuthorizationPersonSubjectPath(
            RelationshipAuthorizationPersonSubjectPathKind.TransitiveJoinPath,
            [
                new ColumnPathStep(rootTable, schoolIdColumn, studentSchoolAssociationTable, schoolIdColumn),
                new ColumnPathStep(
                    studentSchoolAssociationTable,
                    AuthNames.StudentDocumentId,
                    studentTable,
                    AuthNames.StudentDocumentId
                ),
            ]
        );
        var subject = new RelationshipAuthorizationSubject(
            request.WritePlan.Model.Resource,
            studentSchoolAssociationTable,
            AuthNames.StudentDocumentId,
            RelationshipAuthorizationAuthObject.CreatePerson(
                RelationshipAuthorizationPersonAuthViewKind.Student
            ),
            [
                new RelationshipAuthorizationSubjectContributor(
                    SecurableElementKind.Student,
                    "$.studentReference.studentUniqueId",
                    "StudentUniqueId"
                ),
            ],
            new RelationshipAuthorizationPersonSubjectMetadata(
                RelationshipAuthorizationPersonKind.Student,
                personPath,
                new RelationshipAuthorizationPersonStoredAnchor(rootTable, new DbColumnName("DocumentId")),
                new RelationshipAuthorizationPersonProposedAnchor(
                    RelationshipAuthorizationPersonProposedAnchorKind.FirstHop,
                    proposedBinding
                )
            )
        );
        var checkSpec = new RelationshipAuthorizationCheckSpec(
            new ConfiguredAuthorizationStrategy(
                AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnly,
                0
            ),
            0,
            RelationshipAuthorizationHierarchyDirection.Normal,
            RelationshipAuthorizationValueSource.Proposed,
            [subject],
            new RelationshipAuthorizationCheckTarget.Proposed(rootTable, [proposedBinding])
        );

        return new RelationshipAuthorizationResult.Authorized(
            [checkSpec],
            AuthorizationClaimEducationOrganizationIdParameterizationFactory.Create(
                request.MappingSet.Key.Dialect,
                [1234L],
                RelationalAuthorizationParameterNameConstants.ClaimEducationOrganizationIds
            )
        );
    }

    private static RelationshipAuthorizationResult.Authorized CreateSelfPeopleExistingTargetRelationshipAuthorization(
        RelationalWriteExecutorRequest request,
        SecurableElementKind securableElementKind
    )
    {
        var rootPlan = request.WritePlan.TablePlansInDependencyOrder[0];
        var rootTable = rootPlan.TableModel.Table;
        var documentIdBinding = rootPlan
            .ColumnBindings.Select(static (binding, index) => (binding, index))
            .Single(static entry => entry.binding.Column.ColumnName.Value == "DocumentId");
        var documentIdColumn = documentIdBinding.binding.Column.ColumnName;
        var proposedBinding = new RelationshipAuthorizationProposedValueBinding(
            rootTable,
            documentIdColumn,
            documentIdBinding.index,
            documentIdColumn.Value,
            documentIdBinding.binding.ParameterName
        );
        var personMetadata = CreateSelfPersonSubjectMetadata(
            rootTable,
            documentIdColumn,
            proposedBinding,
            securableElementKind
        );
        var subject = new RelationshipAuthorizationSubject(
            request.WritePlan.Model.Resource,
            rootTable,
            documentIdColumn,
            personMetadata.AuthObject,
            [
                new RelationshipAuthorizationSubjectContributor(
                    securableElementKind,
                    GetSelfPersonJsonPath(securableElementKind),
                    documentIdColumn.Value
                ),
            ],
            personMetadata.Metadata
        );
        var checkSpec = new RelationshipAuthorizationCheckSpec(
            new ConfiguredAuthorizationStrategy(
                AuthorizationStrategyNameConstants.RelationshipsWithPeopleOnly,
                0
            ),
            0,
            RelationshipAuthorizationHierarchyDirection.Normal,
            RelationshipAuthorizationValueSource.Proposed,
            [subject],
            new RelationshipAuthorizationCheckTarget.Proposed(rootTable, [proposedBinding])
        );

        return new RelationshipAuthorizationResult.Authorized(
            [checkSpec],
            AuthorizationClaimEducationOrganizationIdParameterizationFactory.Create(
                request.MappingSet.Key.Dialect,
                [1234L],
                RelationalAuthorizationParameterNameConstants.ClaimEducationOrganizationIds
            )
        );
    }

    private static (
        RelationshipAuthorizationAuthObject AuthObject,
        RelationshipAuthorizationPersonSubjectMetadata Metadata
    ) CreateSelfPersonSubjectMetadata(
        DbTableName rootTable,
        DbColumnName documentIdColumn,
        RelationshipAuthorizationProposedValueBinding proposedBinding,
        SecurableElementKind securableElementKind
    )
    {
        var (personKind, authViewKind) = securableElementKind switch
        {
            SecurableElementKind.Student => (
                RelationshipAuthorizationPersonKind.Student,
                RelationshipAuthorizationPersonAuthViewKind.Student
            ),
            SecurableElementKind.Contact => (
                RelationshipAuthorizationPersonKind.Contact,
                RelationshipAuthorizationPersonAuthViewKind.Contact
            ),
            SecurableElementKind.Staff => (
                RelationshipAuthorizationPersonKind.Staff,
                RelationshipAuthorizationPersonAuthViewKind.Staff
            ),
            _ => throw new ArgumentOutOfRangeException(
                nameof(securableElementKind),
                securableElementKind,
                "Unsupported self person relationship authorization kind."
            ),
        };
        var authObject = RelationshipAuthorizationAuthObject.CreatePerson(authViewKind);

        return (
            authObject,
            new RelationshipAuthorizationPersonSubjectMetadata(
                personKind,
                new RelationshipAuthorizationPersonSubjectPath(
                    RelationshipAuthorizationPersonSubjectPathKind.SelfRootDocumentId,
                    []
                ),
                new RelationshipAuthorizationPersonStoredAnchor(rootTable, documentIdColumn),
                new RelationshipAuthorizationPersonProposedAnchor(
                    RelationshipAuthorizationPersonProposedAnchorKind.ExistingTargetDocumentId,
                    proposedBinding
                )
            )
        );
    }

    private static PostRelationshipAuthorizationPlans CreatePostRelationshipAuthorizationPlans(
        RelationshipAuthorizationResult? existingResourceStoredAuthorization = null,
        RelationshipAuthorizationResult.Authorized? existingResourceProposedAuthorization = null,
        RelationshipAuthorizationResult.Authorized? createNewProposedAuthorization = null,
        RelationalWriteExecutorResult? createNewImmediateResult = null
    )
    {
        var noAuthorizationRequired = new RelationshipAuthorizationResult.NoAuthorizationRequired([]);

        return new PostRelationshipAuthorizationPlans(
            new RelationshipAuthorizationUpdatePlan(
                existingResourceStoredAuthorization ?? noAuthorizationRequired,
                (RelationshipAuthorizationResult?)existingResourceProposedAuthorization
                    ?? noAuthorizationRequired,
                [],
                []
            ),
            createNewProposedAuthorization,
            createNewImmediateResult
        );
    }

    private static RelationshipAuthorizationResult.Authorized CreateStoredSchoolIdRelationshipAuthorization(
        RelationalWriteExecutorRequest request
    )
    {
        var rootPlan = request.WritePlan.TablePlansInDependencyOrder[0];
        var subject = CreateRelationshipAuthorizationSubject(
            request,
            rootPlan,
            "SchoolId",
            "$.schoolId",
            "SchoolId"
        );
        var checkSpec = new RelationshipAuthorizationCheckSpec(
            new ConfiguredAuthorizationStrategy(
                AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly,
                0
            ),
            0,
            RelationshipAuthorizationHierarchyDirection.Normal,
            RelationshipAuthorizationValueSource.Stored,
            [subject],
            new RelationshipAuthorizationCheckTarget.Stored(
                rootPlan.TableModel.Table,
                new DbColumnName("DocumentId")
            )
        );

        return new RelationshipAuthorizationResult.Authorized(
            [checkSpec],
            AuthorizationClaimEducationOrganizationIdParameterizationFactory.Create(
                request.MappingSet.Key.Dialect,
                [1234L],
                RelationalAuthorizationParameterNameConstants.ClaimEducationOrganizationIds
            )
        );
    }

    private static RelationshipAuthorizationResult.NoClaims CreateStoredSchoolIdNoClaimsAuthorization(
        RelationalWriteExecutorRequest request
    )
    {
        var authorized = CreateStoredSchoolIdRelationshipAuthorization(request);
        var checkSpec = authorized.CheckSpecs.Single();

        return new RelationshipAuthorizationResult.NoClaims(
            authorized.CheckSpecs,
            [
                new RelationshipAuthorizationFailureMetadata(
                    RelationshipAuthorizationFailureKind.NoClaimEducationOrganizationIds,
                    request.WritePlan.Model.Resource,
                    checkSpec.ConfiguredStrategy,
                    checkSpec.RelationshipLocalOrder,
                    checkSpec.ValueSource,
                    checkSpec.Subjects[0].AuthObject,
                    new RelationshipAuthorizationFailureLocation(
                        Kind: SecurableElementKind.EducationOrganization,
                        JsonPath: "$.schoolId",
                        ReadableName: "SchoolId",
                        Table: request.WritePlan.Model.Root.Table,
                        Column: new DbColumnName("SchoolId")
                    ),
                    Hint: "Relationship authorization requires at least one claim EducationOrganizationId."
                ),
            ]
        );
    }

    private static RelationshipAuthorizationResult.NoClaims CreateNamespaceRootNoClaimsAuthorization(
        RelationalWriteExecutorRequest request
    )
    {
        var rootPlan = request.WritePlan.TablePlansInDependencyOrder[0];
        var subject = CreateRelationshipAuthorizationSubject(
            request,
            rootPlan,
            "Namespace",
            "$.namespace",
            "Namespace"
        );
        var checkSpec = new RelationshipAuthorizationCheckSpec(
            new ConfiguredAuthorizationStrategy(
                AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly,
                0
            ),
            0,
            RelationshipAuthorizationHierarchyDirection.Normal,
            RelationshipAuthorizationValueSource.Proposed,
            [subject],
            new RelationshipAuthorizationCheckTarget.Stored(
                rootPlan.TableModel.Table,
                new DbColumnName("DocumentId")
            )
        );

        return new RelationshipAuthorizationResult.NoClaims(
            [checkSpec],
            [
                new RelationshipAuthorizationFailureMetadata(
                    RelationshipAuthorizationFailureKind.NoClaimEducationOrganizationIds,
                    request.WritePlan.Model.Resource,
                    checkSpec.ConfiguredStrategy,
                    checkSpec.RelationshipLocalOrder,
                    checkSpec.ValueSource,
                    checkSpec.Subjects[0].AuthObject,
                    new RelationshipAuthorizationFailureLocation(
                        Kind: SecurableElementKind.EducationOrganization,
                        JsonPath: "$.namespace",
                        ReadableName: "Namespace",
                        Table: rootPlan.TableModel.Table,
                        Column: _namespaceColumn
                    ),
                    Hint: "Relationship authorization requires at least one claim EducationOrganizationId."
                ),
            ]
        );
    }

    private static RelationshipAuthorizationFailure CreateProposedSchoolIdRelationshipFailure(
        RelationalWriteExecutorRequest request
    ) =>
        CreateProposedRelationshipFailure(
            CreateProposedSchoolIdRelationshipAuthorization(request),
            new RelationshipAuthorizationAuth1SubjectFailure(
                0,
                0,
                RelationshipAuthorizationAuth1SubjectFailureKind.NoRelationship
            )
        );

    private static RelationshipAuthorizationFailure CreateProposedRelationshipFailure(
        RelationshipAuthorizationResult.Authorized authorized,
        params RelationshipAuthorizationAuth1SubjectFailure[] subjectFailures
    )
    {
        if (
            !RelationshipAuthorizationFailureMapper.TryMapAuth1Failure(
                new RelationshipAuthorizationAuth1FailurePayload(0, subjectFailures),
                expectedEmittedAuth1Index: 0,
                authorized.CheckSpecs,
                authorized.ClaimEducationOrganizationIdParameterization!.ClaimEducationOrganizationIds,
                out var relationshipFailure
            ) || relationshipFailure is null
        )
        {
            throw new InvalidOperationException(
                "Test setup could not map the proposed relationship authorization failure."
            );
        }

        return relationshipFailure;
    }

    private static RelationshipAuthorizationResult.Authorized CreateSingleStrategyTwoSubjectRelationshipAuthorization(
        RelationalWriteExecutorRequest request
    )
    {
        var rootPlan = request.WritePlan.TablePlansInDependencyOrder[0];
        var subjects = new[]
        {
            CreateRelationshipAuthorizationSubject(request, rootPlan, "SchoolId", "$.schoolId", "SchoolId"),
            CreateRelationshipAuthorizationSubject(request, rootPlan, "Name", "$.name", "Name"),
        };
        var bindings = subjects
            .Select(subject => CreateProposedValueBinding(rootPlan, subject.Column.Value))
            .ToArray();

        return new RelationshipAuthorizationResult.Authorized(
            [
                CreateProposedCheckSpec(
                    rootPlan,
                    subjects,
                    bindings,
                    relationshipLocalOrder: 0,
                    rawConfiguredIndex: 0,
                    direction: RelationshipAuthorizationHierarchyDirection.Normal
                ),
            ],
            AuthorizationClaimEducationOrganizationIdParameterizationFactory.Create(
                request.MappingSet.Key.Dialect,
                [1234L],
                RelationalAuthorizationParameterNameConstants.ClaimEducationOrganizationIds
            )
        );
    }

    private static RelationshipAuthorizationResult.Authorized CreateTwoStrategyTwoSubjectRelationshipAuthorization(
        RelationalWriteExecutorRequest request
    )
    {
        var rootPlan = request.WritePlan.TablePlansInDependencyOrder[0];
        var subjects = new[]
        {
            CreateRelationshipAuthorizationSubject(request, rootPlan, "SchoolId", "$.schoolId", "SchoolId"),
            CreateRelationshipAuthorizationSubject(request, rootPlan, "Name", "$.name", "Name"),
        };
        var bindings = subjects
            .Select(subject => CreateProposedValueBinding(rootPlan, subject.Column.Value))
            .ToArray();

        return new RelationshipAuthorizationResult.Authorized(
            [
                CreateProposedCheckSpec(
                    rootPlan,
                    subjects,
                    bindings,
                    relationshipLocalOrder: 0,
                    rawConfiguredIndex: 0,
                    direction: RelationshipAuthorizationHierarchyDirection.Normal
                ),
                CreateProposedCheckSpec(
                    rootPlan,
                    subjects,
                    bindings,
                    relationshipLocalOrder: 1,
                    rawConfiguredIndex: 1,
                    direction: RelationshipAuthorizationHierarchyDirection.Inverted
                ),
            ],
            AuthorizationClaimEducationOrganizationIdParameterizationFactory.Create(
                request.MappingSet.Key.Dialect,
                [1234L],
                RelationalAuthorizationParameterNameConstants.ClaimEducationOrganizationIds
            )
        );
    }

    private static RelationshipAuthorizationResult.Authorized CreateTwoSingleSubjectStrategyRelationshipAuthorization(
        RelationalWriteExecutorRequest request
    )
    {
        var rootPlan = request.WritePlan.TablePlansInDependencyOrder[0];
        var schoolIdSubject = CreateRelationshipAuthorizationSubject(
            request,
            rootPlan,
            "SchoolId",
            "$.schoolId",
            "SchoolId"
        );
        var nameSubject = CreateRelationshipAuthorizationSubject(request, rootPlan, "Name", "$.name", "Name");

        return new RelationshipAuthorizationResult.Authorized(
            [
                CreateProposedCheckSpec(
                    rootPlan,
                    [schoolIdSubject],
                    [CreateProposedValueBinding(rootPlan, schoolIdSubject.Column.Value)],
                    relationshipLocalOrder: 0,
                    rawConfiguredIndex: 0,
                    direction: RelationshipAuthorizationHierarchyDirection.Normal
                ),
                CreateProposedCheckSpec(
                    rootPlan,
                    [nameSubject],
                    [CreateProposedValueBinding(rootPlan, nameSubject.Column.Value)],
                    relationshipLocalOrder: 1,
                    rawConfiguredIndex: 1,
                    direction: RelationshipAuthorizationHierarchyDirection.Inverted
                ),
            ],
            AuthorizationClaimEducationOrganizationIdParameterizationFactory.Create(
                request.MappingSet.Key.Dialect,
                [1234L],
                RelationalAuthorizationParameterNameConstants.ClaimEducationOrganizationIds
            )
        );
    }

    private static RelationshipAuthorizationCheckSpec CreateProposedCheckSpec(
        TableWritePlan rootPlan,
        IReadOnlyList<RelationshipAuthorizationSubject> subjects,
        IReadOnlyList<RelationshipAuthorizationProposedValueBinding> bindings,
        int relationshipLocalOrder,
        int rawConfiguredIndex,
        RelationshipAuthorizationHierarchyDirection direction
    ) =>
        new(
            new ConfiguredAuthorizationStrategy(
                direction is RelationshipAuthorizationHierarchyDirection.Normal
                    ? AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly
                    : AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnlyInverted,
                rawConfiguredIndex
            ),
            relationshipLocalOrder,
            direction,
            RelationshipAuthorizationValueSource.Proposed,
            [
                .. subjects.Select(subject =>
                    subject with
                    {
                        AuthObject = RelationshipAuthorizationAuthObject.CreateEdOrgHierarchy(direction),
                    }
                ),
            ],
            new RelationshipAuthorizationCheckTarget.Proposed(rootPlan.TableModel.Table, bindings)
        );

    private static RelationshipAuthorizationSubject CreateRelationshipAuthorizationSubject(
        RelationalWriteExecutorRequest request,
        TableWritePlan rootPlan,
        string columnName,
        string jsonPath,
        string readableName
    )
    {
        var binding = rootPlan
            .ColumnBindings.Select(static (binding, index) => (binding, index))
            .Single(entry => entry.binding.Column.ColumnName.Value == columnName);

        return new RelationshipAuthorizationSubject(
            request.WritePlan.Model.Resource,
            rootPlan.TableModel.Table,
            binding.binding.Column.ColumnName,
            RelationshipAuthorizationAuthObject.CreateEdOrgHierarchy(
                RelationshipAuthorizationHierarchyDirection.Normal
            ),
            [
                new RelationshipAuthorizationSubjectContributor(
                    SecurableElementKind.EducationOrganization,
                    jsonPath,
                    readableName
                ),
            ]
        );
    }

    private static RelationshipAuthorizationProposedValueBinding CreateProposedValueBinding(
        TableWritePlan rootPlan,
        string columnName
    )
    {
        var binding = rootPlan
            .ColumnBindings.Select(static (binding, index) => (binding, index))
            .Single(entry => entry.binding.Column.ColumnName.Value == columnName);

        return new RelationshipAuthorizationProposedValueBinding(
            rootPlan.TableModel.Table,
            binding.binding.Column.ColumnName,
            binding.index,
            binding.binding.Column.ColumnName.Value,
            binding.binding.ParameterName
        );
    }

    private static BackendProfileWriteContext BuildVisiblePresentRootProfileWriteContext(
        JsonNode writableBody,
        ResourceWritePlan writePlan
    )
    {
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
            ],
            VisibleRequestCollectionItems: []
        );

        // For the existing-document profile path, the executor invokes the projection fake.
        // Configure it to return a structurally-valid stored context so the merge
        // synthesizer observes both current state and profile-applied context as non-null.
        var storedAppliedContext = new ProfileAppliedWriteContext(
            Request: profileRequest,
            VisibleStoredBody: writableBody.DeepClone(),
            StoredScopeStates:
            [
                new StoredScopeState(
                    Address: new ScopeInstanceAddress("$", []),
                    Visibility: ProfileVisibilityKind.VisiblePresent,
                    HiddenMemberPaths: []
                ),
            ],
            VisibleStoredCollectionRows: []
        );

        var projectionInvoker = A.Fake<IStoredStateProjectionInvoker>();
        A.CallTo(() =>
                projectionInvoker.ProjectStoredState(
                    A<JsonNode>._,
                    A<ProfileAppliedWriteRequest>._,
                    A<IReadOnlyList<CompiledScopeDescriptor>>._
                )
            )
            .Returns(storedAppliedContext);

        return new BackendProfileWriteContext(
            Request: profileRequest,
            ProfileName: "test-write-profile",
            CompiledScopeCatalog: CompiledScopeAdapterFactory.BuildFromWritePlan(writePlan),
            StoredStateProjectionInvoker: projectionInvoker
        );
    }

    private static RelationalWriteExecutorRequest CreateRequest(
        RelationalWriteOperationKind operationKind,
        bool allowIdentityUpdates = false,
        IReadOnlyList<DocumentReference>? documentReferences = null,
        IReadOnlyList<DescriptorReference>? descriptorReferences = null,
        RelationalWriteTargetContext? targetContext = null,
        TableWritePlan? rootWritePlan = null,
        JsonNode? selectedBody = null,
        SqlDialect dialect = SqlDialect.Pgsql,
        WritePrecondition? writePrecondition = null
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
            targetContext: resolvedTargetContext,
            writePrecondition: writePrecondition
        );
    }

    // The composed write-result etag the executor produces for a committed write at a given
    // ContentVersion: schema epoch from the standard test mapping set ("schema-hash"), JSON format,
    // the write profile (or none), and links-on (the default ResourceLinksOptions).
    private static string ComposedWriteResultEtag(long contentVersion, string? profileName = null) =>
        new ServedEtagComposer().Compose(
            new ServedEtagContext(
                "schema-hash",
                ResponseFormat.Json,
                profileName,
                LinksEnabled: true,
                contentVersion
            )
        );

    // The composed current etag the write If-Match path produces for a request at a given
    // ContentVersion: schema epoch from the mapping set, JSON format, the write profile (or none),
    // and links-on. format/linkFlag are projected out of the If-Match comparison.
    private static string ComposedCurrentEtag(RelationalWriteExecutorRequest request, long contentVersion) =>
        EtagComposer.Compose(
            contentVersion,
            VariantKeyFactory.Create(
                request.MappingSet.Key.EffectiveSchemaHash,
                ResponseFormat.Json,
                ProfileVariantCode.Of(request.ProfileWriteContext?.ProfileName),
                linksEnabled: true
            )
        );

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

    private static readonly DbTableName _namespaceRootTable = new(new DbSchemaName("edfi"), "Survey");
    private static readonly DbColumnName _namespaceColumn = new("Namespace");

    private static TableWritePlan CreateNamespaceRootPlan()
    {
        var tableModel = new DbTableModel(
            _namespaceRootTable,
            new JsonPathExpression("$", []),
            new TableKey(
                "PK_Survey",
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
                    _namespaceColumn,
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 255),
                    false,
                    new JsonPathExpression("$.namespace", []),
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
            InsertSql: "insert into edfi.\"Survey\" values (@DocumentId, @Namespace)",
            UpdateSql: "update edfi.\"Survey\" set \"Namespace\" = @Namespace where \"DocumentId\" = @DocumentId",
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(100, 2, 1000),
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
                        new JsonPathExpression("$.namespace", []),
                        new RelationalScalarType(ScalarKind.String, MaxLength: 255)
                    ),
                    "Namespace"
                ),
            ],
            KeyUnificationPlans: []
        );
    }

    private static RelationalWriteNamespaceAuthorization CreateProposedNamespaceAuthorization(
        SqlDialect dialect = SqlDialect.Pgsql,
        string[]? prefixes = null
    ) =>
        new(
            [
                new NamespaceAuthorizationCheckSpec(
                    0,
                    NamespaceAuthorizationCheckValueSource.Proposed,
                    _namespaceRootTable,
                    _namespaceColumn
                ),
            ],
            NamespacePrefixParameterizationFactory.Create(
                dialect,
                prefixes ?? ["uri://ed-fi.org/"],
                "namespacePrefixes"
            )
        );

    private static RelationalWriteNamespaceAuthorization CreateStoredNamespaceAuthorization(
        SqlDialect dialect = SqlDialect.Pgsql,
        string[]? prefixes = null
    ) =>
        new(
            [
                new NamespaceAuthorizationCheckSpec(
                    0,
                    NamespaceAuthorizationCheckValueSource.Stored,
                    _namespaceRootTable,
                    _namespaceColumn
                ),
            ],
            NamespacePrefixParameterizationFactory.Create(
                dialect,
                prefixes ?? ["uri://ed-fi.org/"],
                "namespacePrefixes"
            )
        );

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

    private sealed class RecordingRelationalCurrentEtagPreconditionChecker
        : IRelationalCurrentEtagPreconditionChecker
    {
        public int CheckCallCount { get; private set; }

        public RelationalCurrentEtagPreconditionCheckRequest? CapturedRequest { get; private set; }

        public IRelationalWriteSession? CapturedWriteSession { get; private set; }

        public RelationalCurrentEtagPreconditionCheckResult? ResultToReturn { get; set; }

        public Queue<RelationalCurrentEtagPreconditionCheckResult?> QueuedResults { get; } = [];

        public Task<RelationalCurrentEtagPreconditionCheckResult?> CheckAsync(
            RelationalCurrentEtagPreconditionCheckRequest request,
            IRelationalWriteSession writeSession,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            CheckCallCount++;
            CapturedRequest = request;
            CapturedWriteSession = writeSession;

            return Task.FromResult(QueuedResults.Count > 0 ? QueuedResults.Dequeue() : ResultToReturn);
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

        public Exception? ExceptionToThrow { get; set; }

        public ProfileMergeOutcome Synthesize(RelationalWriteProfileMergeRequest request)
        {
            SynthesizeCallCount++;
            CapturedRequest = request;

            if (ExceptionToThrow is not null)
            {
                throw ExceptionToThrow;
            }

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

        public int AuthorizeProposedRelationshipCallCount { get; private set; }

        public RelationalWriteExecutorRequest? CapturedRequest { get; private set; }

        public RelationalWriteMergeResult? CapturedMergeResult { get; private set; }

        public IRelationalWriteSession? CapturedWriteSession { get; private set; }

        public Exception? ExceptionToThrow { get; set; }

        public Exception? ProposedAuthorizationExceptionToThrow { get; set; }

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

        public Task AuthorizeProposedRelationshipAsync(
            RelationalWriteExecutorRequest request,
            RelationalWriteMergeResult mergeResult,
            IRelationalWriteSession writeSession,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            AuthorizeProposedRelationshipCallCount++;
            CapturedRequest = request;
            CapturedMergeResult = mergeResult;
            CapturedWriteSession = writeSession;

            if (ProposedAuthorizationExceptionToThrow is not null)
            {
                throw ProposedAuthorizationExceptionToThrow;
            }

            return Task.CompletedTask;
        }

        private static RelationalWritePersistResult CreateDefaultResult(
            RelationalWriteExecutorRequest request
        ) =>
            request.TargetContext switch
            {
                RelationalWriteTargetContext.CreateNew(var documentUuid) => new(910L, documentUuid, 77L),
                RelationalWriteTargetContext.ExistingDocument(var documentId, var documentUuid, _) => new(
                    documentId,
                    documentUuid,
                    77L
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

    private static RelationalCurrentEtagPreconditionCheckResult CreatePreconditionCheckResult(
        RelationalWriteExecutorRequest request,
        bool isMatch,
        string currentEtag,
        long contentVersion,
        string schoolName = "Lincoln High"
    )
    {
        var currentState = CreateCurrentState(request, contentVersion, schoolName);
        var targetContext =
            request.TargetContext as RelationalWriteTargetContext.ExistingDocument
            ?? throw new InvalidOperationException("Expected an existing-document target context.");

        return new RelationalCurrentEtagPreconditionCheckResult(
            currentState,
            targetContext with
            {
                ObservedContentVersion = contentVersion,
            },
            currentEtag,
            isMatch
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

        public IRelationalCommandExecutor RelationshipAuthorizationCommandExecutor { get; set; } =
            CreateAuthorizedRelationshipAuthorizationCommandExecutor();

        public List<RelationalCommand> RelationshipAuthorizationCommands =>
            RelationshipAuthorizationCommandExecutor switch
            {
                InMemoryRelationalCommandExecutor inMemoryExecutor => inMemoryExecutor.Commands,
                ThrowingRelationalCommandExecutor throwingExecutor => throwingExecutor.Commands,
                _ => throw new InvalidOperationException(
                    "Relationship authorization command executor does not expose recorded commands."
                ),
            };

        public int CreateCommandExecutorCallCount { get; private set; }

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

        public IRelationalCommandExecutor CreateCommandExecutor()
        {
            CreateCommandExecutorCallCount++;
            return RelationshipAuthorizationCommandExecutor;
        }

        private static InMemoryRelationalCommandExecutor CreateAuthorizedRelationshipAuthorizationCommandExecutor(
            long contentVersion = 45L
        ) =>
            new([
                new InMemoryRelationalCommandExecution([
                    InMemoryRelationalResultSet.Create(
                        RelationalAccessTestData.CreateRow(
                            ("AuthorizationResult", 1),
                            ("ContentVersion", contentVersion)
                        )
                    ),
                ]),
            ]);

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

    private sealed class RecordingRelationalParameterConfigurator : IRelationalParameterConfigurator
    {
        public List<QuerySqlParameter> CapturedParameters { get; } = [];

        public void ConfigureParameter(DbParameter dbParameter, QuerySqlParameter querySqlParameter)
        {
            ArgumentNullException.ThrowIfNull(dbParameter);
            ArgumentNullException.ThrowIfNull(querySqlParameter);

            CapturedParameters.Add(querySqlParameter);
        }
    }

    private sealed class StubRelationshipAuthorizationProviderFailureExtractor(
        string? providerErrorCode,
        string providerMessage
    ) : IRelationshipAuthorizationProviderFailureExtractor
    {
        public int ExtractCallCount { get; private set; }

        public RelationshipAuthorizationProviderFailure Extract(DbException exception)
        {
            ArgumentNullException.ThrowIfNull(exception);

            ExtractCallCount++;
            return new RelationshipAuthorizationProviderFailure(providerErrorCode, providerMessage);
        }
    }

    private sealed class ThrowingRelationalCommandExecutor(SqlDialect dialect, DbException exceptionToThrow)
        : IRelationalCommandExecutor
    {
        private readonly DbException _exceptionToThrow =
            exceptionToThrow ?? throw new ArgumentNullException(nameof(exceptionToThrow));

        public SqlDialect Dialect { get; } = dialect;

        public List<RelationalCommand> Commands { get; } = [];

        public Task<TResult> ExecuteReaderAsync<TResult>(
            RelationalCommand command,
            Func<IRelationalCommandReader, CancellationToken, Task<TResult>> readAsync,
            CancellationToken cancellationToken = default
        )
        {
            ArgumentNullException.ThrowIfNull(command);
            ArgumentNullException.ThrowIfNull(readAsync);
            cancellationToken.ThrowIfCancellationRequested();

            Commands.Add(command);
            throw _exceptionToThrow;
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

        public bool IsForeignKeyViolation(DbException exception) => false;

        public bool IsUniqueConstraintViolation(DbException exception) => false;

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

        public void StripReferenceLinks(JsonNode document, ResourceReadPlan readPlan)
        {
            // No-op recording double — write executor paths never invoke the strip pass.
        }
    }

    // ── Top-level collection profile routing tests ──────────────────────────

    [Test]
    public async Task Given_Top_level_collection_request_with_root_inlined_scope_runs_profile_merge()
    {
        // Slice 4 composes with earlier slices: a top-level collection row stream plus
        // a root-hosted inlined scope must still reach the profile merge synthesizer.
        var writableBody = JsonNode.Parse("""{"schoolId":255901}""")!;
        // Use CreateRootPlan() which has the proper 3-column shape (DocumentId, SchoolId, Name)
        // so FlattenedWriteSet can provide matching values for all ColumnBindings.
        var rootPlan = CreateRootPlan();
        var collectionPlan = ProfileRoutingTestPlans.CreateCollectionTablePlan(
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
            .Be(1, "profile merge must run for top-level collection requests");
        _noProfilePersister.TryPersistCallCount.Should().Be(1);
        _writeSessionFactory.Session.CommitCallCount.Should().Be(1);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(0);

        var upsertResult = result.Should().BeOfType<RelationalWriteExecutorResult.Upsert>().Subject;
        upsertResult.Result.Should().BeOfType<UpsertResult.InsertSuccess>();
    }

    [Test]
    public async Task Given_Top_level_collection_request_with_collection_extension_scope_runs_profile_merge()
    {
        // When a request includes a top-level collection and also exercises a
        // CollectionExtensionScope, the aligned scope flows through profile merge.
        var writableBody = JsonNode.Parse("""{"schoolId":255901}""")!;
        var rootPlan = ProfileRoutingTestPlans.RootTablePlan();
        var collectionPlan = ProfileRoutingTestPlans.CreateCollectionTablePlan(
            "$.addresses[*]",
            "Addresses",
            DbTableKind.Collection
        );
        var collectionExtPlan = ProfileRoutingTestPlans.CreateTablePlan(
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
                    // so the contract validator accepts the profile request.
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
        _writeFlattener.ResultToReturn = new FlattenedWriteSet(
            new RootWriteRowBuffer(rootPlan, [FlattenedWriteValue.UnresolvedRootDocumentId.Instance])
        );

        var result = await _sut.ExecuteAsync(request);

        _writeFlattener
            .FlattenCallCount.Should()
            .Be(1, "TopLevelCollection + CollectionExtensionScope now reaches flattening");
        _profileMergeSynthesizer
            .SynthesizeCallCount.Should()
            .Be(1, "profile merge must run after the collection-aligned guard is retired");
        _noProfilePersister.TryPersistCallCount.Should().Be(1);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(0);
        _writeSessionFactory.Session.CommitCallCount.Should().Be(1);

        var upsertResult = result.Should().BeOfType<RelationalWriteExecutorResult.Upsert>().Subject;
        upsertResult.Result.Should().BeOfType<UpsertResult.InsertSuccess>();
    }

    [Test]
    public async Task Given_Top_level_collection_request_with_reference_backed_semantic_identity_runs_profile_merge()
    {
        // A collection table whose identity comes from a reference-derived fallback
        // (ReferenceFallback) is still a plain DbTableKind.Collection table and must reach
        // profile merge synthesis.
        var writableBody = JsonNode.Parse("""{"schoolId":255901}""")!;
        var rootPlan = CreateRootPlan();
        var collectionPlan = ProfileRoutingTestPlans.CreateCollectionTablePlanWithReferenceBackedIdentity(
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
            .Be(1, "reference-backed semantic identity must reach the flattener");
        _profileMergeSynthesizer
            .SynthesizeCallCount.Should()
            .Be(1, "profile merge synthesizer must be called");
        _noProfilePersister.TryPersistCallCount.Should().Be(1);
        _writeSessionFactory.Session.CommitCallCount.Should().Be(1);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(0);

        var upsertResult = result.Should().BeOfType<RelationalWriteExecutorResult.Upsert>().Subject;
        upsertResult.Result.Should().BeOfType<UpsertResult.InsertSuccess>();
    }

    private sealed class StubDbException(string message) : DbException(message);
}

/// <summary>
/// File-local write-plan builders for profile write routing tests.
/// </summary>
file static class ProfileRoutingTestPlans
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
    /// <see cref="DbTableIdentityMetadata"/>. Used to prove that reference-backed collection
    /// identity still reaches the profile merge synthesizer.
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

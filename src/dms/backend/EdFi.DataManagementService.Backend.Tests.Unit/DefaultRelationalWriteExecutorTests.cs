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
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.External.Security;
using EdFi.DataManagementService.Core.Profile;
using FakeItEasy;
using FluentAssertions;
using FluentAssertions.Execution;
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
    private RecordingRelationalWriteTargetLookupResolver _targetLookupResolver = null!;
    private RecordingRelationalWriteNoProfileMergeSynthesizer _noProfileMergeSynthesizer = null!;
    private RecordingRelationalWriteProfileMergeSynthesizer _profileMergeSynthesizer = null!;
    private RecordingRelationalWriteNoProfilePersister _noProfilePersister = null!;
    private RecordingRelationalWriteExceptionClassifier _writeExceptionClassifier = null!;
    private RecordingRelationalWriteConstraintResolver _writeConstraintResolver = null!;
    private RecordingRelationalReadMaterializer _readMaterializer = null!;
    private RelationalWriteTargetContext _arrangedTargetContext = null!;
    private DefaultRelationalWriteExecutor _sut = null!;

    private static readonly DocumentUuid CreateDocumentUuid = new(
        Guid.Parse("cccccccc-1111-2222-3333-dddddddddddd")
    );

    private static readonly DocumentUuid UpdateDocumentUuid = new(
        Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb")
    );
    private static readonly DocumentCacheTargetKey DocumentCacheTelemetryTargetKey =
        DocumentCacheTargetKey.Create("Tenant-A", 99);

    [SetUp]
    public void Setup()
    {
        _writeSessionFactory = new RecordingRelationalWriteSessionFactory();
        _referenceResolverAdapterFactory = new RecordingReferenceResolverAdapterFactory();
        _writeFlattener = new RecordingRelationalWriteFlattener();
        _currentStateLoader = new RecordingRelationalWriteCurrentStateLoader();
        _targetLookupResolver = new RecordingRelationalWriteTargetLookupResolver();
        _noProfileMergeSynthesizer = new RecordingRelationalWriteNoProfileMergeSynthesizer();
        _profileMergeSynthesizer = new RecordingRelationalWriteProfileMergeSynthesizer();
        _noProfilePersister = new RecordingRelationalWriteNoProfilePersister();
        _writeExceptionClassifier = new RecordingRelationalWriteExceptionClassifier();
        _writeConstraintResolver = new RecordingRelationalWriteConstraintResolver();
        _readMaterializer = new RecordingRelationalReadMaterializer();
        _sut = CreateExecutor();
    }

    /// <summary>
    /// Builds the executor under test with the fixture's fakes. The first phase and the
    /// second-command phase are sequential test seams: they observe through
    /// the same fakeable resolver, adapter factory, state loader, and persister the pre-composite
    /// pipeline used, while their decisions run through the production policy functions.
    /// </summary>
    private DefaultRelationalWriteExecutor CreateExecutor(
        IRelationalWriteNoProfileMergeSynthesizer? noProfileMergeSynthesizer = null,
        IRelationalParameterConfigurator? relationalParameterConfigurator = null,
        IRelationshipAuthorizationProviderFailureExtractor? relationshipAuthorizationProviderFailureExtractor =
            null,
        ILogger<DefaultRelationalWriteExecutor>? logger = null,
        IDocumentCacheEnqueueTelemetry? documentCacheEnqueueTelemetry = null,
        IDataStoreSelection? dataStoreSelection = null,
        IDocumentCacheTargetRegistry? documentCacheTargetRegistry = null,
        IDocumentCacheProviderCommandTimeoutClassifier? documentCacheProviderCommandTimeoutClassifier = null
    ) =>
        new(
            _writeSessionFactory,
            _referenceResolverAdapterFactory,
            _writeFlattener,
            noProfileMergeSynthesizer ?? _noProfileMergeSynthesizer,
            _profileMergeSynthesizer,
            _writeExceptionClassifier,
            _writeConstraintResolver,
            _readMaterializer,
            new ServedEtagComposer(),
            Options.Create(new ResourceLinksOptions()),
            relationalParameterConfigurator,
            relationshipAuthorizationProviderFailureExtractor,
            logger,
            loggerFactory: null,
            dataStoreSelection: dataStoreSelection,
            documentCacheEnqueueTelemetry: documentCacheEnqueueTelemetry,
            documentCacheTargetRegistry: documentCacheTargetRegistry,
            writeFirstPhase: new FakeSequentialRelationalWriteFirstPhase(
                _targetLookupResolver,
                _referenceResolverAdapterFactory,
                _currentStateLoader,
                relationalParameterConfigurator,
                relationshipAuthorizationProviderFailureExtractor,
                logger
            ),
            secondCommandPhase: new FakeSequentialRelationalWriteSecondCommand(
                _noProfilePersister,
                relationshipAuthorizationProviderFailureExtractor
            ),
            documentCacheProviderCommandTimeoutClassifier: documentCacheProviderCommandTimeoutClassifier
        );

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
            .CapturedCommandExecutor.Should()
            .BeSameAs(_writeSessionFactory.Session.RelationshipAuthorizationCommandExecutor);
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
        _targetLookupResolver
            .CapturedCommandExecutor.Should()
            .BeSameAs(_writeSessionFactory.Session.RelationshipAuthorizationCommandExecutor);
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
        // The initial observation precedes reference resolution, so it happens even when the
        // attempt short-circuits on a reference failure; nothing re-observes afterwards.
        _targetLookupResolver.ResolveForPostCallCount.Should().Be(1);
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
    public async Task It_records_DocumentCacheEnqueueTelemetry_success_only_after_committed_applied_write()
    {
        var telemetry = new RecordingDocumentCacheEnqueueTelemetry();
        _sut = CreateExecutor(
            documentCacheEnqueueTelemetry: telemetry,
            dataStoreSelection: CreateSelectedDataStoreSelection(),
            documentCacheTargetRegistry: CreateDocumentCacheTargetRegistry()
        );
        var request = CreateRequest(
            RelationalWriteOperationKind.Post,
            tenantKey: DocumentCacheTelemetryTargetKey.TenantKey
        );

        await _sut.ExecuteAsync(request);

        _writeSessionFactory.Session.CommitCallCount.Should().Be(1);
        telemetry.Successes.Should().ContainSingle();
        telemetry.Failures.Should().BeEmpty();
        telemetry.Successes[0].TargetKey.Should().Be(DocumentCacheTelemetryTargetKey);
        telemetry
            .Successes[0]
            .CanonicalOperation.Should()
            .Be(DocumentCacheEnqueueTelemetryCanonicalOperation.Insert);
        telemetry.Successes[0].ResourceKind.Should().Be(DocumentCacheEnqueueTelemetryResourceKind.Resource);
    }

    [Test]
    public async Task It_records_DocumentCacheEnqueueTelemetry_success_for_committed_already_satisfied_enqueue_outcome()
    {
        var telemetry = new RecordingDocumentCacheEnqueueTelemetry();
        _sut = CreateExecutor(
            documentCacheEnqueueTelemetry: telemetry,
            dataStoreSelection: CreateSelectedDataStoreSelection(),
            documentCacheTargetRegistry: CreateDocumentCacheTargetRegistry()
        );
        var request = CreateRequest(
            RelationalWriteOperationKind.Post,
            tenantKey: DocumentCacheTelemetryTargetKey.TenantKey
        );
        _noProfilePersister.ResultToReturn = new RelationalWritePersistResult(
            910L,
            CreateDocumentUuid,
            77L,
            DocumentCacheEnqueueOutcome.AlreadySatisfied
        );

        await _sut.ExecuteAsync(request);

        telemetry.Successes.Should().ContainSingle();
        telemetry.Successes[0].TargetKey.Should().Be(DocumentCacheTelemetryTargetKey);
    }

    [Test]
    public async Task It_does_not_record_DocumentCacheEnqueueTelemetry_success_when_registry_is_enabled_but_transaction_reports_no_work()
    {
        var telemetry = new RecordingDocumentCacheEnqueueTelemetry();
        _sut = CreateExecutor(
            documentCacheEnqueueTelemetry: telemetry,
            dataStoreSelection: CreateSelectedDataStoreSelection(),
            documentCacheTargetRegistry: CreateDocumentCacheTargetRegistry()
        );
        var request = CreateRequest(
            RelationalWriteOperationKind.Post,
            tenantKey: DocumentCacheTelemetryTargetKey.TenantKey
        );
        _noProfilePersister.ResultToReturn = new RelationalWritePersistResult(
            910L,
            CreateDocumentUuid,
            77L,
            DocumentCacheEnqueueOutcome.NoWorkQueued
        );

        await _sut.ExecuteAsync(request);

        _writeSessionFactory.Session.CommitCallCount.Should().Be(1);
        telemetry.Successes.Should().BeEmpty();
        telemetry.Failures.Should().BeEmpty();
    }

    [Test]
    public async Task It_records_DocumentCacheEnqueueTelemetry_success_for_exact_request_tenant_when_targets_share_data_store()
    {
        var telemetry = new RecordingDocumentCacheEnqueueTelemetry();
        DocumentCacheTargetKey peerTargetKey = DocumentCacheTargetKey.Create("Tenant-B", 99);
        _sut = CreateExecutor(
            documentCacheEnqueueTelemetry: telemetry,
            dataStoreSelection: CreateSelectedDataStoreSelection(),
            documentCacheTargetRegistry: CreateDocumentCacheTargetRegistry(
                CreateDocumentCacheTargetObservation(),
                CreateDocumentCacheTargetObservation(peerTargetKey)
            )
        );
        var request = CreateRequest(RelationalWriteOperationKind.Post, tenantKey: peerTargetKey.TenantKey);

        await _sut.ExecuteAsync(request);

        telemetry.Successes.Should().ContainSingle();
        telemetry.Successes[0].TargetKey.Should().Be(peerTargetKey);
    }

    [Test]
    public async Task It_records_DocumentCacheEnqueueTelemetry_failure_for_classified_enqueue_provider_errors()
    {
        var telemetry = new RecordingDocumentCacheEnqueueTelemetry();
        _sut = CreateExecutor(
            documentCacheEnqueueTelemetry: telemetry,
            dataStoreSelection: CreateSelectedDataStoreSelection(),
            documentCacheTargetRegistry: CreateDocumentCacheTargetRegistry()
        );
        var request = CreateRequest(
            RelationalWriteOperationKind.Put,
            tenantKey: DocumentCacheTelemetryTargetKey.TenantKey
        );
        _noProfileMergeSynthesizer.ResultToReturn = CreateMergeResult(
            request.WritePlan.TablePlansInDependencyOrder[0],
            currentSchoolId: 255901,
            mergedSchoolId: 255901,
            currentName: "Lincoln High",
            mergedName: "Lincoln High Updated"
        );
        _noProfilePersister.ExceptionToThrow = new StubDbException(
            "insert or update on table \"DocumentProjectionWork\" violates foreign key constraint"
        );
        _writeExceptionClassifier.ClassificationToReturn =
            new RelationalWriteExceptionClassification.ForeignKeyConstraintViolation(
                "FK_DocumentProjectionWork_Document"
            );

        await _sut.ExecuteAsync(request);

        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
        telemetry.Successes.Should().BeEmpty();
        telemetry.Failures.Should().ContainSingle();
        telemetry.Failures[0].Context.TargetKey.Should().Be(DocumentCacheTelemetryTargetKey);
        telemetry.Failures[0].Category.Should().Be(DocumentCacheEnqueueFailureCategory.WorkPersistenceFailed);
        telemetry
            .Failures[0]
            .Context.CanonicalOperation.Should()
            .Be(DocumentCacheEnqueueTelemetryCanonicalOperation.Update);
    }

    [Test]
    public async Task It_does_not_record_DocumentCacheEnqueueTelemetry_provider_timeout_without_enqueue_artifacts()
    {
        var telemetry = new RecordingDocumentCacheEnqueueTelemetry();
        _sut = CreateExecutor(
            documentCacheEnqueueTelemetry: telemetry,
            dataStoreSelection: CreateSelectedDataStoreSelection(),
            documentCacheTargetRegistry: CreateDocumentCacheTargetRegistry(),
            documentCacheProviderCommandTimeoutClassifier: new RecordingDocumentCacheProviderCommandTimeoutClassifier
            {
                IsProviderCommandTimeoutToReturn = true,
            }
        );
        var request = CreateRequest(
            RelationalWriteOperationKind.Put,
            tenantKey: DocumentCacheTelemetryTargetKey.TenantKey
        );
        _noProfileMergeSynthesizer.ResultToReturn = CreateMergeResult(
            request.WritePlan.TablePlansInDependencyOrder[0],
            currentSchoolId: 255901,
            mergedSchoolId: 255901,
            currentName: "Lincoln High",
            mergedName: "Lincoln High Updated"
        );
        _noProfilePersister.ExceptionToThrow = new StubDbException(
            "command timeout while applying the canonical write"
        );
        _writeExceptionClassifier.IsTransientFailureToReturn = true;

        var result = await _sut.ExecuteAsync(request);

        result
            .Should()
            .BeEquivalentTo(
                new RelationalWriteExecutorResult.Update(new UpdateResult.UpdateFailureWriteConflict())
            );
        telemetry.Failures.Should().BeEmpty();
    }

    [Test]
    public async Task It_records_DocumentCacheEnqueueTelemetry_transient_projection_work_failures_as_work_persistence_failures()
    {
        var telemetry = new RecordingDocumentCacheEnqueueTelemetry();
        _sut = CreateExecutor(
            documentCacheEnqueueTelemetry: telemetry,
            dataStoreSelection: CreateSelectedDataStoreSelection(),
            documentCacheTargetRegistry: CreateDocumentCacheTargetRegistry(),
            documentCacheProviderCommandTimeoutClassifier: new RecordingDocumentCacheProviderCommandTimeoutClassifier()
        );
        var request = CreateRequest(
            RelationalWriteOperationKind.Put,
            tenantKey: DocumentCacheTelemetryTargetKey.TenantKey
        );
        _noProfileMergeSynthesizer.ResultToReturn = CreateMergeResult(
            request.WritePlan.TablePlansInDependencyOrder[0],
            currentSchoolId: 255901,
            mergedSchoolId: 255901,
            currentName: "Lincoln High",
            mergedName: "Lincoln High Updated"
        );
        _noProfilePersister.ExceptionToThrow = new StubDbException(
            "deadlock detected while inserting into dms.DocumentProjectionWork"
        );
        _writeExceptionClassifier.IsTransientFailureToReturn = true;

        var result = await _sut.ExecuteAsync(request);

        result
            .Should()
            .BeEquivalentTo(
                new RelationalWriteExecutorResult.Update(new UpdateResult.UpdateFailureWriteConflict())
            );
        telemetry.Failures.Should().ContainSingle();
        telemetry.Failures[0].Category.Should().Be(DocumentCacheEnqueueFailureCategory.WorkPersistenceFailed);
    }

    [Test]
    public async Task It_does_not_record_DocumentCacheEnqueueTelemetry_failure_for_mapped_ordinary_write_failures()
    {
        var telemetry = new RecordingDocumentCacheEnqueueTelemetry();
        _sut = CreateExecutor(
            documentCacheEnqueueTelemetry: telemetry,
            dataStoreSelection: CreateSelectedDataStoreSelection(),
            documentCacheTargetRegistry: CreateDocumentCacheTargetRegistry()
        );
        var request = CreateRequest(
            RelationalWriteOperationKind.Post,
            tenantKey: DocumentCacheTelemetryTargetKey.TenantKey
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
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
        telemetry.Successes.Should().BeEmpty();
        telemetry.Failures.Should().BeEmpty();
    }

    [Test]
    public async Task It_does_not_record_DocumentCacheEnqueueTelemetry_failure_for_transient_canonical_write_failures_without_enqueue_artifacts()
    {
        var telemetry = new RecordingDocumentCacheEnqueueTelemetry();
        _sut = CreateExecutor(
            documentCacheEnqueueTelemetry: telemetry,
            dataStoreSelection: CreateSelectedDataStoreSelection(),
            documentCacheTargetRegistry: CreateDocumentCacheTargetRegistry()
        );
        var request = CreateRequest(
            RelationalWriteOperationKind.Put,
            tenantKey: DocumentCacheTelemetryTargetKey.TenantKey
        );
        _noProfileMergeSynthesizer.ResultToReturn = CreateMergeResult(
            request.WritePlan.TablePlansInDependencyOrder[0],
            currentSchoolId: 255901,
            mergedSchoolId: 255901,
            currentName: "Lincoln High",
            mergedName: "Lincoln High Updated"
        );
        _noProfilePersister.ExceptionToThrow = new StubDbException(
            "deadlock detected while updating the canonical School row"
        );
        _writeExceptionClassifier.IsTransientFailureToReturn = true;

        var result = await _sut.ExecuteAsync(request);

        result
            .Should()
            .BeEquivalentTo(
                new RelationalWriteExecutorResult.Update(new UpdateResult.UpdateFailureWriteConflict())
            );
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
        telemetry.Successes.Should().BeEmpty();
        telemetry.Failures.Should().BeEmpty();
    }

    [Test]
    public async Task It_records_DocumentCacheEnqueueTelemetry_failure_for_exact_request_tenant_when_targets_share_data_store()
    {
        var telemetry = new RecordingDocumentCacheEnqueueTelemetry();
        DocumentCacheTargetKey peerTargetKey = DocumentCacheTargetKey.Create("Tenant-B", 99);
        _sut = CreateExecutor(
            documentCacheEnqueueTelemetry: telemetry,
            dataStoreSelection: CreateSelectedDataStoreSelection(),
            documentCacheTargetRegistry: CreateDocumentCacheTargetRegistry(
                CreateDocumentCacheTargetObservation(),
                CreateDocumentCacheTargetObservation(peerTargetKey)
            )
        );
        var request = CreateRequest(RelationalWriteOperationKind.Put, tenantKey: peerTargetKey.TenantKey);
        _noProfileMergeSynthesizer.ResultToReturn = CreateMergeResult(
            request.WritePlan.TablePlansInDependencyOrder[0],
            currentSchoolId: 255901,
            mergedSchoolId: 255901,
            currentName: "Lincoln High",
            mergedName: "Lincoln High Updated"
        );
        _noProfilePersister.ExceptionToThrow = new StubDbException(
            "insert or update on table \"DocumentProjectionWork\" violates foreign key constraint"
        );
        _writeExceptionClassifier.ClassificationToReturn =
            new RelationalWriteExceptionClassification.ForeignKeyConstraintViolation(
                "FK_DocumentProjectionWork_Document"
            );

        await _sut.ExecuteAsync(request);

        telemetry.Failures.Should().ContainSingle();
        telemetry.Failures[0].Context.TargetKey.Should().Be(peerTargetKey);
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
        _targetLookupResolver.ResolveForPostCallCount.Should().Be(1);
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
        // Stored authorization plus reference resolution, both now on the session's executor.
        _writeSessionFactory.Session.CreateCommandExecutorCallCount.Should().Be(3);
        _writeSessionFactory.Session.RelationshipAuthorizationCommands.Should().ContainSingle();
        _referenceResolverAdapterFactory.CreateSessionAdapterCallCount.Should().Be(1);
        // The first phase always hydrates a surviving existing target's current state under the
        // capture lock, even when reference failures later short-circuit the attempt.
        _currentStateLoader.LoadCallCount.Should().Be(1);
        _writeFlattener.FlattenCallCount.Should().Be(0);
        _noProfilePersister.TryPersistCallCount.Should().Be(0);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_uses_provider_parameter_configurator_for_stored_relationship_authorization_inside_the_write_session()
    {
        var parameterConfigurator = new RecordingRelationalParameterConfigurator();
        _sut = CreateExecutor(relationalParameterConfigurator: parameterConfigurator);
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
        _sut = CreateExecutor(relationshipAuthorizationProviderFailureExtractor: providerFailureExtractor);
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
        _sut = CreateExecutor(
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
    public async Task It_returns_stored_relationship_no_claims_for_an_existing_put_without_a_second_observation()
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
        // The capture statement that observes the target also locks it, so no standalone lock
        // command is recorded on the session.
        _writeSessionFactory.Session.Commands.Should().BeEmpty();
        _writeSessionFactory.Session.CreateCommandExecutorCallCount.Should().Be(1);
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
        _writeSessionFactory.Session.Commands.Should().BeEmpty();
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
        _writeSessionFactory.Session.Commands.Should().BeEmpty();
        // Both verbs observe their target once at the start of the session, on the session's
        // command executor, before any authorization work.
        _writeSessionFactory.Session.CreateCommandExecutorCallCount.Should().Be(1);
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
        // In-session POST target lookup, stored authorization, and reference resolution, all now on
        // the session's executor.
        _writeSessionFactory.Session.CreateCommandExecutorCallCount.Should().Be(3);
        _writeSessionFactory.Session.RelationshipAuthorizationCommands.Should().ContainSingle();
        _referenceResolverAdapterFactory.CreateSessionAdapterCallCount.Should().Be(1);
        // The first phase always hydrates a surviving existing target's current state under the
        // capture lock, even when reference failures later short-circuit the attempt.
        _currentStateLoader.LoadCallCount.Should().Be(1);
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
                new DateTimeOffset(2026, 4, 2, 12, 0, 0, TimeSpan.Zero),
                1
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
        // PUT resolves its target once, at the start of the session, on the session's executor.
        _targetLookupResolver.ResolveForPutCallCount.Should().Be(1);
        _targetLookupResolver.ResolveForPostCallCount.Should().Be(0);
        _targetLookupResolver
            .CapturedCommandExecutor.Should()
            .BeSameAs(_writeSessionFactory.Session.RelationshipAuthorizationCommandExecutor);
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
        var persistedTarget = new RelationalWritePersistResult(910L, CreateDocumentUuid, 77L);
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
            request,
            45L,
            existingTarget: existingTargetContext
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
        _targetLookupResolver
            .CapturedCommandExecutor.Should()
            .BeSameAs(_writeSessionFactory.Session.RelationshipAuthorizationCommandExecutor);
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
        _targetLookupResolver.ResolveForPostCallCount.Should().Be(1);
        _writeSessionFactory.Session.CommitCallCount.Should().Be(1);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(0);
        _writeSessionFactory.Session.DisposeCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_uses_the_session_observed_content_version_when_guarding_unchanged_put_requests()
    {
        var request = CreateRequest(RelationalWriteOperationKind.Put);
        // The attempt's single in-session observation reports 45L, superseding the 44L default
        // arrangement; the capture lock keeps that observation current through commit.
        _targetLookupResolver.PutResults.Enqueue(
            new RelationalWriteTargetLookupResult.ExistingDocument(345L, UpdateDocumentUuid, 45L)
        );
        _currentStateLoader.ResultToReturn = CreateCurrentState(request, 45L);

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
        _currentStateLoader.CapturedRequest!.TargetContext.ObservedContentVersion.Should().Be(45L);
        _writeSessionFactory.Session.CommitCallCount.Should().Be(1);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(0);
    }

    [Test]
    public async Task It_uses_the_session_observed_content_version_when_guarding_unchanged_post_as_update_requests()
    {
        var request = CreateRequest(
            RelationalWriteOperationKind.Post,
            targetContext: new RelationalWriteTargetContext.ExistingDocument(
                345L,
                new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb")),
                44L
            )
        );
        // The attempt's single in-session observation reports 45L, superseding the 44L advisory
        // target context; the capture lock keeps that observation current through commit.
        _targetLookupResolver.PostResults.Enqueue(
            new RelationalWriteTargetLookupResult.ExistingDocument(345L, UpdateDocumentUuid, 45L)
        );
        _currentStateLoader.ResultToReturn = CreateCurrentState(request, 45L);

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
        _currentStateLoader.CapturedRequest!.TargetContext.ObservedContentVersion.Should().Be(45L);
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
                new DateTimeOffset(2026, 4, 2, 12, 0, 0, TimeSpan.Zero),
                1
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
        _sut = CreateExecutor(noProfileMergeSynthesizer: new RelationalWriteNoProfileMergeSynthesizer());

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
        _noProfilePersister.TryPersistCallCount.Should().Be(0);
        _writeSessionFactory.Session.CommitCallCount.Should().Be(1);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(0);
    }

    [Test]
    public async Task It_throws_when_current_state_hydration_returns_no_metadata_for_a_locked_put_target()
    {
        var request = CreateRequest(RelationalWriteOperationKind.Put);
        _currentStateLoader.ReturnMissingTarget = true;

        var act = () => _sut.ExecuteAsync(request);

        // The capture statement locks the observed row through commit, so an empty hydration can
        // no longer mean the row vanished; it is an invariant violation that rolls back and
        // rethrows.
        await act.Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("*Current-state hydration returned no metadata*");
        _currentStateLoader.LoadCallCount.Should().Be(1);
        _writeFlattener.FlattenCallCount.Should().Be(0);
        _writeSessionFactory.Session.CommitCallCount.Should().Be(0);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
        _writeSessionFactory.Session.DisposeCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_returns_if_match_failure_for_a_wildcard_put_when_the_target_is_missing()
    {
        // RFC 9110 §13.1.1 If-Match: * requires the target to exist; against a missing PUT target the
        // wildcard yields 412 (ETag mismatch) rather than 404 (not exists). The in-session
        // observation finding no row shapes the missing-PUT result immediately.
        var request = CreateRequest(
            RelationalWriteOperationKind.Put,
            writePrecondition: new WritePrecondition.IfMatch("some-wrong-value", IsWildcard: true)
        );
        _targetLookupResolver.PutResults.Enqueue(new RelationalWriteTargetLookupResult.NotFound());

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
        _referenceResolverAdapterFactory.CreateSessionAdapterCallCount.Should().Be(0);
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
        _targetLookupResolver.PutResults.Enqueue(new RelationalWriteTargetLookupResult.NotFound());

        var result = await _sut.ExecuteAsync(request);

        result
            .Should()
            .BeOfType<RelationalWriteExecutorResult.Update>()
            .Which.Result.Should()
            .BeOfType<UpdateResult.UpdateFailureNotExists>();
        _referenceResolverAdapterFactory.CreateSessionAdapterCallCount.Should().Be(0);
        _writeFlattener.FlattenCallCount.Should().Be(0);
        _currentStateLoader.LoadCallCount.Should().Be(0);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
        _writeSessionFactory.Session.DisposeCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_returns_if_match_failure_for_put_before_reference_failures_when_the_current_etag_mismatches()
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

        var result = await _sut.ExecuteAsync(request);

        // The target exists but its hydrated current state does not match the specific-tag If-Match
        // precondition, so the reason is Concurrency rather than TargetDoesNotExist.
        result
            .Should()
            .BeOfType<RelationalWriteExecutorResult.Update>()
            .Which.Result.Should()
            .BeOfType<UpdateResult.UpdateFailureETagMisMatch>()
            .Which.Reason.Should()
            .Be(ETagPreconditionFailureReason.Concurrency);
        // References resolve inside the first phase, but the mismatch verdict returns before the
        // missing document reference could surface as a reference failure.
        _referenceResolverAdapterFactory.CreateSessionAdapterCallCount.Should().Be(1);
        _writeFlattener.FlattenCallCount.Should().Be(0);
        _currentStateLoader.LoadCallCount.Should().Be(1);
        _noProfilePersister.TryPersistCallCount.Should().Be(0);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
        _writeSessionFactory.Session.DisposeCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_returns_if_match_failure_for_post_as_update_before_reference_failures_when_the_current_etag_mismatches()
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

        var result = await _sut.ExecuteAsync(request);

        result
            .Should()
            .BeEquivalentTo(
                new RelationalWriteExecutorResult.Upsert(new UpsertResult.UpsertFailureETagMisMatch())
            );
        _targetLookupResolver.ResolveForPostCallCount.Should().Be(1);
        // References resolve inside the first phase, but the mismatch verdict returns before the
        // missing document reference could surface as a reference failure.
        _referenceResolverAdapterFactory.CreateSessionAdapterCallCount.Should().Be(1);
        _writeFlattener.FlattenCallCount.Should().Be(0);
        _currentStateLoader.LoadCallCount.Should().Be(1);
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
        _targetLookupResolver
            .CapturedCommandExecutor.Should()
            .BeSameAs(_writeSessionFactory.Session.RelationshipAuthorizationCommandExecutor);
        _referenceResolverAdapterFactory.CreateSessionAdapterCallCount.Should().Be(1);
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
        _referenceResolverAdapterFactory.CreateSessionAdapterCallCount.Should().Be(1);
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
            selectedBody: JsonNode.Parse("""{"schoolId":255901,"name":"Lincoln High Updated"}""")!
        );
        // The precondition is evaluated client-side against the current state the first phase
        // hydrated under the capture lock, so arrange the loaded ContentVersion and an If-Match
        // value composed at that same version.
        _currentStateLoader.ResultToReturn = CreateCurrentState(request, 45L);
        request = request with
        {
            WritePrecondition = new WritePrecondition.IfMatch(ComposedCurrentEtag(request, 45L)),
        };
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
        _referenceResolverAdapterFactory.CreateSessionAdapterCallCount.Should().Be(1);
        _currentStateLoader.LoadCallCount.Should().Be(1);
        _noProfileMergeSynthesizer.CapturedRequest.Should().NotBeNull();
        _noProfileMergeSynthesizer
            .CapturedRequest!.CurrentState.Should()
            .BeSameAs(_currentStateLoader.ResultToReturn);
        _noProfilePersister.TryPersistCallCount.Should().Be(1);
        _writeSessionFactory.Session.CommitCallCount.Should().Be(1);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(0);
    }

    [Test]
    public async Task It_observes_the_post_create_target_once_on_the_session_before_any_other_work()
    {
        var request = CreateRequest(RelationalWriteOperationKind.Post);

        var result = await _sut.ExecuteAsync(request);

        result
            .Should()
            .BeOfType<RelationalWriteExecutorResult.Upsert>()
            .Which.Result.Should()
            .BeOfType<UpsertResult.InsertSuccess>();
        _targetLookupResolver.ResolveForPostCallCount.Should().Be(1);
        _targetLookupResolver.ResolveForPutCallCount.Should().Be(0);
        _targetLookupResolver
            .CapturedCommandExecutor.Should()
            .BeSameAs(_writeSessionFactory.Session.RelationshipAuthorizationCommandExecutor);
        _writeSessionFactory.CreateAsyncCallCount.Should().Be(1);
        _writeSessionFactory.Session.CommitCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_observes_the_post_existing_target_once_on_the_session()
    {
        var existingDocumentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));
        var request = CreateRequest(
            RelationalWriteOperationKind.Post,
            targetContext: new RelationalWriteTargetContext.ExistingDocument(345L, existingDocumentUuid, 44L)
        );
        _currentStateLoader.ResultToReturn = CreateCurrentState(request, 44L);

        var result = await _sut.ExecuteAsync(request);

        result
            .Should()
            .BeOfType<RelationalWriteExecutorResult.Upsert>()
            .Which.Result.Should()
            .BeOfType<UpsertResult.UpdateSuccess>();
        _targetLookupResolver.ResolveForPostCallCount.Should().Be(1);
        _targetLookupResolver.ResolveForPutCallCount.Should().Be(0);
        _currentStateLoader.LoadCallCount.Should().Be(1);
        _writeSessionFactory.Session.CommitCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_observes_the_put_target_once_on_the_session()
    {
        var request = CreateRequest(RelationalWriteOperationKind.Put);
        _currentStateLoader.ResultToReturn = CreateCurrentState(request, 44L);

        var result = await _sut.ExecuteAsync(request);

        result
            .Should()
            .BeOfType<RelationalWriteExecutorResult.Update>()
            .Which.Result.Should()
            .BeOfType<UpdateResult.UpdateSuccess>();
        _targetLookupResolver.ResolveForPutCallCount.Should().Be(1);
        _targetLookupResolver.ResolveForPostCallCount.Should().Be(0);
        _targetLookupResolver
            .CapturedCommandExecutor.Should()
            .BeSameAs(_writeSessionFactory.Session.RelationshipAuthorizationCommandExecutor);
        _writeSessionFactory.Session.CommitCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_returns_not_exists_and_rolls_back_when_the_session_finds_no_put_target()
    {
        var request = CreateRequest(RelationalWriteOperationKind.Put);
        _targetLookupResolver.PutResults.Enqueue(new RelationalWriteTargetLookupResult.NotFound());

        var result = await _sut.ExecuteAsync(request);

        result
            .Should()
            .BeEquivalentTo(
                new RelationalWriteExecutorResult.Update(new UpdateResult.UpdateFailureNotExists())
            );
        _targetLookupResolver.ResolveForPutCallCount.Should().Be(1);
        // Nothing downstream of the observation runs for a target that does not exist.
        _referenceResolverAdapterFactory.CreateSessionAdapterCallCount.Should().Be(0);
        _currentStateLoader.LoadCallCount.Should().Be(0);
        _writeFlattener.FlattenCallCount.Should().Be(0);
        _noProfileMergeSynthesizer.SynthesizeCallCount.Should().Be(0);
        _noProfilePersister.TryPersistCallCount.Should().Be(0);
        _writeSessionFactory.Session.CommitCallCount.Should().Be(0);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
        _writeSessionFactory.Session.DisposeCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_returns_precondition_failed_and_rolls_back_when_a_wildcard_if_match_put_target_is_missing()
    {
        var request = CreateRequest(
            RelationalWriteOperationKind.Put,
            writePrecondition: new WritePrecondition.IfMatch("*", IsWildcard: true)
        );
        _targetLookupResolver.PutResults.Enqueue(new RelationalWriteTargetLookupResult.NotFound());

        var result = await _sut.ExecuteAsync(request);

        // RFC 9110 13.1.1: a wildcard If-Match against a missing target is a precondition failure,
        // not a not-exists result.
        result
            .Should()
            .BeOfType<RelationalWriteExecutorResult.Update>()
            .Which.Result.Should()
            .BeOfType<UpdateResult.UpdateFailureETagMisMatch>()
            .Which.Reason.Should()
            .Be(ETagPreconditionFailureReason.TargetDoesNotExist);
        _targetLookupResolver.ResolveForPutCallCount.Should().Be(1);
        _referenceResolverAdapterFactory.CreateSessionAdapterCallCount.Should().Be(0);
        _currentStateLoader.LoadCallCount.Should().Be(0);
        _writeFlattener.FlattenCallCount.Should().Be(0);
        _noProfilePersister.TryPersistCallCount.Should().Be(0);
        _writeSessionFactory.Session.CommitCallCount.Should().Be(0);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_does_not_observe_the_post_target_again_when_an_etag_precondition_is_present()
    {
        var existingDocumentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));
        var request = CreateRequest(
            RelationalWriteOperationKind.Post,
            targetContext: new RelationalWriteTargetContext.ExistingDocument(345L, existingDocumentUuid, 44L)
        );
        request = request with
        {
            WritePrecondition = new WritePrecondition.IfMatch(
                ComposedCurrentEtag(request, 44L),
                IsWildcard: false
            ),
        };

        var result = await _sut.ExecuteAsync(request);

        result
            .Should()
            .BeOfType<RelationalWriteExecutorResult.Upsert>()
            .Which.Result.Should()
            .BeOfType<UpsertResult.UpdateSuccess>();
        // The precondition path used to trigger its own POST lookup; the initial observation now
        // serves it, so the attempt still observes the target exactly once.
        _targetLookupResolver.ResolveForPostCallCount.Should().Be(1);
        _writeSessionFactory.Session.CommitCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_returns_stored_relationship_no_claims_for_an_observed_post_target_without_a_second_observation()
    {
        var existingDocumentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));
        var request = CreateRequest(
            RelationalWriteOperationKind.Post,
            targetContext: new RelationalWriteTargetContext.ExistingDocument(345L, existingDocumentUuid, 44L)
        );

        var result = await _sut.ExecuteAsync(
            request with
            {
                StoredRelationshipAuthorization = CreateStoredSchoolIdNoClaimsAuthorization(request),
            }
        );

        result
            .Should()
            .BeOfType<RelationalWriteExecutorResult.Upsert>()
            .Which.Result.Should()
            .BeOfType<UpsertResult.UpsertFailureRelationshipNotAuthorized>();
        // One observation, then the denial; the capture statement that observed the target also
        // locked it, so no standalone lock command is recorded on the session.
        _targetLookupResolver.ResolveForPostCallCount.Should().Be(1);
        _writeSessionFactory.Session.Commands.Should().BeEmpty();
        _currentStateLoader.LoadCallCount.Should().Be(0);
        _writeFlattener.FlattenCallCount.Should().Be(0);
        _noProfilePersister.TryPersistCallCount.Should().Be(0);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_throws_when_current_state_hydration_returns_no_metadata_for_a_locked_post_target()
    {
        var request = CreateRequest(
            RelationalWriteOperationKind.Post,
            targetContext: new RelationalWriteTargetContext.ExistingDocument(
                345L,
                new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb")),
                44L
            )
        );
        _currentStateLoader.ReturnMissingTarget = true;

        var act = () => _sut.ExecuteAsync(request);

        // The capture statement locks the observed row through commit, so an empty hydration can
        // no longer mean the row vanished; it is an invariant violation that rolls back and
        // rethrows.
        await act.Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("*Current-state hydration returned no metadata*");
        _targetLookupResolver.ResolveForPostCallCount.Should().Be(1);
        _currentStateLoader.LoadCallCount.Should().Be(1);
        _writeFlattener.FlattenCallCount.Should().Be(0);
        _noProfilePersister.TryPersistCallCount.Should().Be(0);
        _writeSessionFactory.Session.CommitCallCount.Should().Be(0);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_reuses_the_same_write_session_for_the_post_target_observation_and_current_state_load()
    {
        var existingDocumentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));
        var request = CreateRequest(
            RelationalWriteOperationKind.Post,
            targetContext: new RelationalWriteTargetContext.ExistingDocument(345L, existingDocumentUuid, 45L)
        );

        _currentStateLoader.QueuedResults.Enqueue(
            new RelationalWriteCurrentState(
                new DocumentMetadataRow(
                    345L,
                    existingDocumentUuid.Value,
                    45L,
                    45L,
                    new DateTimeOffset(2026, 4, 2, 12, 0, 0, TimeSpan.Zero),
                    new DateTimeOffset(2026, 4, 2, 12, 0, 0, TimeSpan.Zero),
                    1
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
        // One observation, one current-state load, both on the session the executor opened.
        _currentStateLoader.LoadCallCount.Should().Be(1);
        _currentStateLoader.CapturedRequests.Should().ContainSingle();
        _currentStateLoader.CapturedRequests[0].TargetContext.ObservedContentVersion.Should().Be(45L);
        _currentStateLoader.CapturedWriteSessions.Should().ContainSingle();
        _currentStateLoader
            .CapturedWriteSessions.Should()
            .OnlyContain(writeSession => ReferenceEquals(writeSession, _writeSessionFactory.Session));
        _targetLookupResolver.ResolveForPostCallCount.Should().Be(1);
        _currentStateLoader
            .CapturedRequests[0]
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
            .CapturedCommandExecutor.Should()
            .BeSameAs(_writeSessionFactory.Session.RelationshipAuthorizationCommandExecutor);
        _writeSessionFactory.Session.CommitCallCount.Should().Be(1);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(0);
    }

    [Test]
    public async Task It_maps_a_create_landing_after_the_post_observation_to_write_conflict_without_updating()
    {
        var request = CreateRequest(
            RelationalWriteOperationKind.Post,
            selectedBody: JsonNode.Parse(
                """
                {"schoolId":255901,"name":"Lincoln High"}
                """
            )!
        );
        var candidateDocumentUuid = (
            (RelationalWriteTargetRequest.Post)request.TargetRequest
        ).CandidateDocumentUuid;
        _noProfileMergeSynthesizer.ResultToReturn = CreateMergeResult(
            request.WritePlan.TablePlansInDependencyOrder[0],
            currentSchoolId: 255901,
            mergedSchoolId: 255901,
            currentName: "Lincoln High",
            mergedName: "Lincoln High Updated"
        );
        _noProfilePersister.ExceptionToThrow = new StubDbException("concurrent duplicate key");
        _writeExceptionClassifier.ClassificationToReturn =
            new RelationalWriteExceptionClassification.UniqueConstraintViolation("UK_School_NaturalKey");
        _writeConstraintResolver.ResolutionToReturn =
            new RelationalWriteConstraintResolution.RootNaturalKeyUnique("UK_School_NaturalKey");

        var result = await _sut.ExecuteAsync(request);

        // The initial observation saw no row, so this attempt stays an insert. A create that commits
        // afterwards is not re-observed into an update; it surfaces as the natural-key conflict the
        // insert hits, mapped to the existing write-conflict result.
        result
            .Should()
            .BeEquivalentTo(
                new RelationalWriteExecutorResult.Upsert(new UpsertResult.UpsertFailureWriteConflict())
            );
        _targetLookupResolver.ResolveForPostCallCount.Should().Be(1);
        _writeFlattener
            .CapturedInput!.TargetContext.Should()
            .BeEquivalentTo(new RelationalWriteTargetContext.CreateNew(candidateDocumentUuid));
        _noProfilePersister.TryPersistCallCount.Should().Be(1);
        _noProfilePersister
            .CapturedRequest!.TargetContext.Should()
            .BeOfType<RelationalWriteTargetContext.CreateNew>();
        _writeSessionFactory.Session.CommitCallCount.Should().Be(0);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_permits_a_post_insert_under_an_if_none_match_wildcard()
    {
        // If-None-Match: * on an insert (CreateNew) is the create-only success case: it proceeds, the
        // exact inverse of If-Match which 412s on CreateNew.
        var request = CreateRequest(
            RelationalWriteOperationKind.Post,
            writePrecondition: new WritePrecondition.IfNoneMatch("*", IsWildcard: true)
        );

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
        _targetLookupResolver.ResolveForPostCallCount.Should().Be(1);
        _noProfilePersister.TryPersistCallCount.Should().Be(1);
        _writeSessionFactory.Session.CommitCallCount.Should().Be(1);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(0);
    }

    [Test]
    public async Task It_permits_a_post_insert_under_an_if_none_match_specific_tag()
    {
        var request = CreateRequest(
            RelationalWriteOperationKind.Post,
            writePrecondition: new WritePrecondition.IfNoneMatch("\"5-abc\"")
        );

        var result = await _sut.ExecuteAsync(request);

        result
            .Should()
            .BeOfType<RelationalWriteExecutorResult.Upsert>()
            .Which.Result.Should()
            .BeOfType<UpsertResult.InsertSuccess>();
        _writeSessionFactory.Session.CommitCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_returns_if_match_failure_for_a_post_insert_under_if_match_wildcard()
    {
        // Regression: If-Match: * on an insert must still 412 (unchanged inverse of If-None-Match).
        var request = CreateRequest(
            RelationalWriteOperationKind.Post,
            writePrecondition: new WritePrecondition.IfMatch("*", IsWildcard: true)
        );

        var result = await _sut.ExecuteAsync(request);

        result
            .Should()
            .BeOfType<RelationalWriteExecutorResult.Upsert>()
            .Which.Result.Should()
            .BeOfType<UpsertResult.UpsertFailureETagMisMatch>();
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_returns_precondition_failure_for_post_as_update_under_if_none_match_when_the_target_exists()
    {
        // POST resolving to an existing target under If-None-Match: the before-auth gate evaluates
        // the precondition against the hydrated current state and reports not-satisfied → 412.
        var existingDocumentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));
        var request = CreateRequest(
            RelationalWriteOperationKind.Post,
            targetContext: new RelationalWriteTargetContext.ExistingDocument(345L, existingDocumentUuid, 44L),
            writePrecondition: new WritePrecondition.IfNoneMatch("*", IsWildcard: true)
        );
        _targetLookupResolver.PostResults.Enqueue(
            new RelationalWriteTargetLookupResult.ExistingDocument(345L, existingDocumentUuid, 44L)
        );

        var result = await _sut.ExecuteAsync(request);

        result
            .Should()
            .BeEquivalentTo(
                new RelationalWriteExecutorResult.Upsert(
                    new UpsertResult.UpsertFailureETagMisMatch(
                        ETagPreconditionFailureReason.CurrentRepresentationMatchesIfNoneMatch
                    )
                )
            );
        _currentStateLoader.LoadCallCount.Should().Be(1);
        _noProfilePersister.TryPersistCallCount.Should().Be(0);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_never_reaches_the_guarded_no_op_check_for_a_post_as_update_no_op_body_under_if_none_match_wildcard()
    {
        // Regression (B7): If-None-Match: * against an EXISTING row with an UNCHANGED (no-op) body must
        // 412 at the precondition check itself, upstream of the guarded no-op machinery. Proven by
        // asserting the merge synthesizer is never invoked — if the precondition check were skipped
        // or deferred, this request (default no-op body against the default-named current state)
        // would instead flow into the guarded-no-op branch.
        var existingDocumentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));
        var request = CreateRequest(
            RelationalWriteOperationKind.Post,
            targetContext: new RelationalWriteTargetContext.ExistingDocument(345L, existingDocumentUuid, 44L),
            writePrecondition: new WritePrecondition.IfNoneMatch("*", IsWildcard: true)
        );
        _targetLookupResolver.PostResults.Enqueue(
            new RelationalWriteTargetLookupResult.ExistingDocument(345L, existingDocumentUuid, 44L)
        );

        var result = await _sut.ExecuteAsync(request);

        result
            .Should()
            .BeEquivalentTo(
                new RelationalWriteExecutorResult.Upsert(
                    new UpsertResult.UpsertFailureETagMisMatch(
                        ETagPreconditionFailureReason.CurrentRepresentationMatchesIfNoneMatch
                    )
                )
            );
        result.AttemptOutcome.Should().NotBe(RelationalWriteExecutorAttemptOutcome.GuardedNoOp.Instance);
        _currentStateLoader.LoadCallCount.Should().Be(1);
        _noProfileMergeSynthesizer.SynthesizeCallCount.Should().Be(0);
        _noProfilePersister.TryPersistCallCount.Should().Be(0);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_applies_a_post_as_update_under_if_none_match_when_the_precondition_is_satisfied()
    {
        // A non-matching If-None-Match tag against an existing target is satisfied (client copy stale),
        // so the write proceeds as an update.
        var existingDocumentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));
        var request = CreateRequest(
            RelationalWriteOperationKind.Post,
            selectedBody: JsonNode.Parse("""{"schoolId":255901,"name":"Lincoln High Updated"}""")!,
            targetContext: new RelationalWriteTargetContext.ExistingDocument(345L, existingDocumentUuid, 44L),
            writePrecondition: new WritePrecondition.IfNoneMatch("\"stale-client-tag\"")
        );
        _targetLookupResolver.PostResults.Enqueue(
            new RelationalWriteTargetLookupResult.ExistingDocument(345L, existingDocumentUuid, 44L)
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
            .BeOfType<RelationalWriteExecutorResult.Upsert>()
            .Which.Result.Should()
            .BeOfType<UpsertResult.UpdateSuccess>();
        _currentStateLoader.LoadCallCount.Should().Be(1);
        _writeSessionFactory.Session.CommitCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_returns_precondition_failure_for_an_existing_put_under_if_none_match_wildcard()
    {
        var request = CreateRequest(
            RelationalWriteOperationKind.Put,
            writePrecondition: new WritePrecondition.IfNoneMatch("*", IsWildcard: true)
        );

        var result = await _sut.ExecuteAsync(request);

        result
            .Should()
            .BeEquivalentTo(
                new RelationalWriteExecutorResult.Update(
                    new UpdateResult.UpdateFailureETagMisMatch(
                        ETagPreconditionFailureReason.CurrentRepresentationMatchesIfNoneMatch
                    )
                )
            );
        _currentStateLoader.LoadCallCount.Should().Be(1);
        _noProfilePersister.TryPersistCallCount.Should().Be(0);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_never_reaches_the_guarded_no_op_check_for_a_put_no_op_body_under_if_none_match_wildcard()
    {
        // Regression (B7): mirrors the POST case above for PUT. If-None-Match: * against an EXISTING row
        // with an UNCHANGED (no-op) body 412s at the precondition check, never routing through the
        // guarded no-op path — proven by the merge synthesizer never being invoked.
        var request = CreateRequest(
            RelationalWriteOperationKind.Put,
            writePrecondition: new WritePrecondition.IfNoneMatch("*", IsWildcard: true)
        );

        var result = await _sut.ExecuteAsync(request);

        result
            .Should()
            .BeEquivalentTo(
                new RelationalWriteExecutorResult.Update(
                    new UpdateResult.UpdateFailureETagMisMatch(
                        ETagPreconditionFailureReason.CurrentRepresentationMatchesIfNoneMatch
                    )
                )
            );
        result.AttemptOutcome.Should().NotBe(RelationalWriteExecutorAttemptOutcome.GuardedNoOp.Instance);
        _currentStateLoader.LoadCallCount.Should().Be(1);
        _noProfileMergeSynthesizer.SynthesizeCallCount.Should().Be(0);
        _noProfilePersister.TryPersistCallCount.Should().Be(0);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_applies_an_existing_put_under_if_none_match_when_the_precondition_is_satisfied()
    {
        var request = CreateRequest(
            RelationalWriteOperationKind.Put,
            selectedBody: JsonNode.Parse("""{"schoolId":255901,"name":"Lincoln High Updated"}""")!,
            writePrecondition: new WritePrecondition.IfNoneMatch("\"stale-client-tag\"")
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
            .BeOfType<RelationalWriteExecutorResult.Update>()
            .Which.Result.Should()
            .BeOfType<UpdateResult.UpdateSuccess>();
        _currentStateLoader.LoadCallCount.Should().Be(1);
        _writeSessionFactory.Session.CommitCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_returns_not_exists_for_a_missing_put_under_if_none_match_wildcard()
    {
        // Contrast with If-Match: * (which 412s a missing PUT): If-None-Match against a missing target
        // is the success case and yields the normal 404, never 412.
        var request = CreateRequest(
            RelationalWriteOperationKind.Put,
            writePrecondition: new WritePrecondition.IfNoneMatch("*", IsWildcard: true)
        );
        _targetLookupResolver.PutResults.Enqueue(new RelationalWriteTargetLookupResult.NotFound());

        var result = await _sut.ExecuteAsync(request);

        result
            .Should()
            .BeOfType<RelationalWriteExecutorResult.Update>()
            .Which.Result.Should()
            .BeOfType<UpdateResult.UpdateFailureNotExists>();
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_returns_not_exists_for_a_missing_put_under_if_none_match_specific_tag()
    {
        var request = CreateRequest(
            RelationalWriteOperationKind.Put,
            writePrecondition: new WritePrecondition.IfNoneMatch("\"5-abc\"")
        );
        _targetLookupResolver.PutResults.Enqueue(new RelationalWriteTargetLookupResult.NotFound());

        var result = await _sut.ExecuteAsync(request);

        result
            .Should()
            .BeOfType<RelationalWriteExecutorResult.Update>()
            .Which.Result.Should()
            .BeOfType<UpdateResult.UpdateFailureNotExists>();
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_returns_precondition_failure_on_the_deferred_path_for_an_existing_put_under_if_none_match()
    {
        // FAIL-OPEN REGRESSION: an authorization boundary defers precondition evaluation to after
        // proposed authorization. Before the line-107 guard was widened, If-None-Match dropped out of
        // TryBuildDeferredPreconditionFailureResult and the write proceeded WITHOUT a 412. This proves
        // the deferred path honors the create-guard.
        var request = CreateRequest(
            RelationalWriteOperationKind.Put,
            writePrecondition: new WritePrecondition.IfNoneMatch("*", IsWildcard: true)
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
                new RelationalWriteExecutorResult.Update(
                    new UpdateResult.UpdateFailureETagMisMatch(
                        ETagPreconditionFailureReason.CurrentRepresentationMatchesIfNoneMatch
                    )
                )
            );
        // The deferred path evaluates the precondition against the hydrated current state.
        _currentStateLoader.LoadCallCount.Should().Be(1);
        _noProfilePersister.AuthorizeProposedRelationshipCallCount.Should().Be(1);
        _noProfilePersister.TryPersistCallCount.Should().Be(0);
        _writeSessionFactory.Session.CommitCallCount.Should().Be(0);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_permits_a_deferred_post_insert_under_if_none_match_wildcard()
    {
        // The deferred CreateNew arm proceeds for If-None-Match (create-only success) after successful
        // proposed authorization, the inverse of If-Match which 412s here.
        var request = CreateRequest(
            RelationalWriteOperationKind.Post,
            writePrecondition: new WritePrecondition.IfNoneMatch("*", IsWildcard: true)
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
        _writeSessionFactory.Session.CommitCallCount.Should().Be(1);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(0);
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
    public async Task It_maps_a_losing_IfNoneMatch_wildcard_create_race_to_a_retryable_write_conflict()
    {
        var request = CreateRequest(
            RelationalWriteOperationKind.Post,
            selectedBody: JsonNode.Parse("""{"schoolId":255901,"name":"Lincoln High"}""")!,
            writePrecondition: new WritePrecondition.IfNoneMatch("*", IsWildcard: true)
        );
        _noProfileMergeSynthesizer.ResultToReturn = CreateMergeResult(
            request.WritePlan.TablePlansInDependencyOrder[0],
            currentSchoolId: 255901,
            mergedSchoolId: 255901,
            currentName: "Lincoln High",
            mergedName: "Lincoln High Updated"
        );
        _noProfilePersister.ExceptionToThrow = new StubDbException("concurrent duplicate key");
        _writeExceptionClassifier.ClassificationToReturn =
            new RelationalWriteExceptionClassification.UniqueConstraintViolation("UK_School_NaturalKey");
        _writeConstraintResolver.ResolutionToReturn =
            new RelationalWriteConstraintResolution.RootNaturalKeyUnique("UK_School_NaturalKey");

        var result = await _sut.ExecuteAsync(request);

        result
            .Should()
            .BeEquivalentTo(
                new RelationalWriteExecutorResult.Upsert(new UpsertResult.UpsertFailureWriteConflict())
            );
        _noProfilePersister.TryPersistCallCount.Should().Be(1);
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
        // A commit-phase failure is still classified, but it is never rolled back: the server may have
        // committed and only failed to acknowledge it.
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(0);
    }

    [Test]
    public async Task It_does_not_roll_back_an_applied_write_whose_commit_failure_is_unmapped()
    {
        var telemetry = new RecordingDocumentCacheEnqueueTelemetry();
        _sut = CreateExecutor(
            documentCacheEnqueueTelemetry: telemetry,
            dataStoreSelection: CreateSelectedDataStoreSelection(),
            documentCacheTargetRegistry: CreateDocumentCacheTargetRegistry()
        );
        var request = CreateRequest(
            RelationalWriteOperationKind.Put,
            selectedBody: JsonNode.Parse("""{"schoolId":255901,"name":"Lincoln High"}""")!,
            tenantKey: DocumentCacheTelemetryTargetKey.TenantKey
        );
        _noProfileMergeSynthesizer.ResultToReturn = CreateMergeResult(
            request.WritePlan.TablePlansInDependencyOrder[0],
            currentSchoolId: 255901,
            mergedSchoolId: 255901,
            currentName: "Lincoln High",
            mergedName: "Lincoln High Updated"
        );
        _writeSessionFactory.Session.CommitExceptionToThrow = new StubDbException(
            "connection reset on commit"
        );

        var act = () => _sut.ExecuteAsync(request);

        // The unmapped failure surfaces unchanged. A client-side rollback here could only fail against
        // a transaction the server has already completed, replacing this failure with an unrelated
        // one. Disposing the session settles whatever state is still pending instead.
        await act.Should().ThrowAsync<StubDbException>().WithMessage("connection reset on commit");
        _writeSessionFactory.Session.CommitCallCount.Should().Be(1);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(0);
        _writeSessionFactory.Session.DisposeCallCount.Should().Be(1);
        telemetry.Successes.Should().BeEmpty();
        telemetry.Failures.Should().BeEmpty();
    }

    /// <summary>
    /// A commit that fails on the client - a command timeout, a connection lost waiting for the
    /// acknowledgement - may have been applied in full by the server. Handing the retry pipeline a
    /// write conflict would replay it, and a replayed write answers for the state the first attempt
    /// already produced: a re-run DELETE reads as 404, a re-run conditional write as 412. Reporting
    /// a transient database condition as a client error is the defect this whole change exists to
    /// remove, so an indeterminate commit must stay off the retry path.
    /// </summary>
    [TestCase(RelationalWriteOperationKind.Post)]
    [TestCase(RelationalWriteOperationKind.Put)]
    public async Task It_does_not_retry_a_commit_whose_outcome_is_indeterminate(
        RelationalWriteOperationKind operationKind
    )
    {
        var telemetry = new RecordingDocumentCacheEnqueueTelemetry();
        _sut = CreateExecutor(
            documentCacheEnqueueTelemetry: telemetry,
            dataStoreSelection: CreateSelectedDataStoreSelection(),
            documentCacheTargetRegistry: CreateDocumentCacheTargetRegistry(),
            documentCacheProviderCommandTimeoutClassifier: new RecordingDocumentCacheProviderCommandTimeoutClassifier
            {
                IsProviderCommandTimeoutToReturn = true,
            }
        );
        var request = CreateRequest(
            operationKind,
            selectedBody: JsonNode.Parse("""{"schoolId":255901,"name":"Lincoln High"}""")!,
            tenantKey: DocumentCacheTelemetryTargetKey.TenantKey
        );
        _noProfileMergeSynthesizer.ResultToReturn = CreateMergeResult(
            request.WritePlan.TablePlansInDependencyOrder[0],
            currentSchoolId: 255901,
            mergedSchoolId: 255901,
            currentName: "Lincoln High",
            mergedName: "Lincoln High Updated"
        );
        _writeExceptionClassifier.IsTransientFailureToReturn = false;
        _writeExceptionClassifier.ClassificationToReturn = RelationalWriteExceptionClassification
            .IndeterminateOutcomeFailure
            .Instance;
        _writeSessionFactory.Session.CommitExceptionToThrow = new StubDbException(
            "Execution Timeout Expired."
        );

        var result = await _sut.ExecuteAsync(request);

        using (new AssertionScope())
        {
            switch (operationKind)
            {
                case RelationalWriteOperationKind.Post:
                    var upsert = result.Should().BeOfType<RelationalWriteExecutorResult.Upsert>().Subject;
                    upsert.Result.Should().BeOfType<UpsertResult.UnknownFailure>();
                    upsert.Result.Should().NotBeOfType<UpsertResult.UpsertFailureWriteConflict>();
                    break;
                case RelationalWriteOperationKind.Put:
                    var update = result.Should().BeOfType<RelationalWriteExecutorResult.Update>().Subject;
                    update.Result.Should().BeOfType<UpdateResult.UnknownFailure>();
                    update.Result.Should().NotBeOfType<UpdateResult.UpdateFailureWriteConflict>();
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(operationKind), operationKind, null);
            }

            _writeSessionFactory.Session.CommitCallCount.Should().Be(1);
            telemetry.Successes.Should().BeEmpty();
            telemetry.Failures.Should().BeEmpty();
        }
    }

    [Test]
    public async Task It_does_not_roll_back_a_guarded_no_op_whose_commit_fails()
    {
        var telemetry = new RecordingDocumentCacheEnqueueTelemetry();
        _sut = CreateExecutor(
            documentCacheEnqueueTelemetry: telemetry,
            dataStoreSelection: CreateSelectedDataStoreSelection(),
            documentCacheTargetRegistry: CreateDocumentCacheTargetRegistry()
        );
        var request = CreateRequest(
            RelationalWriteOperationKind.Put,
            selectedBody: JsonNode.Parse("""{"name":"Lincoln High"}""")!,
            tenantKey: DocumentCacheTelemetryTargetKey.TenantKey
        );
        _writeSessionFactory.Session.CommitExceptionToThrow = new StubDbException(
            "connection reset on commit"
        );

        var act = () => _sut.ExecuteAsync(request);

        // The guarded no-op path commits without DML, so it reaches the same ambiguous commit state.
        await act.Should().ThrowAsync<StubDbException>().WithMessage("connection reset on commit");
        _noProfilePersister.TryPersistCallCount.Should().Be(0);
        _writeSessionFactory.Session.CommitCallCount.Should().Be(1);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(0);
        _writeSessionFactory.Session.DisposeCallCount.Should().Be(1);
        telemetry.Successes.Should().BeEmpty();
        telemetry.Failures.Should().BeEmpty();
    }

    [Test]
    public async Task It_does_not_roll_back_when_a_commit_fails_with_a_non_database_exception()
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
        _writeSessionFactory.Session.CommitExceptionToThrow = new InvalidOperationException(
            "commit already began"
        );

        var act = () => _sut.ExecuteAsync(request);

        // The catch-all handler is as unable to roll back a begun commit as the database-failure one.
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("commit already began");
        _writeSessionFactory.Session.CommitCallCount.Should().Be(1);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(0);
        _writeSessionFactory.Session.DisposeCallCount.Should().Be(1);
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

    /// <summary>
    /// An unrecognized write failure is shaped into a generic message, so unless the provider
    /// exception is logged here the engine's own error number and text are lost and the only way to
    /// learn why a write failed is to attach a debugger to a production incident.
    /// </summary>
    [Test]
    public async Task It_logs_the_provider_exception_behind_an_unrecognized_write_failure()
    {
        CapturingLogger<DefaultRelationalWriteExecutor> logger = new();
        _sut = CreateExecutor(logger: logger);
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

        await _sut.ExecuteAsync(request);

        logger.JoinedMessages().Should().Contain("provider write failure");
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

    [TestCase(RelationalWriteOperationKind.Post)]
    [TestCase(RelationalWriteOperationKind.Put)]
    public async Task It_maps_transient_canonical_write_db_failures_to_retryable_write_conflicts(
        RelationalWriteOperationKind operationKind
    )
    {
        var request = CreateRequest(operationKind);
        _noProfileMergeSynthesizer.ResultToReturn = CreateMergeResult(
            request.WritePlan.TablePlansInDependencyOrder[0],
            currentSchoolId: 255901,
            mergedSchoolId: 255901,
            currentName: "Lincoln High",
            mergedName: "Lincoln High Updated"
        );
        _noProfilePersister.ExceptionToThrow = new StubDbException("canonical enqueue lock timeout");
        _writeExceptionClassifier.IsTransientFailureToReturn = true;

        var result = await _sut.ExecuteAsync(request);

        switch (operationKind)
        {
            case RelationalWriteOperationKind.Post:
                result
                    .Should()
                    .BeEquivalentTo(
                        new RelationalWriteExecutorResult.Upsert(
                            new UpsertResult.UpsertFailureWriteConflict()
                        )
                    );
                break;
            case RelationalWriteOperationKind.Put:
                result
                    .Should()
                    .BeEquivalentTo(
                        new RelationalWriteExecutorResult.Update(
                            new UpdateResult.UpdateFailureWriteConflict()
                        )
                    );
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(operationKind), operationKind, null);
        }

        _noProfilePersister.TryPersistCallCount.Should().Be(1);
        _writeExceptionClassifier.IsTransientFailureCallCount.Should().Be(1);
        _writeExceptionClassifier.TryClassifyCallCount.Should().Be(0);
        _writeConstraintResolver.ResolveCallCount.Should().Be(0);
        _writeSessionFactory.Session.CommitCallCount.Should().Be(0);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
    }

    /// <summary>
    /// The first phase captures and locks the target, so a deadlock victim is at least as likely
    /// there as in the write itself. No execution request is resolved yet, but transience does not
    /// depend on one, so the failure maps to the same retryable write conflict a second-phase
    /// failure produces rather than escaping the executor as an unhandled exception.
    /// </summary>
    [TestCase(RelationalWriteOperationKind.Post)]
    [TestCase(RelationalWriteOperationKind.Put)]
    public async Task It_maps_transient_first_phase_db_failures_to_retryable_write_conflicts(
        RelationalWriteOperationKind operationKind
    )
    {
        var request = CreateRequest(operationKind);
        _writeExceptionClassifier.IsTransientFailureToReturn = true;
        _targetLookupResolver.ExceptionToThrow = new StubDbException(
            "Transaction (Process ID 112) was deadlocked on lock resources with another process "
                + "and has been chosen as the deadlock victim. Rerun the transaction."
        );

        var result = await _sut.ExecuteAsync(request);

        switch (operationKind)
        {
            case RelationalWriteOperationKind.Post:
                result
                    .Should()
                    .BeEquivalentTo(
                        new RelationalWriteExecutorResult.Upsert(
                            new UpsertResult.UpsertFailureWriteConflict()
                        )
                    );
                break;
            case RelationalWriteOperationKind.Put:
                result
                    .Should()
                    .BeEquivalentTo(
                        new RelationalWriteExecutorResult.Update(
                            new UpdateResult.UpdateFailureWriteConflict()
                        )
                    );
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(operationKind), operationKind, null);
        }

        _writeExceptionClassifier.IsTransientFailureCallCount.Should().Be(1);
        _writeSessionFactory.Session.CommitCallCount.Should().Be(0);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(1);
    }

    /// <summary>
    /// Session creation sits outside the executor's main try, so a provider failure there took a
    /// different path from an identical failure one statement later. This pins the wiring: whatever
    /// the provider's classifier calls transient at session creation maps to the same retryable
    /// conflict it would produce inside the transaction, rather than escaping unhandled. Which
    /// engine codes qualify is the classifier's business and is asserted against the real
    /// classifier in MssqlRelationalWriteExceptionClassifierTests; the stub here only supplies a
    /// DbException the fake classifier has been told to treat as transient.
    /// </summary>
    [TestCase(RelationalWriteOperationKind.Post)]
    [TestCase(RelationalWriteOperationKind.Put)]
    public async Task It_maps_transient_write_session_creation_failures_to_retryable_write_conflicts(
        RelationalWriteOperationKind operationKind
    )
    {
        var request = CreateRequest(operationKind);
        _writeExceptionClassifier.IsTransientFailureToReturn = true;
        _writeSessionFactory.ExceptionToThrow = new StubDbException(
            "Provider failure the classifier reports as transient."
        );

        var result = await _sut.ExecuteAsync(request);

        switch (operationKind)
        {
            case RelationalWriteOperationKind.Post:
                result
                    .Should()
                    .BeEquivalentTo(
                        new RelationalWriteExecutorResult.Upsert(
                            new UpsertResult.UpsertFailureWriteConflict()
                        )
                    );
                break;
            case RelationalWriteOperationKind.Put:
                result
                    .Should()
                    .BeEquivalentTo(
                        new RelationalWriteExecutorResult.Update(
                            new UpdateResult.UpdateFailureWriteConflict()
                        )
                    );
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(operationKind), operationKind, null);
        }

        _writeExceptionClassifier.IsTransientFailureCallCount.Should().Be(1);
        _writeSessionFactory.Session.RollbackCallCount.Should().Be(0);
    }

    /// <summary>
    /// A session-creation failure the classifier does not call transient has no session to roll back
    /// and no request to attribute a write failure to, so it stays an unmapped fault.
    /// </summary>
    [Test]
    public async Task It_rethrows_a_non_transient_write_session_creation_failure()
    {
        var request = CreateRequest(RelationalWriteOperationKind.Post);
        _writeExceptionClassifier.IsTransientFailureToReturn = false;
        _writeSessionFactory.ExceptionToThrow = new StubDbException("login failed");

        var act = () => _sut.ExecuteAsync(request);

        await act.Should().ThrowAsync<StubDbException>().WithMessage("login failed");
    }

    /// <summary>
    /// A first-phase failure the classifier does not call transient still has no execution request
    /// to attribute a write failure to, so it stays an unmapped fault.
    /// </summary>
    [Test]
    public async Task It_rethrows_a_non_transient_first_phase_db_exception()
    {
        var request = CreateRequest(RelationalWriteOperationKind.Post);
        _writeExceptionClassifier.IsTransientFailureToReturn = false;
        _targetLookupResolver.ExceptionToThrow = new StubDbException("connection reset");

        var act = () => _sut.ExecuteAsync(request);

        await act.Should().ThrowAsync<StubDbException>().WithMessage("connection reset");
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
                new DateTimeOffset(2026, 4, 2, 12, 0, 0, TimeSpan.Zero),
                1
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

        // Stored authorization and reference resolution for both verbs, plus the initial in-session
        // target observation, which both verbs now take on the session's executor.
        _writeSessionFactory.Session.CreateCommandExecutorCallCount.Should().Be(3);
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

        // No authorization executor is created on this path; the one call is reference resolution,
        // which now runs on the session's executor.
        _writeSessionFactory.Session.CreateCommandExecutorCallCount.Should().Be(2);
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
    public void It_rejects_an_operation_kind_that_does_not_match_the_target_request()
    {
        var writePlan = CreateRootPlan();
        var resourceModel = CreateRelationalResourceModel(writePlan.TableModel);
        var resourceWritePlan = new ResourceWritePlan(resourceModel, [writePlan]);
        var mappingSet = CreateMappingSet(resourceModel);

        var act = () =>
            new RelationalWriteExecutorInput(
                mappingSet,
                RelationalWriteOperationKind.Post,
                new RelationalWriteTargetRequest.Put(UpdateDocumentUuid),
                resourceWritePlan,
                CreateReadPlan(resourceModel),
                JsonNode.Parse("""{"name":"Lincoln High"}""")!,
                false,
                new TraceId("write-executor-test"),
                new ReferenceResolverRequest(mappingSet, resourceWritePlan.Model.Resource, [], [])
            );

        act.Should().Throw<ArgumentException>().WithParameterName("targetRequest");
        // The executor cannot be handed a mismatched pair, so no session opens and neither target
        // resolver runs: an invalid input never reaches the database.
        _writeSessionFactory.CreateAsyncCallCount.Should().Be(0);
        _targetLookupResolver.ResolveForPostCallCount.Should().Be(0);
        _targetLookupResolver.ResolveForPutCallCount.Should().Be(0);
    }

    [Test]
    public void It_rejects_an_existing_document_read_plan_for_another_resource()
    {
        var writePlan = CreateRootPlan();
        var resourceModel = CreateRelationalResourceModel(writePlan.TableModel);
        var resourceWritePlan = new ResourceWritePlan(resourceModel, [writePlan]);
        var mappingSet = CreateMappingSet(resourceModel);
        var otherResourceModel = CreateRelationalResourceModel(
            writePlan.TableModel,
            new QualifiedResourceName("Ed-Fi", "Student")
        );

        var act = () =>
            new RelationalWriteExecutorInput(
                mappingSet,
                RelationalWriteOperationKind.Post,
                new RelationalWriteTargetRequest.Post(new ReferentialId(Guid.NewGuid()), CreateDocumentUuid),
                resourceWritePlan,
                CreateReadPlan(otherResourceModel),
                JsonNode.Parse("""{"name":"Lincoln High"}""")!,
                false,
                new TraceId("write-executor-test"),
                new ReferenceResolverRequest(mappingSet, resourceWritePlan.Model.Resource, [], [])
            );

        act.Should().Throw<ArgumentException>().WithParameterName("existingDocumentReadPlan");
    }

    [Test]
    public void It_rejects_a_reference_resolution_request_built_from_another_mapping_set_instance()
    {
        var writePlan = CreateRootPlan();
        var resourceModel = CreateRelationalResourceModel(writePlan.TableModel);
        var resourceWritePlan = new ResourceWritePlan(resourceModel, [writePlan]);
        var mappingSet = CreateMappingSet(resourceModel);
        var otherMappingSetInstance = CreateMappingSet(resourceModel);

        var act = () =>
            new RelationalWriteExecutorInput(
                mappingSet,
                RelationalWriteOperationKind.Post,
                new RelationalWriteTargetRequest.Post(new ReferentialId(Guid.NewGuid()), CreateDocumentUuid),
                resourceWritePlan,
                CreateReadPlan(resourceModel),
                JsonNode.Parse("""{"name":"Lincoln High"}""")!,
                false,
                new TraceId("write-executor-test"),
                new ReferenceResolverRequest(
                    otherMappingSetInstance,
                    resourceWritePlan.Model.Resource,
                    [],
                    []
                )
            );

        act.Should().Throw<ArgumentException>().WithParameterName("referenceResolutionRequest");
    }

    [Test]
    public void It_rejects_a_reference_resolution_request_for_another_resource()
    {
        var writePlan = CreateRootPlan();
        var resourceModel = CreateRelationalResourceModel(writePlan.TableModel);
        var resourceWritePlan = new ResourceWritePlan(resourceModel, [writePlan]);
        var mappingSet = CreateMappingSet(resourceModel);

        var act = () =>
            new RelationalWriteExecutorInput(
                mappingSet,
                RelationalWriteOperationKind.Post,
                new RelationalWriteTargetRequest.Post(new ReferentialId(Guid.NewGuid()), CreateDocumentUuid),
                resourceWritePlan,
                CreateReadPlan(resourceModel),
                JsonNode.Parse("""{"name":"Lincoln High"}""")!,
                false,
                new TraceId("write-executor-test"),
                new ReferenceResolverRequest(
                    mappingSet,
                    new QualifiedResourceName("Ed-Fi", "Student"),
                    [],
                    []
                )
            );

        act.Should().Throw<ArgumentException>().WithParameterName("referenceResolutionRequest");
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
        var persistedTarget = new RelationalWritePersistResult(910L, CreateDocumentUuid, 77L);

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
        _writeSessionFactory.Session.CommitCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_returns_if_match_failure_for_profiled_post_as_update_when_the_current_etag_mismatches()
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

        var result = await _sut.ExecuteAsync(request);

        // The If-Match value cannot match the hydrated current state, so the 412 verdict returns
        // before any profile merge work runs.
        result
            .Should()
            .BeOfType<RelationalWriteExecutorResult.Upsert>()
            .Which.Result.Should()
            .BeOfType<UpsertResult.UpsertFailureETagMisMatch>()
            .Which.Reason.Should()
            .Be(ETagPreconditionFailureReason.Concurrency);
        result.AttemptOutcome.Should().Be(RelationalWriteExecutorAttemptOutcome.Failed.Instance);
        _currentStateLoader.LoadCallCount.Should().Be(1);
        _profileMergeSynthesizer.SynthesizeCallCount.Should().Be(0);
        _noProfilePersister.TryPersistCallCount.Should().Be(0);
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

    private RelationalWriteExecutorInput CreateNamespacePostCreateRequest(
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
        _sut = CreateExecutor(
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
        // Stored authorization plus reference resolution, both now on the session's executor.
        _writeSessionFactory.Session.CreateCommandExecutorCallCount.Should().Be(3);
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
        _currentStateLoader.LoadCallCount.Should().Be(1);
        _noProfilePersister.AuthorizeProposedRelationshipCallCount.Should().Be(1);
        _readMaterializer.MaterializeCallCount.Should().Be(0);
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
        _currentStateLoader.LoadCallCount.Should().Be(1);
        _noProfilePersister.AuthorizeProposedRelationshipCallCount.Should().Be(1);
        // If-Match now composes the current etag from ContentVersion; it no longer materializes.
        _readMaterializer.MaterializeCallCount.Should().Be(0);
        // The capture statement that observes the target also locks it, so no standalone lock
        // command is recorded on the session.
        _writeSessionFactory.Session.Commands.Should().BeEmpty();
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
        _readMaterializer.MaterializeCallCount.Should().Be(0);
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
        _currentStateLoader.LoadCallCount.Should().Be(1);
        _noProfilePersister.AuthorizeProposedRelationshipCallCount.Should().Be(1);
        // If-Match now composes the current etag from ContentVersion; it no longer materializes.
        _readMaterializer.MaterializeCallCount.Should().Be(0);
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
        _currentStateLoader.LoadCallCount.Should().Be(1);
        _noProfilePersister.AuthorizeProposedRelationshipCallCount.Should().Be(1);
        // If-Match now composes the current etag from ContentVersion; it no longer materializes.
        _readMaterializer.MaterializeCallCount.Should().Be(0);
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

    internal static RelationshipAuthorizationResult.Authorized CreateProposedSchoolIdRelationshipAuthorization(
        RelationalWriteExecutorInput request,
        long[]? claimEducationOrganizationIds = null
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
                claimEducationOrganizationIds ?? [1234L],
                RelationalAuthorizationParameterNameConstants.ClaimEducationOrganizationIds
            )
        );
    }

    /// <summary>
    /// The deferred no-claims disposition for whichever proposed relationship authorization the request
    /// already carries: the caller holds no education-organization claims, so the check needs no statement
    /// of its own, only a denial an earlier namespace statement may outrank.
    /// </summary>
    internal static RelationshipAuthorizationResult.NoClaims CreateProposedNoClaimsAuthorization(
        RelationalWriteExecutorRequest request
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        var authorized = (RelationshipAuthorizationResult.Authorized)
            request.ProposedRelationshipAuthorization!;
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

    private static RelationshipAuthorizationResult.Authorized CreateTransitivePeopleProposedRelationshipAuthorization(
        RelationalWriteExecutorInput request
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
        RelationalWriteExecutorInput request,
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

    internal static RelationshipAuthorizationResult.Authorized CreateStoredSchoolIdRelationshipAuthorization(
        RelationalWriteExecutorInput request,
        IReadOnlyList<long>? claimEducationOrganizationIds = null
    ) =>
        CreateStoredSchoolIdRelationshipAuthorization(
            request.MappingSet,
            request.WritePlan.Model.Resource,
            request.WritePlan.TablePlansInDependencyOrder[0],
            claimEducationOrganizationIds
        );

    /// <summary>
    /// The same stored SchoolId relationship authorization, arranged from the mapping set and root plan
    /// alone, for fixtures whose verb has no <see cref="RelationalWriteExecutorInput"/>.
    /// </summary>
    internal static RelationshipAuthorizationResult.Authorized CreateStoredSchoolIdRelationshipAuthorization(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        TableWritePlan rootPlan,
        IReadOnlyList<long>? claimEducationOrganizationIds = null
    )
    {
        var subject = CreateRelationshipAuthorizationSubject(
            resource,
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
                mappingSet.Key.Dialect,
                claimEducationOrganizationIds ?? [1234L],
                RelationalAuthorizationParameterNameConstants.ClaimEducationOrganizationIds
            )
        );
    }

    private static RelationshipAuthorizationResult.NoClaims CreateStoredSchoolIdNoClaimsAuthorization(
        RelationalWriteExecutorInput request
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
        RelationalWriteExecutorInput request
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
        RelationalWriteExecutorInput request
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
        RelationalWriteExecutorInput request
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
        RelationalWriteExecutorInput request
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
        RelationalWriteExecutorInput request
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
        RelationalWriteExecutorInput request,
        TableWritePlan rootPlan,
        string columnName,
        string jsonPath,
        string readableName
    ) =>
        CreateRelationshipAuthorizationSubject(
            request.WritePlan.Model.Resource,
            rootPlan,
            columnName,
            jsonPath,
            readableName
        );

    private static RelationshipAuthorizationSubject CreateRelationshipAuthorizationSubject(
        QualifiedResourceName resource,
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
            resource,
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

    /// <summary>
    /// Builds the executor input for an attempt and arranges the target the executor's own in-session
    /// lookup observes for it. <paramref name="targetContext"/> is therefore an arrangement of what the
    /// session resolver reports, not a value carried into the executor.
    /// </summary>
    private RelationalWriteExecutorInput CreateRequest(
        RelationalWriteOperationKind operationKind,
        bool allowIdentityUpdates = false,
        IReadOnlyList<DocumentReference>? documentReferences = null,
        IReadOnlyList<DescriptorReference>? descriptorReferences = null,
        RelationalWriteTargetContext? targetContext = null,
        TableWritePlan? rootWritePlan = null,
        JsonNode? selectedBody = null,
        SqlDialect dialect = SqlDialect.Pgsql,
        WritePrecondition? writePrecondition = null,
        string tenantKey = ""
    )
    {
        var resolvedRootWritePlan = rootWritePlan ?? CreateRootPlan();
        var resourceModel = CreateRelationalResourceModel(resolvedRootWritePlan.TableModel);
        var resourceWritePlan = new ResourceWritePlan(resourceModel, [resolvedRootWritePlan]);
        var mappingSet = CreateMappingSet(resourceModel, [resolvedRootWritePlan], dialect);
        var createDocumentUuid = CreateDocumentUuid;
        var updateDocumentUuid = UpdateDocumentUuid;
        var resolvedTargetContext =
            targetContext
            ?? (
                operationKind == RelationalWriteOperationKind.Put
                    ? new RelationalWriteTargetContext.ExistingDocument(345L, updateDocumentUuid, 44L)
                    : new RelationalWriteTargetContext.CreateNew(createDocumentUuid)
            );

        _targetLookupResolver.ArrangeInitialTarget(operationKind, resolvedTargetContext);
        _arrangedTargetContext = resolvedTargetContext;

        return new RelationalWriteExecutorInput(
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
            writePrecondition: writePrecondition,
            tenantKey: tenantKey
        );
    }

    /// <summary>
    /// The target context the current attempt's arrangement told the session resolver to report. Test
    /// helpers that need the existing target's identity read it here, because the executor input no
    /// longer carries a target.
    /// </summary>
    private RelationalWriteTargetContext.ExistingDocument ArrangedExistingTarget =>
        _arrangedTargetContext as RelationalWriteTargetContext.ExistingDocument
        ?? throw new InvalidOperationException("Expected an existing-document target context.");

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
    private static string ComposedCurrentEtag(RelationalWriteExecutorInput request, long contentVersion) =>
        EtagComposer.Compose(
            contentVersion,
            VariantKeyFactory.Create(
                request.MappingSet.Key.EffectiveSchemaHash,
                ResponseFormat.Json,
                ProfileVariantCode.Of(request.ProfileWriteContext?.ProfileName),
                linksEnabled: true
            )
        );

    internal static MappingSet CreateMappingSet(
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

    internal static ResourceReadPlan CreateReadPlan(
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
        var selectBySingleDocumentSql =
            $"{selectSql} where {QuoteIdentifier("DocumentId", dialect)} "
            + $"= @{HydrationSqlConventions.SingleDocumentIdParameterName}";

        return new ResourceReadPlan(
            resourceModel,
            KeysetTableConventions.GetKeysetTableContract(dialect),
            [new TableReadPlan(resourceModel.Root, selectSql, selectBySingleDocumentSql)],
            [],
            []
        );
    }

    internal static RelationalResourceModel CreateRelationalResourceModel(
        DbTableModel rootTable,
        QualifiedResourceName? resourceOverride = null
    )
    {
        var resource = resourceOverride ?? new QualifiedResourceName("Ed-Fi", "School");

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

    internal static TableWritePlan CreateRootPlan()
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

        public IRelationalCommandExecutor? CapturedCommandExecutor { get; private set; }

        public int CreateAdapterCallCount { get; private set; }

        public int CreateSessionAdapterCallCount { get; private set; }

        public IReferenceResolverAdapter CreateAdapter()
        {
            CreateAdapterCallCount++;
            return Adapter;
        }

        public IReferenceResolverAdapter CreateSessionAdapter(IRelationalCommandExecutor commandExecutor)
        {
            CreateSessionAdapterCallCount++;
            CapturedCommandExecutor = commandExecutor;
            return Adapter;
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

        public int ResolveForPutCallCount { get; private set; }

        public IRelationalCommandExecutor? CapturedCommandExecutor { get; private set; }

        public Queue<RelationalWriteTargetLookupResult> PostResults { get; } = [];

        public Queue<RelationalWriteTargetLookupResult> PutResults { get; } = [];

        private RelationalWriteTargetLookupResult? _defaultPostResult;

        private RelationalWriteTargetLookupResult? _defaultPutResult;

        /// <summary>
        /// Arranges the target this resolver reports when a test queues nothing of its own. A queued
        /// result always wins, so a test that queues one is arranging what the attempt's single
        /// in-session lookup observes.
        /// </summary>
        public void ArrangeInitialTarget(
            RelationalWriteOperationKind operationKind,
            RelationalWriteTargetContext targetContext
        )
        {
            RelationalWriteTargetLookupResult lookupResult = targetContext switch
            {
                RelationalWriteTargetContext.CreateNew createNew =>
                    new RelationalWriteTargetLookupResult.CreateNew(createNew.DocumentUuid),
                RelationalWriteTargetContext.ExistingDocument existingDocument =>
                    new RelationalWriteTargetLookupResult.ExistingDocument(
                        existingDocument.DocumentId,
                        existingDocument.DocumentUuid,
                        existingDocument.ObservedContentVersion
                    ),
                _ => throw new ArgumentOutOfRangeException(nameof(targetContext), targetContext, null),
            };

            if (operationKind is RelationalWriteOperationKind.Put)
            {
                _defaultPutResult = lookupResult;
                return;
            }

            _defaultPostResult = lookupResult;
        }

        /// <summary>
        /// Raised in place of a lookup result, standing in for a provider failure on the first
        /// phase's target capture statement.
        /// </summary>
        public Exception? ExceptionToThrow { get; set; }

        public Task<RelationalWriteTargetLookupResult> ResolveForPostAsync(
            MappingSet mappingSet,
            QualifiedResourceName resource,
            ReferentialId referentialId,
            DocumentUuid candidateDocumentUuid,
            IRelationalCommandExecutor commandExecutor,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            ResolveForPostCallCount++;
            CapturedCommandExecutor = commandExecutor;

            if (ExceptionToThrow is not null)
            {
                throw ExceptionToThrow;
            }

            return Task.FromResult(
                PostResults.Count > 0
                    ? PostResults.Dequeue()
                    : _defaultPostResult
                        ?? new RelationalWriteTargetLookupResult.CreateNew(candidateDocumentUuid)
            );
        }

        public Task<RelationalWriteTargetLookupResult> ResolveForPutAsync(
            MappingSet mappingSet,
            QualifiedResourceName resource,
            DocumentUuid documentUuid,
            IRelationalCommandExecutor commandExecutor,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            ResolveForPutCallCount++;
            CapturedCommandExecutor = commandExecutor;

            if (ExceptionToThrow is not null)
            {
                throw ExceptionToThrow;
            }

            return Task.FromResult(
                PutResults.Count > 0
                    ? PutResults.Dequeue()
                    : _defaultPutResult ?? new RelationalWriteTargetLookupResult.NotFound()
            );
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
                            DateTimeOffset.UnixEpoch,
                            1
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
                RelationalWriteTargetContext.CreateNew(var documentUuid) => new(
                    910L,
                    documentUuid,
                    77L,
                    DocumentCacheEnqueueOutcome.AlreadySatisfied
                ),
                RelationalWriteTargetContext.ExistingDocument(var documentId, var documentUuid, _) => new(
                    documentId,
                    documentUuid,
                    77L,
                    DocumentCacheEnqueueOutcome.AlreadySatisfied
                ),
                _ => throw new ArgumentOutOfRangeException(nameof(request), request, null),
            };
    }

    internal static RelationalWriteMergeResult CreateMergeResult(
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

    private RelationalWriteCurrentState CreateCurrentState(
        RelationalWriteExecutorInput request,
        long contentVersion,
        string schoolName = "Lincoln High",
        RelationalWriteTargetContext.ExistingDocument? existingTarget = null
    )
    {
        var targetContext = existingTarget ?? ArrangedExistingTarget;

        return new RelationalWriteCurrentState(
            new DocumentMetadataRow(
                targetContext.DocumentId,
                targetContext.DocumentUuid.Value,
                contentVersion,
                contentVersion,
                new DateTimeOffset(2026, 4, 2, 12, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 4, 2, 12, 0, 0, TimeSpan.Zero),
                1
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

        /// <summary>
        /// Raised instead of returning a session, standing in for a provider failure while opening
        /// the connection or beginning the transaction.
        /// </summary>
        public Exception? ExceptionToThrow { get; set; }

        public Task<IRelationalWriteSession> CreateAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CreateAsyncCallCount++;

            if (ExceptionToThrow is not null)
            {
                throw ExceptionToThrow;
            }

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

        /// <summary>
        /// Counts every in-session consumer that asked the session for a command executor: stored and
        /// proposed authorization, bulk reference resolution, and the in-session POST target lookup.
        /// It is no longer an authorization-only signal — assertions that care specifically about
        /// authorization should read <see cref="RelationshipAuthorizationCommands"/>.
        /// </summary>
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

    /// <summary>Captures formatted log messages together with the exception each carried.</summary>
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly List<string> _messages = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            _messages.Add($"{formatter(state, exception)} {exception?.Message}");
        }

        public string JoinedMessages() => string.Join('\n', _messages);
    }

    private sealed class RecordingRelationalWriteExceptionClassifier : IRelationalWriteExceptionClassifier
    {
        public int IsTransientFailureCallCount { get; private set; }

        public int TryClassifyCallCount { get; private set; }

        public DbException? CapturedException { get; private set; }

        public Exception? ExceptionToThrow { get; set; }

        public RelationalWriteExceptionClassification? ClassificationToReturn { get; set; }

        public bool IsTransientFailureToReturn { get; set; }

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

        public bool IsTransientFailure(DbException exception)
        {
            IsTransientFailureCallCount++;
            return IsTransientFailureToReturn;
        }
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

    private static IDataStoreSelection CreateSelectedDataStoreSelection()
    {
        var selection = new DataStoreSelection();
        selection.SetSelectedDataStore(
            new DataStore(
                DocumentCacheTelemetryTargetKey.DataStoreId,
                "postgresql",
                "document-cache-enqueue-telemetry",
                "Host=localhost;Database=document-cache-enqueue-telemetry",
                [],
                RelationalProviderToken.Postgresql,
                RelationalProviderMetadataStatus.Supported
            )
        );

        return selection;
    }

    private static IDocumentCacheTargetRegistry CreateDocumentCacheTargetRegistry(
        params DocumentCacheTargetObservation[] targets
    ) =>
        new StaticDocumentCacheTargetRegistry(
            new DocumentCacheTargetRegistrySnapshot(
                targets.Length == 0 ? [CreateDocumentCacheTargetObservation()] : [.. targets],
                new DateTimeOffset(2026, 4, 2, 12, 0, 0, TimeSpan.Zero)
            )
        );

    private static DocumentCacheTargetObservation CreateDocumentCacheTargetObservation(
        DocumentCacheTargetKey? targetKey = null
    ) =>
        DocumentCacheTargetObservation.ResolvedEligible(
            targetKey ?? DocumentCacheTelemetryTargetKey,
            new DocumentCacheTargetEffectiveSettings(
                readAccelerationEnabled: true,
                directFillTimeout: TimeSpan.FromMilliseconds(250),
                projectorPollInterval: TimeSpan.FromSeconds(5),
                projectorPageSize: 10,
                projectorMaxConcurrentTargets: 1,
                projectorFailureBackoff: TimeSpan.FromSeconds(30),
                projectorBaselineHighWaterMark: 1000,
                administrationWorkflowTimeout: TimeSpan.FromHours(24)
            ),
            new DocumentCacheTargetContextGeneration(1),
            RelationalProviderToken.Postgresql,
            new DocumentCachePhysicalSourceFingerprint(
                "sha256:1111111111111111111111111111111111111111111111111111111111111111"
            ),
            new DocumentCacheLifecycleObservation(
                DocumentCacheLifecycleState.Tracking,
                CacheAheadRecoveryRequired: false
            ),
            new DocumentCacheInventoryValidationResult(
                DocumentCacheInventoryStatus.Satisfied,
                "Inventory satisfied."
            ),
            new DocumentCacheEnqueueTriggerValidationResult(
                DocumentCacheEnqueueTriggerStatus.Satisfied,
                "Enqueue trigger satisfied."
            ),
            DocumentCacheSqlServerPrerequisiteDetails.NotApplicable()
        );

    private sealed class RecordingDocumentCacheEnqueueTelemetry : IDocumentCacheEnqueueTelemetry
    {
        public List<DocumentCacheEnqueueTelemetryContext> Successes { get; } = [];

        public List<DocumentCacheEnqueueFailureRecord> Failures { get; } = [];

        public void RecordSuccess(DocumentCacheEnqueueTelemetryContext context) => Successes.Add(context);

        public void RecordFailure(
            DocumentCacheEnqueueTelemetryContext context,
            DocumentCacheEnqueueFailureCategory category
        ) => Failures.Add(new DocumentCacheEnqueueFailureRecord(context, category));
    }

    private sealed record DocumentCacheEnqueueFailureRecord(
        DocumentCacheEnqueueTelemetryContext Context,
        DocumentCacheEnqueueFailureCategory Category
    );

    private sealed class RecordingDocumentCacheProviderCommandTimeoutClassifier
        : IDocumentCacheProviderCommandTimeoutClassifier
    {
        public bool IsProviderCommandTimeoutToReturn { get; set; }

        public bool IsProviderCommandTimeout(Exception exception) => IsProviderCommandTimeoutToReturn;
    }

    private sealed class StaticDocumentCacheTargetRegistry(
        DocumentCacheTargetRegistrySnapshot currentSnapshot
    ) : IDocumentCacheTargetRegistry
    {
        public DocumentCacheTargetRegistrySnapshot CurrentSnapshot { get; } = currentSnapshot;

        public DocumentCacheTargetRuntimeSnapshot CurrentRuntimeSnapshot { get; } =
            new([], currentSnapshot.ObservedAt);

        public Task<DocumentCacheTargetRegistrySnapshot> RefreshAsync(
            DocumentCacheTargetRefreshReason reason,
            CancellationToken cancellationToken = default
        ) => Task.FromResult(CurrentSnapshot);
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

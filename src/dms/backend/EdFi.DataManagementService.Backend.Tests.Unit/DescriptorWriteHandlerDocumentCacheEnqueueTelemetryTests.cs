// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.Etag;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
[Parallelizable]
[Category("DocumentCacheEnqueueTelemetry")]
public class Given_DescriptorWriteHandler_DocumentCacheEnqueueTelemetry
{
    private static readonly DateTimeOffset ObservedAt = new(2026, 8, 17, 16, 0, 0, TimeSpan.Zero);
    private static readonly QualifiedResourceName DescriptorResource = new("Ed-Fi", "SchoolTypeDescriptor");
    private static readonly DocumentCacheTargetKey TargetKey = DocumentCacheTargetKey.Create("Tenant-A", 99);

    [Test]
    public async Task It_records_descriptor_insert_success_after_the_enqueue_enabled_transaction_commits()
    {
        var documentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));
        var targetLookupService = new StubRelationalWriteTargetLookupService
        {
            PostResult = new RelationalWriteTargetLookupResult.CreateNew(documentUuid),
        };
        var sessionFactory = new RecordingRelationalWriteSessionFactory(SqlDialect.Pgsql);
        sessionFactory.Session.Executor.ResultSets.Enqueue([CreateContentVersionResultSet(42L)]);
        var telemetry = new RecordingDocumentCacheEnqueueTelemetry(
            () => sessionFactory.Session.CommitCallCount,
            () => sessionFactory.Session.RollbackCallCount
        );
        var sut = CreateSut(targetLookupService, sessionFactory, telemetry);

        var result = await sut.HandlePostAsync(
            CreatePostRequest(CreateMappingSet(SqlDialect.Pgsql), documentUuid)
        );

        result.Should().BeOfType<UpsertResult.InsertSuccess>();
        sessionFactory.Session.CommitCallCount.Should().Be(1);
        telemetry.Successes.Should().ContainSingle();
        telemetry.Successes[0].CommitCallCountAtRecord.Should().Be(1);
        telemetry.Successes[0].Context.TargetKey.Should().Be(TargetKey);
        telemetry.Successes[0].Context.ProviderToken.Should().Be(RelationalProviderToken.Postgresql);
        telemetry
            .Successes[0]
            .Context.CanonicalOperation.Should()
            .Be(DocumentCacheEnqueueTelemetryCanonicalOperation.Insert);
        telemetry
            .Successes[0]
            .Context.ResourceKind.Should()
            .Be(DocumentCacheEnqueueTelemetryResourceKind.Descriptor);
        telemetry.Failures.Should().BeEmpty();
    }

    [TestCase(nameof(DocumentCacheEnqueueOutcome.Inserted))]
    [TestCase(nameof(DocumentCacheEnqueueOutcome.Advanced))]
    [TestCase(nameof(DocumentCacheEnqueueOutcome.AlreadySatisfied))]
    public async Task It_records_descriptor_insert_success_for_committed_enqueue_outcomes(
        string enqueueOutcomeName
    )
    {
        var enqueueOutcome = Enum.Parse<DocumentCacheEnqueueOutcome>(enqueueOutcomeName);
        var documentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));
        var targetLookupService = new StubRelationalWriteTargetLookupService
        {
            PostResult = new RelationalWriteTargetLookupResult.CreateNew(documentUuid),
        };
        var sessionFactory = new RecordingRelationalWriteSessionFactory(SqlDialect.Pgsql);
        sessionFactory.Session.Executor.ResultSets.Enqueue([
            CreateContentVersionResultSet(42L, enqueueOutcome),
        ]);
        var telemetry = new RecordingDocumentCacheEnqueueTelemetry(
            () => sessionFactory.Session.CommitCallCount,
            () => sessionFactory.Session.RollbackCallCount
        );
        var sut = CreateSut(targetLookupService, sessionFactory, telemetry);

        await sut.HandlePostAsync(CreatePostRequest(CreateMappingSet(SqlDialect.Pgsql), documentUuid));

        sessionFactory.Session.CommitCallCount.Should().Be(1);
        telemetry.Successes.Should().ContainSingle();
        telemetry.Successes[0].CommitCallCountAtRecord.Should().Be(1);
        telemetry.Successes[0].Context.TargetKey.Should().Be(TargetKey);
    }

    [Test]
    public async Task It_does_not_record_descriptor_insert_success_when_registry_is_enabled_but_transaction_reports_no_work()
    {
        var documentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));
        var targetLookupService = new StubRelationalWriteTargetLookupService
        {
            PostResult = new RelationalWriteTargetLookupResult.CreateNew(documentUuid),
        };
        var sessionFactory = new RecordingRelationalWriteSessionFactory(SqlDialect.Pgsql);
        sessionFactory.Session.Executor.ResultSets.Enqueue([
            CreateContentVersionResultSet(42L, DocumentCacheEnqueueOutcome.NoWorkQueued),
        ]);
        var telemetry = new RecordingDocumentCacheEnqueueTelemetry(
            () => sessionFactory.Session.CommitCallCount,
            () => sessionFactory.Session.RollbackCallCount
        );
        var sut = CreateSut(targetLookupService, sessionFactory, telemetry);

        var result = await sut.HandlePostAsync(
            CreatePostRequest(CreateMappingSet(SqlDialect.Pgsql), documentUuid)
        );

        result.Should().BeOfType<UpsertResult.InsertSuccess>();
        sessionFactory.Session.CommitCallCount.Should().Be(1);
        telemetry.Successes.Should().BeEmpty();
        telemetry.Failures.Should().BeEmpty();
    }

    [TestCase(DescriptorWritePath.PostAsUpdate)]
    [TestCase(DescriptorWritePath.PutUpdate)]
    public async Task It_records_descriptor_update_success_after_the_enqueue_enabled_transaction_commits(
        DescriptorWritePath writePath
    )
    {
        var documentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));
        var targetLookupService = new StubRelationalWriteTargetLookupService();
        var sessionFactory = new RecordingRelationalWriteSessionFactory(SqlDialect.Pgsql);
        sessionFactory.Session.ScalarResults.Enqueue(44L);
        sessionFactory.Session.Executor.ResultSets.Enqueue([
            CreatePersistedDescriptorResultSet(description: "Previous Description"),
        ]);
        sessionFactory.Session.Executor.ResultSets.Enqueue([CreateContentVersionResultSet(45L)]);
        var telemetry = new RecordingDocumentCacheEnqueueTelemetry(
            () => sessionFactory.Session.CommitCallCount,
            () => sessionFactory.Session.RollbackCallCount
        );
        var sut = CreateSut(targetLookupService, sessionFactory, telemetry);
        var mappingSet = CreateMappingSet(SqlDialect.Pgsql);

        if (writePath == DescriptorWritePath.PostAsUpdate)
        {
            targetLookupService.PostResult = new RelationalWriteTargetLookupResult.ExistingDocument(
                345L,
                documentUuid,
                44L
            );

            await sut.HandlePostAsync(CreatePostRequest(mappingSet, documentUuid));
        }
        else
        {
            targetLookupService.PutResult = new RelationalWriteTargetLookupResult.ExistingDocument(
                345L,
                documentUuid,
                44L
            );

            await sut.HandlePutAsync(
                CreatePutRequest(mappingSet, documentUuid, description: "Updated Description")
            );
        }

        sessionFactory.Session.CommitCallCount.Should().Be(1);
        telemetry.Successes.Should().ContainSingle();
        telemetry.Successes[0].CommitCallCountAtRecord.Should().Be(1);
        telemetry
            .Successes[0]
            .Context.CanonicalOperation.Should()
            .Be(DocumentCacheEnqueueTelemetryCanonicalOperation.Update);
        telemetry
            .Successes[0]
            .Context.ResourceKind.Should()
            .Be(DocumentCacheEnqueueTelemetryResourceKind.Descriptor);
        telemetry.Successes[0].Context.TargetKey.Should().Be(TargetKey);
        telemetry.Failures.Should().BeEmpty();
    }

    [Test]
    public async Task It_does_not_record_descriptor_enqueue_success_for_no_op_put_rollbacks()
    {
        var documentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));
        var targetLookupService = new StubRelationalWriteTargetLookupService
        {
            PutResult = new RelationalWriteTargetLookupResult.ExistingDocument(345L, documentUuid, 44L),
        };
        var sessionFactory = new RecordingRelationalWriteSessionFactory(SqlDialect.Pgsql);
        sessionFactory.Session.ScalarResults.Enqueue(44L);
        sessionFactory.Session.Executor.ResultSets.Enqueue([CreatePersistedDescriptorResultSet()]);
        var telemetry = new RecordingDocumentCacheEnqueueTelemetry(
            () => sessionFactory.Session.CommitCallCount,
            () => sessionFactory.Session.RollbackCallCount
        );
        var sut = CreateSut(targetLookupService, sessionFactory, telemetry);

        await sut.HandlePutAsync(CreatePutRequest(CreateMappingSet(SqlDialect.Pgsql), documentUuid));

        sessionFactory.Session.CommitCallCount.Should().Be(0);
        sessionFactory.Session.RollbackCallCount.Should().Be(1);
        telemetry.Successes.Should().BeEmpty();
        telemetry.Failures.Should().BeEmpty();
    }

    [Test]
    public async Task It_records_and_retains_descriptor_enqueue_failures_under_the_exact_current_target()
    {
        DocumentCacheTargetKey peerTargetKey = DocumentCacheTargetKey.Create("Tenant-B", 99);
        var documentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));
        var targetLookupService = new StubRelationalWriteTargetLookupService
        {
            PostResult = new RelationalWriteTargetLookupResult.CreateNew(documentUuid),
        };
        var sessionFactory = new RecordingRelationalWriteSessionFactory(SqlDialect.Pgsql);
        sessionFactory.Session.Executor.ExceptionToThrow = new StubDbException(
            "insert or update on table \"DocumentProjectionWork\" violates foreign key constraint"
        );
        var targetRegistry = new StaticTargetRegistry([
            CreateDocumentCacheTargetObservation(TargetKey),
            CreateDocumentCacheTargetObservation(peerTargetKey),
        ]);
        DocumentCacheEnqueueTelemetry telemetry = CreateTelemetry(targetRegistry);
        var sut = CreateSut(
            targetLookupService,
            sessionFactory,
            telemetry,
            targetRegistry,
            tenantKey: peerTargetKey.TenantKey
        );

        var result = await sut.HandlePostAsync(
            CreatePostRequest(
                CreateMappingSet(SqlDialect.Pgsql),
                documentUuid,
                tenantKey: peerTargetKey.TenantKey
            )
        );

        result.Should().BeOfType<UpsertResult.UnknownFailure>();
        sessionFactory.Session.CommitCallCount.Should().Be(0);
        sessionFactory.Session.RollbackCallCount.Should().Be(1);
        telemetry.GetFailureSnapshot(TargetKey).RecentEvents.Should().BeEmpty();

        DocumentCacheEnqueueFailureSnapshot snapshot = telemetry.GetFailureSnapshot(peerTargetKey);
        snapshot.RecentEvents.Should().ContainSingle();
        snapshot.RecentEvents[0].TargetKey.Should().Be(peerTargetKey);
        snapshot
            .RecentEvents[0]
            .Category.Should()
            .Be(DocumentCacheEnqueueFailureCategory.WorkPersistenceFailed);
        snapshot
            .RecentEvents[0]
            .CanonicalOperation.Should()
            .Be(DocumentCacheEnqueueTelemetryCanonicalOperation.Insert);
        snapshot
            .RecentEvents[0]
            .ResourceKind.Should()
            .Be(DocumentCacheEnqueueTelemetryResourceKind.Descriptor);
    }

    [TestCase(
        DescriptorWritePath.PostInsert,
        nameof(DocumentCacheEnqueueTelemetryCanonicalOperation.Insert),
        typeof(UpsertResult.UnknownFailure)
    )]
    [TestCase(
        DescriptorWritePath.PostAsUpdate,
        nameof(DocumentCacheEnqueueTelemetryCanonicalOperation.Update),
        typeof(UpsertResult.UnknownFailure)
    )]
    [TestCase(
        DescriptorWritePath.PutUpdate,
        nameof(DocumentCacheEnqueueTelemetryCanonicalOperation.Update),
        typeof(UpdateResult.UnknownFailure)
    )]
    public async Task It_records_descriptor_enqueue_failure_when_commit_throws_classified_exception(
        DescriptorWritePath writePath,
        string expectedOperationName,
        Type expectedResultType
    )
    {
        var expectedOperation = Enum.Parse<DocumentCacheEnqueueTelemetryCanonicalOperation>(
            expectedOperationName
        );
        var documentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));
        var targetLookupService = new StubRelationalWriteTargetLookupService();
        var sessionFactory = new RecordingRelationalWriteSessionFactory(SqlDialect.Pgsql);
        sessionFactory.Session.CommitExceptionToThrow = new StubDbException(
            "insert or update on table \"DocumentProjectionWork\" violates foreign key constraint"
        );
        var telemetry = new RecordingDocumentCacheEnqueueTelemetry(
            () => sessionFactory.Session.CommitCallCount,
            () => sessionFactory.Session.RollbackCallCount
        );
        var sut = CreateSut(targetLookupService, sessionFactory, telemetry);
        var mappingSet = CreateMappingSet(SqlDialect.Pgsql);

        object result;
        if (writePath == DescriptorWritePath.PostInsert)
        {
            targetLookupService.PostResult = new RelationalWriteTargetLookupResult.CreateNew(documentUuid);
            sessionFactory.Session.Executor.ResultSets.Enqueue([CreateContentVersionResultSet(46L)]);

            result = await sut.HandlePostAsync(CreatePostRequest(mappingSet, documentUuid));
        }
        else if (writePath == DescriptorWritePath.PostAsUpdate)
        {
            targetLookupService.PostResult = new RelationalWriteTargetLookupResult.ExistingDocument(
                345L,
                documentUuid,
                44L
            );
            sessionFactory.Session.ScalarResults.Enqueue(44L);
            sessionFactory.Session.Executor.ResultSets.Enqueue([
                CreatePersistedDescriptorResultSet(description: "Previous Description"),
            ]);
            sessionFactory.Session.Executor.ResultSets.Enqueue([CreateContentVersionResultSet(45L)]);

            result = await sut.HandlePostAsync(CreatePostRequest(mappingSet, documentUuid));
        }
        else
        {
            targetLookupService.PutResult = new RelationalWriteTargetLookupResult.ExistingDocument(
                345L,
                documentUuid,
                44L
            );
            sessionFactory.Session.ScalarResults.Enqueue(44L);
            sessionFactory.Session.Executor.ResultSets.Enqueue([
                CreatePersistedDescriptorResultSet(description: "Previous Description"),
            ]);
            sessionFactory.Session.Executor.ResultSets.Enqueue([CreateContentVersionResultSet(45L)]);

            result = await sut.HandlePutAsync(
                CreatePutRequest(mappingSet, documentUuid, description: "Updated Description")
            );
        }

        result.Should().BeOfType(expectedResultType);
        sessionFactory.Session.CommitCallCount.Should().Be(1);
        sessionFactory.Session.RollbackCallCount.Should().Be(1);
        telemetry.Successes.Should().BeEmpty();
        telemetry.Failures.Should().ContainSingle();
        telemetry.Failures[0].RollbackCallCountAtRecord.Should().Be(0);
        telemetry.Failures[0].Category.Should().Be(DocumentCacheEnqueueFailureCategory.WorkPersistenceFailed);
        telemetry.Failures[0].Context.TargetKey.Should().Be(TargetKey);
        telemetry.Failures[0].Context.ProviderToken.Should().Be(RelationalProviderToken.Postgresql);
        telemetry.Failures[0].Context.CanonicalOperation.Should().Be(expectedOperation);
        telemetry
            .Failures[0]
            .Context.ResourceKind.Should()
            .Be(DocumentCacheEnqueueTelemetryResourceKind.Descriptor);
    }

    [Test]
    public async Task It_records_descriptor_enqueue_provider_timeout_only_from_timeout_classifier()
    {
        var documentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));
        var targetLookupService = new StubRelationalWriteTargetLookupService();
        var sessionFactory = new RecordingRelationalWriteSessionFactory(SqlDialect.Pgsql);
        sessionFactory.Session.CommitExceptionToThrow = new StubDbException(
            "command timeout while inserting into dms.DocumentProjectionWork"
        );
        var telemetry = new RecordingDocumentCacheEnqueueTelemetry(
            () => sessionFactory.Session.CommitCallCount,
            () => sessionFactory.Session.RollbackCallCount
        );
        var sut = CreateSut(
            targetLookupService,
            sessionFactory,
            telemetry,
            writeExceptionClassifier: new TransientRelationalWriteExceptionClassifier(),
            documentCacheProviderCommandTimeoutClassifier: new StubDocumentCacheProviderCommandTimeoutClassifier(
                isProviderCommandTimeout: true
            )
        );
        var mappingSet = CreateMappingSet(SqlDialect.Pgsql);
        targetLookupService.PutResult = new RelationalWriteTargetLookupResult.ExistingDocument(
            345L,
            documentUuid,
            44L
        );
        sessionFactory.Session.ScalarResults.Enqueue(44L);
        sessionFactory.Session.Executor.ResultSets.Enqueue([
            CreatePersistedDescriptorResultSet(description: "Previous Description"),
        ]);
        sessionFactory.Session.Executor.ResultSets.Enqueue([CreateContentVersionResultSet(45L)]);

        var result = await sut.HandlePutAsync(
            CreatePutRequest(mappingSet, documentUuid, description: "Updated Description")
        );

        result.Should().BeOfType<UpdateResult.UpdateFailureWriteConflict>();
        telemetry.Failures.Should().ContainSingle();
        telemetry.Failures[0].Category.Should().Be(DocumentCacheEnqueueFailureCategory.ProviderTimeout);
    }

    [Test]
    public async Task It_records_descriptor_transient_projection_work_failures_as_work_persistence_failures()
    {
        var documentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));
        var targetLookupService = new StubRelationalWriteTargetLookupService();
        var sessionFactory = new RecordingRelationalWriteSessionFactory(SqlDialect.Pgsql);
        sessionFactory.Session.CommitExceptionToThrow = new StubDbException(
            "deadlock detected while inserting into dms.DocumentProjectionWork"
        );
        var telemetry = new RecordingDocumentCacheEnqueueTelemetry(
            () => sessionFactory.Session.CommitCallCount,
            () => sessionFactory.Session.RollbackCallCount
        );
        var sut = CreateSut(
            targetLookupService,
            sessionFactory,
            telemetry,
            writeExceptionClassifier: new TransientRelationalWriteExceptionClassifier(),
            documentCacheProviderCommandTimeoutClassifier: new StubDocumentCacheProviderCommandTimeoutClassifier()
        );
        var mappingSet = CreateMappingSet(SqlDialect.Pgsql);
        targetLookupService.PutResult = new RelationalWriteTargetLookupResult.ExistingDocument(
            345L,
            documentUuid,
            44L
        );
        sessionFactory.Session.ScalarResults.Enqueue(44L);
        sessionFactory.Session.Executor.ResultSets.Enqueue([
            CreatePersistedDescriptorResultSet(description: "Previous Description"),
        ]);
        sessionFactory.Session.Executor.ResultSets.Enqueue([CreateContentVersionResultSet(45L)]);

        var result = await sut.HandlePutAsync(
            CreatePutRequest(mappingSet, documentUuid, description: "Updated Description")
        );

        result.Should().BeOfType<UpdateResult.UpdateFailureWriteConflict>();
        telemetry.Failures.Should().ContainSingle();
        telemetry.Failures[0].Category.Should().Be(DocumentCacheEnqueueFailureCategory.WorkPersistenceFailed);
    }

    [TestCase(DescriptorWritePath.PostInsert, typeof(UpsertResult.UpsertFailureWriteConflict))]
    [TestCase(DescriptorWritePath.PostAsUpdate, typeof(UpsertResult.UpsertFailureWriteConflict))]
    [TestCase(DescriptorWritePath.PutUpdate, typeof(UpdateResult.UpdateFailureWriteConflict))]
    public async Task It_does_not_record_descriptor_enqueue_failure_when_commit_throws_transient_exception_without_enqueue_artifacts(
        DescriptorWritePath writePath,
        Type expectedResultType
    )
    {
        var documentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));
        var targetLookupService = new StubRelationalWriteTargetLookupService();
        var sessionFactory = new RecordingRelationalWriteSessionFactory(SqlDialect.Pgsql);
        sessionFactory.Session.CommitExceptionToThrow = new StubDbException(
            "deadlock detected while committing the canonical descriptor row"
        );
        var telemetry = new RecordingDocumentCacheEnqueueTelemetry(
            () => sessionFactory.Session.CommitCallCount,
            () => sessionFactory.Session.RollbackCallCount
        );
        var sut = CreateSut(
            targetLookupService,
            sessionFactory,
            telemetry,
            writeExceptionClassifier: new TransientRelationalWriteExceptionClassifier()
        );
        var mappingSet = CreateMappingSet(SqlDialect.Pgsql);

        object result;
        if (writePath == DescriptorWritePath.PostInsert)
        {
            targetLookupService.PostResult = new RelationalWriteTargetLookupResult.CreateNew(documentUuid);
            sessionFactory.Session.Executor.ResultSets.Enqueue([CreateContentVersionResultSet(46L)]);

            result = await sut.HandlePostAsync(CreatePostRequest(mappingSet, documentUuid));
        }
        else if (writePath == DescriptorWritePath.PostAsUpdate)
        {
            targetLookupService.PostResult = new RelationalWriteTargetLookupResult.ExistingDocument(
                345L,
                documentUuid,
                44L
            );
            sessionFactory.Session.ScalarResults.Enqueue(44L);
            sessionFactory.Session.Executor.ResultSets.Enqueue([
                CreatePersistedDescriptorResultSet(description: "Previous Description"),
            ]);
            sessionFactory.Session.Executor.ResultSets.Enqueue([CreateContentVersionResultSet(45L)]);

            result = await sut.HandlePostAsync(CreatePostRequest(mappingSet, documentUuid));
        }
        else
        {
            targetLookupService.PutResult = new RelationalWriteTargetLookupResult.ExistingDocument(
                345L,
                documentUuid,
                44L
            );
            sessionFactory.Session.ScalarResults.Enqueue(44L);
            sessionFactory.Session.Executor.ResultSets.Enqueue([
                CreatePersistedDescriptorResultSet(description: "Previous Description"),
            ]);
            sessionFactory.Session.Executor.ResultSets.Enqueue([CreateContentVersionResultSet(45L)]);

            result = await sut.HandlePutAsync(
                CreatePutRequest(mappingSet, documentUuid, description: "Updated Description")
            );
        }

        result.Should().BeOfType(expectedResultType);
        sessionFactory.Session.CommitCallCount.Should().Be(1);
        sessionFactory.Session.RollbackCallCount.Should().Be(1);
        telemetry.Successes.Should().BeEmpty();
        telemetry.Failures.Should().BeEmpty();
    }

    public enum DescriptorWritePath
    {
        PostInsert,
        PostAsUpdate,
        PutUpdate,
    }

    private static DescriptorWriteHandler CreateSut(
        IRelationalWriteTargetLookupService targetLookupService,
        IRelationalWriteSessionFactory writeSessionFactory,
        IDocumentCacheEnqueueTelemetry telemetry,
        IDocumentCacheTargetRegistry? targetRegistry = null,
        string tenantKey = TargetKeyTenant,
        IRelationalWriteExceptionClassifier? writeExceptionClassifier = null,
        IDocumentCacheProviderCommandTimeoutClassifier? documentCacheProviderCommandTimeoutClassifier = null
    )
    {
        return new DescriptorWriteHandler(
            targetLookupService,
            writeExceptionClassifier ?? new NoOpRelationalWriteExceptionClassifier(),
            A.Fake<IRelationalDeleteConstraintResolver>(),
            writeSessionFactory,
            NullLogger<DescriptorWriteHandler>.Instance,
            new ServedEtagComposer(),
            dataStoreSelection: CreateSelectedDataStoreSelection(tenantKey),
            documentCacheEnqueueTelemetry: telemetry,
            documentCacheTargetRegistry: targetRegistry
                ?? CreateTargetRegistry(CreateDocumentCacheTargetObservation()),
            documentCacheProviderCommandTimeoutClassifier: documentCacheProviderCommandTimeoutClassifier
        );
    }

    private const string TargetKeyTenant = "Tenant-A";

    private static IDataStoreSelection CreateSelectedDataStoreSelection(string tenantKey)
    {
        var selection = new DataStoreSelection();
        selection.SetSelectedDataStore(
            new DataStore(
                99,
                "postgresql",
                $"document-cache-enqueue-telemetry-{tenantKey}",
                "Host=localhost;Database=document-cache-enqueue-telemetry",
                [],
                RelationalProviderToken.Postgresql,
                RelationalProviderMetadataStatus.Supported
            )
        );

        return selection;
    }

    private static DocumentCacheEnqueueTelemetry CreateTelemetry(IDocumentCacheTargetRegistry targetRegistry)
    {
        DocumentCacheOptions options = new();
        options.Projector.PageSize = 10;

        return new(
            Options.Create(options),
            new FixedTimeProvider(ObservedAt),
            NullLogger<DocumentCacheEnqueueTelemetry>.Instance,
            targetRegistry
        );
    }

    private static IDocumentCacheTargetRegistry CreateTargetRegistry(
        params DocumentCacheTargetObservation[] targets
    ) => new StaticTargetRegistry([.. targets]);

    private static DescriptorWriteRequest CreatePostRequest(
        MappingSet mappingSet,
        DocumentUuid documentUuid,
        string description = "Charter",
        string tenantKey = TargetKeyTenant
    )
    {
        return new DescriptorWriteRequest(
            mappingSet,
            DescriptorResource,
            CreateRequestBody(description),
            documentUuid,
            new ReferentialId(Guid.Parse("cccccccc-1111-2222-3333-dddddddddddd")),
            new TraceId("descriptor-post-enqueue-telemetry"),
            tenantKey: tenantKey
        );
    }

    private static DescriptorWriteRequest CreatePutRequest(
        MappingSet mappingSet,
        DocumentUuid documentUuid,
        string description = "Charter",
        string tenantKey = TargetKeyTenant
    )
    {
        return new DescriptorWriteRequest(
            mappingSet,
            DescriptorResource,
            CreateRequestBody(description),
            documentUuid,
            referentialId: null,
            new TraceId("descriptor-put-enqueue-telemetry"),
            tenantKey: tenantKey
        );
    }

    private static JsonNode CreateRequestBody(string description)
    {
        return JsonNode.Parse(
            $$"""
            {
              "namespace": "uri://ed-fi.org/SchoolTypeDescriptor",
              "codeValue": "Charter",
              "shortDescription": "Charter",
              "description": "{{description}}",
              "effectiveBeginDate": "2024-01-01"
            }
            """
        )!;
    }

    private static InMemoryRelationalResultSet CreateContentVersionResultSet(
        long contentVersion,
        DocumentCacheEnqueueOutcome enqueueOutcome = DocumentCacheEnqueueOutcome.AlreadySatisfied
    ) =>
        InMemoryRelationalResultSet.Create(
            new Dictionary<string, object?>
            {
                ["ContentVersion"] = contentVersion,
                ["DocumentCacheEnqueueOutcome"] = (int)enqueueOutcome,
            }
        );

    private static InMemoryRelationalResultSet CreatePersistedDescriptorResultSet(
        string description = "Charter"
    )
    {
        return InMemoryRelationalResultSet.Create(
            new Dictionary<string, object?>
            {
                ["Namespace"] = "uri://ed-fi.org/SchoolTypeDescriptor",
                ["CodeValue"] = "Charter",
                ["Uri"] = "uri://ed-fi.org/SchoolTypeDescriptor#Charter",
                ["ShortDescription"] = "Charter",
                ["Description"] = description,
                ["EffectiveBeginDate"] = new DateOnly(2024, 1, 1),
                ["EffectiveEndDate"] = DBNull.Value,
                ["ContentVersion"] = 44L,
            }
        );
    }

    private static MappingSet CreateMappingSet(SqlDialect dialect)
    {
        var resourceKey = new ResourceKeyEntry(1, DescriptorResource, "1.0.0", true);
        var rootTable = CreateRootTable();
        var resourceModel = new RelationalResourceModel(
            Resource: resourceKey.Resource,
            PhysicalSchema: new DbSchemaName("edfi"),
            StorageKind: ResourceStorageKind.SharedDescriptorTable,
            Root: rootTable,
            TablesInDependencyOrder: [rootTable],
            DocumentReferenceBindings: [],
            DescriptorEdgeSources: []
        );

        return new MappingSet(
            Key: new MappingSetKey("schema-hash", dialect, "v1"),
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
                Dialect: dialect,
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
            ),
            WritePlansByResource: new Dictionary<QualifiedResourceName, ResourceWritePlan>(),
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

    private static DbTableModel CreateRootTable()
    {
        return new DbTableModel(
            new DbTableName(new DbSchemaName("edfi"), "SchoolTypeDescriptor"),
            new JsonPathExpression("$", []),
            new TableKey(
                "PK_SchoolTypeDescriptor",
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
    }

    private static DocumentCacheTargetObservation CreateDocumentCacheTargetObservation(
        DocumentCacheTargetKey? targetKey = null
    ) =>
        DocumentCacheTargetObservation.ResolvedEligible(
            targetKey ?? TargetKey,
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

    private sealed class RecordingDocumentCacheEnqueueTelemetry(
        Func<int> commitCallCountAccessor,
        Func<int> rollbackCallCountAccessor
    ) : IDocumentCacheEnqueueTelemetry
    {
        public List<RecordedEnqueueSuccess> Successes { get; } = [];

        public List<RecordedEnqueueFailure> Failures { get; } = [];

        public void RecordSuccess(DocumentCacheEnqueueTelemetryContext context) =>
            Successes.Add(new RecordedEnqueueSuccess(context, commitCallCountAccessor()));

        public void RecordFailure(
            DocumentCacheEnqueueTelemetryContext context,
            DocumentCacheEnqueueFailureCategory category
        ) => Failures.Add(new RecordedEnqueueFailure(context, category, rollbackCallCountAccessor()));
    }

    private sealed record RecordedEnqueueSuccess(
        DocumentCacheEnqueueTelemetryContext Context,
        int CommitCallCountAtRecord
    );

    private sealed record RecordedEnqueueFailure(
        DocumentCacheEnqueueTelemetryContext Context,
        DocumentCacheEnqueueFailureCategory Category,
        int RollbackCallCountAtRecord
    );

    private sealed class TransientRelationalWriteExceptionClassifier : IRelationalWriteExceptionClassifier
    {
        public bool TryClassify(
            DbException exception,
            [NotNullWhen(true)] out RelationalWriteExceptionClassification? classification
        )
        {
            classification = null;
            return false;
        }

        public bool IsForeignKeyViolation(DbException exception) => false;

        public bool IsUniqueConstraintViolation(DbException exception) => false;

        public bool IsTransientFailure(DbException exception) => true;
    }

    private sealed class StubDocumentCacheProviderCommandTimeoutClassifier(
        bool isProviderCommandTimeout = false
    ) : IDocumentCacheProviderCommandTimeoutClassifier
    {
        public bool IsProviderCommandTimeout(Exception exception) => isProviderCommandTimeout;
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class StaticTargetRegistry(ImmutableArray<DocumentCacheTargetObservation> targets)
        : IDocumentCacheTargetRegistry
    {
        public DocumentCacheTargetRegistrySnapshot CurrentSnapshot { get; } = new(targets, ObservedAt);

        public DocumentCacheTargetRuntimeSnapshot CurrentRuntimeSnapshot { get; } = new([], ObservedAt);

        public Task<DocumentCacheTargetRegistrySnapshot> RefreshAsync(
            DocumentCacheTargetRefreshReason reason,
            CancellationToken cancellationToken = default
        ) => Task.FromResult(CurrentSnapshot);
    }

    private sealed class RecordingRelationalCommandExecutor(SqlDialect dialect) : IRelationalCommandExecutor
    {
        public SqlDialect Dialect { get; } = dialect;

        public Queue<IReadOnlyList<InMemoryRelationalResultSet>> ResultSets { get; } = [];

        public DbException? ExceptionToThrow { get; set; }

        public async Task<TResult> ExecuteReaderAsync<TResult>(
            RelationalCommand command,
            Func<IRelationalCommandReader, CancellationToken, Task<TResult>> readAsync,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (ExceptionToThrow is not null)
            {
                throw ExceptionToThrow;
            }

            IReadOnlyList<InMemoryRelationalResultSet> resultSets =
                ResultSets.Count == 0 ? [] : ResultSets.Dequeue();

            await using var reader = new InMemoryRelationalCommandReader(resultSets);
            return await readAsync(reader, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class RecordingRelationalWriteSessionFactory(SqlDialect dialect)
        : IRelationalWriteSessionFactory
    {
        public RecordingRelationalWriteSession Session { get; } = new(dialect);

        public Task<IRelationalWriteSession> CreateAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IRelationalWriteSession>(Session);
        }
    }

    private sealed class RecordingRelationalWriteSession : IRelationalWriteSession
    {
        private readonly RecordingDbConnection _connection = new(
            new RecordingDbCommand(new DataTable().CreateDataReader())
        );
        private readonly RecordingDbTransaction _transaction;

        public RecordingRelationalWriteSession(SqlDialect dialect)
        {
            _transaction = new RecordingDbTransaction(_connection, IsolationLevel.ReadCommitted);
            Executor = new RecordingRelationalCommandExecutor(dialect);
        }

        public DbConnection Connection => _connection;

        public DbTransaction Transaction => _transaction;

        public RecordingRelationalCommandExecutor Executor { get; }

        public Queue<object?> ScalarResults { get; } = [];

        public int CommitCallCount { get; private set; }

        public int RollbackCallCount { get; private set; }

        public DbException? CommitExceptionToThrow { get; set; }

        public DbCommand CreateCommand(RelationalCommand command)
        {
            var dbCommand = new RecordingDbCommand(new DataTable().CreateDataReader())
            {
                CommandText = command.CommandText,
                ScalarResult = ScalarResults.Count == 0 ? null : ScalarResults.Dequeue(),
            };

            foreach (var parameter in command.Parameters)
            {
                var dbParameter = dbCommand.CreateParameter();
                dbParameter.ParameterName = parameter.Name;
                dbParameter.Value = parameter.Value ?? DBNull.Value;
                parameter.ConfigureParameter?.Invoke(dbParameter);
                dbCommand.Parameters.Add((RecordingDbParameter)dbParameter);
            }

            return dbCommand;
        }

        public IRelationalCommandExecutor CreateCommandExecutor() => Executor;

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

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class StubRelationalWriteTargetLookupService : IRelationalWriteTargetLookupService
    {
        public RelationalWriteTargetLookupResult PostResult { get; set; } =
            new RelationalWriteTargetLookupResult.NotFound();

        public RelationalWriteTargetLookupResult PutResult { get; set; } =
            new RelationalWriteTargetLookupResult.NotFound();

        public Task<RelationalWriteTargetLookupResult> ResolveForPostAsync(
            MappingSet mappingSet,
            QualifiedResourceName resource,
            ReferentialId referentialId,
            DocumentUuid candidateDocumentUuid,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(PostResult);
        }

        public Task<RelationalWriteTargetLookupResult> ResolveForPutAsync(
            MappingSet mappingSet,
            QualifiedResourceName resource,
            DocumentUuid documentUuid,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(PutResult);
        }
    }

    private sealed class StubDbException(string message) : DbException(message);
}

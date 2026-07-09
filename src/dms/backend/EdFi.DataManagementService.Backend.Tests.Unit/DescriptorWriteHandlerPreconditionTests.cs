// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Data.Common;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.Etag;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Tests.Unit.TestSupport;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
[Parallelizable]
public class Given_Descriptor_Write_Preconditions
{
    private static readonly QualifiedResourceName _descriptorResource = new("Ed-Fi", "SchoolTypeDescriptor");
    private static readonly IServedEtagComposer _servedEtagComposer = new ServedEtagComposer();

    [Test]
    public async Task It_re_resolves_descriptor_post_creates_inside_the_write_session_before_returning_precondition_failed()
    {
        var documentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));
        var targetLookupService = new StubRelationalWriteTargetLookupService
        {
            PostResult = new RelationalWriteTargetLookupResult.CreateNew(documentUuid),
        };
        var sessionFactory = new RecordingRelationalWriteSessionFactory(SqlDialect.Pgsql);
        var sut = CreateSut(targetLookupService, sessionFactory);
        var request = CreatePostRequest(CreateMappingSet(SqlDialect.Pgsql), documentUuid) with
        {
            WritePrecondition = new WritePrecondition.IfMatch("\"stale-etag\""),
        };

        var result = await sut.HandlePostAsync(request);

        // The advisory target re-resolves as CreateNew, so there is no current representation to
        // satisfy If-Match against, and the reason is TargetDoesNotExist rather than Concurrency.
        result
            .Should()
            .BeOfType<UpsertResult.UpsertFailureETagMisMatch>()
            .Which.Reason.Should()
            .Be(ETagPreconditionFailureReason.TargetDoesNotExist);
        sessionFactory.CreateAsyncCallCount.Should().Be(1);
        sessionFactory.Session.CommitCallCount.Should().Be(0);
        sessionFactory.Session.RollbackCallCount.Should().Be(1);
        sessionFactory.Session.DisposeCallCount.Should().Be(1);
        sessionFactory.Session.Executor.Commands.Should().ContainSingle();
        sessionFactory
            .Session.Executor.Commands[0]
            .CommandText.Should()
            .Contain("FROM dms.\"ReferentialIdentity\"");
        sessionFactory.Session.ScalarCommands.Should().BeEmpty();
    }

    [Test]
    public async Task It_uses_the_in_session_descriptor_post_target_when_the_advisory_create_target_now_exists()
    {
        var documentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));
        var targetLookupService = new StubRelationalWriteTargetLookupService
        {
            PostResult = new RelationalWriteTargetLookupResult.CreateNew(documentUuid),
        };
        var sessionFactory = new RecordingRelationalWriteSessionFactory(SqlDialect.Pgsql);
        var currentEtag = ExpectedComposedDescriptorEtag(44L);
        sessionFactory.Session.Executor.ResultSets.Enqueue([CreateResolvedExistingDocumentRow(documentUuid)]);
        sessionFactory.Session.ScalarResults.Enqueue(44L);
        sessionFactory.Session.Executor.ResultSets.Enqueue([
            CreatePersistedDescriptorRow(description: "Current Charter"),
        ]);
        sessionFactory.Session.Executor.ResultSets.Enqueue([CreateContentVersionRow(45L)]);
        var sut = CreateSut(targetLookupService, sessionFactory);
        var request = CreatePostRequest(
            CreateMappingSet(SqlDialect.Pgsql),
            documentUuid,
            description: "Updated Charter"
        ) with
        {
            WritePrecondition = new WritePrecondition.IfMatch(currentEtag),
        };

        var result = await sut.HandlePostAsync(request);

        result
            .Should()
            .BeEquivalentTo(
                new UpsertResult.UpdateSuccess(documentUuid, ExpectedComposedDescriptorEtag(45L))
            );
        sessionFactory.CreateAsyncCallCount.Should().Be(1);
        sessionFactory.Session.CommitCallCount.Should().Be(1);
        sessionFactory.Session.RollbackCallCount.Should().Be(0);
        sessionFactory.Session.DisposeCallCount.Should().Be(1);
        sessionFactory.Session.Executor.Commands.Should().HaveCount(3);
        sessionFactory.Session.Executor.Commands[2].CommandText.Should().Contain("UPDATE dms.\"Descriptor\"");
        sessionFactory.Session.ScalarCommands.Should().ContainSingle();
        sessionFactory.Session.ScalarCommands[0].CommandText.Should().Contain("FOR UPDATE");
    }

    [Test]
    public async Task It_returns_precondition_failed_for_descriptor_post_as_update_when_if_match_mismatches()
    {
        var documentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));
        var targetLookupService = new StubRelationalWriteTargetLookupService
        {
            PostResult = new RelationalWriteTargetLookupResult.ExistingDocument(345L, documentUuid, 44L),
        };
        var sessionFactory = new RecordingRelationalWriteSessionFactory(SqlDialect.Pgsql);
        sessionFactory.Session.Executor.ResultSets.Enqueue([CreateResolvedExistingDocumentRow(documentUuid)]);
        sessionFactory.Session.ScalarResults.Enqueue(44L);
        sessionFactory.Session.Executor.ResultSets.Enqueue([
            CreatePersistedDescriptorRow(description: "Current Charter"),
        ]);
        sessionFactory.Session.Executor.ResultSets.Enqueue([CreateContentVersionRow(45L)]);
        var sut = CreateSut(targetLookupService, sessionFactory);
        var request = CreatePostRequest(CreateMappingSet(SqlDialect.Pgsql), documentUuid) with
        {
            WritePrecondition = new WritePrecondition.IfMatch("\"stale-etag\""),
        };

        var result = await sut.HandlePostAsync(request);

        // The target exists but its current etag does not match the specific-tag If-Match precondition,
        // so the reason is Concurrency rather than TargetDoesNotExist.
        result
            .Should()
            .BeOfType<UpsertResult.UpsertFailureETagMisMatch>()
            .Which.Reason.Should()
            .Be(ETagPreconditionFailureReason.Concurrency);
        sessionFactory.CreateAsyncCallCount.Should().Be(1);
        sessionFactory.Session.CommitCallCount.Should().Be(0);
        sessionFactory.Session.RollbackCallCount.Should().Be(1);
        sessionFactory.Session.DisposeCallCount.Should().Be(1);
        sessionFactory.Session.Executor.Commands.Should().HaveCount(2);
        sessionFactory
            .Session.Executor.Commands.Should()
            .NotContain(command =>
                command.CommandText.Contains("UPDATE dms.\"Descriptor\"", StringComparison.Ordinal)
            );
        sessionFactory.Session.ScalarCommands.Should().ContainSingle();
        sessionFactory.Session.ScalarCommands[0].CommandText.Should().Contain("FOR UPDATE");
    }

    [Test]
    public async Task It_returns_precondition_failed_for_descriptor_put_when_if_match_mismatches()
    {
        var documentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));
        var sessionFactory = new RecordingRelationalWriteSessionFactory(SqlDialect.Pgsql);
        sessionFactory.Session.Executor.ResultSets.Enqueue([CreateResolvedExistingDocumentRow(documentUuid)]);
        sessionFactory.Session.ScalarResults.Enqueue(44L);
        sessionFactory.Session.Executor.ResultSets.Enqueue([
            CreatePersistedDescriptorRow(description: "Current Charter"),
        ]);
        sessionFactory.Session.Executor.ResultSets.Enqueue([CreateContentVersionRow(45L)]);
        var sut = CreateSut(new StubRelationalWriteTargetLookupService(), sessionFactory);
        var request = CreatePutRequest(CreateMappingSet(SqlDialect.Pgsql), documentUuid) with
        {
            WritePrecondition = new WritePrecondition.IfMatch("\"stale-etag\""),
        };

        var result = await sut.HandlePutAsync(request);

        // The target exists but its current etag does not match the specific-tag If-Match precondition,
        // so the reason is Concurrency rather than TargetDoesNotExist.
        result
            .Should()
            .BeOfType<UpdateResult.UpdateFailureETagMisMatch>()
            .Which.Reason.Should()
            .Be(ETagPreconditionFailureReason.Concurrency);
        sessionFactory.CreateAsyncCallCount.Should().Be(1);
        sessionFactory.Session.CommitCallCount.Should().Be(0);
        sessionFactory.Session.RollbackCallCount.Should().Be(1);
        sessionFactory.Session.DisposeCallCount.Should().Be(1);
        sessionFactory
            .Session.Executor.Commands.Should()
            .NotContain(command =>
                command.CommandText.Contains("UPDATE dms.\"Descriptor\"", StringComparison.Ordinal)
            );
        sessionFactory.Session.ScalarCommands.Should().ContainSingle();
        sessionFactory.Session.ScalarCommands[0].CommandText.Should().Contain("FOR UPDATE");
    }

    [Test]
    public async Task It_short_circuits_descriptor_post_as_update_overlap_when_if_match_exactly_matches_current_etag()
    {
        var documentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));
        var targetLookupService = new StubRelationalWriteTargetLookupService
        {
            PostResult = new RelationalWriteTargetLookupResult.ExistingDocument(345L, documentUuid, 44L),
        };
        var sessionFactory = new RecordingRelationalWriteSessionFactory(SqlDialect.Pgsql);
        var request = CreatePostRequest(CreateMappingSet(SqlDialect.Pgsql), documentUuid);
        var currentEtag = ExpectedComposedDescriptorEtag(44L);
        sessionFactory.Session.Executor.ResultSets.Enqueue([CreateResolvedExistingDocumentRow(documentUuid)]);
        sessionFactory.Session.ScalarResults.Enqueue(44L);
        sessionFactory.Session.Executor.ResultSets.Enqueue([CreatePersistedDescriptorRow()]);
        var sut = CreateSut(targetLookupService, sessionFactory);
        request = request with { WritePrecondition = new WritePrecondition.IfMatch(currentEtag) };

        var result = await sut.HandlePostAsync(request);

        result.Should().BeEquivalentTo(new UpsertResult.UpdateSuccess(documentUuid, currentEtag));
        sessionFactory.CreateAsyncCallCount.Should().Be(1);
        sessionFactory.Session.CommitCallCount.Should().Be(0);
        sessionFactory.Session.RollbackCallCount.Should().Be(1);
        sessionFactory.Session.DisposeCallCount.Should().Be(1);
        sessionFactory.Session.Executor.Commands.Should().HaveCount(2);
        sessionFactory
            .Session.Executor.Commands.Should()
            .NotContain(command =>
                command.CommandText.Contains("UPDATE dms.\"Descriptor\"", StringComparison.Ordinal)
            );
    }

    [Test]
    public async Task It_rechecks_descriptor_post_as_update_no_ops_under_a_content_version_lock_when_if_match_is_absent()
    {
        var documentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));
        var targetLookupService = new StubRelationalWriteTargetLookupService
        {
            PostResult = new RelationalWriteTargetLookupResult.ExistingDocument(345L, documentUuid, 44L),
        };
        var sessionFactory = new RecordingRelationalWriteSessionFactory(SqlDialect.Pgsql);
        sessionFactory.Session.ScalarResults.Enqueue(44L);
        sessionFactory.Session.Executor.ResultSets.Enqueue([CreatePersistedDescriptorRow()]);
        var sut = CreateSut(targetLookupService, sessionFactory);
        var request = CreatePostRequest(CreateMappingSet(SqlDialect.Pgsql), documentUuid);
        var currentEtag = ExpectedComposedDescriptorEtag(44L);

        var result = await sut.HandlePostAsync(request);

        result.Should().BeEquivalentTo(new UpsertResult.UpdateSuccess(documentUuid, currentEtag));
        sessionFactory.CreateAsyncCallCount.Should().Be(1);
        sessionFactory.Session.CommitCallCount.Should().Be(0);
        sessionFactory.Session.RollbackCallCount.Should().Be(1);
        sessionFactory.Session.DisposeCallCount.Should().Be(1);
        sessionFactory.Session.ScalarCommands.Should().ContainSingle();
        sessionFactory.Session.ScalarCommands[0].CommandText.Should().Contain("FOR UPDATE");
        sessionFactory.Session.Executor.Commands.Should().ContainSingle();
        sessionFactory.Session.Executor.Commands[0].CommandText.Should().Contain("FROM dms.\"Descriptor\"");
    }

    [Test]
    public async Task It_rechecks_descriptor_put_no_ops_under_a_content_version_lock_when_if_match_is_absent()
    {
        var documentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));
        var targetLookupService = new StubRelationalWriteTargetLookupService
        {
            PutResult = new RelationalWriteTargetLookupResult.ExistingDocument(345L, documentUuid, 44L),
        };
        var sessionFactory = new RecordingRelationalWriteSessionFactory(SqlDialect.Pgsql);
        sessionFactory.Session.ScalarResults.Enqueue(44L);
        sessionFactory.Session.Executor.ResultSets.Enqueue([CreatePersistedDescriptorRow()]);
        var sut = CreateSut(targetLookupService, sessionFactory);
        var request = CreatePutRequest(CreateMappingSet(SqlDialect.Pgsql), documentUuid);
        var currentEtag = ExpectedComposedDescriptorEtag(44L);

        var result = await sut.HandlePutAsync(request);

        result.Should().BeEquivalentTo(new UpdateResult.UpdateSuccess(documentUuid, currentEtag));
        sessionFactory.CreateAsyncCallCount.Should().Be(1);
        sessionFactory.Session.CommitCallCount.Should().Be(0);
        sessionFactory.Session.RollbackCallCount.Should().Be(1);
        sessionFactory.Session.DisposeCallCount.Should().Be(1);
        sessionFactory.Session.ScalarCommands.Should().ContainSingle();
        sessionFactory.Session.ScalarCommands[0].CommandText.Should().Contain("FOR UPDATE");
        sessionFactory.Session.Executor.Commands.Should().ContainSingle();
        sessionFactory.Session.Executor.Commands[0].CommandText.Should().Contain("FROM dms.\"Descriptor\"");
    }

    [Test]
    public async Task It_updates_descriptor_post_as_update_under_a_content_version_lock_when_if_match_is_absent()
    {
        var documentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));
        var targetLookupService = new StubRelationalWriteTargetLookupService
        {
            PostResult = new RelationalWriteTargetLookupResult.ExistingDocument(345L, documentUuid, 44L),
        };
        var sessionFactory = new RecordingRelationalWriteSessionFactory(SqlDialect.Pgsql);
        sessionFactory.Session.ScalarResults.Enqueue(45L);
        sessionFactory.Session.Executor.ResultSets.Enqueue([
            CreatePersistedDescriptorRow(description: "Changed Elsewhere"),
        ]);
        sessionFactory.Session.Executor.ResultSets.Enqueue([CreateContentVersionRow(46L)]);
        var sut = CreateSut(targetLookupService, sessionFactory);
        var request = CreatePostRequest(CreateMappingSet(SqlDialect.Pgsql), documentUuid);

        var result = await sut.HandlePostAsync(request);

        result
            .Should()
            .BeEquivalentTo(
                new UpsertResult.UpdateSuccess(documentUuid, ExpectedComposedDescriptorEtag(46L))
            );
        sessionFactory.CreateAsyncCallCount.Should().Be(1);
        sessionFactory.Session.CommitCallCount.Should().Be(1);
        sessionFactory.Session.RollbackCallCount.Should().Be(0);
        sessionFactory.Session.Executor.Commands.Should().HaveCount(2);
        sessionFactory.Session.Executor.Commands[0].CommandText.Should().Contain("FROM dms.\"Descriptor\"");
        sessionFactory.Session.Executor.Commands[1].CommandText.Should().Contain("UPDATE dms.\"Descriptor\"");
    }

    [Test]
    public async Task It_updates_descriptor_put_under_a_content_version_lock_when_if_match_is_absent()
    {
        var documentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));
        var targetLookupService = new StubRelationalWriteTargetLookupService
        {
            PutResult = new RelationalWriteTargetLookupResult.ExistingDocument(345L, documentUuid, 44L),
        };
        var sessionFactory = new RecordingRelationalWriteSessionFactory(SqlDialect.Pgsql);
        sessionFactory.Session.ScalarResults.Enqueue(45L);
        sessionFactory.Session.Executor.ResultSets.Enqueue([
            CreatePersistedDescriptorRow(description: "Changed Elsewhere"),
        ]);
        sessionFactory.Session.Executor.ResultSets.Enqueue([CreateContentVersionRow(46L)]);
        var sut = CreateSut(targetLookupService, sessionFactory);
        var request = CreatePutRequest(CreateMappingSet(SqlDialect.Pgsql), documentUuid);

        var result = await sut.HandlePutAsync(request);

        result
            .Should()
            .BeEquivalentTo(
                new UpdateResult.UpdateSuccess(documentUuid, ExpectedComposedDescriptorEtag(46L))
            );
        sessionFactory.CreateAsyncCallCount.Should().Be(1);
        sessionFactory.Session.CommitCallCount.Should().Be(1);
        sessionFactory.Session.RollbackCallCount.Should().Be(0);
        sessionFactory.Session.Executor.Commands.Should().HaveCount(2);
        sessionFactory.Session.Executor.Commands[0].CommandText.Should().Contain("FROM dms.\"Descriptor\"");
        sessionFactory.Session.Executor.Commands[1].CommandText.Should().Contain("UPDATE dms.\"Descriptor\"");
    }

    [Test]
    public async Task It_returns_write_conflict_for_descriptor_post_as_update_when_the_locked_document_is_missing()
    {
        var documentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));
        var targetLookupService = new StubRelationalWriteTargetLookupService
        {
            PostResult = new RelationalWriteTargetLookupResult.ExistingDocument(345L, documentUuid, 44L),
        };
        var sessionFactory = new RecordingRelationalWriteSessionFactory(SqlDialect.Pgsql);
        var sut = CreateSut(targetLookupService, sessionFactory);
        var request = CreatePostRequest(
            CreateMappingSet(SqlDialect.Pgsql),
            documentUuid,
            description: "Updated Description"
        );

        var result = await sut.HandlePostAsync(request);

        result.Should().BeOfType<UpsertResult.UpsertFailureWriteConflict>();
        sessionFactory.CreateAsyncCallCount.Should().Be(1);
        sessionFactory.Session.CommitCallCount.Should().Be(0);
        sessionFactory.Session.RollbackCallCount.Should().Be(1);
        sessionFactory.Session.ScalarCommands.Should().ContainSingle();
        sessionFactory.Session.ScalarCommands[0].CommandText.Should().Contain("FOR UPDATE");
        sessionFactory.Session.Executor.Commands.Should().BeEmpty();
    }

    [Test]
    public async Task It_returns_not_exists_for_descriptor_put_when_the_locked_document_is_missing()
    {
        var documentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));
        var targetLookupService = new StubRelationalWriteTargetLookupService
        {
            PutResult = new RelationalWriteTargetLookupResult.ExistingDocument(345L, documentUuid, 44L),
        };
        var sessionFactory = new RecordingRelationalWriteSessionFactory(SqlDialect.Pgsql);
        var sut = CreateSut(targetLookupService, sessionFactory);
        var request = CreatePutRequest(
            CreateMappingSet(SqlDialect.Pgsql),
            documentUuid,
            description: "Updated Description"
        );

        var result = await sut.HandlePutAsync(request);

        result.Should().BeOfType<UpdateResult.UpdateFailureNotExists>();
        sessionFactory.CreateAsyncCallCount.Should().Be(1);
        sessionFactory.Session.CommitCallCount.Should().Be(0);
        sessionFactory.Session.RollbackCallCount.Should().Be(1);
        sessionFactory.Session.ScalarCommands.Should().ContainSingle();
        sessionFactory.Session.ScalarCommands[0].CommandText.Should().Contain("FOR UPDATE");
        sessionFactory.Session.Executor.Commands.Should().BeEmpty();
    }

    [Test]
    public async Task It_returns_precondition_failed_for_descriptor_put_when_the_target_is_missing_under_a_wildcard_if_match()
    {
        // RFC 7232 If-Match: * requires the target to exist; against a missing PUT target the
        // wildcard yields 412 (ETag mismatch) rather than 404 (not exists). The scoped PUT lookup
        // misses (no enqueued rows), so ResolveLockedDescriptorForIfMatchAsync returns NotFound.
        var documentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));
        var sessionFactory = new RecordingRelationalWriteSessionFactory(SqlDialect.Pgsql);
        var sut = CreateSut(new StubRelationalWriteTargetLookupService(), sessionFactory);
        var request = CreatePutRequest(
            CreateMappingSet(SqlDialect.Pgsql),
            documentUuid,
            description: "Updated Description"
        ) with
        {
            WritePrecondition = new WritePrecondition.IfMatch("some-wrong-value", IsWildcard: true),
        };

        var result = await sut.HandlePutAsync(request);

        // The PUT target does not exist, so the reason is TargetDoesNotExist rather than Concurrency.
        result
            .Should()
            .BeOfType<UpdateResult.UpdateFailureETagMisMatch>()
            .Which.Reason.Should()
            .Be(ETagPreconditionFailureReason.TargetDoesNotExist);
        sessionFactory.CreateAsyncCallCount.Should().Be(1);
        sessionFactory.Session.CommitCallCount.Should().Be(0);
        sessionFactory.Session.RollbackCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_returns_not_exists_for_descriptor_put_when_the_target_is_missing_under_a_non_wildcard_if_match()
    {
        // Regression guard: a non-wildcard If-Match against a missing PUT target still returns 404.
        var documentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));
        var sessionFactory = new RecordingRelationalWriteSessionFactory(SqlDialect.Pgsql);
        var sut = CreateSut(new StubRelationalWriteTargetLookupService(), sessionFactory);
        var request = CreatePutRequest(
            CreateMappingSet(SqlDialect.Pgsql),
            documentUuid,
            description: "Updated Description"
        ) with
        {
            WritePrecondition = new WritePrecondition.IfMatch("\"current-etag\""),
        };

        var result = await sut.HandlePutAsync(request);

        result.Should().BeOfType<UpdateResult.UpdateFailureNotExists>();
        sessionFactory.CreateAsyncCallCount.Should().Be(1);
        sessionFactory.Session.CommitCallCount.Should().Be(0);
        sessionFactory.Session.RollbackCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_rolls_back_descriptor_put_update_transactions_when_if_match_is_absent_and_the_write_fails()
    {
        var documentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));
        var targetLookupService = new StubRelationalWriteTargetLookupService
        {
            PutResult = new RelationalWriteTargetLookupResult.ExistingDocument(345L, documentUuid, 44L),
        };
        var sessionFactory = new RecordingRelationalWriteSessionFactory(SqlDialect.Pgsql);
        sessionFactory.Session.ScalarResults.Enqueue(44L);
        sessionFactory.Session.Executor.ResultSets.Enqueue([
            CreatePersistedDescriptorRow(description: "Previous Description"),
        ]);
        sessionFactory.Session.Executor.CommandExceptionFactory = command =>
            command.CommandText.Contains("UPDATE dms.\"Descriptor\"", StringComparison.Ordinal)
                ? new StubDbException("descriptor update failed")
                : null;
        var sut = CreateSut(targetLookupService, sessionFactory);
        var request = CreatePutRequest(
            CreateMappingSet(SqlDialect.Pgsql),
            documentUuid,
            description: "Updated Description"
        );

        var result = await sut.HandlePutAsync(request);

        result.Should().BeOfType<UpdateResult.UnknownFailure>();
        sessionFactory.CreateAsyncCallCount.Should().Be(1);
        sessionFactory.Session.CommitCallCount.Should().Be(0);
        sessionFactory.Session.RollbackCallCount.Should().Be(1);
        sessionFactory.Session.DisposeCallCount.Should().Be(1);
        sessionFactory.Session.Executor.Commands.Should().HaveCount(2);
        sessionFactory.Session.Executor.Commands[1].CommandText.Should().Contain("UPDATE dms.\"Descriptor\"");
    }

    [Test]
    public async Task It_updates_descriptor_put_when_if_match_exactly_matches_current_etag()
    {
        var documentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));
        var targetLookupService = new StubRelationalWriteTargetLookupService
        {
            PutResult = new RelationalWriteTargetLookupResult.ExistingDocument(345L, documentUuid, 44L),
        };
        var sessionFactory = new RecordingRelationalWriteSessionFactory(SqlDialect.Pgsql);
        var currentEtag = ExpectedComposedDescriptorEtag(44L);
        sessionFactory.Session.Executor.ResultSets.Enqueue([CreateResolvedExistingDocumentRow(documentUuid)]);
        sessionFactory.Session.ScalarResults.Enqueue(44L);
        sessionFactory.Session.Executor.ResultSets.Enqueue([
            CreatePersistedDescriptorRow(description: "Current Charter"),
        ]);
        sessionFactory.Session.Executor.ResultSets.Enqueue([CreateContentVersionRow(45L)]);
        var sut = CreateSut(targetLookupService, sessionFactory);
        var request = CreatePutRequest(
            CreateMappingSet(SqlDialect.Pgsql),
            documentUuid,
            description: "Updated Charter"
        ) with
        {
            WritePrecondition = new WritePrecondition.IfMatch(currentEtag),
        };

        var result = await sut.HandlePutAsync(request);

        result
            .Should()
            .BeEquivalentTo(
                new UpdateResult.UpdateSuccess(documentUuid, ExpectedComposedDescriptorEtag(45L))
            );
        sessionFactory.CreateAsyncCallCount.Should().Be(1);
        sessionFactory.Session.CommitCallCount.Should().Be(1);
        sessionFactory.Session.RollbackCallCount.Should().Be(0);
        sessionFactory.Session.DisposeCallCount.Should().Be(1);
        sessionFactory.Session.Executor.Commands.Should().HaveCount(3);
        sessionFactory.Session.Executor.Commands[2].CommandText.Should().Contain("UPDATE dms.\"Descriptor\"");
    }

    [Test]
    public async Task It_updates_descriptor_put_when_if_match_uses_an_etag_obtained_under_a_readable_profile()
    {
        // The served descriptor _etag is now profile-sensitive on GET (DescriptorReadHandler composes
        // it with the active readable profile's name, per IServedEtagComposer). If-Match, however,
        // remains profile-insensitive (EtagMatchProjection.Of drops profileCode): a client that read a
        // descriptor through a profile lens, then PUTs unprofiled using that same etag, must still
        // succeed as long as ContentVersion/schemaEpoch agree.
        var documentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));
        var targetLookupService = new StubRelationalWriteTargetLookupService
        {
            PutResult = new RelationalWriteTargetLookupResult.ExistingDocument(345L, documentUuid, 44L),
        };
        var sessionFactory = new RecordingRelationalWriteSessionFactory(SqlDialect.Pgsql);
        var mappingSet = CreateMappingSet(SqlDialect.Pgsql);
        var profileObtainedEtag = _servedEtagComposer.Compose(
            new ServedEtagContext(
                mappingSet.Key.EffectiveSchemaHash,
                ResponseFormat.Json,
                ProfileName: "Some-Readable-Profile",
                LinksEnabled: false,
                ContentVersion: 44L
            )
        );
        var unprofiledEtag = ExpectedComposedDescriptorEtag(44L);
        profileObtainedEtag.Should().NotBe(unprofiledEtag);
        sessionFactory.Session.Executor.ResultSets.Enqueue([CreateResolvedExistingDocumentRow(documentUuid)]);
        sessionFactory.Session.ScalarResults.Enqueue(44L);
        sessionFactory.Session.Executor.ResultSets.Enqueue([
            CreatePersistedDescriptorRow(description: "Current Charter"),
        ]);
        sessionFactory.Session.Executor.ResultSets.Enqueue([CreateContentVersionRow(45L)]);
        var sut = CreateSut(targetLookupService, sessionFactory);
        var request = CreatePutRequest(mappingSet, documentUuid, description: "Updated Charter") with
        {
            WritePrecondition = new WritePrecondition.IfMatch(profileObtainedEtag),
        };

        var result = await sut.HandlePutAsync(request);

        result.Should().NotBeOfType<UpdateResult.UpdateFailureETagMisMatch>();
        result
            .Should()
            .BeEquivalentTo(
                new UpdateResult.UpdateSuccess(documentUuid, ExpectedComposedDescriptorEtag(45L))
            );
        sessionFactory.CreateAsyncCallCount.Should().Be(1);
        sessionFactory.Session.CommitCallCount.Should().Be(1);
        sessionFactory.Session.RollbackCallCount.Should().Be(0);
        sessionFactory.Session.DisposeCallCount.Should().Be(1);
        sessionFactory.Session.Executor.Commands.Should().HaveCount(3);
        sessionFactory.Session.Executor.Commands[2].CommandText.Should().Contain("UPDATE dms.\"Descriptor\"");
    }

    [Test]
    public async Task It_updates_descriptor_put_when_if_match_is_a_wildcard_against_an_existing_descriptor()
    {
        // RFC 7232 If-Match: * succeeds against any existing target, even when the supplied opaque
        // value would not match the current etag.
        var documentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));
        var targetLookupService = new StubRelationalWriteTargetLookupService
        {
            PutResult = new RelationalWriteTargetLookupResult.ExistingDocument(345L, documentUuid, 44L),
        };
        var sessionFactory = new RecordingRelationalWriteSessionFactory(SqlDialect.Pgsql);
        sessionFactory.Session.Executor.ResultSets.Enqueue([CreateResolvedExistingDocumentRow(documentUuid)]);
        sessionFactory.Session.ScalarResults.Enqueue(44L);
        sessionFactory.Session.Executor.ResultSets.Enqueue([
            CreatePersistedDescriptorRow(description: "Current Charter"),
        ]);
        sessionFactory.Session.Executor.ResultSets.Enqueue([CreateContentVersionRow(45L)]);
        var sut = CreateSut(targetLookupService, sessionFactory);
        var request = CreatePutRequest(
            CreateMappingSet(SqlDialect.Pgsql),
            documentUuid,
            description: "Updated Charter"
        ) with
        {
            WritePrecondition = new WritePrecondition.IfMatch("some-wrong-value", IsWildcard: true),
        };

        var result = await sut.HandlePutAsync(request);

        result.Should().NotBeOfType<UpdateResult.UpdateFailureETagMisMatch>();
        result
            .Should()
            .BeEquivalentTo(
                new UpdateResult.UpdateSuccess(documentUuid, ExpectedComposedDescriptorEtag(45L))
            );
        sessionFactory.CreateAsyncCallCount.Should().Be(1);
        sessionFactory.Session.CommitCallCount.Should().Be(1);
        sessionFactory.Session.RollbackCallCount.Should().Be(0);
        sessionFactory.Session.DisposeCallCount.Should().Be(1);
        sessionFactory.Session.Executor.Commands.Should().HaveCount(3);
        sessionFactory.Session.Executor.Commands[2].CommandText.Should().Contain("UPDATE dms.\"Descriptor\"");
    }

    [Test]
    public async Task It_preserves_descriptor_put_immutable_identity_failures_after_exact_if_match()
    {
        var documentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));
        var targetLookupService = new StubRelationalWriteTargetLookupService
        {
            PutResult = new RelationalWriteTargetLookupResult.ExistingDocument(345L, documentUuid, 44L),
        };
        var sessionFactory = new RecordingRelationalWriteSessionFactory(SqlDialect.Pgsql);
        var currentEtag = ExpectedComposedDescriptorEtag(44L);
        sessionFactory.Session.Executor.ResultSets.Enqueue([CreateResolvedExistingDocumentRow(documentUuid)]);
        sessionFactory.Session.ScalarResults.Enqueue(44L);
        sessionFactory.Session.Executor.ResultSets.Enqueue([CreatePersistedDescriptorRow()]);
        var sut = CreateSut(targetLookupService, sessionFactory);
        var request = new DescriptorWriteRequest(
            CreateMappingSet(SqlDialect.Pgsql),
            _descriptorResource,
            JsonNode.Parse(
                """
                {
                  "namespace": "uri://ed-fi.org/SchoolTypeDescriptor",
                  "codeValue": "Alternative",
                  "shortDescription": "Alternative",
                  "description": "Alternative",
                  "effectiveBeginDate": "2024-01-01"
                }
                """
            )!,
            documentUuid,
            null,
            new TraceId("descriptor-put-immutable-if-match")
        )
        {
            WritePrecondition = new WritePrecondition.IfMatch(currentEtag),
        };

        var result = await sut.HandlePutAsync(request);

        result.Should().BeOfType<UpdateResult.UpdateFailureImmutableIdentity>();
        sessionFactory.CreateAsyncCallCount.Should().Be(1);
        sessionFactory.Session.CommitCallCount.Should().Be(0);
        sessionFactory.Session.RollbackCallCount.Should().Be(1);
        sessionFactory.Session.DisposeCallCount.Should().Be(1);
        sessionFactory.Session.Executor.Commands.Should().HaveCount(2);
        sessionFactory
            .Session.Executor.Commands.Should()
            .NotContain(command =>
                command.CommandText.Contains("UPDATE dms.\"Descriptor\"", StringComparison.Ordinal)
            );
    }

    [Test]
    public async Task It_returns_precondition_failed_for_descriptor_delete_when_if_match_mismatches()
    {
        var documentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));
        var sessionFactory = new RecordingRelationalWriteSessionFactory(SqlDialect.Pgsql);
        sessionFactory.Session.Executor.ResultSets.Enqueue([CreateResolvedExistingDocumentRow(documentUuid)]);
        sessionFactory.Session.ScalarResults.Enqueue(44L);
        sessionFactory.Session.Executor.ResultSets.Enqueue([CreatePersistedDescriptorRow()]);
        var sut = CreateSut(new StubRelationalWriteTargetLookupService(), sessionFactory);
        var request = CreateDeleteRequest(CreateMappingSet(SqlDialect.Pgsql), documentUuid) with
        {
            WritePrecondition = new WritePrecondition.IfMatch("\"stale-etag\""),
        };

        var result = await sut.HandleDeleteAsync(request);

        // The target exists but its current etag does not match the specific-tag If-Match precondition,
        // so the reason is Concurrency rather than TargetDoesNotExist.
        result
            .Should()
            .BeOfType<DeleteResult.DeleteFailureETagMisMatch>()
            .Which.Reason.Should()
            .Be(ETagPreconditionFailureReason.Concurrency);
        sessionFactory.CreateAsyncCallCount.Should().Be(1);
        sessionFactory.Session.CommitCallCount.Should().Be(0);
        sessionFactory.Session.RollbackCallCount.Should().Be(1);
        sessionFactory.Session.Executor.Commands.Should().HaveCount(2);
        sessionFactory
            .Session.Executor.Commands.Should()
            .NotContain(command =>
                command.CommandText.Contains("DELETE FROM dms.\"Document\"", StringComparison.Ordinal)
            );
        sessionFactory.Session.ScalarCommands.Should().ContainSingle();
        sessionFactory.Session.ScalarCommands[0].CommandText.Should().Contain("FOR UPDATE");
    }

    [Test]
    public async Task It_deletes_the_descriptor_when_delete_if_match_exactly_matches_the_current_etag()
    {
        var documentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));
        var sessionFactory = new RecordingRelationalWriteSessionFactory(SqlDialect.Pgsql);
        var currentEtag = ExpectedComposedDescriptorEtag(44L);
        sessionFactory.Session.Executor.ResultSets.Enqueue([CreateResolvedExistingDocumentRow(documentUuid)]);
        sessionFactory.Session.ScalarResults.Enqueue(44L);
        sessionFactory.Session.Executor.ResultSets.Enqueue([CreatePersistedDescriptorRow()]);
        sessionFactory.Session.Executor.ResultSets.Enqueue([
            InMemoryRelationalResultSet.Create(new Dictionary<string, object?> { ["DocumentId"] = 345L }),
        ]);
        var sut = CreateSut(new StubRelationalWriteTargetLookupService(), sessionFactory);
        var request = CreateDeleteRequest(CreateMappingSet(SqlDialect.Pgsql), documentUuid) with
        {
            WritePrecondition = new WritePrecondition.IfMatch(currentEtag),
        };

        var result = await sut.HandleDeleteAsync(request);

        result.Should().BeOfType<DeleteResult.DeleteSuccess>();
        sessionFactory.CreateAsyncCallCount.Should().Be(1);
        sessionFactory.Session.CommitCallCount.Should().Be(1);
        sessionFactory.Session.RollbackCallCount.Should().Be(0);
        sessionFactory.Session.Executor.Commands.Should().HaveCount(3);
        sessionFactory
            .Session.Executor.Commands[2]
            .CommandText.Should()
            .Contain("DELETE FROM dms.\"Document\"");
    }

    [Test]
    public async Task It_deletes_the_descriptor_when_delete_if_match_uses_an_etag_obtained_under_a_readable_profile()
    {
        // Mirrors the PUT invariant test above: a descriptor DELETE using an If-Match value obtained
        // from a profiled GET must still succeed against the (always profile-insensitive) write path.
        var documentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));
        var sessionFactory = new RecordingRelationalWriteSessionFactory(SqlDialect.Pgsql);
        var mappingSet = CreateMappingSet(SqlDialect.Pgsql);
        var profileObtainedEtag = _servedEtagComposer.Compose(
            new ServedEtagContext(
                mappingSet.Key.EffectiveSchemaHash,
                ResponseFormat.Json,
                ProfileName: "Some-Readable-Profile",
                LinksEnabled: false,
                ContentVersion: 44L
            )
        );
        profileObtainedEtag.Should().NotBe(ExpectedComposedDescriptorEtag(44L));
        sessionFactory.Session.Executor.ResultSets.Enqueue([CreateResolvedExistingDocumentRow(documentUuid)]);
        sessionFactory.Session.ScalarResults.Enqueue(44L);
        sessionFactory.Session.Executor.ResultSets.Enqueue([CreatePersistedDescriptorRow()]);
        sessionFactory.Session.Executor.ResultSets.Enqueue([
            InMemoryRelationalResultSet.Create(new Dictionary<string, object?> { ["DocumentId"] = 345L }),
        ]);
        var sut = CreateSut(new StubRelationalWriteTargetLookupService(), sessionFactory);
        var request = CreateDeleteRequest(mappingSet, documentUuid) with
        {
            WritePrecondition = new WritePrecondition.IfMatch(profileObtainedEtag),
        };

        var result = await sut.HandleDeleteAsync(request);

        result.Should().BeOfType<DeleteResult.DeleteSuccess>();
        sessionFactory.CreateAsyncCallCount.Should().Be(1);
        sessionFactory.Session.CommitCallCount.Should().Be(1);
        sessionFactory.Session.RollbackCallCount.Should().Be(0);
        sessionFactory.Session.Executor.Commands.Should().HaveCount(3);
        sessionFactory
            .Session.Executor.Commands[2]
            .CommandText.Should()
            .Contain("DELETE FROM dms.\"Document\"");
    }

    [TestCase(
        SqlDialect.Pgsql,
        "DELETE FROM dms.\"Descriptor\"",
        "DELETE FROM dms.\"Document\"",
        "RETURNING \"DocumentId\""
    )]
    [TestCase(
        SqlDialect.Mssql,
        "DELETE FROM [dms].[Descriptor]",
        "DELETE FROM [dms].[Document]",
        "OUTPUT DELETED.[DocumentId]"
    )]
    public async Task It_deletes_the_shared_descriptor_row_before_the_document_row_when_delete_if_match_matches(
        SqlDialect dialect,
        string descriptorDeleteFragment,
        string documentDeleteFragment,
        string finalResultFragment
    )
    {
        var documentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));
        var sessionFactory = new RecordingRelationalWriteSessionFactory(dialect);
        var currentEtag = ExpectedComposedDescriptorEtag(44L);
        sessionFactory.Session.Executor.ResultSets.Enqueue([CreateResolvedExistingDocumentRow(documentUuid)]);
        sessionFactory.Session.ScalarResults.Enqueue(44L);
        sessionFactory.Session.Executor.ResultSets.Enqueue([CreatePersistedDescriptorRow()]);
        sessionFactory.Session.Executor.ResultSets.Enqueue([
            InMemoryRelationalResultSet.Create(),
            InMemoryRelationalResultSet.Create(new Dictionary<string, object?> { ["DocumentId"] = 345L }),
        ]);
        var sut = CreateSut(new StubRelationalWriteTargetLookupService(), sessionFactory);
        var request = CreateDeleteRequest(CreateMappingSet(dialect), documentUuid) with
        {
            WritePrecondition = new WritePrecondition.IfMatch(currentEtag),
        };

        var result = await sut.HandleDeleteAsync(request);

        result.Should().BeOfType<DeleteResult.DeleteSuccess>();
        sessionFactory.Session.Executor.Commands.Should().HaveCount(3);
        var deleteCommand = sessionFactory.Session.Executor.Commands[2];
        var statements = deleteCommand.CommandText.Split(
            ';',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
        );
        statements.Should().NotBeEmpty();
        var finalStatement = statements[^1];

        deleteCommand.CommandText.Should().Contain(descriptorDeleteFragment);
        deleteCommand.CommandText.Should().Contain(documentDeleteFragment);
        finalStatement.Should().Contain(documentDeleteFragment);
        finalStatement.Should().Contain(finalResultFragment);
        deleteCommand
            .CommandText.IndexOf(descriptorDeleteFragment, StringComparison.Ordinal)
            .Should()
            .BeLessThan(deleteCommand.CommandText.IndexOf(documentDeleteFragment, StringComparison.Ordinal));
    }

    [Test]
    public async Task It_returns_not_exists_for_descriptor_delete_when_the_scoped_lookup_misses_under_if_match()
    {
        var documentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));
        var sessionFactory = new RecordingRelationalWriteSessionFactory(SqlDialect.Pgsql);
        var sut = CreateSut(new StubRelationalWriteTargetLookupService(), sessionFactory);
        var request = CreateDeleteRequest(CreateMappingSet(SqlDialect.Pgsql), documentUuid) with
        {
            WritePrecondition = new WritePrecondition.IfMatch("\"current-etag\""),
        };

        var result = await sut.HandleDeleteAsync(request);

        result.Should().BeOfType<DeleteResult.DeleteFailureNotExists>();
        sessionFactory.CreateAsyncCallCount.Should().Be(1);
        sessionFactory.Session.CommitCallCount.Should().Be(0);
        sessionFactory.Session.RollbackCallCount.Should().Be(1);
        sessionFactory.Session.Executor.Commands.Should().ContainSingle();
        sessionFactory.Session.ScalarCommands.Should().BeEmpty();
    }

    [Test]
    public async Task It_returns_precondition_failed_for_descriptor_delete_when_the_scoped_lookup_misses_under_a_wildcard_if_match()
    {
        // RFC 7232 If-Match: * requires the target to exist; against a missing DELETE target the
        // wildcard yields 412 (ETag mismatch) rather than 404 (not exists).
        var documentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));
        var sessionFactory = new RecordingRelationalWriteSessionFactory(SqlDialect.Pgsql);
        var sut = CreateSut(new StubRelationalWriteTargetLookupService(), sessionFactory);
        var request = CreateDeleteRequest(CreateMappingSet(SqlDialect.Pgsql), documentUuid) with
        {
            WritePrecondition = new WritePrecondition.IfMatch("some-wrong-value", IsWildcard: true),
        };

        var result = await sut.HandleDeleteAsync(request);

        // The DELETE target does not exist, so the reason is TargetDoesNotExist rather than Concurrency.
        result
            .Should()
            .BeOfType<DeleteResult.DeleteFailureETagMisMatch>()
            .Which.Reason.Should()
            .Be(ETagPreconditionFailureReason.TargetDoesNotExist);
        sessionFactory.CreateAsyncCallCount.Should().Be(1);
        sessionFactory.Session.CommitCallCount.Should().Be(0);
        sessionFactory.Session.RollbackCallCount.Should().Be(1);
        sessionFactory.Session.Executor.Commands.Should().ContainSingle();
        sessionFactory.Session.ScalarCommands.Should().BeEmpty();
    }

    [Test]
    public async Task It_preserves_descriptor_delete_fk_conflict_mapping_after_an_exact_if_match()
    {
        const string constraintName = "FK_School_SchoolTypeDescriptor";
        var documentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));
        var classifier = new ConfigurableRelationalWriteExceptionClassifier
        {
            IsForeignKeyViolationToReturn = true,
            ClassificationToReturn = new RelationalWriteExceptionClassification.ForeignKeyConstraintViolation(
                constraintName
            ),
        };
        var resolver = A.Fake<IRelationalDeleteConstraintResolver>();
        var referencingResource = new QualifiedResourceName("Ed-Fi", "School");
        A.CallTo(() => resolver.TryResolveReferencingResource(A<DerivedRelationalModelSet>._, constraintName))
            .Returns(referencingResource);

        var sessionFactory = new RecordingRelationalWriteSessionFactory(SqlDialect.Pgsql);
        var currentEtag = ExpectedComposedDescriptorEtag(44L);
        sessionFactory.Session.Executor.ResultSets.Enqueue([CreateResolvedExistingDocumentRow(documentUuid)]);
        sessionFactory.Session.ScalarResults.Enqueue(44L);
        sessionFactory.Session.Executor.ResultSets.Enqueue([CreatePersistedDescriptorRow()]);
        sessionFactory.Session.Executor.CommandExceptionFactory = command =>
            command.CommandText.Contains("DELETE FROM dms.\"Document\"", StringComparison.Ordinal)
                ? new StubDbException("FK constraint violation")
                : null;

        var sut = CreateSut(
            new StubRelationalWriteTargetLookupService(),
            sessionFactory,
            classifier,
            resolver
        );
        var request = CreateDeleteRequest(CreateMappingSet(SqlDialect.Pgsql), documentUuid) with
        {
            WritePrecondition = new WritePrecondition.IfMatch(currentEtag),
        };

        var result = await sut.HandleDeleteAsync(request);

        result
            .Should()
            .BeEquivalentTo(new DeleteResult.DeleteFailureReference([referencingResource.ResourceName]));
        sessionFactory.CreateAsyncCallCount.Should().Be(1);
        sessionFactory.Session.CommitCallCount.Should().Be(0);
        sessionFactory.Session.RollbackCallCount.Should().Be(1);
        A.CallTo(() => resolver.TryResolveReferencingResource(A<DerivedRelationalModelSet>._, constraintName))
            .MustHaveHappenedOnceExactly();
    }

    private static string ExpectedComposedDescriptorEtag(long contentVersion) =>
        EtagComposer.Compose(
            contentVersion,
            DescriptorEtagTestSupport.NoProfileNoLinksJsonVariantKey(
                CreateMappingSet(SqlDialect.Pgsql).Key.EffectiveSchemaHash
            )
        );

    private static InMemoryRelationalResultSet CreateContentVersionRow(long contentVersion) =>
        InMemoryRelationalResultSet.Create(
            new Dictionary<string, object?> { ["ContentVersion"] = contentVersion }
        );

    private static InMemoryRelationalResultSet CreateResolvedExistingDocumentRow(DocumentUuid documentUuid)
    {
        return InMemoryRelationalResultSet.Create(
            new Dictionary<string, object?>
            {
                ["DocumentId"] = 345L,
                ["DocumentUuid"] = documentUuid.Value,
                ["ResourceKeyId"] = 1,
                ["ContentVersion"] = 44L,
            }
        );
    }

    private static InMemoryRelationalResultSet CreatePersistedDescriptorRow(string description = "Charter")
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
                ["EffectiveEndDate"] = null,
            }
        );
    }

    private static DescriptorWriteHandler CreateSut(
        IRelationalWriteTargetLookupService targetLookupService,
        RecordingRelationalWriteSessionFactory sessionFactory,
        IRelationalWriteExceptionClassifier? classifier = null,
        IRelationalDeleteConstraintResolver? deleteConstraintResolver = null
    )
    {
        return new DescriptorWriteHandler(
            targetLookupService,
            classifier ?? new NoOpRelationalWriteExceptionClassifier(),
            deleteConstraintResolver ?? A.Fake<IRelationalDeleteConstraintResolver>(),
            sessionFactory,
            NullLogger<DescriptorWriteHandler>.Instance,
            new ServedEtagComposer(),
            new IfMatchEvaluator()
        );
    }

    private static DescriptorWriteRequest CreatePostRequest(
        MappingSet mappingSet,
        DocumentUuid documentUuid,
        string description = "Charter"
    )
    {
        return new DescriptorWriteRequest(
            mappingSet,
            _descriptorResource,
            CreateRequestBody(description),
            documentUuid,
            new ReferentialId(Guid.Parse("cccccccc-1111-2222-3333-dddddddddddd")),
            new TraceId("descriptor-post-precondition")
        );
    }

    private static DescriptorWriteRequest CreatePutRequest(
        MappingSet mappingSet,
        DocumentUuid documentUuid,
        string description = "Charter"
    )
    {
        return new DescriptorWriteRequest(
            mappingSet,
            _descriptorResource,
            CreateRequestBody(description),
            documentUuid,
            null,
            new TraceId("descriptor-put-precondition")
        );
    }

    private static DescriptorDeleteRequest CreateDeleteRequest(
        MappingSet mappingSet,
        DocumentUuid documentUuid
    )
    {
        return new DescriptorDeleteRequest(
            mappingSet,
            _descriptorResource,
            documentUuid,
            new TraceId("descriptor-delete-precondition")
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

    private static MappingSet CreateMappingSet(SqlDialect dialect)
    {
        var resourceKey = new ResourceKeyEntry(1, _descriptorResource, "1.0.0", true);
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

    private sealed class RecordingRelationalWriteSessionFactory(SqlDialect dialect)
        : IRelationalWriteSessionFactory
    {
        public int CreateAsyncCallCount { get; private set; }

        public RecordingRelationalWriteSession Session { get; } = new(dialect);

        public Task<IRelationalWriteSession> CreateAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CreateAsyncCallCount++;
            return Task.FromResult<IRelationalWriteSession>(Session);
        }
    }

    private sealed class RecordingRelationalWriteSession : IRelationalWriteSession
    {
        private readonly RecordingDbConnection _connection = new(
            new RecordingDbCommand(new DataTable().CreateDataReader())
        );
        private readonly RecordingDbTransaction _transaction;

        public RecordingRelationalWriteSession(
            SqlDialect dialect,
            RecordingRelationalCommandExecutor? executor = null
        )
        {
            _transaction = new RecordingDbTransaction(_connection, IsolationLevel.ReadCommitted);
            Executor = executor ?? new RecordingRelationalCommandExecutor(dialect);
        }

        public DbConnection Connection => _connection;

        public DbTransaction Transaction => _transaction;

        public RecordingRelationalCommandExecutor Executor { get; }

        public Queue<object?> ScalarResults { get; } = [];

        public List<RelationalCommand> ScalarCommands { get; } = [];

        public int CommitCallCount { get; private set; }

        public int RollbackCallCount { get; private set; }

        public int DisposeCallCount { get; private set; }

        public DbCommand CreateCommand(RelationalCommand command)
        {
            ScalarCommands.Add(command);

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

    private sealed class RecordingRelationalCommandExecutor(SqlDialect dialect) : IRelationalCommandExecutor
    {
        public SqlDialect Dialect { get; } = dialect;

        public Queue<IReadOnlyList<InMemoryRelationalResultSet>> ResultSets { get; } = [];

        public List<RelationalCommand> Commands { get; } = [];

        public Func<RelationalCommand, Exception?>? CommandExceptionFactory { get; set; }

        public async Task<TResult> ExecuteReaderAsync<TResult>(
            RelationalCommand command,
            Func<IRelationalCommandReader, CancellationToken, Task<TResult>> readAsync,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            Commands.Add(command);

            if (CommandExceptionFactory?.Invoke(command) is { } exception)
            {
                throw exception;
            }

            IReadOnlyList<InMemoryRelationalResultSet> resultSets =
                ResultSets.Count == 0 ? [] : ResultSets.Dequeue();

            await using var reader = new InMemoryRelationalCommandReader(resultSets);
            return await readAsync(reader, cancellationToken);
        }
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

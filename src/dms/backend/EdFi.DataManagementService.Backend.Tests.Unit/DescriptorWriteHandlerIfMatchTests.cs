// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Backend.Tests.Unit.TestSupport;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Profile;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

// ─────────────────────────────────────────────────────────────────────────────
// Descriptor PUT – If-Match scenarios
// ─────────────────────────────────────────────────────────────────────────────

[TestFixture]
[Parallelizable]
public class Given_Descriptor_PUT_if_match_header_is_absent
{
    private UpdateResult _result = default!;
    private DescriptorIfMatchCommandRecorder _commandExecutor = default!;

    [SetUp]
    public async Task Arrange_and_act()
    {
        var documentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));
        var targetLookup = new DescriptorIfMatchTargetLookupStub
        {
            PutResult = new RelationalWriteTargetLookupResult.ExistingDocument(345L, documentUuid, 44L),
        };
        _commandExecutor = new DescriptorIfMatchCommandRecorder(SqlDialect.Pgsql);
        _commandExecutor.ResultSets.Enqueue([
            InMemoryRelationalResultSet.Create(DescriptorIfMatchHelper.StandardPersistedRow()),
        ]);

        var sut = DescriptorIfMatchHelper.CreateSut(targetLookup, _commandExecutor);
        var request = DescriptorIfMatchHelper.CreatePutRequest(documentUuid, ifMatchEtag: null);

        _result = await sut.HandlePutAsync(request);
    }

    [Test]
    public void It_returns_update_success()
    {
        _result.Should().BeOfType<UpdateResult.UpdateSuccess>();
    }

    [Test]
    public void It_does_not_return_an_etag_mismatch()
    {
        _result.Should().NotBeOfType<UpdateResult.UpdateFailureETagMisMatch>();
    }

    [Test]
    public void It_does_not_use_a_locked_select()
    {
        _commandExecutor.Commands.Should().HaveCount(1);
        _commandExecutor.Commands[0].CommandText.Should().NotContain("FOR UPDATE");
        _commandExecutor.Commands[0].CommandText.Should().NotContain("UPDLOCK");
        _commandExecutor.Commands[0].CommandText.Should().NotContain("ROWLOCK");
    }
}

[TestFixture]
[Parallelizable]
public class Given_Descriptor_PUT_if_match_header_matches_persisted_etag
{
    private UpdateResult _result = default!;
    private DescriptorIfMatchWriteSessionFactory _sessionFactory = default!;

    [SetUp]
    public async Task Arrange_and_act()
    {
        var documentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));
        var targetLookup = new DescriptorIfMatchTargetLookupStub
        {
            PutResult = new RelationalWriteTargetLookupResult.ExistingDocument(345L, documentUuid, 44L),
        };

        var persistedRow = DescriptorIfMatchHelper.StandardPersistedRow();

        // Compute the ETag exactly as IsDescriptorEtagMismatch does: from Namespace+CodeValue
        // and mutable fields read as separate DB columns – not reconstructed from a URI.
        var matchingEtag = DescriptorIfMatchHelper.ComputeEtagFromPersistedRow(persistedRow);

        // For If-Match paths, the locked read goes through the write session factory.
        _sessionFactory = new DescriptorIfMatchWriteSessionFactory();
        _sessionFactory.EnqueueDescriptorRow(persistedRow);

        var sut = DescriptorIfMatchHelper.CreateSut(
            targetLookup,
            A.Fake<IRelationalCommandExecutor>(),
            _sessionFactory
        );
        var request = DescriptorIfMatchHelper.CreatePutRequest(documentUuid, ifMatchEtag: matchingEtag);

        _result = await sut.HandlePutAsync(request);
    }

    [Test]
    public void It_returns_update_success()
    {
        _result.Should().BeOfType<UpdateResult.UpdateSuccess>();
    }

    [Test]
    public void It_reads_namespace_and_code_value_as_separate_columns()
    {
        // Verifies the ETag uses Namespace/CodeValue DB columns, not a parsed URI.
        // Matching ETag + unchanged body = no-op; only the locked SELECT is issued.
        _sessionFactory.SessionCommands.Should().HaveCount(1, "only the locked SELECT runs for a no-op");
        _sessionFactory.SessionCommands[0].CommandText.Should().Contain("Namespace");
        _sessionFactory.SessionCommands[0].CommandText.Should().Contain("CodeValue");
    }

    [Test]
    public void It_uses_a_postgresql_locked_select()
    {
        _sessionFactory.SessionCommands.Should().HaveCount(1);
        _sessionFactory.SessionCommands[0].CommandText.Should().Contain("FOR UPDATE");
    }
}

[TestFixture]
[Parallelizable]
public class Given_Descriptor_PUT_if_match_header_is_wildcard
{
    private UpdateResult _result = default!;
    private DescriptorIfMatchWriteSessionFactory _sessionFactory = default!;

    [SetUp]
    public async Task Arrange_and_act()
    {
        var documentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));
        var targetLookup = new DescriptorIfMatchTargetLookupStub
        {
            PutResult = new RelationalWriteTargetLookupResult.ExistingDocument(345L, documentUuid, 44L),
        };

        // If-Match: * is not supported. Wildcard is rejected immediately, before any
        // database work — no locked read is issued.
        _sessionFactory = new DescriptorIfMatchWriteSessionFactory();
        var sut = DescriptorIfMatchHelper.CreateSut(
            targetLookup,
            A.Fake<IRelationalCommandExecutor>(),
            _sessionFactory
        );
        var request = DescriptorIfMatchHelper.CreatePutRequest(documentUuid, ifMatchEtag: "*");

        _result = await sut.HandlePutAsync(request);
    }

    [Test]
    public void It_returns_update_failure_etag_mismatch()
    {
        _result.Should().BeOfType<UpdateResult.UpdateFailureETagMisMatch>();
    }

    [Test]
    public void It_does_not_issue_any_database_commands()
    {
        // Wildcard is explicitly rejected before entering the locked session path.
        _sessionFactory.SessionCommands.Should().BeEmpty();
    }
}

[TestFixture]
[Parallelizable]
public class Given_Descriptor_PUT_if_match_header_mismatches_persisted_etag
{
    private UpdateResult _result = default!;
    private DescriptorIfMatchWriteSessionFactory _sessionFactory = default!;

    [SetUp]
    public async Task Arrange_and_act()
    {
        var documentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));
        var targetLookup = new DescriptorIfMatchTargetLookupStub
        {
            PutResult = new RelationalWriteTargetLookupResult.ExistingDocument(345L, documentUuid, 44L),
        };

        // For If-Match paths, the locked read goes through the write session factory.
        _sessionFactory = new DescriptorIfMatchWriteSessionFactory();
        _sessionFactory.EnqueueDescriptorRow(DescriptorIfMatchHelper.StandardPersistedRow());

        var sut = DescriptorIfMatchHelper.CreateSut(
            targetLookup,
            A.Fake<IRelationalCommandExecutor>(),
            _sessionFactory
        );
        var request = DescriptorIfMatchHelper.CreatePutRequest(
            documentUuid,
            ifMatchEtag: "\"stale-client-etag\""
        );

        _result = await sut.HandlePutAsync(request);
    }

    [Test]
    public void It_returns_update_failure_etag_mismatch()
    {
        _result.Should().BeOfType<UpdateResult.UpdateFailureETagMisMatch>();
    }

    [Test]
    public void It_does_not_execute_any_write_command()
    {
        // Only the locked pre-check SELECT is issued; no UPDATE follows the mismatch.
        _sessionFactory
            .SessionCommands.Should()
            .HaveCount(1, "the mismatch short-circuits before any write");
    }

    [Test]
    public void It_reads_namespace_and_code_value_as_separate_columns()
    {
        _sessionFactory.SessionCommands[0].CommandText.Should().Contain("Namespace");
        _sessionFactory.SessionCommands[0].CommandText.Should().Contain("CodeValue");
    }
}

[TestFixture]
[Parallelizable]
public class Given_Descriptor_PUT_if_match_target_disappears_before_locked_read
{
    private UpdateResult _result = default!;
    private DescriptorIfMatchWriteSessionFactory _sessionFactory = default!;

    [SetUp]
    public async Task Arrange_and_act()
    {
        var documentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));
        var targetLookup = new DescriptorIfMatchTargetLookupStub
        {
            PutResult = new RelationalWriteTargetLookupResult.ExistingDocument(345L, documentUuid, 44L),
        };

        _sessionFactory = new DescriptorIfMatchWriteSessionFactory();

        var sut = DescriptorIfMatchHelper.CreateSut(
            targetLookup,
            A.Fake<IRelationalCommandExecutor>(),
            _sessionFactory
        );
        var request = DescriptorIfMatchHelper.CreatePutRequest(
            documentUuid,
            ifMatchEtag: "\"stale-client-etag\""
        );

        _result = await sut.HandlePutAsync(request);
    }

    [Test]
    public void It_returns_update_failure_etag_mismatch()
    {
        _result.Should().BeOfType<UpdateResult.UpdateFailureETagMisMatch>();
    }

    [Test]
    public void It_only_issues_the_locked_pre_check_select()
    {
        _sessionFactory.SessionCommands.Should().HaveCount(1);
        _sessionFactory.SessionCommands[0].CommandText.Should().Contain("FOR UPDATE");
    }
}

[TestFixture]
[Parallelizable]
public class Given_Descriptor_PUT_if_match_header_matches_and_content_is_changed
{
    private UpdateResult _result = default!;
    private string _expectedEtag = default!;
    private DescriptorIfMatchWriteSessionFactory _sessionFactory = default!;
    private IRelationalCommandExecutor _commandExecutor = default!;

    [SetUp]
    public async Task Arrange_and_act()
    {
        var documentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));
        var targetLookup = new DescriptorIfMatchTargetLookupStub
        {
            PutResult = new RelationalWriteTargetLookupResult.ExistingDocument(345L, documentUuid, 44L),
        };

        var persistedRow = DescriptorIfMatchHelper.StandardPersistedRow();
        var matchingEtag = DescriptorIfMatchHelper.ComputeEtagFromPersistedRow(persistedRow);

        _sessionFactory = new DescriptorIfMatchWriteSessionFactory();
        _sessionFactory.EnqueueDescriptorRow(persistedRow);
        _sessionFactory.EnqueueDescriptorRow(DescriptorIfMatchHelper.ChangedPersistedRow());

        _commandExecutor = A.Fake<IRelationalCommandExecutor>();

        var sut = DescriptorIfMatchHelper.CreateSut(targetLookup, _commandExecutor, _sessionFactory);
        var changedBody = DescriptorIfMatchHelper.CreateChangedRequestBody();
        var request = DescriptorIfMatchHelper.CreatePutRequestWithBody(
            documentUuid,
            matchingEtag,
            changedBody
        );

        _expectedEtag = DescriptorIfMatchHelper.ComputeEtagFromChangedBody();
        _result = await sut.HandlePutAsync(request);
    }

    [Test]
    public void It_returns_update_success()
    {
        _result.Should().BeOfType<UpdateResult.UpdateSuccess>();
    }

    [Test]
    public void It_returns_etag_computed_from_persisted_state()
    {
        ((UpdateResult.UpdateSuccess)_result).ETag.Should().Be(_expectedEtag);
    }

    [Test]
    public void It_does_not_issue_a_follow_up_read_on_the_shared_executor()
    {
        A.CallTo(_commandExecutor).MustNotHaveHappened();
    }

    [Test]
    public void It_issues_the_locked_select_update_and_in_session_readback()
    {
        _sessionFactory.SessionCommands.Should().HaveCount(3);
        _sessionFactory.SessionCommands[0].CommandText.Should().Contain("FOR UPDATE");
        _sessionFactory.SessionCommands[1].CommandText.Should().Contain("UPDATE");
        _sessionFactory.SessionCommands[2].CommandText.Should().Contain("SELECT");
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Descriptor PUT – If-Match + missing descriptor (RFC 7232 §3.1)
// ─────────────────────────────────────────────────────────────────────────────

[TestFixture]
[Parallelizable]
public class Given_Descriptor_PUT_if_match_targets_missing_descriptor
{
    private UpdateResult _result = default!;

    [SetUp]
    public async Task Arrange_and_act()
    {
        // PutResult defaults to NotFound — descriptor does not exist
        var targetLookup = new DescriptorIfMatchTargetLookupStub();
        var commandExecutor = new DescriptorIfMatchCommandRecorder(SqlDialect.Pgsql);
        var sut = DescriptorIfMatchHelper.CreateSut(targetLookup, commandExecutor);
        var documentUuid = new DocumentUuid(Guid.NewGuid());
        var request = DescriptorIfMatchHelper.CreatePutRequest(documentUuid, ifMatchEtag: "\"abc123\"");

        _result = await sut.HandlePutAsync(request);
    }

    [Test]
    public void It_returns_update_failure_etag_mismatch()
    {
        _result.Should().BeOfType<UpdateResult.UpdateFailureETagMisMatch>();
    }
}

[TestFixture]
[Parallelizable]
public class Given_Descriptor_PUT_no_if_match_targets_missing_descriptor
{
    private UpdateResult _result = default!;

    [SetUp]
    public async Task Arrange_and_act()
    {
        var targetLookup = new DescriptorIfMatchTargetLookupStub();
        var commandExecutor = new DescriptorIfMatchCommandRecorder(SqlDialect.Pgsql);
        var sut = DescriptorIfMatchHelper.CreateSut(targetLookup, commandExecutor);
        var documentUuid = new DocumentUuid(Guid.NewGuid());
        var request = DescriptorIfMatchHelper.CreatePutRequest(documentUuid, ifMatchEtag: null);

        _result = await sut.HandlePutAsync(request);
    }

    [Test]
    public void It_returns_update_failure_not_exists()
    {
        _result.Should().BeOfType<UpdateResult.UpdateFailureNotExists>();
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Descriptor POST-as-update – If-Match scenarios
// ─────────────────────────────────────────────────────────────────────────────

[TestFixture]
[Parallelizable]
public class Given_Descriptor_POST_as_update_if_match_header_is_absent
{
    private UpsertResult _result = default!;
    private DescriptorIfMatchCommandRecorder _commandExecutor = default!;

    [SetUp]
    public async Task Arrange_and_act()
    {
        var documentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));
        var targetLookup = new DescriptorIfMatchTargetLookupStub
        {
            PostResult = new RelationalWriteTargetLookupResult.ExistingDocument(345L, documentUuid, 44L),
        };
        _commandExecutor = new DescriptorIfMatchCommandRecorder(SqlDialect.Pgsql);
        _commandExecutor.ResultSets.Enqueue([]);
        _commandExecutor.ResultSets.Enqueue([
            InMemoryRelationalResultSet.Create(DescriptorIfMatchHelper.StandardPersistedRow()),
        ]);

        var sut = DescriptorIfMatchHelper.CreateSut(targetLookup, _commandExecutor);
        var request = DescriptorIfMatchHelper.CreatePostRequest(documentUuid, ifMatchEtag: null);

        _result = await sut.HandlePostAsync(request);
    }

    [Test]
    public void It_returns_upsert_update_success()
    {
        _result.Should().BeOfType<UpsertResult.UpdateSuccess>();
    }

    [Test]
    public void It_does_not_return_an_etag_mismatch()
    {
        _result.Should().NotBeOfType<UpsertResult.UpsertFailureETagMisMatch>();
    }

    [Test]
    public void It_re_reads_committed_descriptor_state_after_the_update()
    {
        _commandExecutor.Commands.Should().HaveCount(2);
        _commandExecutor.Commands[1].CommandText.Should().Contain("SELECT");
        _commandExecutor.Commands[1].CommandText.Should().Contain("Descriptor");
    }
}

[TestFixture]
[Parallelizable]
public class Given_Descriptor_POST_as_update_if_match_header_mismatches_persisted_etag
{
    private UpsertResult _result = default!;
    private DescriptorIfMatchWriteSessionFactory _sessionFactory = default!;

    [SetUp]
    public async Task Arrange_and_act()
    {
        var documentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));
        var targetLookup = new DescriptorIfMatchTargetLookupStub
        {
            PostResult = new RelationalWriteTargetLookupResult.ExistingDocument(345L, documentUuid, 44L),
        };

        // For If-Match paths, the locked read goes through the write session factory.
        _sessionFactory = new DescriptorIfMatchWriteSessionFactory();
        _sessionFactory.EnqueueDescriptorRow(DescriptorIfMatchHelper.StandardPersistedRow());

        var sut = DescriptorIfMatchHelper.CreateSut(
            targetLookup,
            A.Fake<IRelationalCommandExecutor>(),
            _sessionFactory
        );
        var request = DescriptorIfMatchHelper.CreatePostRequest(
            documentUuid,
            ifMatchEtag: "\"stale-client-etag\""
        );

        _result = await sut.HandlePostAsync(request);
    }

    [Test]
    public void It_returns_upsert_failure_etag_mismatch()
    {
        _result.Should().BeOfType<UpsertResult.UpsertFailureETagMisMatch>();
    }

    [Test]
    public void It_reads_namespace_and_code_value_as_separate_columns()
    {
        // Verifies Namespace and CodeValue are read as DB columns, not parsed from Uri.
        _sessionFactory.SessionCommands.Should().HaveCount(1, "only the locked pre-check SELECT is issued");
        _sessionFactory.SessionCommands[0].CommandText.Should().Contain("Namespace");
        _sessionFactory.SessionCommands[0].CommandText.Should().Contain("CodeValue");
    }
}

[TestFixture]
[Parallelizable]
public class Given_Descriptor_POST_as_update_if_match_header_is_wildcard
{
    private UpsertResult _result = default!;
    private DescriptorIfMatchWriteSessionFactory _sessionFactory = default!;

    [SetUp]
    public async Task Arrange_and_act()
    {
        var documentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));
        var targetLookup = new DescriptorIfMatchTargetLookupStub
        {
            PostResult = new RelationalWriteTargetLookupResult.ExistingDocument(345L, documentUuid, 44L),
        };

        // If-Match: * is not supported. Wildcard is rejected immediately, before any
        // database work — no locked read is issued.
        _sessionFactory = new DescriptorIfMatchWriteSessionFactory();
        var sut = DescriptorIfMatchHelper.CreateSut(
            targetLookup,
            A.Fake<IRelationalCommandExecutor>(),
            _sessionFactory
        );
        var request = DescriptorIfMatchHelper.CreatePostRequest(documentUuid, ifMatchEtag: "*");

        _result = await sut.HandlePostAsync(request);
    }

    [Test]
    public void It_returns_upsert_failure_etag_mismatch()
    {
        _result.Should().BeOfType<UpsertResult.UpsertFailureETagMisMatch>();
    }

    [Test]
    public void It_does_not_issue_any_database_commands()
    {
        // Wildcard is explicitly rejected before entering the locked session path.
        _sessionFactory.SessionCommands.Should().BeEmpty();
    }
}

[TestFixture]
[Parallelizable]
public class Given_Descriptor_POST_as_update_target_disappears_before_locked_read
{
    private UpsertResult _result = default!;
    private DescriptorIfMatchWriteSessionFactory _sessionFactory = default!;

    [SetUp]
    public async Task Arrange_and_act()
    {
        var documentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));
        var targetLookup = new DescriptorIfMatchTargetLookupStub
        {
            PostResult = new RelationalWriteTargetLookupResult.ExistingDocument(345L, documentUuid, 44L),
        };

        _sessionFactory = new DescriptorIfMatchWriteSessionFactory();

        var sut = DescriptorIfMatchHelper.CreateSut(
            targetLookup,
            A.Fake<IRelationalCommandExecutor>(),
            _sessionFactory
        );
        var request = DescriptorIfMatchHelper.CreatePostRequest(
            documentUuid,
            ifMatchEtag: "\"stale-client-etag\""
        );

        _result = await sut.HandlePostAsync(request);
    }

    [Test]
    public void It_returns_upsert_failure_etag_mismatch()
    {
        _result.Should().BeOfType<UpsertResult.UpsertFailureETagMisMatch>();
    }

    [Test]
    public void It_only_issues_the_locked_pre_check_select()
    {
        _sessionFactory.SessionCommands.Should().HaveCount(1);
        _sessionFactory.SessionCommands[0].CommandText.Should().Contain("FOR UPDATE");
    }
}

[TestFixture]
[Parallelizable]
public class Given_Descriptor_POST_as_update_if_match_header_matches_persisted_etag_and_content_is_unchanged
{
    private UpsertResult _result = default!;
    private DescriptorIfMatchWriteSessionFactory _sessionFactory = default!;

    [SetUp]
    public async Task Arrange_and_act()
    {
        var documentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));
        var targetLookup = new DescriptorIfMatchTargetLookupStub
        {
            PostResult = new RelationalWriteTargetLookupResult.ExistingDocument(345L, documentUuid, 44L),
        };

        var persistedRow = DescriptorIfMatchHelper.StandardPersistedRow();
        var matchingEtag = DescriptorIfMatchHelper.ComputeEtagFromPersistedRow(persistedRow);

        _sessionFactory = new DescriptorIfMatchWriteSessionFactory();
        _sessionFactory.EnqueueDescriptorRow(persistedRow);

        var sut = DescriptorIfMatchHelper.CreateSut(
            targetLookup,
            A.Fake<IRelationalCommandExecutor>(),
            _sessionFactory
        );
        var request = DescriptorIfMatchHelper.CreatePostRequest(documentUuid, ifMatchEtag: matchingEtag);

        _result = await sut.HandlePostAsync(request);
    }

    [Test]
    public void It_returns_upsert_update_success()
    {
        _result.Should().BeOfType<UpsertResult.UpdateSuccess>();
    }

    [Test]
    public void It_does_not_execute_any_write_command()
    {
        // No-op: ETag matches and content is unchanged; only the locked SELECT is issued.
        _sessionFactory.SessionCommands.Should().HaveCount(1, "only the locked SELECT runs for a no-op");
    }

    [Test]
    public void It_uses_a_postgresql_locked_select()
    {
        _sessionFactory.SessionCommands.Should().HaveCount(1);
        _sessionFactory.SessionCommands[0].CommandText.Should().Contain("FOR UPDATE");
    }

    [Test]
    public void It_reads_namespace_and_code_value_as_separate_columns()
    {
        _sessionFactory.SessionCommands[0].CommandText.Should().Contain("Namespace");
        _sessionFactory.SessionCommands[0].CommandText.Should().Contain("CodeValue");
    }
}

[TestFixture]
[Parallelizable]
public class Given_Descriptor_POST_as_update_if_match_header_matches_and_content_is_changed
{
    private UpsertResult _result = default!;
    private string _expectedEtag = default!;
    private DescriptorIfMatchWriteSessionFactory _sessionFactory = default!;
    private IRelationalCommandExecutor _commandExecutor = default!;

    [SetUp]
    public async Task Arrange_and_act()
    {
        var documentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));
        var targetLookup = new DescriptorIfMatchTargetLookupStub
        {
            PostResult = new RelationalWriteTargetLookupResult.ExistingDocument(345L, documentUuid, 44L),
        };

        var persistedRow = DescriptorIfMatchHelper.StandardPersistedRow();
        var matchingEtag = DescriptorIfMatchHelper.ComputeEtagFromPersistedRow(persistedRow);

        _sessionFactory = new DescriptorIfMatchWriteSessionFactory();
        _sessionFactory.EnqueueDescriptorRow(persistedRow);
        _sessionFactory.EnqueueDescriptorRow(DescriptorIfMatchHelper.ChangedPersistedRow());

        _commandExecutor = A.Fake<IRelationalCommandExecutor>();

        var sut = DescriptorIfMatchHelper.CreateSut(targetLookup, _commandExecutor, _sessionFactory);
        var changedBody = DescriptorIfMatchHelper.CreateChangedRequestBody();
        var request = DescriptorIfMatchHelper.CreatePostRequestWithBody(
            documentUuid,
            matchingEtag,
            changedBody
        );

        _expectedEtag = DescriptorIfMatchHelper.ComputeEtagFromChangedBody();
        _result = await sut.HandlePostAsync(request);
    }

    [Test]
    public void It_returns_upsert_update_success()
    {
        _result.Should().BeOfType<UpsertResult.UpdateSuccess>();
    }

    [Test]
    public void It_returns_etag_computed_from_persisted_state()
    {
        ((UpsertResult.UpdateSuccess)_result).ETag.Should().Be(_expectedEtag);
    }

    [Test]
    public void It_does_not_issue_a_follow_up_read_on_the_shared_executor()
    {
        A.CallTo(_commandExecutor).MustNotHaveHappened();
    }

    [Test]
    public void It_issues_the_locked_select_update_and_in_session_readback()
    {
        _sessionFactory.SessionCommands.Should().HaveCount(3);
        _sessionFactory.SessionCommands[0].CommandText.Should().Contain("FOR UPDATE");
        _sessionFactory.SessionCommands[1].CommandText.Should().Contain("UPDATE");
        _sessionFactory.SessionCommands[2].CommandText.Should().Contain("SELECT");
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Descriptor DELETE – If-Match scenarios
// ─────────────────────────────────────────────────────────────────────────────

[TestFixture]
[Parallelizable]
public class Given_Descriptor_DELETE_if_match_header_is_absent
{
    private DeleteResult _result = default!;

    [SetUp]
    public async Task Arrange_and_act()
    {
        var commandExecutor = new DescriptorIfMatchCommandRecorder(SqlDialect.Pgsql);
        // The DELETE RETURNING query returns the deleted DocumentId row.
        commandExecutor.ResultSets.Enqueue([
            InMemoryRelationalResultSet.Create(new Dictionary<string, object?> { ["DocumentId"] = 345L }),
        ]);

        var sut = DescriptorIfMatchHelper.CreateSut(new DescriptorIfMatchTargetLookupStub(), commandExecutor);

        _result = await DescriptorIfMatchHelper.ExecuteDeleteAsync(
            sut,
            new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb")),
            ifMatchEtag: null
        );
    }

    [Test]
    public void It_returns_delete_success()
    {
        _result.Should().BeOfType<DeleteResult.DeleteSuccess>();
    }

    [Test]
    public void It_does_not_return_an_etag_mismatch()
    {
        _result.Should().NotBeOfType<DeleteResult.DeleteFailureETagMisMatch>();
    }
}

[TestFixture]
[Parallelizable]
public class Given_Descriptor_DELETE_if_match_header_mismatches_persisted_etag
{
    private DeleteResult _result = default!;
    private DescriptorIfMatchWriteSessionFactory _sessionFactory = default!;

    [SetUp]
    public async Task Arrange_and_act()
    {
        // For If-Match paths, the locked read goes through the write session factory.
        _sessionFactory = new DescriptorIfMatchWriteSessionFactory();
        _sessionFactory.EnqueueDescriptorRow(DescriptorIfMatchHelper.StandardPersistedRow());

        var sut = DescriptorIfMatchHelper.CreateSut(
            new DescriptorIfMatchTargetLookupStub(),
            A.Fake<IRelationalCommandExecutor>(),
            _sessionFactory
        );

        _result = await DescriptorIfMatchHelper.ExecuteDeleteAsync(
            sut,
            new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb")),
            ifMatchEtag: "\"stale-client-etag\""
        );
    }

    [Test]
    public void It_returns_delete_failure_etag_mismatch()
    {
        _result.Should().BeOfType<DeleteResult.DeleteFailureETagMisMatch>();
    }

    [Test]
    public void It_reads_namespace_and_code_value_as_separate_columns()
    {
        // Verifies Namespace and CodeValue are read as DB columns, not parsed from Uri.
        _sessionFactory.SessionCommands.Should().HaveCount(1, "only the locked pre-check SELECT is issued");
        _sessionFactory.SessionCommands[0].CommandText.Should().Contain("Namespace");
        _sessionFactory.SessionCommands[0].CommandText.Should().Contain("CodeValue");
    }

    [Test]
    public void It_uses_a_postgresql_locked_select_by_uuid()
    {
        _sessionFactory.SessionCommands.Should().HaveCount(1);
        _sessionFactory.SessionCommands[0].CommandText.Should().Contain("FOR UPDATE");
    }
}

[TestFixture]
[Parallelizable]
public class Given_Descriptor_DELETE_if_match_header_is_wildcard
{
    private DeleteResult _result = default!;

    [SetUp]
    public async Task Arrange_and_act()
    {
        var sut = DescriptorIfMatchHelper.CreateSut(
            new DescriptorIfMatchTargetLookupStub(),
            A.Fake<IRelationalCommandExecutor>(),
            new DescriptorIfMatchWriteSessionFactory()
        );

        _result = await DescriptorIfMatchHelper.ExecuteDeleteAsync(
            sut,
            new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb")),
            ifMatchEtag: "*"
        );
    }

    [Test]
    public void It_returns_delete_failure_etag_mismatch()
    {
        _result.Should().BeOfType<DeleteResult.DeleteFailureETagMisMatch>();
    }
}

[TestFixture]
[Parallelizable]
public class Given_Descriptor_DELETE_if_match_header_is_wildcard_and_target_is_missing
{
    private DeleteResult _result = default!;
    private DescriptorIfMatchWriteSessionFactory _sessionFactory = default!;

    [SetUp]
    public async Task Arrange_and_act()
    {
        _sessionFactory = new DescriptorIfMatchWriteSessionFactory();

        var sut = DescriptorIfMatchHelper.CreateSut(
            new DescriptorIfMatchTargetLookupStub(),
            A.Fake<IRelationalCommandExecutor>(),
            _sessionFactory
        );

        _result = await DescriptorIfMatchHelper.ExecuteDeleteAsync(
            sut,
            new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb")),
            ifMatchEtag: "*"
        );
    }

    [Test]
    public void It_returns_delete_failure_etag_mismatch()
    {
        _result.Should().BeOfType<DeleteResult.DeleteFailureETagMisMatch>();
    }

    [Test]
    public void It_does_not_issue_any_database_commands()
    {
        // Wildcard is explicitly rejected before entering the locked session path.
        _sessionFactory.SessionCommands.Should().BeEmpty();
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Wildcard with existing descriptor – explicit rejection returns ETagMisMatch
// ─────────────────────────────────────────────────────────────────────────────

[TestFixture]
[Parallelizable]
public class Given_Descriptor_PUT_if_match_header_is_wildcard_and_descriptor_exists
{
    private UpdateResult _result = default!;
    private DescriptorIfMatchWriteSessionFactory _sessionFactory = default!;

    [SetUp]
    public async Task Arrange_and_act()
    {
        var documentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));
        var targetLookup = new DescriptorIfMatchTargetLookupStub
        {
            PutResult = new RelationalWriteTargetLookupResult.ExistingDocument(345L, documentUuid, 44L),
        };

        // Wildcard is rejected immediately before any database work, so no rows need
        // to be enqueued — the session factory is never used.
        _sessionFactory = new DescriptorIfMatchWriteSessionFactory();

        var sut = DescriptorIfMatchHelper.CreateSut(
            targetLookup,
            A.Fake<IRelationalCommandExecutor>(),
            _sessionFactory
        );
        var request = DescriptorIfMatchHelper.CreatePutRequest(documentUuid, ifMatchEtag: "*");

        _result = await sut.HandlePutAsync(request);
    }

    [Test]
    public void It_returns_update_failure_etag_mismatch()
    {
        _result.Should().BeOfType<UpdateResult.UpdateFailureETagMisMatch>();
    }

    [Test]
    public void It_does_not_issue_any_database_commands()
    {
        // Wildcard is explicitly rejected before entering the locked session path.
        _sessionFactory.SessionCommands.Should().BeEmpty();
    }
}

[TestFixture]
[Parallelizable]
public class Given_Descriptor_POST_as_update_if_match_header_is_wildcard_and_descriptor_exists
{
    private UpsertResult _result = default!;
    private DescriptorIfMatchWriteSessionFactory _sessionFactory = default!;

    [SetUp]
    public async Task Arrange_and_act()
    {
        var documentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));
        var targetLookup = new DescriptorIfMatchTargetLookupStub
        {
            PostResult = new RelationalWriteTargetLookupResult.ExistingDocument(345L, documentUuid, 44L),
        };

        // Wildcard is rejected immediately before any database work, so no rows need
        // to be enqueued — the session factory is never used.
        _sessionFactory = new DescriptorIfMatchWriteSessionFactory();

        var sut = DescriptorIfMatchHelper.CreateSut(
            targetLookup,
            A.Fake<IRelationalCommandExecutor>(),
            _sessionFactory
        );
        var request = DescriptorIfMatchHelper.CreatePostRequest(documentUuid, ifMatchEtag: "*");

        _result = await sut.HandlePostAsync(request);
    }

    [Test]
    public void It_returns_upsert_failure_etag_mismatch()
    {
        _result.Should().BeOfType<UpsertResult.UpsertFailureETagMisMatch>();
    }

    [Test]
    public void It_does_not_issue_any_database_commands()
    {
        // Wildcard is explicitly rejected before entering the locked session path.
        _sessionFactory.SessionCommands.Should().BeEmpty();
    }
}

[TestFixture]
[Parallelizable]
public class Given_Descriptor_DELETE_if_match_header_is_wildcard_and_descriptor_exists
{
    private DeleteResult _result = default!;
    private DescriptorIfMatchWriteSessionFactory _sessionFactory = default!;

    [SetUp]
    public async Task Arrange_and_act()
    {
        // Wildcard is rejected immediately before any database work, so no rows need
        // to be enqueued — the session factory is never used.
        _sessionFactory = new DescriptorIfMatchWriteSessionFactory();

        var sut = DescriptorIfMatchHelper.CreateSut(
            new DescriptorIfMatchTargetLookupStub(),
            A.Fake<IRelationalCommandExecutor>(),
            _sessionFactory
        );

        _result = await DescriptorIfMatchHelper.ExecuteDeleteAsync(
            sut,
            new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb")),
            ifMatchEtag: "*"
        );
    }

    [Test]
    public void It_returns_delete_failure_etag_mismatch()
    {
        _result.Should().BeOfType<DeleteResult.DeleteFailureETagMisMatch>();
    }

    [Test]
    public void It_does_not_issue_any_database_commands()
    {
        // Wildcard is explicitly rejected before entering the locked session path.
        _sessionFactory.SessionCommands.Should().BeEmpty();
    }
}

[TestFixture]
[Parallelizable]
public class Given_Descriptor_PUT_if_match_header_mismatches_persisted_etag_using_mssql_dialect
{
    private UpdateResult _result = default!;
    private DescriptorIfMatchWriteSessionFactory _sessionFactory = default!;

    [SetUp]
    public async Task Arrange_and_act()
    {
        var documentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));
        var targetLookup = new DescriptorIfMatchTargetLookupStub
        {
            PutResult = new RelationalWriteTargetLookupResult.ExistingDocument(345L, documentUuid, 44L),
        };

        _sessionFactory = new DescriptorIfMatchWriteSessionFactory(SqlDialect.Mssql);
        _sessionFactory.EnqueueDescriptorRow(DescriptorIfMatchHelper.StandardPersistedRow());

        var sut = DescriptorIfMatchHelper.CreateSut(
            targetLookup,
            A.Fake<IRelationalCommandExecutor>(),
            _sessionFactory
        );
        var request = DescriptorIfMatchHelper.CreatePutRequest(
            documentUuid,
            ifMatchEtag: "\"stale-client-etag\"",
            dialect: SqlDialect.Mssql
        );

        _result = await sut.HandlePutAsync(request);
    }

    [Test]
    public void It_returns_update_failure_etag_mismatch()
    {
        _result.Should().BeOfType<UpdateResult.UpdateFailureETagMisMatch>();
    }

    [Test]
    public void It_uses_an_mssql_locked_select()
    {
        _sessionFactory.SessionCommands.Should().HaveCount(1);
        _sessionFactory.SessionCommands[0].CommandText.Should().Contain("WITH (UPDLOCK, ROWLOCK)");
    }
}

[TestFixture]
[Parallelizable]
public class Given_Descriptor_DELETE_if_match_header_mismatches_persisted_etag_using_mssql_dialect
{
    private DeleteResult _result = default!;
    private DescriptorIfMatchWriteSessionFactory _sessionFactory = default!;

    [SetUp]
    public async Task Arrange_and_act()
    {
        _sessionFactory = new DescriptorIfMatchWriteSessionFactory(SqlDialect.Mssql);
        _sessionFactory.EnqueueDescriptorRow(DescriptorIfMatchHelper.StandardPersistedRow());

        var sut = DescriptorIfMatchHelper.CreateSut(
            new DescriptorIfMatchTargetLookupStub(),
            new DescriptorIfMatchCommandRecorder(SqlDialect.Mssql),
            _sessionFactory
        );

        _result = await DescriptorIfMatchHelper.ExecuteDeleteAsync(
            sut,
            new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb")),
            ifMatchEtag: "\"stale-client-etag\"",
            dialect: SqlDialect.Mssql,
            cancellationToken: default
        );
    }

    [Test]
    public void It_returns_delete_failure_etag_mismatch()
    {
        _result.Should().BeOfType<DeleteResult.DeleteFailureETagMisMatch>();
    }

    [Test]
    public void It_uses_an_mssql_locked_select_by_uuid()
    {
        _sessionFactory.SessionCommands.Should().HaveCount(1);
        _sessionFactory.SessionCommands[0].CommandText.Should().Contain("WITH (UPDLOCK, ROWLOCK)");
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Descriptor DELETE – DbException translation in locked session
// ─────────────────────────────────────────────────────────────────────────────

[TestFixture]
[Parallelizable]
public class Given_Descriptor_DELETE_if_match_fk_violation_in_locked_session
{
    private DeleteResult _result = default!;
    private DescriptorIfMatchWriteSessionFactory _sessionFactory = default!;

    [SetUp]
    public async Task Arrange_and_act()
    {
        var matchingEtag = DescriptorIfMatchHelper.ComputeEtagFromPersistedRow(
            DescriptorIfMatchHelper.StandardPersistedRow()
        );

        _sessionFactory = new DescriptorIfMatchWriteSessionFactory();
        _sessionFactory.EnqueueThrowingExecuteAfterRow(
            DescriptorIfMatchHelper.StandardPersistedRow(),
            new FakeFkViolationDbException()
        );

        var sut = DescriptorIfMatchHelper.CreateSut(
            new DescriptorIfMatchTargetLookupStub(),
            A.Fake<IRelationalCommandExecutor>(),
            _sessionFactory,
            new ConfigurableRelationalWriteExceptionClassifier
            {
                ClassificationToReturn =
                    new RelationalWriteExceptionClassification.ForeignKeyConstraintViolation(
                        "fake_fk_constraint"
                    ),
            }
        );

        _result = await DescriptorIfMatchHelper.ExecuteDeleteAsync(
            sut,
            new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb")),
            ifMatchEtag: matchingEtag
        );
    }

    [Test]
    public void It_returns_delete_failure_reference()
    {
        _result.Should().BeOfType<DeleteResult.DeleteFailureReference>();
    }

    [Test]
    public void It_issues_the_locked_select_and_the_delete_command()
    {
        _sessionFactory.SessionCommands.Should().HaveCount(2);
        _sessionFactory.SessionCommands[0].CommandText.Should().Contain("FOR UPDATE");
        _sessionFactory.SessionCommands[1].CommandText.Should().Contain("DELETE");
    }
}

[TestFixture]
[Parallelizable]
public class Given_Descriptor_POST_transient_database_failure
{
    private UpsertResult _result = default!;

    [SetUp]
    public async Task Arrange_and_act()
    {
        var documentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));
        var targetLookup = new DescriptorIfMatchTargetLookupStub
        {
            PostResult = new RelationalWriteTargetLookupResult.CreateNew(documentUuid),
        };
        var commandExecutor = new ThrowingRelationalCommandExecutor(
            SqlDialect.Pgsql,
            new FakeTransientDbException()
        );
        var exceptionClassifier = new ConfigurableRelationalWriteExceptionClassifier
        {
            IsTransientFailureToReturn = true,
        };

        var sut = DescriptorIfMatchHelper.CreateSut(
            targetLookup,
            commandExecutor,
            exceptionClassifier: exceptionClassifier
        );
        var request = DescriptorIfMatchHelper.CreatePostRequest(documentUuid, ifMatchEtag: null);

        _result = await sut.HandlePostAsync(request);
    }

    [Test]
    public void It_returns_upsert_failure_write_conflict()
    {
        _result.Should().BeOfType<UpsertResult.UpsertFailureWriteConflict>();
    }
}

[TestFixture]
[Parallelizable]
public class Given_Descriptor_POST_unique_constraint_violation_classified_by_exception_classifier
{
    private UpsertResult _result = default!;

    [SetUp]
    public async Task Arrange_and_act()
    {
        var documentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));
        var targetLookup = new DescriptorIfMatchTargetLookupStub
        {
            PostResult = new RelationalWriteTargetLookupResult.CreateNew(documentUuid),
        };
        var commandExecutor = new ThrowingRelationalCommandExecutor(
            SqlDialect.Mssql,
            new FakeGenericDbException()
        );
        var exceptionClassifier = new ConfigurableRelationalWriteExceptionClassifier
        {
            ClassificationToReturn = new RelationalWriteExceptionClassification.UniqueConstraintViolation(
                "UX_Descriptor_NaturalKey"
            ),
        };

        var sut = DescriptorIfMatchHelper.CreateSut(
            targetLookup,
            commandExecutor,
            exceptionClassifier: exceptionClassifier
        );
        var request = DescriptorIfMatchHelper.CreatePostRequest(
            documentUuid,
            ifMatchEtag: null,
            dialect: SqlDialect.Mssql
        );

        _result = await sut.HandlePostAsync(request);
    }

    [Test]
    public void It_returns_upsert_failure_write_conflict()
    {
        _result.Should().BeOfType<UpsertResult.UpsertFailureWriteConflict>();
    }
}

[TestFixture]
[Parallelizable]
public class Given_Descriptor_PUT_transient_database_failure
{
    private UpdateResult _result = default!;

    [SetUp]
    public async Task Arrange_and_act()
    {
        var documentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));
        var targetLookup = new DescriptorIfMatchTargetLookupStub
        {
            PutResult = new RelationalWriteTargetLookupResult.ExistingDocument(345L, documentUuid, 44L),
        };
        var commandExecutor = new ThrowingRelationalCommandExecutor(
            SqlDialect.Pgsql,
            new FakeTransientDbException(),
            [
                [InMemoryRelationalResultSet.Create(DescriptorIfMatchHelper.StandardPersistedRow())],
            ],
            throwOnCall: 2
        );
        var exceptionClassifier = new ConfigurableRelationalWriteExceptionClassifier
        {
            IsTransientFailureToReturn = true,
        };

        var sut = DescriptorIfMatchHelper.CreateSut(
            targetLookup,
            commandExecutor,
            exceptionClassifier: exceptionClassifier
        );
        var request = DescriptorIfMatchHelper.CreatePutRequestWithBody(
            documentUuid,
            ifMatchEtag: null,
            DescriptorIfMatchHelper.CreateChangedRequestBody()
        );

        _result = await sut.HandlePutAsync(request);
    }

    [Test]
    public void It_returns_update_failure_write_conflict()
    {
        _result.Should().BeOfType<UpdateResult.UpdateFailureWriteConflict>();
    }
}

[TestFixture]
[Parallelizable]
public class Given_Descriptor_PUT_if_match_transient_database_failure
{
    private UpdateResult _result = default!;

    [SetUp]
    public async Task Arrange_and_act()
    {
        var documentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));
        var targetLookup = new DescriptorIfMatchTargetLookupStub
        {
            PutResult = new RelationalWriteTargetLookupResult.ExistingDocument(345L, documentUuid, 44L),
        };
        var matchingEtag = DescriptorIfMatchHelper.ComputeEtagFromPersistedRow(
            DescriptorIfMatchHelper.StandardPersistedRow()
        );

        var sessionFactory = new DescriptorIfMatchWriteSessionFactory();
        sessionFactory.EnqueueThrowingExecuteAfterRow(
            DescriptorIfMatchHelper.StandardPersistedRow(),
            new FakeTransientDbException()
        );

        var exceptionClassifier = new ConfigurableRelationalWriteExceptionClassifier
        {
            IsTransientFailureToReturn = true,
        };

        var sut = DescriptorIfMatchHelper.CreateSut(
            targetLookup,
            A.Fake<IRelationalCommandExecutor>(),
            sessionFactory,
            exceptionClassifier
        );
        var request = DescriptorIfMatchHelper.CreatePutRequestWithBody(
            documentUuid,
            matchingEtag,
            DescriptorIfMatchHelper.CreateChangedRequestBody()
        );

        _result = await sut.HandlePutAsync(request);
    }

    [Test]
    public void It_returns_update_failure_write_conflict()
    {
        _result.Should().BeOfType<UpdateResult.UpdateFailureWriteConflict>();
    }
}

[TestFixture]
[Parallelizable]
public class Given_Descriptor_PUT_if_match_transient_database_failure_during_locked_read
{
    private UpdateResult _result = default!;

    [SetUp]
    public async Task Arrange_and_act()
    {
        var documentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));
        var targetLookup = new DescriptorIfMatchTargetLookupStub
        {
            PutResult = new RelationalWriteTargetLookupResult.ExistingDocument(345L, documentUuid, 44L),
        };

        var sessionFactory = new DescriptorIfMatchWriteSessionFactory();
        sessionFactory.EnqueueExecuteException(new FakeTransientDbException());

        var sut = DescriptorIfMatchHelper.CreateSut(
            targetLookup,
            A.Fake<IRelationalCommandExecutor>(),
            sessionFactory,
            new ConfigurableRelationalWriteExceptionClassifier { IsTransientFailureToReturn = true }
        );

        _result = await sut.HandlePutAsync(
            DescriptorIfMatchHelper.CreatePutRequestWithBody(
                documentUuid,
                DescriptorIfMatchHelper.ComputeEtagFromPersistedRow(
                    DescriptorIfMatchHelper.StandardPersistedRow()
                ),
                DescriptorIfMatchHelper.CreateChangedRequestBody()
            )
        );
    }

    [Test]
    public void It_returns_update_failure_write_conflict()
    {
        _result.Should().BeOfType<UpdateResult.UpdateFailureWriteConflict>();
    }
}

[TestFixture]
[Parallelizable]
public class Given_Descriptor_POST_as_update_if_match_transient_database_failure_during_locked_read
{
    private UpsertResult _result = default!;

    [SetUp]
    public async Task Arrange_and_act()
    {
        var documentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));
        var targetLookup = new DescriptorIfMatchTargetLookupStub
        {
            PostResult = new RelationalWriteTargetLookupResult.ExistingDocument(345L, documentUuid, 44L),
        };

        var sessionFactory = new DescriptorIfMatchWriteSessionFactory();
        sessionFactory.EnqueueExecuteException(new FakeTransientDbException());

        var sut = DescriptorIfMatchHelper.CreateSut(
            targetLookup,
            A.Fake<IRelationalCommandExecutor>(),
            sessionFactory,
            new ConfigurableRelationalWriteExceptionClassifier { IsTransientFailureToReturn = true }
        );

        _result = await sut.HandlePostAsync(
            DescriptorIfMatchHelper.CreatePostRequestWithBody(
                documentUuid,
                DescriptorIfMatchHelper.ComputeEtagFromPersistedRow(
                    DescriptorIfMatchHelper.StandardPersistedRow()
                ),
                DescriptorIfMatchHelper.CreateChangedRequestBody()
            )
        );
    }

    [Test]
    public void It_returns_upsert_failure_write_conflict()
    {
        _result.Should().BeOfType<UpsertResult.UpsertFailureWriteConflict>();
    }
}

[TestFixture]
[Parallelizable]
public class Given_Descriptor_POST_as_update_if_match_transient_database_failure_during_commit
{
    private UpsertResult _result = default!;

    [SetUp]
    public async Task Arrange_and_act()
    {
        var documentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));
        var targetLookup = new DescriptorIfMatchTargetLookupStub
        {
            PostResult = new RelationalWriteTargetLookupResult.ExistingDocument(345L, documentUuid, 44L),
        };

        var sessionFactory = new DescriptorIfMatchWriteSessionFactory();
        sessionFactory.EnqueueDescriptorRow(DescriptorIfMatchHelper.StandardPersistedRow());
        sessionFactory.EnqueueResultSet([]);
        sessionFactory.EnqueueDescriptorRow(DescriptorIfMatchHelper.ChangedPersistedRow());
        sessionFactory.EnqueueCommitException(new FakeTransientDbException());

        var sut = DescriptorIfMatchHelper.CreateSut(
            targetLookup,
            A.Fake<IRelationalCommandExecutor>(),
            sessionFactory,
            new ConfigurableRelationalWriteExceptionClassifier { IsTransientFailureToReturn = true }
        );

        _result = await sut.HandlePostAsync(
            DescriptorIfMatchHelper.CreatePostRequestWithBody(
                documentUuid,
                DescriptorIfMatchHelper.ComputeEtagFromPersistedRow(
                    DescriptorIfMatchHelper.StandardPersistedRow()
                ),
                DescriptorIfMatchHelper.CreateChangedRequestBody()
            )
        );
    }

    [Test]
    public void It_returns_upsert_failure_write_conflict()
    {
        _result.Should().BeOfType<UpsertResult.UpsertFailureWriteConflict>();
    }
}

[TestFixture]
[Parallelizable]
public class Given_Descriptor_DELETE_if_match_db_error_in_locked_session
{
    private DeleteResult _result = default!;
    private DescriptorIfMatchWriteSessionFactory _sessionFactory = default!;

    [SetUp]
    public async Task Arrange_and_act()
    {
        var matchingEtag = DescriptorIfMatchHelper.ComputeEtagFromPersistedRow(
            DescriptorIfMatchHelper.StandardPersistedRow()
        );

        _sessionFactory = new DescriptorIfMatchWriteSessionFactory();
        _sessionFactory.EnqueueThrowingExecuteAfterRow(
            DescriptorIfMatchHelper.StandardPersistedRow(),
            new FakeGenericDbException()
        );

        var sut = DescriptorIfMatchHelper.CreateSut(
            new DescriptorIfMatchTargetLookupStub(),
            A.Fake<IRelationalCommandExecutor>(),
            _sessionFactory
        );

        _result = await DescriptorIfMatchHelper.ExecuteDeleteAsync(
            sut,
            new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb")),
            ifMatchEtag: matchingEtag
        );
    }

    [Test]
    public void It_returns_unknown_failure()
    {
        _result.Should().BeOfType<DeleteResult.UnknownFailure>();
    }

    [Test]
    public void It_issues_the_locked_select_and_the_delete_command()
    {
        _sessionFactory.SessionCommands.Should().HaveCount(2);
        _sessionFactory.SessionCommands[0].CommandText.Should().Contain("FOR UPDATE");
        _sessionFactory.SessionCommands[1].CommandText.Should().Contain("DELETE");
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Descriptor PUT – DbException translation in locked session
// ─────────────────────────────────────────────────────────────────────────────

[TestFixture]
[Parallelizable]
public class Given_Descriptor_DELETE_if_match_transient_database_failure_during_locked_read
{
    private DeleteResult _result = default!;

    [SetUp]
    public async Task Arrange_and_act()
    {
        var sessionFactory = new DescriptorIfMatchWriteSessionFactory();
        sessionFactory.EnqueueExecuteException(new FakeTransientDbException());

        var sut = DescriptorIfMatchHelper.CreateSut(
            new DescriptorIfMatchTargetLookupStub(),
            A.Fake<IRelationalCommandExecutor>(),
            sessionFactory,
            new ConfigurableRelationalWriteExceptionClassifier { IsTransientFailureToReturn = true }
        );

        _result = await DescriptorIfMatchHelper.ExecuteDeleteAsync(
            sut,
            new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb")),
            ifMatchEtag: DescriptorIfMatchHelper.ComputeEtagFromPersistedRow(
                DescriptorIfMatchHelper.StandardPersistedRow()
            )
        );
    }

    [Test]
    public void It_returns_delete_failure_write_conflict()
    {
        _result.Should().BeOfType<DeleteResult.DeleteFailureWriteConflict>();
    }
}

[TestFixture]
[Parallelizable]
public class Given_Descriptor_DELETE_if_match_transient_database_failure_during_commit
{
    private DeleteResult _result = default!;

    [SetUp]
    public async Task Arrange_and_act()
    {
        var sessionFactory = new DescriptorIfMatchWriteSessionFactory();
        sessionFactory.EnqueueDescriptorRow(DescriptorIfMatchHelper.StandardPersistedRow());
        sessionFactory.EnqueueResultSet([new Dictionary<string, object?> { ["DocumentId"] = 345L }]);
        sessionFactory.EnqueueCommitException(new FakeTransientDbException());

        var sut = DescriptorIfMatchHelper.CreateSut(
            new DescriptorIfMatchTargetLookupStub(),
            A.Fake<IRelationalCommandExecutor>(),
            sessionFactory,
            new ConfigurableRelationalWriteExceptionClassifier { IsTransientFailureToReturn = true }
        );

        _result = await DescriptorIfMatchHelper.ExecuteDeleteAsync(
            sut,
            new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb")),
            ifMatchEtag: DescriptorIfMatchHelper.ComputeEtagFromPersistedRow(
                DescriptorIfMatchHelper.StandardPersistedRow()
            )
        );
    }

    [Test]
    public void It_returns_delete_failure_write_conflict()
    {
        _result.Should().BeOfType<DeleteResult.DeleteFailureWriteConflict>();
    }
}

[TestFixture]
[Parallelizable]
public class Given_Descriptor_PUT_if_match_db_error_in_locked_session
{
    private UpdateResult _result = default!;
    private DescriptorIfMatchWriteSessionFactory _sessionFactory = default!;

    [SetUp]
    public async Task Arrange_and_act()
    {
        var matchingEtag = DescriptorIfMatchHelper.ComputeEtagFromPersistedRow(
            DescriptorIfMatchHelper.StandardPersistedRow()
        );

        _sessionFactory = new DescriptorIfMatchWriteSessionFactory();
        _sessionFactory.EnqueueThrowingExecuteAfterRow(
            DescriptorIfMatchHelper.StandardPersistedRow(),
            new FakeGenericDbException()
        );

        var targetLookup = new DescriptorIfMatchTargetLookupStub
        {
            PutResult = new RelationalWriteTargetLookupResult.ExistingDocument(
                345L,
                new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb")),
                44L
            ),
        };

        var sut = DescriptorIfMatchHelper.CreateSut(
            targetLookup,
            A.Fake<IRelationalCommandExecutor>(),
            _sessionFactory
        );

        _result = await sut.HandlePutAsync(
            DescriptorIfMatchHelper.CreatePutRequestWithBody(
                new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb")),
                ifMatchEtag: matchingEtag,
                body: DescriptorIfMatchHelper.CreateChangedRequestBody()
            )
        );
    }

    [Test]
    public void It_returns_unknown_failure()
    {
        _result.Should().BeOfType<UpdateResult.UnknownFailure>();
    }

    [Test]
    public void It_issues_the_locked_select_and_the_update_command()
    {
        _sessionFactory.SessionCommands.Should().HaveCount(2);
        _sessionFactory.SessionCommands[0].CommandText.Should().Contain("FOR UPDATE");
        _sessionFactory.SessionCommands[1].CommandText.Should().Contain("UPDATE");
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// P2: Descriptor If-Match – DbException from session creation is caught
// ─────────────────────────────────────────────────────────────────────────────

[TestFixture]
[Parallelizable]
public class Given_Descriptor_PUT_if_match_transient_database_failure_during_session_creation
{
    private UpdateResult _result = default!;

    [SetUp]
    public async Task Arrange_and_act()
    {
        var documentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));
        var targetLookup = new DescriptorIfMatchTargetLookupStub
        {
            PutResult = new RelationalWriteTargetLookupResult.ExistingDocument(345L, documentUuid, 44L),
        };

        var sessionFactory = new DescriptorIfMatchWriteSessionFactory();
        sessionFactory.EnqueueCreateException(new FakeTransientDbException());

        var sut = DescriptorIfMatchHelper.CreateSut(
            targetLookup,
            A.Fake<IRelationalCommandExecutor>(),
            sessionFactory,
            new ConfigurableRelationalWriteExceptionClassifier { IsTransientFailureToReturn = true }
        );

        _result = await sut.HandlePutAsync(
            DescriptorIfMatchHelper.CreatePutRequest(documentUuid, ifMatchEtag: "\"some-etag\"")
        );
    }

    [Test]
    public void It_returns_update_failure_write_conflict()
    {
        _result.Should().BeOfType<UpdateResult.UpdateFailureWriteConflict>();
    }
}

[TestFixture]
[Parallelizable]
public class Given_Descriptor_DELETE_if_match_transient_database_failure_during_session_creation
{
    private DeleteResult _result = default!;

    [SetUp]
    public async Task Arrange_and_act()
    {
        var sessionFactory = new DescriptorIfMatchWriteSessionFactory();
        sessionFactory.EnqueueCreateException(new FakeTransientDbException());

        var sut = DescriptorIfMatchHelper.CreateSut(
            new DescriptorIfMatchTargetLookupStub(),
            A.Fake<IRelationalCommandExecutor>(),
            sessionFactory,
            new ConfigurableRelationalWriteExceptionClassifier { IsTransientFailureToReturn = true }
        );

        _result = await DescriptorIfMatchHelper.ExecuteDeleteAsync(
            sut,
            new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb")),
            ifMatchEtag: "\"some-etag\""
        );
    }

    [Test]
    public void It_returns_delete_failure_write_conflict()
    {
        _result.Should().BeOfType<DeleteResult.DeleteFailureWriteConflict>();
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Descriptor DELETE – FK violation resolver branching (log and resolver contract)
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// When the FK classifier extracts a constraint name and the resolver maps it to an owning
/// resource, HandleDeleteAsync must return a populated DeleteFailureReference, call the resolver
/// exactly once with the correct model, and emit a Debug log that contains both the constraint
/// name and the resolved resource name.
/// </summary>
[TestFixture]
[Parallelizable]
public class Given_Descriptor_DELETE_fk_violation_constraint_name_resolves_to_owning_resource
{
    private DeleteResult _result = default!;
    private IRelationalDeleteConstraintResolver _resolver = default!;
    private RecordingLogger<DescriptorWriteHandler> _logger = default!;
    private DescriptorIfMatchWriteSessionFactory _sessionFactory = default!;
    private MappingSet _mappingSet = default!;
    private const string ConstraintName = "FK_School_EdOrgCategoryDescriptor";
    private static readonly QualifiedResourceName ReferencingResource = new("Ed-Fi", "School");

    [SetUp]
    public async Task Arrange_and_act()
    {
        var matchingEtag = DescriptorIfMatchHelper.ComputeEtagFromPersistedRow(
            DescriptorIfMatchHelper.StandardPersistedRow()
        );

        _sessionFactory = new DescriptorIfMatchWriteSessionFactory();
        _sessionFactory.EnqueueThrowingExecuteAfterRow(
            DescriptorIfMatchHelper.StandardPersistedRow(),
            new FakeFkViolationDbException()
        );

        _resolver = A.Fake<IRelationalDeleteConstraintResolver>();
        _logger = new RecordingLogger<DescriptorWriteHandler>();

        var exceptionClassifier = new ConfigurableRelationalWriteExceptionClassifier
        {
            IsForeignKeyViolationToReturn = true,
            ClassificationToReturn = new RelationalWriteExceptionClassification.ForeignKeyConstraintViolation(
                ConstraintName
            ),
        };

        var sut = DescriptorIfMatchHelper.CreateSut(
            new DescriptorIfMatchTargetLookupStub(),
            A.Fake<IRelationalCommandExecutor>(),
            _sessionFactory,
            exceptionClassifier,
            deleteConstraintResolver: _resolver,
            logger: _logger
        );

        // Create the MappingSet before configuring the resolver mock so the same instance is
        // passed to HandleDeleteAsync — FakeItEasy uses reference equality for model-set args.
        _mappingSet = DescriptorIfMatchHelper.CreateMappingSetForTest();

        A.CallTo(() => _resolver.TryResolveReferencingResource(_mappingSet.Model, ConstraintName))
            .Returns(ReferencingResource);

        _result = await DescriptorIfMatchHelper.ExecuteDeleteAsync(
            sut,
            new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb")),
            ifMatchEtag: matchingEtag,
            mappingSet: _mappingSet
        );
    }

    [Test]
    public void It_returns_delete_failure_reference_with_the_resolved_resource_name()
    {
        _result
            .Should()
            .BeEquivalentTo(new DeleteResult.DeleteFailureReference([ReferencingResource.ResourceName]));
    }

    [Test]
    public void It_calls_the_resolver_exactly_once_with_the_correct_model()
    {
        // Pinning the exact MappingSet.Model reference catches a regression where the handler
        // stops forwarding mappingSet.Model to the resolver (e.g., passes null or a stale set).
        A.CallTo(() => _resolver.TryResolveReferencingResource(_mappingSet.Model, ConstraintName))
            .MustHaveHappenedOnceExactly();
    }

    [Test]
    public void It_emits_a_debug_log_containing_the_constraint_name_and_resource_name()
    {
        // Pins both the log level and payload so a future log-level demotion or message change
        // is caught. An unrelated "Deleting descriptor document..." Debug log is always emitted
        // first, so a bare Level==Debug check would pass even without the FK log line.
        _logger
            .Records.Should()
            .ContainSingle(r =>
                r.Level == LogLevel.Debug
                && r.Message.Contains(ConstraintName, StringComparison.Ordinal)
                && r.Message.Contains(ReferencingResource.ResourceName, StringComparison.Ordinal)
            );
    }
}

/// <summary>
/// When <see cref="IRelationalWriteExceptionClassifier.IsForeignKeyViolation"/> returns
/// <c>true</c> but <see cref="IRelationalWriteExceptionClassifier.TryClassify"/> cannot produce
/// a <see cref="RelationalWriteExceptionClassification.ForeignKeyConstraintViolation"/>
/// (e.g., the driver omits the constraint name), HandleDeleteAsync must return an empty
/// DeleteFailureReference, must NOT call the constraint resolver, and must emit a single
/// Information log (not a Warning).
/// </summary>
[TestFixture]
[Parallelizable]
public class Given_Descriptor_DELETE_fk_violation_constraint_name_cannot_be_extracted
{
    private DeleteResult _result = default!;
    private IRelationalDeleteConstraintResolver _resolver = default!;
    private RecordingLogger<DescriptorWriteHandler> _logger = default!;
    private DescriptorIfMatchWriteSessionFactory _sessionFactory = default!;

    [SetUp]
    public async Task Arrange_and_act()
    {
        var matchingEtag = DescriptorIfMatchHelper.ComputeEtagFromPersistedRow(
            DescriptorIfMatchHelper.StandardPersistedRow()
        );

        _sessionFactory = new DescriptorIfMatchWriteSessionFactory();
        _sessionFactory.EnqueueThrowingExecuteAfterRow(
            DescriptorIfMatchHelper.StandardPersistedRow(),
            new FakeFkViolationDbException()
        );

        _resolver = A.Fake<IRelationalDeleteConstraintResolver>();
        _logger = new RecordingLogger<DescriptorWriteHandler>();

        // IsForeignKeyViolationToReturn=true makes the FK catch fire; UnrecognizedWriteFailure
        // means TryClassify does not produce a ForeignKeyConstraintViolation, so the code falls
        // through to the LogInformation branch in MapForeignKeyViolation.
        var exceptionClassifier = new ConfigurableRelationalWriteExceptionClassifier
        {
            IsForeignKeyViolationToReturn = true,
            ClassificationToReturn = RelationalWriteExceptionClassification.UnrecognizedWriteFailure.Instance,
        };

        var sut = DescriptorIfMatchHelper.CreateSut(
            new DescriptorIfMatchTargetLookupStub(),
            A.Fake<IRelationalCommandExecutor>(),
            _sessionFactory,
            exceptionClassifier,
            deleteConstraintResolver: _resolver,
            logger: _logger
        );

        _result = await DescriptorIfMatchHelper.ExecuteDeleteAsync(
            sut,
            new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb")),
            ifMatchEtag: matchingEtag
        );
    }

    [Test]
    public void It_returns_empty_delete_failure_reference()
    {
        _result.Should().BeEquivalentTo(new DeleteResult.DeleteFailureReference([]));
    }

    [Test]
    public void It_does_not_call_the_resolver()
    {
        A.CallTo(() => _resolver.TryResolveReferencingResource(A<DerivedRelationalModelSet>._, A<string>._))
            .MustNotHaveHappened();
    }

    [Test]
    public void It_emits_an_information_log()
    {
        _logger.Records.Should().ContainSingle(r => r.Level == LogLevel.Information);
    }

    [Test]
    public void It_does_not_emit_a_warning_log()
    {
        // Decisions #4 splits Information (missing constraint name) from Warning (unresolved
        // constraint name). Assert Warning is absent to pin the branch split.
        _logger.Records.Should().NotContain(r => r.Level == LogLevel.Warning);
    }
}

/// <summary>
/// When the FK classifier extracts a constraint name but the compiled relational model has no
/// matching FK (DDL/model drift), HandleDeleteAsync must return an empty DeleteFailureReference,
/// call the resolver exactly once, and emit a Warning log (not an Information log).
/// </summary>
[TestFixture]
[Parallelizable]
public class Given_Descriptor_DELETE_fk_violation_constraint_name_not_in_compiled_model
{
    private DeleteResult _result = default!;
    private IRelationalDeleteConstraintResolver _resolver = default!;
    private RecordingLogger<DescriptorWriteHandler> _logger = default!;
    private DescriptorIfMatchWriteSessionFactory _sessionFactory = default!;
    private MappingSet _mappingSet = default!;
    private const string ConstraintName = "FK_Unknown_To_Model";

    [SetUp]
    public async Task Arrange_and_act()
    {
        var matchingEtag = DescriptorIfMatchHelper.ComputeEtagFromPersistedRow(
            DescriptorIfMatchHelper.StandardPersistedRow()
        );

        _sessionFactory = new DescriptorIfMatchWriteSessionFactory();
        _sessionFactory.EnqueueThrowingExecuteAfterRow(
            DescriptorIfMatchHelper.StandardPersistedRow(),
            new FakeFkViolationDbException()
        );

        _resolver = A.Fake<IRelationalDeleteConstraintResolver>();
        _logger = new RecordingLogger<DescriptorWriteHandler>();

        var exceptionClassifier = new ConfigurableRelationalWriteExceptionClassifier
        {
            IsForeignKeyViolationToReturn = true,
            ClassificationToReturn = new RelationalWriteExceptionClassification.ForeignKeyConstraintViolation(
                ConstraintName
            ),
        };

        var sut = DescriptorIfMatchHelper.CreateSut(
            new DescriptorIfMatchTargetLookupStub(),
            A.Fake<IRelationalCommandExecutor>(),
            _sessionFactory,
            exceptionClassifier,
            deleteConstraintResolver: _resolver,
            logger: _logger
        );

        // Create the MappingSet before configuring the resolver mock so the same instance is
        // passed to HandleDeleteAsync — FakeItEasy uses reference equality for model-set args.
        _mappingSet = DescriptorIfMatchHelper.CreateMappingSetForTest();

        // Resolver returns null → constraint not in compiled model (drift scenario).
        A.CallTo(() => _resolver.TryResolveReferencingResource(_mappingSet.Model, ConstraintName))
            .Returns((QualifiedResourceName?)null);

        _result = await DescriptorIfMatchHelper.ExecuteDeleteAsync(
            sut,
            new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb")),
            ifMatchEtag: matchingEtag,
            mappingSet: _mappingSet
        );
    }

    [Test]
    public void It_returns_empty_delete_failure_reference()
    {
        _result.Should().BeEquivalentTo(new DeleteResult.DeleteFailureReference([]));
    }

    [Test]
    public void It_calls_the_resolver_exactly_once()
    {
        A.CallTo(() => _resolver.TryResolveReferencingResource(_mappingSet.Model, ConstraintName))
            .MustHaveHappenedOnceExactly();
    }

    [Test]
    public void It_emits_a_warning_log()
    {
        _logger.Records.Should().ContainSingle(r => r.Level == LogLevel.Warning);
    }

    [Test]
    public void It_does_not_emit_an_information_log()
    {
        // Decisions #4 splits Warning (unresolved constraint name) from Information (missing
        // constraint name). Assert Information is absent to pin the branch split.
        _logger.Records.Should().NotContain(r => r.Level == LogLevel.Information);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// File-scoped shared test support
// ─────────────────────────────────────────────────────────────────────────────

internal static class DescriptorIfMatchHelper
{
    private static readonly QualifiedResourceName DescriptorResource = new("Ed-Fi", "SchoolTypeDescriptor");

    public static DescriptorWriteHandler CreateSut(
        IRelationalWriteTargetLookupService targetLookup,
        IRelationalCommandExecutor commandExecutor,
        IRelationalWriteSessionFactory? writeSessionFactory = null,
        IRelationalWriteExceptionClassifier? exceptionClassifier = null,
        IRelationalDeleteConstraintResolver? deleteConstraintResolver = null,
        ILogger<DescriptorWriteHandler>? logger = null
    ) =>
        new(
            targetLookup,
            commandExecutor,
            exceptionClassifier ?? A.Fake<IRelationalWriteExceptionClassifier>(),
            deleteConstraintResolver ?? A.Fake<IRelationalDeleteConstraintResolver>(),
            writeSessionFactory ?? A.Fake<IRelationalWriteSessionFactory>(),
            A.Fake<IReadableProfileProjector>(),
            logger ?? NullLogger<DescriptorWriteHandler>.Instance
        );

    public static Task<DeleteResult> ExecuteDeleteAsync(
        DescriptorWriteHandler sut,
        DocumentUuid documentUuid,
        string? ifMatchEtag,
        SqlDialect dialect = SqlDialect.Pgsql,
        MappingSet? mappingSet = null,
        CancellationToken cancellationToken = default
    ) =>
        sut.HandleDeleteAsync(
            mappingSet ?? CreateMappingSet(dialect),
            DescriptorResource,
            documentUuid,
            new TraceId("descriptor-delete-trace"),
            ifMatchEtag,
            ifMatchReadableProjectionContext: null,
            cancellationToken
        );

    /// <summary>
    /// Returns a single persisted-row dictionary with Namespace and CodeValue as discrete
    /// columns (matching the DB schema), not reconstructed from a URI.
    /// </summary>
    public static IReadOnlyDictionary<string, object?> StandardPersistedRow() =>
        new Dictionary<string, object?>
        {
            ["Namespace"] = "uri://ed-fi.org/SchoolTypeDescriptor",
            ["CodeValue"] = "Charter",
            ["Uri"] = "uri://ed-fi.org/SchoolTypeDescriptor#Charter",
            ["ShortDescription"] = "Charter",
            ["Description"] = "Charter",
            ["EffectiveBeginDate"] = new DateOnly(2024, 1, 1),
            ["EffectiveEndDate"] = null,
        };

    public static IReadOnlyDictionary<string, object?> ChangedPersistedRow() =>
        new Dictionary<string, object?>
        {
            ["Namespace"] = "uri://ed-fi.org/SchoolTypeDescriptor",
            ["CodeValue"] = "Charter",
            ["Uri"] = "uri://ed-fi.org/SchoolTypeDescriptor#Charter",
            ["ShortDescription"] = "Charter School",
            ["Description"] = "Charter",
            ["EffectiveBeginDate"] = new DateOnly(2024, 1, 1),
            ["EffectiveEndDate"] = null,
        };

    /// <summary>
    /// Computes the ETag exactly as <c>IsDescriptorEtagMismatch</c> does: from the
    /// <c>Namespace</c> and <c>CodeValue</c> DB columns plus mutable fields.
    /// Uri and Discriminator are excluded from the hash.
    /// </summary>
    public static string ComputeEtagFromPersistedRow(IReadOnlyDictionary<string, object?> row) =>
        RelationalApiMetadataFormatter.FormatEtag(
            new ExtractedDescriptorBody(
                (string)row["Namespace"]!,
                (string)row["CodeValue"]!,
                (string?)row["ShortDescription"],
                (string?)row["Description"],
                (DateOnly?)row["EffectiveBeginDate"],
                (DateOnly?)row["EffectiveEndDate"],
                string.Empty,
                string.Empty
            )
        );

    public static DescriptorWriteRequest CreatePostRequest(
        DocumentUuid documentUuid,
        string? ifMatchEtag,
        SqlDialect dialect = SqlDialect.Pgsql
    ) =>
        new(
            CreateMappingSet(dialect),
            DescriptorResource,
            CreateRequestBody(),
            documentUuid,
            new ReferentialId(Guid.Parse("cccccccc-1111-2222-3333-dddddddddddd")),
            new TraceId("descriptor-post-trace"),
            ifMatchEtag
        );

    public static DescriptorWriteRequest CreatePostRequestWithBody(
        DocumentUuid documentUuid,
        string? ifMatchEtag,
        JsonNode body,
        SqlDialect dialect = SqlDialect.Pgsql
    ) =>
        new(
            CreateMappingSet(dialect),
            DescriptorResource,
            body,
            documentUuid,
            new ReferentialId(Guid.Parse("cccccccc-1111-2222-3333-dddddddddddd")),
            new TraceId("descriptor-post-trace"),
            ifMatchEtag
        );

    public static DescriptorWriteRequest CreatePutRequest(
        DocumentUuid documentUuid,
        string? ifMatchEtag,
        SqlDialect dialect = SqlDialect.Pgsql
    ) =>
        new(
            CreateMappingSet(dialect),
            DescriptorResource,
            CreateRequestBody(),
            documentUuid,
            null,
            new TraceId("descriptor-put-trace"),
            ifMatchEtag
        );

    public static DescriptorWriteRequest CreatePutRequestWithBody(
        DocumentUuid documentUuid,
        string? ifMatchEtag,
        JsonNode body,
        SqlDialect dialect = SqlDialect.Pgsql
    ) =>
        new(
            CreateMappingSet(dialect),
            DescriptorResource,
            body,
            documentUuid,
            null,
            new TraceId("descriptor-put-trace"),
            ifMatchEtag
        );

    /// <summary>
    /// Returns a request body with a changed <c>shortDescription</c> so that
    /// <c>IsDescriptorUnchanged</c> returns <c>false</c> against
    /// <see cref="StandardPersistedRow"/>.
    /// </summary>
    public static JsonNode CreateChangedRequestBody() =>
        JsonNode.Parse(
            """
            {
              "namespace": "uri://ed-fi.org/SchoolTypeDescriptor",
              "codeValue": "Charter",
              "shortDescription": "Charter School",
              "description": "Charter",
              "effectiveBeginDate": "2024-01-01"
            }
            """
        )!;

    /// <summary>
    /// Returns the ETag that <c>RelationalApiMetadataFormatter.FormatEtag</c> produces
    /// for the body returned by <see cref="CreateChangedRequestBody"/>.
    /// </summary>
    public static string ComputeEtagFromChangedBody() =>
        RelationalApiMetadataFormatter.FormatEtag(
            new ExtractedDescriptorBody(
                "uri://ed-fi.org/SchoolTypeDescriptor",
                "Charter",
                "Charter School",
                "Charter",
                new DateOnly(2024, 1, 1),
                null,
                string.Empty,
                string.Empty
            )
        );

    private static JsonNode CreateRequestBody() =>
        JsonNode.Parse(
            """
            {
              "namespace": "uri://ed-fi.org/SchoolTypeDescriptor",
              "codeValue": "Charter",
              "shortDescription": "Charter",
              "description": "Charter",
              "effectiveBeginDate": "2024-01-01"
            }
            """
        )!;

    /// <summary>
    /// Exposes the same <see cref="MappingSet"/> produced by <see cref="ExecuteDeleteAsync"/>
    /// so tests can pin the exact <c>Model</c> reference when verifying resolver calls.
    /// </summary>
    public static MappingSet CreateMappingSetForTest(SqlDialect dialect = SqlDialect.Pgsql) =>
        CreateMappingSet(dialect);

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

    private static DbTableModel CreateRootTable() =>
        new DbTableModel(
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

internal sealed class DescriptorIfMatchCommandRecorder(SqlDialect dialect) : IRelationalCommandExecutor
{
    public SqlDialect Dialect { get; } = dialect;
    public Queue<IReadOnlyList<InMemoryRelationalResultSet>> ResultSets { get; } = [];
    public List<RelationalCommand> Commands { get; } = [];

    public async Task<TResult> ExecuteReaderAsync<TResult>(
        RelationalCommand command,
        Func<IRelationalCommandReader, CancellationToken, Task<TResult>> readAsync,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        Commands.Add(command);

        IReadOnlyList<InMemoryRelationalResultSet> resultSets =
            ResultSets.Count == 0 ? [] : ResultSets.Dequeue();

        await using var reader = new InMemoryRelationalCommandReader(resultSets);
        return await readAsync(reader, cancellationToken);
    }
}

internal sealed class ThrowingRelationalCommandExecutor(
    SqlDialect dialect,
    DbException exceptionToThrow,
    IReadOnlyList<IReadOnlyList<InMemoryRelationalResultSet>>? resultSetsByCall = null,
    int throwOnCall = 1
) : IRelationalCommandExecutor
{
    public SqlDialect Dialect { get; } = dialect;
    private readonly Queue<IReadOnlyList<InMemoryRelationalResultSet>> _resultSets = new(
        resultSetsByCall ?? []
    );
    private int _callCount;

    public async Task<TResult> ExecuteReaderAsync<TResult>(
        RelationalCommand command,
        Func<IRelationalCommandReader, CancellationToken, Task<TResult>> readAsync,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        _callCount++;

        if (_callCount == throwOnCall)
        {
            throw exceptionToThrow;
        }

        IReadOnlyList<InMemoryRelationalResultSet> queuedResultSets =
            _resultSets.Count == 0 ? [] : _resultSets.Dequeue();

        await using var reader = new InMemoryRelationalCommandReader(queuedResultSets);
        return await readAsync(reader, cancellationToken);
    }
}

internal sealed class DescriptorIfMatchTargetLookupStub : IRelationalWriteTargetLookupService
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

internal sealed class DescriptorIfMatchWriteSessionFactory(SqlDialect dialect = SqlDialect.Pgsql)
    : IRelationalWriteSessionFactory
{
    private readonly Queue<IReadOnlyList<IReadOnlyDictionary<string, object?>>> _resultSets = [];
    private Queue<Exception?>? _exceptionQueue;
    private Queue<Exception?>? _commitExceptionQueue;
    private Exception? _createException;

    public List<RelationalCommand> SessionCommands { get; } = [];

    public void EnqueueDescriptorRow(IReadOnlyDictionary<string, object?> row) => _resultSets.Enqueue([row]);

    public void EnqueueResultSet(IReadOnlyList<IReadOnlyDictionary<string, object?>> rows) =>
        _resultSets.Enqueue(rows);

    public void EnqueueCreateException(Exception exceptionToThrow) => _createException = exceptionToThrow;

    public void EnqueueCommitException(Exception exceptionToThrow)
    {
        _commitExceptionQueue ??= new Queue<Exception?>();
        _commitExceptionQueue.Enqueue(exceptionToThrow);
    }

    public void EnqueueExecuteException(Exception exceptionToThrow)
    {
        _exceptionQueue ??= new Queue<Exception?>();
        _exceptionQueue.Enqueue(exceptionToThrow);
    }

    /// <summary>
    /// Simulates a locked session where the pre-check SELECT succeeds (returning
    /// <paramref name="row"/>) but the subsequent DML command throws
    /// <paramref name="exceptionToThrow"/>.
    /// </summary>
    public void EnqueueThrowingExecuteAfterRow(
        IReadOnlyDictionary<string, object?> row,
        Exception exceptionToThrow
    )
    {
        _resultSets.Enqueue([row]);
        _exceptionQueue ??= new Queue<Exception?>();
        _exceptionQueue.Enqueue(null); // command 1 (locked SELECT): proceed normally
        _exceptionQueue.Enqueue(exceptionToThrow); // command 2 (write): throw
    }

    public Task<IRelationalWriteSession> CreateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_createException is not null)
        {
            ExceptionDispatchInfo.Throw(_createException);
        }

        DbConnection connection = dialect switch
        {
            SqlDialect.Pgsql => new NpgsqlDescriptorIfMatchConnection(
                _resultSets,
                SessionCommands,
                _exceptionQueue
            ),
            SqlDialect.Mssql => new SqlClientDescriptorIfMatchConnection(
                _resultSets,
                SessionCommands,
                _exceptionQueue
            ),
            _ => throw new NotSupportedException($"Unsupported test dialect '{dialect}'."),
        };

        return Task.FromResult<IRelationalWriteSession>(
            new RelationalWriteSession(
                connection,
                new DescriptorIfMatchDbTransaction(connection, _commitExceptionQueue)
            )
        );
    }
}

internal abstract class DescriptorIfMatchDbConnection(
    Queue<IReadOnlyList<IReadOnlyDictionary<string, object?>>> resultSets,
    List<RelationalCommand> sessionCommands,
    Queue<Exception?>? exceptionQueue = null
) : DbConnection
{
    private ConnectionState _state = ConnectionState.Open;

    [AllowNull]
    public override string ConnectionString { get; set; } = "Host=localhost;Database=test";

    public override string Database => "test";

    public override string DataSource => "stub";

    public override string ServerVersion => "1.0";

    public override ConnectionState State => _state;

    public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();

    public override void Close() => _state = ConnectionState.Closed;

    public override void Open() => _state = ConnectionState.Open;

    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) =>
        new DescriptorIfMatchDbTransaction(this);

    protected override DbCommand CreateDbCommand() =>
        new DescriptorIfMatchDbCommand(resultSets, sessionCommands, exceptionQueue) { Connection = this };
}

internal sealed class NpgsqlDescriptorIfMatchConnection(
    Queue<IReadOnlyList<IReadOnlyDictionary<string, object?>>> resultSets,
    List<RelationalCommand> sessionCommands,
    Queue<Exception?>? exceptionQueue = null
) : DescriptorIfMatchDbConnection(resultSets, sessionCommands, exceptionQueue);

internal sealed class SqlClientDescriptorIfMatchConnection(
    Queue<IReadOnlyList<IReadOnlyDictionary<string, object?>>> resultSets,
    List<RelationalCommand> sessionCommands,
    Queue<Exception?>? exceptionQueue = null
) : DescriptorIfMatchDbConnection(resultSets, sessionCommands, exceptionQueue);

internal sealed class DescriptorIfMatchDbTransaction(
    DbConnection connection,
    Queue<Exception?>? commitExceptionQueue = null
) : DbTransaction
{
    public override IsolationLevel IsolationLevel => IsolationLevel.ReadCommitted;

    protected override DbConnection DbConnection { get; } = connection;

    public override void Commit()
    {
        if (
            commitExceptionQueue is not null
            && commitExceptionQueue.TryDequeue(out var pendingEx)
            && pendingEx is not null
        )
        {
            ExceptionDispatchInfo.Throw(pendingEx);
        }
    }

    public override void Rollback() { }
}

internal sealed class DescriptorIfMatchDbCommand(
    Queue<IReadOnlyList<IReadOnlyDictionary<string, object?>>> resultSets,
    List<RelationalCommand> sessionCommands,
    Queue<Exception?>? exceptionQueue = null
) : DbCommand
{
    private readonly DescriptorIfMatchDbParameterCollection _parameters = [];

    [AllowNull]
    public override string CommandText { get; set; } = string.Empty;

    public override int CommandTimeout { get; set; }

    public override CommandType CommandType { get; set; } = CommandType.Text;

    protected override DbConnection? DbConnection { get; set; }

    protected override DbParameterCollection DbParameterCollection => _parameters;

    protected override DbTransaction? DbTransaction { get; set; }

    public override bool DesignTimeVisible { get; set; }

    public override UpdateRowSource UpdatedRowSource { get; set; }

    public override void Cancel() { }

    public override int ExecuteNonQuery() => throw new NotSupportedException();

    public override object? ExecuteScalar() => throw new NotSupportedException();

    public override void Prepare() { }

    protected override DbParameter CreateDbParameter() => new DescriptorIfMatchDbParameter();

    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
    {
        sessionCommands.Add(new RelationalCommand(CommandText, ToRelationalParameters()));
        return CreateReader();
    }

    protected override Task<DbDataReader> ExecuteDbDataReaderAsync(
        CommandBehavior behavior,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        sessionCommands.Add(new RelationalCommand(CommandText, ToRelationalParameters()));

        if (
            exceptionQueue is not null
            && exceptionQueue.TryDequeue(out var pendingEx)
            && pendingEx is not null
        )
        {
            ExceptionDispatchInfo.Throw(pendingEx);
        }

        return Task.FromResult(CreateReader());
    }

    private DbDataReader CreateReader()
    {
        var rows = resultSets.Count == 0 ? [] : resultSets.Dequeue();
        return CreateDataTable(rows).CreateDataReader();
    }

    private IReadOnlyList<RelationalParameter> ToRelationalParameters() =>
        _parameters
            .Items.Select(parameter => new RelationalParameter(parameter.ParameterName, parameter.Value))
            .ToList();

    private static DataTable CreateDataTable(IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
    {
        var table = new DataTable();

        if (rows.Count == 0)
        {
            return table;
        }

        foreach (var column in rows[0].Keys)
        {
            table.Columns.Add(column, typeof(object));
        }

        foreach (var row in rows)
        {
            var dataRow = table.NewRow();

            foreach (var kvp in row)
            {
                dataRow[kvp.Key] = kvp.Value ?? DBNull.Value;
            }

            table.Rows.Add(dataRow);
        }

        return table;
    }
}

internal sealed class DescriptorIfMatchDbParameterCollection : DbParameterCollection
{
    public List<DescriptorIfMatchDbParameter> Items { get; } = [];

    public override int Count => Items.Count;

    public override object SyncRoot => ((ICollection)Items).SyncRoot!;

    public override int Add(object value)
    {
        Items.Add((DescriptorIfMatchDbParameter)value);
        return Items.Count - 1;
    }

    public override void AddRange(Array values)
    {
        foreach (var value in values)
        {
            Add(value!);
        }
    }

    public override void Clear() => Items.Clear();

    public override bool Contains(object value) => Items.Contains((DescriptorIfMatchDbParameter)value);

    public override bool Contains(string value) =>
        Items.Exists(parameter => parameter.ParameterName == value);

    public override void CopyTo(Array array, int index) => ((ICollection)Items).CopyTo(array, index);

    public override IEnumerator GetEnumerator() => Items.GetEnumerator();

    protected override DbParameter GetParameter(int index) => Items[index];

    protected override DbParameter GetParameter(string parameterName) =>
        Items.Single(parameter => parameter.ParameterName == parameterName);

    public override int IndexOf(object value) => Items.IndexOf((DescriptorIfMatchDbParameter)value);

    public override int IndexOf(string parameterName) =>
        Items.FindIndex(parameter => parameter.ParameterName == parameterName);

    public override void Insert(int index, object value) =>
        Items.Insert(index, (DescriptorIfMatchDbParameter)value);

    public override void Remove(object value) => Items.Remove((DescriptorIfMatchDbParameter)value);

    public override void RemoveAt(int index) => Items.RemoveAt(index);

    public override void RemoveAt(string parameterName)
    {
        var index = IndexOf(parameterName);

        if (index >= 0)
        {
            Items.RemoveAt(index);
        }
    }

    protected override void SetParameter(int index, DbParameter value) =>
        Items[index] = (DescriptorIfMatchDbParameter)value;

    protected override void SetParameter(string parameterName, DbParameter value)
    {
        var index = IndexOf(parameterName);

        if (index < 0)
        {
            Items.Add((DescriptorIfMatchDbParameter)value);
            return;
        }

        Items[index] = (DescriptorIfMatchDbParameter)value;
    }
}

internal sealed class DescriptorIfMatchDbParameter : DbParameter
{
    public override DbType DbType { get; set; }

    public override ParameterDirection Direction { get; set; } = ParameterDirection.Input;

    public override bool IsNullable { get; set; }

    [AllowNull]
    public override string ParameterName { get; set; }

    [AllowNull]
    public override string SourceColumn { get; set; }

    public override object? Value { get; set; }

    public override bool SourceColumnNullMapping { get; set; }

    public override int Size { get; set; }

    public override void ResetDbType() { }
}

/// <summary>
/// Fake DbException that satisfies the <c>IsForeignKeyViolation</c> predicate
/// (SqlState "23503" = Postgres FK violation).
/// </summary>
file sealed class FakeFkViolationDbException() : DbException("Fake FK constraint violation")
{
    public override string? SqlState => "23503";
}

/// <summary>
/// Fake DbException for generic database error scenarios (no special SqlState).
/// </summary>
file sealed class FakeGenericDbException() : DbException("Fake database error") { }

file sealed class FakeTransientDbException() : DbException("Fake transient database error") { }

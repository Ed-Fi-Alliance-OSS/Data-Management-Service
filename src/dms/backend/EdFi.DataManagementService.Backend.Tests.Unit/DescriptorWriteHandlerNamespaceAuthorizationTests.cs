// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Data.Common;
using EdFi.DataManagementService.Backend.Etag;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.External.Security;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
[Parallelizable]
public class Given_Descriptor_Write_Handler_Namespace_Authorization
{
    private static readonly QualifiedResourceName _descriptorResource = new("Ed-Fi", "SchoolTypeDescriptor");
    private static readonly DocumentUuid _documentUuid = new(
        Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb")
    );

    private static NamespaceAuthorizationFailure StoredMismatchFailure() =>
        new(
            NamespaceAuthorizationFailureKind.NamespaceMismatch,
            NamespaceAuthorizationFailureValueSource.Stored,
            EmittedAuth1Index: 0,
            AuthorizationStrategyNameConstants.NamespaceBased,
            ConfiguredNamespacePrefixes: ["uri://ed-fi.org/"]
        );

    private static NamespaceAuthorizationFailure ProposedMismatchFailure() =>
        new(
            NamespaceAuthorizationFailureKind.NamespaceMismatch,
            NamespaceAuthorizationFailureValueSource.Proposed,
            EmittedAuth1Index: 0,
            AuthorizationStrategyNameConstants.NamespaceBased,
            ConfiguredNamespacePrefixes: ["uri://ed-fi.org/"]
        );

    private static NamespaceAuthorizationFailure ProposedMissingFailure() =>
        new(
            NamespaceAuthorizationFailureKind.ProposedNamespaceMissing,
            NamespaceAuthorizationFailureValueSource.Proposed,
            EmittedAuth1Index: 0,
            AuthorizationStrategyNameConstants.NamespaceBased,
            ConfiguredNamespacePrefixes: ["uri://ed-fi.org/"]
        );

    [TestCase(AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly)]
    [TestCase(AuthorizationStrategyNameConstants.OwnershipBased)]
    public async Task It_fails_closed_for_descriptor_post_with_an_unsupported_strategy_without_executing_sql(
        string authorizationStrategyName
    )
    {
        var targetLookupService = new StubRelationalWriteTargetLookupService();
        var sessionFactory = new RecordingNamespaceWriteSessionFactory(SqlDialect.Pgsql);
        var sut = CreateSut(sessionFactory, targetLookupService);

        var result = await sut.HandlePostAsync(
            CreatePostRequest(
                namespacePrefixes: ["uri://ed-fi.org/"],
                authorizationStrategy: UnsupportedStrategy(authorizationStrategyName)
            )
        );

        result.Should().BeOfType<UpsertResult.UpsertFailureNotImplemented>();
        result
            .As<UpsertResult.UpsertFailureNotImplemented>()
            .FailureMessage.Should()
            .Contain(authorizationStrategyName);
        sessionFactory.CreateAsyncCallCount.Should().Be(0);
    }

    [Test]
    public async Task It_returns_security_configuration_for_descriptor_post_with_an_unknown_strategy_without_opening_a_session()
    {
        const string unknownStrategyName = "UnknownDescriptorStrategy";
        var targetLookupService = new StubRelationalWriteTargetLookupService();
        var sessionFactory = new RecordingNamespaceWriteSessionFactory(SqlDialect.Pgsql);
        var sut = CreateSut(sessionFactory, targetLookupService);

        var result = await sut.HandlePostAsync(
            CreatePostRequest(
                namespacePrefixes: ["uri://ed-fi.org/"],
                authorizationStrategy: UnsupportedStrategy(unknownStrategyName)
            )
        );

        var failure = result.Should().BeOfType<UpsertResult.UpsertFailureSecurityConfiguration>().Subject;
        failure
            .Errors.Should()
            .Equal(
                SecurityConfigurationFailureMessages.UnknownAuthorizationStrategies([unknownStrategyName])
            );
        sessionFactory.CreateAsyncCallCount.Should().Be(0);
    }

    [TestCase(AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly)]
    [TestCase(AuthorizationStrategyNameConstants.OwnershipBased)]
    public async Task It_fails_closed_for_descriptor_put_with_an_unsupported_strategy_without_executing_sql(
        string authorizationStrategyName
    )
    {
        var targetLookupService = new StubRelationalWriteTargetLookupService();
        var sessionFactory = new RecordingNamespaceWriteSessionFactory(SqlDialect.Pgsql);
        var sut = CreateSut(sessionFactory, targetLookupService);

        var result = await sut.HandlePutAsync(
            CreatePutRequest(
                namespacePrefixes: ["uri://ed-fi.org/"],
                authorizationStrategy: UnsupportedStrategy(authorizationStrategyName)
            )
        );

        result.Should().BeOfType<UpdateResult.UpdateFailureNotImplemented>();
        result
            .As<UpdateResult.UpdateFailureNotImplemented>()
            .FailureMessage.Should()
            .Contain(authorizationStrategyName);
        sessionFactory.CreateAsyncCallCount.Should().Be(0);
    }

    [Test]
    public async Task It_returns_security_configuration_for_descriptor_put_with_an_unknown_strategy_without_opening_a_session()
    {
        const string unknownStrategyName = "UnknownDescriptorStrategy";
        var targetLookupService = new StubRelationalWriteTargetLookupService();
        var sessionFactory = new RecordingNamespaceWriteSessionFactory(SqlDialect.Pgsql);
        var sut = CreateSut(sessionFactory, targetLookupService);

        var result = await sut.HandlePutAsync(
            CreatePutRequest(
                namespacePrefixes: ["uri://ed-fi.org/"],
                authorizationStrategy: UnsupportedStrategy(unknownStrategyName)
            )
        );

        var failure = result.Should().BeOfType<UpdateResult.UpdateFailureSecurityConfiguration>().Subject;
        failure
            .Errors.Should()
            .Equal(
                SecurityConfigurationFailureMessages.UnknownAuthorizationStrategies([unknownStrategyName])
            );
        sessionFactory.CreateAsyncCallCount.Should().Be(0);
    }

    [Test]
    public async Task It_returns_namespace_403_without_opening_a_session_for_descriptor_post_when_the_client_has_no_prefixes()
    {
        var targetLookupService = new StubRelationalWriteTargetLookupService();
        var sessionFactory = new RecordingNamespaceWriteSessionFactory(SqlDialect.Pgsql);
        var sut = CreateSut(sessionFactory, targetLookupService);

        var result = await sut.HandlePostAsync(
            CreatePostRequest(namespacePrefixes: [], authorizationStrategy: NamespaceStrategy())
        );

        result.Should().BeOfType<UpsertResult.UpsertFailureNamespaceNotAuthorized>();
        result
            .As<UpsertResult.UpsertFailureNamespaceNotAuthorized>()
            .NamespaceFailure.FailureKind.Should()
            .Be(NamespaceAuthorizationFailureKind.NoPrefixesConfigured);
        sessionFactory.CreateAsyncCallCount.Should().Be(0);
    }

    [Test]
    public async Task It_returns_namespace_403_without_opening_a_session_for_descriptor_put_when_the_client_has_no_prefixes()
    {
        var targetLookupService = new StubRelationalWriteTargetLookupService();
        var sessionFactory = new RecordingNamespaceWriteSessionFactory(SqlDialect.Pgsql);
        var sut = CreateSut(sessionFactory, targetLookupService);

        var result = await sut.HandlePutAsync(
            CreatePutRequest(namespacePrefixes: [], authorizationStrategy: NamespaceStrategy())
        );

        result.Should().BeOfType<UpdateResult.UpdateFailureNamespaceNotAuthorized>();
        result
            .As<UpdateResult.UpdateFailureNamespaceNotAuthorized>()
            .NamespaceFailure.FailureKind.Should()
            .Be(NamespaceAuthorizationFailureKind.NoPrefixesConfigured);
        sessionFactory.CreateAsyncCallCount.Should().Be(0);
    }

    [Test]
    public async Task It_returns_namespace_403_without_inserting_when_descriptor_post_create_proposed_namespace_does_not_match_a_prefix()
    {
        var targetLookupService = new StubRelationalWriteTargetLookupService
        {
            PostResult = new RelationalWriteTargetLookupResult.CreateNew(_documentUuid),
        };
        var sessionFactory = new RecordingNamespaceWriteSessionFactory(SqlDialect.Pgsql);
        sessionFactory.Session.Executor.NamespaceResults.Enqueue(
            new NamespaceAuthorizationExecutionResult.NotAuthorized(ProposedMismatchFailure())
        );
        var sut = CreateSut(sessionFactory, targetLookupService);

        var result = await sut.HandlePostAsync(
            CreatePostRequest(
                namespacePrefixes: ["uri://ed-fi.org/"],
                authorizationStrategy: NamespaceStrategy(),
                @namespace: "uri://other.org/SchoolTypeDescriptor"
            )
        );

        result.Should().BeOfType<UpsertResult.UpsertFailureNamespaceNotAuthorized>();
        result
            .As<UpsertResult.UpsertFailureNamespaceNotAuthorized>()
            .NamespaceFailure.ValueSource.Should()
            .Be(NamespaceAuthorizationFailureValueSource.Proposed);
        sessionFactory
            .Session.Executor.Commands.Should()
            .NotContain(command =>
                command.CommandText.Contains("INSERT INTO dms.\"Document\"", StringComparison.Ordinal)
            );
        sessionFactory.Session.CommitCallCount.Should().Be(0);
        sessionFactory.Session.RollbackCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_returns_namespace_403_without_inserting_when_descriptor_post_create_proposed_namespace_is_missing()
    {
        var targetLookupService = new StubRelationalWriteTargetLookupService
        {
            PostResult = new RelationalWriteTargetLookupResult.CreateNew(_documentUuid),
        };
        var sessionFactory = new RecordingNamespaceWriteSessionFactory(SqlDialect.Pgsql);
        sessionFactory.Session.Executor.NamespaceResults.Enqueue(
            new NamespaceAuthorizationExecutionResult.NotAuthorized(ProposedMissingFailure())
        );
        var sut = CreateSut(sessionFactory, targetLookupService);

        var result = await sut.HandlePostAsync(
            CreatePostRequest(
                namespacePrefixes: ["uri://ed-fi.org/"],
                authorizationStrategy: NamespaceStrategy()
            )
        );

        result
            .As<UpsertResult.UpsertFailureNamespaceNotAuthorized>()
            .NamespaceFailure.FailureKind.Should()
            .Be(NamespaceAuthorizationFailureKind.ProposedNamespaceMissing);
        sessionFactory
            .Session.Executor.Commands.Should()
            .NotContain(command =>
                command.CommandText.Contains("INSERT INTO dms.\"Document\"", StringComparison.Ordinal)
            );
    }

    [Test]
    public async Task It_inserts_a_descriptor_when_post_create_proposed_namespace_matches_a_prefix()
    {
        var targetLookupService = new StubRelationalWriteTargetLookupService
        {
            PostResult = new RelationalWriteTargetLookupResult.CreateNew(_documentUuid),
        };
        var sessionFactory = new RecordingNamespaceWriteSessionFactory(SqlDialect.Pgsql);
        sessionFactory.Session.Executor.NamespaceResults.Enqueue(
            new NamespaceAuthorizationExecutionResult.Authorized()
        );
        sessionFactory.Session.Executor.ResultSets.Enqueue([CreateContentVersionRow()]);
        var sut = CreateSut(sessionFactory, targetLookupService);

        var result = await sut.HandlePostAsync(
            CreatePostRequest(
                namespacePrefixes: ["uri://ed-fi.org/"],
                authorizationStrategy: NamespaceStrategy()
            )
        );

        result.Should().BeOfType<UpsertResult.InsertSuccess>();
        sessionFactory
            .Session.Executor.Commands.Should()
            .Contain(command =>
                command.CommandText.Contains("INSERT INTO dms.\"Document\"", StringComparison.Ordinal)
            );
        sessionFactory.Session.CommitCallCount.Should().Be(1);
    }

    /// <summary>
    /// A descriptor create stamps <c>CreatedByOwnershipTokenId</c> exactly as a regular-resource create does.
    /// Ownership <em>enforcement</em> for descriptors remains a 501 for this ticket, but stamping is
    /// unconditional and does not wait for it: an unstamped descriptor could never be reached by ownership
    /// authorization once it is enabled.
    /// </summary>
    [TestCase(SqlDialect.Pgsql, "\"CreatedByOwnershipTokenId\"")]
    [TestCase(SqlDialect.Mssql, "[CreatedByOwnershipTokenId]")]
    public async Task It_stamps_the_creator_ownership_token_on_a_descriptor_create(
        SqlDialect dialect,
        string quotedColumn
    )
    {
        var sessionFactory = await InsertDescriptorWithOwnershipContextAsync(
            dialect,
            new RelationalAuthorizationContext(
                [],
                ["uri://ed-fi.org/"],
                creatorOwnershipTokenId: 42,
                ownershipTokenIds: []
            )
        );

        var insert = DescriptorDocumentInsert(sessionFactory, quotedColumn);
        insert
            .Parameters.Should()
            .ContainSingle(parameter => parameter.Name == "@createdByOwnershipTokenId")
            .Which.Value.Should()
            .Be((short)42);
    }

    [TestCase(SqlDialect.Pgsql, "\"CreatedByOwnershipTokenId\"")]
    [TestCase(SqlDialect.Mssql, "[CreatedByOwnershipTokenId]")]
    public async Task It_stamps_null_on_a_descriptor_create_when_the_client_has_no_creator_token(
        SqlDialect dialect,
        string quotedColumn
    )
    {
        var sessionFactory = await InsertDescriptorWithOwnershipContextAsync(
            dialect,
            new RelationalAuthorizationContext(
                [],
                ["uri://ed-fi.org/"],
                creatorOwnershipTokenId: null,
                ownershipTokenIds: [7]
            )
        );

        var insert = DescriptorDocumentInsert(sessionFactory, quotedColumn);
        insert
            .Parameters.Should()
            .ContainSingle(parameter => parameter.Name == "@createdByOwnershipTokenId")
            .Which.Value.Should()
            .BeNull();
    }

    /// <summary>
    /// The SQL Server insert captures the insert-time <c>ContentVersion</c> through
    /// <c>OUTPUT ... INTO</c>, and that capture must keep working with the added column — an added column is
    /// exactly the kind of change that would silently break an <c>OUTPUT</c> clause's column list if the two
    /// were coupled.
    /// </summary>
    [Test]
    public async Task It_keeps_the_sql_server_content_version_capture_alongside_the_ownership_column()
    {
        var sessionFactory = await InsertDescriptorWithOwnershipContextAsync(
            SqlDialect.Mssql,
            new RelationalAuthorizationContext(
                [],
                ["uri://ed-fi.org/"],
                creatorOwnershipTokenId: 42,
                ownershipTokenIds: []
            )
        );

        var insert = DescriptorDocumentInsert(sessionFactory, "[CreatedByOwnershipTokenId]");
        insert
            .CommandText.Should()
            .Contain("OUTPUT INSERTED.[ContentVersion] INTO @insertedContentVersion ([ContentVersion])");
    }

    private static async Task<RecordingNamespaceWriteSessionFactory> InsertDescriptorWithOwnershipContextAsync(
        SqlDialect dialect,
        RelationalAuthorizationContext authorizationContext
    )
    {
        var targetLookupService = new StubRelationalWriteTargetLookupService
        {
            PostResult = new RelationalWriteTargetLookupResult.CreateNew(_documentUuid),
        };
        var sessionFactory = new RecordingNamespaceWriteSessionFactory(dialect);
        sessionFactory.Session.Executor.NamespaceResults.Enqueue(
            new NamespaceAuthorizationExecutionResult.Authorized()
        );
        sessionFactory.Session.Executor.ResultSets.Enqueue([CreateContentVersionRow()]);
        var sut = CreateSut(sessionFactory, targetLookupService);

        var request = new DescriptorWriteRequest(
            CreateMappingSet(dialect),
            _descriptorResource,
            CreateDescriptorRequestBody("uri://ed-fi.org/SchoolTypeDescriptor", "Charter"),
            _documentUuid,
            new ReferentialId(Guid.Parse("11111111-2222-3333-4444-555555555555")),
            new TraceId("descriptor-post-ownership-stamp"),
            [NamespaceStrategy()],
            authorizationContext
        );

        var result = await sut.HandlePostAsync(request);

        result.Should().BeOfType<UpsertResult.InsertSuccess>();
        return sessionFactory;
    }

    private static RelationalCommand DescriptorDocumentInsert(
        RecordingNamespaceWriteSessionFactory sessionFactory,
        string quotedOwnershipColumn
    ) =>
        sessionFactory
            .Session.Executor.Commands.Should()
            .ContainSingle(command =>
                command.CommandText.Contains(quotedOwnershipColumn, StringComparison.Ordinal)
            )
            .Subject;

    [Test]
    public async Task It_returns_namespace_403_not_precondition_failed_when_descriptor_post_create_under_stale_if_match_has_a_proposed_namespace_denial()
    {
        var targetLookupService = new StubRelationalWriteTargetLookupService
        {
            PostResult = new RelationalWriteTargetLookupResult.CreateNew(_documentUuid),
        };
        var sessionFactory = new RecordingNamespaceWriteSessionFactory(SqlDialect.Pgsql);
        sessionFactory.Session.Executor.NamespaceResults.Enqueue(
            new NamespaceAuthorizationExecutionResult.NotAuthorized(ProposedMismatchFailure())
        );
        var sut = CreateSut(sessionFactory, targetLookupService);
        var request = CreatePostRequest(
            namespacePrefixes: ["uri://ed-fi.org/"],
            authorizationStrategy: NamespaceStrategy(),
            @namespace: "uri://other.org/SchoolTypeDescriptor"
        ) with
        {
            WritePrecondition = new WritePrecondition.IfMatch("\"stale-etag\""),
        };

        var result = await sut.HandlePostAsync(request);

        result.Should().BeOfType<UpsertResult.UpsertFailureNamespaceNotAuthorized>();
        result
            .As<UpsertResult.UpsertFailureNamespaceNotAuthorized>()
            .NamespaceFailure.ValueSource.Should()
            .Be(NamespaceAuthorizationFailureValueSource.Proposed);
        sessionFactory
            .Session.Executor.Commands.Should()
            .NotContain(command =>
                command.CommandText.Contains("INSERT INTO dms.\"Document\"", StringComparison.Ordinal)
            );
        sessionFactory.Session.CommitCallCount.Should().Be(0);
    }

    [Test]
    public async Task It_returns_namespace_403_and_does_not_insert_when_descriptor_post_create_under_wildcard_if_none_match_has_a_proposed_namespace_denial()
    {
        // If-None-Match on a CreateNew descriptor POST is the new "proceed to insert" branch, but the
        // proposed-namespace check runs inside the locked resolve before the insert. A denial must
        // therefore return the namespace 403, issue no INSERT, and never commit — mirroring the
        // If-Match create case so a future switch-reordering that inserted before the auth check fails
        // here rather than opening a namespace-authorization bypass.
        var targetLookupService = new StubRelationalWriteTargetLookupService
        {
            PostResult = new RelationalWriteTargetLookupResult.CreateNew(_documentUuid),
        };
        var sessionFactory = new RecordingNamespaceWriteSessionFactory(SqlDialect.Pgsql);
        sessionFactory.Session.Executor.NamespaceResults.Enqueue(
            new NamespaceAuthorizationExecutionResult.NotAuthorized(ProposedMismatchFailure())
        );
        var sut = CreateSut(sessionFactory, targetLookupService);
        var request = CreatePostRequest(
            namespacePrefixes: ["uri://ed-fi.org/"],
            authorizationStrategy: NamespaceStrategy(),
            @namespace: "uri://other.org/SchoolTypeDescriptor"
        ) with
        {
            WritePrecondition = new WritePrecondition.IfNoneMatch("*", IsWildcard: true),
        };

        var result = await sut.HandlePostAsync(request);

        result.Should().BeOfType<UpsertResult.UpsertFailureNamespaceNotAuthorized>();
        result
            .As<UpsertResult.UpsertFailureNamespaceNotAuthorized>()
            .NamespaceFailure.ValueSource.Should()
            .Be(NamespaceAuthorizationFailureValueSource.Proposed);
        sessionFactory
            .Session.Executor.Commands.Should()
            .NotContain(command =>
                command.CommandText.Contains("INSERT INTO dms.\"Document\"", StringComparison.Ordinal)
            );
        sessionFactory.Session.CommitCallCount.Should().Be(0);
    }

    [Test]
    public async Task It_returns_namespace_403_and_does_not_update_when_descriptor_post_upsert_stored_namespace_is_not_authorized()
    {
        var documentId = 345L;
        var targetLookupService = new StubRelationalWriteTargetLookupService
        {
            PostResult = new RelationalWriteTargetLookupResult.ExistingDocument(
                documentId,
                _documentUuid,
                44L
            ),
        };
        var sessionFactory = new RecordingNamespaceWriteSessionFactory(SqlDialect.Pgsql);
        // Lock scalar then persisted-row read, then a denied stored namespace check.
        sessionFactory.Session.ScalarResults.Enqueue(44L);
        sessionFactory.Session.Executor.ResultSets.Enqueue([CreatePersistedDescriptorRow()]);
        sessionFactory.Session.Executor.NamespaceResults.Enqueue(
            new NamespaceAuthorizationExecutionResult.NotAuthorized(StoredMismatchFailure())
        );
        var sut = CreateSut(sessionFactory, targetLookupService);

        var result = await sut.HandlePostAsync(
            CreatePostRequest(
                namespacePrefixes: ["uri://ed-fi.org/"],
                authorizationStrategy: NamespaceStrategy()
            )
        );

        result
            .As<UpsertResult.UpsertFailureNamespaceNotAuthorized>()
            .NamespaceFailure.ValueSource.Should()
            .Be(NamespaceAuthorizationFailureValueSource.Stored);
        sessionFactory
            .Session.Executor.Commands.Should()
            .NotContain(command =>
                command.CommandText.Contains("UPDATE dms.\"Descriptor\"", StringComparison.Ordinal)
            );
        sessionFactory.Session.CommitCallCount.Should().Be(0);
        sessionFactory.Session.RollbackCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_returns_namespace_403_and_does_not_update_when_descriptor_post_upsert_proposed_namespace_is_not_authorized()
    {
        var documentId = 345L;
        var targetLookupService = new StubRelationalWriteTargetLookupService
        {
            PostResult = new RelationalWriteTargetLookupResult.ExistingDocument(
                documentId,
                _documentUuid,
                44L
            ),
        };
        var sessionFactory = new RecordingNamespaceWriteSessionFactory(SqlDialect.Pgsql);
        sessionFactory.Session.ScalarResults.Enqueue(44L);
        sessionFactory.Session.Executor.ResultSets.Enqueue([CreatePersistedDescriptorRow()]);
        sessionFactory.Session.Executor.NamespaceResults.Enqueue(
            new NamespaceAuthorizationExecutionResult.Authorized()
        );
        sessionFactory.Session.Executor.NamespaceResults.Enqueue(
            new NamespaceAuthorizationExecutionResult.NotAuthorized(ProposedMismatchFailure())
        );
        var sut = CreateSut(sessionFactory, targetLookupService);

        var result = await sut.HandlePostAsync(
            CreatePostRequest(
                namespacePrefixes: ["uri://ed-fi.org/"],
                authorizationStrategy: NamespaceStrategy(),
                @namespace: "uri://other.org/SchoolTypeDescriptor"
            )
        );

        result
            .As<UpsertResult.UpsertFailureNamespaceNotAuthorized>()
            .NamespaceFailure.ValueSource.Should()
            .Be(NamespaceAuthorizationFailureValueSource.Proposed);
        sessionFactory
            .Session.Executor.Commands.Should()
            .NotContain(command =>
                command.CommandText.Contains("UPDATE dms.\"Descriptor\"", StringComparison.Ordinal)
            );
        sessionFactory.Session.CommitCallCount.Should().Be(0);
    }

    [Test]
    public async Task It_updates_descriptor_when_post_upsert_stored_and_proposed_namespaces_are_authorized()
    {
        var documentId = 345L;
        var targetLookupService = new StubRelationalWriteTargetLookupService
        {
            PostResult = new RelationalWriteTargetLookupResult.ExistingDocument(
                documentId,
                _documentUuid,
                44L
            ),
        };
        var sessionFactory = new RecordingNamespaceWriteSessionFactory(SqlDialect.Pgsql);
        sessionFactory.Session.ScalarResults.Enqueue(44L);
        // Persisted row has a different shortDescription so the no-op check doesn't kick in.
        sessionFactory.Session.Executor.ResultSets.Enqueue([CreatePersistedDescriptorRowWithEdFiNamespace()]);
        sessionFactory.Session.Executor.ResultSets.Enqueue([CreateContentVersionRow()]);
        sessionFactory.Session.Executor.NamespaceResults.Enqueue(
            new NamespaceAuthorizationExecutionResult.Authorized()
        );
        sessionFactory.Session.Executor.NamespaceResults.Enqueue(
            new NamespaceAuthorizationExecutionResult.Authorized()
        );
        var sut = CreateSut(sessionFactory, targetLookupService);

        var result = await sut.HandlePostAsync(
            CreatePostRequest(
                namespacePrefixes: ["uri://ed-fi.org/"],
                authorizationStrategy: NamespaceStrategy(),
                codeValue: "ChangedCode"
            )
        );

        result.Should().BeOfType<UpsertResult.UpdateSuccess>();
        sessionFactory
            .Session.Executor.Commands.Should()
            .Contain(command =>
                command.CommandText.Contains("UPDATE dms.\"Descriptor\"", StringComparison.Ordinal)
            );
        sessionFactory.Session.CommitCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_returns_namespace_403_and_does_not_update_when_descriptor_put_stored_namespace_is_not_authorized()
    {
        var documentId = 345L;
        var targetLookupService = new StubRelationalWriteTargetLookupService
        {
            PutResult = new RelationalWriteTargetLookupResult.ExistingDocument(
                documentId,
                _documentUuid,
                44L
            ),
        };
        var sessionFactory = new RecordingNamespaceWriteSessionFactory(SqlDialect.Pgsql);
        sessionFactory.Session.ScalarResults.Enqueue(44L);
        sessionFactory.Session.Executor.ResultSets.Enqueue([CreatePersistedDescriptorRow()]);
        sessionFactory.Session.Executor.NamespaceResults.Enqueue(
            new NamespaceAuthorizationExecutionResult.NotAuthorized(StoredMismatchFailure())
        );
        var sut = CreateSut(sessionFactory, targetLookupService);

        var result = await sut.HandlePutAsync(
            CreatePutRequest(
                namespacePrefixes: ["uri://ed-fi.org/"],
                authorizationStrategy: NamespaceStrategy()
            )
        );

        result
            .As<UpdateResult.UpdateFailureNamespaceNotAuthorized>()
            .NamespaceFailure.ValueSource.Should()
            .Be(NamespaceAuthorizationFailureValueSource.Stored);
        sessionFactory
            .Session.Executor.Commands.Should()
            .NotContain(command =>
                command.CommandText.Contains("UPDATE dms.\"Descriptor\"", StringComparison.Ordinal)
            );
        sessionFactory.Session.CommitCallCount.Should().Be(0);
    }

    [Test]
    public async Task It_returns_namespace_403_and_does_not_update_when_descriptor_put_proposed_namespace_is_not_authorized()
    {
        var documentId = 345L;
        var targetLookupService = new StubRelationalWriteTargetLookupService
        {
            PutResult = new RelationalWriteTargetLookupResult.ExistingDocument(
                documentId,
                _documentUuid,
                44L
            ),
        };
        var sessionFactory = new RecordingNamespaceWriteSessionFactory(SqlDialect.Pgsql);
        sessionFactory.Session.ScalarResults.Enqueue(44L);
        sessionFactory.Session.Executor.ResultSets.Enqueue([CreatePersistedDescriptorRow()]);
        sessionFactory.Session.Executor.NamespaceResults.Enqueue(
            new NamespaceAuthorizationExecutionResult.Authorized()
        );
        sessionFactory.Session.Executor.NamespaceResults.Enqueue(
            new NamespaceAuthorizationExecutionResult.NotAuthorized(ProposedMismatchFailure())
        );
        var sut = CreateSut(sessionFactory, targetLookupService);

        var result = await sut.HandlePutAsync(
            CreatePutRequest(
                namespacePrefixes: ["uri://ed-fi.org/"],
                authorizationStrategy: NamespaceStrategy(),
                @namespace: "uri://other.org/SchoolTypeDescriptor"
            )
        );

        result
            .As<UpdateResult.UpdateFailureNamespaceNotAuthorized>()
            .NamespaceFailure.ValueSource.Should()
            .Be(NamespaceAuthorizationFailureValueSource.Proposed);
        sessionFactory
            .Session.Executor.Commands.Should()
            .NotContain(command =>
                command.CommandText.Contains("UPDATE dms.\"Descriptor\"", StringComparison.Ordinal)
            );
    }

    [Test]
    public async Task It_updates_descriptor_when_put_stored_and_proposed_namespaces_are_authorized()
    {
        var documentId = 345L;
        var targetLookupService = new StubRelationalWriteTargetLookupService
        {
            PutResult = new RelationalWriteTargetLookupResult.ExistingDocument(
                documentId,
                _documentUuid,
                44L
            ),
        };
        var sessionFactory = new RecordingNamespaceWriteSessionFactory(SqlDialect.Pgsql);
        sessionFactory.Session.ScalarResults.Enqueue(44L);
        // Persisted has Description set; the request body sends no description so IsUnchanged is false
        // and the handler issues an UPDATE rather than a no-op rollback.
        sessionFactory.Session.Executor.ResultSets.Enqueue([CreatePersistedDescriptorRowWithEdFiNamespace()]);
        sessionFactory.Session.Executor.ResultSets.Enqueue([CreateContentVersionRow()]);
        sessionFactory.Session.Executor.NamespaceResults.Enqueue(
            new NamespaceAuthorizationExecutionResult.Authorized()
        );
        sessionFactory.Session.Executor.NamespaceResults.Enqueue(
            new NamespaceAuthorizationExecutionResult.Authorized()
        );
        var sut = CreateSut(sessionFactory, targetLookupService);

        // Keep the codeValue (and Uri) identical to persisted to satisfy descriptor immutable identity
        // for PUT; the body's null Description differs from persisted "Original Description" so the
        // no-op path is bypassed.
        var result = await sut.HandlePutAsync(
            CreatePutRequest(
                namespacePrefixes: ["uri://ed-fi.org/"],
                authorizationStrategy: NamespaceStrategy()
            )
        );

        result.Should().BeOfType<UpdateResult.UpdateSuccess>();
        sessionFactory
            .Session.Executor.Commands.Should()
            .Contain(command =>
                command.CommandText.Contains("UPDATE dms.\"Descriptor\"", StringComparison.Ordinal)
            );
        sessionFactory.Session.CommitCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_returns_namespace_403_not_precondition_failed_when_descriptor_post_upsert_under_stale_if_match_has_stored_namespace_denial()
    {
        var documentId = 345L;
        var targetLookupService = new StubRelationalWriteTargetLookupService();
        var sessionFactory = new RecordingNamespaceWriteSessionFactory(SqlDialect.Pgsql);
        // IfMatch POST path: resolve target -> lock scalar -> load persisted -> stored ns check.
        sessionFactory.Session.Executor.ResultSets.Enqueue([
            CreateResolvedExistingDocumentRowWithId(documentId),
        ]);
        sessionFactory.Session.ScalarResults.Enqueue(44L);
        sessionFactory.Session.Executor.ResultSets.Enqueue([CreatePersistedDescriptorRow()]);
        sessionFactory.Session.Executor.NamespaceResults.Enqueue(
            new NamespaceAuthorizationExecutionResult.NotAuthorized(StoredMismatchFailure())
        );
        var sut = CreateSut(sessionFactory, targetLookupService);
        var request = CreatePostRequest(
            namespacePrefixes: ["uri://ed-fi.org/"],
            authorizationStrategy: NamespaceStrategy()
        ) with
        {
            WritePrecondition = new WritePrecondition.IfMatch("\"stale-etag\""),
        };

        var result = await sut.HandlePostAsync(request);

        result.Should().BeOfType<UpsertResult.UpsertFailureNamespaceNotAuthorized>();
        sessionFactory
            .Session.Executor.Commands.Should()
            .NotContain(command =>
                command.CommandText.Contains("UPDATE dms.\"Descriptor\"", StringComparison.Ordinal)
            );
        sessionFactory.Session.CommitCallCount.Should().Be(0);
    }

    [Test]
    public async Task It_returns_namespace_403_not_precondition_failed_when_descriptor_put_under_stale_if_match_has_stored_namespace_denial()
    {
        var documentId = 345L;
        var sessionFactory = new RecordingNamespaceWriteSessionFactory(SqlDialect.Pgsql);
        // IfMatch PUT path: resolve target via session executor -> lock scalar -> load persisted -> ns check.
        sessionFactory.Session.Executor.ResultSets.Enqueue([
            CreateResolvedExistingDocumentRowWithId(documentId),
        ]);
        sessionFactory.Session.ScalarResults.Enqueue(44L);
        sessionFactory.Session.Executor.ResultSets.Enqueue([CreatePersistedDescriptorRow()]);
        sessionFactory.Session.Executor.NamespaceResults.Enqueue(
            new NamespaceAuthorizationExecutionResult.NotAuthorized(StoredMismatchFailure())
        );
        var sut = CreateSut(sessionFactory);
        var request = CreatePutRequest(
            namespacePrefixes: ["uri://ed-fi.org/"],
            authorizationStrategy: NamespaceStrategy()
        ) with
        {
            WritePrecondition = new WritePrecondition.IfMatch("\"stale-etag\""),
        };

        var result = await sut.HandlePutAsync(request);

        result.Should().BeOfType<UpdateResult.UpdateFailureNamespaceNotAuthorized>();
        sessionFactory
            .Session.Executor.Commands.Should()
            .NotContain(command =>
                command.CommandText.Contains("UPDATE dms.\"Descriptor\"", StringComparison.Ordinal)
            );
        sessionFactory.Session.CommitCallCount.Should().Be(0);
    }

    [Test]
    public async Task It_maps_invalid_namespace_authorization_metadata_to_a_security_configuration_failure_for_post()
    {
        var targetLookupService = new StubRelationalWriteTargetLookupService
        {
            PostResult = new RelationalWriteTargetLookupResult.CreateNew(_documentUuid),
        };
        var sessionFactory = new RecordingNamespaceWriteSessionFactory(SqlDialect.Pgsql);
        sessionFactory.Session.Executor.NamespaceResults.Enqueue(
            new NamespaceAuthorizationExecutionResult.InvalidAuthorizationFailure(
                "Namespace authorization failed, but the AUTH1 failure metadata could not be mapped.",
                [
                    new SecurityConfigurationFailureDiagnostic(
                        ProviderOrPlannerFailureKind: AuthorizationSecurityConfigurationDiagnostics.NamespaceAuth1PayloadMappingFailed
                    ),
                ]
            )
        );
        var sut = CreateSut(sessionFactory, targetLookupService);

        var result = await sut.HandlePostAsync(
            CreatePostRequest(
                namespacePrefixes: ["uri://ed-fi.org/"],
                authorizationStrategy: NamespaceStrategy()
            )
        );

        result
            .Should()
            .BeOfType<UpsertResult.UpsertFailureSecurityConfiguration>()
            .Which.Diagnostics.Should()
            .ContainSingle()
            .Which.ProviderOrPlannerFailureKind.Should()
            .Be(AuthorizationSecurityConfigurationDiagnostics.NamespaceAuth1PayloadMappingFailed);
    }

    [Test]
    public async Task It_maps_a_postgresql_namespace_auth1_denial_to_namespace_not_authorized_for_descriptor_post_create()
    {
        // PostgreSQL surfaces the AUTH1 discriminator in SqlState, so the provider failure extractor
        // must recover it. With the extractor threaded through, the handler's namespace executor maps
        // the provider failure to a 403 namespace denial rather than letting it escape as a 500.
        var payloadText = NamespaceAuthorizationAuth1FailurePayloadCodec.Encode(
            new NamespaceAuthorizationAuth1FailurePayload(
                0,
                NamespaceAuthorizationAuth1FailureKind.NamespaceMismatch
            )
        );
        var targetLookupService = new StubRelationalWriteTargetLookupService
        {
            PostResult = new RelationalWriteTargetLookupResult.CreateNew(_documentUuid),
        };
        var sessionFactory = new RecordingNamespaceWriteSessionFactory(SqlDialect.Pgsql);
        sessionFactory.Session.Executor.NamespaceAuthorizationException = new StubDbException(
            "PostgreSQL provider exception"
        );
        var sut = CreateSut(
            sessionFactory,
            targetLookupService,
            new StubRelationshipAuthorizationProviderFailureExtractor(
                NamespaceAuthorizationAuth1FailurePayloadCodec.ProviderFailureCode,
                payloadText
            )
        );

        var result = await sut.HandlePostAsync(
            CreatePostRequest(
                namespacePrefixes: ["uri://ed-fi.org/"],
                authorizationStrategy: NamespaceStrategy(),
                @namespace: "uri://other.org/SchoolTypeDescriptor"
            )
        );

        var notAuthorized = result
            .Should()
            .BeOfType<UpsertResult.UpsertFailureNamespaceNotAuthorized>()
            .Subject;
        notAuthorized
            .NamespaceFailure.FailureKind.Should()
            .Be(NamespaceAuthorizationFailureKind.NamespaceMismatch);
        notAuthorized
            .NamespaceFailure.ValueSource.Should()
            .Be(NamespaceAuthorizationFailureValueSource.Proposed);
        sessionFactory.Session.CommitCallCount.Should().Be(0);
        sessionFactory.Session.RollbackCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_escapes_a_postgresql_namespace_auth1_denial_as_an_unknown_failure_for_descriptor_post_create_with_the_default_message_only_extractor()
    {
        // Regression guard for the threading fix: the default extractor reads only the provider message,
        // never SqlState, so it cannot recover a PostgreSQL AUTH1 namespace payload. Without the
        // SqlState-aware extractor the denial escapes the namespace mapping and surfaces as a 500.
        var targetLookupService = new StubRelationalWriteTargetLookupService
        {
            PostResult = new RelationalWriteTargetLookupResult.CreateNew(_documentUuid),
        };
        var sessionFactory = new RecordingNamespaceWriteSessionFactory(SqlDialect.Pgsql);
        sessionFactory.Session.Executor.NamespaceAuthorizationException = new StubDbException(
            "PostgreSQL provider exception"
        );
        var sut = CreateSut(sessionFactory, targetLookupService);

        var result = await sut.HandlePostAsync(
            CreatePostRequest(
                namespacePrefixes: ["uri://ed-fi.org/"],
                authorizationStrategy: NamespaceStrategy(),
                @namespace: "uri://other.org/SchoolTypeDescriptor"
            )
        );

        result.Should().BeOfType<UpsertResult.UnknownFailure>();
        sessionFactory.Session.CommitCallCount.Should().Be(0);
    }

    [Test]
    public async Task It_maps_invalid_namespace_authorization_metadata_to_a_security_configuration_failure_for_put()
    {
        var documentId = 345L;
        var targetLookupService = new StubRelationalWriteTargetLookupService
        {
            PutResult = new RelationalWriteTargetLookupResult.ExistingDocument(
                documentId,
                _documentUuid,
                44L
            ),
        };
        var sessionFactory = new RecordingNamespaceWriteSessionFactory(SqlDialect.Pgsql);
        sessionFactory.Session.ScalarResults.Enqueue(44L);
        sessionFactory.Session.Executor.ResultSets.Enqueue([CreatePersistedDescriptorRow()]);
        sessionFactory.Session.Executor.NamespaceResults.Enqueue(
            new NamespaceAuthorizationExecutionResult.InvalidAuthorizationFailure(
                "Namespace authorization failed, but the AUTH1 failure metadata could not be mapped.",
                [
                    new SecurityConfigurationFailureDiagnostic(
                        ProviderOrPlannerFailureKind: AuthorizationSecurityConfigurationDiagnostics.NamespaceAuth1PayloadMappingFailed
                    ),
                ]
            )
        );
        var sut = CreateSut(sessionFactory, targetLookupService);

        var result = await sut.HandlePutAsync(
            CreatePutRequest(
                namespacePrefixes: ["uri://ed-fi.org/"],
                authorizationStrategy: NamespaceStrategy()
            )
        );

        result
            .Should()
            .BeOfType<UpdateResult.UpdateFailureSecurityConfiguration>()
            .Which.Diagnostics.Should()
            .ContainSingle()
            .Which.ProviderOrPlannerFailureKind.Should()
            .Be(AuthorizationSecurityConfigurationDiagnostics.NamespaceAuth1PayloadMappingFailed);
    }

    [Test]
    public async Task It_validates_a_descriptor_post_custom_view_configured_before_a_namespace_no_prefixes_terminal()
    {
        var sessionFactory = new RecordingNamespaceWriteSessionFactory(SqlDialect.Pgsql);
        var validationExecutor = new RecordingCustomViewValidationExecutor();
        var sut = CreateSut(sessionFactory, customViewValidationCommandExecutor: validationExecutor);

        var result = await sut.HandlePostAsync(
            CreatePostRequest(
                namespacePrefixes: [],
                authorizationStrategies: [DeleteCustomViewStrategy(), NamespaceStrategy()]
            )
        );

        result
            .Should()
            .BeOfType<UpsertResult.UpsertFailureNamespaceNotAuthorized>()
            .Which.NamespaceFailure.FailureKind.Should()
            .Be(NamespaceAuthorizationFailureKind.NoPrefixesConfigured);
        validationExecutor
            .Commands.Should()
            .ContainSingle()
            .Which.Should()
            .Contain(DeleteCustomViewStrategyName);
        sessionFactory.CreateAsyncCallCount.Should().Be(0);
    }

    [Test]
    public async Task It_does_not_validate_a_descriptor_post_custom_view_configured_after_a_namespace_no_prefixes_terminal()
    {
        var sessionFactory = new RecordingNamespaceWriteSessionFactory(SqlDialect.Pgsql);
        var validationExecutor = new RecordingCustomViewValidationExecutor();
        var sut = CreateSut(sessionFactory, customViewValidationCommandExecutor: validationExecutor);

        var result = await sut.HandlePostAsync(
            CreatePostRequest(
                namespacePrefixes: [],
                authorizationStrategies: [NamespaceStrategy(), DeleteCustomViewStrategy()]
            )
        );

        result.Should().BeOfType<UpsertResult.UpsertFailureNamespaceNotAuthorized>();
        validationExecutor.Commands.Should().BeEmpty();
        sessionFactory.CreateAsyncCallCount.Should().Be(0);
    }

    [Test]
    public async Task It_validates_a_descriptor_put_custom_view_configured_before_an_unsupported_strategy()
    {
        var sessionFactory = new RecordingNamespaceWriteSessionFactory(SqlDialect.Pgsql);
        var validationExecutor = new RecordingCustomViewValidationExecutor();
        var sut = CreateSut(sessionFactory, customViewValidationCommandExecutor: validationExecutor);

        var result = await sut.HandlePutAsync(
            CreatePutRequest(
                namespacePrefixes: ["uri://ed-fi.org/"],
                authorizationStrategies:
                [
                    DeleteCustomViewStrategy(),
                    UnsupportedStrategy(AuthorizationStrategyNameConstants.OwnershipBased),
                ]
            )
        );

        result.Should().BeOfType<UpdateResult.UpdateFailureNotImplemented>();
        validationExecutor
            .Commands.Should()
            .ContainSingle()
            .Which.Should()
            .Contain(DeleteCustomViewStrategyName);
        sessionFactory.CreateAsyncCallCount.Should().Be(0);
    }

    [Test]
    public async Task It_fails_closed_for_a_descriptor_write_custom_view_needing_a_proposed_reference_value_before_a_namespace_terminal()
    {
        // The view is configured before the namespace terminal, so it executes first. Planning it through the
        // page planner accepted a basis this path cannot execute and reported the namespace 403 instead; the
        // terminal has to plan with the same descriptor-write rules the Plan outcome uses.
        var sessionFactory = new RecordingNamespaceWriteSessionFactory(SqlDialect.Pgsql);
        var sut = CreateSut(sessionFactory);

        var result = await sut.HandlePostAsync(
            CreatePostRequest(
                namespacePrefixes: [],
                authorizationStrategies: [CustomViewStrategy("StudentWithATag"), NamespaceStrategy()],
                mappingSet: CreateMappingSetWithStudentReference(SqlDialect.Pgsql)
            )
        );

        result
            .Should()
            .BeOfType<UpsertResult.UpsertFailureSecurityConfiguration>()
            .Which.Errors.Should()
            .ContainSingle()
            .Which.Should()
            .Be(
                CustomViewAuthorizationSecurityConfigurationMessages.UnsupportedProposedBasisForDescriptorWrite(
                    "StudentWithATag"
                )
            );
        sessionFactory.CreateAsyncCallCount.Should().Be(0);
    }

    [Test]
    public async Task It_validates_an_earlier_descriptor_write_custom_view_before_an_unknown_strategy_terminal()
    {
        // The later basis does not resolve, so the strategy-level planner reports it and the write stops at
        // that terminal. The earlier self-basis view is configured before it and executes first, so the
        // terminal has to carry and validate it. Reached through WriteTerminal, which is why this fails if
        // that terminal plans with the page planner instead of the descriptor-write single-record path.
        var sessionFactory = new RecordingNamespaceWriteSessionFactory(SqlDialect.Pgsql);
        var validationExecutor = new RecordingCustomViewValidationExecutor();
        var sut = CreateSut(sessionFactory, customViewValidationCommandExecutor: validationExecutor);

        var result = await sut.HandlePostAsync(
            CreatePostRequest(
                namespacePrefixes: ["uri://ed-fi.org/"],
                authorizationStrategies: [DeleteCustomViewStrategy(), CustomViewStrategy("MeetingWithATag")]
            )
        );

        result.Should().BeOfType<UpsertResult.UpsertFailureSecurityConfiguration>();
        validationExecutor
            .Commands.Should()
            .ContainSingle()
            .Which.Should()
            .Contain(DeleteCustomViewStrategyName);
        sessionFactory.CreateAsyncCallCount.Should().Be(0);
    }

    [Test]
    public async Task It_validates_an_earlier_descriptor_write_custom_view_before_an_unsupported_proposed_basis()
    {
        // Same ordering rule for the fail-closed proposed-basis rejection: the view configured before it
        // planned successfully and executes first, so it is validated before that failure is reported.
        var sessionFactory = new RecordingNamespaceWriteSessionFactory(SqlDialect.Pgsql);
        var validationExecutor = new RecordingCustomViewValidationExecutor();
        var sut = CreateSut(sessionFactory, customViewValidationCommandExecutor: validationExecutor);

        var result = await sut.HandlePostAsync(
            CreatePostRequest(
                namespacePrefixes: ["uri://ed-fi.org/"],
                authorizationStrategies: [DeleteCustomViewStrategy(), CustomViewStrategy("StudentWithATag")],
                mappingSet: CreateMappingSetWithStudentReference(SqlDialect.Pgsql)
            )
        );

        result
            .Should()
            .BeOfType<UpsertResult.UpsertFailureSecurityConfiguration>()
            .Which.Errors.Should()
            .ContainSingle()
            .Which.Should()
            .Be(
                CustomViewAuthorizationSecurityConfigurationMessages.UnsupportedProposedBasisForDescriptorWrite(
                    "StudentWithATag"
                )
            );
        validationExecutor
            .Commands.Should()
            .ContainSingle()
            .Which.Should()
            .Contain(DeleteCustomViewStrategyName);
        sessionFactory.CreateAsyncCallCount.Should().Be(0);
    }

    [Test]
    public async Task It_fails_closed_for_a_descriptor_write_custom_view_needing_a_proposed_reference_value()
    {
        // A basis reached through a document reference needs a proposed basis value bound from the finalized
        // root row, which descriptor writes have no equivalent of. Skipping the check would serve a write the
        // strategy restricts, so the plan is rejected before the write session opens.
        var sessionFactory = new RecordingNamespaceWriteSessionFactory(SqlDialect.Pgsql);
        var sut = CreateSut(sessionFactory);

        var result = await sut.HandlePostAsync(
            CreatePostRequest(
                namespacePrefixes: [],
                authorizationStrategies: [CustomViewStrategy("StudentWithATag")],
                mappingSet: CreateMappingSetWithStudentReference(SqlDialect.Pgsql)
            )
        );

        result
            .Should()
            .BeOfType<UpsertResult.UpsertFailureSecurityConfiguration>()
            .Which.Errors.Should()
            .ContainSingle()
            .Which.Should()
            .Be(
                CustomViewAuthorizationSecurityConfigurationMessages.UnsupportedProposedBasisForDescriptorWrite(
                    "StudentWithATag"
                )
            );
        sessionFactory.CreateAsyncCallCount.Should().Be(0);
    }

    [Test]
    public async Task It_reports_a_self_basis_denial_through_the_precondition_helper_without_a_later_namespace_check()
    {
        // Seam E: the If-None-Match create resolves through the shared locked-precondition helper rather than
        // the plain create path. The exact defect this guards was the two paths disagreeing, so the same
        // configured-order rule has to hold here — the denial preempts both the namespace check and the
        // precondition outcome.
        var sessionFactory = new RecordingNamespaceWriteSessionFactory(SqlDialect.Pgsql);
        var validationExecutor = new RecordingCustomViewValidationExecutor();
        var targetLookupService = new StubRelationalWriteTargetLookupService
        {
            PostResult = new RelationalWriteTargetLookupResult.CreateNew(_documentUuid),
        };
        var sut = CreateSut(
            sessionFactory,
            targetLookupService,
            customViewValidationCommandExecutor: validationExecutor
        );

        var result = await sut.HandlePostAsync(
            CreatePostRequest(
                namespacePrefixes: ["uri://ed-fi.org/"],
                authorizationStrategies: [DeleteCustomViewStrategy(), NamespaceStrategy()],
                writePrecondition: new WritePrecondition.IfNoneMatch(["\"some-etag\""])
            )
        );

        result
            .Should()
            .BeOfType<UpsertResult.UpsertFailureCustomViewNotAuthorized>()
            .Which.CustomViewFailure.StrategyName.Should()
            .Be(DeleteCustomViewStrategyName);
        validationExecutor.Commands.Should().ContainSingle();
        // The outcome is the same in either ordering once namespace authorizes, so the ordering is only
        // observable in whether the namespace statement was issued at all.
        sessionFactory
            .Session.Executor.Commands.Should()
            .NotContain(command =>
                command.CommandText.Contains("namespacePrefixes", StringComparison.OrdinalIgnoreCase)
            );
    }

    [Test]
    public async Task It_runs_a_namespace_check_configured_first_before_a_precondition_helper_self_basis_denial()
    {
        // The mirror of the previous case through the same helper: NamespaceBased is configured first, so it
        // runs, and the denial is reported only once it authorizes.
        var sessionFactory = new RecordingNamespaceWriteSessionFactory(SqlDialect.Pgsql);
        var validationExecutor = new RecordingCustomViewValidationExecutor();
        var targetLookupService = new StubRelationalWriteTargetLookupService
        {
            PostResult = new RelationalWriteTargetLookupResult.CreateNew(_documentUuid),
        };
        var sut = CreateSut(
            sessionFactory,
            targetLookupService,
            customViewValidationCommandExecutor: validationExecutor
        );

        var result = await sut.HandlePostAsync(
            CreatePostRequest(
                namespacePrefixes: ["uri://ed-fi.org/"],
                authorizationStrategies: [NamespaceStrategy(), DeleteCustomViewStrategy()],
                writePrecondition: new WritePrecondition.IfNoneMatch(["\"some-etag\""])
            )
        );

        result
            .Should()
            .BeOfType<UpsertResult.UpsertFailureCustomViewNotAuthorized>()
            .Which.CustomViewFailure.StrategyName.Should()
            .Be(DeleteCustomViewStrategyName);
        validationExecutor.Commands.Should().ContainSingle();
        // The mirror assertion: configured first, the namespace statement is issued before the denial.
        sessionFactory
            .Session.Executor.Commands.Should()
            .Contain(command =>
                command.CommandText.Contains("namespacePrefixes", StringComparison.OrdinalIgnoreCase)
            );
    }

    [Test]
    public async Task It_reports_a_self_basis_denial_without_running_a_namespace_check_configured_after_it()
    {
        // Configured order decides the first failure. The denial is deterministic and configured first, so the
        // namespace check never runs — no session is opened at all.
        var sessionFactory = new RecordingNamespaceWriteSessionFactory(SqlDialect.Pgsql);
        var validationExecutor = new RecordingCustomViewValidationExecutor();
        var targetLookupService = new StubRelationalWriteTargetLookupService
        {
            PostResult = new RelationalWriteTargetLookupResult.CreateNew(_documentUuid),
        };
        var sut = CreateSut(
            sessionFactory,
            targetLookupService,
            customViewValidationCommandExecutor: validationExecutor
        );

        var result = await sut.HandlePostAsync(
            CreatePostRequest(
                namespacePrefixes: ["uri://ed-fi.org/"],
                authorizationStrategies: [DeleteCustomViewStrategy(), NamespaceStrategy()]
            )
        );

        result
            .Should()
            .BeOfType<UpsertResult.UpsertFailureCustomViewNotAuthorized>()
            .Which.CustomViewFailure.StrategyName.Should()
            .Be(DeleteCustomViewStrategyName);
        validationExecutor.Commands.Should().ContainSingle();
        sessionFactory.CreateAsyncCallCount.Should().Be(0);
    }

    [Test]
    public async Task It_runs_a_namespace_check_configured_before_a_self_basis_denial_first()
    {
        // The mirror case: NamespaceBased is configured first, so it runs — which requires a session — and the
        // denial is only reported once that check has authorized.
        var sessionFactory = new RecordingNamespaceWriteSessionFactory(SqlDialect.Pgsql);
        var validationExecutor = new RecordingCustomViewValidationExecutor();
        var targetLookupService = new StubRelationalWriteTargetLookupService
        {
            PostResult = new RelationalWriteTargetLookupResult.CreateNew(_documentUuid),
        };
        var sut = CreateSut(
            sessionFactory,
            targetLookupService,
            customViewValidationCommandExecutor: validationExecutor
        );

        var result = await sut.HandlePostAsync(
            CreatePostRequest(
                namespacePrefixes: ["uri://ed-fi.org/"],
                authorizationStrategies: [NamespaceStrategy(), DeleteCustomViewStrategy()]
            )
        );

        result
            .Should()
            .BeOfType<UpsertResult.UpsertFailureCustomViewNotAuthorized>()
            .Which.CustomViewFailure.StrategyName.Should()
            .Be(DeleteCustomViewStrategyName);
        // The session proves the earlier namespace check actually ran before the denial was reported.
        sessionFactory.CreateAsyncCallCount.Should().Be(1);
        validationExecutor.Commands.Should().ContainSingle();
    }

    [Test]
    public async Task It_denies_a_descriptor_post_create_with_a_self_basis_custom_view_after_validating_the_view()
    {
        // The row does not exist yet, so no view row can reference it — a deterministic auth.md 2.4 denial. The
        // view is still probed first so a misconfigured view keeps its own 500, and no session is opened.
        var sessionFactory = new RecordingNamespaceWriteSessionFactory(SqlDialect.Pgsql);
        var validationExecutor = new RecordingCustomViewValidationExecutor();
        var targetLookupService = new StubRelationalWriteTargetLookupService
        {
            PostResult = new RelationalWriteTargetLookupResult.CreateNew(_documentUuid),
        };
        var sut = CreateSut(
            sessionFactory,
            targetLookupService,
            customViewValidationCommandExecutor: validationExecutor
        );

        var result = await sut.HandlePostAsync(
            CreatePostRequest(namespacePrefixes: [], authorizationStrategies: [DeleteCustomViewStrategy()])
        );

        var failure = result
            .Should()
            .BeOfType<UpsertResult.UpsertFailureCustomViewNotAuthorized>()
            .Subject.CustomViewFailure;
        failure.StrategyName.Should().Be(DeleteCustomViewStrategyName);
        failure.FailureKind.Should().Be(CustomViewAuthorizationFailureKind.NoMatchingRow);
        failure.ValueSource.Should().Be(CustomViewAuthorizationFailureValueSource.Proposed);
        validationExecutor
            .Commands.Should()
            .ContainSingle()
            .Which.Should()
            .Contain(DeleteCustomViewStrategyName);
        sessionFactory.CreateAsyncCallCount.Should().Be(0);
    }

    [Test]
    public async Task It_runs_a_descriptor_update_custom_view_configured_after_namespace_before_the_proposed_namespace_check()
    {
        // Every stored check AND-composes against the locked row, in configured order, before the proposed
        // value's own check reads the request body. Running the proposed namespace check first would let its
        // 403 mask the stored custom-view answer configured after NamespaceBased — here, the
        // urn:ed-fi:api:system 500 its nonconforming view owes.
        var documentId = 345L;
        var targetLookupService = new StubRelationalWriteTargetLookupService
        {
            PutResult = new RelationalWriteTargetLookupResult.ExistingDocument(
                documentId,
                _documentUuid,
                44L
            ),
        };
        var sessionFactory = new RecordingNamespaceWriteSessionFactory(SqlDialect.Pgsql);
        sessionFactory.Session.ScalarResults.Enqueue(44L);
        sessionFactory.Session.Executor.ResultSets.Enqueue([CreatePersistedDescriptorRow()]);
        // The stored namespace check authorizes; the proposed one would deny, and must not get to answer
        // while a stored check configured after it is still owed.
        sessionFactory.Session.Executor.NamespaceResults.Enqueue(
            new NamespaceAuthorizationExecutionResult.Authorized()
        );
        sessionFactory.Session.Executor.NamespaceResults.Enqueue(
            new NamespaceAuthorizationExecutionResult.NotAuthorized(ProposedMismatchFailure())
        );
        var validationExecutor = new RecordingCustomViewValidationExecutor(
            new StubDbException("missing authorization view")
        );
        var sut = CreateSut(
            sessionFactory,
            targetLookupService,
            customViewValidationCommandExecutor: validationExecutor
        );

        var act = async () =>
            await sut.HandlePutAsync(
                CreatePutRequest(
                    namespacePrefixes: ["uri://ed-fi.org/"],
                    authorizationStrategies: [NamespaceStrategy(), DeleteCustomViewStrategy()],
                    @namespace: "uri://other.org/SchoolTypeDescriptor"
                )
            );

        await act.Should().ThrowAsync<CustomViewAuthorizationValidationException>();
        validationExecutor
            .Commands.Should()
            .ContainSingle()
            .Which.Should()
            .Contain(DeleteCustomViewStrategyName);
        sessionFactory.Session.CommitCallCount.Should().Be(0);
    }

    [Test]
    public async Task It_runs_a_precondition_path_custom_view_configured_after_namespace_before_the_proposed_namespace_check()
    {
        // The same ordering through the shared locked-precondition helper. The exact defect this guards is the
        // two paths disagreeing, so the stored sequence has to complete before the proposed check here too.
        var documentId = 345L;
        var sessionFactory = new RecordingNamespaceWriteSessionFactory(SqlDialect.Pgsql);
        // IfMatch PUT path: resolve target via session executor -> lock scalar -> load persisted -> checks.
        sessionFactory.Session.Executor.ResultSets.Enqueue([
            CreateResolvedExistingDocumentRowWithId(documentId),
        ]);
        sessionFactory.Session.ScalarResults.Enqueue(44L);
        sessionFactory.Session.Executor.ResultSets.Enqueue([CreatePersistedDescriptorRow()]);
        sessionFactory.Session.Executor.NamespaceResults.Enqueue(
            new NamespaceAuthorizationExecutionResult.Authorized()
        );
        sessionFactory.Session.Executor.NamespaceResults.Enqueue(
            new NamespaceAuthorizationExecutionResult.NotAuthorized(ProposedMismatchFailure())
        );
        var validationExecutor = new RecordingCustomViewValidationExecutor(
            new StubDbException("missing authorization view")
        );
        var sut = CreateSut(sessionFactory, customViewValidationCommandExecutor: validationExecutor);
        var request = CreatePutRequest(
            namespacePrefixes: ["uri://ed-fi.org/"],
            authorizationStrategies: [NamespaceStrategy(), DeleteCustomViewStrategy()],
            @namespace: "uri://other.org/SchoolTypeDescriptor"
        ) with
        {
            WritePrecondition = new WritePrecondition.IfMatch("\"stale-etag\""),
        };

        var act = async () => await sut.HandlePutAsync(request);

        await act.Should().ThrowAsync<CustomViewAuthorizationValidationException>();
        validationExecutor
            .Commands.Should()
            .ContainSingle()
            .Which.Should()
            .Contain(DeleteCustomViewStrategyName);
        sessionFactory.Session.CommitCallCount.Should().Be(0);
    }

    [Test]
    public async Task It_validates_a_descriptor_delete_custom_view_configured_before_a_namespace_no_prefixes_terminal()
    {
        // The namespace 403 resolves before the write session opens, but a custom view configured ahead of it
        // executes first, so a missing or non-conforming view keeps its own 500.
        var sessionFactory = new RecordingNamespaceWriteSessionFactory(SqlDialect.Pgsql);
        var validationExecutor = new RecordingCustomViewValidationExecutor();
        var sut = CreateSut(sessionFactory, customViewValidationCommandExecutor: validationExecutor);

        var result = await sut.HandleDeleteAsync(
            CreateDeleteRequest(
                namespacePrefixes: [],
                authorizationStrategies: [DeleteCustomViewStrategy(), NamespaceStrategy()]
            )
        );

        result
            .Should()
            .BeOfType<DeleteResult.DeleteFailureNamespaceNotAuthorized>()
            .Which.NamespaceFailure.FailureKind.Should()
            .Be(NamespaceAuthorizationFailureKind.NoPrefixesConfigured);
        validationExecutor
            .Commands.Should()
            .ContainSingle()
            .Which.Should()
            .Contain(DeleteCustomViewStrategyName);
        sessionFactory.CreateAsyncCallCount.Should().Be(0);
    }

    [Test]
    public async Task It_does_not_validate_a_descriptor_delete_custom_view_configured_after_a_namespace_no_prefixes_terminal()
    {
        // The run would have aborted at the namespace position, so a view configured after it never executes
        // and must not be probed either.
        var sessionFactory = new RecordingNamespaceWriteSessionFactory(SqlDialect.Pgsql);
        var validationExecutor = new RecordingCustomViewValidationExecutor();
        var sut = CreateSut(sessionFactory, customViewValidationCommandExecutor: validationExecutor);

        var result = await sut.HandleDeleteAsync(
            CreateDeleteRequest(
                namespacePrefixes: [],
                authorizationStrategies: [NamespaceStrategy(), DeleteCustomViewStrategy()]
            )
        );

        result.Should().BeOfType<DeleteResult.DeleteFailureNamespaceNotAuthorized>();
        validationExecutor.Commands.Should().BeEmpty();
        sessionFactory.CreateAsyncCallCount.Should().Be(0);
    }

    [Test]
    public async Task It_validates_a_descriptor_delete_custom_view_configured_before_an_unsupported_strategy()
    {
        // OwnershipBased executes last regardless of configured position, so every resolved view is validated
        // before its 501.
        var sessionFactory = new RecordingNamespaceWriteSessionFactory(SqlDialect.Pgsql);
        var validationExecutor = new RecordingCustomViewValidationExecutor();
        var sut = CreateSut(sessionFactory, customViewValidationCommandExecutor: validationExecutor);

        var result = await sut.HandleDeleteAsync(
            CreateDeleteRequest(
                namespacePrefixes: ["uri://ed-fi.org/"],
                authorizationStrategies:
                [
                    DeleteCustomViewStrategy(),
                    UnsupportedStrategy(AuthorizationStrategyNameConstants.OwnershipBased),
                ]
            )
        );

        result.Should().BeOfType<DeleteResult.DeleteFailureNotImplemented>();
        validationExecutor
            .Commands.Should()
            .ContainSingle()
            .Which.Should()
            .Contain(DeleteCustomViewStrategyName);
        sessionFactory.CreateAsyncCallCount.Should().Be(0);
    }

    [Test]
    public async Task It_returns_namespace_403_without_opening_a_session_when_the_client_has_no_prefixes()
    {
        var sessionFactory = new RecordingNamespaceWriteSessionFactory(SqlDialect.Pgsql);
        var sut = CreateSut(sessionFactory);

        var result = await sut.HandleDeleteAsync(
            CreateDeleteRequest(namespacePrefixes: [], authorizationStrategy: NamespaceStrategy())
        );

        result.Should().BeOfType<DeleteResult.DeleteFailureNamespaceNotAuthorized>();
        result
            .As<DeleteResult.DeleteFailureNamespaceNotAuthorized>()
            .NamespaceFailure.FailureKind.Should()
            .Be(NamespaceAuthorizationFailureKind.NoPrefixesConfigured);
        sessionFactory.CreateAsyncCallCount.Should().Be(0);
    }

    [Test]
    public async Task It_returns_security_configuration_for_descriptor_delete_with_an_unknown_strategy_without_opening_a_session()
    {
        const string unknownStrategyName = "UnknownDescriptorStrategy";
        var sessionFactory = new RecordingNamespaceWriteSessionFactory(SqlDialect.Pgsql);
        var sut = CreateSut(sessionFactory);

        var result = await sut.HandleDeleteAsync(
            CreateDeleteRequest(
                namespacePrefixes: ["uri://ed-fi.org/"],
                authorizationStrategy: UnsupportedStrategy(unknownStrategyName)
            )
        );

        var failure = result.Should().BeOfType<DeleteResult.DeleteFailureSecurityConfiguration>().Subject;
        failure
            .Errors.Should()
            .Equal(
                SecurityConfigurationFailureMessages.UnknownAuthorizationStrategies([unknownStrategyName])
            );
        sessionFactory.CreateAsyncCallCount.Should().Be(0);
    }

    [Test]
    public async Task It_returns_namespace_403_and_does_not_delete_when_the_stored_namespace_is_not_authorized()
    {
        var sessionFactory = new RecordingNamespaceWriteSessionFactory(SqlDialect.Pgsql);
        // No-IfMatch DELETE: resolve target -> lock (scalar) -> namespace check.
        sessionFactory.Session.Executor.ResultSets.Enqueue([CreateResolvedExistingDocumentRow()]);
        sessionFactory.Session.ScalarResults.Enqueue(44L);
        sessionFactory.Session.Executor.NamespaceResults.Enqueue(
            new NamespaceAuthorizationExecutionResult.NotAuthorized(StoredMismatchFailure())
        );
        var sut = CreateSut(sessionFactory);

        var result = await sut.HandleDeleteAsync(
            CreateDeleteRequest(
                namespacePrefixes: ["uri://ed-fi.org/"],
                authorizationStrategy: NamespaceStrategy()
            )
        );

        result.Should().BeOfType<DeleteResult.DeleteFailureNamespaceNotAuthorized>();
        sessionFactory
            .Session.Executor.Commands.Should()
            .NotContain(command =>
                command.CommandText.Contains("DELETE FROM dms.\"Document\"", StringComparison.Ordinal)
            );
        sessionFactory.Session.CommitCallCount.Should().Be(0);
        sessionFactory.Session.RollbackCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_returns_namespace_403_when_the_stored_namespace_is_uninitialized()
    {
        var uninitializedFailure = new NamespaceAuthorizationFailure(
            NamespaceAuthorizationFailureKind.StoredNamespaceUninitialized,
            NamespaceAuthorizationFailureValueSource.Stored,
            EmittedAuth1Index: 0,
            AuthorizationStrategyNameConstants.NamespaceBased,
            ConfiguredNamespacePrefixes: ["uri://ed-fi.org/"]
        );
        var sessionFactory = new RecordingNamespaceWriteSessionFactory(SqlDialect.Pgsql);
        sessionFactory.Session.Executor.ResultSets.Enqueue([CreateResolvedExistingDocumentRow()]);
        sessionFactory.Session.ScalarResults.Enqueue(44L);
        sessionFactory.Session.Executor.NamespaceResults.Enqueue(
            new NamespaceAuthorizationExecutionResult.NotAuthorized(uninitializedFailure)
        );
        var sut = CreateSut(sessionFactory);

        var result = await sut.HandleDeleteAsync(
            CreateDeleteRequest(
                namespacePrefixes: ["uri://ed-fi.org/"],
                authorizationStrategy: NamespaceStrategy()
            )
        );

        result
            .As<DeleteResult.DeleteFailureNamespaceNotAuthorized>()
            .NamespaceFailure.FailureKind.Should()
            .Be(NamespaceAuthorizationFailureKind.StoredNamespaceUninitialized);
        sessionFactory
            .Session.Executor.Commands.Should()
            .NotContain(command =>
                command.CommandText.Contains("DELETE FROM dms.\"Document\"", StringComparison.Ordinal)
            );
    }

    [Test]
    public async Task It_deletes_when_the_stored_namespace_is_authorized()
    {
        var sessionFactory = new RecordingNamespaceWriteSessionFactory(SqlDialect.Pgsql);
        sessionFactory.Session.Executor.ResultSets.Enqueue([CreateResolvedExistingDocumentRow()]);
        sessionFactory.Session.ScalarResults.Enqueue(44L);
        sessionFactory.Session.Executor.NamespaceResults.Enqueue(
            new NamespaceAuthorizationExecutionResult.Authorized()
        );
        sessionFactory.Session.Executor.ResultSets.Enqueue([
            InMemoryRelationalResultSet.Create(),
            InMemoryRelationalResultSet.Create(new Dictionary<string, object?> { ["DocumentId"] = 345L }),
        ]);
        var sut = CreateSut(sessionFactory);

        var result = await sut.HandleDeleteAsync(
            CreateDeleteRequest(
                namespacePrefixes: ["uri://ed-fi.org/"],
                authorizationStrategy: NamespaceStrategy()
            )
        );

        result.Should().BeOfType<DeleteResult.DeleteSuccess>();
        var deleteCommand = sessionFactory
            .Session.Executor.Commands.Should()
            .ContainSingle(command =>
                command.CommandText.Contains("DELETE FROM dms.\"Document\"", StringComparison.Ordinal)
            )
            .Subject;
        deleteCommand.CommandText.Should().Contain("DELETE FROM dms.\"Descriptor\"");
        deleteCommand
            .CommandText.IndexOf("DELETE FROM dms.\"Descriptor\"", StringComparison.Ordinal)
            .Should()
            .BeLessThan(
                deleteCommand.CommandText.IndexOf("DELETE FROM dms.\"Document\"", StringComparison.Ordinal)
            );
        sessionFactory.Session.CommitCallCount.Should().Be(1);
    }

    [Test]
    public async Task It_returns_namespace_403_not_precondition_failed_when_the_stored_namespace_denies_under_a_stale_if_match()
    {
        var sessionFactory = new RecordingNamespaceWriteSessionFactory(SqlDialect.Pgsql);
        // Resolve target → lock (scalar) → load persisted → namespace check (denied before ETag compare).
        sessionFactory.Session.Executor.ResultSets.Enqueue([CreateResolvedExistingDocumentRow()]);
        sessionFactory.Session.ScalarResults.Enqueue(44L);
        sessionFactory.Session.Executor.ResultSets.Enqueue([CreatePersistedDescriptorRow()]);
        sessionFactory.Session.Executor.NamespaceResults.Enqueue(
            new NamespaceAuthorizationExecutionResult.NotAuthorized(StoredMismatchFailure())
        );
        var sut = CreateSut(sessionFactory);
        var request = CreateDeleteRequest(
            namespacePrefixes: ["uri://ed-fi.org/"],
            authorizationStrategy: NamespaceStrategy()
        ) with
        {
            WritePrecondition = new WritePrecondition.IfMatch("\"stale-etag\""),
        };

        var result = await sut.HandleDeleteAsync(request);

        result.Should().BeOfType<DeleteResult.DeleteFailureNamespaceNotAuthorized>();
        sessionFactory
            .Session.Executor.Commands.Should()
            .NotContain(command =>
                command.CommandText.Contains("DELETE FROM dms.\"Document\"", StringComparison.Ordinal)
            );
        sessionFactory.Session.CommitCallCount.Should().Be(0);
    }

    [Test]
    public async Task It_maps_invalid_namespace_authorization_metadata_to_a_security_configuration_failure()
    {
        var sessionFactory = new RecordingNamespaceWriteSessionFactory(SqlDialect.Pgsql);
        sessionFactory.Session.Executor.ResultSets.Enqueue([CreateResolvedExistingDocumentRow()]);
        sessionFactory.Session.ScalarResults.Enqueue(44L);
        sessionFactory.Session.Executor.NamespaceResults.Enqueue(
            new NamespaceAuthorizationExecutionResult.InvalidAuthorizationFailure(
                "Namespace authorization failed, but the AUTH1 failure metadata could not be mapped.",
                [
                    new SecurityConfigurationFailureDiagnostic(
                        ProviderOrPlannerFailureKind: AuthorizationSecurityConfigurationDiagnostics.NamespaceAuth1PayloadMappingFailed
                    ),
                ]
            )
        );
        var sut = CreateSut(sessionFactory);

        var result = await sut.HandleDeleteAsync(
            CreateDeleteRequest(
                namespacePrefixes: ["uri://ed-fi.org/"],
                authorizationStrategy: NamespaceStrategy()
            )
        );

        result
            .Should()
            .BeOfType<DeleteResult.DeleteFailureSecurityConfiguration>()
            .Which.Diagnostics.Should()
            .ContainSingle()
            .Which.ProviderOrPlannerFailureKind.Should()
            .Be(AuthorizationSecurityConfigurationDiagnostics.NamespaceAuth1PayloadMappingFailed);
    }

    [Test]
    public async Task It_returns_not_exists_when_the_descriptor_delete_target_is_unlocked_to_a_concurrent_delete()
    {
        var sessionFactory = new RecordingNamespaceWriteSessionFactory(SqlDialect.Pgsql);
        // No-IfMatch DELETE: resolve target succeeds, but the FOR UPDATE lock returns no row
        // because a concurrent committed delete removed the document between resolve and lock.
        sessionFactory.Session.Executor.ResultSets.Enqueue([CreateResolvedExistingDocumentRow()]);
        sessionFactory.Session.ScalarResults.Enqueue(null);
        var sut = CreateSut(sessionFactory);

        var result = await sut.HandleDeleteAsync(
            CreateDeleteRequest(
                namespacePrefixes: ["uri://ed-fi.org/"],
                authorizationStrategy: NamespaceStrategy()
            )
        );

        result.Should().BeOfType<DeleteResult.DeleteFailureNotExists>();
        sessionFactory
            .Session.Executor.Commands.Should()
            .NotContain(command =>
                command.CommandText.Contains("DELETE FROM dms.\"Document\"", StringComparison.Ordinal)
            );
        sessionFactory
            .Session.ScalarCommands.Should()
            .ContainSingle("the lock probe must run between resolve and namespace authorization");
        sessionFactory.Session.CommitCallCount.Should().Be(0);
    }

    [Test]
    public void It_carries_authorization_strategy_evaluators_and_a_relational_authorization_context_on_the_delete_request()
    {
        var evaluators = new[] { NamespaceStrategy() };
        var context = new RelationalAuthorizationContext([], ["uri://ed-fi.org/"]);

        var request = new DescriptorDeleteRequest(
            CreateMappingSet(SqlDialect.Pgsql),
            _descriptorResource,
            _documentUuid,
            new TraceId("descriptor-delete-contract"),
            evaluators,
            context
        );

        request.AuthorizationStrategyEvaluators.Should().BeSameAs(evaluators);
        request.RelationalAuthorizationContext.NamespacePrefixes.Should().ContainSingle();
        request.RelationalAuthorizationContext.NamespacePrefixes[0].Should().Be("uri://ed-fi.org/");
    }

    [Test]
    public void It_defaults_the_delete_request_relational_authorization_context_to_empty_prefixes()
    {
        var request = new DescriptorDeleteRequest(
            CreateMappingSet(SqlDialect.Pgsql),
            _descriptorResource,
            _documentUuid,
            new TraceId("descriptor-delete-contract-default")
        );

        request.AuthorizationStrategyEvaluators.Should().BeEmpty();
        request.RelationalAuthorizationContext.NamespacePrefixes.Should().BeEmpty();
    }

    [Test]
    public void It_carries_authorization_strategy_evaluators_and_a_relational_authorization_context_on_the_write_request()
    {
        var evaluators = new[] { NamespaceStrategy() };
        var context = new RelationalAuthorizationContext([], ["uri://ed-fi.org/"]);

        var request = new DescriptorWriteRequest(
            CreateMappingSet(SqlDialect.Pgsql),
            _descriptorResource,
            CreateDescriptorRequestBody("uri://ed-fi.org/SchoolTypeDescriptor", "Charter"),
            _documentUuid,
            new ReferentialId(Guid.Parse("11111111-2222-3333-4444-555555555555")),
            new TraceId("descriptor-write-contract"),
            evaluators,
            context
        );

        request.AuthorizationStrategyEvaluators.Should().BeSameAs(evaluators);
        request.RelationalAuthorizationContext.NamespacePrefixes.Should().ContainSingle();
        request.RelationalAuthorizationContext.NamespacePrefixes[0].Should().Be("uri://ed-fi.org/");
    }

    [Test]
    public void It_defaults_the_write_request_relational_authorization_context_to_empty_prefixes()
    {
        var request = new DescriptorWriteRequest(
            CreateMappingSet(SqlDialect.Pgsql),
            _descriptorResource,
            CreateDescriptorRequestBody("uri://ed-fi.org/SchoolTypeDescriptor", "Charter"),
            _documentUuid,
            new ReferentialId(Guid.Parse("11111111-2222-3333-4444-555555555555")),
            new TraceId("descriptor-write-contract-default")
        );

        request.AuthorizationStrategyEvaluators.Should().BeEmpty();
        request.RelationalAuthorizationContext.NamespacePrefixes.Should().BeEmpty();
    }

    private static System.Text.Json.Nodes.JsonNode CreateDescriptorRequestBody(
        string @namespace,
        string codeValue
    )
    {
        return System.Text.Json.Nodes.JsonNode.Parse(
            $$"""
            {
                "namespace": "{{@namespace}}",
                "codeValue": "{{codeValue}}",
                "shortDescription": "{{codeValue}}"
            }
            """
        )!;
    }

    private static AuthorizationStrategyEvaluator NamespaceStrategy() =>
        new(AuthorizationStrategyNameConstants.NamespaceBased, [], FilterOperator.Or);

    private static AuthorizationStrategyEvaluator UnsupportedStrategy(string name) =>
        new(name, [], FilterOperator.And);

    private static DescriptorDeleteRequest CreateDeleteRequest(
        IReadOnlyList<string> namespacePrefixes,
        AuthorizationStrategyEvaluator authorizationStrategy
    ) =>
        new(
            CreateMappingSet(SqlDialect.Pgsql),
            _descriptorResource,
            _documentUuid,
            new TraceId("descriptor-delete-namespace"),
            [authorizationStrategy],
            new RelationalAuthorizationContext([], namespacePrefixes)
        );

    private static DescriptorWriteRequest CreatePostRequest(
        IReadOnlyList<string> namespacePrefixes,
        AuthorizationStrategyEvaluator[] authorizationStrategies,
        string @namespace = "uri://ed-fi.org/SchoolTypeDescriptor",
        string codeValue = "Charter",
        SqlDialect dialect = SqlDialect.Pgsql,
        WritePrecondition? writePrecondition = null,
        MappingSet? mappingSet = null
    ) =>
        new(
            mappingSet ?? CreateMappingSet(dialect),
            _descriptorResource,
            CreateDescriptorRequestBody(@namespace, codeValue),
            _documentUuid,
            new ReferentialId(Guid.Parse("11111111-2222-3333-4444-555555555555")),
            new TraceId("descriptor-post-namespace"),
            authorizationStrategies,
            new RelationalAuthorizationContext([], namespacePrefixes)
        )
        {
            WritePrecondition = writePrecondition ?? new WritePrecondition.None(),
        };

    private static DescriptorWriteRequest CreatePostRequest(
        IReadOnlyList<string> namespacePrefixes,
        AuthorizationStrategyEvaluator authorizationStrategy,
        string @namespace = "uri://ed-fi.org/SchoolTypeDescriptor",
        string codeValue = "Charter",
        SqlDialect dialect = SqlDialect.Pgsql
    ) =>
        new(
            CreateMappingSet(dialect),
            _descriptorResource,
            CreateDescriptorRequestBody(@namespace, codeValue),
            _documentUuid,
            new ReferentialId(Guid.Parse("11111111-2222-3333-4444-555555555555")),
            new TraceId("descriptor-post-namespace"),
            [authorizationStrategy],
            new RelationalAuthorizationContext([], namespacePrefixes)
        );

    private static DescriptorWriteRequest CreatePutRequest(
        IReadOnlyList<string> namespacePrefixes,
        AuthorizationStrategyEvaluator[] authorizationStrategies,
        string @namespace = "uri://ed-fi.org/SchoolTypeDescriptor",
        string codeValue = "Charter",
        SqlDialect dialect = SqlDialect.Pgsql
    ) =>
        new(
            CreateMappingSet(dialect),
            _descriptorResource,
            CreateDescriptorRequestBody(@namespace, codeValue),
            _documentUuid,
            referentialId: null,
            new TraceId("descriptor-put-namespace"),
            authorizationStrategies,
            new RelationalAuthorizationContext([], namespacePrefixes)
        );

    private static DescriptorWriteRequest CreatePutRequest(
        IReadOnlyList<string> namespacePrefixes,
        AuthorizationStrategyEvaluator authorizationStrategy,
        string @namespace = "uri://ed-fi.org/SchoolTypeDescriptor",
        string codeValue = "Charter",
        SqlDialect dialect = SqlDialect.Pgsql
    ) =>
        new(
            CreateMappingSet(dialect),
            _descriptorResource,
            CreateDescriptorRequestBody(@namespace, codeValue),
            _documentUuid,
            referentialId: null,
            new TraceId("descriptor-put-namespace"),
            [authorizationStrategy],
            new RelationalAuthorizationContext([], namespacePrefixes)
        );

    private static DescriptorWriteHandler CreateSut(
        RecordingNamespaceWriteSessionFactory sessionFactory,
        IRelationalWriteTargetLookupService? targetLookupService = null,
        IRelationshipAuthorizationProviderFailureExtractor? providerFailureExtractor = null,
        IRelationalCommandExecutor? customViewValidationCommandExecutor = null
    ) =>
        new(
            targetLookupService ?? A.Fake<IRelationalWriteTargetLookupService>(),
            new NoOpRelationalWriteExceptionClassifier(),
            A.Fake<IRelationalDeleteConstraintResolver>(),
            sessionFactory,
            NullLogger<DescriptorWriteHandler>.Instance,
            new ServedEtagComposer(),
            providerFailureExtractor,
            customViewValidationCommandExecutor: customViewValidationCommandExecutor
        );

    private const string DeleteCustomViewStrategyName = "SchoolTypeDescriptorWithATag";

    private static AuthorizationStrategyEvaluator DeleteCustomViewStrategy() =>
        new(DeleteCustomViewStrategyName, [], FilterOperator.And);

    private static AuthorizationStrategyEvaluator CustomViewStrategy(string strategyName) =>
        new(strategyName, [], FilterOperator.And);

    private static DescriptorDeleteRequest CreateDeleteRequest(
        IReadOnlyList<string> namespacePrefixes,
        AuthorizationStrategyEvaluator[] authorizationStrategies
    ) =>
        new(
            CreateMappingSet(SqlDialect.Pgsql),
            _descriptorResource,
            _documentUuid,
            new TraceId("descriptor-delete-namespace"),
            authorizationStrategies,
            new RelationalAuthorizationContext([], namespacePrefixes)
        );

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

    private static InMemoryRelationalResultSet CreateResolvedExistingDocumentRow() =>
        CreateResolvedExistingDocumentRowWithId(345L);

    private static InMemoryRelationalResultSet CreateResolvedExistingDocumentRowWithId(long documentId) =>
        InMemoryRelationalResultSet.Create(
            new Dictionary<string, object?>
            {
                ["DocumentId"] = documentId,
                ["DocumentUuid"] = _documentUuid.Value,
                ["ResourceKeyId"] = 1,
                ["ContentVersion"] = 44L,
                ["ContentLastModifiedAt"] = new DateTimeOffset(2026, 4, 11, 12, 30, 45, TimeSpan.Zero),
            }
        );

    private static InMemoryRelationalResultSet CreateContentVersionRow(long contentVersion = 44L) =>
        InMemoryRelationalResultSet.Create(
            new Dictionary<string, object?>
            {
                ["ContentVersion"] = contentVersion,
                ["DocumentCacheEnqueueOutcome"] = (int)DocumentCacheEnqueueOutcome.AlreadySatisfied,
            }
        );

    private static InMemoryRelationalResultSet CreatePersistedDescriptorRow() =>
        InMemoryRelationalResultSet.Create(
            new Dictionary<string, object?>
            {
                ["Namespace"] = "uri://other.org/SchoolTypeDescriptor",
                ["CodeValue"] = "Charter",
                ["Uri"] = "uri://other.org/SchoolTypeDescriptor#Charter",
                ["ShortDescription"] = "Charter",
                ["Description"] = "Charter",
                ["EffectiveBeginDate"] = new DateOnly(2024, 1, 1),
                ["EffectiveEndDate"] = null,
            }
        );

    private static InMemoryRelationalResultSet CreatePersistedDescriptorRowWithEdFiNamespace() =>
        InMemoryRelationalResultSet.Create(
            new Dictionary<string, object?>
            {
                ["Namespace"] = "uri://ed-fi.org/SchoolTypeDescriptor",
                ["CodeValue"] = "Charter",
                ["Uri"] = "uri://ed-fi.org/SchoolTypeDescriptor#Charter",
                ["ShortDescription"] = "Charter",
                ["Description"] = "Original Description",
                ["EffectiveBeginDate"] = new DateOnly(2024, 1, 1),
                ["EffectiveEndDate"] = null,
            }
        );

    /// <summary>
    /// A descriptor mapping set whose root carries a document reference to <c>Ed-Fi.Student</c>. No shipped
    /// ApiSchema produces this shape — see the ApiSchema pinning test — but nothing in the model builder
    /// prevents it, so the descriptor write path's fail-closed branch needs a way to be reached.
    /// </summary>
    private static MappingSet CreateMappingSetWithStudentReference(SqlDialect dialect) =>
        CreateMappingSet(dialect, withStudentReference: true);

    private static MappingSet CreateMappingSet(SqlDialect dialect, bool withStudentReference = false)
    {
        var resourceKey = new ResourceKeyEntry(1, _descriptorResource, "1.0.0", true);
        var studentResource = new QualifiedResourceName("Ed-Fi", "Student");
        var studentResourceKey = new ResourceKeyEntry(2, studentResource, "1.0.0", true);
        var descriptorSchema = new DbSchemaName("dms");
        var rootTable = new DbTableModel(
            new DbTableName(descriptorSchema, "Descriptor"),
            new JsonPathExpression("$", []),
            new TableKey(
                "PK_Descriptor",
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
                new DbColumnModel(
                    new DbColumnName("Namespace"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 306),
                    false,
                    new JsonPathExpression("$.namespace", []),
                    null,
                    new ColumnStorage.Stored()
                ),
                .. withStudentReference
                    ?
                    [
                        new DbColumnModel(
                            new DbColumnName("StudentDocumentId"),
                            ColumnKind.Scalar,
                            new RelationalScalarType(ScalarKind.Int64),
                            true,
                            null,
                            null,
                            new ColumnStorage.Stored()
                        ),
                    ]
                    : (DbColumnModel[])[],
            ],
            []
        );
        var resourceModel = new RelationalResourceModel(
            Resource: resourceKey.Resource,
            PhysicalSchema: descriptorSchema,
            StorageKind: ResourceStorageKind.SharedDescriptorTable,
            Root: rootTable,
            TablesInDependencyOrder: [rootTable],
            DocumentReferenceBindings: withStudentReference
                ?
                [
                    new DocumentReferenceBinding(
                        IsIdentityComponent: false,
                        ReferenceObjectPath: new JsonPathExpression("$.studentReference", []),
                        Table: rootTable.Table,
                        FkColumn: new DbColumnName("StudentDocumentId"),
                        TargetResource: studentResource,
                        IdentityBindings:
                        [
                            new ReferenceIdentityBinding(
                                IdentityJsonPath: new JsonPathExpression("$.studentUniqueId", []),
                                ReferenceJsonPath: new JsonPathExpression(
                                    "$.studentReference.studentUniqueId",
                                    []
                                ),
                                Column: new DbColumnName("StudentDocumentId")
                            ),
                        ]
                    ),
                ]
                : [],
            DescriptorEdgeSources: []
        );
        var studentRootTable = new DbTableModel(
            new DbTableName(new DbSchemaName("edfi"), "Student"),
            new JsonPathExpression("$", []),
            new TableKey(
                "PK_Student",
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
        );
        var studentResourceModel = new RelationalResourceModel(
            Resource: studentResource,
            PhysicalSchema: new DbSchemaName("edfi"),
            StorageKind: ResourceStorageKind.RelationalTables,
            Root: studentRootTable,
            TablesInDependencyOrder: [studentRootTable],
            DocumentReferenceBindings: [],
            DescriptorEdgeSources: []
        );
        var descriptorMetadata = new DescriptorMetadata(
            new DescriptorColumnContract(
                Namespace: new DbColumnName("Namespace"),
                CodeValue: new DbColumnName("CodeValue"),
                ShortDescription: new DbColumnName("ShortDescription"),
                Description: new DbColumnName("Description"),
                EffectiveBeginDate: new DbColumnName("EffectiveBeginDate"),
                EffectiveEndDate: new DbColumnName("EffectiveEndDate"),
                Discriminator: null
            ),
            DiscriminatorStrategy.ResourceKeyId
        );

        return new MappingSet(
            Key: new MappingSetKey("schema-hash", dialect, "v1"),
            Model: new DerivedRelationalModelSet(
                EffectiveSchema: new EffectiveSchemaInfo(
                    ApiSchemaFormatVersion: "1.0",
                    RelationalMappingVersion: "v1",
                    EffectiveSchemaHash: "schema-hash",
                    ResourceKeyCount: (short)(withStudentReference ? 2 : 1),
                    ResourceKeySeedHash: [1, 2, 3],
                    SchemaComponentsInEndpointOrder:
                    [
                        new SchemaComponentInfo("ed-fi", "Ed-Fi", "1.0.0", false, "component-hash"),
                    ],
                    ResourceKeysInIdOrder: withStudentReference
                        ? [resourceKey, studentResourceKey]
                        : [resourceKey]
                ),
                Dialect: dialect,
                ProjectSchemasInEndpointOrder:
                [
                    new ProjectSchemaInfo("ed-fi", "Ed-Fi", "1.0.0", false, descriptorSchema),
                ],
                ConcreteResourcesInNameOrder:
                [
                    new ConcreteResourceModel(
                        resourceKey,
                        ResourceStorageKind.SharedDescriptorTable,
                        resourceModel,
                        descriptorMetadata
                    ),
                    .. withStudentReference
                        ?
                        [
                            new ConcreteResourceModel(
                                studentResourceKey,
                                ResourceStorageKind.RelationalTables,
                                studentResourceModel
                            ),
                        ]
                        : (ConcreteResourceModel[])[],
                ],
                AbstractIdentityTablesInNameOrder: [],
                AbstractUnionViewsInNameOrder: [],
                IndexesInCreateOrder: [],
                TriggersInCreateOrder: []
            ),
            WritePlansByResource: new Dictionary<QualifiedResourceName, ResourceWritePlan>(),
            ReadPlansByResource: new Dictionary<QualifiedResourceName, ResourceReadPlan>(),
            ResourceKeyIdByResource: withStudentReference
                ? new Dictionary<QualifiedResourceName, short>
                {
                    [resourceKey.Resource] = resourceKey.ResourceKeyId,
                    [studentResource] = studentResourceKey.ResourceKeyId,
                }
                : new Dictionary<QualifiedResourceName, short>
                {
                    [resourceKey.Resource] = resourceKey.ResourceKeyId,
                },
            ResourceKeyById: withStudentReference
                ? new Dictionary<short, ResourceKeyEntry>
                {
                    [resourceKey.ResourceKeyId] = resourceKey,
                    [studentResourceKey.ResourceKeyId] = studentResourceKey,
                }
                : new Dictionary<short, ResourceKeyEntry> { [resourceKey.ResourceKeyId] = resourceKey },
            SecurableElementColumnPathsByResource: new Dictionary<
                QualifiedResourceName,
                IReadOnlyList<ResolvedSecurableElementPath>
            >()
        );
    }

    private sealed class StubDbException(string message) : DbException(message);

    private sealed class StubRelationshipAuthorizationProviderFailureExtractor(
        string? providerErrorCode,
        string providerMessage
    ) : IRelationshipAuthorizationProviderFailureExtractor
    {
        public RelationshipAuthorizationProviderFailure Extract(DbException exception)
        {
            ArgumentNullException.ThrowIfNull(exception);

            return new RelationshipAuthorizationProviderFailure(providerErrorCode, providerMessage);
        }
    }

    private sealed class RecordingNamespaceWriteSessionFactory(SqlDialect dialect)
        : IRelationalWriteSessionFactory
    {
        public int CreateAsyncCallCount { get; private set; }

        public RecordingNamespaceWriteSession Session { get; } = new(dialect);

        public Task<IRelationalWriteSession> CreateAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CreateAsyncCallCount++;
            return Task.FromResult<IRelationalWriteSession>(Session);
        }
    }

    private sealed class RecordingNamespaceWriteSession : IRelationalWriteSession
    {
        private readonly RecordingDbConnection _connection = new(
            new RecordingDbCommand(new DataTable().CreateDataReader())
        );
        private readonly RecordingDbTransaction _transaction;

        public RecordingNamespaceWriteSession(SqlDialect dialect)
        {
            _transaction = new RecordingDbTransaction(_connection, IsolationLevel.ReadCommitted);
            Executor = new RecordingNamespaceCommandExecutor(dialect);
        }

        public System.Data.Common.DbConnection Connection => _connection;

        public System.Data.Common.DbTransaction Transaction => _transaction;

        public RecordingNamespaceCommandExecutor Executor { get; }

        public Queue<object?> ScalarResults { get; } = [];

        public List<RelationalCommand> ScalarCommands { get; } = [];

        public int CommitCallCount { get; private set; }

        public int RollbackCallCount { get; private set; }

        public int DisposeCallCount { get; private set; }

        public System.Data.Common.DbCommand CreateCommand(RelationalCommand command)
        {
            ScalarCommands.Add(command);

            return new RecordingDbCommand(new DataTable().CreateDataReader())
            {
                CommandText = command.CommandText,
                ScalarResult = ScalarResults.Count == 0 ? null : ScalarResults.Dequeue(),
            };
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

    private sealed class RecordingNamespaceCommandExecutor(SqlDialect dialect) : IRelationalCommandExecutor
    {
        public SqlDialect Dialect { get; } = dialect;

        public Queue<IReadOnlyList<InMemoryRelationalResultSet>> ResultSets { get; } = [];

        public Queue<NamespaceAuthorizationExecutionResult> NamespaceResults { get; } = [];

        public List<RelationalCommand> Commands { get; } = [];

        /// <summary>
        /// When set, the namespace authorization command raises this provider exception instead of
        /// returning a canned <see cref="NamespaceAuthorizationExecutionResult"/>. This lets the handler's
        /// real <see cref="NamespaceAuthorizationExecutor"/> run its AUTH1 mapping against the injected
        /// provider failure extractor rather than bypassing it with a pre-built result.
        /// </summary>
        public DbException? NamespaceAuthorizationException { get; set; }

        public async Task<TResult> ExecuteReaderAsync<TResult>(
            RelationalCommand command,
            Func<IRelationalCommandReader, CancellationToken, Task<TResult>> readAsync,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            Commands.Add(command);

            if (typeof(TResult) == typeof(NamespaceAuthorizationExecutionResult))
            {
                if (NamespaceAuthorizationException is not null)
                {
                    throw NamespaceAuthorizationException;
                }

                NamespaceAuthorizationExecutionResult namespaceResult =
                    NamespaceResults.Count == 0
                        ? new NamespaceAuthorizationExecutionResult.Authorized()
                        : NamespaceResults.Dequeue();
                return (TResult)(object)namespaceResult;
            }

            IReadOnlyList<InMemoryRelationalResultSet> resultSets =
                ResultSets.Count == 0 ? [] : ResultSets.Dequeue();

            await using var reader = new InMemoryRelationalCommandReader(resultSets);
            return await readAsync(reader, cancellationToken);
        }
    }
}

/// <summary>
/// Records the custom-view validation probes a descriptor write terminal issues. The probe is parameterless
/// catalog SQL, so the recorded command text is the whole observable effect. Supplying
/// <paramref name="failure"/> makes every probe fail the way a missing or nonconforming view does.
/// </summary>
internal sealed class RecordingCustomViewValidationExecutor(DbException? failure = null)
    : IRelationalCommandExecutor
{
    public SqlDialect Dialect => SqlDialect.Pgsql;

    public List<string> Commands { get; } = [];

    public Task<TResult> ExecuteReaderAsync<TResult>(
        RelationalCommand command,
        Func<IRelationalCommandReader, CancellationToken, Task<TResult>> readAsync,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(command);
        Commands.Add(command.CommandText);

        return failure is null ? Task.FromResult(default(TResult)!) : Task.FromException<TResult>(failure);
    }
}

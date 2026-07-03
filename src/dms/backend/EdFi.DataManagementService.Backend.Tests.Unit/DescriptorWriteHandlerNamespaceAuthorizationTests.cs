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
        IRelationshipAuthorizationProviderFailureExtractor? providerFailureExtractor = null
    ) =>
        new(
            targetLookupService ?? A.Fake<IRelationalWriteTargetLookupService>(),
            new NoOpRelationalWriteExceptionClassifier(),
            A.Fake<IRelationalDeleteConstraintResolver>(),
            sessionFactory,
            NullLogger<DescriptorWriteHandler>.Instance,
            new EtagComposer(),
            providerFailureExtractor
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
            }
        );

    private static InMemoryRelationalResultSet CreateContentVersionRow(long contentVersion = 44L) =>
        InMemoryRelationalResultSet.Create(
            new Dictionary<string, object?> { ["ContentVersion"] = contentVersion }
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

    private static MappingSet CreateMappingSet(SqlDialect dialect)
    {
        var resourceKey = new ResourceKeyEntry(1, _descriptorResource, "1.0.0", true);
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
            ],
            []
        );
        var resourceModel = new RelationalResourceModel(
            Resource: resourceKey.Resource,
            PhysicalSchema: descriptorSchema,
            StorageKind: ResourceStorageKind.SharedDescriptorTable,
            Root: rootTable,
            TablesInDependencyOrder: [rootTable],
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

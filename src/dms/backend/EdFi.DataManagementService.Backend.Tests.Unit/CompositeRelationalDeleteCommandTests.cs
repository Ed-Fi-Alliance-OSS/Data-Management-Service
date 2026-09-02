// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using EdFi.DataManagementService.Backend.Composite;
using EdFi.DataManagementService.Backend.Etag;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Backend.Tests.Unit.Composite;
using EdFi.DataManagementService.Backend.Tests.Unit.TestSupport;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.External.Security;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

/// <summary>
/// Owns the relational DELETE command stream's observable behavior: how many commands it issues, the order
/// its statements are emitted in, and how each decoded or provider outcome maps back to a
/// <see cref="DeleteResult"/>.
/// </summary>
[TestFixture]
[Parallelizable]
public class Given_The_Composite_Relational_Delete_Command
{
    private static readonly DocumentUuid TargetDocumentUuid = new(
        Guid.Parse("cccccccc-1111-2222-3333-dddddddddddd")
    );

    private const long TargetDocumentId = 345L;
    private const long TargetContentVersion = 44L;

    /// <summary>The namespace check's emitted shape, which no other delete statement produces.</summary>
    private const string NamespaceCheckMarker = "SELECT CASE";

    [TestCase(SqlDialect.Pgsql, "RETURNING \"DocumentId\"")]
    [TestCase(SqlDialect.Mssql, "OUTPUT DELETED.[DocumentId]")]
    public async Task It_deletes_the_root_row_then_the_document_row_in_one_command(
        SqlDialect dialect,
        string documentIdProjection
    )
    {
        var session = CreateSession(dialect, DeleteReader(rootDeleteOrdinal: 1, deleted: true));

        var result = await CreateSut().ExecuteAsync(CreateRequest(dialect), session);

        // The root row goes first so the tombstone trigger can still read the DocumentUuid, and the
        // dms.Document delete projects the id it removed, which is how delete success is decided.
        result.Should().BeOfType<DeleteResult.DeleteSuccess>();
        var command = session.Commands.Should().ContainSingle().Subject;
        IndexOfRootDelete(session, dialect)
            .Should()
            .BeLessThan(IndexOfDocumentDelete(session, dialect))
            .And.BePositive();
        command
            .CommandText.IndexOf(documentIdProjection, StringComparison.Ordinal)
            .Should()
            .BeGreaterThan(IndexOfDocumentDelete(session, dialect));

        // Both deletes consume the capture carrier, so neither binds a document id of its own: only the
        // capture's own predicate parameters are sent.
        command
            .Parameters.Should()
            .NotContain(parameter =>
                parameter.Name.Contains("documentId", StringComparison.OrdinalIgnoreCase)
            );
    }

    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public async Task It_returns_not_exists_when_the_capture_observed_no_target(SqlDialect dialect)
    {
        var session = CreateSession(
            dialect,
            DeleteReader(rootDeleteOrdinal: 1, deleted: false, captured: false)
        );

        var result = await CreateSut().ExecuteAsync(CreateRequest(dialect), session);

        result.Should().BeOfType<DeleteResult.DeleteFailureNotExists>();
        session.Commands.Should().ContainSingle();
    }

    [Test]
    public async Task It_returns_precondition_failed_for_a_wildcard_if_match_against_a_missing_target()
    {
        var session = CreateSession(
            SqlDialect.Pgsql,
            DeleteReader(rootDeleteOrdinal: 1, deleted: false, captured: false)
        );
        var request = CreateRequest(SqlDialect.Pgsql) with
        {
            WritePrecondition = new WritePrecondition.IfMatch("*", IsWildcard: true),
        };

        var result = await CreateSut().ExecuteAsync(request, session);

        result
            .Should()
            .BeEquivalentTo(
                new DeleteResult.DeleteFailureETagMisMatch(ETagPreconditionFailureReason.TargetDoesNotExist)
            );
    }

    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public async Task It_authorizes_the_stored_namespace_before_deleting(SqlDialect dialect)
    {
        var session = CreateSession(
            dialect,
            DeleteReader(rootDeleteOrdinal: 2, deleted: true, namespaceCheckCount: 1)
        );
        var request = CreateRequest(dialect) with
        {
            StoredNamespaceAuthorization = CreateStoredNamespaceAuthorization(dialect),
        };

        var result = await CreateSut().ExecuteAsync(request, session);

        result.Should().BeOfType<DeleteResult.DeleteSuccess>();
        session.Commands.Should().ContainSingle();
        session
            .Commands[0]
            .CommandText.IndexOf(NamespaceCheckMarker, StringComparison.Ordinal)
            .Should()
            .BeLessThan(IndexOfRootDelete(session, dialect))
            .And.BePositive();
    }

    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public async Task It_authorizes_the_stored_relationship_after_the_namespace_and_before_deleting(
        SqlDialect dialect
    )
    {
        var session = CreateSession(
            dialect,
            DeleteReader(
                rootDeleteOrdinal: 3,
                deleted: true,
                namespaceCheckCount: 1,
                includeRelationshipRow: true
            )
        );
        var request = CreateRequest(dialect) with
        {
            StoredNamespaceAuthorization = CreateStoredNamespaceAuthorization(dialect),
            StoredRelationshipAuthorization = CreateStoredRelationshipAuthorization(dialect),
        };

        var result = await CreateSut().ExecuteAsync(request, session);

        result.Should().BeOfType<DeleteResult.DeleteSuccess>();
        session.Commands.Should().ContainSingle();
        session
            .Commands[0]
            .CommandText.IndexOf("AuthorizationResult", StringComparison.Ordinal)
            .Should()
            .BeLessThan(IndexOfRootDelete(session, dialect))
            .And.BePositive();
    }

    /// <summary>
    /// One stored custom-view check per configured strategy, indexed across the request the way the planner
    /// assigns them. A self-basis path keeps the fixture independent of any reference model.
    /// </summary>
    private static RelationalCustomViewAuthorization CreateStoredCustomViewAuthorization(
        params (string StrategyName, int RawConfiguredIndex)[] strategies
    ) =>
        new([
            .. strategies.Select(
                (strategy, index) =>
                    new SingleRecordCustomViewAuthorizationCheckSpec(
                        new ConfiguredAuthorizationStrategy(
                            strategy.StrategyName,
                            strategy.RawConfiguredIndex
                        ),
                        index,
                        CustomViewAuthorizationCheckValueSource.Stored,
                        new DbTableName(new DbSchemaName("auth"), strategy.StrategyName),
                        new DbColumnName("DocumentId"),
                        [
                            new ColumnPathStep(
                                new DbTableName(new DbSchemaName("edfi"), "School"),
                                new DbColumnName("DocumentId"),
                                null,
                                null
                            ),
                        ],
                        new CustomViewAuthorizationCheckTarget.Stored(
                            new DbTableName(new DbSchemaName("edfi"), "School"),
                            new DbColumnName("DocumentId")
                        ),
                        new QualifiedResourceName("Ed-Fi", "School"),
                        [$"{strategy.StrategyName}Element"],
                        $"You may need a {strategy.StrategyName} hint."
                    )
            ),
        ]);

    private static string EncodeCustomViewPayload(
        int index,
        CustomViewAuthorizationAuth1FailureKind failureKind =
            CustomViewAuthorizationAuth1FailureKind.NoMatchingCustomViewRow
    ) =>
        CustomViewAuthorizationAuth1FailurePayloadCodec.Encode(
            new CustomViewAuthorizationAuth1FailurePayload(index, failureKind)
        );

    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public async Task It_emits_the_custom_view_checks_before_the_deletes(SqlDialect dialect)
    {
        var session = CreateSession(
            dialect,
            DeleteReader(rootDeleteOrdinal: 2, deleted: true, namespaceCheckCount: 1)
        );
        var request = CreateRequest(dialect) with
        {
            CustomViewAuthorization = CreateStoredCustomViewAuthorization(("SchoolWithATag", 0)),
        };

        var result = await CreateSut().ExecuteAsync(request, session);

        result.Should().BeOfType<DeleteResult.DeleteSuccess>();
        session.Commands.Should().ContainSingle();
        // "the delete does not happen" when unauthorized only holds if the check precedes the deletes in the
        // same command, where the AUTH1 abort stops the batch.
        session
            .Commands[0]
            .CommandText.IndexOf("SchoolWithATag", StringComparison.Ordinal)
            .Should()
            .BeLessThan(IndexOfRootDelete(session, dialect))
            .And.BePositive();
    }

    [Test]
    public async Task It_maps_a_custom_view_auth1_failure_to_the_custom_view_denial()
    {
        var session = CreateSession(SqlDialect.Pgsql, new FakeDbException("AUTH1", "AUTH1"));
        var request = CreateRequest(SqlDialect.Pgsql) with
        {
            CustomViewAuthorization = CreateStoredCustomViewAuthorization(("SchoolWithATag", 0)),
        };

        var result = await CreateSut(new StubProviderFailureExtractor("AUTH1", EncodeCustomViewPayload(0)))
            .ExecuteAsync(request, session);

        var failure = result.Should().BeOfType<DeleteResult.DeleteFailureCustomViewNotAuthorized>().Subject;
        failure.CustomViewFailure.StrategyName.Should().Be("SchoolWithATag");
        failure.CustomViewFailure.ReadableSecurableElements.Should().Equal("SchoolWithATagElement");
        failure.CustomViewFailure.Hint.Should().Be("You may need a SchoolWithATag hint.");
    }

    [Test]
    public async Task It_maps_a_stale_custom_view_target_to_not_exists()
    {
        // Unreachable while the capture lock holds; the same defensive mapping the namespace path uses.
        var session = CreateSession(SqlDialect.Pgsql, new FakeDbException("AUTH1", "AUTH1"));
        var request = CreateRequest(SqlDialect.Pgsql) with
        {
            CustomViewAuthorization = CreateStoredCustomViewAuthorization(("SchoolWithATag", 0)),
        };
        var payload = EncodeCustomViewPayload(0, CustomViewAuthorizationAuth1FailureKind.StoredTargetMissing);

        var result = await CreateSut(new StubProviderFailureExtractor("AUTH1", payload))
            .ExecuteAsync(request, session);

        result.Should().BeOfType<DeleteResult.DeleteFailureNotExists>();
    }

    [Test]
    public async Task It_maps_an_unmappable_custom_view_payload_to_a_security_configuration_failure()
    {
        // Index 4 addresses no planned check, so the payload is a configuration defect rather than a denial.
        var session = CreateSession(SqlDialect.Pgsql, new FakeDbException("AUTH1", "AUTH1"));
        var request = CreateRequest(SqlDialect.Pgsql) with
        {
            CustomViewAuthorization = CreateStoredCustomViewAuthorization(("SchoolWithATag", 0)),
        };

        var result = await CreateSut(new StubProviderFailureExtractor("AUTH1", EncodeCustomViewPayload(4)))
            .ExecuteAsync(request, session);

        result.Should().BeOfType<DeleteResult.DeleteFailureSecurityConfiguration>();
    }

    [Test]
    public async Task It_emits_a_custom_view_configured_before_NamespaceBased_ahead_of_it()
    {
        var session = CreateSession(
            SqlDialect.Pgsql,
            DeleteReader(rootDeleteOrdinal: 3, deleted: true, namespaceCheckCount: 2)
        );
        var request = CreateRequest(SqlDialect.Pgsql) with
        {
            CustomViewAuthorization = CreateStoredCustomViewAuthorization(("SchoolWithATag", 0)),
            StoredNamespaceAuthorization = CreateStoredNamespaceAuthorization(SqlDialect.Pgsql),
        };

        await CreateSut().ExecuteAsync(request, session);

        var commandText = session.Commands[0].CommandText;
        commandText
            .IndexOf("SchoolWithATag", StringComparison.Ordinal)
            .Should()
            .BeLessThan(commandText.IndexOf("namespacePrefixes", StringComparison.Ordinal))
            .And.BePositive();
    }

    [Test]
    public async Task It_runs_a_custom_view_configured_after_NamespaceBased_as_its_own_ordered_segment()
    {
        // The namespace planner stamps its check with configured index 0, so a view at index 1 runs after it.
        // It takes a segment of its own rather than riding the opening command behind the namespace statement:
        // its view may be validated only once that check has passed, and the deletes wait for both.
        var session = CreateSession(
            SqlDialect.Pgsql,
            CaptureOnlyReader(captured: true, namespaceCheckCount: 1),
            NamespaceCheckReader(checkCount: 1),
            SegmentDeleteReader(deleted: true)
        );
        var request = CreateRequest(SqlDialect.Pgsql) with
        {
            CustomViewAuthorization = CreateStoredCustomViewAuthorization(("SchoolWithATag", 1)),
            StoredNamespaceAuthorization = CreateStoredNamespaceAuthorization(SqlDialect.Pgsql),
        };

        var result = await CreateSut().ExecuteAsync(request, session);

        result.Should().BeOfType<DeleteResult.DeleteSuccess>();
        session.Commands.Should().HaveCount(3);
        session
            .Commands[0]
            .CommandText.Should()
            .Contain("namespacePrefixes")
            .And.NotContain("SchoolWithATag")
            .And.NotContain("DELETE FROM");
        session.Commands[1].CommandText.Should().Contain("SchoolWithATag");
        session.Commands[2].CommandText.Should().Contain("DELETE FROM");
    }

    [Test]
    public async Task It_resolves_a_straddling_split_payload_against_the_full_planned_check_list()
    {
        // Two views surround the namespace check, so the later view lands in the second run. Its payload
        // index is non-zero, and the mapper must resolve it against the request's whole planned list rather
        // than against the run that raised it — otherwise the denial would report the earlier view.
        var session = CreateSession(SqlDialect.Pgsql, new FakeDbException("AUTH1", "AUTH1"));
        var request = CreateRequest(SqlDialect.Pgsql) with
        {
            CustomViewAuthorization = CreateStoredCustomViewAuthorization(
                ("SchoolWithAnEarlyTag", 0),
                ("SchoolWithALateTag", 2)
            ),
            StoredNamespaceAuthorization = CreateStoredNamespaceAuthorization(SqlDialect.Pgsql),
        };

        var result = await CreateSut(new StubProviderFailureExtractor("AUTH1", EncodeCustomViewPayload(1)))
            .ExecuteAsync(request, session);

        var failure = result.Should().BeOfType<DeleteResult.DeleteFailureCustomViewNotAuthorized>().Subject;
        failure.CustomViewFailure.StrategyName.Should().Be("SchoolWithALateTag");
        failure.CustomViewFailure.ReadableSecurableElements.Should().Equal("SchoolWithALateTagElement");
        failure.CustomViewFailure.Hint.Should().Be("You may need a SchoolWithALateTag hint.");
        failure.CustomViewFailure.EmittedAuth1Index.Should().Be(1);
    }

    [Test]
    public async Task It_straddles_the_namespace_check_with_both_custom_view_runs_in_configured_order()
    {
        // The view configured before NamespaceBased rides the opening command ahead of it; the one configured
        // after follows as a segment, and the deletes wait for that segment.
        var session = CreateSession(
            SqlDialect.Pgsql,
            CaptureOnlyReader(captured: true, namespaceCheckCount: 2),
            NamespaceCheckReader(checkCount: 1),
            SegmentDeleteReader(deleted: true)
        );
        var request = CreateRequest(SqlDialect.Pgsql) with
        {
            CustomViewAuthorization = CreateStoredCustomViewAuthorization(
                ("SchoolWithAnEarlyTag", 0),
                ("SchoolWithALateTag", 2)
            ),
            StoredNamespaceAuthorization = CreateStoredNamespaceAuthorization(SqlDialect.Pgsql),
        };

        await CreateSut().ExecuteAsync(request, session);

        var openingCommandText = session.Commands[0].CommandText;
        var earlyPosition = openingCommandText.IndexOf("SchoolWithAnEarlyTag", StringComparison.Ordinal);
        var namespacePosition = openingCommandText.IndexOf("namespacePrefixes", StringComparison.Ordinal);

        earlyPosition.Should().BePositive();
        earlyPosition.Should().BeLessThan(namespacePosition);
        openingCommandText.Should().NotContain("SchoolWithALateTag").And.NotContain("DELETE FROM");
        session.Commands[1].CommandText.Should().Contain("SchoolWithALateTag");
        session.Commands[2].CommandText.Should().Contain("DELETE FROM");
        // Each run keeps its request-wide indexes, so the two runs' payloads stay distinguishable.
        openingCommandText.Should().Contain("cv1|0|n");
        session.Commands[1].CommandText.Should().Contain("cv1|1|n");
    }

    [Test]
    public async Task It_runs_a_custom_view_configured_after_a_segmented_namespace_check_as_its_own_segment()
    {
        // The regression this pins: when the namespace check cannot fit the opening command it runs as a
        // later segment, so the custom views configured after it cannot ride the opening command either. If
        // they were simply dropped, the row would be deleted without a configured view ever being enforced.
        var session = CreateSession(
            SqlDialect.Pgsql,
            CaptureOnlyReader(captured: true),
            NamespaceCheckReader(checkCount: 1),
            NamespaceCheckReader(checkCount: 1),
            SegmentDeleteReader(deleted: true)
        );
        var request = CreateRequest(SqlDialect.Pgsql) with
        {
            StoredNamespaceAuthorization = CreateStoredNamespaceAuthorization(SqlDialect.Pgsql),
            CustomViewAuthorization = CreateStoredCustomViewAuthorization(("SchoolWithALateTag", 1)),
        };

        // A budget the capture's own two parameters consume, so the namespace statement cannot join it.
        var result = await CreateSut(commandBudget: new RelationalCommandBudget(2, 1000))
            .ExecuteAsync(request, session);

        result.Should().BeOfType<DeleteResult.DeleteSuccess>();
        session.Commands.Should().HaveCount(4);
        session
            .Commands[0]
            .CommandText.Should()
            .NotContain("SchoolWithALateTag")
            .And.NotContain("DELETE FROM");
        session.Commands[1].CommandText.Should().Contain("namespacePrefixes");
        session.Commands[2].CommandText.Should().Contain("SchoolWithALateTag");
        session.Commands[3].CommandText.Should().Contain("DELETE FROM");
    }

    [Test]
    public async Task It_denies_from_a_segmented_custom_view_run_before_deleting()
    {
        var session = CreateSession(
            SqlDialect.Pgsql,
            CaptureOnlyReader(captured: true),
            NamespaceCheckReader(checkCount: 1),
            new FakeDbException("AUTH1", "AUTH1")
        );
        var request = CreateRequest(SqlDialect.Pgsql) with
        {
            StoredNamespaceAuthorization = CreateStoredNamespaceAuthorization(SqlDialect.Pgsql),
            CustomViewAuthorization = CreateStoredCustomViewAuthorization(("SchoolWithALateTag", 1)),
        };

        var result = await CreateSut(
                new StubProviderFailureExtractor("AUTH1", EncodeCustomViewPayload(0)),
                commandBudget: new RelationalCommandBudget(2, 1000)
            )
            .ExecuteAsync(request, session);

        result
            .Should()
            .BeOfType<DeleteResult.DeleteFailureCustomViewNotAuthorized>()
            .Subject.CustomViewFailure.StrategyName.Should()
            .Be("SchoolWithALateTag");
        session.Commands.Should().HaveCount(3);
        session
            .Commands.Should()
            .AllSatisfy(command => command.CommandText.Should().NotContain("DELETE FROM"));
    }

    [Test]
    public async Task It_authorizes_the_relationship_after_an_owed_custom_view_segment_rather_than_in_the_opening_command()
    {
        // The regression this pins: relationship authorization runs after every configured AND filter, so
        // while a custom-view segment is owed the relationship check cannot ride the opening command. Emitted
        // there, its denial would answer before the segment ever ran, and a view the CMS configured would
        // never be enforced on this delete.
        var session = CreateSession(
            SqlDialect.Pgsql,
            CaptureOnlyReader(captured: true, namespaceCheckCount: 1),
            NamespaceCheckReader(checkCount: 1),
            new FakeDbException("AUTH1", "AUTH1")
        );
        var request = CreateRequest(SqlDialect.Pgsql) with
        {
            StoredNamespaceAuthorization = CreateStoredNamespaceAuthorization(SqlDialect.Pgsql),
            CustomViewAuthorization = CreateStoredCustomViewAuthorization(("SchoolWithALateTag", 1)),
            StoredRelationshipAuthorization = CreateStoredRelationshipAuthorization(SqlDialect.Pgsql),
        };
        var payload = RelationshipAuthorizationAuth1FailurePayloadCodec.Encode(
            new RelationshipAuthorizationAuth1FailurePayload(
                CompositeRelationalDeleteCommand.RelationshipAuthorizationAuth1Index,
                [
                    new RelationshipAuthorizationAuth1SubjectFailure(
                        0,
                        0,
                        RelationshipAuthorizationAuth1SubjectFailureKind.NoRelationship
                    ),
                ]
            )
        );

        var result = await CreateSut(new StubProviderFailureExtractor("AUTH1", payload))
            .ExecuteAsync(request, session);

        result.Should().BeOfType<DeleteResult.DeleteFailureRelationshipNotAuthorized>();
        session.Commands.Should().HaveCount(3);
        session
            .Commands[0]
            .CommandText.Should()
            .Contain("namespacePrefixes")
            .And.NotContain("SchoolWithALateTag")
            .And.NotContain("AuthorizationResult");
        session.Commands[1].CommandText.Should().Contain("SchoolWithALateTag");
        session.Commands[2].CommandText.Should().Contain("AuthorizationResult");
        session
            .Commands.Should()
            .AllSatisfy(command => command.CommandText.Should().NotContain("DELETE FROM"));
    }

    [Test]
    public async Task It_reports_an_owed_custom_view_segment_denial_over_a_relationship_that_would_also_deny()
    {
        // Configured order decides: the view precedes the relationship group, so its denial is the answer and
        // the relationship check is never issued at all.
        var session = CreateSession(
            SqlDialect.Pgsql,
            CaptureOnlyReader(captured: true, namespaceCheckCount: 1),
            new FakeDbException("AUTH1", "AUTH1")
        );
        var request = CreateRequest(SqlDialect.Pgsql) with
        {
            StoredNamespaceAuthorization = CreateStoredNamespaceAuthorization(SqlDialect.Pgsql),
            CustomViewAuthorization = CreateStoredCustomViewAuthorization(("SchoolWithALateTag", 1)),
            StoredRelationshipAuthorization = CreateStoredRelationshipAuthorization(SqlDialect.Pgsql),
        };

        var result = await CreateSut(new StubProviderFailureExtractor("AUTH1", EncodeCustomViewPayload(0)))
            .ExecuteAsync(request, session);

        result
            .Should()
            .BeOfType<DeleteResult.DeleteFailureCustomViewNotAuthorized>()
            .Subject.CustomViewFailure.StrategyName.Should()
            .Be("SchoolWithALateTag");
        session.Commands.Should().HaveCount(2);
        // The relationship statement never joined the opening command, so nothing carried it ahead of the
        // segment, and the segment's denial ended the request before it could be issued on its own.
        session
            .Commands.Should()
            .AllSatisfy(command =>
                command.CommandText.Should().NotContain("AuthorizationResult").And.NotContain("DELETE FROM")
            );
        session.Commands[1].CommandText.Should().Contain("SchoolWithALateTag");
    }

    [Test]
    public async Task It_reports_an_invalid_view_configured_before_NamespaceBased_over_the_namespace_denial()
    {
        // The view rides the opening command ahead of the namespace statement, so its contract is settled
        // before that command runs at all: a table masquerading as auth.{StrategyName} answers the membership
        // SQL without raising anything, and the configured order puts its 500 ahead of the namespace denial.
        var session = CreateSession(SqlDialect.Pgsql, new FakeDbException("AUTH1", "AUTH1"));
        var request = CreateRequest(SqlDialect.Pgsql) with
        {
            CustomViewAuthorization = CreateStoredCustomViewAuthorization(("SchoolWithAnEarlyTag", 0)),
            StoredNamespaceAuthorization = CreateStoredNamespaceAuthorization(SqlDialect.Pgsql),
        };
        var validationExecutor = new StubValidationCommandExecutor(
            new FakeDbException("invalid custom authorization view DocumentId contract", "P0001")
        );

        var act = async () =>
            await CreateSut(
                    new StubProviderFailureExtractor("AUTH1", NamespaceDenialPayload()),
                    customViewValidationCommandExecutor: validationExecutor
                )
                .ExecuteAsync(request, session);

        await act.Should().ThrowAsync<CustomViewAuthorizationValidationException>();
        validationExecutor
            .ExecutedCommands.Should()
            .ContainSingle()
            .Subject.CommandText.Should()
            .Contain("SchoolWithAnEarlyTag");
        // The opening command never ran, so nothing was deleted and the namespace denial was never reached.
        session.Commands.Should().BeEmpty();
    }

    [Test]
    public async Task It_reports_the_namespace_denial_over_an_invalid_view_configured_after_NamespaceBased()
    {
        // The mirror case: the view is configured after NamespaceBased, so it runs as a later segment and the
        // namespace denial the opening command raises is the one the caller sees. Validating every planned view
        // up front, or validating the segment's view before the namespace check, would report a 500 instead.
        var session = CreateSession(SqlDialect.Pgsql, new FakeDbException("AUTH1", "AUTH1"));
        var request = CreateRequest(SqlDialect.Pgsql) with
        {
            CustomViewAuthorization = CreateStoredCustomViewAuthorization(("SchoolWithALateTag", 1)),
            StoredNamespaceAuthorization = CreateStoredNamespaceAuthorization(SqlDialect.Pgsql),
        };
        var validationExecutor = new StubValidationCommandExecutor(
            new FakeDbException("invalid custom authorization view DocumentId contract", "P0001")
        );

        var result = await CreateSut(
                new StubProviderFailureExtractor("AUTH1", NamespaceDenialPayload()),
                customViewValidationCommandExecutor: validationExecutor
            )
            .ExecuteAsync(request, session);

        result.Should().BeOfType<DeleteResult.DeleteFailureNamespaceNotAuthorized>();
        validationExecutor.ExecutedCommands.Should().BeEmpty();
    }

    // ----- Ownership --------------------------------------------------------

    /// <summary>
    /// The production call-site order, which the helper-level fixture cannot prove: the ownership statement
    /// lands after the namespace one and before both deletes. Statement order is precedence order because
    /// the command aborts at its first AUTH1, so a denial ahead of the deletes is what keeps the row.
    /// </summary>
    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public async Task It_emits_the_ownership_statement_after_namespace_and_before_both_deletes(
        SqlDialect dialect
    )
    {
        // One result set for the namespace check and one for the ownership check, then the two deletes.
        var session = CreateSession(
            dialect,
            DeleteReader(rootDeleteOrdinal: 3, deleted: true, namespaceCheckCount: 2)
        );
        var request = CreateRequest(dialect) with
        {
            StoredNamespaceAuthorization = CreateStoredNamespaceAuthorization(dialect),
            StoredOwnershipAuthorization = CreateStoredOwnershipAuthorization(dialect),
        };

        var result = await CreateSut().ExecuteAsync(request, session);

        result.Should().BeOfType<DeleteResult.DeleteSuccess>();
        var commandText = session.Commands.Should().ContainSingle().Subject.CommandText;
        // Not NamespaceCheckMarker: the ownership statement is also a SELECT CASE, so that marker matches
        // whichever check came first and the comparison below would hold either way. The prefix parameter is
        // emitted only by the namespace check.
        var namespaceIndex = commandText.IndexOf("namespacePrefixes", StringComparison.Ordinal);
        var ownershipIndex = commandText.IndexOf(OwnershipCheckMarker(dialect), StringComparison.Ordinal);
        namespaceIndex.Should().BePositive();
        ownershipIndex.Should().BePositive();
        namespaceIndex.Should().BeLessThan(ownershipIndex);
        ownershipIndex.Should().BeLessThan(IndexOfRootDelete(session, dialect));
        ownershipIndex.Should().BeLessThan(IndexOfDocumentDelete(session, dialect));
    }

    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public async Task It_denies_a_delete_on_an_ownership_token_mismatch_and_leaves_the_row(SqlDialect dialect)
    {
        var session = CreateSession(dialect, new FakeDbException("AUTH1", "AUTH1"));
        var request = CreateRequest(dialect) with
        {
            StoredOwnershipAuthorization = CreateStoredOwnershipAuthorization(dialect),
        };

        var result = await CreateSut(
                new StubProviderFailureExtractor("AUTH1", OwnershipDenialPayload(dialect))
            )
            .ExecuteAsync(request, session);

        result
            .Should()
            .BeOfType<DeleteResult.DeleteFailureOwnershipNotAuthorized>()
            .Which.OwnershipFailure.FailureKind.Should()
            .Be(OwnershipAuthorizationFailureKind.OwnershipTokenMismatch);
        // The deletes rode the same command, so the abort rolled them back with it: the row survives.
        session.Commands.Should().ContainSingle();
    }

    /// <summary>
    /// An uninitialized stored token is auth.md 2.14 and reaches the caller as its own failure kind, not
    /// collapsed into the 2.13 mismatch the sibling test covers.
    /// </summary>
    [Test]
    public async Task It_denies_a_delete_whose_stored_ownership_token_was_never_assigned()
    {
        var session = CreateSession(SqlDialect.Pgsql, new FakeDbException("AUTH1", "AUTH1"));
        var request = CreateRequest(SqlDialect.Pgsql) with
        {
            StoredOwnershipAuthorization = CreateStoredOwnershipAuthorization(SqlDialect.Pgsql),
        };

        var result = await CreateSut(
                new StubProviderFailureExtractor(
                    "AUTH1",
                    OwnershipDenialPayload(
                        SqlDialect.Pgsql,
                        OwnershipAuthorizationAuth1FailureKind.StoredOwnershipTokenUninitialized
                    )
                )
            )
            .ExecuteAsync(request, session);

        result
            .Should()
            .BeOfType<DeleteResult.DeleteFailureOwnershipNotAuthorized>()
            .Which.OwnershipFailure.FailureKind.Should()
            .Be(OwnershipAuthorizationFailureKind.StoredOwnershipTokenUninitialized);
    }

    /// <summary>
    /// A namespace denial still wins over an ownership one. Both statements ride the command and the command
    /// aborts at the first, so the emitted order is what decides — which is why the ownership statement is
    /// appended after the namespace one rather than at its configured position.
    /// </summary>
    [Test]
    public async Task It_reports_a_namespace_denial_over_an_ownership_check_configured_before_it()
    {
        var session = CreateSession(SqlDialect.Pgsql, new FakeDbException("AUTH1", "AUTH1"));
        var request = CreateRequest(SqlDialect.Pgsql) with
        {
            StoredNamespaceAuthorization = CreateStoredNamespaceAuthorization(SqlDialect.Pgsql),
            // Configured ahead of NamespaceBased, and still executed after it.
            StoredOwnershipAuthorization = CreateStoredOwnershipAuthorization(
                SqlDialect.Pgsql,
                rawConfiguredIndex: 0
            ),
        };

        var result = await CreateSut(new StubProviderFailureExtractor("AUTH1", NamespaceDenialPayload()))
            .ExecuteAsync(request, session);

        result.Should().BeOfType<DeleteResult.DeleteFailureNamespaceNotAuthorized>();
    }

    /// <summary>
    /// When a check configured ahead of ownership owes an ordered segment, ownership must not ride the
    /// opening command either — it executes after that check — and the deletes must be withheld so nothing
    /// is removed before the ownership decision.
    /// </summary>
    [Test]
    public async Task It_withholds_the_deletes_when_ownership_must_run_as_a_segment()
    {
        var session = CreateSession(
            SqlDialect.Pgsql,
            CaptureOnlyReader(captured: true, namespaceCheckCount: 1),
            // The custom-view segment, then the ownership segment, then the withheld deletes.
            NamespaceCheckReader(checkCount: 1),
            NamespaceCheckReader(checkCount: 1),
            SegmentDeleteReader(deleted: true)
        );
        var request = CreateRequest(SqlDialect.Pgsql) with
        {
            StoredNamespaceAuthorization = CreateStoredNamespaceAuthorization(SqlDialect.Pgsql),
            // A view configured after NamespaceBased always takes a segment, which pushes ownership out too.
            CustomViewAuthorization = CreateStoredCustomViewAuthorization(("SchoolWithALateTag", 1)),
            StoredOwnershipAuthorization = CreateStoredOwnershipAuthorization(SqlDialect.Pgsql),
        };

        var result = await CreateSut().ExecuteAsync(request, session);

        result.Should().BeOfType<DeleteResult.DeleteSuccess>();
        session.Commands.Should().HaveCount(4);
        // The opening command carries the namespace check but neither the ownership statement nor the deletes.
        session
            .Commands[0]
            .CommandText.Should()
            .Contain("namespacePrefixes")
            .And.NotContain(OwnershipCheckMarker(SqlDialect.Pgsql))
            .And.NotContain("DELETE FROM");
        session.Commands[1].CommandText.Should().Contain("SchoolWithALateTag");
        // Ownership runs after the custom-view segment and before the deletes.
        session.Commands[2].CommandText.Should().Contain(OwnershipCheckMarker(SqlDialect.Pgsql));
        session.Commands[3].CommandText.Should().Contain("DELETE FROM");
    }

    /// <summary>
    /// The case the sibling test cannot reach: ownership alone does not fit the command's parameter budget,
    /// with no custom-view segment owed. The deletes must still be withheld, or the row would be removed by
    /// the opening command before the ownership check that runs afterwards could deny it.
    /// </summary>
    [Test]
    public async Task It_withholds_the_deletes_when_only_ownership_does_not_fit_the_command()
    {
        var session = CreateSession(
            SqlDialect.Mssql,
            CaptureOnlyReader(captured: true),
            // The ownership segment, then the withheld deletes.
            NamespaceCheckReader(checkCount: 1),
            SegmentDeleteReader(deleted: true)
        );
        var request = CreateRequest(SqlDialect.Mssql) with
        {
            // SQL Server binds one scalar per token, so three tokens cannot fit beside the capture's own
            // parameter in a two-slot budget.
            StoredOwnershipAuthorization = CreateStoredOwnershipAuthorization(
                SqlDialect.Mssql,
                ownershipTokenIds: [3, 5, 7]
            ),
        };

        var result = await CreateSut(
                commandBudget: new RelationalCommandBudget(
                    MaxParametersPerCommand: 2,
                    MaxRowsPerStatement: 1000
                )
            )
            .ExecuteAsync(request, session);

        result.Should().BeOfType<DeleteResult.DeleteSuccess>();
        session.Commands.Should().HaveCount(3);
        session
            .Commands[0]
            .CommandText.Should()
            .NotContain(OwnershipCheckMarker(SqlDialect.Mssql))
            .And.NotContain("DELETE FROM");
        session.Commands[1].CommandText.Should().Contain(OwnershipCheckMarker(SqlDialect.Mssql));
        session.Commands[2].CommandText.Should().Contain("DELETE FROM");
    }

    private static RelationalOwnershipAuthorization CreateStoredOwnershipAuthorization(
        SqlDialect dialect,
        int rawConfiguredIndex = 1,
        IReadOnlyList<short>? ownershipTokenIds = null
    ) =>
        new(
            new OwnershipAuthorizationCheckSpec(rawConfiguredIndex),
            OwnershipTokenParameterizationFactory.Create(
                dialect,
                ownershipTokenIds ?? [11],
                "ownershipTokenIds"
            )
        );

    /// <summary>
    /// The ownership check's emitted shape, which no other delete statement produces: only it reads
    /// CreatedByOwnershipTokenId.
    /// </summary>
    private static string OwnershipCheckMarker(SqlDialect dialect) =>
        dialect is SqlDialect.Pgsql ? "\"CreatedByOwnershipTokenId\"" : "[CreatedByOwnershipTokenId]";

    private static string OwnershipDenialPayload(
        SqlDialect dialect,
        OwnershipAuthorizationAuth1FailureKind failureKind =
            OwnershipAuthorizationAuth1FailureKind.OwnershipTokenMismatch,
        int configuredStrategyIndex = 1
    )
    {
        var payload = OwnershipAuthorizationAuth1FailurePayloadCodec.Encode(
            new OwnershipAuthorizationAuth1FailurePayload(configuredStrategyIndex, failureKind)
        );

        return dialect is SqlDialect.Mssql
            ? $"{OwnershipAuthorizationAuth1FailurePayloadCodec.ProviderFailureCode} - {payload}"
            : payload;
    }

    private static string NamespaceDenialPayload() =>
        NamespaceAuthorizationAuth1FailurePayloadCodec.Encode(
            new NamespaceAuthorizationAuth1FailurePayload(
                0,
                NamespaceAuthorizationAuth1FailureKind.NamespaceMismatch
            )
        );

    [Test]
    public async Task It_attributes_a_non_auth1_failure_in_a_custom_view_statement_to_the_configured_view()
    {
        // A dropped or revoked auth.{StrategyName} raises no AUTH1 payload. auth.md requires that to surface
        // as the urn:ed-fi:api:system 500, which the middleware derives from this exception, rather than as a
        // generic delete failure.
        var session = CreateSession(
            SqlDialect.Pgsql,
            new FakeDbException("relation does not exist", "42P01")
        );
        var request = CreateRequest(SqlDialect.Pgsql) with
        {
            CustomViewAuthorization = CreateStoredCustomViewAuthorization(("SchoolWithATag", 0)),
        };

        var act = async () =>
            await CreateSut(new StubProviderFailureExtractor("42P01", "relation does not exist"))
                .ExecuteAsync(request, session);

        await act.Should().ThrowAsync<CustomViewAuthorizationValidationException>();
    }

    [Test]
    public async Task It_maps_a_transient_failure_in_a_custom_view_command_to_a_write_conflict()
    {
        // A deadlock on the opening command carries no AUTH1 payload either, but it proves nothing about
        // the configured view: it must keep the retryable 409 write-conflict classification instead of
        // being relabelled as a custom-view validation 500.
        var session = CreateSession(SqlDialect.Pgsql, new FakeDbException("deadlock detected", "40P01"));
        var request = CreateRequest(SqlDialect.Pgsql) with
        {
            CustomViewAuthorization = CreateStoredCustomViewAuthorization(("SchoolWithATag", 0)),
        };
        var classifier = new ConfigurableRelationalWriteExceptionClassifier
        {
            IsTransientFailureToReturn = true,
        };

        var result = await CreateSut(
                new StubProviderFailureExtractor("40P01", "deadlock detected"),
                classifier: classifier
            )
            .ExecuteAsync(request, session);

        result.Should().BeOfType<DeleteResult.DeleteFailureWriteConflict>();
    }

    [Test]
    public async Task It_maps_a_namespace_auth1_failure_to_the_namespace_denial()
    {
        var session = CreateSession(SqlDialect.Pgsql, new FakeDbException("AUTH1", "AUTH1"));
        var request = CreateRequest(SqlDialect.Pgsql) with
        {
            StoredNamespaceAuthorization = CreateStoredNamespaceAuthorization(SqlDialect.Pgsql),
        };
        var payload = NamespaceAuthorizationAuth1FailurePayloadCodec.Encode(
            new NamespaceAuthorizationAuth1FailurePayload(
                0,
                NamespaceAuthorizationAuth1FailureKind.NamespaceMismatch
            )
        );

        var result = await CreateSut(new StubProviderFailureExtractor("AUTH1", payload))
            .ExecuteAsync(request, session);

        result.Should().BeOfType<DeleteResult.DeleteFailureNamespaceNotAuthorized>();
    }

    [Test]
    public async Task It_maps_a_relationship_auth1_failure_to_the_relationship_denial()
    {
        var session = CreateSession(SqlDialect.Pgsql, new FakeDbException("AUTH1", "AUTH1"));
        var request = CreateRequest(SqlDialect.Pgsql) with
        {
            StoredRelationshipAuthorization = CreateStoredRelationshipAuthorization(SqlDialect.Pgsql),
        };
        var payload = RelationshipAuthorizationAuth1FailurePayloadCodec.Encode(
            new RelationshipAuthorizationAuth1FailurePayload(
                CompositeRelationalDeleteCommand.RelationshipAuthorizationAuth1Index,
                [
                    new RelationshipAuthorizationAuth1SubjectFailure(
                        0,
                        0,
                        RelationshipAuthorizationAuth1SubjectFailureKind.NoRelationship
                    ),
                ]
            )
        );

        var result = await CreateSut(new StubProviderFailureExtractor("AUTH1", payload))
            .ExecuteAsync(request, session);

        result.Should().BeOfType<DeleteResult.DeleteFailureRelationshipNotAuthorized>();
    }

    [Test]
    public async Task It_withholds_the_deletes_when_the_caller_holds_no_usable_claims()
    {
        var denial = new DeleteResult.DeleteFailureNotExists();
        var session = CreateSession(SqlDialect.Pgsql, DeleteReader(rootDeleteOrdinal: -1, deleted: false));
        var request = CreateRequest(SqlDialect.Pgsql) with
        {
            StoredRelationshipAuthorization = new RelationshipAuthorizationResult.NoClaims([], []),
            DeferredRelationshipDenial = denial,
        };

        var result = await CreateSut().ExecuteAsync(request, session);

        result.Should().BeSameAs(denial);
        session.Commands.Should().ContainSingle();
        IndexOfRootDelete(session, SqlDialect.Pgsql).Should().BeNegative();
    }

    [Test]
    public async Task It_answers_not_found_before_a_no_claims_denial_when_the_target_is_missing()
    {
        var session = CreateSession(
            SqlDialect.Pgsql,
            DeleteReader(rootDeleteOrdinal: -1, deleted: false, captured: false)
        );
        var request = CreateRequest(SqlDialect.Pgsql) with
        {
            StoredRelationshipAuthorization = new RelationshipAuthorizationResult.NoClaims([], []),
            DeferredRelationshipDenial = new DeleteResult.DeleteFailureRelationshipNotAuthorized(
                new RelationshipAuthorizationFailure(
                    RelationshipAuthorizationFailureValueSource.Stored,
                    CompositeRelationalDeleteCommand.RelationshipAuthorizationAuth1Index,
                    [],
                    []
                )
            ),
        };

        var result = await CreateSut().ExecuteAsync(request, session);

        result.Should().BeOfType<DeleteResult.DeleteFailureNotExists>();
    }

    [Test]
    public async Task It_evaluates_a_specific_tag_if_match_between_the_capture_and_the_deletes()
    {
        var session = CreateSession(
            SqlDialect.Pgsql,
            CaptureOnlyReader(captured: true),
            SegmentDeleteReader(deleted: true)
        );
        var request = CreateRequest(SqlDialect.Pgsql) with
        {
            WritePrecondition = new WritePrecondition.IfMatch(CurrentEtag(TargetContentVersion)),
        };

        var result = await CreateSut().ExecuteAsync(request, session);

        result.Should().BeOfType<DeleteResult.DeleteSuccess>();
        session.Commands.Should().HaveCount(2);
        session.Commands[0].CommandText.Should().NotContain("DELETE FROM");
        session.Commands[1].CommandText.Should().Contain("DELETE FROM");
    }

    [Test]
    public async Task It_returns_etag_mismatch_without_issuing_the_deletes_for_a_stale_if_match_tag()
    {
        var session = CreateSession(SqlDialect.Pgsql, CaptureOnlyReader(captured: true));
        var request = CreateRequest(SqlDialect.Pgsql) with
        {
            WritePrecondition = new WritePrecondition.IfMatch(CurrentEtag(TargetContentVersion + 1)),
        };

        var result = await CreateSut().ExecuteAsync(request, session);

        result.Should().BeOfType<DeleteResult.DeleteFailureETagMisMatch>();
        session.Commands.Should().ContainSingle();
    }

    [Test]
    public async Task It_runs_a_structured_claim_relationship_check_as_its_own_segment_before_the_deletes()
    {
        var session = CreateSession(
            SqlDialect.Mssql,
            CaptureOnlyReader(captured: true),
            RelationshipRowReader(),
            SegmentDeleteReader(deleted: true)
        );
        var request = CreateRequest(SqlDialect.Mssql) with
        {
            StoredRelationshipAuthorization = CreateStoredRelationshipAuthorization(
                SqlDialect.Mssql,
                [.. Enumerable.Range(1, 2000).Select(static value => (long)value)]
            ),
        };

        var result = await CreateSut().ExecuteAsync(request, session);

        result.Should().BeOfType<DeleteResult.DeleteSuccess>();
        session.Commands.Should().HaveCount(3);
        session.Commands[1].CommandText.Should().Contain("AuthorizationResult");
        session.Commands[2].CommandText.Should().Contain("DELETE FROM");
    }

    [Test]
    public async Task It_runs_a_relationship_check_that_does_not_fit_the_command_as_its_own_segment()
    {
        var session = CreateSession(
            SqlDialect.Mssql,
            CaptureOnlyReader(captured: true, namespaceCheckCount: 1),
            RelationshipRowReader(),
            SegmentDeleteReader(deleted: true)
        );
        var request = CreateRequest(SqlDialect.Mssql) with
        {
            StoredNamespaceAuthorization = CreateStoredNamespaceAuthorization(
                SqlDialect.Mssql,
                // Valid but large: below the SQL Server prefix cap, and small enough that the namespace
                // check still fits this command on its own.
                [.. Enumerable.Range(0, 1500).Select(static index => $"uri://prefix-{index}/")]
            ),
            StoredRelationshipAuthorization = CreateStoredRelationshipAuthorization(
                SqlDialect.Mssql,
                // Below the structured-parameterization threshold, so these bind as scalars — the shape the
                // composite rewriter can rename. Together with the prefixes they exceed the command budget.
                [.. Enumerable.Range(1, 900).Select(static value => (long)value)]
            ),
        };

        var result = await CreateSut().ExecuteAsync(request, session);

        // The check does not fit, so it takes the ordered segment a table-valued claim list already needs,
        // and the deletes wait for it: authorization is never silently skipped to keep one command.
        result.Should().BeOfType<DeleteResult.DeleteSuccess>();
        session.Commands.Should().HaveCount(3);
        session.Commands[0].CommandText.Should().Contain(NamespaceCheckMarker).And.NotContain("DELETE FROM");
        session.Commands[1].CommandText.Should().Contain("AuthorizationResult");
        session.Commands[2].CommandText.Should().Contain("DELETE FROM");
    }

    [Test]
    public async Task It_runs_a_namespace_check_that_does_not_fit_the_command_as_its_own_segment()
    {
        var session = CreateSession(
            SqlDialect.Pgsql,
            CaptureOnlyReader(captured: true),
            NamespaceCheckReader(checkCount: 1),
            RelationshipRowReader(),
            SegmentDeleteReader(deleted: true)
        );
        var request = CreateRequest(SqlDialect.Pgsql) with
        {
            StoredNamespaceAuthorization = CreateStoredNamespaceAuthorization(SqlDialect.Pgsql),
            StoredRelationshipAuthorization = CreateStoredRelationshipAuthorization(SqlDialect.Pgsql),
        };

        // A budget the capture's own two parameters consume, so neither authorization statement can join it.
        var result = await CreateSut(commandBudget: new RelationalCommandBudget(2, 1000))
            .ExecuteAsync(request, session);

        // The relationship check follows the namespace check onto a segment rather than riding the opening
        // command, because co-batching it there would place it ahead of the denial that outranks it.
        result.Should().BeOfType<DeleteResult.DeleteSuccess>();
        session.Commands.Should().HaveCount(4);
        session.Commands[0].CommandText.Should().NotContain(NamespaceCheckMarker);
        session.Commands[1].CommandText.Should().Contain(NamespaceCheckMarker);
        session.Commands[2].CommandText.Should().Contain("AuthorizationResult");
        session.Commands[3].CommandText.Should().Contain("DELETE FROM");
    }

    [Test]
    public async Task It_authorizes_the_stored_relationship_before_a_specific_tag_if_match_and_the_delete()
    {
        var session = CreateSession(
            SqlDialect.Pgsql,
            CaptureOnlyReader(captured: true, includeRelationshipRow: true),
            SegmentDeleteReader(deleted: true)
        );
        var request = CreateRequest(SqlDialect.Pgsql) with
        {
            StoredRelationshipAuthorization = CreateStoredRelationshipAuthorization(SqlDialect.Pgsql),
            WritePrecondition = new WritePrecondition.IfMatch(CurrentEtag(TargetContentVersion)),
        };

        var result = await CreateSut().ExecuteAsync(request, session);

        // Authorization rides the capture command, the precondition is compared in process against the
        // captured ContentVersion, and only then is the delete sent.
        result.Should().BeOfType<DeleteResult.DeleteSuccess>();
        session.Commands.Should().HaveCount(2);
        session.Commands[0].CommandText.Should().Contain("AuthorizationResult").And.NotContain("DELETE FROM");
        session.Commands[1].CommandText.Should().Contain("DELETE FROM");
    }

    [Test]
    public async Task It_returns_the_relationship_denial_rather_than_a_stale_if_match_mismatch()
    {
        var session = CreateSession(SqlDialect.Pgsql, new FakeDbException("AUTH1", "AUTH1"));
        var request = CreateRequest(SqlDialect.Pgsql) with
        {
            StoredRelationshipAuthorization = CreateStoredRelationshipAuthorization(SqlDialect.Pgsql),
            WritePrecondition = new WritePrecondition.IfMatch(CurrentEtag(TargetContentVersion + 1)),
        };
        var payload = RelationshipAuthorizationAuth1FailurePayloadCodec.Encode(
            new RelationshipAuthorizationAuth1FailurePayload(
                CompositeRelationalDeleteCommand.RelationshipAuthorizationAuth1Index,
                [
                    new RelationshipAuthorizationAuth1SubjectFailure(
                        0,
                        0,
                        RelationshipAuthorizationAuth1SubjectFailureKind.NoRelationship
                    ),
                ]
            )
        );

        var result = await CreateSut(new StubProviderFailureExtractor("AUTH1", payload))
            .ExecuteAsync(request, session);

        // The check statement precedes the precondition compare, so a caller who is not authorized learns
        // that rather than learning its tag was stale, and no delete is ever sent.
        result.Should().BeOfType<DeleteResult.DeleteFailureRelationshipNotAuthorized>();
        session.Commands.Should().ContainSingle();
        session.Commands[0].CommandText.Should().NotContain("DELETE FROM");
    }

    [Test]
    public async Task It_fails_closed_with_security_configuration_when_the_relationship_auth1_payload_is_malformed()
    {
        var session = CreateSession(SqlDialect.Pgsql, new FakeDbException("AUTH1", "AUTH1"));
        var request = CreateRequest(SqlDialect.Pgsql) with
        {
            StoredRelationshipAuthorization = CreateStoredRelationshipAuthorization(SqlDialect.Pgsql),
        };

        var result = await CreateSut(new StubProviderFailureExtractor("AUTH1", "1|0|1|not-a-subject-failure"))
            .ExecuteAsync(request, session);

        // The denial is real but its metadata cannot be decoded, so the request fails closed on the
        // security configuration rather than reporting a denial it cannot describe.
        var failure = result.Should().BeOfType<DeleteResult.DeleteFailureSecurityConfiguration>().Subject;
        failure
            .Errors.Should()
            .Equal(
                RelationshipAuthorizationSecurityConfigurationFailureMessages.InvalidFailurePayloadSecurityConfigurationError
            );
        failure
            .Diagnostics.Should()
            .ContainSingle()
            .Which.ProviderOrPlannerFailureKind.Should()
            .Be("RelationshipAuthorization.Auth1.PayloadParseFailed");
    }

    [Test]
    public async Task It_preserves_mixed_auth_object_and_people_subject_details_in_the_relationship_denial()
    {
        var session = CreateSession(SqlDialect.Pgsql, new FakeDbException("AUTH1", "AUTH1"));
        var request = CreateRequest(SqlDialect.Pgsql) with
        {
            StoredRelationshipAuthorization = CreateMixedSubjectStoredRelationshipAuthorization(
                SqlDialect.Pgsql
            ),
        };
        var payload = RelationshipAuthorizationAuth1FailurePayloadCodec.Encode(
            new RelationshipAuthorizationAuth1FailurePayload(
                CompositeRelationalDeleteCommand.RelationshipAuthorizationAuth1Index,
                [
                    new RelationshipAuthorizationAuth1SubjectFailure(
                        0,
                        0,
                        RelationshipAuthorizationAuth1SubjectFailureKind.NoRelationship
                    ),
                    new RelationshipAuthorizationAuth1SubjectFailure(
                        0,
                        1,
                        RelationshipAuthorizationAuth1SubjectFailureKind.NoRelationship
                    ),
                ]
            )
        );

        var result = await CreateSut(new StubProviderFailureExtractor("AUTH1", payload))
            .ExecuteAsync(request, session);

        // One OR group whose subjects use different auth objects has no single strategy-level auth object,
        // and the person subject's own metadata has to reach the caller for the hint to be actionable.
        var failure = result
            .Should()
            .BeOfType<DeleteResult.DeleteFailureRelationshipNotAuthorized>()
            .Subject.RelationshipFailure;
        var failedStrategy = failure.FailedStrategies.Should().ContainSingle().Which;
        failedStrategy.AuthObject.Should().BeNull();
        failedStrategy
            .FailedSubjects.Select(static subject => subject.AuthObject.Name)
            .Should()
            .Equal(
                "auth.EducationOrganizationIdToEducationOrganizationId",
                "auth.EducationOrganizationIdToStudentDocumentId"
            );
        failedStrategy
            .FailedSubjects.SelectMany(static subject => subject.SecurableElements)
            .Select(static element => element.ReadableName)
            .Should()
            .Equal("SchoolId", "StudentUniqueId");
        failedStrategy.FailedSubjects[0].PersonSubject.Should().BeNull();
        failedStrategy.FailedSubjects[1].PersonSubject!.PersonKind.Should().Be("Student");
    }

    [Test]
    public async Task It_maps_an_inbound_foreign_key_violation_to_a_delete_reference_conflict()
    {
        var session = CreateSession(SqlDialect.Pgsql, new FakeDbException("FK violation", "23503"));
        var classifier = new ConfigurableRelationalWriteExceptionClassifier
        {
            IsForeignKeyViolationToReturn = true,
            ClassificationToReturn = new RelationalWriteExceptionClassification.ForeignKeyConstraintViolation(
                "FK_Calendar_SchoolRef"
            ),
        };
        var constraintResolver = A.Fake<IRelationalDeleteConstraintResolver>();
        A.CallTo(() =>
                constraintResolver.TryResolveReferencingResource(
                    A<DerivedRelationalModelSet>._,
                    "FK_Calendar_SchoolRef"
                )
            )
            .Returns(new QualifiedResourceName("Ed-Fi", "Calendar"));

        var result = await CreateSut(classifier: classifier, constraintResolver: constraintResolver)
            .ExecuteAsync(CreateRequest(SqlDialect.Pgsql), session);

        result
            .Should()
            .BeOfType<DeleteResult.DeleteFailureReference>()
            .Which.ReferencingDocumentResourceNames.Should()
            .Equal("Calendar");
    }

    [Test]
    public async Task It_maps_a_transient_provider_failure_to_a_write_conflict()
    {
        var session = CreateSession(SqlDialect.Pgsql, new FakeDbException("deadlock", "40P01"));
        var classifier = new ConfigurableRelationalWriteExceptionClassifier
        {
            IsTransientFailureToReturn = true,
        };

        var result = await CreateSut(classifier: classifier)
            .ExecuteAsync(CreateRequest(SqlDialect.Pgsql), session);

        result.Should().BeOfType<DeleteResult.DeleteFailureWriteConflict>();
    }

    private static CompositeRelationalDeleteCommand CreateSut(
        IRelationshipAuthorizationProviderFailureExtractor? providerFailureExtractor = null,
        IRelationalWriteExceptionClassifier? classifier = null,
        IRelationalDeleteConstraintResolver? constraintResolver = null,
        RelationalCommandBudget? commandBudget = null,
        IRelationalCommandExecutor? customViewValidationCommandExecutor = null
    ) =>
        new(
            new RelationalCurrentEtagPreconditionChecker(
                A.Fake<IRelationalWriteCurrentStateLoader>(),
                NullLogger<RelationalCurrentEtagPreconditionChecker>.Instance
            ),
            classifier ?? new ConfigurableRelationalWriteExceptionClassifier(),
            constraintResolver ?? A.Fake<IRelationalDeleteConstraintResolver>(),
            relationshipAuthorizationProviderFailureExtractor: providerFailureExtractor,
            commandBudget: commandBudget,
            customViewValidationCommandExecutor: customViewValidationCommandExecutor
        );

    /// <summary>The served etag a client would hold for a document at <paramref name="contentVersion"/>.</summary>
    private static string CurrentEtag(long contentVersion) =>
        EtagComposer.Compose(
            contentVersion,
            VariantKeyFactory.Create(
                "schema-hash",
                ResponseFormat.Json,
                ProfileVariantCode.Of(null),
                linksEnabled: true
            )
        );

    private static ScriptedWriteSession CreateSession(SqlDialect dialect, params object[] scripts) =>
        new(scripts) { Dialect = dialect };

    private static int IndexOfRootDelete(ScriptedWriteSession session, SqlDialect dialect) =>
        session
            .Commands[0]
            .CommandText.IndexOf(
                dialect is SqlDialect.Pgsql
                    ? "DELETE FROM \"edfi\".\"School\""
                    : "DELETE FROM [edfi].[School]",
                StringComparison.Ordinal
            );

    private static int IndexOfDocumentDelete(ScriptedWriteSession session, SqlDialect dialect) =>
        session
            .Commands[0]
            .CommandText.IndexOf(
                dialect is SqlDialect.Pgsql ? "DELETE FROM dms.\"Document\"" : "DELETE FROM [dms].[Document]",
                StringComparison.Ordinal
            );

    private static RelationalDeleteCommandRequest CreateRequest(SqlDialect dialect)
    {
        var mappingSet = CreateMappingSet(dialect);

        return new RelationalDeleteCommandRequest(
            mappingSet,
            mappingSet.Model.ConcreteResourcesInNameOrder[0].RelationalModel.Resource,
            TargetDocumentUuid,
            new TraceId("composite-delete-test"),
            StoredNamespaceAuthorization: null,
            new RelationshipAuthorizationResult.NoAuthorizationRequired([])
        );
    }

    private static MappingSet CreateMappingSet(SqlDialect dialect)
    {
        var rootPlan = Given_Default_Relational_Write_Executor.CreateRootPlan();

        return Given_Default_Relational_Write_Executor.CreateMappingSet(
            Given_Default_Relational_Write_Executor.CreateRelationalResourceModel(rootPlan.TableModel),
            [rootPlan],
            dialect
        );
    }

    private static RelationalWriteNamespaceAuthorization CreateStoredNamespaceAuthorization(
        SqlDialect dialect,
        IReadOnlyList<string>? namespacePrefixes = null
    ) =>
        new(
            [
                new NamespaceAuthorizationCheckSpec(
                    0,
                    NamespaceAuthorizationCheckValueSource.Stored,
                    new DbTableName(new DbSchemaName("edfi"), "School"),
                    new DbColumnName("Name")
                ),
            ],
            NamespacePrefixParameterizationFactory.Create(
                dialect,
                namespacePrefixes ?? ["uri://ed-fi.org/"],
                "namespacePrefixes"
            )
        );

    /// <summary>
    /// One OR group whose two subjects use different auth objects: the root education organization and a
    /// <c>Student</c> person subject, which is the shape whose failure detail a single-subject check cannot
    /// exercise.
    /// </summary>
    private static RelationshipAuthorizationResult.Authorized CreateMixedSubjectStoredRelationshipAuthorization(
        SqlDialect dialect
    )
    {
        var rootTable = new DbTableName(new DbSchemaName("edfi"), "School");
        var resource = new QualifiedResourceName("Ed-Fi", "School");

        return new RelationshipAuthorizationResult.Authorized(
            [
                new RelationshipAuthorizationCheckSpec(
                    new ConfiguredAuthorizationStrategy(
                        AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsAndPeople,
                        RawConfiguredIndex: 0
                    ),
                    RelationshipLocalOrder: 0,
                    RelationshipAuthorizationHierarchyDirection.Normal,
                    RelationshipAuthorizationValueSource.Stored,
                    [
                        new RelationshipAuthorizationSubject(
                            resource,
                            rootTable,
                            new DbColumnName("SchoolId"),
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
                        ),
                        new RelationshipAuthorizationSubject(
                            resource,
                            rootTable,
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
                                new RelationshipAuthorizationPersonSubjectPath(
                                    RelationshipAuthorizationPersonSubjectPathKind.DirectRootColumn,
                                    [new ColumnPathStep(rootTable, AuthNames.StudentDocumentId, null, null)]
                                ),
                                new RelationshipAuthorizationPersonStoredAnchor(
                                    rootTable,
                                    new DbColumnName("DocumentId")
                                ),
                                ProposedAnchor: null
                            )
                        ),
                    ],
                    new RelationshipAuthorizationCheckTarget.Stored(rootTable, new DbColumnName("DocumentId"))
                ),
            ],
            AuthorizationClaimEducationOrganizationIdParameterizationFactory.Create(
                dialect,
                [255901L],
                RelationalAuthorizationParameterNameConstants.ClaimEducationOrganizationIds
            )
        );
    }

    private static RelationshipAuthorizationResult.Authorized CreateStoredRelationshipAuthorization(
        SqlDialect dialect,
        IReadOnlyList<long>? claimEducationOrganizationIds = null
    ) =>
        Given_Default_Relational_Write_Executor.CreateStoredSchoolIdRelationshipAuthorization(
            CreateMappingSet(dialect),
            new QualifiedResourceName("Ed-Fi", "School"),
            Given_Default_Relational_Write_Executor.CreateRootPlan(),
            claimEducationOrganizationIds
        );

    /// <summary>
    /// The opening command's stream when it carries no deletes: the capture row, then one row set per
    /// co-batched namespace check, then the relationship row when one was co-batched.
    /// </summary>
    private static ScriptedDbDataReader CaptureOnlyReader(
        bool captured,
        int namespaceCheckCount = 0,
        bool includeRelationshipRow = false
    )
    {
        List<IReadOnlyList<object?[]>> resultSets =
        [
            captured
                ?
                [
                    [TargetDocumentId, TargetContentVersion, TargetDocumentUuid.Value, "345"],
                ]
                :
                [
                    [null, null, null, ""],
                ],
        ];
        List<string[]> columnNames =
        [
            ["DocumentId", "ContentVersion", "DocumentUuid", "CapturedToken"],
        ];

        for (var check = 0; check < namespaceCheckCount; check++)
        {
            resultSets.Add([
                [1],
            ]);
            columnNames.Add(["Authorized"]);
        }

        if (includeRelationshipRow)
        {
            resultSets.Add([
                [1, TargetContentVersion],
            ]);
            columnNames.Add(["AuthorizationResult", "ContentVersion"]);
        }

        return new ScriptedDbDataReader(resultSets, columnNames);
    }

    /// <summary>The ordered-segment namespace command's stream: one authorizing row set per check.</summary>
    private static ScriptedDbDataReader NamespaceCheckReader(int checkCount) =>
        new(
            [
                .. Enumerable.Repeat<IReadOnlyList<object?[]>>(
                    [
                        [1],
                    ],
                    checkCount
                ),
            ],
            [.. Enumerable.Repeat<string[]>(["Authorized"], checkCount)]
        );

    private static ScriptedDbDataReader RelationshipRowReader() =>
        new(
            [
                [
                    [1, TargetContentVersion],
                ],
            ],
            [
                ["AuthorizationResult", "ContentVersion"],
            ]
        );

    /// <summary>
    /// The ordered-segment delete command's stream: the root delete produces no result set of its own, so
    /// the only rows are the <c>dms.Document</c> delete's returned id.
    /// </summary>
    private static ScriptedDbDataReader SegmentDeleteReader(bool deleted) =>
        new(
            deleted
                ?
                [
                    [
                        [TargetDocumentId],
                    ],
                ]
                :
                [
                    [],
                ],
            [
                ["DocumentId"],
            ]
        );

    /// <summary>
    /// The composite command's declared result-set stream: the capture row, one row set per namespace check,
    /// the relationship row when one is co-batched, the root delete's sentinel echoing its own ordinal, and
    /// the <c>dms.Document</c> delete's returned id.
    /// </summary>
    private static ScriptedDbDataReader DeleteReader(
        int rootDeleteOrdinal,
        bool deleted,
        bool captured = true,
        int namespaceCheckCount = 0,
        bool includeRelationshipRow = false
    )
    {
        var includeDeletes = rootDeleteOrdinal >= 0;
        List<IReadOnlyList<object?[]>> resultSets =
        [
            captured
                ?
                [
                    [TargetDocumentId, TargetContentVersion, TargetDocumentUuid.Value, "345"],
                ]
                :
                [
                    [null, null, null, ""],
                ],
        ];
        List<string[]> columnNames =
        [
            ["DocumentId", "ContentVersion", "DocumentUuid", "CapturedToken"],
        ];

        for (var check = 0; check < namespaceCheckCount; check++)
        {
            resultSets.Add([
                [1],
            ]);
            columnNames.Add(["Authorized"]);
        }

        if (includeRelationshipRow)
        {
            resultSets.Add([
                [1, TargetContentVersion],
            ]);
            columnNames.Add(["AuthorizationResult", "ContentVersion"]);
        }

        if (includeDeletes)
        {
            resultSets.Add([
                [rootDeleteOrdinal],
            ]);
            columnNames.Add(["LogicalStatementOrdinal"]);

            resultSets.Add(
                deleted
                    ?
                    [
                        [TargetDocumentId],
                    ]
                    : []
            );
            columnNames.Add(["DocumentId"]);
        }

        return new ScriptedDbDataReader(resultSets, columnNames);
    }

    private sealed class StubProviderFailureExtractor(string? providerErrorCode, string providerMessage)
        : IRelationshipAuthorizationProviderFailureExtractor
    {
        public RelationshipAuthorizationProviderFailure Extract(DbException exception) =>
            new(providerErrorCode, providerMessage);
    }

    /// <summary>
    /// Stands in for the fresh-connection executor the custom-view validation probe uses, recording what it was
    /// asked to run and optionally failing the way an object that is not a conforming view would.
    /// </summary>
    private sealed class StubValidationCommandExecutor(DbException? failure = null)
        : IRelationalCommandExecutor
    {
        public SqlDialect Dialect => SqlDialect.Pgsql;

        public List<RelationalCommand> ExecutedCommands { get; } = [];

        public Task<TResult> ExecuteReaderAsync<TResult>(
            RelationalCommand command,
            Func<IRelationalCommandReader, CancellationToken, Task<TResult>> readAsync,
            CancellationToken cancellationToken = default
        )
        {
            ExecutedCommands.Add(command);

            return failure is null
                ? Task.FromResult(default(TResult)!)
                : Task.FromException<TResult>(failure);
        }
    }

    private sealed class FakeDbException(string message, string sqlState) : DbException(message)
    {
        public override string SqlState => sqlState;
    }
}

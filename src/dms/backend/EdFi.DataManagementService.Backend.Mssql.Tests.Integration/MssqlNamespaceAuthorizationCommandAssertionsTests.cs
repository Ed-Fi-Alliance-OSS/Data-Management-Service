// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Mssql.Tests.Integration;
using FluentAssertions;
using NUnit.Framework;

// Sibling namespace outside the MSSQL integration setup-fixture scope so these pure-string assertions never
// provision a database; the Integration namespace is imported only for the internal assertion helper, the
// recorded-command record, and the MssqlCiShards.Shard4 category constant.
namespace EdFi.DataManagementService.Backend.Mssql.Tests.CommandAssertions;

/// <summary>
/// Guards the no-mutation boundary itself. <see cref="MssqlNamespaceAuthorizationCommandAssertions"/> is the
/// generic proof that a NamespaceBased denial wrote nothing — including extension, identity, and tracking
/// rows — so a hole in its ordering logic would silently weaken every denial test that depends on it.
/// </summary>
[TestFixture]
[Category(MssqlCiShards.Shard4)]
public class Given_The_Mssql_Namespace_Authorization_Command_Assertions
{
    private const string LockCommand =
        "SELECT [ContentVersion] FROM [dms].[Document] WITH (UPDLOCK, ROWLOCK, HOLDLOCK) WHERE [DocumentId] = @documentId;";

    private const string DocumentInsert =
        "INSERT INTO [dms].[Document] ([DocumentUuid], [ResourceKeyId]) VALUES (@documentUuid, @resourceKeyId);";

    private const string RootUpdate =
        "UPDATE [authz].[AuthorizationNamespaceResource] SET [Name] = @name WHERE [DocumentId] = @documentId;";

    [Test]
    public void It_passes_when_separate_stored_and_proposed_checks_persist_nothing()
    {
        var act = () =>
            MssqlNamespaceAuthorizationCommandAssertions.AssertNoPersistenceAfterNamespaceAuthorization([
                new MssqlRelationalQueryAuthorizationRecordedCommand(1, LockCommand),
                new MssqlRelationalQueryAuthorizationRecordedCommand(1, StoredCheck()),
                new MssqlRelationalQueryAuthorizationRecordedCommand(1, ProposedCheck()),
            ]);

        act.Should().NotThrow();
    }

    [Test]
    public void It_passes_when_the_final_check_is_co_batched_ahead_of_its_persistence()
    {
        // The stored check runs as its own command and the proposed check shares a batch with the insert. The
        // insert follows every namespace marker in that batch, so the AUTH1 raise aborts before persisting.
        var act = () =>
            MssqlNamespaceAuthorizationCommandAssertions.AssertNoPersistenceAfterNamespaceAuthorization([
                new MssqlRelationalQueryAuthorizationRecordedCommand(1, StoredCheck()),
                new MssqlRelationalQueryAuthorizationRecordedCommand(
                    1,
                    ProposedCheck() + "\n" + DocumentInsert
                ),
            ]);

        act.Should().NotThrow();
    }

    [Test]
    public void It_fails_when_persistence_precedes_the_only_authorization_command()
    {
        var act = () =>
            MssqlNamespaceAuthorizationCommandAssertions.AssertNoPersistenceAfterNamespaceAuthorization([
                new MssqlRelationalQueryAuthorizationRecordedCommand(1, DocumentInsert),
                new MssqlRelationalQueryAuthorizationRecordedCommand(1, StoredCheck()),
            ]);

        act.Should().Throw<AssertionException>().WithMessage("*before namespace authorization completes*");
    }

    [Test]
    public void It_fails_when_persistence_falls_between_the_stored_and_proposed_checks()
    {
        var act = () =>
            MssqlNamespaceAuthorizationCommandAssertions.AssertNoPersistenceAfterNamespaceAuthorization([
                new MssqlRelationalQueryAuthorizationRecordedCommand(1, StoredCheck()),
                new MssqlRelationalQueryAuthorizationRecordedCommand(1, DocumentInsert),
                new MssqlRelationalQueryAuthorizationRecordedCommand(1, ProposedCheck()),
            ]);

        act.Should().Throw<AssertionException>().WithMessage("*before namespace authorization completes*");
    }

    [Test]
    public void It_fails_when_co_batched_persistence_precedes_the_last_namespace_marker()
    {
        // Same batch, wrong order: the insert sits between two namespace markers, so a later CASE branch
        // could raise AUTH1 only after the row was already written.
        var act = () =>
            MssqlNamespaceAuthorizationCommandAssertions.AssertNoPersistenceAfterNamespaceAuthorization([
                new MssqlRelationalQueryAuthorizationRecordedCommand(
                    1,
                    "SELECT CASE WHEN EXISTS (...) THEN CAST('AUTH1 - ns1|0|u' AS INT)\n"
                        + DocumentInsert
                        + "\nELSE CAST('AUTH1 - ns1|0|m' AS INT) END;"
                ),
            ]);

        act.Should().Throw<AssertionException>().WithMessage("*co-batched with namespace authorization*");
    }

    [Test]
    public void It_fails_when_persistence_follows_the_authorization_command()
    {
        var act = () =>
            MssqlNamespaceAuthorizationCommandAssertions.AssertNoPersistenceAfterNamespaceAuthorization([
                new MssqlRelationalQueryAuthorizationRecordedCommand(1, StoredCheck()),
                new MssqlRelationalQueryAuthorizationRecordedCommand(1, RootUpdate),
            ]);

        act.Should().Throw<AssertionException>().WithMessage("*must not be followed by any persistence*");
    }

    [Test]
    public void It_fails_when_no_namespace_authorization_command_was_recorded()
    {
        var act = () =>
            MssqlNamespaceAuthorizationCommandAssertions.AssertNoPersistenceAfterNamespaceAuthorization([
                new MssqlRelationalQueryAuthorizationRecordedCommand(1, LockCommand),
            ]);

        act.Should()
            .Throw<AssertionException>()
            .WithMessage("*cannot pass without*", "the boundary must never pass vacuously");
    }

    [Test]
    public void It_ignores_a_relationship_authorization_payload_when_locating_the_boundary()
    {
        // A relationship AUTH1 payload ('1|...') is not a namespace marker, so a command carrying only that
        // payload cannot satisfy the boundary — and persistence beside it is still rejected.
        var act = () =>
            MssqlNamespaceAuthorizationCommandAssertions.AssertNoPersistenceAfterNamespaceAuthorization([
                new MssqlRelationalQueryAuthorizationRecordedCommand(
                    1,
                    "SELECT CASE WHEN ... THEN CAST('AUTH1 - 1|0|1|0:0:n' AS INT) END; " + DocumentInsert
                ),
            ]);

        act.Should().Throw<AssertionException>().WithMessage("*cannot pass without*");
    }

    [Test]
    public void It_requires_an_empty_command_list_for_a_planner_terminal_denial()
    {
        var passing = () => MssqlNamespaceAuthorizationCommandAssertions.AssertNoCommandsIssued([]);
        var failing = () =>
            MssqlNamespaceAuthorizationCommandAssertions.AssertNoCommandsIssued([
                new MssqlRelationalQueryAuthorizationRecordedCommand(1, LockCommand),
            ]);

        passing.Should().NotThrow();
        failing.Should().Throw<AssertionException>().WithMessage("*before the write session issues*");
    }

    /// <summary>
    /// Shape of the compiled stored-value check: one namespace marker per failing CASE branch, so the last
    /// marker — not the first — is the ordering boundary inside the statement.
    /// </summary>
    private static string StoredCheck() =>
        "SELECT CASE\n"
        + "WHEN EXISTS (SELECT 1 FROM [authz].[AuthorizationNamespaceResource] r WHERE r.[DocumentId] = @documentId AND (r.[Namespace] IS NOT NULL AND (r.[Namespace] LIKE @namespacePrefixes_0 ESCAPE '\\'))) THEN 1\n"
        + "WHEN EXISTS (SELECT 1 FROM [authz].[AuthorizationNamespaceResource] r WHERE r.[DocumentId] = @documentId AND (r.[Namespace] IS NULL OR r.[Namespace] = '')) THEN CAST('AUTH1 - ns1|0|u' AS INT)\n"
        + "WHEN NOT EXISTS (SELECT 1 FROM [authz].[AuthorizationNamespaceResource] r WHERE r.[DocumentId] = @documentId) THEN CAST('AUTH1 - ns1|0|s' AS INT)\n"
        + "ELSE CAST('AUTH1 - ns1|0|m' AS INT)\n"
        + "END;";

    private static string ProposedCheck() =>
        "SELECT CASE\n"
        + "WHEN @proposedNamespace IS NULL OR @proposedNamespace = '' THEN CAST('AUTH1 - ns1|0|r' AS INT)\n"
        + "WHEN @proposedNamespace LIKE @namespacePrefixes_0 ESCAPE '\\' THEN 1\n"
        + "ELSE CAST('AUTH1 - ns1|0|m' AS INT)\n"
        + "END;";
}

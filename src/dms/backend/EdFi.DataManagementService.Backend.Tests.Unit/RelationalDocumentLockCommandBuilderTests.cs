// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

/// <summary>
/// The document version lock pins one existing <c>dms.Document</c> row for the rest of the transaction.
/// On SQL Server that is an exact-key <c>UPDLOCK, ROWLOCK</c>; <c>HOLDLOCK</c> would add key-range locks
/// on the primary key that serialize unrelated concurrent writers and are the source of the lock convoys
/// and deadlocks measured under load. PostgreSQL's <c>FOR UPDATE</c> likewise takes no range lock.
/// </summary>
[TestFixture]
[Parallelizable]
public class Given_RelationalDocumentLockCommandBuilder
{
    private static IEnumerable<TestCaseData> LockCommandBuilders()
    {
        yield return new TestCaseData(
            (Func<SqlDialect, RelationalCommand>)(
                dialect => RelationalDocumentLockCommandBuilder.BuildContentVersionCommand(dialect, 123L)
            )
        ).SetArgDisplayNames("ContentVersion");
        yield return new TestCaseData(
            (Func<SqlDialect, RelationalCommand>)(
                dialect =>
                    RelationalDocumentLockCommandBuilder.BuildContentVersionWithDocumentCacheEnqueueOutcomeCommand(
                        dialect,
                        123L
                    )
            )
        ).SetArgDisplayNames("ContentVersionWithEnqueueOutcome(boundId)");
        yield return new TestCaseData(
            (Func<SqlDialect, RelationalCommand>)(
                dialect =>
                    RelationalDocumentLockCommandBuilder.BuildContentVersionWithDocumentCacheEnqueueOutcomeCommand(
                        dialect,
                        "@createdDocumentId"
                    )
            )
        ).SetArgDisplayNames("ContentVersionWithEnqueueOutcome(idSql)");
    }

    [TestCaseSource(nameof(LockCommandBuilders))]
    public void It_takes_a_sql_server_row_lock_without_a_range_lock(Func<SqlDialect, RelationalCommand> build)
    {
        var commandText = build(SqlDialect.Mssql).CommandText;

        commandText.Should().Contain("FROM [dms].[Document] document WITH (UPDLOCK, ROWLOCK)");
        commandText.Should().NotContain("HOLDLOCK");
    }

    [TestCaseSource(nameof(LockCommandBuilders))]
    public void It_locks_the_postgresql_row_with_for_update_and_no_table_hint(
        Func<SqlDialect, RelationalCommand> build
    )
    {
        var commandText = build(SqlDialect.Pgsql).CommandText;

        commandText.Should().Contain("FOR UPDATE");
        commandText.Should().NotContain("WITH (");
    }
}

// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using EdFi.DataManagementService.Backend.Composite;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

/// <summary>
/// SQL Server mirror of the composite-command live-provider gates: ordered failure attribution, the
/// batch-local captured-target carrier, the session-option envelope and its scope, and the session's
/// tolerance of a rollback the server already performed.
/// </summary>
[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[Category(MssqlCiShards.Shard4)]
public class Given_A_Mssql_Composite_Command_Against_A_Live_Provider
{
    private const string FixtureRelativePath =
        "src/dms/backend/EdFi.DataManagementService.Backend.Ddl.Tests.Unit/Fixtures/focused/stable-key-update-semantics";

    private MssqlGeneratedDdlTestDatabase _database = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        if (!MssqlTestDatabaseHelper.IsConfigured())
        {
            Assert.Ignore(
                "SQL Server integration tests require a MssqlAdmin connection string in appsettings.Test.json"
            );
        }

        var fixture = MssqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(FixtureRelativePath);
        _database = await MssqlGeneratedDdlTestDatabase.CreateProvisionedAsync(fixture.GeneratedDdl);
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_database is not null)
        {
            await _database.DisposeAsync();
            _database = null!;
        }
    }

    [SetUp]
    public async Task SetUp() => await _database.ResetAsync();

    // --- Gate 1: ordered failure attribution ---

    [TestCase(0, TestName = "It_attributes_a_failure_in_the_first_logical_statement")]
    [TestCase(1, TestName = "It_attributes_a_failure_in_a_middle_logical_statement")]
    [TestCase(2, TestName = "It_attributes_a_failure_in_the_last_logical_statement")]
    public async Task It_attributes_a_provider_failure_to_the_logical_statement_that_raised_it(
        int failingOrdinal
    )
    {
        var builder = new RelationalCompositeCommandBuilder(
            IRelationalCompositeCommandDialect.Create(SqlDialect.Mssql)
        );

        for (var ordinal = 0; ordinal < 3; ordinal++)
        {
            builder.Append(
                $"statement-{ordinal}",
                ordinal == failingOrdinal ? "SELECT 1 / 0;" : "SELECT 1;",
                [],
                RelationalCompositeResultShape.Scalar
            );
        }

        var composite = builder.Seal();

        // Every one of these commands carries the session-option envelope, so it is exercised live on each.
        composite.Command.CommandText.Should().Contain("SET XACT_ABORT ON;");

        var execution = new RelationalCompositeCommandExecution();

        await using var session = await CreateSessionAsync();

        var act = async () => await execution.ExecuteAsync(session, composite);

        var thrown = (await act.Should().ThrowAsync<SqlException>()).Which;

        // The provider exception arrives unchanged, so the existing classifier and constraint resolver
        // stay authoritative. 8134 is SQL Server's divide-by-zero.
        thrown.Number.Should().Be(8134);
        execution.Failure.Should().NotBeNull();

        // The claim that matters, and it holds identically on both providers: the reported ordinal
        // identifies the statement that actually failed.
        execution.Failure!.Ordinal.Should().Be(failingOrdinal);
        execution.Failure.Label.Should().Be($"statement-{failingOrdinal}");

        // Measured provider difference. SqlClient hands back a reader and lets NextResult succeed onto the
        // failing statement's result set, then raises the error when its rows are read. Npgsql instead
        // raises at the reader-open or result-set boundary. The stage is therefore diagnostic only and is
        // not a cross-provider invariant; the ordinal is. Attribution stays correct on SQL Server precisely
        // because every logical statement emits exactly one result set, so advancing cannot skip past the
        // failing statement onto a later one.
        execution.Failure.Stage.Should().Be(RelationalCompositeFailureStage.ReadingRows);

        await session.RollbackAsync();
    }

    [Test]
    public async Task It_decodes_every_statement_in_order_when_nothing_fails()
    {
        var builder = new RelationalCompositeCommandBuilder(
            IRelationalCompositeCommandDialect.Create(SqlDialect.Mssql)
        );
        builder.Append("scalar", "SELECT 41;", [], RelationalCompositeResultShape.Scalar);
        builder.Append(
            "dml",
            "CREATE TABLE #composite_probe (id int);",
            [],
            RelationalCompositeResultShape.Sentinel
        );
        builder.Append(
            "rows",
            "SELECT value FROM (VALUES (1), (2), (3)) AS probe(value);",
            [],
            RelationalCompositeResultShape.Rows
        );

        await using var session = await CreateSessionAsync();

        var outcomes = await new RelationalCompositeCommandExecution().ExecuteAsync(session, builder.Seal());

        outcomes.Select(outcome => outcome.Label).Should().Equal("scalar", "dml", "rows");
        outcomes[0].Value.Should().Be(41);
        // A sentinel echoes its own ordinal, which is how the decoder proves emitted and declared order
        // agree.
        outcomes[1].Value.Should().Be(1);
        outcomes[2].Value.Should().Be(3);

        await session.RollbackAsync();
    }

    // --- Gate 2: batch-local captured-target carrier ---

    [Test]
    public async Task It_declares_the_batch_local_carrier_and_publishes_the_capture_to_dependent_statements()
    {
        var documentUuid = Guid.NewGuid();
        var documentId = await SeedDocumentAsync(documentUuid);
        var builder = CreateCaptureBuilder(documentUuid, out var carrier);

        builder.Append(
            "dependent-id",
            $"SELECT {carrier.CapturedTargetIdExpression} AS [CapturedDocumentId];",
            [],
            RelationalCompositeResultShape.Scalar
        );
        builder.Append(
            "dependent-present",
            $"SELECT CAST(CASE WHEN {carrier.CapturedTargetPresentPredicate} THEN 1 ELSE 0 END AS bit) AS [TargetPresent];",
            [],
            RelationalCompositeResultShape.Scalar
        );

        var composite = builder.Seal();
        composite.Command.CommandText.Should().Contain("DECLARE @dms_composite_target_documentid BIGINT");

        await using var session = await CreateSessionAsync();

        var outcomes = await new RelationalCompositeCommandExecution().ExecuteAsync(session, composite);

        outcomes[0]
            .Value.Should()
            .BeOfType<RelationalCompositeCapturedTarget>()
            .Which.DocumentId.Should()
            .Be(documentId);
        outcomes[1].Value.Should().Be(documentId);
        outcomes[2].Value.Should().Be(true);

        await session.RollbackAsync();
    }

    [Test]
    public async Task It_captures_an_absent_target_so_dependent_statements_see_no_target()
    {
        var builder = CreateCaptureBuilder(Guid.NewGuid(), out var carrier);
        builder.Append(
            "dependent-present",
            $"SELECT CAST(CASE WHEN {carrier.CapturedTargetPresentPredicate} THEN 1 ELSE 0 END AS bit) AS [TargetPresent];",
            [],
            RelationalCompositeResultShape.Scalar
        );
        builder.Append(
            "dependent-id",
            $"SELECT {carrier.CapturedTargetIdExpression} AS [CapturedDocumentId];",
            [],
            RelationalCompositeResultShape.Scalar
        );

        await using var session = await CreateSessionAsync();

        var outcomes = await new RelationalCompositeCommandExecution().ExecuteAsync(session, builder.Seal());

        // The capture statement assigned nothing, so its own select returns a NULL row rather than no row,
        // and the dependents observe the absent capture.
        outcomes[0].Value.Should().BeNull();
        outcomes[1].Value.Should().Be(false);
        outcomes[2].Value.Should().BeNull();

        await session.RollbackAsync();
    }

    [Test]
    public async Task It_scopes_the_carrier_to_its_own_batch_so_a_later_command_cannot_see_it()
    {
        var documentUuid = Guid.NewGuid();
        await SeedDocumentAsync(documentUuid);

        await using var session = await CreateSessionAsync();

        await new RelationalCompositeCommandExecution().ExecuteAsync(
            session,
            CreateCaptureBuilder(documentUuid, out var carrier).Seal()
        );

        // A batch-local variable cannot outlive its batch, so a separate command on the same transaction
        // must fail to compile rather than observe stale state. That is the SQL Server equivalent of the
        // PostgreSQL transaction-local revert, and it needs no cleanup statement.
        await using var probeCommand = session.Connection.CreateCommand();
        probeCommand.Transaction = session.Transaction;
        probeCommand.CommandText = $"SELECT {carrier.CapturedTargetIdExpression};";

        var act = async () => await probeCommand.ExecuteScalarAsync();

        (await act.Should().ThrowAsync<SqlException>())
            .Which.Message.Should()
            .Contain("dms_composite_target_documentid");

        await session.RollbackAsync();
    }

    // --- Session-option prologue on a command holding one logical statement ---

    [Test]
    public async Task It_carries_the_session_option_prologue_on_a_single_logical_data_modifying_statement()
    {
        var builder = new RelationalCompositeCommandBuilder(
            IRelationalCompositeCommandDialect.Create(SqlDialect.Mssql)
        );

        // One logical statement, four emitted statements once the sentinel is appended. Deterministic
        // packing makes a command holding a single logical statement ordinary at a budget boundary.
        builder.Append(
            "single-dml",
            """
            CREATE TABLE #composite_single_probe (id INT NOT NULL);
            INSERT INTO #composite_single_probe (id) VALUES (1), (2);
            """,
            [],
            RelationalCompositeResultShape.Sentinel
        );

        var composite = builder.Seal();

        composite.Command.CommandText.Should().Contain("SET XACT_ABORT ON;");

        await using var session = await CreateSessionAsync();

        var outcomes = await new RelationalCompositeCommandExecution().ExecuteAsync(session, composite);

        // The single declared result set is the sentinel and it echoes its own ordinal. Measured: SqlClient
        // does not surface the insert's row-count completion as a result set, so this decodes the same with
        // NOCOUNT off; the session options are established for the abort semantics, not for the decoder.
        outcomes.Should().HaveCount(1);
        outcomes[0].Label.Should().Be("single-dml");
        outcomes[0].Value.Should().Be(0);

        await session.RollbackAsync();
    }

    [Test]
    public async Task It_aborts_a_failing_single_logical_statement_batch_instead_of_continuing_it()
    {
        var builder = new RelationalCompositeCommandBuilder(
            IRelationalCompositeCommandDialect.Create(SqlDialect.Mssql)
        );

        builder.Append(
            "single-dml",
            """
            CREATE TABLE #composite_abort_probe (id INT NOT NULL PRIMARY KEY);
            INSERT INTO #composite_abort_probe (id) VALUES (1);
            INSERT INTO #composite_abort_probe (id) VALUES (1);
            INSERT INTO #composite_abort_probe (id) VALUES (2);
            """,
            [],
            RelationalCompositeResultShape.Sentinel
        );

        await using var session = await CreateSessionAsync();

        var execution = new RelationalCompositeCommandExecution();

        var act = async () => await execution.ExecuteAsync(session, builder.Seal());

        // 2627 is the primary-key violation on the duplicate insert.
        (await act.Should().ThrowAsync<SqlException>())
            .Which.Number.Should()
            .Be(2627);

        // The ordinal is the invariant; the stage is provider-dependent and is deliberately not asserted.
        execution.Failure.Should().NotBeNull();
        execution.Failure!.Ordinal.Should().Be(0);
        execution.Failure.Label.Should().Be("single-dml");

        // The discriminating assertion. Without the session options the violation would abort only the
        // offending statement, the batch would run on through the following insert, and the transaction would
        // still be committable. XACT_ABORT dooms it instead, so nothing the batch emitted after the violation
        // can reach durable state.
        var commit = async () => await session.CommitAsync();
        var commitFailure = (await commit.Should().ThrowAsync<Exception>()).Which;

        // The type depends on how far the server got: SqlClient raises InvalidOperationException once the
        // server has rolled back and detached the client transaction, and SqlException when it rejects the
        // commit of a doomed transaction. Committing must fail either way.
        (commitFailure is InvalidOperationException || commitFailure is SqlException)
            .Should()
            .BeTrue($"commit after an aborted batch threw {commitFailure.GetType().Name}");

        // The rollback completes rather than throwing over the mapped 2627. The composite execution reported
        // the provider failure on this session and the probe proves the server already completed the
        // transaction, which is the only case the session tolerates.
        await session.RollbackAsync();
    }

    [Test]
    public async Task It_rolls_back_a_transaction_the_server_already_completed_without_masking_the_failure()
    {
        var builder = new RelationalCompositeCommandBuilder(
            IRelationalCompositeCommandDialect.Create(SqlDialect.Mssql)
        );
        builder.Append(
            "abort",
            """
            CREATE TABLE #composite_tolerance_probe (id INT NOT NULL PRIMARY KEY);
            INSERT INTO #composite_tolerance_probe (id) VALUES (1);
            INSERT INTO #composite_tolerance_probe (id) VALUES (1);
            """,
            [],
            RelationalCompositeResultShape.Sentinel
        );

        await using var session = await CreateSessionAsync();

        var act = async () =>
            await new RelationalCompositeCommandExecution().ExecuteAsync(session, builder.Seal());

        var thrown = (await act.Should().ThrowAsync<SqlException>()).Which;
        thrown.Number.Should().Be(2627);

        // The transaction the server aborted is detached, which is what the probe recognizes.
        session.Transaction.Should().BeOfType<SqlTransaction>().Which.Connection.Should().BeNull();
        session.Connection.State.Should().Be(ConnectionState.Open);

        // The rollback completes, and the caller still holds the original provider exception, so a mapped
        // 409 is never replaced by an unrelated invalid-operation failure.
        var rollback = async () => await session.RollbackAsync();

        await rollback.Should().NotThrowAsync();
        thrown.Number.Should().Be(2627);
    }

    [Test]
    public async Task It_refuses_tolerance_when_the_reported_failure_is_fatal()
    {
        await using var session = await CreateSessionAsync();

        var fatalFailure = await CaptureFatalSqlExceptionAsync();

        // Severity 20 and above terminates the connection, so the client cannot conclude the transaction's
        // fate from a detached reference. Even in the otherwise tolerable shape the probe must defer.
        fatalFailure.Class.Should().BeGreaterThanOrEqualTo(20);
        await AbortTheBatchAsync(session);
        session.Transaction.Should().BeOfType<SqlTransaction>().Which.Connection.Should().BeNull();

        MssqlTransactionStateProbe
            .Instance.IsAlreadyCompleted(session.Connection, session.Transaction, fatalFailure)
            .Should()
            .BeFalse();
    }

    [Test]
    public async Task It_refuses_tolerance_when_the_connection_is_no_longer_open()
    {
        var session = await CreateSessionAsync();
        var connection = session.Connection;
        var transaction = session.Transaction;

        var nonFatalFailure = await AbortTheBatchAsync(session);
        await connection.CloseAsync();

        // A connection-level fault also detaches the transaction but has to surface, so an open connection
        // is required evidence rather than a convenience check.
        connection.State.Should().NotBe(ConnectionState.Open);
        MssqlTransactionStateProbe
            .Instance.IsAlreadyCompleted(connection, transaction, nonFatalFailure)
            .Should()
            .BeFalse();

        await session.DisposeAsync();
    }

    // --- Does the session-option envelope escape the composite command's execution context? ---

    [TestCase(true, TestName = "It_confines_the_options_when_the_composite_command_carries_parameters")]
    [TestCase(false, TestName = "It_confines_the_options_when_the_composite_command_has_no_parameters")]
    public async Task It_confines_the_session_options_to_the_composite_command(bool parameterized)
    {
        await using var session = await CreateSessionAsync();
        await CreateOptionScopeProbeTableAsync(session);
        await ExecuteOptionScopeCompositeAsync(session, parameterized);

        // An ordinary single-statement command that violates the key. Whether this dooms the transaction is
        // the measurement: options confined to the composite command leave the transaction alive, and
        // options that reached the session doom it. SqlClient sends a parameterized command through
        // sp_executesql, whose procedure context restores SET options on exit, and a parameterless command
        // as a plain batch that would otherwise leak them, so both shapes are covered.
        await using (
            var violatingCommand = session.CreateCommand(
                new RelationalCommand("INSERT INTO #option_scope_probe (id) VALUES (1);")
            )
        )
        {
            var act = async () => await violatingCommand.ExecuteNonQueryAsync();

            (await act.Should().ThrowAsync<SqlException>()).Which.Number.Should().Be(2627);
        }

        // Still committable, so a failure raised outside a composite command is not doomed by options the
        // composite command established.
        await session.CommitAsync();
    }

    [TestCase(true, TestName = "It_restores_an_ambient_option_when_the_command_carries_parameters")]
    [TestCase(false, TestName = "It_restores_an_ambient_option_when_the_command_has_no_parameters")]
    public async Task It_restores_an_ambient_session_option_after_a_successful_composite_command(
        bool parameterized
    )
    {
        await using var session = await CreateSessionAsync();

        await using (var ambientCommand = session.CreateCommand(new RelationalCommand("SET XACT_ABORT ON;")))
        {
            await ambientCommand.ExecuteNonQueryAsync();
        }

        await CreateOptionScopeProbeTableAsync(session);
        await ExecuteOptionScopeCompositeAsync(session, parameterized);

        // The epilogue restores the captured prior value rather than forcing OFF, so a caller who had
        // XACT_ABORT on keeps it and the ordinary violation below still dooms the transaction.
        await using (
            var violatingCommand = session.CreateCommand(
                new RelationalCommand("INSERT INTO #option_scope_probe (id) VALUES (1);")
            )
        )
        {
            var act = async () => await violatingCommand.ExecuteNonQueryAsync();

            (await act.Should().ThrowAsync<SqlException>()).Which.Number.Should().Be(2627);
        }

        var commit = async () => await session.CommitAsync();

        await commit.Should().ThrowAsync<Exception>();
    }

    // --- The abort path leaves the options set, so pooled reset is its cleanup boundary ---

    [TestCase(
        true,
        TestName = "It_resets_the_session_options_after_an_aborted_parameterized_composite_is_pooled"
    )]
    [TestCase(
        false,
        TestName = "It_resets_the_session_options_after_an_aborted_parameterless_composite_is_pooled"
    )]
    public async Task It_relies_on_pooled_reset_to_clear_the_session_options_after_an_abort(
        bool parameterized
    )
    {
        // An aborted batch stops before the epilogue, so both options are still set on the physical session
        // when it is returned. Nothing in the command can restore them, which makes the client's reset of a
        // pooled connection the only cleanup boundary the abort path has. Observing that reset requires the
        // reborrow to land on the same physical session, so the case takes exactly one pool hop: the session
        // itself opens the pool's first connection and carries the baseline reading.
        var pooledConnectionString = BuildIsolatedPoolConnectionString(parameterized);

        try
        {
            var (session, baseline) = await OpenPooledSessionAsync(pooledConnectionString);

            await using (session)
            {
                // Self-checking assumption: if this instance defaulted either option on, the assertion after
                // the reborrow would be measuring a server default rather than a reset.
                baseline.XactAbortOn.Should().BeFalse();
                baseline.NoCountOn.Should().BeFalse();

                var builder = new RelationalCompositeCommandBuilder(
                    IRelationalCompositeCommandDialect.Create(SqlDialect.Mssql)
                );

                if (parameterized)
                {
                    var parameterName = builder.Allocator.AllocateStatementScoped("probe", 0);
                    builder.Append(
                        "abort",
                        $"""
                        CREATE TABLE #pool_reset_probe (id INT NOT NULL PRIMARY KEY);
                        INSERT INTO #pool_reset_probe (id) VALUES ({parameterName});
                        INSERT INTO #pool_reset_probe (id) VALUES ({parameterName});
                        """,
                        [new RelationalParameter(parameterName, 1L)],
                        RelationalCompositeResultShape.Sentinel
                    );
                }
                else
                {
                    builder.Append(
                        "abort",
                        """
                        CREATE TABLE #pool_reset_probe (id INT NOT NULL PRIMARY KEY);
                        INSERT INTO #pool_reset_probe (id) VALUES (1);
                        INSERT INTO #pool_reset_probe (id) VALUES (1);
                        """,
                        [],
                        RelationalCompositeResultShape.Sentinel
                    );
                }

                var act = async () =>
                    await new RelationalCompositeCommandExecution().ExecuteAsync(session, builder.Seal());

                // 2627 is the primary-key violation, non-fatal, so the connection stays healthy and can be
                // pooled while the transaction is doomed.
                (await act.Should().ThrowAsync<SqlException>())
                    .Which.Number.Should()
                    .Be(2627);

                // The transaction is detached, which proves the options were in effect during the batch:
                // without XACT_ABORT the violation would have aborted only its own statement.
                session.Transaction.Should().BeOfType<SqlTransaction>().Which.Connection.Should().BeNull();

                var rollback = async () => await session.RollbackAsync();

                await rollback.Should().NotThrowAsync();

                // Readable on a raw command because the server already completed the transaction, so the
                // connection no longer holds a pending one.
                var aborted = await ReadSessionOptionStateAsync((SqlConnection)session.Connection);

                // Still the connection the session opened, so this guards against the abort or the
                // server-completed rollback reconnecting underneath the session.
                aborted.SessionId.Should().Be(baseline.SessionId);

                if (parameterized)
                {
                    // Measured: sp_executesql's procedure context restores the options even when the batch
                    // aborted inside it. This shape therefore does not leak, and its reborrow below is a
                    // reuse-and-health check rather than a proof of pooled reset.
                    aborted.XactAbortOn.Should().BeFalse();
                    aborted.NoCountOn.Should().BeFalse();
                }
                else
                {
                    // A plain batch has no procedure context to unwind and the epilogue is unreachable after
                    // an abort, so both options are still set when the connection is returned. That is what
                    // makes the assertion after the reborrow a reset rather than a value never changed.
                    aborted.XactAbortOn.Should().BeTrue();
                    aborted.NoCountOn.Should().BeTrue();
                }
            }

            await using (var reusedConnection = new SqlConnection(pooledConnectionString))
            {
                await reusedConnection.OpenAsync();

                // The first command after a reborrow is the one SqlClient carries the reset request on, so
                // reading both the session id and the options here leaves no unreset window.
                var reused = await ReadSessionOptionStateAsync(reusedConnection);

                if (reused.SessionId != baseline.SessionId)
                {
                    // Discarding a returned connection is a legitimate pool choice - MaxPoolSize bounds
                    // concurrency, not identity - and a fresh session starts with both options off, so the
                    // options read below would pass without a reset ever happening. The reset is therefore
                    // unobserved rather than absent, which is not a result this case can report either way.
                    Assert.Inconclusive(
                        $"The reborrow landed on session {reused.SessionId} rather than {baseline.SessionId}, "
                            + "so the aborted connection was discarded instead of handed back and pooled reset "
                            + "could not be observed."
                    );
                }

                reused.XactAbortOn.Should().BeFalse();
                reused.NoCountOn.Should().BeFalse();

                await using var usableCommand = reusedConnection.CreateCommand();
                usableCommand.CommandText = "SELECT 1;";

                (await usableCommand.ExecuteScalarAsync()).Should().Be(1);
            }
        }
        finally
        {
            using SqlConnection poolKey = new(pooledConnectionString);
            SqlConnection.ClearPool(poolKey);
        }
    }

    /// <summary>
    /// Derives a connection string with its own pool identity and a single slot, so a case's borrows are
    /// isolated from the fixture's other connections and only one physical session is live at a time. The
    /// single slot makes a reborrow reuse that session but cannot guarantee it: the pool stays free to
    /// discard a returned connection and open a replacement.
    /// </summary>
    private string BuildIsolatedPoolConnectionString(bool parameterized) =>
        new SqlConnectionStringBuilder(_database.ConnectionString)
        {
            // Pool identity is the whole connection string, and the unique token keeps repeated or parallel
            // execution from sharing a slot.
            ApplicationName =
                $"dms-composite-pool-reset-{(parameterized ? "parameterized" : "parameterless")}-{Guid.NewGuid():N}",
            MaxPoolSize = 1,
            Pooling = true,
        }.ConnectionString;

    /// <summary>
    /// Opens a pooled session and reads its physical session state before the transaction begins, so a case
    /// measuring pooled reset needs only the one pool hop it is actually measuring.
    /// </summary>
    private static async Task<(
        RelationalWriteSession Session,
        PooledSessionOptionState Baseline
    )> OpenPooledSessionAsync(string connectionString)
    {
        var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();

        // Read before the transaction begins: a raw command cannot run on a connection that holds a pending
        // local transaction it has not been handed.
        var baseline = await ReadSessionOptionStateAsync(connection);

        var transaction = (SqlTransaction)
            await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted);

        return (
            new RelationalWriteSession(connection, transaction, MssqlTransactionStateProbe.Instance),
            baseline
        );
    }

    /// <summary>
    /// Reads the physical session id and both option bits in one command. Bit 16384 is
    /// <c>XACT_ABORT</c> and bit 512 is <c>NOCOUNT</c> in <c>@@OPTIONS</c>.
    /// </summary>
    private static async Task<PooledSessionOptionState> ReadSessionOptionStateAsync(SqlConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT @@SPID AS [SessionId], @@OPTIONS & 16384 AS [XactAbort], @@OPTIONS & 512 AS [NoCount];";

        await using var reader = await command.ExecuteReaderAsync();

        (await reader.ReadAsync()).Should().BeTrue();

        return new PooledSessionOptionState(
            reader.GetInt16(0),
            reader.GetInt32(1) != 0,
            reader.GetInt32(2) != 0
        );
    }

    private sealed record PooledSessionOptionState(short SessionId, bool XactAbortOn, bool NoCountOn);

    private static async Task CreateOptionScopeProbeTableAsync(RelationalWriteSession session)
    {
        await using var setupCommand = session.CreateCommand(
            new RelationalCommand(
                """
                CREATE TABLE #option_scope_probe (id INT NOT NULL PRIMARY KEY);
                INSERT INTO #option_scope_probe (id) VALUES (1);
                """
            )
        );

        await setupCommand.ExecuteNonQueryAsync();
    }

    private static async Task ExecuteOptionScopeCompositeAsync(
        RelationalWriteSession session,
        bool parameterized
    )
    {
        var builder = new RelationalCompositeCommandBuilder(
            IRelationalCompositeCommandDialect.Create(SqlDialect.Mssql)
        );

        if (parameterized)
        {
            var parameterName = builder.Allocator.AllocateStatementScoped("probe", 0);
            builder.Append(
                "scalar",
                $"SELECT {parameterName};",
                [new RelationalParameter(parameterName, 1L)],
                RelationalCompositeResultShape.Scalar
            );
        }
        else
        {
            builder.Append("scalar", "SELECT 1;", [], RelationalCompositeResultShape.Scalar);
        }

        var composite = builder.Seal();
        composite.Command.CommandText.Should().Contain("SET XACT_ABORT ON;");

        await new RelationalCompositeCommandExecution().ExecuteAsync(session, composite);
    }

    /// <summary>
    /// Aborts a composite batch on the session and returns the non-fatal provider failure it raised, leaving
    /// the transaction in the state the server completed itself.
    /// </summary>
    private static async Task<SqlException> AbortTheBatchAsync(RelationalWriteSession session)
    {
        var builder = new RelationalCompositeCommandBuilder(
            IRelationalCompositeCommandDialect.Create(SqlDialect.Mssql)
        );
        builder.Append(
            "abort",
            """
            CREATE TABLE #composite_abort_scratch (id INT NOT NULL PRIMARY KEY);
            INSERT INTO #composite_abort_scratch (id) VALUES (1);
            INSERT INTO #composite_abort_scratch (id) VALUES (1);
            """,
            [],
            RelationalCompositeResultShape.Sentinel
        );

        var act = async () =>
            await new RelationalCompositeCommandExecution().ExecuteAsync(session, builder.Seal());

        return (await act.Should().ThrowAsync<SqlException>()).Which;
    }

    /// <summary>
    /// Raises a severity-20 error on a throwaway connection so a genuinely fatal <see cref="SqlException"/>
    /// is available without breaking the connection under test.
    /// </summary>
    private async Task<SqlException> CaptureFatalSqlExceptionAsync()
    {
        await using var connection = new SqlConnection(_database.ConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "RAISERROR ('composite probe fatal', 20, 1) WITH LOG;";

        var act = async () => await command.ExecuteNonQueryAsync();

        return (await act.Should().ThrowAsync<SqlException>()).Which;
    }

    private static RelationalCompositeCommandBuilder CreateCaptureBuilder(
        Guid documentUuid,
        out IRelationalCompositeTargetCarrier carrier
    )
    {
        var builder = new RelationalCompositeCommandBuilder(
            IRelationalCompositeCommandDialect.Create(SqlDialect.Mssql)
        );
        var uuidParameterName = builder.Allocator.AllocateStatementScoped("documentUuid", 0);

        builder.AppendCaptureTarget(
            $"d.[DocumentUuid] = {uuidParameterName}",
            [new RelationalParameter(uuidParameterName, documentUuid)]
        );

        carrier = builder.Carrier;

        return builder;
    }

    /// <summary>
    /// Builds the session the same way the production factory does, including the SQL Server transaction
    /// state probe, so these gates exercise the shared session's real rollback behavior.
    /// </summary>
    private async Task<RelationalWriteSession> CreateSessionAsync()
    {
        var connection = new SqlConnection(_database.ConnectionString);
        await connection.OpenAsync();
        var transaction = (SqlTransaction)
            await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted);

        return new RelationalWriteSession(connection, transaction, MssqlTransactionStateProbe.Instance);
    }

    private async Task<long> SeedDocumentAsync(Guid documentUuid)
    {
        // Provisioning seeds dms.ResourceKey, so reuse an existing key rather than inventing one.
        var resourceKeyId = await _database.ExecuteScalarAsync<short>(
            "SELECT MIN([ResourceKeyId]) FROM [dms].[ResourceKey];"
        );

        return await _database.ExecuteScalarAsync<long>(
            """
            INSERT INTO [dms].[Document] ([DocumentUuid], [ResourceKeyId])
            VALUES (@documentUuid, @resourceKeyId);
            SELECT CAST(SCOPE_IDENTITY() AS bigint);
            """,
            new SqlParameter("@documentUuid", SqlDbType.UniqueIdentifier) { Value = documentUuid },
            new SqlParameter("@resourceKeyId", SqlDbType.SmallInt) { Value = resourceKeyId }
        );
    }
}

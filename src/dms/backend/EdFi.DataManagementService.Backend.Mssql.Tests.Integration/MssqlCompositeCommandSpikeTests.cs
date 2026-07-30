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
/// batch-local captured-target carrier, and the multi-statement session-option prologue.
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

        // Every one of these commands is multi-statement, so the prologue is exercised live on each.
        composite.Command.CommandText.Should().StartWith("SET XACT_ABORT ON;");

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

        await TryRollbackAsync(session);
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
        // agree. SET NOCOUNT ON keeps the DML from injecting a row-count message ahead of it.
        outcomes[1].Value.Should().Be(1);
        outcomes[2].Value.Should().Be(3);

        await TryRollbackAsync(session);
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

        outcomes[0].Value.Should().Be(documentId);
        outcomes[1].Value.Should().Be(documentId);
        outcomes[2].Value.Should().Be(true);

        await TryRollbackAsync(session);
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

        await TryRollbackAsync(session);
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

        await TryRollbackAsync(session);
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

    private async Task<RelationalWriteSession> CreateSessionAsync()
    {
        var connection = new SqlConnection(_database.ConnectionString);
        await connection.OpenAsync();
        var transaction = (SqlTransaction)
            await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted);

        return new RelationalWriteSession(connection, transaction);
    }

    /// <summary>
    /// Rolls back best-effort. With <c>SET XACT_ABORT ON</c> the server may already have rolled the
    /// transaction back and detached the client-side transaction object, in which case rollback throws.
    /// Making the shared session tolerant of exactly that case is unit-4 scope; these gates only observe it.
    /// </summary>
    private static async Task TryRollbackAsync(RelationalWriteSession session)
    {
        try
        {
            await session.RollbackAsync();
        }
        catch (InvalidOperationException)
        {
            // Observed and expected after an XACT_ABORT-aborted batch; recorded as unit-4 evidence.
        }
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

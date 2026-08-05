// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Globalization;
using EdFi.DataManagementService.Backend.Composite;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using FluentAssertions;
using Npgsql;
using NpgsqlTypes;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

/// <summary>
/// The live-provider gates that must pass before any production write path consumes a composite
/// command: ordered failure attribution, and the captured-target carrier.
/// </summary>
/// <remarks>
/// Attribution needs no tables, so it is proved with statements that always fail the same way. The
/// carrier needs a real <c>dms.Document</c> row, so it runs against a provisioned generated-DDL
/// database.
/// </remarks>
[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
public class Given_A_Postgresql_Composite_Command_Against_A_Live_Provider
{
    private const string FixtureRelativePath =
        "src/dms/backend/EdFi.DataManagementService.Backend.Ddl.Tests.Unit/Fixtures/focused/stable-key-update-semantics";

    private const string CarrierProbeSql = "SELECT current_setting('dms.composite_target_documentid', true);";

    private PostgresqlGeneratedDdlTestDatabase _database = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        var fixture = PostgresqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(FixtureRelativePath);
        _database = await PostgresqlGeneratedDdlTestDatabase.CreateProvisionedAsync(fixture.GeneratedDdl);
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
            IRelationalCompositeCommandDialect.Create(SqlDialect.Pgsql)
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
        var execution = new RelationalCompositeCommandExecution();

        await using var session = await CreateSessionAsync();

        var act = async () => await execution.ExecuteAsync(session, composite);

        var thrown = (await act.Should().ThrowAsync<PostgresException>()).Which;

        // The provider exception arrives unchanged, so the existing classifier and constraint resolver
        // stay authoritative.
        thrown.SqlState.Should().Be(PostgresErrorCodes.DivisionByZero);
        execution.Failure.Should().NotBeNull();
        execution.Failure!.Ordinal.Should().Be(failingOrdinal);
        execution.Failure.Label.Should().Be($"statement-{failingOrdinal}");
        execution
            .Failure.Stage.Should()
            .Be(
                failingOrdinal == 0
                    ? RelationalCompositeFailureStage.OpeningReader
                    : RelationalCompositeFailureStage.AdvancingResultSet
            );

        await session.RollbackAsync();
    }

    [Test]
    public async Task It_decodes_every_statement_in_order_when_nothing_fails()
    {
        var builder = new RelationalCompositeCommandBuilder(
            IRelationalCompositeCommandDialect.Create(SqlDialect.Pgsql)
        );
        builder.Append("scalar", "SELECT 41;", [], RelationalCompositeResultShape.Scalar);
        builder.Append(
            "dml",
            "CREATE TEMP TABLE composite_probe (id int) ON COMMIT DROP;",
            [],
            RelationalCompositeResultShape.Sentinel
        );
        builder.Append(
            "rows",
            "SELECT * FROM generate_series(1, 3);",
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

    // --- Gate 2: captured-target carrier ---

    [Test]
    public async Task It_accepts_the_locking_clause_in_the_capture_cte_and_publishes_the_capture_to_dependent_statements()
    {
        var documentUuid = Guid.NewGuid();
        var documentId = await SeedDocumentAsync(documentUuid);
        var builder = CreateCaptureBuilder(documentUuid, out var carrier);

        // Dependent statements read the captured value rather than repeating the predicate. If
        // set_config had not already run, these would observe NULL and false.
        builder.Append(
            "dependent-id",
            $"SELECT {carrier.CapturedTargetIdExpression} AS \"CapturedDocumentId\";",
            [],
            RelationalCompositeResultShape.Scalar
        );
        builder.Append(
            "dependent-present",
            $"SELECT {carrier.CapturedTargetPresentPredicate} AS \"TargetPresent\";",
            [],
            RelationalCompositeResultShape.Scalar
        );

        await using var session = await CreateSessionAsync();

        var outcomes = await new RelationalCompositeCommandExecution().ExecuteAsync(session, builder.Seal());

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
            $"SELECT {carrier.CapturedTargetPresentPredicate} AS \"TargetPresent\";",
            [],
            RelationalCompositeResultShape.Scalar
        );
        builder.Append(
            "dependent-id",
            $"SELECT {carrier.CapturedTargetIdExpression} AS \"CapturedDocumentId\";",
            [],
            RelationalCompositeResultShape.Scalar
        );

        await using var session = await CreateSessionAsync();

        var outcomes = await new RelationalCompositeCommandExecution().ExecuteAsync(session, builder.Seal());

        // The capture statement still ran and captured "no target": its unconditional projection row
        // carries NULLs, which decodes as an absent captured target, and the dependents observe it.
        outcomes[0].Value.Should().BeNull();
        outcomes[1].Value.Should().Be(false);
        outcomes[2].Value.Should().BeNull();

        await session.RollbackAsync();
    }

    [TestCase(true, TestName = "It_reverts_the_carrier_after_commit")]
    [TestCase(false, TestName = "It_reverts_the_carrier_after_rollback")]
    public async Task It_keeps_the_carrier_transaction_local(bool commit)
    {
        var documentUuid = Guid.NewGuid();
        var documentId = await SeedDocumentAsync(documentUuid);

        await using var connection = new NpgsqlConnection(_database.ConnectionString);
        await connection.OpenAsync();

        await using (var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted))
        {
            var session = new RelationalWriteSession(connection, transaction);

            await new RelationalCompositeCommandExecution().ExecuteAsync(
                session,
                CreateCaptureBuilder(documentUuid, out _).Seal()
            );

            var carrierInsideTransaction = await ReadCarrierAsync(connection, transaction);
            carrierInsideTransaction
                .Should()
                .Be(
                    documentId.ToString(CultureInfo.InvariantCulture),
                    "the captured id must be readable inside the transaction that set it"
                );

            if (commit)
            {
                await transaction.CommitAsync();
            }
            else
            {
                await transaction.RollbackAsync();
            }
        }

        // Measured PostgreSQL behavior: is_local reverts the captured *value* at transaction end for both
        // outcomes, but the custom-GUC placeholder stays defined on the session as an empty string rather
        // than returning to unset. What matters is that the captured id does not survive and that the
        // carrier's own expressions therefore report "no target" — which is what every dependent statement
        // consumes. Asserting the raw setting is null would be asserting a placeholder detail, not the
        // promise.
        var carrierAfterTransaction = await ReadCarrierAsync(connection, transaction: null);
        carrierAfterTransaction
            .Should()
            .NotBe(
                documentId.ToString(CultureInfo.InvariantCulture),
                "the captured document id must not survive the transaction"
            );
        (await ReadCapturedIdExpressionAsync(connection)).Should().BeNull();
        (await ReadCapturedPresentExpressionAsync(connection)).Should().BeFalse();
    }

    [Test]
    public async Task It_leaves_no_captured_target_for_the_next_pooled_borrower()
    {
        var documentUuid = Guid.NewGuid();
        var documentId = await SeedDocumentAsync(documentUuid);

        await using (var session = await CreateSessionAsync())
        {
            await new RelationalCompositeCommandExecution().ExecuteAsync(
                session,
                CreateCaptureBuilder(documentUuid, out _).Seal()
            );
            await session.CommitAsync();
        }

        // Borrow repeatedly so a pooled physical connection is very likely to be reused.
        for (var attempt = 0; attempt < 5; attempt++)
        {
            await using var connection = new NpgsqlConnection(_database.ConnectionString);
            await connection.OpenAsync();

            var carrier = await ReadCarrierAsync(connection, transaction: null);
            carrier
                .Should()
                .NotBe(
                    documentId.ToString(CultureInfo.InvariantCulture),
                    $"attempt {attempt} borrowed a connection still carrying the captured id"
                );
            (await ReadCapturedIdExpressionAsync(connection))
                .Should()
                .BeNull($"attempt {attempt} observed a captured target");
            (await ReadCapturedPresentExpressionAsync(connection))
                .Should()
                .BeFalse($"attempt {attempt} observed a present target");
        }
    }

    private static RelationalCompositeCommandBuilder CreateCaptureBuilder(
        Guid documentUuid,
        out IRelationalCompositeTargetCarrier carrier
    )
    {
        var builder = new RelationalCompositeCommandBuilder(
            IRelationalCompositeCommandDialect.Create(SqlDialect.Pgsql)
        );
        var uuidParameterName = builder.Allocator.AllocateStatementScoped("documentUuid", 0);

        builder.AppendCaptureTarget(
            $"d.\"DocumentUuid\" = {uuidParameterName}",
            [new RelationalParameter(uuidParameterName, documentUuid)]
        );

        carrier = builder.Carrier;

        return builder;
    }

    private async Task<RelationalWriteSession> CreateSessionAsync()
    {
        var connection = new NpgsqlConnection(_database.ConnectionString);
        await connection.OpenAsync();
        var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted);

        return new RelationalWriteSession(connection, transaction);
    }

    private async Task<long> SeedDocumentAsync(Guid documentUuid)
    {
        // Provisioning seeds dms.ResourceKey, so reuse an existing key rather than inventing one.
        var resourceKeyId = await _database.ExecuteScalarAsync<short>(
            """
            SELECT MIN("ResourceKeyId") FROM dms."ResourceKey";
            """
        );

        return await _database.ExecuteScalarAsync<long>(
            """
            INSERT INTO dms."Document" ("DocumentUuid", "ResourceKeyId")
            VALUES (@documentUuid, @resourceKeyId)
            RETURNING "DocumentId";
            """,
            new NpgsqlParameter("documentUuid", NpgsqlDbType.Uuid) { Value = documentUuid },
            new NpgsqlParameter("resourceKeyId", NpgsqlDbType.Smallint) { Value = resourceKeyId }
        );
    }

    private static async Task<string?> ReadCarrierAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction
    )
    {
        var value = await ExecuteScalarAsync(connection, transaction, CarrierProbeSql);

        return value is null or DBNull ? null : (string)value;
    }

    /// <summary>
    /// Evaluates the carrier's own captured-id expression, which is what dependent statements consume.
    /// </summary>
    private static async Task<long?> ReadCapturedIdExpressionAsync(NpgsqlConnection connection)
    {
        var value = await ExecuteScalarAsync(
            connection,
            transaction: null,
            $"SELECT {PgsqlCompositeTargetCarrier.Instance.CapturedTargetIdExpression};"
        );

        return value is null or DBNull ? null : (long)value;
    }

    /// <summary>Evaluates the carrier's own target-present predicate.</summary>
    private static async Task<bool?> ReadCapturedPresentExpressionAsync(NpgsqlConnection connection)
    {
        var value = await ExecuteScalarAsync(
            connection,
            transaction: null,
            $"SELECT {PgsqlCompositeTargetCarrier.Instance.CapturedTargetPresentPredicate};"
        );

        return value is null or DBNull ? null : (bool)value;
    }

    private static async Task<object?> ExecuteScalarAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string sql
    )
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;

        return await command.ExecuteScalarAsync();
    }
}

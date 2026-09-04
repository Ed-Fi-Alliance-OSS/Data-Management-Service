// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Composite;
using EdFi.DataManagementService.Backend.External;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit.Composite;

[TestFixture]
public class Given_A_Relational_Composite_Command_Dialect
{
    /// <summary>
    /// Asserts the SQL Server envelope: the caller's option state is captured before either option is
    /// established, and each is restored to the captured value after the last statement.
    /// </summary>
    private static void ShouldCarryTheSqlServerOptionEnvelope(string commandText)
    {
        commandText
            .Should()
            .StartWith("DECLARE @dms_composite_prior_xact_abort BIT = IIF((@@OPTIONS & 16384) = 0, 0, 1);");
        commandText
            .Should()
            .Contain("DECLARE @dms_composite_prior_nocount BIT = IIF((@@OPTIONS & 512) = 0, 0, 1);");

        var captureEnd = commandText.IndexOf(
            "DECLARE @dms_composite_prior_nocount",
            StringComparison.Ordinal
        );
        var establish = commandText.IndexOf("SET XACT_ABORT ON;", StringComparison.Ordinal);

        // Capturing after establishing would record the value this command just imposed.
        captureEnd.Should().BeLessThan(establish);
        commandText.Should().Contain("SET NOCOUNT ON;");

        commandText
            .Should()
            .EndWith(
                "IF @dms_composite_prior_xact_abort = 0 SET XACT_ABORT OFF;\n"
                    + "IF @dms_composite_prior_nocount = 0 SET NOCOUNT OFF;"
            );
    }

    [Test]
    public void It_emits_the_sql_server_session_option_envelope_on_a_single_statement_command()
    {
        var builder = new RelationalCompositeCommandBuilder(
            IRelationalCompositeCommandDialect.Create(SqlDialect.Mssql)
        );
        builder.Append("only", "SELECT 1;", [], RelationalCompositeResultShape.Scalar);

        // The envelope is unconditional because a logical statement count does not bound the emitted
        // statement count, so no logical shape can be relied on to make the batch single-statement.
        ShouldCarryTheSqlServerOptionEnvelope(builder.Seal().Command.CommandText);
    }

    [Test]
    public void It_emits_the_sql_server_session_option_envelope_on_a_single_data_modifying_statement()
    {
        var builder = new RelationalCompositeCommandBuilder(
            IRelationalCompositeCommandDialect.Create(SqlDialect.Mssql)
        );
        builder.Append(
            "insert",
            "INSERT INTO [edfi].[School] DEFAULT VALUES;",
            [],
            RelationalCompositeResultShape.Sentinel
        );

        var commandText = builder.Seal().Command.CommandText;

        // One logical statement of this shape emits two statements: the DML and its appended sentinel. A
        // constraint violation in that DML must abort the batch rather than leave the transaction
        // committable, which is what the session options establish.
        ShouldCarryTheSqlServerOptionEnvelope(commandText);
        commandText.Should().Contain("SELECT 0 AS [LogicalStatementOrdinal];");
    }

    [Test]
    public void It_emits_the_sql_server_session_options_ahead_of_the_captured_target_declaration()
    {
        var builder = new RelationalCompositeCommandBuilder(
            IRelationalCompositeCommandDialect.Create(SqlDialect.Mssql)
        );
        var parameter = new RelationalParameter(
            builder.Allocator.AllocateStatementScoped("documentUuid", 0),
            Guid.NewGuid()
        );
        builder.AppendCaptureTarget("d.[DocumentUuid] = @documentUuid_s0", [parameter]);

        var commandText = builder.Seal().Command.CommandText;

        // A capture-only command is a single logical statement that emits a declaration, an assignment
        // select, and a projection select, and the session options must precede all of them.
        ShouldCarryTheSqlServerOptionEnvelope(commandText);
        commandText
            .IndexOf("SET NOCOUNT ON;", StringComparison.Ordinal)
            .Should()
            .BeLessThan(
                commandText.IndexOf("DECLARE @dms_composite_target_documentid", StringComparison.Ordinal)
            );
    }

    [Test]
    public void It_emits_the_sql_server_session_option_envelope_on_a_multi_statement_command()
    {
        var builder = new RelationalCompositeCommandBuilder(
            IRelationalCompositeCommandDialect.Create(SqlDialect.Mssql)
        );
        builder.Append("first", "SELECT 1;", [], RelationalCompositeResultShape.Scalar);
        builder.Append("second", "SELECT 2;", [], RelationalCompositeResultShape.Scalar);

        ShouldCarryTheSqlServerOptionEnvelope(builder.Seal().Command.CommandText);
    }

    [Test]
    public void It_reserves_the_sql_server_captured_option_variables_from_the_allocator()
    {
        var builder = new RelationalCompositeCommandBuilder(
            IRelationalCompositeCommandDialect.Create(SqlDialect.Mssql)
        );

        // The prologue's batch-locals must be unavailable to statements, because SqlClient rejects a
        // batch-local that shares a name with a bound parameter.
        builder
            .Allocator.ReservedNames.Should()
            .Contain("dms_composite_prior_xact_abort")
            .And.Contain("dms_composite_prior_nocount");
    }

    [Test]
    public void It_reserves_no_command_names_for_postgresql()
    {
        IRelationalCompositeCommandDialect.Create(SqlDialect.Pgsql).ReservedCommandNames.Should().BeEmpty();
    }

    [TestCase(1, TestName = "It_emits_no_prologue_for_a_single_statement_postgresql_command")]
    [TestCase(2, TestName = "It_emits_no_prologue_for_a_multi_statement_postgresql_command")]
    public void It_emits_no_prologue_for_postgresql_which_always_aborts_the_transaction_on_error(
        int statementCount
    )
    {
        var builder = new RelationalCompositeCommandBuilder(
            IRelationalCompositeCommandDialect.Create(SqlDialect.Pgsql)
        );

        for (var ordinal = 0; ordinal < statementCount; ordinal++)
        {
            builder.Append(
                $"statement-{ordinal}",
                "INSERT INTO edfi.\"School\" DEFAULT VALUES;",
                [],
                RelationalCompositeResultShape.Sentinel
            );
        }

        var commandText = builder.Seal().Command.CommandText;

        commandText.Should().StartWith("INSERT INTO edfi.\"School\" DEFAULT VALUES;");
        // No epilogue either, so the text ends at the last statement's sentinel.
        commandText.Should().EndWith($"SELECT {statementCount - 1} AS \"LogicalStatementOrdinal\";");
        commandText.Should().NotContain("XACT_ABORT");
        commandText.Should().NotContain("NOCOUNT");
        commandText.Should().NotContain("@@OPTIONS");
    }

    [Test]
    public void It_emits_quoted_sentinels_per_dialect()
    {
        IRelationalCompositeCommandDialect
            .Create(SqlDialect.Pgsql)
            .EmitSentinel(7)
            .Should()
            .Be("SELECT 7 AS \"LogicalStatementOrdinal\";");
        IRelationalCompositeCommandDialect
            .Create(SqlDialect.Mssql)
            .EmitSentinel(7)
            .Should()
            .Be("SELECT 7 AS [LogicalStatementOrdinal];");
    }

    [Test]
    public void It_captures_the_postgresql_target_with_a_transaction_local_setting_evaluated_once()
    {
        var carrier = IRelationalCompositeCommandDialect.Create(SqlDialect.Pgsql).Carrier;

        var sql = carrier.EmitCaptureTarget("d.\"DocumentUuid\" = @documentUuid_s0");

        sql.Should().Contain("FOR UPDATE");
        // The capture must run whether or not the target CTE produced a row, so it is referenced from
        // the final select list rather than written inside the target CTE.
        sql.Should().Contain("set_config(");
        sql.Should().Contain("true");
        sql.Should().Contain("COALESCE((SELECT \"DocumentId\"::text FROM target), '')");
        sql.Should().Contain("(SELECT \"CapturedToken\" FROM captured) AS \"CapturedToken\"");
        carrier.DeclarationPrologue.Should().BeNull();
        carrier.ReservedNames.Should().BeEmpty();
    }

    [Test]
    public void It_exposes_postgresql_captured_expressions_that_read_the_transaction_local_setting()
    {
        var carrier = IRelationalCompositeCommandDialect.Create(SqlDialect.Pgsql).Carrier;

        carrier
            .CapturedTargetIdExpression.Should()
            .Be("NULLIF(current_setting('dms.composite_target_documentid', true), '')::bigint");
        carrier
            .CapturedTargetPresentPredicate.Should()
            .Be("COALESCE(current_setting('dms.composite_target_documentid', true), '') <> ''");
    }

    [Test]
    public void It_captures_the_sql_server_target_into_reserved_batch_local_variables()
    {
        var carrier = IRelationalCompositeCommandDialect.Create(SqlDialect.Mssql).Carrier;

        var sql = carrier.EmitCaptureTarget("d.[DocumentUuid] = @documentUuid_s0");

        sql.Should().Contain("WITH (UPDLOCK, ROWLOCK)");
        sql.Should().Contain("@dms_composite_target_documentid = d.[DocumentId]");
        carrier.DeclarationPrologue.Should().Contain("DECLARE @dms_composite_target_documentid BIGINT");
        carrier.CapturedTargetIdExpression.Should().Be("@dms_composite_target_documentid");
        carrier.CapturedTargetPresentPredicate.Should().Be("@dms_composite_target_documentid IS NOT NULL");
        // SqlClient rejects a batch-local sharing a name with a bound parameter, so these must never be
        // issued by the allocator.
        carrier
            .ReservedNames.Should()
            .Contain("dms_composite_target_documentid")
            .And.Contain("dms_composite_target_contentversion");
    }

    [Test]
    public void It_emits_the_sql_server_carrier_declaration_before_the_capture_statement()
    {
        var builder = new RelationalCompositeCommandBuilder(
            IRelationalCompositeCommandDialect.Create(SqlDialect.Mssql)
        );
        var parameter = new RelationalParameter(
            builder.Allocator.AllocateStatementScoped("documentUuid", 0),
            Guid.NewGuid()
        );
        builder.AppendCaptureTarget("d.[DocumentUuid] = @documentUuid_s0", [parameter]);

        var commandText = builder.Seal().Command.CommandText;

        commandText
            .IndexOf("DECLARE @dms_composite_target_documentid", StringComparison.Ordinal)
            .Should()
            .BeLessThan(commandText.IndexOf("WITH (UPDLOCK", StringComparison.Ordinal));
    }

    [Test]
    public void It_rejects_an_unsupported_dialect()
    {
        var act = () => IRelationalCompositeCommandDialect.Create((SqlDialect)999);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}

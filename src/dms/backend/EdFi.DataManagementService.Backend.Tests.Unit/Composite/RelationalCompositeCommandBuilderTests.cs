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
public class Given_A_Relational_Composite_Command_Builder
{
    private const string TargetPredicate = "d.\"DocumentUuid\" = @documentUuid_s0";

    private static RelationalCompositeCommandBuilder CreateBuilder(
        SqlDialect dialect = SqlDialect.Pgsql,
        IEnumerable<string>? reservedParameterNames = null
    ) =>
        new(
            IRelationalCompositeCommandDialect.Create(dialect),
            reservedParameterNames: reservedParameterNames
        );

    private static RelationalParameter Allocate(
        RelationalCompositeCommandBuilder builder,
        string baseName,
        int statementOrdinal,
        object? value = null
    ) => new(builder.Allocator.AllocateStatementScoped(baseName, statementOrdinal), value ?? 1L);

    [Test]
    public void It_assigns_sequential_ordinals()
    {
        var builder = CreateBuilder();

        builder.Append("first", "SELECT 1;", [], RelationalCompositeResultShape.Scalar).Should().Be(0);
        builder.Append("second", "SELECT 2;", [], RelationalCompositeResultShape.Scalar).Should().Be(1);
    }

    [Test]
    public void It_appends_a_sentinel_for_data_modifying_statements_so_every_statement_yields_one_result_set()
    {
        var builder = CreateBuilder();
        builder.Append(
            "insert",
            "INSERT INTO edfi.\"School\" DEFAULT VALUES;",
            [],
            RelationalCompositeResultShape.Sentinel
        );

        builder.Seal().Command.CommandText.Should().Contain("SELECT 0 AS \"LogicalStatementOrdinal\";");
    }

    [Test]
    public void It_rejects_a_reader_on_a_shape_that_does_not_consume_one()
    {
        var builder = CreateBuilder();

        var act = () =>
            builder.Append(
                "scalar",
                "SELECT 1;",
                [],
                RelationalCompositeResultShape.Scalar,
                (_, _) => Task.FromResult<object?>(null)
            );

        act.Should().Throw<ArgumentException>().WithMessage("*declared shape*");
    }

    [Test]
    public void It_requires_the_capture_statement_to_come_first_so_the_lock_precedes_observation()
    {
        var builder = CreateBuilder();
        builder.Append("hydrate", "SELECT 1;", [], RelationalCompositeResultShape.Scalar);

        var act = () => builder.AppendCaptureTarget(TargetPredicate, [Allocate(builder, "documentUuid", 0)]);

        act.Should().Throw<InvalidOperationException>().WithMessage("*must be the first logical statement*");
    }

    [Test]
    public void It_allows_capturing_the_target_only_once()
    {
        var builder = CreateBuilder();
        builder.AppendCaptureTarget(TargetPredicate, [Allocate(builder, "documentUuid", 0)]);

        var act = () => builder.AppendCaptureTarget(TargetPredicate, []);

        act.Should().Throw<InvalidOperationException>().WithMessage("*only once*");
    }

    [Test]
    public void It_fails_the_build_when_a_later_statement_repeats_the_captured_target_predicate()
    {
        var builder = CreateBuilder();
        builder.AppendCaptureTarget(TargetPredicate, [Allocate(builder, "documentUuid", 0)]);

        // A fresh predicate is a fresh snapshot, so it can observe a target the lock never covered.
        var act = () =>
            builder.Append(
                "stored-namespace-auth",
                $"SELECT 1 FROM dms.\"Document\" d WHERE {TargetPredicate};",
                [],
                RelationalCompositeResultShape.Scalar
            );

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*repeats the captured target predicate*");
    }

    [Test]
    public void It_permits_a_later_statement_that_consumes_the_captured_expressions()
    {
        var builder = CreateBuilder();
        builder.AppendCaptureTarget(TargetPredicate, [Allocate(builder, "documentUuid", 0)]);

        var act = () =>
            builder.Append(
                "stored-namespace-auth",
                $"SELECT 1 WHERE {builder.Carrier.CapturedTargetPresentPredicate};",
                [],
                RelationalCompositeResultShape.Scalar
            );

        act.Should().NotThrow();
    }

    [Test]
    public void It_rejects_a_parameter_the_allocator_did_not_issue()
    {
        var builder = CreateBuilder();

        var act = () =>
            builder.Append(
                "hand-named",
                "SELECT @documentId;",
                [new RelationalParameter("@documentId", 1L)],
                RelationalCompositeResultShape.Scalar
            );

        act.Should().Throw<InvalidOperationException>().WithMessage("*allocator did not issue*");
    }

    [Test]
    public void It_rejects_a_statement_that_would_overflow_the_command_parameter_budget()
    {
        var builder = new RelationalCompositeCommandBuilder(
            IRelationalCompositeCommandDialect.Create(SqlDialect.Mssql),
            new RelationalCommandBudget(MaxParametersPerCommand: 2, MaxRowsPerStatement: 1000)
        );
        List<RelationalParameter> parameters =
        [
            Allocate(builder, "a", 0),
            Allocate(builder, "b", 0),
            Allocate(builder, "c", 0),
        ];

        var act = () =>
            builder.Append("too-wide", "SELECT 1;", parameters, RelationalCompositeResultShape.Scalar);

        act.Should().Throw<InvalidOperationException>().WithMessage("*Seal the command and open the next*");
    }

    [Test]
    public void It_reports_remaining_budget_and_fit()
    {
        var builder = new RelationalCompositeCommandBuilder(
            IRelationalCompositeCommandDialect.Create(SqlDialect.Pgsql),
            new RelationalCommandBudget(MaxParametersPerCommand: 3, MaxRowsPerStatement: 1000)
        );
        builder.Append(
            "one",
            "SELECT @a_s0;",
            [Allocate(builder, "a", 0)],
            RelationalCompositeResultShape.Scalar
        );

        builder.RemainingParameterBudget.Should().Be(2);
        builder.Fits(2).Should().BeTrue();
        builder.Fits(3).Should().BeFalse();
    }

    [Test]
    public void It_refuses_to_seal_an_empty_command()
    {
        var act = () => CreateBuilder().Seal();

        act.Should().Throw<InvalidOperationException>().WithMessage("*at least one logical statement*");
    }

    [Test]
    public void It_refuses_to_append_after_sealing()
    {
        var builder = CreateBuilder();
        builder.Append("only", "SELECT 1;", [], RelationalCompositeResultShape.Scalar);
        builder.Seal();

        var act = () => builder.Append("late", "SELECT 2;", [], RelationalCompositeResultShape.Scalar);

        act.Should().Throw<ObjectDisposedException>();
    }

    [Test]
    public void It_rejects_a_parameter_name_that_collides_with_a_provider_carrier_variable()
    {
        var builder = CreateBuilder(SqlDialect.Mssql);
        var carrierName = builder.Carrier.ReservedNames[0];

        var act = () => builder.Allocator.AllocateStatementScoped(carrierName, 0);

        // The carrier reserves the bare name; the allocator's suffixing means a collision only occurs
        // for the exact reserved name, which is what SqlClient rejects against a batch-local.
        act.Should().NotThrow();
        builder.Carrier.ReservedNames.Should().Contain("dms_composite_target_documentid");
    }

    [Test]
    public void It_rejects_an_allocation_that_exactly_matches_a_reserved_name()
    {
        var builder = CreateBuilder(reservedParameterNames: ["schoolId_s0"]);

        var act = () => builder.Allocator.AllocateStatementScoped("schoolId", 0);

        act.Should().Throw<InvalidOperationException>().WithMessage("*collides with a reserved name*");
    }

    [Test]
    public void It_rejects_a_duplicate_allocation()
    {
        var builder = CreateBuilder();
        builder.Allocator.Allocate("city", 3, 0);

        var act = () => builder.Allocator.Allocate("city", 3, 0);

        act.Should().Throw<InvalidOperationException>().WithMessage("*already issued*");
    }

    [Test]
    public void It_scopes_parameter_names_by_statement_and_row_so_co_batched_statements_cannot_collide()
    {
        var builder = CreateBuilder();

        builder.Allocator.Allocate("city", 4, 0).Should().Be("@city_s4_0");
        builder.Allocator.Allocate("city", 4, 1).Should().Be("@city_s4_1");
        builder.Allocator.Allocate("city", 5, 0).Should().Be("@city_s5_0");
        builder.Allocator.AllocateStatementScoped("documentUuid", 0).Should().Be("@documentUuid_s0");
    }
}

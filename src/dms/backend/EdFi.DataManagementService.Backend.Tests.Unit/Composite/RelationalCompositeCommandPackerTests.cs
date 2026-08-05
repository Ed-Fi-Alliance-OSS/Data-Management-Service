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
public class Given_A_Relational_Composite_Command_Packer
{
    private static readonly RelationalCommandBudget _smallBudget = new(
        MaxParametersPerCommand: 10,
        MaxRowsPerStatement: 3
    );

    [Test]
    public void It_reads_dialect_ceilings_from_the_plan_layer_rather_than_a_local_copy()
    {
        RelationalCommandBudget.ForDialect(SqlDialect.Mssql).MaxParametersPerCommand.Should().Be(2098);
        RelationalCommandBudget.ForDialect(SqlDialect.Pgsql).MaxParametersPerCommand.Should().Be(65535);
        RelationalCommandBudget.ForDialect(SqlDialect.Mssql).MaxRowsPerStatement.Should().Be(1000);
        RelationalCommandBudget.ForDialect(SqlDialect.Pgsql).MaxRowsPerStatement.Should().Be(1000);
    }

    [Test]
    public void It_packs_several_tables_into_one_command_when_their_rows_fit()
    {
        var commands = RelationalCompositeCommandPacker.Pack(
            [
                new RelationalCompositePackUnit("insert:a", RowCount: 2, ParametersPerRow: 2),
                new RelationalCompositePackUnit("insert:b", RowCount: 1, ParametersPerRow: 2),
                new RelationalCompositePackUnit("insert:c", RowCount: 1, ParametersPerRow: 2),
            ],
            _smallBudget
        );

        commands.Should().HaveCount(1);
        commands[0]
            .Should()
            .BeEquivalentTo(
                new[]
                {
                    new RelationalCompositePackGroup("insert:a", 0, 2, 4),
                    new RelationalCompositePackGroup("insert:b", 0, 1, 2),
                    new RelationalCompositePackGroup("insert:c", 0, 1, 2),
                },
                options => options.WithStrictOrdering()
            );
    }

    [Test]
    public void It_does_not_grow_the_command_count_merely_because_more_tables_were_added()
    {
        var oneTable = RelationalCompositeCommandPacker.Pack(
            [new RelationalCompositePackUnit("insert:a", RowCount: 1, ParametersPerRow: 2)],
            _smallBudget
        );
        var fiveTables = RelationalCompositeCommandPacker.Pack(
            [
                new RelationalCompositePackUnit("insert:a", RowCount: 1, ParametersPerRow: 2),
                new RelationalCompositePackUnit("insert:b", RowCount: 1, ParametersPerRow: 2),
                new RelationalCompositePackUnit("insert:c", RowCount: 1, ParametersPerRow: 2),
                new RelationalCompositePackUnit("insert:d", RowCount: 1, ParametersPerRow: 2),
                new RelationalCompositePackUnit("insert:e", RowCount: 1, ParametersPerRow: 2),
            ],
            _smallBudget
        );

        // This is the invariant the story exists to protect: no per-table N+1.
        oneTable.Should().HaveCount(1);
        fiveTables.Should().HaveCount(1);
    }

    [Test]
    public void It_seals_and_opens_a_new_command_when_the_parameter_budget_is_exhausted()
    {
        var commands = RelationalCompositeCommandPacker.Pack(
            [
                new RelationalCompositePackUnit("insert:a", RowCount: 3, ParametersPerRow: 2),
                new RelationalCompositePackUnit("insert:b", RowCount: 3, ParametersPerRow: 2),
            ],
            _smallBudget
        );

        // Budget 10: a consumes 6, b's first two rows consume 4, b's last row starts a new command.
        commands.Should().HaveCount(2);
        commands[0]
            .Should()
            .BeEquivalentTo(
                new[]
                {
                    new RelationalCompositePackGroup("insert:a", 0, 3, 6),
                    new RelationalCompositePackGroup("insert:b", 0, 2, 4),
                },
                options => options.WithStrictOrdering()
            );
        commands[1].Should().BeEquivalentTo(new[] { new RelationalCompositePackGroup("insert:b", 2, 1, 2) });
    }

    [Test]
    public void It_splits_a_unit_at_the_row_cap_without_splitting_a_row()
    {
        var commands = RelationalCompositeCommandPacker.Pack(
            [new RelationalCompositePackUnit("insert:a", RowCount: 5, ParametersPerRow: 1)],
            _smallBudget
        );

        commands.Should().HaveCount(1);
        commands[0]
            .Should()
            .BeEquivalentTo(
                new[]
                {
                    new RelationalCompositePackGroup("insert:a", 0, 3, 3),
                    new RelationalCompositePackGroup("insert:a", 3, 2, 2),
                },
                options => options.WithStrictOrdering()
            );
    }

    [Test]
    public void It_starts_a_new_command_at_a_dependency_boundary()
    {
        var commands = RelationalCompositeCommandPacker.Pack(
            [
                new RelationalCompositePackUnit("insert:a", RowCount: 1, ParametersPerRow: 1),
                new RelationalCompositePackUnit(
                    "insert:aligned-extension",
                    RowCount: 1,
                    ParametersPerRow: 1,
                    StartsNewCommand: true
                ),
            ],
            _smallBudget
        );

        commands.Should().HaveCount(2);
        commands[0].Should().ContainSingle().Which.Label.Should().Be("insert:a");
        commands[1].Should().ContainSingle().Which.Label.Should().Be("insert:aligned-extension");
    }

    [Test]
    public void It_counts_fixed_parameters_against_the_budget()
    {
        var commands = RelationalCompositeCommandPacker.Pack(
            [
                new RelationalCompositePackUnit(
                    "insert:a",
                    RowCount: 2,
                    ParametersPerRow: 2,
                    FixedParameterCount: 1
                ),
            ],
            _smallBudget
        );

        commands.Should().HaveCount(1);
        commands[0].Should().ContainSingle().Which.ParameterCount.Should().Be(5);
    }

    [Test]
    public void It_drops_a_zero_row_unit_that_contributes_nothing()
    {
        var commands = RelationalCompositeCommandPacker.Pack(
            [
                new RelationalCompositePackUnit("insert:a", RowCount: 0, ParametersPerRow: 2),
                new RelationalCompositePackUnit("insert:b", RowCount: 1, ParametersPerRow: 2),
            ],
            _smallBudget
        );

        commands.Should().HaveCount(1);
        commands[0].Should().ContainSingle().Which.Label.Should().Be("insert:b");
    }

    [Test]
    public void It_keeps_a_zero_row_unit_that_still_binds_fixed_parameters()
    {
        var commands = RelationalCompositeCommandPacker.Pack(
            [
                new RelationalCompositePackUnit(
                    "delete-by-parent:a",
                    RowCount: 0,
                    ParametersPerRow: 0,
                    FixedParameterCount: 1
                ),
            ],
            _smallBudget
        );

        commands.Should().HaveCount(1);
        commands[0].Should().ContainSingle().Which.ParameterCount.Should().Be(1);
    }

    [Test]
    public void It_returns_no_commands_for_no_units()
    {
        RelationalCompositeCommandPacker.Pack([], _smallBudget).Should().BeEmpty();
    }

    [Test]
    public void It_fails_loudly_when_a_single_row_cannot_fit_an_empty_command()
    {
        var act = () =>
            RelationalCompositeCommandPacker.Pack(
                [new RelationalCompositePackUnit("insert:too-wide", RowCount: 1, ParametersPerRow: 11)],
                _smallBudget
            );

        act.Should().Throw<InvalidOperationException>().WithMessage("*cannot be packed*");
    }

    [Test]
    public void It_grows_the_command_count_monotonically_with_total_parameters()
    {
        var previousCount = 0;

        foreach (var rowCount in new[] { 1, 5, 9, 20, 45 })
        {
            var commands = RelationalCompositeCommandPacker.Pack(
                [new RelationalCompositePackUnit("insert:a", rowCount, ParametersPerRow: 2)],
                _smallBudget
            );

            commands.Count.Should().BeGreaterThanOrEqualTo(previousCount);
            previousCount = commands.Count;
        }
    }
}

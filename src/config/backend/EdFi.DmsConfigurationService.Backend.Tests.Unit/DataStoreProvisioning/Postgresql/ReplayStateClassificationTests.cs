// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Postgresql.DataStoreProvisioning;
using FluentAssertions;
using ReplayState = EdFi.DmsConfigurationService.Backend.Postgresql.DataStoreProvisioning.PostgresqlDmsTargetEngine.ReplayState;
using TableState = EdFi.DmsConfigurationService.Backend.Postgresql.DataStoreProvisioning.PostgresqlDmsTargetEngine.ReplayTableState;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.Postgresql;

/// <summary>
/// The replay-state table (6.3 step 5): only both tables existing is a commit and only both missing is a
/// rollback; every other combination, including an unreadable table, is inconclusive.
/// </summary>
public class ReplayStateClassificationTests
{
    [TestFixture]
    public class Given_every_combination_of_table_states
    {
        private readonly Dictionary<(TableState, TableState), ReplayState> _verdicts = [];

        [SetUp]
        public void Setup()
        {
            _verdicts.Clear();
            foreach (TableState identity in Enum.GetValues<TableState>())
            {
                foreach (TableState effectiveSchema in Enum.GetValues<TableState>())
                {
                    _verdicts[(identity, effectiveSchema)] = PostgresqlDmsTargetEngine.ClassifyReplayState(
                        identity,
                        effectiveSchema
                    );
                }
            }
        }

        [Test]
        public void It_reads_two_existing_tables_as_committed() =>
            _verdicts[(TableState.Exists, TableState.Exists)].Should().Be(ReplayState.Committed);

        [Test]
        public void It_reads_two_missing_tables_as_rolled_back() =>
            _verdicts[(TableState.Missing, TableState.Missing)].Should().Be(ReplayState.RolledBack);

        [Test]
        public void It_reads_every_other_combination_as_inconclusive() =>
            _verdicts
                .Where(verdict =>
                    verdict.Key
                        is not
                            (TableState.Exists, TableState.Exists)
                            and not
                            (TableState.Missing, TableState.Missing)
                )
                .Select(verdict => verdict.Value)
                .Should()
                .HaveCount(7)
                .And.OnlyContain(verdict => verdict == ReplayState.Inconclusive);
    }

    /// <summary>
    /// Review finding 1: a rolled-back replay proves that nothing committed, not that a retry can succeed. Only
    /// connection-class evidence names it <c>RestoreRolledBack</c> (transient in the orchestrator): a SQLSTATE that
    /// leaves the outcome in doubt, or psql's exit 2. A script error psql reported without a readable SQLSTATE,
    /// psql's own fatal error, and a process ended from outside are the replay's own step (permanent), and a
    /// stale code from an earlier notice does not count as connection evidence. The bound and the caller keep
    /// their own names.
    /// </summary>
    [TestFixture]
    public class Given_every_way_a_replay_found_rolled_back_can_have_ended
    {
        private readonly Dictionary<string, string> _steps = [];

        [SetUp]
        public void Setup()
        {
            _steps.Clear();
            (string Name, PsqlRunResult Run)[] runs =
            [
                ("connection failed or lost", new PsqlRunResult.Exited(1, 2, null)),
                ("connection lost after a notice", new PsqlRunResult.Exited(1, 2, "42710")),
                ("query cancelled", new PsqlRunResult.Exited(1, 3, "57014")),
                ("connection class", new PsqlRunResult.Exited(1, 3, "08006")),
                ("administrator shutdown", new PsqlRunResult.Exited(1, 2, "57P01")),
                ("script error without a readable sqlstate", new PsqlRunResult.Exited(1, 3, null)),
                ("psql fatal error", new PsqlRunResult.Exited(1, 1, null)),
                ("ended from outside after a notice", new PsqlRunResult.Exited(1, 137, "42710")),
                ("restore bound", new PsqlRunResult.Killed(1, PsqlKillReason.TimedOut)),
                ("caller cancellation", new PsqlRunResult.Killed(1, PsqlKillReason.Cancelled)),
            ];
            foreach ((string name, PsqlRunResult run) in runs)
            {
                _steps[name] = PostgresqlDmsTargetEngine.RolledBackStepOf(run);
            }
        }

        [Test]
        public void It_names_connection_evidence_as_a_retryable_rollback() =>
            new[]
            {
                "connection failed or lost",
                "connection lost after a notice",
                "query cancelled",
                "connection class",
                "administrator shutdown",
            }
                .Select(name => _steps[name])
                .Should()
                .AllBe(DmsEngineOperations.RestoreRolledBack);

        [Test]
        public void It_names_every_other_end_as_the_replay_step() =>
            new[]
            {
                "script error without a readable sqlstate",
                "psql fatal error",
                "ended from outside after a notice",
            }
                .Select(name => _steps[name])
                .Should()
                .AllBe(PsqlRestoreRunner.RestoreStep);

        [Test]
        public void It_keeps_the_bound_and_the_cancellation_names()
        {
            _steps["restore bound"].Should().Be(DmsEngineOperations.RestoreTimedOut);
            _steps["caller cancellation"].Should().Be(DmsEngineOperations.RestoreCancelled);
        }
    }
}

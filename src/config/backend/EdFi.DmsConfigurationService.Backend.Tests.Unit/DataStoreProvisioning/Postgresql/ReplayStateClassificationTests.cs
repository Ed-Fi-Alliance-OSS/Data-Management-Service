// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

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
}

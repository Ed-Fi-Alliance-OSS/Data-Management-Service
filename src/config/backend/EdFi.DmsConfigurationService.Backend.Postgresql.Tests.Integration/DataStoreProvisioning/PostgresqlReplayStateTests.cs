// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.Postgresql.DataStoreProvisioning;
using FluentAssertions;
using static EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration.DataStoreProvisioning.DmsProvisioningTestServer;
using ReplayState = EdFi.DmsConfigurationService.Backend.Postgresql.DataStoreProvisioning.PostgresqlDmsTargetEngine.ReplayState;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration.DataStoreProvisioning;

/// <summary>
/// The replay-state read the outcome protocol relies on (6.3 step 5, Step 3.5 review): a table that exists
/// is evidence of a commit whatever rows it holds, so two existing but empty singleton tables are a commit
/// for validation to judge, never a rollback; both missing is a rollback; one of each is inconclusive.
/// </summary>
[Category(Category)]
public class PostgresqlReplayStateTests
{
    [TestFixture]
    public class Given_staging_databases_in_each_replay_state
    {
        private readonly List<TestDatabase> _databases = [];
        private ReplayState _populated;
        private ReplayState _emptyTables;
        private ReplayState _noTables;
        private ReplayState _identityOnly;

        private async Task<ReplayState> StateOfAsync(string purpose, string? afterShape)
        {
            TestDatabase database = afterShape is null
                ? await CreateDatabaseAsync(purpose)
                : await CreateDmsShapedDatabaseAsync(purpose);
            _databases.Add(database);
            if (!string.IsNullOrEmpty(afterShape))
            {
                await database.ExecuteAsync(afterShape);
            }

            return await Engine().ReadReplayStateAsync(database.DatabaseName, default);
        }

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            _populated = await StateOfAsync("populated", string.Empty);
            _emptyTables = await StateOfAsync(
                "empty",
                "DELETE FROM \"dms\".\"DataStoreIdentity\"; DELETE FROM \"dms\".\"EffectiveSchema\";"
            );
            _noTables = await StateOfAsync("none", null);
            _identityOnly = await StateOfAsync("half", "DROP TABLE \"dms\".\"EffectiveSchema\";");
        }

        [OneTimeTearDown]
        public async Task Teardown()
        {
            foreach (TestDatabase database in _databases)
            {
                await database.DisposeAsync();
            }
        }

        [Test]
        public void It_reads_populated_tables_as_committed() => _populated.Should().Be(ReplayState.Committed);

        [Test]
        public void It_reads_existing_empty_tables_as_committed() =>
            _emptyTables.Should().Be(ReplayState.Committed);

        [Test]
        public void It_reads_missing_tables_as_rolled_back() => _noTables.Should().Be(ReplayState.RolledBack);

        [Test]
        public void It_reads_one_table_as_inconclusive() =>
            _identityOnly.Should().Be(ReplayState.Inconclusive);
    }
}

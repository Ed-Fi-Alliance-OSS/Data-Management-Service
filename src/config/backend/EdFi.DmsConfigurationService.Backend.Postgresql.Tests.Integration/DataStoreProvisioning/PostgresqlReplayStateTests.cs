// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Postgresql.DataStoreProvisioning;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
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

    /// <summary>
    /// Review finding 2: once every replay session is proven gone, the outcome reads run on a bound of their own.
    /// The test holds the identity table so the engine's read waits on it, observes that wait in <c>pg_locks</c>,
    /// and only then advances the fake clock: past <c>SessionTerminationTimeout</c> (5 s) but inside
    /// <c>CommandTimeout</c> (30 s), the read still completes and the committed instance comes back; past
    /// <c>CommandTimeout</c>, the read is cancelled and the outcome is unknown but still quiescent, because nothing
    /// of the replay remains to change the database.
    /// </summary>
    [TestFixture]
    public class Given_the_outcome_reads_of_a_replay_whose_sessions_are_proven_gone_are_held
    {
        private static readonly TimeSpan _sessionTerminationTimeout = TimeSpan.FromSeconds(5);

        private DmsInstanceIdentity.Postgresql _catalog = null!;
        private DmsCommandOutcome<DmsInstanceIdentity> _pastTerminationBound = null!;
        private DmsCommandOutcome<DmsInstanceIdentity> _pastCommandTimeout = null!;

        private static async Task<DmsCommandOutcome<DmsInstanceIdentity>> ResolveWhileHeldAsync(
            TestDatabase database,
            TimeSpan advance
        )
        {
            FakeTimeProvider time = new();
            PostgresqlDmsTargetEngine engine = Engine(
                timeProvider: time,
                sessionTerminationTimeout: _sessionTerminationTimeout
            );
            await using NpgsqlConnection holder = await AdminConnection()
                .OpenAsync(database.Name, "dms1438-test-holder", default);
            await using NpgsqlTransaction transaction = await holder.BeginTransactionAsync();
            await using (
                NpgsqlCommand hold = new(
                    "LOCK TABLE \"dms\".\"DataStoreIdentity\" IN ACCESS EXCLUSIVE MODE;",
                    holder,
                    transaction
                )
            )
            {
                await hold.ExecuteNonQueryAsync();
            }

            Task<DmsCommandOutcome<DmsInstanceIdentity>> resolving = engine.ResolveReplayAsync(
                database.DatabaseName,
                DmsOperationId.New(),
                new PsqlRunResult.Exited(1, PsqlRestoreRunner.ConnectionFailedExitCode, null)
            );
            bool waiting = await WaitUntilAsync(
                () =>
                    ScalarOnServerAsync<bool>(
                        "SELECT EXISTS (SELECT 1 FROM pg_catalog.pg_locks l JOIN pg_catalog.pg_stat_activity a ON a.pid = l.pid WHERE a.datname = @name AND a.application_name = @app AND NOT l.granted);",
                        ("name", database.Name),
                        ("app", PostgresqlAdminConnection.SessionApplicationName)
                    ),
                TimeSpan.FromSeconds(30)
            );
            waiting.Should().BeTrue("the engine's outcome read must be waiting on the held table");

            time.Advance(advance);
            await transaction.RollbackAsync();
            return await resolving.WaitAsync(TimeSpan.FromSeconds(30));
        }

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            await using TestDatabase database = await CreateDmsShapedDatabaseAsync("held");
            _catalog = await database.CatalogInstanceAsync();
            _pastTerminationBound = await ResolveWhileHeldAsync(database, TimeSpan.FromSeconds(10));
            _pastCommandTimeout = await ResolveWhileHeldAsync(database, TimeSpan.FromSeconds(31));
        }

        [Test]
        public void It_reads_the_committed_instance_after_the_termination_bound_elapsed() =>
            _pastTerminationBound
                .Should()
                .BeOfType<DmsCommandOutcome<DmsInstanceIdentity>.Succeeded>()
                .Which.Value.IsSameStableInstance(_catalog)
                .Should()
                .BeTrue();

        [Test]
        public void It_keeps_the_outcome_quiescent_when_its_own_read_bound_elapses() =>
            _pastCommandTimeout
                .Should()
                .BeOfType<DmsCommandOutcome<DmsInstanceIdentity>.Unknown>()
                .Which.Should()
                .BeEquivalentTo(new { Session = (DmsServerSessionId?)null, Quiescent = true });
    }
}

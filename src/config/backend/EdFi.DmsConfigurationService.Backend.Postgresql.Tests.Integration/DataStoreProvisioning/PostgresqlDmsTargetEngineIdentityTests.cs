// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Postgresql.DataStoreProvisioning;
using FluentAssertions;
using static EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration.DataStoreProvisioning.DmsProvisioningTestServer;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration.DataStoreProvisioning;

/// <summary>
/// The delivered identity rule (F-D1, F-P12) over hand-built tables: exactly one row, a <c>D</c>-format GUID,
/// not all zero; every other shape is a typed invalid read and nothing is mutated.
/// </summary>
[Category(Category)]
public class PostgresqlDmsTargetEngineIdentityTests
{
    [TestFixture]
    public class Given_identity_tables_of_every_shape
    {
        private static readonly Guid _valid = Guid.Parse("8a6f1c2e-3b4d-4e5f-9a1b-2c3d4e5f6a7b");

        private TestDatabase? _database;
        private DmsIdentityRead _noSchema = null!;
        private DmsIdentityRead _noTable = null!;
        private DmsIdentityRead _noRows = null!;
        private DmsIdentityRead _twoRows = null!;
        private DmsIdentityRead _unparsable = null!;
        private DmsIdentityRead _empty = null!;
        private DmsIdentityRead _found = null!;
        private DmsIdentityRead _columnMissing = null!;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            _database = await CreateDatabaseAsync("identity");
            _noSchema = await ReadAsync();
            await _database.ExecuteAsync("CREATE SCHEMA \"dms\";");
            _noTable = await ReadAsync();
            await _database.ExecuteAsync(
                "CREATE TABLE \"dms\".\"DataStoreIdentity\" (\"DataStoreIdentitySingletonId\" smallint NOT NULL, \"SourceIdentity\" text NOT NULL);"
            );
            _noRows = await ReadAsync();
            await _database.ExecuteAsync(
                $"INSERT INTO \"dms\".\"DataStoreIdentity\" VALUES (1, '{_valid:D}'), (2, '{Guid.NewGuid():D}');"
            );
            _twoRows = await ReadAsync();
            await _database.ExecuteAsync(
                "DELETE FROM \"dms\".\"DataStoreIdentity\"; INSERT INTO \"dms\".\"DataStoreIdentity\" VALUES (1, 'not-a-guid');"
            );
            _unparsable = await ReadAsync();
            await _database.ExecuteAsync(
                $"UPDATE \"dms\".\"DataStoreIdentity\" SET \"SourceIdentity\" = '{Guid.Empty:D}';"
            );
            _empty = await ReadAsync();
            await _database.ExecuteAsync(
                $"UPDATE \"dms\".\"DataStoreIdentity\" SET \"SourceIdentity\" = '{_valid:D}';"
            );
            _found = await ReadAsync();
            await _database.ExecuteAsync(
                "ALTER TABLE \"dms\".\"DataStoreIdentity\" DROP COLUMN \"SourceIdentity\";"
            );
            _columnMissing = await ReadAsync();
        }

        [OneTimeTearDown]
        public async Task Teardown()
        {
            if (_database is not null)
            {
                await _database.DisposeAsync();
            }
        }

        [Test]
        public void It_reports_a_missing_schema_as_table_missing() =>
            _noSchema.Should().Be(new DmsIdentityRead.Invalid(DmsIdentityReadProblem.TableMissing));

        [Test]
        public void It_reports_a_missing_table() =>
            _noTable.Should().Be(new DmsIdentityRead.Invalid(DmsIdentityReadProblem.TableMissing));

        [Test]
        public void It_reports_no_rows() =>
            _noRows.Should().Be(new DmsIdentityRead.Invalid(DmsIdentityReadProblem.NoRows));

        [Test]
        public void It_reports_two_rows() =>
            _twoRows.Should().Be(new DmsIdentityRead.Invalid(DmsIdentityReadProblem.MultipleRows));

        [Test]
        public void It_reports_an_unparsable_value() =>
            _unparsable.Should().Be(new DmsIdentityRead.Invalid(DmsIdentityReadProblem.Unparsable));

        [Test]
        public void It_reports_the_all_zero_guid() =>
            _empty.Should().Be(new DmsIdentityRead.Invalid(DmsIdentityReadProblem.EmptyGuid));

        [Test]
        public void It_finds_a_single_valid_identity() =>
            _found.Should().BeOfType<DmsIdentityRead.Found>().Which.SourceIdentity.Should().Be(_valid);

        [Test]
        public void It_reports_a_table_without_the_identity_column_as_column_missing() =>
            _columnMissing.Should().Be(new DmsIdentityRead.Invalid(DmsIdentityReadProblem.ColumnMissing));

        private async Task<DmsIdentityRead> ReadAsync()
        {
            PostgresqlDmsTargetEngine engine = Engine();
            DmsSessionOpenResult opened = await engine.OpenSessionAsync(
                _database!.DatabaseName,
                await _database.CatalogInstanceAsync(),
                default
            );
            await using DmsSession session = ((DmsSessionOpenResult.Opened)opened).Session;
            return await engine.ReadIdentityAsync(session, default);
        }
    }

    /// <summary>
    /// A lost connection is never read as malformed metadata: on a database whose identity column and effective-schema
    /// columns are both missing, reads on a session whose backend was terminated raise the connection failure, which
    /// the orchestrator classifies as loss or an unknown outcome, never as a typed invalid read.
    /// </summary>
    [TestFixture]
    public class Given_the_session_is_terminated_before_malformed_metadata_is_read
    {
        private TestDatabase? _database;
        private Exception? _identityRead;
        private Exception? _factsRead;
        private bool _sessionGone = true;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            _database = await CreateDmsShapedDatabaseAsync("lost");
            await _database.ExecuteAsync(
                "ALTER TABLE \"dms\".\"DataStoreIdentity\" DROP COLUMN \"SourceIdentity\"; ALTER TABLE \"dms\".\"EffectiveSchema\" DROP COLUMN \"ResourceKeySeedHash\";"
            );

            PostgresqlDmsTargetEngine engine = Engine();
            _identityRead = await ReadOnTerminatedSessionAsync(
                engine,
                session => engine.ReadIdentityAsync(session, default)
            );
            _factsRead = await ReadOnTerminatedSessionAsync(
                engine,
                session => engine.ReadTargetFactsAsync(session, default)
            );
        }

        [OneTimeTearDown]
        public async Task Teardown()
        {
            if (_database is not null)
            {
                await _database.DisposeAsync();
            }
        }

        [Test]
        public void It_raises_the_connection_failure_from_the_identity_read() =>
            _identityRead
                .Should()
                .NotBeNull()
                .And.Match<Exception>(exception => PostgresqlErrorClassifier.IsConnectionFailure(exception));

        [Test]
        public void It_raises_the_connection_failure_from_the_facts_read() =>
            _factsRead
                .Should()
                .NotBeNull()
                .And.Match<Exception>(exception => PostgresqlErrorClassifier.IsConnectionFailure(exception));

        [Test]
        public void It_terminated_each_session_before_reading() => _sessionGone.Should().BeTrue();

        private async Task<Exception?> ReadOnTerminatedSessionAsync(
            PostgresqlDmsTargetEngine engine,
            Func<DmsSession, Task> read
        )
        {
            DmsSessionOpenResult opened = await engine.OpenSessionAsync(
                _database!.DatabaseName,
                await _database.CatalogInstanceAsync(),
                default
            );
            await using DmsSession session = ((DmsSessionOpenResult.Opened)opened).Session;
            long pid = session.ServerSession.Id;
            await TerminateBackendAsync(pid);
            _sessionGone &= await WaitUntilAsync(
                async () => !await SessionExistsAsync(pid),
                TimeSpan.FromSeconds(30)
            );
            return await Record.ExceptionAsync(() => read(session));
        }
    }

    [TestFixture]
    public class Given_a_role_the_server_refuses_access_to_the_target
    {
        private static readonly DmsProvisioningDiagnostic _readRefused = new(
            typeof(Npgsql.PostgresException).FullName!,
            "42501",
            PostgresqlDmsTargetEngine.ReadIdentityOperation
        );

        private static readonly DmsProvisioningDiagnostic _connectRefused = new(
            typeof(Npgsql.PostgresException).FullName!,
            "42501",
            PostgresqlDmsTargetEngine.OpenSessionOperation
        );

        private static readonly DmsProvisioningDiagnostic _factsRefused = new(
            typeof(Npgsql.PostgresException).FullName!,
            "42501",
            PostgresqlDmsTargetEngine.ReadTargetFactsOperation
        );

        private TestDatabase? _database;
        private string? _role;
        private DmsIdentityRead _identity = null!;
        private DmsIdentityRead _grantedIdentity = null!;
        private Exception? _factsOutcome;
        private DmsSessionOpenResult _connect = null!;
        private Guid _identityAfter;
        private bool _roleSessionsGone;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            _database = await CreateDmsShapedDatabaseAsync("refused");
            DmsInstanceIdentity instance = await _database.CatalogInstanceAsync();

            // CONNECT through PUBLIC, nothing on the dms schema: the identity read is refused.
            _role = await CreateLoginRoleAsync("refused");
            PostgresqlDmsTargetEngine engine = EngineAs(_role);
            DmsSessionOpenResult opened = await engine.OpenSessionAsync(
                _database.DatabaseName,
                instance,
                default
            );
            await using (DmsSession session = ((DmsSessionOpenResult.Opened)opened).Session)
            {
                _identity = await engine.ReadIdentityAsync(session, default);
            }

            // The identity is readable, dms.EffectiveSchema is not: the facts read is refused.
            await _database.ExecuteAsync(
                $"GRANT USAGE ON SCHEMA \"dms\" TO {PostgresqlIdentifiers.Quote(_role)}; GRANT SELECT ON \"dms\".\"DataStoreIdentity\" TO {PostgresqlIdentifiers.Quote(_role)};"
            );
            DmsSessionOpenResult reopened = await engine.OpenSessionAsync(
                _database.DatabaseName,
                instance,
                default
            );
            await using (DmsSession session = ((DmsSessionOpenResult.Opened)reopened).Session)
            {
                _grantedIdentity = await engine.ReadIdentityAsync(session, default);
                _factsOutcome = await Record.ExceptionAsync(() =>
                    engine.ReadTargetFactsAsync(session, default)
                );
            }

            // CONNECT revoked from PUBLIC: the open itself is refused.
            string quoted = PostgresqlIdentifiers.Quote(_database.Name);
            await ExecuteOnServerAsync($"REVOKE CONNECT ON DATABASE {quoted} FROM PUBLIC;");
            try
            {
                _connect = await engine.OpenSessionAsync(_database.DatabaseName, instance, default);
            }
            finally
            {
                await ExecuteOnServerAsync($"GRANT CONNECT ON DATABASE {quoted} TO PUBLIC;");
            }

            string role = _role;
            _roleSessionsGone = await WaitUntilAsync(
                async () =>
                    await ScalarOnServerAsync<long>(
                        "SELECT COUNT(*) FROM pg_catalog.pg_stat_activity WHERE usename = @role;",
                        ("role", role)
                    ) == 0,
                TimeSpan.FromSeconds(20)
            );
            _identityAfter = await _database.ScalarAsync<Guid>(
                "SELECT \"SourceIdentity\" FROM \"dms\".\"DataStoreIdentity\";"
            );
        }

        [OneTimeTearDown]
        public async Task Teardown()
        {
            if (_database is not null)
            {
                await _database.DisposeAsync();
            }

            if (_role is not null)
            {
                await DropRoleAsync(_role);
            }
        }

        [Test]
        public void It_reports_a_refused_identity_read_as_access_denied_with_the_sqlstate_only()
        {
            DmsIdentityRead.Invalid invalid = _identity.Should().BeOfType<DmsIdentityRead.Invalid>().Subject;
            (invalid.Problem, invalid.Diagnostic)
                .Should()
                .Be((DmsIdentityReadProblem.AccessDenied, _readRefused));
        }

        [Test]
        public void It_reports_a_refused_connection_as_access_denied_with_the_sqlstate_only() =>
            _connect.Should().Be(new DmsSessionOpenResult.AccessDenied(_connectRefused));

        [Test]
        public void It_reports_a_refused_facts_read_as_access_denied_with_the_sqlstate_only()
        {
            _grantedIdentity
                .Should()
                .BeOfType<DmsIdentityRead.Found>()
                .Which.SourceIdentity.Should()
                .Be(DmsShapedManifest.SourceIdentity);
            DmsAccessDeniedException refused = _factsOutcome
                .Should()
                .BeOfType<DmsAccessDeniedException>()
                .Subject;
            (refused.Diagnostic, refused.InnerException).Should().Be((_factsRefused, (Exception?)null));
        }

        [Test]
        public void It_leaves_no_session_of_the_role_and_the_identity_unchanged() =>
            (_roleSessionsGone, _identityAfter).Should().Be((true, DmsShapedManifest.SourceIdentity));
    }
}

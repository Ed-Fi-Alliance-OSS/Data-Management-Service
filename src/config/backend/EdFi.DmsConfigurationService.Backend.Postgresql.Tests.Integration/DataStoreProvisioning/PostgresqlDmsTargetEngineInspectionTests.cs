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
/// The engine's server-level reads against a live server: server facts (6.1 step 5), inspection by name
/// with the protected and fenced classifications and the <c>oid</c> plus claim instance identity (6.1 step
/// 6, D-13), and instance-bound sessions that verify the instance at open and capture the backend id (D-7b).
/// </summary>
[Category(Category)]
public class PostgresqlDmsTargetEngineInspectionTests
{
    private static DmsDatabaseName Name(string value) =>
        DmsDatabaseName.TryCreate(value, out DmsDatabaseName name)
            ? name
            : throw new InvalidOperationException("The name does not satisfy the grammar.");

    [TestFixture]
    public class Given_server_facts
    {
        private DmsServerFacts _facts = null!;
        private int _serverVersionNum;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            _facts = await Engine().ReadServerFactsAsync(default);
            _serverVersionNum = await ScalarOnServerAsync<int>(
                "SELECT current_setting('server_version_num')::int;"
            );
        }

        [Test]
        public void It_derives_the_major_from_server_version_num() =>
            _facts.MajorVersion.Should().Be(_serverVersionNum / 10000);

        [Test]
        public void It_reports_no_default_paths() =>
            (_facts.DefaultDataPath, _facts.DefaultLogPath).Should().Be((null, null));
    }

    [TestFixture]
    public class Given_inspection_of_names
    {
        private static readonly DmsProvisionClaim _claim = new(DmsOperationId.New(), Guid.NewGuid());

        private TestDatabase? _claimed;
        private TestDatabase? _fenced;
        private DmsInstanceIdentity.Postgresql _claimedInstance = null!;
        private DmsInstanceIdentity.Postgresql _fencedInstance = null!;
        private uint _postgresOid;
        private DmsLiveTargetState _template1 = null!;
        private DmsLiveTargetState _template0 = null!;
        private DmsLiveTargetState _postgres = null!;
        private DmsLiveTargetState _absent = null!;
        private DmsLiveTargetState _claimedState = null!;
        private DmsLiveTargetState _fencedState = null!;
        private DmsProvisionClaim? _claimReadBack;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            _claimed = await CreateDatabaseAsync("claimed");
            await ExecuteOnServerAsync(
                $"COMMENT ON DATABASE {PostgresqlIdentifiers.Quote(_claimed.Name)} IS {PostgresqlIdentifiers.Literal(_claim.Text)};"
            );
            _fenced = await CreateDatabaseAsync("fenced");
            await ExecuteOnServerAsync(
                $"ALTER DATABASE {PostgresqlIdentifiers.Quote(_fenced.Name)} WITH ALLOW_CONNECTIONS false;"
            );
            _claimedInstance = await _claimed.CatalogInstanceAsync();
            _fencedInstance = await _fenced.CatalogInstanceAsync();
            _postgresOid = await ScalarOnServerAsync<uint>(
                "SELECT oid FROM pg_database WHERE datname = 'postgres';"
            );

            PostgresqlDmsTargetEngine engine = Engine();
            _template1 = await engine.InspectAsync(Name("template1"), default);
            _template0 = await engine.InspectAsync(Name("template0"), default);
            _postgres = await engine.InspectAsync(Name("postgres"), default);
            _absent = await engine.InspectAsync(Name("dms1438_absent_000000000000"), default);
            _claimedState = await engine.InspectAsync(_claimed.DatabaseName, default);
            DmsProvisionClaim.TryParse(
                (_claimedState as DmsLiveTargetState.Present)?.Instance is DmsInstanceIdentity.Postgresql read
                    ? read.Claim
                    : null,
                out _claimReadBack
            );
            _fencedState = await engine.InspectAsync(_fenced.DatabaseName, default);
        }

        [OneTimeTearDown]
        public async Task Teardown()
        {
            if (_claimed is not null)
            {
                await _claimed.DisposeAsync();
            }

            if (_fenced is not null)
            {
                await _fenced.DisposeAsync();
            }
        }

        [Test]
        public void It_reports_template1_as_protected() =>
            _template1.Should().BeOfType<DmsLiveTargetState.Protected>();

        [Test]
        public void It_reports_template0_as_protected_rather_than_fenced() =>
            _template0.Should().BeOfType<DmsLiveTargetState.Protected>();

        [Test]
        public void It_reports_the_maintenance_database_as_present_and_online() =>
            _postgres
                .Should()
                .BeOfType<DmsLiveTargetState.Present>()
                .Which.Should()
                .BeEquivalentTo(
                    new DmsLiveTargetState.Present(
                        new DmsInstanceIdentity.Postgresql(_postgresOid, string.Empty),
                        DmsDatabaseState.Online
                    )
                );

        [Test]
        public void It_reports_an_unknown_name_as_absent() =>
            _absent.Should().BeOfType<DmsLiveTargetState.Absent>();

        [Test]
        public void It_reports_a_claimed_database_with_its_oid_and_claim() =>
            _claimedState
                .Should()
                .BeOfType<DmsLiveTargetState.Present>()
                .Which.Instance.Should()
                .Be(_claimedInstance);

        [Test]
        public void It_reads_the_claim_back_verbatim() => _claimReadBack.Should().Be(_claim);

        [Test]
        public void It_reports_a_database_refusing_connections_as_fenced() =>
            _fencedState
                .Should()
                .BeOfType<DmsLiveTargetState.Fenced>()
                .Which.Instance.Should()
                .Be(_fencedInstance);
    }

    [TestFixture]
    public class Given_sessions_opened_against_an_instance
    {
        private TestDatabase? _database;
        private DmsInstanceIdentity.Postgresql _instance = null!;
        private DmsSessionOpenResult _opened = null!;
        private DmsSession? _session;
        private DateTime _backendStart;
        private string _applicationName = string.Empty;
        private string _databaseName = string.Empty;
        private DmsSessionOpenResult _oidMismatch = null!;
        private DmsSessionOpenResult _claimMismatch = null!;
        private DmsSessionOpenResult _absent = null!;
        private Exception? _wrongEngine;
        private bool _onlyTheOpenedSessionRemains;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            _database = await CreateDatabaseAsync("session");
            await ExecuteOnServerAsync(
                $"COMMENT ON DATABASE {PostgresqlIdentifiers.Quote(_database.Name)} IS 'edfi-dms-provision:v1:0123456789ab:{Guid.NewGuid():D}';"
            );
            _instance = await _database.CatalogInstanceAsync();

            PostgresqlDmsTargetEngine engine = Engine();
            _opened = await engine.OpenSessionAsync(_database.DatabaseName, _instance, default);
            _session = (_opened as DmsSessionOpenResult.Opened)?.Session;
            if (_session is not null)
            {
                _backendStart = await ScalarOnServerAsync<DateTime>(
                    "SELECT backend_start FROM pg_stat_activity WHERE pid = @pid;",
                    ("pid", (int)_session.ServerSession.Id)
                );
                _applicationName = await ScalarOnServerAsync<string>(
                    "SELECT application_name FROM pg_stat_activity WHERE pid = @pid;",
                    ("pid", (int)_session.ServerSession.Id)
                );
                _databaseName = await ScalarOnServerAsync<string>(
                    "SELECT datname FROM pg_stat_activity WHERE pid = @pid;",
                    ("pid", (int)_session.ServerSession.Id)
                );
            }

            _oidMismatch = await engine.OpenSessionAsync(
                _database.DatabaseName,
                _instance with
                {
                    Oid = _instance.Oid + 1,
                },
                default
            );
            _claimMismatch = await engine.OpenSessionAsync(
                _database.DatabaseName,
                _instance with
                {
                    Claim = string.Empty,
                },
                default
            );
            _absent = await engine.OpenSessionAsync(Name("dms1438_absent_000000000000"), _instance, default);
            _wrongEngine = await Record.ExceptionAsync(() =>
                engine.OpenSessionAsync(
                    _database.DatabaseName,
                    new DmsInstanceIdentity.Mssql(5, "__0123456789ab", DateTimeOffset.UnixEpoch),
                    default
                )
            );
            _onlyTheOpenedSessionRemains = await WaitUntilAsync(
                async () => await _database.EngineSessionCountAsync() == 1,
                TimeSpan.FromSeconds(10)
            );
        }

        [OneTimeTearDown]
        public async Task Teardown()
        {
            if (_session is not null)
            {
                await _session.DisposeAsync();
            }

            if (_database is not null)
            {
                await _database.DisposeAsync();
            }
        }

        [Test]
        public void It_opens_against_the_matching_instance() =>
            _opened
                .Should()
                .BeOfType<DmsSessionOpenResult.Opened>()
                .Which.Session.Instance.Should()
                .Be(_instance);

        [Test]
        public void It_captures_the_backend_pid_and_start_time() =>
            _session!.ServerSession.StartedAt.Should().Be(new DateTimeOffset(_backendStart, TimeSpan.Zero));

        [Test]
        public void It_names_the_session() =>
            _applicationName.Should().Be(PostgresqlAdminConnection.SessionApplicationName);

        [Test]
        public void It_binds_the_session_to_the_database() => _databaseName.Should().Be(_database!.Name);

        [Test]
        public void It_refuses_a_different_oid() =>
            _oidMismatch.Should().BeOfType<DmsSessionOpenResult.InstanceChanged>();

        [Test]
        public void It_refuses_a_different_claim() =>
            _claimMismatch.Should().BeOfType<DmsSessionOpenResult.InstanceChanged>();

        [Test]
        public void It_reports_a_missing_database_as_absent() =>
            _absent.Should().BeOfType<DmsSessionOpenResult.Absent>();

        [Test]
        public void It_refuses_a_sql_server_identity() => _wrongEngine.Should().BeOfType<ArgumentException>();

        [Test]
        public void It_keeps_nothing_open_after_a_refusal() => _onlyTheOpenedSessionRemains.Should().BeTrue();
    }
}

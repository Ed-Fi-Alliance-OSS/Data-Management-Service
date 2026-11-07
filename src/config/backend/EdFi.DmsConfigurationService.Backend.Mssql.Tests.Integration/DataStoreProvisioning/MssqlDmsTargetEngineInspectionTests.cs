// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Mssql.DataStoreProvisioning;
using FluentAssertions;
using static EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration.DataStoreProvisioning.MssqlProvisioningTestServer;

namespace EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration.DataStoreProvisioning;

/// <summary>
/// The engine's server-level reads against a live server: server facts with the default paths and the UTC
/// clock (6.1 step 5, 6.4 step 2), inspection by name with the protected and fenced classifications and the
/// <c>database_id</c> plus primary file plus <c>create_date</c> instance identity (6.1 step 6, D-13), the
/// identity a rename keeps and the snapshot it changes (F-P17), and instance-bound sessions that verify the
/// instance at open and capture <c>@@SPID</c> and <c>login_time</c> (D-7b).
/// </summary>
[Category(Category)]
public class MssqlDmsTargetEngineInspectionTests
{
    [TestFixture]
    public class Given_server_facts
    {
        private DmsServerFacts _facts = null!;
        private string _productVersion = string.Empty;
        private int _productMajor;
        private string _dataPath = string.Empty;
        private string _logPath = string.Empty;
        private DateTime _serverUtcNow;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            _facts = await Engine().ReadServerFactsAsync(default);
            _productVersion = await ScalarOnServerAsync<string>(
                "SELECT CONVERT(nvarchar(128), SERVERPROPERTY('ProductVersion'));"
            );
            _productMajor = await ScalarOnServerAsync<int>(
                "SELECT CONVERT(int, SERVERPROPERTY('ProductMajorVersion'));"
            );
            _dataPath = await ScalarOnServerAsync<string>(
                "SELECT CONVERT(nvarchar(4000), SERVERPROPERTY('InstanceDefaultDataPath'));"
            );
            _logPath = await ScalarOnServerAsync<string>(
                "SELECT CONVERT(nvarchar(4000), SERVERPROPERTY('InstanceDefaultLogPath'));"
            );
            _serverUtcNow = await ScalarOnServerAsync<DateTime>("SELECT SYSUTCDATETIME();");
        }

        [Test]
        public void It_reports_the_product_version() => _facts.ProductVersion.Should().Be(_productVersion);

        [Test]
        public void It_reports_the_product_major_version() => _facts.MajorVersion.Should().Be(_productMajor);

        [Test]
        public void It_reports_the_default_data_and_log_paths() =>
            (_facts.DefaultDataPath, _facts.DefaultLogPath).Should().Be((_dataPath, _logPath));

        [Test]
        public void It_reports_no_server_encoding() => _facts.ServerEncoding.Should().BeNull();

        [Test]
        public void It_reports_the_server_clock_in_utc() =>
            _facts
                .ServerUtcNow.Should()
                .BeCloseTo(new DateTimeOffset(_serverUtcNow, TimeSpan.Zero), TimeSpan.FromMinutes(5));
    }

    [TestFixture]
    public class Given_inspection_of_names
    {
        private readonly Dictionary<string, DmsLiveTargetState> _systemDatabases = new(
            StringComparer.Ordinal
        );
        private readonly List<TestDatabase> _databases = [];
        private DmsLiveTargetState _absent = null!;
        private DmsInstanceIdentity.Mssql _plainInstance = null!;
        private DmsLiveTargetState _plain = null!;
        private readonly Dictionary<
            string,
            (DmsInstanceIdentity.Mssql Catalog, DmsLiveTargetState Inspected)
        > _fenced = new(StringComparer.Ordinal);
        private DmsInstanceIdentity.Mssql _emergencyInstance = null!;
        private DmsLiveTargetState _emergency = null!;
        private string _emergencyUserAccess = string.Empty;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            MssqlDmsTargetEngine engine = Engine();
            foreach (string system in new[] { "master", "tempdb", "model", "msdb" })
            {
                _systemDatabases[system] = await engine.InspectAsync(DatabaseNamed(system), default);
            }

            _absent = await engine.InspectAsync(DatabaseNamed("dms1438_absent_000000000000"), default);

            TestDatabase plain = await Track(CreateDatabaseAsync("plain"));
            _plainInstance = await plain.CatalogInstanceAsync();
            _plain = await engine.InspectAsync(plain.DatabaseName, default);

            await FenceAsync(
                engine,
                "SINGLE_USER",
                "single",
                db => $"ALTER DATABASE {db} SET SINGLE_USER WITH ROLLBACK IMMEDIATE;"
            );
            await FenceAsync(
                engine,
                "RESTRICTED_USER",
                "restricted",
                db => $"ALTER DATABASE {db} SET RESTRICTED_USER WITH ROLLBACK IMMEDIATE;"
            );
            await FenceAsync(
                engine,
                "OFFLINE",
                "offline",
                db => $"ALTER DATABASE {db} SET OFFLINE WITH ROLLBACK IMMEDIATE;"
            );
            string fullBackup = MssqlIdentifiers.Literal(
                await BackupPathAsync("dms1438_inspection_restoring.bak")
            );
            string tailBackup = MssqlIdentifiers.Literal(
                await BackupPathAsync("dms1438_inspection_restoring.trn")
            );
            await FenceAsync(
                engine,
                "RESTORING",
                "restoring",
                db =>
                    $"ALTER DATABASE {db} SET RECOVERY FULL; BACKUP DATABASE {db} TO DISK = {fullBackup} WITH INIT, FORMAT; BACKUP LOG {db} TO DISK = {tailBackup} WITH INIT, FORMAT, NORECOVERY;"
            );

            TestDatabase emergency = await Track(CreateDatabaseAsync("emergency"));
            await ExecuteOnServerAsync(
                $"ALTER DATABASE {MssqlIdentifiers.Quote(emergency.Name)} SET EMERGENCY;"
            );
            _emergencyInstance = await emergency.CatalogInstanceAsync();
            _emergency = await engine.InspectAsync(emergency.DatabaseName, default);
            _emergencyUserAccess = await ScalarOnServerAsync<string>(
                "SELECT user_access_desc FROM sys.databases WHERE name = @name;",
                ("@name", emergency.Name)
            );
        }

        private async Task<TestDatabase> Track(Task<TestDatabase> creating)
        {
            TestDatabase database = await creating;
            _databases.Add(database);
            return database;
        }

        private async Task FenceAsync(
            MssqlDmsTargetEngine engine,
            string key,
            string purpose,
            Func<string, string> fence
        )
        {
            TestDatabase database = await Track(CreateDatabaseAsync(purpose));
            DmsInstanceIdentity.Mssql catalog = await database.CatalogInstanceAsync();
            await ExecuteOnServerAsync(fence(MssqlIdentifiers.Quote(database.Name)));
            _fenced[key] = (catalog, await engine.InspectAsync(database.DatabaseName, default));
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
        public void It_reports_every_system_database_as_protected() =>
            _systemDatabases
                .Should()
                .AllSatisfy(pair => pair.Value.Should().BeOfType<DmsLiveTargetState.Protected>(pair.Key));

        [Test]
        public void It_reports_an_unknown_name_as_absent() =>
            _absent.Should().BeOfType<DmsLiveTargetState.Absent>();

        [Test]
        public void It_reports_a_plain_database_as_present_and_online_with_its_catalog_identity() =>
            _plain.Should().Be(new DmsLiveTargetState.Present(_plainInstance, DmsDatabaseState.Online));

        [Test]
        public void It_reads_the_primary_file_as_the_stable_file_evidence() =>
            _plainInstance.PrimaryFileTag.Should().EndWith(".mdf");

        [Test]
        public void It_reports_each_fencing_state_as_fenced_with_the_unchanged_identity() =>
            _fenced
                .Should()
                .HaveCount(4)
                .And.AllSatisfy(pair =>
                    pair.Value.Inspected.Should()
                        .Be(new DmsLiveTargetState.Fenced(pair.Value.Catalog), pair.Key)
                );

        [Test]
        public void It_reports_another_non_online_state_as_present_in_another_state() =>
            (_emergencyUserAccess, _emergency)
                .Should()
                .Be(
                    ("MULTI_USER", new DmsLiveTargetState.Present(_emergencyInstance, DmsDatabaseState.Other))
                );
    }

    [TestFixture]
    public class Given_a_database_renamed_after_inspection
    {
        private TestDatabase? _database;
        private DmsInstanceIdentity.Mssql _before = null!;
        private DmsInstanceIdentity.Mssql _after = null!;
        private DmsSessionOpenResult _staleSnapshot = null!;
        private DmsSessionOpenResult _freshSnapshot = null!;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            MssqlDmsTargetEngine engine = Engine();
            TestDatabase original = await CreateDatabaseAsync("renamefrom");
            _database = original;
            _before = (DmsInstanceIdentity.Mssql)
                (
                    (DmsLiveTargetState.Present)await engine.InspectAsync(original.DatabaseName, default)
                ).Instance;

            // create_date has a resolution of about 3 ms: the rename waits until the server's own clock has
            // passed the creation time, so an unchanged snapshot can only mean the rename kept it.
            DateTime created = _before.CreateDateSnapshot.UtcDateTime;
            bool clockMoved = await WaitUntilAsync(
                async () =>
                    await ScalarOnServerAsync<DateTime>("SELECT GETDATE();") > created.AddMilliseconds(10),
                TimeSpan.FromSeconds(10)
            );
            clockMoved.Should().BeTrue();
            _database = await original.RenameAsync(UniqueName("renameto"));
            _after = (DmsInstanceIdentity.Mssql)
                (
                    (DmsLiveTargetState.Present)await engine.InspectAsync(_database.DatabaseName, default)
                ).Instance;

            _staleSnapshot = await engine.OpenSessionAsync(_database.DatabaseName, _before, default);
            _freshSnapshot = await engine.OpenSessionAsync(_database.DatabaseName, _after, default);
            if (_freshSnapshot is DmsSessionOpenResult.Opened opened)
            {
                await opened.Session.DisposeAsync();
            }
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
        public void It_keeps_the_database_id_and_primary_file_across_the_rename() =>
            _after.IsSameStableInstance(_before).Should().BeTrue();

        [Test]
        public void It_changes_the_create_date_snapshot() =>
            _after.CreateDateSnapshot.Should().NotBe(_before.CreateDateSnapshot);

        [Test]
        public void It_refuses_a_session_against_the_pre_rename_snapshot() =>
            _staleSnapshot.Should().BeOfType<DmsSessionOpenResult.InstanceChanged>();

        [Test]
        public void It_opens_a_session_against_the_re_captured_snapshot() =>
            _freshSnapshot.Should().BeOfType<DmsSessionOpenResult.Opened>();
    }

    [TestFixture]
    public class Given_sessions_opened_against_an_instance
    {
        private TestDatabase? _database;
        private DmsInstanceIdentity.Mssql _instance = null!;
        private DmsSessionOpenResult _opened = null!;
        private DmsSession? _session;
        private DateTime _loginTime;
        private string _programName = string.Empty;
        private int _boundDatabaseId;
        private DmsSessionOpenResult _idMismatch = null!;
        private DmsSessionOpenResult _fileMismatch = null!;
        private DmsSessionOpenResult _snapshotMismatch = null!;
        private DmsSessionOpenResult _absent = null!;
        private Exception? _wrongEngine;
        private bool _onlyTheOpenedSessionRemains;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            _database = await CreateDatabaseAsync("session");
            _instance = await _database.CatalogInstanceAsync();

            MssqlDmsTargetEngine engine = Engine();
            _opened = await engine.OpenSessionAsync(_database.DatabaseName, _instance, default);
            _session = (_opened as DmsSessionOpenResult.Opened)?.Session;
            if (_session is not null)
            {
                (string Name, object Value) spid = ("@spid", (int)_session.ServerSession.Id);
                _loginTime = await ScalarOnServerAsync<DateTime>(
                    "SELECT login_time FROM sys.dm_exec_sessions WHERE session_id = @spid;",
                    spid
                );
                _programName = await ScalarOnServerAsync<string>(
                    "SELECT program_name FROM sys.dm_exec_sessions WHERE session_id = @spid;",
                    spid
                );
                _boundDatabaseId = await ScalarOnServerAsync<int>(
                    "SELECT CONVERT(int, database_id) FROM sys.dm_exec_sessions WHERE session_id = @spid;",
                    spid
                );
            }

            _idMismatch = await engine.OpenSessionAsync(
                _database.DatabaseName,
                _instance with
                {
                    DatabaseId = _instance.DatabaseId + 1000,
                },
                default
            );
            _fileMismatch = await engine.OpenSessionAsync(
                _database.DatabaseName,
                _instance with
                {
                    PrimaryFileTag = _instance.PrimaryFileTag + ".old",
                },
                default
            );
            _snapshotMismatch = await engine.OpenSessionAsync(
                _database.DatabaseName,
                _instance.WithCreateDateSnapshot(_instance.CreateDateSnapshot.AddSeconds(-1)),
                default
            );
            _absent = await engine.OpenSessionAsync(
                DatabaseNamed("dms1438_absent_000000000000"),
                _instance,
                default
            );
            try
            {
                await engine.OpenSessionAsync(
                    _database.DatabaseName,
                    new DmsInstanceIdentity.Postgresql(16384, string.Empty),
                    default
                );
            }
            catch (ArgumentException exception)
            {
                _wrongEngine = exception;
            }

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
        public void It_captures_the_session_login_time() =>
            _session!.ServerSession.StartedAt.Should().Be(new DateTimeOffset(_loginTime, TimeSpan.Zero));

        [Test]
        public void It_names_the_session() =>
            _programName.Should().Be(MssqlAdminConnection.SessionApplicationName);

        [Test]
        public void It_binds_the_session_to_the_database() =>
            _boundDatabaseId.Should().Be(_instance.DatabaseId);

        [Test]
        public void It_refuses_a_different_database_id() =>
            _idMismatch.Should().BeOfType<DmsSessionOpenResult.InstanceChanged>();

        [Test]
        public void It_refuses_a_different_primary_file() =>
            _fileMismatch.Should().BeOfType<DmsSessionOpenResult.InstanceChanged>();

        [Test]
        public void It_refuses_a_different_create_date_snapshot() =>
            _snapshotMismatch.Should().BeOfType<DmsSessionOpenResult.InstanceChanged>();

        [Test]
        public void It_reports_a_missing_database_as_absent() =>
            _absent.Should().BeOfType<DmsSessionOpenResult.Absent>();

        [Test]
        public void It_refuses_a_postgresql_identity() => _wrongEngine.Should().NotBeNull();

        [Test]
        public void It_keeps_nothing_open_after_a_refusal() => _onlyTheOpenedSessionRemains.Should().BeTrue();
    }
}

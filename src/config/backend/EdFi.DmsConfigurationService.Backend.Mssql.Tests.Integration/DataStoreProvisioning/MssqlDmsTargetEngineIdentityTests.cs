// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Mssql.DataStoreProvisioning;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using static EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration.DataStoreProvisioning.MssqlProvisioningTestServer;

namespace EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration.DataStoreProvisioning;

/// <summary>
/// The delivered identity rule (F-D1, F-P12) over hand-built tables: exactly one row, a <c>D</c>-format GUID,
/// not all zero; every other shape is a typed invalid read and nothing is mutated. A server refusal of the
/// read, of the facts read, or of the connection itself is reported as an access refusal carrying the error
/// number only.
/// </summary>
[Category(Category)]
public class MssqlDmsTargetEngineIdentityTests
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
        private DmsIdentityRead _guidWithSuffix = null!;
        private DmsIdentityRead _empty = null!;
        private DmsIdentityRead _found = null!;
        private DmsIdentityRead _foundTyped = null!;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            _database = await CreateDatabaseAsync("identity");
            _noSchema = await ReadAsync();
            await _database.ExecuteAsync("CREATE SCHEMA [dms];");
            _noTable = await ReadAsync();
            await _database.ExecuteAsync(
                "CREATE TABLE [dms].[DataStoreIdentity] ([DataStoreIdentitySingletonId] smallint NOT NULL, [SourceIdentity] nvarchar(100) NOT NULL);"
            );
            _noRows = await ReadAsync();
            await _database.ExecuteAsync(
                $"INSERT INTO [dms].[DataStoreIdentity] VALUES (1, N'{_valid:D}'), (2, N'{Guid.NewGuid():D}');"
            );
            _twoRows = await ReadAsync();
            await _database.ExecuteAsync(
                "DELETE FROM [dms].[DataStoreIdentity]; INSERT INTO [dms].[DataStoreIdentity] VALUES (1, N'not-a-guid');"
            );
            _unparsable = await ReadAsync();
            await _database.ExecuteAsync(
                $"UPDATE [dms].[DataStoreIdentity] SET [SourceIdentity] = N'{_valid:D}-tail';"
            );
            _guidWithSuffix = await ReadAsync();
            await _database.ExecuteAsync(
                $"UPDATE [dms].[DataStoreIdentity] SET [SourceIdentity] = N'{Guid.Empty:D}';"
            );
            _empty = await ReadAsync();
            await _database.ExecuteAsync(
                $"UPDATE [dms].[DataStoreIdentity] SET [SourceIdentity] = N'{_valid:D}';"
            );
            _found = await ReadAsync();
            await _database.ExecuteAsync(
                $"DROP TABLE [dms].[DataStoreIdentity]; CREATE TABLE [dms].[DataStoreIdentity] ([DataStoreIdentitySingletonId] smallint NOT NULL, [SourceIdentity] uniqueidentifier NOT NULL); INSERT INTO [dms].[DataStoreIdentity] VALUES (1, '{_valid:D}');"
            );
            _foundTyped = await ReadAsync();
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
        public void It_never_truncates_a_longer_value_into_a_valid_guid() =>
            _guidWithSuffix.Should().Be(new DmsIdentityRead.Invalid(DmsIdentityReadProblem.Unparsable));

        [Test]
        public void It_reports_the_all_zero_guid() =>
            _empty.Should().Be(new DmsIdentityRead.Invalid(DmsIdentityReadProblem.EmptyGuid));

        [Test]
        public void It_finds_a_single_valid_identity_held_as_text() =>
            _found.Should().Be(new DmsIdentityRead.Found(_valid));

        [Test]
        public void It_finds_a_single_valid_identity_held_as_a_uniqueidentifier() =>
            _foundTyped.Should().Be(new DmsIdentityRead.Found(_valid));

        private async Task<DmsIdentityRead> ReadAsync()
        {
            MssqlDmsTargetEngine engine = Engine();
            DmsSessionOpenResult opened = await engine.OpenSessionAsync(
                _database!.DatabaseName,
                await _database.CatalogInstanceAsync(),
                default
            );
            await using DmsSession session = ((DmsSessionOpenResult.Opened)opened).Session;
            return await engine.ReadIdentityAsync(session, default);
        }
    }

    [TestFixture]
    public class Given_a_login_the_server_refuses_access_to_the_target
    {
        private static readonly string _sqlExceptionType = typeof(SqlException).FullName!;

        private TestDatabase? _database;
        private string? _login;
        private DmsIdentityRead _identity = null!;
        private DmsIdentityRead _grantedIdentity = null!;
        private Exception? _factsOutcome;
        private DmsSessionOpenResult _connect = null!;
        private DmsSessionOpenResult _fileCatalogHidden = null!;
        private DmsLiveTargetState _ownInspection = null!;
        private DmsSessionOpenResult _ownInspectionOpen = null!;
        private bool _ownInspectionLeftNoSession;
        private Guid _identityAfter;
        private bool _loginSessionsGone;

        [OneTimeSetUp]
        public async Task Setup()
        {
            RequireConfiguredOrSkip();
            _database = await CreateDmsShapedDatabaseAsync("refused");
            DmsInstanceIdentity instance = await _database.CatalogInstanceAsync();
            _login = await CreateLoginAsync("refused");
            string login = MssqlIdentifiers.Quote(_login);
            MssqlDmsTargetEngine engine = EngineAs(_login);

            // A login with no user in the database, and guest without CONNECT: the open itself is refused (4060).
            // The login cannot see sys.master_files either, so the refusal is resolved on the visible identity.
            _connect = await engine.OpenSessionAsync(_database.DatabaseName, instance, default);

            // A user in the database, still without the file catalog: the connection opens, but the primary file
            // cannot be verified on it, so no session is handed out.
            await _database.ExecuteAsync($"CREATE USER {login} FOR LOGIN {login};");
            _fileCatalogHidden = await engine.OpenSessionAsync(_database.DatabaseName, instance, default);

            // The same login's own inspection carries no file evidence either; two missing tags must not match.
            _ownInspection = await engine.InspectAsync(_database.DatabaseName, default);
            if (_ownInspection is DmsLiveTargetState.Present ownPresent)
            {
                _ownInspectionOpen = await engine.OpenSessionAsync(
                    _database.DatabaseName,
                    ownPresent.Instance,
                    default
                );
                if (_ownInspectionOpen is DmsSessionOpenResult.Opened handedOut)
                {
                    await handedOut.Session.DisposeAsync();
                }
            }

            string restricted = _login;
            _ownInspectionLeftNoSession = await WaitUntilAsync(
                async () => await SessionCountOfLoginAsync(restricted) == 0,
                TimeSpan.FromSeconds(20)
            );

            // With the file catalog visible and no grant on the dms schema: the identity read is refused.
            await ExecuteOnServerAsync($"GRANT VIEW ANY DEFINITION TO {login};");
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
            await _database.ExecuteAsync($"GRANT SELECT ON [dms].[DataStoreIdentity] TO {login};");
            DmsSessionOpenResult reopened = await engine.OpenSessionAsync(
                _database.DatabaseName,
                instance,
                default
            );
            await using (DmsSession session = ((DmsSessionOpenResult.Opened)reopened).Session)
            {
                _grantedIdentity = await engine.ReadIdentityAsync(session, default);
                try
                {
                    await engine.ReadTargetFactsAsync(session, default);
                }
                catch (Exception exception)
                {
                    _factsOutcome = exception;
                }
            }

            string loginName = _login;
            _loginSessionsGone = await WaitUntilAsync(
                async () => await SessionCountOfLoginAsync(loginName) == 0,
                TimeSpan.FromSeconds(20)
            );
            _identityAfter = await _database.ScalarAsync<Guid>(
                "SELECT [SourceIdentity] FROM [dms].[DataStoreIdentity];"
            );
        }

        [OneTimeTearDown]
        public async Task Teardown()
        {
            if (_database is not null)
            {
                await _database.DisposeAsync();
            }

            if (_login is not null)
            {
                await DropLoginAsync(_login);
            }
        }

        [Test]
        public void It_reports_a_refused_connection_as_access_denied_with_the_error_number_only() =>
            _connect
                .Should()
                .Be(
                    new DmsSessionOpenResult.AccessDenied(
                        new DmsProvisioningDiagnostic(
                            _sqlExceptionType,
                            "4060",
                            MssqlDmsTargetEngine.OpenSessionOperation
                        )
                    )
                );

        [Test]
        public void It_refuses_a_session_whose_primary_file_the_login_cannot_see() =>
            _fileCatalogHidden.Should().BeOfType<DmsSessionOpenResult.InstanceChanged>();

        [Test]
        public void It_inspects_without_file_evidence_when_the_login_cannot_see_the_file_catalog() =>
            _ownInspection
                .Should()
                .BeOfType<DmsLiveTargetState.Present>()
                .Which.Instance.Should()
                .BeOfType<DmsInstanceIdentity.Mssql>()
                .Which.PrimaryFileTag.Should()
                .BeEmpty();

        [Test]
        public void It_refuses_a_session_against_its_own_inspection_when_both_file_tags_are_missing() =>
            _ownInspectionOpen.Should().BeOfType<DmsSessionOpenResult.InstanceChanged>();

        [Test]
        public void It_closes_the_refused_connection() => _ownInspectionLeftNoSession.Should().BeTrue();

        [Test]
        public void It_reports_a_refused_identity_read_as_access_denied_with_the_error_number_only()
        {
            DmsIdentityRead.Invalid invalid = _identity.Should().BeOfType<DmsIdentityRead.Invalid>().Subject;
            (invalid.Problem, invalid.Diagnostic)
                .Should()
                .Be(
                    (
                        DmsIdentityReadProblem.AccessDenied,
                        new DmsProvisioningDiagnostic(
                            _sqlExceptionType,
                            "229",
                            MssqlDmsTargetEngine.ReadIdentityOperation
                        )
                    )
                );
        }

        [Test]
        public void It_reports_a_refused_facts_read_as_access_denied_with_the_error_number_only()
        {
            _grantedIdentity.Should().Be(new DmsIdentityRead.Found(MssqlDmsShapedManifest.SourceIdentity));
            DmsAccessDeniedException refused = _factsOutcome
                .Should()
                .BeOfType<DmsAccessDeniedException>()
                .Subject;
            (refused.Diagnostic, refused.InnerException)
                .Should()
                .Be(
                    (
                        new DmsProvisioningDiagnostic(
                            _sqlExceptionType,
                            "229",
                            MssqlDmsTargetEngine.ReadTargetFactsOperation
                        ),
                        (Exception?)null
                    )
                );
        }

        [Test]
        public void It_leaves_no_session_of_the_login_and_the_identity_unchanged() =>
            (_loginSessionsGone, _identityAfter).Should().Be((true, MssqlDmsShapedManifest.SourceIdentity));
    }
}

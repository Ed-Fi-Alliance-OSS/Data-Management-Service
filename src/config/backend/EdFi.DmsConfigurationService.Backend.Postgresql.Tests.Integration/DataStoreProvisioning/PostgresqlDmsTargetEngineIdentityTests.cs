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
}

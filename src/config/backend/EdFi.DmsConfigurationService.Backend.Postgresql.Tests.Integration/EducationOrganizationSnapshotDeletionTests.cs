// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Dapper;
using EdFi.DmsConfigurationService.Backend.Postgresql.Repositories;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.Backend.Services;
using EdFi.DmsConfigurationService.DataModel.Model.DataStore;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration;

public class EducationOrganizationSnapshotDeletionTests
{
    /// <summary>
    /// The ordinary DELETE path removes a data store's education organization snapshot through the
    /// foreign key cascades, in the same transaction as the catalog row, and leaves other snapshots alone.
    /// </summary>
    [TestFixture]
    public class Given_ordinary_data_stores_with_snapshots : DatabaseTest
    {
        private int _deletedId;
        private int _keptId;

        private static DataStoreRepository Repository() =>
            new(
                Configuration.DatabaseOptions,
                NullLogger<DataStoreRepository>.Instance,
                new ConnectionStringEncryptionService(Configuration.DatabaseOptions),
                A.Fake<IConnectionStringReader>(),
                A.Fake<IDataStoreContextRepository>(),
                A.Fake<IDataStoreDerivativeRepository>(),
                new TestAuditContext(),
                new TenantContextProvider { Context = new TenantContext.NotMultitenant() }
            );

        private static async Task<int> InsertDataStoreAsync(string name) => (
                await Repository()
                    .InsertDataStore(
                        new DataStoreInsertCommand
                        {
                            DataStoreType = "Production",
                            Name = name,
                            Provider = "postgresql",
                            ConnectionString = "Server=localhost;Database=snapshot-test;",
                        }
                    )
            ).Should().BeOfType<DataStoreInsertResult.Success>().Subject.Id;

        private Task SeedSnapshotAsync(int dataStoreId) =>
            Connection!.ExecuteAsync(
                """
                INSERT INTO dmscs."EducationOrganizationSnapshotTarget" ("DataStoreId", "LastGeneration", "ActiveGeneration")
                VALUES (@Id, 1, 1);
                INSERT INTO dmscs."EducationOrganizationSnapshot"
                    ("DataStoreId", "Generation", "EducationOrganizationId", "NameOfInstitution", "Discriminator", "ParentId", "RefreshedAt", "SourceContractVersion")
                VALUES
                    (@Id, 1, 255901, 'Grand Bend ISD', 'edfi.LocalEducationAgency', NULL, (now() AT TIME ZONE 'UTC'), 'educationOrganizationProjection.v1'),
                    (@Id, 1, 255901001, 'Grand Bend High School', 'edfi.School', 255901, (now() AT TIME ZONE 'UTC'), 'educationOrganizationProjection.v1');
                """,
                new { Id = dataStoreId }
            );

        private async Task<(int Targets, int Organizations)> SnapshotCountsAsync(int dataStoreId) =>
            (
                await Connection!.ExecuteScalarAsync<int>(
                    """SELECT COUNT(*) FROM dmscs."EducationOrganizationSnapshotTarget" WHERE "DataStoreId" = @Id;""",
                    new { Id = dataStoreId }
                ),
                await Connection!.ExecuteScalarAsync<int>(
                    """SELECT COUNT(*) FROM dmscs."EducationOrganizationSnapshot" WHERE "DataStoreId" = @Id;""",
                    new { Id = dataStoreId }
                )
            );

        [SetUp]
        public async Task Setup()
        {
            _deletedId = await InsertDataStoreAsync("snapshot-deleted");
            _keptId = await InsertDataStoreAsync("snapshot-kept");
            await SeedSnapshotAsync(_deletedId);
            await SeedSnapshotAsync(_keptId);
        }

        [Test]
        public async Task It_deletes_only_the_deleted_data_stores_snapshot()
        {
            (await Repository().DeleteDataStore(_deletedId))
                .Should()
                .BeOfType<DataStoreDeleteResult.Success>();

            (await SnapshotCountsAsync(_deletedId)).Should().Be((0, 0));
            (await SnapshotCountsAsync(_keptId)).Should().Be((1, 2));
        }

        [Test]
        public async Task It_keeps_the_snapshot_when_the_delete_rolls_back()
        {
            await Connection!.ExecuteAsync(
                """
                CREATE FUNCTION dmscs.reject_snapshot_test_delete() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN RAISE EXCEPTION 'snapshot-delete-test'; END $$;
                CREATE TRIGGER reject_snapshot_test_delete AFTER DELETE ON dmscs."DataStore"
                FOR EACH ROW EXECUTE FUNCTION dmscs.reject_snapshot_test_delete();
                """
            );
            try
            {
                (await Repository().DeleteDataStore(_deletedId))
                    .Should()
                    .BeOfType<DataStoreDeleteResult.FailureUnknown>();
            }
            finally
            {
                await Connection!.ExecuteAsync(
                    """
                    DROP TRIGGER reject_snapshot_test_delete ON dmscs."DataStore";
                    DROP FUNCTION dmscs.reject_snapshot_test_delete();
                    """
                );
            }

            (await SnapshotCountsAsync(_deletedId)).Should().Be((1, 2));
            (await Repository().DeleteDataStore(_deletedId))
                .Should()
                .BeOfType<DataStoreDeleteResult.Success>();
            (await SnapshotCountsAsync(_deletedId)).Should().Be((0, 0));
        }
    }
}

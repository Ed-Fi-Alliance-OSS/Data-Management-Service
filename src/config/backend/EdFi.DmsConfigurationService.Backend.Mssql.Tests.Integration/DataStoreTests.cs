// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.Mssql.Repositories;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.Backend.Services;
using EdFi.DmsConfigurationService.DataModel.Model;
using EdFi.DmsConfigurationService.DataModel.Model.Application;
using EdFi.DmsConfigurationService.DataModel.Model.DataStore;
using EdFi.DmsConfigurationService.DataModel.Model.Vendor;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration;

public class DataStoreTests : DatabaseTest
{
    private static void AssertIsValidEncryptedBase64(string? base64, string expectedPlainText)
    {
        base64.Should().NotBeNullOrEmpty();
        var encryptedBytes = Convert.FromBase64String(base64!);
        var encryptionService = new ConnectionStringEncryptionService(MssqlTestConfiguration.DatabaseOptions);
        encryptionService.Decrypt(encryptedBytes).Should().Be(expectedPlainText);
    }

    private readonly IDataStoreContextRepository _routeContextRepository = new DataStoreContextRepository(
        MssqlTestConfiguration.DatabaseOptions,
        NullLogger<DataStoreContextRepository>.Instance,
        new TestAuditContext(),
        new TenantContextProvider()
    );

    private readonly IDataStoreDerivativeRepository _derivativeRepository = new DataStoreDerivativeRepository(
        MssqlTestConfiguration.DatabaseOptions,
        NullLogger<DataStoreDerivativeRepository>.Instance,
        new ConnectionStringEncryptionService(MssqlTestConfiguration.DatabaseOptions),
        new TestAuditContext()
    );

    private readonly IDataStoreRepository _repository;

    public DataStoreTests()
    {
        _repository = new DataStoreRepository(
            MssqlTestConfiguration.DatabaseOptions,
            NullLogger<DataStoreRepository>.Instance,
            new ConnectionStringEncryptionService(MssqlTestConfiguration.DatabaseOptions),
            _routeContextRepository,
            _derivativeRepository,
            new TestAuditContext(),
            new TenantContextProvider()
        );
    }

    [TestFixture]
    public class Given_insert_data_store : DataStoreTests
    {
        private long _id;

        [SetUp]
        public async Task Setup()
        {
            DataStoreInsertCommand instance = new()
            {
                DataStoreType = "Production",
                Name = "Test Instance",
                ConnectionString = "Server=localhost;Database=TestDb;User Id=user;Password=pass;",
            };

            var result = await _repository.InsertDataStore(instance);
            result.Should().BeOfType<DataStoreInsertResult.Success>();
            _id = (result as DataStoreInsertResult.Success)!.Id;
            _id.Should().BeGreaterThan(0);
        }

        [Test]
        public async Task It_should_retrieve_instance_from_query()
        {
            var getResult = await _repository.QueryDataStore(new DataStoreQuery() { Limit = 25, Offset = 0 });
            getResult.Should().BeOfType<DataStoreQueryResult.Success>();

            var instanceFromDb = ((DataStoreQueryResult.Success)getResult).DataStoreResponses.First();
            instanceFromDb.DataStoreType.Should().Be("Production");
            instanceFromDb.Name.Should().Be("Test Instance");
            AssertIsValidEncryptedBase64(
                instanceFromDb.ConnectionString,
                "Server=localhost;Database=TestDb;User Id=user;Password=pass;"
            );
        }

        [Test]
        public async Task It_should_retrieve_instance_from_get_by_id()
        {
            var getByIdResult = await _repository.GetDataStore(_id);
            getByIdResult.Should().BeOfType<DataStoreGetResult.Success>();

            var instanceFromDb = ((DataStoreGetResult.Success)getByIdResult).DataStoreResponse;
            instanceFromDb.DataStoreType.Should().Be("Production");
            instanceFromDb.Name.Should().Be("Test Instance");
            AssertIsValidEncryptedBase64(
                instanceFromDb.ConnectionString,
                "Server=localhost;Database=TestDb;User Id=user;Password=pass;"
            );
        }
    }

    [TestFixture]
    public class Given_insert_data_store_without_connection_string : DataStoreTests
    {
        private long _id;

        [SetUp]
        public async Task Setup()
        {
            DataStoreInsertCommand instance = new()
            {
                DataStoreType = "Development",
                Name = "Test Instance Without Connection",
                ConnectionString = null,
            };

            var result = await _repository.InsertDataStore(instance);
            result.Should().BeOfType<DataStoreInsertResult.Success>();
            _id = (result as DataStoreInsertResult.Success)!.Id;
            _id.Should().BeGreaterThan(0);
        }

        [Test]
        public async Task It_should_retrieve_instance_with_null_connection_string()
        {
            var getByIdResult = await _repository.GetDataStore(_id);
            getByIdResult.Should().BeOfType<DataStoreGetResult.Success>();

            var instanceFromDb = ((DataStoreGetResult.Success)getByIdResult).DataStoreResponse;
            instanceFromDb.DataStoreType.Should().Be("Development");
            instanceFromDb.Name.Should().Be("Test Instance Without Connection");
            instanceFromDb.ConnectionString.Should().BeNull();
        }
    }

    [TestFixture]
    public class Given_update_data_store : DataStoreTests
    {
        private DataStoreInsertCommand _instanceInsert = null!;
        private DataStoreUpdateCommand _instanceUpdate = null!;

        [SetUp]
        public async Task Setup()
        {
            _instanceInsert = new()
            {
                DataStoreType = "Staging",
                Name = "Original Instance",
                ConnectionString = "Server=original;Database=OriginalDb;",
            };

            _instanceUpdate = new()
            {
                DataStoreType = "Production",
                Name = "Updated Instance",
                ConnectionString = "Server=updated;Database=UpdatedDb;",
            };

            var insertResult = await _repository.InsertDataStore(_instanceInsert);
            insertResult.Should().BeOfType<DataStoreInsertResult.Success>();

            _instanceUpdate.Id = (insertResult as DataStoreInsertResult.Success)!.Id;

            var updateResult = await _repository.UpdateDataStore(_instanceUpdate);
            updateResult.Should().BeOfType<DataStoreUpdateResult.Success>();
        }

        [Test]
        public async Task It_should_retrieve_updated_instance_from_query()
        {
            var getResult = await _repository.QueryDataStore(new DataStoreQuery() { Limit = 25, Offset = 0 });
            getResult.Should().BeOfType<DataStoreQueryResult.Success>();

            var instanceFromDb = ((DataStoreQueryResult.Success)getResult).DataStoreResponses.First();
            instanceFromDb.DataStoreType.Should().Be("Production");
            instanceFromDb.Name.Should().Be("Updated Instance");
            AssertIsValidEncryptedBase64(
                instanceFromDb.ConnectionString,
                "Server=updated;Database=UpdatedDb;"
            );
        }

        [Test]
        public async Task It_should_retrieve_updated_instance_from_get_by_id()
        {
            var getByIdResult = await _repository.GetDataStore(_instanceUpdate.Id);
            getByIdResult.Should().BeOfType<DataStoreGetResult.Success>();

            var instanceFromDb = ((DataStoreGetResult.Success)getByIdResult).DataStoreResponse;
            instanceFromDb.DataStoreType.Should().Be("Production");
            instanceFromDb.Name.Should().Be("Updated Instance");
            AssertIsValidEncryptedBase64(
                instanceFromDb.ConnectionString,
                "Server=updated;Database=UpdatedDb;"
            );
        }
    }

    [TestFixture]
    public class Given_delete_data_store : DataStoreTests
    {
        private long _instance1Id;
        private long _instance2Id;

        [SetUp]
        public async Task Setup()
        {
            var insertResult1 = await _repository.InsertDataStore(
                new DataStoreInsertCommand()
                {
                    DataStoreType = "Production",
                    Name = "Instance to Delete",
                    ConnectionString = "Server=delete;Database=DeleteDb;",
                }
            );

            _instance1Id = ((DataStoreInsertResult.Success)insertResult1).Id;

            var insertResult2 = await _repository.InsertDataStore(
                new DataStoreInsertCommand()
                {
                    DataStoreType = "Staging",
                    Name = "Instance to Keep",
                    ConnectionString = "Server=keep;Database=KeepDb;",
                }
            );

            _instance2Id = ((DataStoreInsertResult.Success)insertResult2).Id;

            var deleteResult = await _repository.DeleteDataStore(_instance1Id);
            deleteResult.Should().BeOfType<DataStoreDeleteResult.Success>();
        }

        [Test]
        public async Task It_should_not_retrieve_deleted_instance_from_query()
        {
            var getResult = await _repository.QueryDataStore(new DataStoreQuery() { Limit = 25, Offset = 0 });
            getResult.Should().BeOfType<DataStoreQueryResult.Success>();

            var instances = ((DataStoreQueryResult.Success)getResult).DataStoreResponses;
            instances.Count().Should().Be(1);
            instances.Count(i => i.Id == _instance1Id).Should().Be(0);
            instances.Count(i => i.Name == "Instance to Delete").Should().Be(0);
            instances.Count(i => i.Id == _instance2Id).Should().Be(1);
            instances.Count(i => i.Name == "Instance to Keep").Should().Be(1);
        }

        [Test]
        public async Task It_should_return_not_found_for_deleted_instance_get_by_id()
        {
            var getByIdResult = await _repository.GetDataStore(_instance1Id);
            getByIdResult.Should().BeOfType<DataStoreGetResult.FailureNotFound>();

            getByIdResult = await _repository.GetDataStore(_instance2Id);
            getByIdResult.Should().BeOfType<DataStoreGetResult.Success>();
        }
    }

    [TestFixture]
    public class Given_update_non_existent_data_store : DataStoreTests
    {
        [Test]
        public async Task It_should_return_failure_not_exists()
        {
            var updateCommand = new DataStoreUpdateCommand()
            {
                Id = 9999,
                DataStoreType = "Production",
                Name = "Non-existent Instance",
                ConnectionString = "Server=fake;Database=FakeDb;",
            };

            var result = await _repository.UpdateDataStore(updateCommand);
            result.Should().BeOfType<DataStoreUpdateResult.FailureNotExists>();
        }
    }

    [TestFixture]
    public class Given_delete_non_existent_data_store : DataStoreTests
    {
        [Test]
        public async Task It_should_return_failure_not_exists()
        {
            var result = await _repository.DeleteDataStore(9999);
            result.Should().BeOfType<DataStoreDeleteResult.FailureNotExists>();
        }
    }

    [TestFixture]
    public class Given_get_non_existent_data_store : DataStoreTests
    {
        [Test]
        public async Task It_should_return_failure_not_found()
        {
            var result = await _repository.GetDataStore(9999);
            result.Should().BeOfType<DataStoreGetResult.FailureNotFound>();
        }
    }

    [TestFixture]
    public class Given_data_store_with_route_contexts : DataStoreTests
    {
        private long _dataStoreId;

        [SetUp]
        public async Task Setup()
        {
            // Insert DMS instance
            DataStoreInsertCommand instance = new()
            {
                DataStoreType = "Production",
                Name = "Instance With Contexts",
                ConnectionString = "Server=localhost;Database=TestDb;",
            };

            var result = await _repository.InsertDataStore(instance);
            result.Should().BeOfType<DataStoreInsertResult.Success>();
            _dataStoreId = (result as DataStoreInsertResult.Success)!.Id;

            // Insert route contexts
            await _routeContextRepository.InsertDataStoreContext(
                new()
                {
                    DataStoreId = _dataStoreId,
                    ContextKey = "environment",
                    ContextValue = "production",
                }
            );

            await _routeContextRepository.InsertDataStoreContext(
                new()
                {
                    DataStoreId = _dataStoreId,
                    ContextKey = "region",
                    ContextValue = "us-east-1",
                }
            );
        }

        [Test]
        public async Task It_should_retrieve_instance_with_route_contexts()
        {
            var getByIdResult = await _repository.GetDataStore(_dataStoreId);
            getByIdResult.Should().BeOfType<DataStoreGetResult.Success>();

            var instanceFromDb = ((DataStoreGetResult.Success)getByIdResult).DataStoreResponse;
            instanceFromDb.DataStoreType.Should().Be("Production");
            instanceFromDb.Name.Should().Be("Instance With Contexts");
            instanceFromDb.DataStoreContexts.Should().HaveCount(2);

            instanceFromDb
                .DataStoreContexts.Should()
                .Contain(c => c.ContextKey == "environment" && c.ContextValue == "production");
            instanceFromDb
                .DataStoreContexts.Should()
                .Contain(c => c.ContextKey == "region" && c.ContextValue == "us-east-1");
        }

        [Test]
        public async Task It_should_return_empty_contexts_for_instance_without_contexts()
        {
            // Insert an instance without route contexts
            DataStoreInsertCommand instance = new()
            {
                DataStoreType = "Development",
                Name = "Instance Without Contexts",
                ConnectionString = "Server=localhost;Database=DevDb;",
            };

            var result = await _repository.InsertDataStore(instance);
            var dataStoreId = (result as DataStoreInsertResult.Success)!.Id;

            var getByIdResult = await _repository.GetDataStore(dataStoreId);
            getByIdResult.Should().BeOfType<DataStoreGetResult.Success>();

            var instanceFromDb = ((DataStoreGetResult.Success)getByIdResult).DataStoreResponse;
            instanceFromDb.DataStoreContexts.Should().BeEmpty();
        }
    }

    [TestFixture]
    public class Given_validate_multiple_data_store_ids : DataStoreTests
    {
        private long _instance1Id;
        private long _instance2Id;
        private long _instance3Id;

        [SetUp]
        public async Task Setup()
        {
            var result1 = await _repository.InsertDataStore(
                new DataStoreInsertCommand()
                {
                    DataStoreType = "Production",
                    Name = "Test Instance 1",
                    ConnectionString = "Server=test1;Database=TestDb1;",
                }
            );
            _instance1Id = ((DataStoreInsertResult.Success)result1).Id;

            var result2 = await _repository.InsertDataStore(
                new DataStoreInsertCommand()
                {
                    DataStoreType = "Staging",
                    Name = "Test Instance 2",
                    ConnectionString = "Server=test2;Database=TestDb2;",
                }
            );
            _instance2Id = ((DataStoreInsertResult.Success)result2).Id;

            var result3 = await _repository.InsertDataStore(
                new DataStoreInsertCommand()
                {
                    DataStoreType = "Development",
                    Name = "Test Instance 3",
                    ConnectionString = "Server=test3;Database=TestDb3;",
                }
            );
            _instance3Id = ((DataStoreInsertResult.Success)result3).Id;
        }

        [Test]
        public async Task It_should_return_all_existing_ids()
        {
            long[] idsToCheck = [_instance1Id, _instance2Id, _instance3Id];
            var result = await _repository.GetExistingDataStoreIds(idsToCheck);

            result.Should().BeOfType<DataStoreIdsExistResult.Success>();
            var existingIds = ((DataStoreIdsExistResult.Success)result).ExistingIds;
            existingIds.Should().HaveCount(3);
            existingIds.Should().Contain(_instance1Id);
            existingIds.Should().Contain(_instance2Id);
            existingIds.Should().Contain(_instance3Id);
        }

        [Test]
        public async Task It_should_return_only_existing_ids_when_some_dont_exist()
        {
            long[] idsToCheck = [_instance1Id, 99999, _instance2Id, 88888, _instance3Id];
            var result = await _repository.GetExistingDataStoreIds(idsToCheck);

            result.Should().BeOfType<DataStoreIdsExistResult.Success>();
            var existingIds = ((DataStoreIdsExistResult.Success)result).ExistingIds;
            existingIds.Should().HaveCount(3);
            existingIds.Should().Contain(_instance1Id);
            existingIds.Should().Contain(_instance2Id);
            existingIds.Should().Contain(_instance3Id);
            existingIds.Should().NotContain(99999);
            existingIds.Should().NotContain(88888);
        }

        [Test]
        public async Task It_should_return_empty_set_when_no_ids_exist()
        {
            long[] idsToCheck = [99999, 88888, 77777];
            var result = await _repository.GetExistingDataStoreIds(idsToCheck);

            result.Should().BeOfType<DataStoreIdsExistResult.Success>();
            var existingIds = ((DataStoreIdsExistResult.Success)result).ExistingIds;
            existingIds.Should().BeEmpty();
        }

        [Test]
        public async Task It_should_return_empty_set_when_input_is_empty()
        {
            long[] idsToCheck = [];
            var result = await _repository.GetExistingDataStoreIds(idsToCheck);

            result.Should().BeOfType<DataStoreIdsExistResult.Success>();
            var existingIds = ((DataStoreIdsExistResult.Success)result).ExistingIds;
            existingIds.Should().BeEmpty();
        }
    }

    [TestFixture]
    public class Given_data_store_is_assigned_to_applications : DataStoreTests
    {
        private long _dataStoreId1;
        private long _dataStoreId2;
        private long _unassignedDataStoreId;
        private long _vendorId;

        [SetUp]
        public async Task Setup()
        {
            DataStoreInsertCommand instance1 = new()
            {
                DataStoreType = "Production",
                Name = "Test Instance",
                ConnectionString = "Server=localhost;Database=TestDb;User Id=user;Password=pass;",
            };

            var instance1Result = await _repository.InsertDataStore(instance1);
            instance1Result.Should().BeOfType<DataStoreInsertResult.Success>();
            _dataStoreId1 = (instance1Result as DataStoreInsertResult.Success)!.Id;
            _dataStoreId1.Should().BeGreaterThan(0);

            DataStoreInsertCommand instance2 = new()
            {
                DataStoreType = "Staging",
                Name = "Test Instance",
                ConnectionString = "Server=localhost;Database=TestDb;User Id=user;Password=pass;",
            };

            var instance2Result = await _repository.InsertDataStore(instance2);
            instance2Result.Should().BeOfType<DataStoreInsertResult.Success>();
            _dataStoreId2 = (instance2Result as DataStoreInsertResult.Success)!.Id;
            _dataStoreId2.Should().BeGreaterThan(0);

            DataStoreInsertCommand unassignedInstance = new()
            {
                DataStoreType = "Unassigned",
                Name = "Test Instance",
                ConnectionString = "Server=localhost;Database=TestDb;User Id=user;Password=pass;",
            };

            var unassignedInstanceResult = await _repository.InsertDataStore(unassignedInstance);
            unassignedInstanceResult.Should().BeOfType<DataStoreInsertResult.Success>();
            _unassignedDataStoreId = (unassignedInstanceResult as DataStoreInsertResult.Success)!.Id;
            _unassignedDataStoreId.Should().BeGreaterThan(0);

            var vendorRepository = new VendorRepository(
                MssqlTestConfiguration.DatabaseOptions,
                NullLogger<VendorRepository>.Instance,
                new TestAuditContext(),
                new TenantContextProvider()
            );

            VendorInsertCommand vendor = new()
            {
                Company = "Fake Company",
                ContactEmailAddress = "test@test.com",
                ContactName = "Fake Name",
                NamespacePrefixes = "FakePrefix1,FakePrefix2",
            };

            var vendorResult = await vendorRepository.InsertVendor(vendor);
            vendorResult.Should().BeOfType<VendorInsertResult.Success>();
            _vendorId = (vendorResult as VendorInsertResult.Success)!.Id;
            _vendorId.Should().BeGreaterThan(0);

            var applicationRepository = new ApplicationRepository(
                MssqlTestConfiguration.DatabaseOptions,
                NullLogger<ApplicationRepository>.Instance,
                new TestAuditContext()
            );

            ApplicationInsertCommand application = new()
            {
                ApplicationName = "Test Application",
                VendorId = _vendorId,
                ClaimSetName = "Test Claim set",
                EducationOrganizationIds = [1, 255911001, 255911002],
                DataStoreIds = [_dataStoreId1, _dataStoreId2],
            };

            var applicationResult = await applicationRepository.InsertApplication(
                application,
                new() { ClientId = Guid.NewGuid().ToString(), ClientUuid = Guid.NewGuid() }
            );
            applicationResult.Should().BeOfType<ApplicationInsertResult.Success>();
            var applicationId = (applicationResult as ApplicationInsertResult.Success)!.Id;
            applicationId.Should().BeGreaterThan(0);

            ApplicationInsertCommand application2 = new()
            {
                ApplicationName = "Test Application 2",
                VendorId = _vendorId,
                ClaimSetName = "Test Claim set 2",
                EducationOrganizationIds = [2, 255911002, 255911003],
                DataStoreIds = [_dataStoreId1],
            };

            var application2Result = await applicationRepository.InsertApplication(
                application2,
                new() { ClientId = Guid.NewGuid().ToString(), ClientUuid = Guid.NewGuid() }
            );
            application2Result.Should().BeOfType<ApplicationInsertResult.Success>();
            var application2Id = (application2Result as ApplicationInsertResult.Success)!.Id;
            application2Id.Should().BeGreaterThan(0);

            ApplicationInsertCommand applicationWithoutEdOrgs = new()
            {
                ApplicationName = "Application without EdOrgs",
                VendorId = _vendorId,
                ClaimSetName = "Test Claim set 3",
                EducationOrganizationIds = [],
                DataStoreIds = [_dataStoreId1],
            };

            var applicationWithoutEdOrgsResult = await applicationRepository.InsertApplication(
                applicationWithoutEdOrgs,
                new() { ClientId = Guid.NewGuid().ToString(), ClientUuid = Guid.NewGuid() }
            );
            applicationWithoutEdOrgsResult.Should().BeOfType<ApplicationInsertResult.Success>();
            var applicationWithoutEdOrgsId = (
                applicationWithoutEdOrgsResult as ApplicationInsertResult.Success
            )!.Id;
            applicationWithoutEdOrgsId.Should().BeGreaterThan(0);
        }

        [Test]
        public async Task It_should_retrieve_applications_by_data_store()
        {
            var queryResult = await _repository.QueryApplicationByDataStore(_dataStoreId1, new PagingQuery());
            queryResult.Should().BeOfType<ApplicationByDataStoreQueryResult.Success>();
            ((ApplicationByDataStoreQueryResult.Success)queryResult)
                .ApplicationResponse.Count()
                .Should()
                .Be(3);

            var application1 = (
                (ApplicationByDataStoreQueryResult.Success)queryResult
            ).ApplicationResponse.ToList()[0];
            application1.ApplicationName.Should().Be("Test Application");
            application1.VendorId.Should().Be(_vendorId);
            application1.ClaimSetName.Should().Be("Test Claim set");
            application1.EducationOrganizationIds.Should().BeEquivalentTo([1, 255911001, 255911002]);
            application1.DataStoreIds.Should().BeEquivalentTo([_dataStoreId1, _dataStoreId2]);

            var application2 = (
                (ApplicationByDataStoreQueryResult.Success)queryResult
            ).ApplicationResponse.ToList()[1];
            application2.ApplicationName.Should().Be("Test Application 2");
            application2.VendorId.Should().Be(_vendorId);
            application2.ClaimSetName.Should().Be("Test Claim set 2");
            application2.EducationOrganizationIds.Should().BeEquivalentTo([2, 255911002, 255911003]);
            application2.DataStoreIds.Should().BeEquivalentTo([_dataStoreId1]);

            var applicationWithoutEdOrgs = (
                (ApplicationByDataStoreQueryResult.Success)queryResult
            ).ApplicationResponse.ToList()[2];
            applicationWithoutEdOrgs.ApplicationName.Should().Be("Application without EdOrgs");
            applicationWithoutEdOrgs.VendorId.Should().Be(_vendorId);
            applicationWithoutEdOrgs.ClaimSetName.Should().Be("Test Claim set 3");
            applicationWithoutEdOrgs.EducationOrganizationIds.Should().BeEmpty();
            applicationWithoutEdOrgs.DataStoreIds.Should().BeEquivalentTo([_dataStoreId1]);
        }

        [Test]
        public async Task It_should_retrieve_paged_applications_by_data_store()
        {
            var queryResult = await _repository.QueryApplicationByDataStore(
                _dataStoreId1,
                new PagingQuery() { Limit = 1, Offset = 1 }
            );
            queryResult.Should().BeOfType<ApplicationByDataStoreQueryResult.Success>();
            ((ApplicationByDataStoreQueryResult.Success)queryResult)
                .ApplicationResponse.Count()
                .Should()
                .Be(1);

            var application = (
                (ApplicationByDataStoreQueryResult.Success)queryResult
            ).ApplicationResponse.ToList()[0];
            application.ApplicationName.Should().Be("Test Application 2");
            application.VendorId.Should().Be(_vendorId);
            application.ClaimSetName.Should().Be("Test Claim set 2");
            application.EducationOrganizationIds.Should().BeEquivalentTo([2, 255911002, 255911003]);
            application.DataStoreIds.Should().BeEquivalentTo([_dataStoreId1]);
        }

        [Test]
        public async Task It_should_return_not_exists_failure()
        {
            var queryResult = await _repository.QueryApplicationByDataStore(
                0,
                new PagingQuery() { Limit = 25, Offset = 1 }
            );
            queryResult.Should().BeOfType<ApplicationByDataStoreQueryResult.FailureNotExists>();
        }

        [Test]
        public async Task It_should_return_empty_array()
        {
            var queryResult = await _repository.QueryApplicationByDataStore(
                _unassignedDataStoreId,
                new PagingQuery() { Limit = 25, Offset = 1 }
            );
            queryResult.Should().BeOfType<ApplicationByDataStoreQueryResult.Success>();
            ((ApplicationByDataStoreQueryResult.Success)queryResult).ApplicationResponse.Should().BeEmpty();
        }
    }

    [TestFixture]
    public class QueryPagingTests : DataStoreTests
    {
        [SetUp]
        public async Task Setup()
        {
            for (int i = 1; i <= 20; i++)
            {
                DataStoreInsertCommand dataStoreCommand = new()
                {
                    Name = $"Instance-{i:D2}",
                    DataStoreType = "SQL",
                    ConnectionString = "encrypted-connection-string",
                };
                var insertResult = await _repository.InsertDataStore(dataStoreCommand);
                insertResult
                    .Should()
                    .BeOfType<DataStoreInsertResult.Success>(
                        $"dms instance {i} (Instance-{i:D2}) should insert successfully"
                    );
            }
        }

        [Test]
        public async Task Should_return_all_results_when_no_paging_params_provided()
        {
            var result = await _repository.QueryDataStore(new DataStoreQuery());
            result.Should().BeOfType<DataStoreQueryResult.Success>();
            ((DataStoreQueryResult.Success)result).DataStoreResponses.Should().HaveCount(20);
        }

        [Test]
        public async Task Should_apply_limit_when_limit_is_provided()
        {
            var result = await _repository.QueryDataStore(new DataStoreQuery { Limit = 8 });
            result.Should().BeOfType<DataStoreQueryResult.Success>();
            ((DataStoreQueryResult.Success)result).DataStoreResponses.Should().HaveCount(8);
        }

        [Test]
        public async Task Should_apply_offset_when_offset_is_provided()
        {
            var result = await _repository.QueryDataStore(new DataStoreQuery { Offset = 15 });
            result.Should().BeOfType<DataStoreQueryResult.Success>();
            ((DataStoreQueryResult.Success)result).DataStoreResponses.Should().HaveCount(5);
        }
    }

    [TestFixture]
    public class QuerySortTests : DataStoreTests
    {
        [SetUp]
        public async Task Setup()
        {
            foreach (var name in new[] { "Zulu-Instance", "Charlie-Instance", "November-Instance" })
            {
                DataStoreInsertCommand dataStoreCommand = new()
                {
                    Name = name,
                    DataStoreType = "SQL",
                    ConnectionString = "encrypted-connection-string",
                };
                var insertResult = await _repository.InsertDataStore(dataStoreCommand);
                insertResult
                    .Should()
                    .BeOfType<DataStoreInsertResult.Success>(
                        $"dms instance '{name}' should insert successfully"
                    );
            }
        }

        [Test]
        public async Task Should_return_ascending_order_by_name()
        {
            var result = await _repository.QueryDataStore(
                new DataStoreQuery { OrderBy = "name", Direction = "ASC" }
            );
            result.Should().BeOfType<DataStoreQueryResult.Success>();
            var names = ((DataStoreQueryResult.Success)result)
                .DataStoreResponses.Select(d => d.Name)
                .ToList();
            names.Should().HaveCount(3);
            names.Should().ContainInOrder("Charlie-Instance", "November-Instance", "Zulu-Instance");
        }

        [Test]
        public async Task Should_return_descending_order_by_name()
        {
            var result = await _repository.QueryDataStore(
                new DataStoreQuery { OrderBy = "name", Direction = "DESC" }
            );
            result.Should().BeOfType<DataStoreQueryResult.Success>();
            var names = ((DataStoreQueryResult.Success)result)
                .DataStoreResponses.Select(d => d.Name)
                .ToList();
            names.Should().HaveCount(3);
            names.Should().ContainInOrder("Zulu-Instance", "November-Instance", "Charlie-Instance");
        }

        [Test]
        public async Task Should_default_to_ascending_order_when_direction_is_omitted()
        {
            var result = await _repository.QueryDataStore(new DataStoreQuery { OrderBy = "name" });
            result.Should().BeOfType<DataStoreQueryResult.Success>();
            var names = ((DataStoreQueryResult.Success)result)
                .DataStoreResponses.Select(d => d.Name)
                .ToList();
            names.Should().HaveCount(3);
            names.Should().ContainInOrder("Charlie-Instance", "November-Instance", "Zulu-Instance");
        }
    }
}

// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.Postgresql.Repositories;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.Backend.Services;
using EdFi.DmsConfigurationService.DataModel.Model;
using EdFi.DmsConfigurationService.DataModel.Model.Application;
using EdFi.DmsConfigurationService.DataModel.Model.DmsInstance;
using EdFi.DmsConfigurationService.DataModel.Model.Vendor;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration;

public class DmsInstanceTests : DatabaseTest
{
    private readonly IDmsInstanceRouteContextRepository _routeContextRepository =
        new DmsInstanceRouteContextRepository(
            Configuration.DatabaseOptions,
            NullLogger<DmsInstanceRouteContextRepository>.Instance,
            new TestAuditContext()
        );

    private readonly IDmsInstanceDerivativeRepository _derivativeRepository =
        new DmsInstanceDerivativeRepository(
            Configuration.DatabaseOptions,
            NullLogger<DmsInstanceDerivativeRepository>.Instance,
            new ConnectionStringEncryptionService(Configuration.DatabaseOptions),
            new TestAuditContext()
        );

    private readonly IDmsInstanceRepository _repository;

    public DmsInstanceTests()
    {
        _repository = new DmsInstanceRepository(
            Configuration.DatabaseOptions,
            NullLogger<DmsInstanceRepository>.Instance,
            new ConnectionStringEncryptionService(Configuration.DatabaseOptions),
            _routeContextRepository,
            _derivativeRepository,
            new TestAuditContext(),
            new TenantContextProvider()
        );
    }

    [TestFixture]
    public class Given_insert_dms_instance : DmsInstanceTests
    {
        private long _id;

        [SetUp]
        public async Task Setup()
        {
            DmsInstanceInsertCommand instance = new()
            {
                InstanceType = "Production",
                InstanceName = "Test Instance",
                ConnectionString = "Server=localhost;Database=TestDb;User Id=user;Password=pass;",
            };

            var result = await _repository.InsertDmsInstance(instance);
            result.Should().BeOfType<DmsInstanceInsertResult.Success>();
            _id = (result as DmsInstanceInsertResult.Success)!.Id;
            _id.Should().BeGreaterThan(0);
        }

        [Test]
        public async Task It_should_retrieve_instance_from_query()
        {
            var getResult = await _repository.QueryDmsInstance(new PagingQuery() { Limit = 25, Offset = 0 });
            getResult.Should().BeOfType<DmsInstanceQueryResult.Success>();

            var instanceFromDb = ((DmsInstanceQueryResult.Success)getResult).DmsInstanceResponses.First();
            instanceFromDb.InstanceType.Should().Be("Production");
            instanceFromDb.InstanceName.Should().Be("Test Instance");
            instanceFromDb
                .ConnectionString.Should()
                .Be("Server=localhost;Database=TestDb;User Id=user;Password=pass;");
        }

        [Test]
        public async Task It_should_retrieve_instance_from_get_by_id()
        {
            var getByIdResult = await _repository.GetDmsInstance(_id);
            getByIdResult.Should().BeOfType<DmsInstanceGetResult.Success>();

            var instanceFromDb = ((DmsInstanceGetResult.Success)getByIdResult).DmsInstanceResponse;
            instanceFromDb.InstanceType.Should().Be("Production");
            instanceFromDb.InstanceName.Should().Be("Test Instance");
            instanceFromDb
                .ConnectionString.Should()
                .Be("Server=localhost;Database=TestDb;User Id=user;Password=pass;");
        }
    }

    [TestFixture]
    public class Given_insert_dms_instance_without_connection_string : DmsInstanceTests
    {
        private long _id;

        [SetUp]
        public async Task Setup()
        {
            DmsInstanceInsertCommand instance = new()
            {
                InstanceType = "Development",
                InstanceName = "Test Instance Without Connection",
                ConnectionString = null,
            };

            var result = await _repository.InsertDmsInstance(instance);
            result.Should().BeOfType<DmsInstanceInsertResult.Success>();
            _id = (result as DmsInstanceInsertResult.Success)!.Id;
            _id.Should().BeGreaterThan(0);
        }

        [Test]
        public async Task It_should_retrieve_instance_with_null_connection_string()
        {
            var getByIdResult = await _repository.GetDmsInstance(_id);
            getByIdResult.Should().BeOfType<DmsInstanceGetResult.Success>();

            var instanceFromDb = ((DmsInstanceGetResult.Success)getByIdResult).DmsInstanceResponse;
            instanceFromDb.InstanceType.Should().Be("Development");
            instanceFromDb.InstanceName.Should().Be("Test Instance Without Connection");
            instanceFromDb.ConnectionString.Should().BeNull();
        }
    }

    [TestFixture]
    public class Given_update_dms_instance : DmsInstanceTests
    {
        private DmsInstanceInsertCommand _instanceInsert = null!;
        private DmsInstanceUpdateCommand _instanceUpdate = null!;

        [SetUp]
        public async Task Setup()
        {
            _instanceInsert = new()
            {
                InstanceType = "Staging",
                InstanceName = "Original Instance",
                ConnectionString = "Server=original;Database=OriginalDb;",
            };

            _instanceUpdate = new()
            {
                InstanceType = "Production",
                InstanceName = "Updated Instance",
                ConnectionString = "Server=updated;Database=UpdatedDb;",
            };

            var insertResult = await _repository.InsertDmsInstance(_instanceInsert);
            insertResult.Should().BeOfType<DmsInstanceInsertResult.Success>();

            _instanceUpdate.Id = (insertResult as DmsInstanceInsertResult.Success)!.Id;

            var updateResult = await _repository.UpdateDmsInstance(_instanceUpdate);
            updateResult.Should().BeOfType<DmsInstanceUpdateResult.Success>();
        }

        [Test]
        public async Task It_should_retrieve_updated_instance_from_query()
        {
            var getResult = await _repository.QueryDmsInstance(new PagingQuery() { Limit = 25, Offset = 0 });
            getResult.Should().BeOfType<DmsInstanceQueryResult.Success>();

            var instanceFromDb = ((DmsInstanceQueryResult.Success)getResult).DmsInstanceResponses.First();
            instanceFromDb.InstanceType.Should().Be("Production");
            instanceFromDb.InstanceName.Should().Be("Updated Instance");
            instanceFromDb.ConnectionString.Should().Be("Server=updated;Database=UpdatedDb;");
        }

        [Test]
        public async Task It_should_retrieve_updated_instance_from_get_by_id()
        {
            var getByIdResult = await _repository.GetDmsInstance(_instanceUpdate.Id);
            getByIdResult.Should().BeOfType<DmsInstanceGetResult.Success>();

            var instanceFromDb = ((DmsInstanceGetResult.Success)getByIdResult).DmsInstanceResponse;
            instanceFromDb.InstanceType.Should().Be("Production");
            instanceFromDb.InstanceName.Should().Be("Updated Instance");
            instanceFromDb.ConnectionString.Should().Be("Server=updated;Database=UpdatedDb;");
        }
    }

    [TestFixture]
    public class Given_delete_dms_instance : DmsInstanceTests
    {
        private long _instance1Id;
        private long _instance2Id;

        [SetUp]
        public async Task Setup()
        {
            var insertResult1 = await _repository.InsertDmsInstance(
                new DmsInstanceInsertCommand()
                {
                    InstanceType = "Production",
                    InstanceName = "Instance to Delete",
                    ConnectionString = "Server=delete;Database=DeleteDb;",
                }
            );

            _instance1Id = ((DmsInstanceInsertResult.Success)insertResult1).Id;

            var insertResult2 = await _repository.InsertDmsInstance(
                new DmsInstanceInsertCommand()
                {
                    InstanceType = "Staging",
                    InstanceName = "Instance to Keep",
                    ConnectionString = "Server=keep;Database=KeepDb;",
                }
            );

            _instance2Id = ((DmsInstanceInsertResult.Success)insertResult2).Id;

            var deleteResult = await _repository.DeleteDmsInstance(_instance1Id);
            deleteResult.Should().BeOfType<DmsInstanceDeleteResult.Success>();
        }

        [Test]
        public async Task It_should_not_retrieve_deleted_instance_from_query()
        {
            var getResult = await _repository.QueryDmsInstance(new PagingQuery() { Limit = 25, Offset = 0 });
            getResult.Should().BeOfType<DmsInstanceQueryResult.Success>();

            var instances = ((DmsInstanceQueryResult.Success)getResult).DmsInstanceResponses;
            instances.Count().Should().Be(1);
            instances.Count(i => i.Id == _instance1Id).Should().Be(0);
            instances.Count(i => i.InstanceName == "Instance to Delete").Should().Be(0);
            instances.Count(i => i.Id == _instance2Id).Should().Be(1);
            instances.Count(i => i.InstanceName == "Instance to Keep").Should().Be(1);
        }

        [Test]
        public async Task It_should_return_not_found_for_deleted_instance_get_by_id()
        {
            var getByIdResult = await _repository.GetDmsInstance(_instance1Id);
            getByIdResult.Should().BeOfType<DmsInstanceGetResult.FailureNotFound>();

            getByIdResult = await _repository.GetDmsInstance(_instance2Id);
            getByIdResult.Should().BeOfType<DmsInstanceGetResult.Success>();
        }
    }

    [TestFixture]
    public class Given_update_non_existent_dms_instance : DmsInstanceTests
    {
        [Test]
        public async Task It_should_return_failure_not_exists()
        {
            var updateCommand = new DmsInstanceUpdateCommand()
            {
                Id = 9999,
                InstanceType = "Production",
                InstanceName = "Non-existent Instance",
                ConnectionString = "Server=fake;Database=FakeDb;",
            };

            var result = await _repository.UpdateDmsInstance(updateCommand);
            result.Should().BeOfType<DmsInstanceUpdateResult.FailureNotExists>();
        }
    }

    [TestFixture]
    public class Given_delete_non_existent_dms_instance : DmsInstanceTests
    {
        [Test]
        public async Task It_should_return_failure_not_exists()
        {
            var result = await _repository.DeleteDmsInstance(9999);
            result.Should().BeOfType<DmsInstanceDeleteResult.FailureNotExists>();
        }
    }

    [TestFixture]
    public class Given_get_non_existent_dms_instance : DmsInstanceTests
    {
        [Test]
        public async Task It_should_return_failure_not_found()
        {
            var result = await _repository.GetDmsInstance(9999);
            result.Should().BeOfType<DmsInstanceGetResult.FailureNotFound>();
        }
    }

    [TestFixture]
    public class Given_dms_instance_with_route_contexts : DmsInstanceTests
    {
        private long _instanceId;

        [SetUp]
        public async Task Setup()
        {
            // Insert DMS instance
            DmsInstanceInsertCommand instance = new()
            {
                InstanceType = "Production",
                InstanceName = "Instance With Contexts",
                ConnectionString = "Server=localhost;Database=TestDb;",
            };

            var result = await _repository.InsertDmsInstance(instance);
            result.Should().BeOfType<DmsInstanceInsertResult.Success>();
            _instanceId = (result as DmsInstanceInsertResult.Success)!.Id;

            // Insert route contexts
            await _routeContextRepository.InsertDmsInstanceRouteContext(
                new()
                {
                    InstanceId = _instanceId,
                    ContextKey = "environment",
                    ContextValue = "production",
                }
            );

            await _routeContextRepository.InsertDmsInstanceRouteContext(
                new()
                {
                    InstanceId = _instanceId,
                    ContextKey = "region",
                    ContextValue = "us-east-1",
                }
            );
        }

        [Test]
        public async Task It_should_retrieve_instance_with_route_contexts()
        {
            var getByIdResult = await _repository.GetDmsInstance(_instanceId);
            getByIdResult.Should().BeOfType<DmsInstanceGetResult.Success>();

            var instanceFromDb = ((DmsInstanceGetResult.Success)getByIdResult).DmsInstanceResponse;
            instanceFromDb.InstanceType.Should().Be("Production");
            instanceFromDb.InstanceName.Should().Be("Instance With Contexts");
            instanceFromDb.DmsInstanceRouteContexts.Should().HaveCount(2);

            instanceFromDb
                .DmsInstanceRouteContexts.Should()
                .Contain(c => c.ContextKey == "environment" && c.ContextValue == "production");
            instanceFromDb
                .DmsInstanceRouteContexts.Should()
                .Contain(c => c.ContextKey == "region" && c.ContextValue == "us-east-1");
        }

        [Test]
        public async Task It_should_return_empty_contexts_for_instance_without_contexts()
        {
            // Insert an instance without route contexts
            DmsInstanceInsertCommand instance = new()
            {
                InstanceType = "Development",
                InstanceName = "Instance Without Contexts",
                ConnectionString = "Server=localhost;Database=DevDb;",
            };

            var result = await _repository.InsertDmsInstance(instance);
            var instanceId = (result as DmsInstanceInsertResult.Success)!.Id;

            var getByIdResult = await _repository.GetDmsInstance(instanceId);
            getByIdResult.Should().BeOfType<DmsInstanceGetResult.Success>();

            var instanceFromDb = ((DmsInstanceGetResult.Success)getByIdResult).DmsInstanceResponse;
            instanceFromDb.DmsInstanceRouteContexts.Should().BeEmpty();
        }
    }

    [TestFixture]
    public class Given_validate_multiple_dms_instance_ids : DmsInstanceTests
    {
        private long _instance1Id;
        private long _instance2Id;
        private long _instance3Id;

        [SetUp]
        public async Task Setup()
        {
            var result1 = await _repository.InsertDmsInstance(
                new DmsInstanceInsertCommand()
                {
                    InstanceType = "Production",
                    InstanceName = "Test Instance 1",
                    ConnectionString = "Server=test1;Database=TestDb1;",
                }
            );
            _instance1Id = ((DmsInstanceInsertResult.Success)result1).Id;

            var result2 = await _repository.InsertDmsInstance(
                new DmsInstanceInsertCommand()
                {
                    InstanceType = "Staging",
                    InstanceName = "Test Instance 2",
                    ConnectionString = "Server=test2;Database=TestDb2;",
                }
            );
            _instance2Id = ((DmsInstanceInsertResult.Success)result2).Id;

            var result3 = await _repository.InsertDmsInstance(
                new DmsInstanceInsertCommand()
                {
                    InstanceType = "Development",
                    InstanceName = "Test Instance 3",
                    ConnectionString = "Server=test3;Database=TestDb3;",
                }
            );
            _instance3Id = ((DmsInstanceInsertResult.Success)result3).Id;
        }

        [Test]
        public async Task It_should_return_all_existing_ids()
        {
            long[] idsToCheck = [_instance1Id, _instance2Id, _instance3Id];
            var result = await _repository.GetExistingDmsInstanceIds(idsToCheck);

            result.Should().BeOfType<DmsInstanceIdsExistResult.Success>();
            var existingIds = ((DmsInstanceIdsExistResult.Success)result).ExistingIds;
            existingIds.Should().HaveCount(3);
            existingIds.Should().Contain(_instance1Id);
            existingIds.Should().Contain(_instance2Id);
            existingIds.Should().Contain(_instance3Id);
        }

        [Test]
        public async Task It_should_return_only_existing_ids_when_some_dont_exist()
        {
            long[] idsToCheck = [_instance1Id, 99999, _instance2Id, 88888, _instance3Id];
            var result = await _repository.GetExistingDmsInstanceIds(idsToCheck);

            result.Should().BeOfType<DmsInstanceIdsExistResult.Success>();
            var existingIds = ((DmsInstanceIdsExistResult.Success)result).ExistingIds;
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
            var result = await _repository.GetExistingDmsInstanceIds(idsToCheck);

            result.Should().BeOfType<DmsInstanceIdsExistResult.Success>();
            var existingIds = ((DmsInstanceIdsExistResult.Success)result).ExistingIds;
            existingIds.Should().BeEmpty();
        }

        [Test]
        public async Task It_should_return_empty_set_when_input_is_empty()
        {
            long[] idsToCheck = [];
            var result = await _repository.GetExistingDmsInstanceIds(idsToCheck);

            result.Should().BeOfType<DmsInstanceIdsExistResult.Success>();
            var existingIds = ((DmsInstanceIdsExistResult.Success)result).ExistingIds;
            existingIds.Should().BeEmpty();
        }
    }

    [TestFixture]
    public class Given_dms_instance_is_assigned_to_applications : DmsInstanceTests
    {
        private long _dmsInstanceId1;
        private long _dmsInstanceId2;
        private long _unassignedInstanceId;
        private long _vendorId;

        [SetUp]
        public async Task Setup()
        {
            DmsInstanceInsertCommand instance1 = new()
            {
                InstanceType = "Production",
                InstanceName = "Test Instance",
                ConnectionString = "Server=localhost;Database=TestDb;User Id=user;Password=pass;",
            };

            var instance1Result = await _repository.InsertDmsInstance(instance1);
            instance1Result.Should().BeOfType<DmsInstanceInsertResult.Success>();
            _dmsInstanceId1 = (instance1Result as DmsInstanceInsertResult.Success)!.Id;
            _dmsInstanceId1.Should().BeGreaterThan(0);

            DmsInstanceInsertCommand instance2 = new()
            {
                InstanceType = "Staging",
                InstanceName = "Test Instance",
                ConnectionString = "Server=localhost;Database=TestDb;User Id=user;Password=pass;",
            };

            var instance2Result = await _repository.InsertDmsInstance(instance2);
            instance2Result.Should().BeOfType<DmsInstanceInsertResult.Success>();
            _dmsInstanceId2 = (instance2Result as DmsInstanceInsertResult.Success)!.Id;
            _dmsInstanceId2.Should().BeGreaterThan(0);

            DmsInstanceInsertCommand unassignedInstance = new()
            {
                InstanceType = "Unassigned",
                InstanceName = "Test Instance",
                ConnectionString = "Server=localhost;Database=TestDb;User Id=user;Password=pass;",
            };

            var unassignedInstanceResult = await _repository.InsertDmsInstance(unassignedInstance);
            unassignedInstanceResult.Should().BeOfType<DmsInstanceInsertResult.Success>();
            _unassignedInstanceId = (unassignedInstanceResult as DmsInstanceInsertResult.Success)!.Id;
            _unassignedInstanceId.Should().BeGreaterThan(0);

            var vendorRepository = new VendorRepository(
                Configuration.DatabaseOptions,
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
                Configuration.DatabaseOptions,
                NullLogger<ApplicationRepository>.Instance,
                new TestAuditContext()
            );

            ApplicationInsertCommand application = new()
            {
                ApplicationName = "Test Application",
                VendorId = _vendorId,
                ClaimSetName = "Test Claim set",
                EducationOrganizationIds = [1, 255911001, 255911002],
                DmsInstanceIds = [_dmsInstanceId1, _dmsInstanceId2]
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
                DmsInstanceIds = [_dmsInstanceId1]
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
                DmsInstanceIds = [_dmsInstanceId1]
            };

            var applicationWithoutEdOrgsResult = await applicationRepository.InsertApplication(
                applicationWithoutEdOrgs,
                new() { ClientId = Guid.NewGuid().ToString(), ClientUuid = Guid.NewGuid() }
            );
            applicationWithoutEdOrgsResult.Should().BeOfType<ApplicationInsertResult.Success>();
            var applicationWithoutEdOrgsId = (applicationWithoutEdOrgsResult as ApplicationInsertResult.Success)!.Id;
            applicationWithoutEdOrgsId.Should().BeGreaterThan(0);
        }

        [Test]
        public async Task It_should_retrieve_applications_by_dms_instance()
        {
            var queryResult = await _repository.QueryApplicationByDmsInstance(_dmsInstanceId1, new PagingQuery());
            queryResult.Should().BeOfType<ApplicationByDmsInstanceQueryResult.Success>();
            ((ApplicationByDmsInstanceQueryResult.Success)queryResult).ApplicationResponse.Count().Should().Be(3);

            var application1 = ((ApplicationByDmsInstanceQueryResult.Success)queryResult).ApplicationResponse.ToList()[0];
            application1.ApplicationName.Should().Be("Test Application");
            application1.VendorId.Should().Be(_vendorId);
            application1.ClaimSetName.Should().Be("Test Claim set");
            application1.EducationOrganizationIds.Should().BeEquivalentTo([1, 255911001, 255911002]);
            application1.DmsInstanceIds.Should().BeEquivalentTo([_dmsInstanceId1, _dmsInstanceId2]);
            application1.CreatedAt.Should().BeAfter(new DateTime());
            application1.CreatedBy.Should().NotBeEmpty();

            var application2 = ((ApplicationByDmsInstanceQueryResult.Success)queryResult).ApplicationResponse.ToList()[1];
            application2.ApplicationName.Should().Be("Test Application 2");
            application2.VendorId.Should().Be(_vendorId);
            application2.ClaimSetName.Should().Be("Test Claim set 2");
            application2.EducationOrganizationIds.Should().BeEquivalentTo([2, 255911002, 255911003]);
            application2.DmsInstanceIds.Should().BeEquivalentTo([_dmsInstanceId1]);
            application2.CreatedAt.Should().BeAfter(new DateTime());
            application2.CreatedBy.Should().NotBeEmpty();

            var applicationWithoutEdOrgs = ((ApplicationByDmsInstanceQueryResult.Success)queryResult).ApplicationResponse.ToList()[2];
            applicationWithoutEdOrgs.ApplicationName.Should().Be("Application without EdOrgs");
            applicationWithoutEdOrgs.VendorId.Should().Be(_vendorId);
            applicationWithoutEdOrgs.ClaimSetName.Should().Be("Test Claim set 3");
            applicationWithoutEdOrgs.EducationOrganizationIds.Should().BeEmpty();
            applicationWithoutEdOrgs.DmsInstanceIds.Should().BeEquivalentTo([_dmsInstanceId1]);
            applicationWithoutEdOrgs.CreatedAt.Should().BeAfter(new DateTime());
            applicationWithoutEdOrgs.CreatedBy.Should().NotBeEmpty();
        }

        [Test]
        public async Task It_should_retrieve_paged_applications_by_dms_instance()
        {
            var queryResult = await _repository.QueryApplicationByDmsInstance(_dmsInstanceId1, new PagingQuery() { Limit = 1, Offset = 1 });
            queryResult.Should().BeOfType<ApplicationByDmsInstanceQueryResult.Success>();
            ((ApplicationByDmsInstanceQueryResult.Success)queryResult).ApplicationResponse.Count().Should().Be(1);

            var application = ((ApplicationByDmsInstanceQueryResult.Success)queryResult).ApplicationResponse.ToList()[0];
            application.ApplicationName.Should().Be("Test Application 2");
            application.VendorId.Should().Be(_vendorId);
            application.ClaimSetName.Should().Be("Test Claim set 2");
            application.EducationOrganizationIds.Should().BeEquivalentTo([2, 255911002, 255911003]);
            application.DmsInstanceIds.Should().BeEquivalentTo([_dmsInstanceId1]);
            application.CreatedAt.Should().BeAfter(new DateTime());
            application.CreatedBy.Should().NotBeEmpty();
        }

        [Test]
        public async Task It_should_return_not_exists_failure()
        {
            var queryResult = await _repository.QueryApplicationByDmsInstance(0, new PagingQuery() { Limit = 25, Offset = 1 });
            queryResult.Should().BeOfType<ApplicationByDmsInstanceQueryResult.FailureNotExists>();
        }

        [Test]
        public async Task It_should_return_empty_array()
        {
            var queryResult = await _repository.QueryApplicationByDmsInstance(_unassignedInstanceId, new PagingQuery() { Limit = 25, Offset = 1 });
            queryResult.Should().BeOfType<ApplicationByDmsInstanceQueryResult.Success>();
            ((ApplicationByDmsInstanceQueryResult.Success)queryResult).ApplicationResponse.Should().BeEmpty();
        }
    }
}

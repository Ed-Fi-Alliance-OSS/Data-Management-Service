// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.Postgresql.Repositories;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.Backend.Services;
using EdFi.DmsConfigurationService.DataModel;
using EdFi.DmsConfigurationService.DataModel.Model;
using EdFi.DmsConfigurationService.DataModel.Model.DmsInstance;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration;

public class DmsInstanceTests : DatabaseTest
{
    private readonly IDmsInstanceRepository _repository = new DmsInstanceRepository(
        Configuration.DatabaseOptions,
        NullLogger<DmsInstanceRepository>.Instance,
        new ConnectionStringEncryptionService(Configuration.DatabaseOptions)
    );

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
}

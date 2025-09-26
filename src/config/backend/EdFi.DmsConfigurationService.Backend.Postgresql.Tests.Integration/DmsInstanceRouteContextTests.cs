// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.Postgresql.Repositories;
using EdFi.DmsConfigurationService.DataModel.Model.DmsInstanceRouteContext;
using EdFi.DmsConfigurationService.DataModel.Model;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.Backend.Services;
using EdFi.DmsConfigurationService.DataModel.Model.DmsInstance;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration;

public class DmsInstanceRouteContextTests : DatabaseTest
{
    private readonly DmsInstanceRouteContextRepository _repository = new DmsInstanceRouteContextRepository(
            Configuration.DatabaseOptions,
            NullLogger<DmsInstanceRouteContextRepository>.Instance
        );

    [TestFixture]
    public class InsertTests : DmsInstanceRouteContextTests
    {
        private long _id;
        private DmsInstanceRouteContextInsertCommand _insertCommand;
        private long _instanceId;

        [SetUp]
        public async Task Setup()
        {
            // Insert a DmsInstance and use its ID
            var instanceRepository = new DmsInstanceRepository(
                Configuration.DatabaseOptions,
                NullLogger<DmsInstanceRepository>.Instance,
                new ConnectionStringEncryptionService(Configuration.DatabaseOptions)
            );
            var instanceInsert = new DmsInstanceInsertCommand
            {
                InstanceType = "Production",
                InstanceName = "Test Instance",
                ConnectionString = "Server=localhost;Database=TestDb;User Id=user;Password=pass;"
            };
            var instanceResult = await instanceRepository.InsertDmsInstance(instanceInsert);
            instanceResult.Should().BeOfType<DmsInstanceInsertResult.Success>();
            _instanceId = ((DmsInstanceInsertResult.Success)instanceResult).Id;
            _instanceId.Should().BeGreaterThan(0);

            _insertCommand = new DmsInstanceRouteContextInsertCommand
            {
                InstanceId = _instanceId,
                ContextKey = "TestKey",
                ContextValue = "TestValue"
            };
            var result = await _repository.InsertDmsInstanceRouteContext(_insertCommand);
            result.Should().BeOfType<DmsInstanceRouteContextInsertResult.Success>();
            _id = ((DmsInstanceRouteContextInsertResult.Success)result).Id;
            _id.Should().BeGreaterThan(0);
        }

        [Test]
        public async Task Should_get_inserted_context_from_get_all()
        {
            var queryResult = await _repository.QueryInstanceRouteContext(new PagingQuery { Limit = 10, Offset = 0 });
            queryResult.Should().BeOfType<DmsInstanceRouteContextQueryResult.Success>();
            var contexts = ((DmsInstanceRouteContextQueryResult.Success)queryResult).DmsInstanceRouteContextResponses.ToList();
            contexts.Should().ContainSingle(c => c.ContextKey == "TestKey" && c.ContextValue == "TestValue");
        }

        [Test]
        public async Task Should_get_inserted_context_by_id()
        {
            var getResult = await _repository.GetInstanceRouteContext(_id);
            getResult.Should().BeOfType<DmsInstanceRouteContextGetResult.Success>();
            var context = ((DmsInstanceRouteContextGetResult.Success)getResult).DmsInstanceRouteContextResponse;
            context.ContextKey.Should().Be("TestKey");
            context.ContextValue.Should().Be("TestValue");
        }

        [Test]
        public async Task Should_fail_on_duplicate_insert()
        {
            var resultDup = await _repository.InsertDmsInstanceRouteContext(_insertCommand);
            resultDup.Should().BeOfType<DmsInstanceRouteContextInsertResult.FailureDuplicateDmsInstanceRouteContext>();
        }
    }

    [TestFixture]
    public class UpdateTests : DmsInstanceRouteContextTests
    {
        private long _id;
        private DmsInstanceRouteContextInsertCommand _insertCommand;
        private DmsInstanceRouteContextUpdateCommand _updateCommand;
        private long _instanceId;

        [SetUp]
        public async Task Setup()
        {
            // Insert a DmsInstance and use its ID
            var instanceRepository = new DmsInstanceRepository(
                Configuration.DatabaseOptions,
                NullLogger<DmsInstanceRepository>.Instance,
                new ConnectionStringEncryptionService(Configuration.DatabaseOptions)
            );
            var instanceInsert = new DmsInstanceInsertCommand
            {
                InstanceType = "Production",
                InstanceName = "Update Instance",
                ConnectionString = "Server=localhost;Database=TestDb;User Id=user;Password=pass;"
            };
            var instanceResult = await instanceRepository.InsertDmsInstance(instanceInsert);
            instanceResult.Should().BeOfType<DmsInstanceInsertResult.Success>();
            _instanceId = ((DmsInstanceInsertResult.Success)instanceResult).Id;
            _instanceId.Should().BeGreaterThan(0);

            _insertCommand = new DmsInstanceRouteContextInsertCommand
            {
                InstanceId = _instanceId,
                ContextKey = "UpdateKey",
                ContextValue = "InitialValue"
            };
            var insertResult = await _repository.InsertDmsInstanceRouteContext(_insertCommand);
            insertResult.Should().BeOfType<DmsInstanceRouteContextInsertResult.Success>();
            _id = ((DmsInstanceRouteContextInsertResult.Success)insertResult).Id;

            _updateCommand = new DmsInstanceRouteContextUpdateCommand
            {
                Id = _id,
                InstanceId = _instanceId,
                ContextKey = "UpdateKey",
                ContextValue = "UpdatedValue"
            };
            var updateResult = await _repository.UpdateDmsInstanceRouteContext(_updateCommand);
            updateResult.Should().BeOfType<DmsInstanceRouteContextUpdateResult.Success>();
        }

        [Test]
        public async Task Should_get_updated_context()
        {
            var getResult = await _repository.GetInstanceRouteContext(_id);
            getResult.Should().BeOfType<DmsInstanceRouteContextGetResult.Success>();
            var context = ((DmsInstanceRouteContextGetResult.Success)getResult).DmsInstanceRouteContextResponse;
            context.ContextValue.Should().Be("UpdatedValue");
        }
    }

    [TestFixture]
    public class DeleteTests : DmsInstanceRouteContextTests
    {
        private long _id;
        private DmsInstanceRouteContextInsertCommand _insertCommand;
        private long _instanceId;

        [SetUp]
        public async Task Setup()
        {
            // Insert a DmsInstance and use its ID
            var instanceRepository = new DmsInstanceRepository(
                Configuration.DatabaseOptions,
                NullLogger<DmsInstanceRepository>.Instance,
                new ConnectionStringEncryptionService(Configuration.DatabaseOptions)
            );
            var instanceInsert = new DmsInstanceInsertCommand
            {
                InstanceType = "Production",
                InstanceName = "Delete Instance",
                ConnectionString = "Server=localhost;Database=TestDb;User Id=user;Password=pass;"
            };
            var instanceResult = await instanceRepository.InsertDmsInstance(instanceInsert);
            instanceResult.Should().BeOfType<DmsInstanceInsertResult.Success>();
            _instanceId = ((DmsInstanceInsertResult.Success)instanceResult).Id;
            _instanceId.Should().BeGreaterThan(0);

            _insertCommand = new DmsInstanceRouteContextInsertCommand
            {
                InstanceId = _instanceId,
                ContextKey = "DeleteKey",
                ContextValue = "DeleteValue"
            };
            var insertResult = await _repository.InsertDmsInstanceRouteContext(_insertCommand);
            insertResult.Should().BeOfType<DmsInstanceRouteContextInsertResult.Success>();
            _id = ((DmsInstanceRouteContextInsertResult.Success)insertResult).Id;
        }

        [Test]
        public async Task Should_delete_context()
        {
            var deleteResult = await _repository.DeleteInstanceRouteContext(_id);
            deleteResult.Should().BeOfType<InstanceRouteContextDeleteResult.Success>();
            var getResult = await _repository.GetInstanceRouteContext(_id);
            getResult.Should().BeOfType<DmsInstanceRouteContextGetResult.FailureNotFound>();
        }
    }

    [TestFixture]
    public class QueryByInstanceTests : DmsInstanceRouteContextTests
    {
        private long _id1;
        private long _id2;
        private long _instanceId;

        [SetUp]
        public async Task Setup()
        {
            // Insert a DmsInstance and use its ID
            var instanceRepository = new DmsInstanceRepository(
                Configuration.DatabaseOptions,
                NullLogger<DmsInstanceRepository>.Instance,
                new ConnectionStringEncryptionService(Configuration.DatabaseOptions)
            );
            var instanceInsert = new DmsInstanceInsertCommand
            {
                InstanceType = "Production",
                InstanceName = "QueryByInstance Instance",
                ConnectionString = "Server=localhost;Database=TestDb;User Id=user;Password=pass;"
            };
            var instanceResult = await instanceRepository.InsertDmsInstance(instanceInsert);
            instanceResult.Should().BeOfType<DmsInstanceInsertResult.Success>();
            _instanceId = ((DmsInstanceInsertResult.Success)instanceResult).Id;
            _instanceId.Should().BeGreaterThan(0);

            var cmd1 = new DmsInstanceRouteContextInsertCommand
            {
                InstanceId = _instanceId,
                ContextKey = "Key1",
                ContextValue = "Value1"
            };
            var cmd2 = new DmsInstanceRouteContextInsertCommand
            {
                InstanceId = _instanceId,
                ContextKey = "Key2",
                ContextValue = "Value2"
            };
            var result1 = await _repository.InsertDmsInstanceRouteContext(cmd1);
            var result2 = await _repository.InsertDmsInstanceRouteContext(cmd2);
            _id1 = ((DmsInstanceRouteContextInsertResult.Success)result1).Id;
            _id2 = ((DmsInstanceRouteContextInsertResult.Success)result2).Id;
        }

        [Test]
        public async Task Should_query_contexts_by_instance()
        {
            var queryResult = await _repository.GetInstanceRouteContextsByInstance(_instanceId);
            queryResult.Should().BeOfType<InstanceRouteContextQueryByInstanceResult.Success>();
            var contexts = ((InstanceRouteContextQueryByInstanceResult.Success)queryResult).DmsInstanceRouteContextResponses.ToList();
            contexts.Should().HaveCount(2);
            contexts.Any(c => c.ContextKey == "Key1" && c.ContextValue == "Value1").Should().BeTrue();
            contexts.Any(c => c.ContextKey == "Key2" && c.ContextValue == "Value2").Should().BeTrue();
        }
    }
}

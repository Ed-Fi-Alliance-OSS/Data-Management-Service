// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.Mssql.Repositories;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.Backend.Services;
using EdFi.DmsConfigurationService.DataModel.Model;
using EdFi.DmsConfigurationService.DataModel.Model.DataStore;
using EdFi.DmsConfigurationService.DataModel.Model.DataStoreContext;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration;

public class DataStoreContextTests : DatabaseTest
{
    private readonly DataStoreContextRepository _repository = new(
        MssqlTestConfiguration.DatabaseOptions,
        NullLogger<DataStoreContextRepository>.Instance,
        new TestAuditContext(),
        new TenantContextProvider()
    );

    [TestFixture]
    public class InsertTests : DataStoreContextTests
    {
        private long _id;
        private DataStoreContextInsertCommand _insertCommand;
        private long _dataStoreId;

        [SetUp]
        public async Task Setup()
        {
            var derivativeRepository = new DataStoreDerivativeRepository(
                MssqlTestConfiguration.DatabaseOptions,
                NullLogger<DataStoreDerivativeRepository>.Instance,
                new ConnectionStringEncryptionService(MssqlTestConfiguration.DatabaseOptions),
                new TestAuditContext(),
                new TenantContextProvider()
            );
            var instanceRepository = new DataStoreRepository(
                MssqlTestConfiguration.DatabaseOptions,
                NullLogger<DataStoreRepository>.Instance,
                new ConnectionStringEncryptionService(MssqlTestConfiguration.DatabaseOptions),
                _repository,
                derivativeRepository,
                new TestAuditContext(),
                new TenantContextProvider()
            );
            var instanceInsert = new DataStoreInsertCommand
            {
                DataStoreType = "Production",
                Name = "Test Instance",
                ConnectionString = "Server=localhost;Database=TestDb;User Id=user;Password=pass;",
            };
            var instanceResult = await instanceRepository.InsertDataStore(instanceInsert);
            instanceResult.Should().BeOfType<DataStoreInsertResult.Success>();
            _dataStoreId = ((DataStoreInsertResult.Success)instanceResult).Id;
            _dataStoreId.Should().BeGreaterThan(0);

            _insertCommand = new DataStoreContextInsertCommand
            {
                DataStoreId = _dataStoreId,
                ContextKey = "TestKey",
                ContextValue = "TestValue",
            };
            var result = await _repository.InsertDataStoreContext(_insertCommand);
            result.Should().BeOfType<DataStoreContextInsertResult.Success>();
            _id = ((DataStoreContextInsertResult.Success)result).Id;
            _id.Should().BeGreaterThan(0);
        }

        [Test]
        public async Task Should_get_inserted_context_from_get_all()
        {
            var queryResult = await _repository.QueryDataStoreContext(
                new PagingQuery { Limit = 10, Offset = 0 }
            );
            queryResult.Should().BeOfType<DataStoreContextQueryResult.Success>();
            var contexts = (
                (DataStoreContextQueryResult.Success)queryResult
            ).DataStoreContextResponses.ToList();
            contexts.Should().ContainSingle(c => c.ContextKey == "TestKey" && c.ContextValue == "TestValue");
        }

        [Test]
        public async Task Should_get_inserted_context_by_id()
        {
            var getResult = await _repository.GetDataStoreContext(_id);
            getResult.Should().BeOfType<DataStoreContextGetResult.Success>();
            var context = ((DataStoreContextGetResult.Success)getResult).DataStoreContextResponse;
            context.ContextKey.Should().Be("TestKey");
            context.ContextValue.Should().Be("TestValue");
        }

        [Test]
        public async Task Should_fail_on_duplicate_insert()
        {
            var resultDup = await _repository.InsertDataStoreContext(_insertCommand);
            resultDup.Should().BeOfType<DataStoreContextInsertResult.FailureDuplicateDataStoreContext>();
        }
    }

    [TestFixture]
    public class UpdateTests : DataStoreContextTests
    {
        private long _id;
        private DataStoreContextInsertCommand _insertCommand;
        private DataStoreContextUpdateCommand _updateCommand;
        private long _dataStoreId;

        [SetUp]
        public async Task Setup()
        {
            // Insert a DataStore and use its ID
            var derivativeRepository = new DataStoreDerivativeRepository(
                MssqlTestConfiguration.DatabaseOptions,
                NullLogger<DataStoreDerivativeRepository>.Instance,
                new ConnectionStringEncryptionService(MssqlTestConfiguration.DatabaseOptions),
                new TestAuditContext(),
                new TenantContextProvider()
            );
            var instanceRepository = new DataStoreRepository(
                MssqlTestConfiguration.DatabaseOptions,
                NullLogger<DataStoreRepository>.Instance,
                new ConnectionStringEncryptionService(MssqlTestConfiguration.DatabaseOptions),
                _repository,
                derivativeRepository,
                new TestAuditContext(),
                new TenantContextProvider()
            );
            var instanceInsert = new DataStoreInsertCommand
            {
                DataStoreType = "Production",
                Name = "Update Instance",
                ConnectionString = "Server=localhost;Database=TestDb;User Id=user;Password=pass;",
            };
            var instanceResult = await instanceRepository.InsertDataStore(instanceInsert);
            instanceResult.Should().BeOfType<DataStoreInsertResult.Success>();
            _dataStoreId = ((DataStoreInsertResult.Success)instanceResult).Id;
            _dataStoreId.Should().BeGreaterThan(0);

            _insertCommand = new DataStoreContextInsertCommand
            {
                DataStoreId = _dataStoreId,
                ContextKey = "UpdateKey",
                ContextValue = "InitialValue",
            };
            var insertResult = await _repository.InsertDataStoreContext(_insertCommand);
            insertResult.Should().BeOfType<DataStoreContextInsertResult.Success>();
            _id = ((DataStoreContextInsertResult.Success)insertResult).Id;

            _updateCommand = new DataStoreContextUpdateCommand
            {
                Id = _id,
                DataStoreId = _dataStoreId,
                ContextKey = "UpdateKey",
                ContextValue = "UpdatedValue",
            };
            var updateResult = await _repository.UpdateDataStoreContext(_updateCommand);
            updateResult.Should().BeOfType<DataStoreContextUpdateResult.Success>();
        }

        [Test]
        public async Task Should_get_updated_context()
        {
            var getResult = await _repository.GetDataStoreContext(_id);
            getResult.Should().BeOfType<DataStoreContextGetResult.Success>();
            var context = ((DataStoreContextGetResult.Success)getResult).DataStoreContextResponse;
            context.ContextValue.Should().Be("UpdatedValue");
        }
    }

    [TestFixture]
    public class DeleteTests : DataStoreContextTests
    {
        private long _id;
        private DataStoreContextInsertCommand _insertCommand;
        private long _dataStoreId;

        [SetUp]
        public async Task Setup()
        {
            // Insert a DataStore and use its ID
            var derivativeRepository = new DataStoreDerivativeRepository(
                MssqlTestConfiguration.DatabaseOptions,
                NullLogger<DataStoreDerivativeRepository>.Instance,
                new ConnectionStringEncryptionService(MssqlTestConfiguration.DatabaseOptions),
                new TestAuditContext(),
                new TenantContextProvider()
            );
            var instanceRepository = new DataStoreRepository(
                MssqlTestConfiguration.DatabaseOptions,
                NullLogger<DataStoreRepository>.Instance,
                new ConnectionStringEncryptionService(MssqlTestConfiguration.DatabaseOptions),
                _repository,
                derivativeRepository,
                new TestAuditContext(),
                new TenantContextProvider()
            );
            var instanceInsert = new DataStoreInsertCommand
            {
                DataStoreType = "Production",
                Name = "Delete Instance",
                ConnectionString = "Server=localhost;Database=TestDb;User Id=user;Password=pass;",
            };
            var instanceResult = await instanceRepository.InsertDataStore(instanceInsert);
            instanceResult.Should().BeOfType<DataStoreInsertResult.Success>();
            _dataStoreId = ((DataStoreInsertResult.Success)instanceResult).Id;
            _dataStoreId.Should().BeGreaterThan(0);

            _insertCommand = new DataStoreContextInsertCommand
            {
                DataStoreId = _dataStoreId,
                ContextKey = "DeleteKey",
                ContextValue = "DeleteValue",
            };
            var insertResult = await _repository.InsertDataStoreContext(_insertCommand);
            insertResult.Should().BeOfType<DataStoreContextInsertResult.Success>();
            _id = ((DataStoreContextInsertResult.Success)insertResult).Id;
        }

        [Test]
        public async Task Should_delete_context()
        {
            var deleteResult = await _repository.DeleteDataStoreContext(_id);
            deleteResult.Should().BeOfType<DataStoreContextDeleteResult.Success>();
            var getResult = await _repository.GetDataStoreContext(_id);
            getResult.Should().BeOfType<DataStoreContextGetResult.FailureNotFound>();
        }
    }

    [TestFixture]
    public class QueryByInstanceTests : DataStoreContextTests
    {
        private long _dataStoreId;

        [SetUp]
        public async Task Setup()
        {
            // Insert a DataStore and use its ID
            var derivativeRepository = new DataStoreDerivativeRepository(
                MssqlTestConfiguration.DatabaseOptions,
                NullLogger<DataStoreDerivativeRepository>.Instance,
                new ConnectionStringEncryptionService(MssqlTestConfiguration.DatabaseOptions),
                new TestAuditContext(),
                new TenantContextProvider()
            );
            var instanceRepository = new DataStoreRepository(
                MssqlTestConfiguration.DatabaseOptions,
                NullLogger<DataStoreRepository>.Instance,
                new ConnectionStringEncryptionService(MssqlTestConfiguration.DatabaseOptions),
                _repository,
                derivativeRepository,
                new TestAuditContext(),
                new TenantContextProvider()
            );
            var instanceInsert = new DataStoreInsertCommand
            {
                DataStoreType = "Production",
                Name = "QueryByInstance Instance",
                ConnectionString = "Server=localhost;Database=TestDb;User Id=user;Password=pass;",
            };
            var instanceResult = await instanceRepository.InsertDataStore(instanceInsert);
            instanceResult.Should().BeOfType<DataStoreInsertResult.Success>();
            _dataStoreId = ((DataStoreInsertResult.Success)instanceResult).Id;
            _dataStoreId.Should().BeGreaterThan(0);

            var cmd1 = new DataStoreContextInsertCommand
            {
                DataStoreId = _dataStoreId,
                ContextKey = "Key1",
                ContextValue = "Value1",
            };
            var cmd2 = new DataStoreContextInsertCommand
            {
                DataStoreId = _dataStoreId,
                ContextKey = "Key2",
                ContextValue = "Value2",
            };
            var result1 = await _repository.InsertDataStoreContext(cmd1);
            var result2 = await _repository.InsertDataStoreContext(cmd2);
            result1.Should().BeOfType<DataStoreContextInsertResult.Success>();
            result2.Should().BeOfType<DataStoreContextInsertResult.Success>();
        }

        [Test]
        public async Task Should_query_contexts_by_instance()
        {
            var queryResult = await _repository.GetDataStoreContextsByDataStore(_dataStoreId);
            queryResult.Should().BeOfType<DataStoreContextQueryByDataStoreResult.Success>();
            var contexts = (
                (DataStoreContextQueryByDataStoreResult.Success)queryResult
            ).DataStoreContextResponses.ToList();
            contexts.Should().HaveCount(2);
            contexts.Exists(c => c.ContextKey == "Key1" && c.ContextValue == "Value1").Should().BeTrue();
            contexts.Exists(c => c.ContextKey == "Key2" && c.ContextValue == "Value2").Should().BeTrue();
        }
    }

    [TestFixture]
    public class QueryPagingTests : DataStoreContextTests
    {
        private long _dataStoreId;

        [SetUp]
        public async Task Setup()
        {
            var derivativeRepository = new DataStoreDerivativeRepository(
                MssqlTestConfiguration.DatabaseOptions,
                NullLogger<DataStoreDerivativeRepository>.Instance,
                new ConnectionStringEncryptionService(MssqlTestConfiguration.DatabaseOptions),
                new TestAuditContext(),
                new TenantContextProvider()
            );
            var instanceRepository = new DataStoreRepository(
                MssqlTestConfiguration.DatabaseOptions,
                NullLogger<DataStoreRepository>.Instance,
                new ConnectionStringEncryptionService(MssqlTestConfiguration.DatabaseOptions),
                _repository,
                derivativeRepository,
                new TestAuditContext(),
                new TenantContextProvider()
            );
            var instanceInsert = new DataStoreInsertCommand
            {
                DataStoreType = "Production",
                Name = "Paging RouteContext Instance",
                ConnectionString = "Server=localhost;Database=TestDb;User Id=user;Password=pass;",
            };
            var instanceResult = await instanceRepository.InsertDataStore(instanceInsert);
            instanceResult.Should().BeOfType<DataStoreInsertResult.Success>();
            _dataStoreId = ((DataStoreInsertResult.Success)instanceResult).Id;

            foreach (
                var (contextKey, contextValue) in new[]
                {
                    ("Alpha", "ValueA"),
                    ("Bravo", "ValueB"),
                    ("Charlie", "ValueC"),
                }
            )
            {
                var insertResult = await _repository.InsertDataStoreContext(
                    new DataStoreContextInsertCommand
                    {
                        DataStoreId = _dataStoreId,
                        ContextKey = contextKey,
                        ContextValue = contextValue,
                    }
                );
                insertResult.Should().BeOfType<DataStoreContextInsertResult.Success>();
            }
        }

        [Test]
        public async Task Should_return_all_results_when_no_paging_params_provided()
        {
            var queryResult = await _repository.QueryDataStoreContext(new PagingQuery());
            queryResult.Should().BeOfType<DataStoreContextQueryResult.Success>();
            ((DataStoreContextQueryResult.Success)queryResult)
                .DataStoreContextResponses.Should()
                .HaveCount(3);
        }

        [Test]
        public async Task Should_apply_limit_when_limit_is_provided()
        {
            var queryResult = await _repository.QueryDataStoreContext(new PagingQuery { Limit = 2 });
            queryResult.Should().BeOfType<DataStoreContextQueryResult.Success>();
            ((DataStoreContextQueryResult.Success)queryResult)
                .DataStoreContextResponses.Should()
                .HaveCount(2);
        }

        [Test]
        public async Task Should_apply_offset_when_offset_is_provided()
        {
            var queryResult = await _repository.QueryDataStoreContext(new PagingQuery { Offset = 1 });
            queryResult.Should().BeOfType<DataStoreContextQueryResult.Success>();
            ((DataStoreContextQueryResult.Success)queryResult)
                .DataStoreContextResponses.Should()
                .HaveCount(2);
        }
    }

    [TestFixture]
    public class QuerySortTests : DataStoreContextTests
    {
        private long _dataStoreId;

        [SetUp]
        public async Task Setup()
        {
            var derivativeRepository = new DataStoreDerivativeRepository(
                MssqlTestConfiguration.DatabaseOptions,
                NullLogger<DataStoreDerivativeRepository>.Instance,
                new ConnectionStringEncryptionService(MssqlTestConfiguration.DatabaseOptions),
                new TestAuditContext(),
                new TenantContextProvider()
            );
            var instanceRepository = new DataStoreRepository(
                MssqlTestConfiguration.DatabaseOptions,
                NullLogger<DataStoreRepository>.Instance,
                new ConnectionStringEncryptionService(MssqlTestConfiguration.DatabaseOptions),
                _repository,
                derivativeRepository,
                new TestAuditContext(),
                new TenantContextProvider()
            );
            var instanceInsert = new DataStoreInsertCommand
            {
                DataStoreType = "Production",
                Name = "Sort RouteContext Instance",
                ConnectionString = "Server=localhost;Database=TestDb;User Id=user;Password=pass;",
            };
            var instanceResult = await instanceRepository.InsertDataStore(instanceInsert);
            instanceResult.Should().BeOfType<DataStoreInsertResult.Success>();
            _dataStoreId = ((DataStoreInsertResult.Success)instanceResult).Id;

            foreach (
                var (contextKey, contextValue) in new[]
                {
                    ("Charlie", "ValueC"),
                    ("Alpha", "ValueA"),
                    ("Bravo", "ValueB"),
                }
            )
            {
                var insertResult = await _repository.InsertDataStoreContext(
                    new DataStoreContextInsertCommand
                    {
                        DataStoreId = _dataStoreId,
                        ContextKey = contextKey,
                        ContextValue = contextValue,
                    }
                );
                insertResult.Should().BeOfType<DataStoreContextInsertResult.Success>();
            }
        }

        [Test]
        public async Task Should_return_ascending_order_by_context_key()
        {
            var queryResult = await _repository.QueryDataStoreContext(
                new PagingQuery { OrderBy = "contextKey", Direction = "ASC" }
            );
            queryResult.Should().BeOfType<DataStoreContextQueryResult.Success>();
            var contextKeys = ((DataStoreContextQueryResult.Success)queryResult)
                .DataStoreContextResponses.Select(c => c.ContextKey)
                .ToList();
            contextKeys.Should().ContainInOrder("Alpha", "Bravo", "Charlie");
        }

        [Test]
        public async Task Should_return_descending_order_by_context_key()
        {
            var queryResult = await _repository.QueryDataStoreContext(
                new PagingQuery { OrderBy = "contextKey", Direction = "DESC" }
            );
            queryResult.Should().BeOfType<DataStoreContextQueryResult.Success>();
            var contextKeys = ((DataStoreContextQueryResult.Success)queryResult)
                .DataStoreContextResponses.Select(c => c.ContextKey)
                .ToList();
            contextKeys.Should().ContainInOrder("Charlie", "Bravo", "Alpha");
        }
    }
}

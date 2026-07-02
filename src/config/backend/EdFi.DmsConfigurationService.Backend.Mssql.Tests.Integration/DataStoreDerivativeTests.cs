// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.Mssql.Repositories;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.Backend.Services;
using EdFi.DmsConfigurationService.DataModel.Model;
using EdFi.DmsConfigurationService.DataModel.Model.DataStore;
using EdFi.DmsConfigurationService.DataModel.Model.DataStoreDerivative;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration;

public class DataStoreDerivativeTests : DatabaseTest
{
    private static void AssertIsValidEncryptedBase64(string? base64, string expectedPlainText)
    {
        base64.Should().NotBeNullOrEmpty();
        var encryptedBytes = Convert.FromBase64String(base64!);
        var encryptionService = new ConnectionStringEncryptionService(MssqlTestConfiguration.DatabaseOptions);
        encryptionService.Decrypt(encryptedBytes).Should().Be(expectedPlainText);
    }

    private readonly IDataStoreRepository _instanceRepository;
    private readonly IDataStoreDerivativeRepository _repository;

    public DataStoreDerivativeTests()
    {
        var routeContextRepository = new DataStoreContextRepository(
            MssqlTestConfiguration.DatabaseOptions,
            NullLogger<DataStoreContextRepository>.Instance,
            new TestAuditContext(),
            new TenantContextProvider()
        );

        _repository = new DataStoreDerivativeRepository(
            MssqlTestConfiguration.DatabaseOptions,
            NullLogger<DataStoreDerivativeRepository>.Instance,
            new ConnectionStringEncryptionService(MssqlTestConfiguration.DatabaseOptions),
            new TestAuditContext()
        );

        _instanceRepository = new DataStoreRepository(
            MssqlTestConfiguration.DatabaseOptions,
            NullLogger<DataStoreRepository>.Instance,
            new ConnectionStringEncryptionService(MssqlTestConfiguration.DatabaseOptions),
            routeContextRepository,
            _repository,
            new TestAuditContext(),
            new TenantContextProvider()
        );
    }

    [TestFixture]
    public class Given_insert_data_store_derivative : DataStoreDerivativeTests
    {
        private long _dataStoreId;
        private long _derivativeId;

        [SetUp]
        public async Task Setup()
        {
            // Create parent instance first
            var instanceResult = await _instanceRepository.InsertDataStore(
                new DataStoreInsertCommand
                {
                    DataStoreType = "Production",
                    Name = "Parent Instance",
                    ConnectionString = "Server=parent;Database=ParentDb;",
                }
            );
            _dataStoreId = ((DataStoreInsertResult.Success)instanceResult).Id;

            // Create derivative
            DataStoreDerivativeInsertCommand derivative = new()
            {
                DataStoreId = _dataStoreId,
                DerivativeType = "ReadReplica",
                ConnectionString = "Server=replica;Database=ReplicaDb;User Id=user;Password=pass;",
            };

            var result = await _repository.InsertDataStoreDerivative(derivative);
            result.Should().BeOfType<DataStoreDerivativeInsertResult.Success>();
            _derivativeId = (result as DataStoreDerivativeInsertResult.Success)!.Id;
            _derivativeId.Should().BeGreaterThan(0);
        }

        [Test]
        public async Task It_should_retrieve_derivative_from_query()
        {
            var getResult = await _repository.QueryDataStoreDerivative(
                new PagingQuery() { Limit = 25, Offset = 0 }
            );
            getResult.Should().BeOfType<DataStoreDerivativeQueryResult.Success>();

            var derivativeFromDb = (
                (DataStoreDerivativeQueryResult.Success)getResult
            ).DataStoreDerivativeResponses.First();
            derivativeFromDb.DataStoreId.Should().Be(_dataStoreId);
            derivativeFromDb.DerivativeType.Should().Be("ReadReplica");
            AssertIsValidEncryptedBase64(
                derivativeFromDb.ConnectionString,
                "Server=replica;Database=ReplicaDb;User Id=user;Password=pass;"
            );
        }

        [Test]
        public async Task It_should_retrieve_derivative_from_get_by_id()
        {
            var getByIdResult = await _repository.GetDataStoreDerivative(_derivativeId);
            getByIdResult.Should().BeOfType<DataStoreDerivativeGetResult.Success>();

            var derivativeFromDb = (
                (DataStoreDerivativeGetResult.Success)getByIdResult
            ).DataStoreDerivativeResponse;
            derivativeFromDb.DataStoreId.Should().Be(_dataStoreId);
            derivativeFromDb.DerivativeType.Should().Be("ReadReplica");
            AssertIsValidEncryptedBase64(
                derivativeFromDb.ConnectionString,
                "Server=replica;Database=ReplicaDb;User Id=user;Password=pass;"
            );
        }
    }

    [TestFixture]
    public class Given_insert_data_store_derivative_with_snapshot_type : DataStoreDerivativeTests
    {
        private long _dataStoreId;
        private long _derivativeId;

        [SetUp]
        public async Task Setup()
        {
            // Create parent instance first
            var instanceResult = await _instanceRepository.InsertDataStore(
                new DataStoreInsertCommand
                {
                    DataStoreType = "Production",
                    Name = "Parent Instance",
                    ConnectionString = "Server=parent;Database=ParentDb;",
                }
            );
            _dataStoreId = ((DataStoreInsertResult.Success)instanceResult).Id;

            // Create derivative
            DataStoreDerivativeInsertCommand derivative = new()
            {
                DataStoreId = _dataStoreId,
                DerivativeType = "Snapshot",
                ConnectionString = "Server=snapshot;Database=SnapshotDb;",
            };

            var result = await _repository.InsertDataStoreDerivative(derivative);
            result.Should().BeOfType<DataStoreDerivativeInsertResult.Success>();
            _derivativeId = (result as DataStoreDerivativeInsertResult.Success)!.Id;
        }

        [Test]
        public async Task It_should_retrieve_derivative_with_snapshot_type()
        {
            var getByIdResult = await _repository.GetDataStoreDerivative(_derivativeId);
            getByIdResult.Should().BeOfType<DataStoreDerivativeGetResult.Success>();

            var derivativeFromDb = (
                (DataStoreDerivativeGetResult.Success)getByIdResult
            ).DataStoreDerivativeResponse;
            derivativeFromDb.DerivativeType.Should().Be("Snapshot");
        }
    }

    [TestFixture]
    public class Given_insert_data_store_derivative_without_connection_string : DataStoreDerivativeTests
    {
        private long _dataStoreId;
        private long _derivativeId;

        [SetUp]
        public async Task Setup()
        {
            // Create parent instance first
            var instanceResult = await _instanceRepository.InsertDataStore(
                new DataStoreInsertCommand
                {
                    DataStoreType = "Development",
                    Name = "Parent Instance",
                    ConnectionString = "Server=parent;Database=ParentDb;",
                }
            );
            _dataStoreId = ((DataStoreInsertResult.Success)instanceResult).Id;

            // Create derivative without connection string
            DataStoreDerivativeInsertCommand derivative = new()
            {
                DataStoreId = _dataStoreId,
                DerivativeType = "ReadReplica",
                ConnectionString = null,
            };

            var result = await _repository.InsertDataStoreDerivative(derivative);
            result.Should().BeOfType<DataStoreDerivativeInsertResult.Success>();
            _derivativeId = (result as DataStoreDerivativeInsertResult.Success)!.Id;
        }

        [Test]
        public async Task It_should_retrieve_derivative_with_null_connection_string()
        {
            var getByIdResult = await _repository.GetDataStoreDerivative(_derivativeId);
            getByIdResult.Should().BeOfType<DataStoreDerivativeGetResult.Success>();

            var derivativeFromDb = (
                (DataStoreDerivativeGetResult.Success)getByIdResult
            ).DataStoreDerivativeResponse;
            derivativeFromDb.ConnectionString.Should().BeNull();
        }
    }

    [TestFixture]
    public class Given_update_data_store_derivative : DataStoreDerivativeTests
    {
        private long _dataStoreId;
        private DataStoreDerivativeInsertCommand _derivativeInsert = null!;
        private DataStoreDerivativeUpdateCommand _derivativeUpdate = null!;

        [SetUp]
        public async Task Setup()
        {
            // Create parent instance first
            var instanceResult = await _instanceRepository.InsertDataStore(
                new DataStoreInsertCommand
                {
                    DataStoreType = "Staging",
                    Name = "Parent Instance",
                    ConnectionString = "Server=parent;Database=ParentDb;",
                }
            );
            _dataStoreId = ((DataStoreInsertResult.Success)instanceResult).Id;

            _derivativeInsert = new()
            {
                DataStoreId = _dataStoreId,
                DerivativeType = "ReadReplica",
                ConnectionString = "Server=original;Database=OriginalDb;",
            };

            _derivativeUpdate = new()
            {
                DataStoreId = _dataStoreId,
                DerivativeType = "Snapshot",
                ConnectionString = "Server=updated;Database=UpdatedDb;",
            };

            var insertResult = await _repository.InsertDataStoreDerivative(_derivativeInsert);
            insertResult.Should().BeOfType<DataStoreDerivativeInsertResult.Success>();

            _derivativeUpdate.Id = (insertResult as DataStoreDerivativeInsertResult.Success)!.Id;

            var updateResult = await _repository.UpdateDataStoreDerivative(_derivativeUpdate);
            updateResult.Should().BeOfType<DataStoreDerivativeUpdateResult.Success>();
        }

        [Test]
        public async Task It_should_retrieve_updated_derivative_from_query()
        {
            var getResult = await _repository.QueryDataStoreDerivative(
                new PagingQuery() { Limit = 25, Offset = 0 }
            );
            getResult.Should().BeOfType<DataStoreDerivativeQueryResult.Success>();

            var derivativeFromDb = (
                (DataStoreDerivativeQueryResult.Success)getResult
            ).DataStoreDerivativeResponses.First();
            derivativeFromDb.DerivativeType.Should().Be("Snapshot");
            AssertIsValidEncryptedBase64(
                derivativeFromDb.ConnectionString,
                "Server=updated;Database=UpdatedDb;"
            );
        }

        [Test]
        public async Task It_should_retrieve_updated_derivative_from_get_by_id()
        {
            var getByIdResult = await _repository.GetDataStoreDerivative(_derivativeUpdate.Id);
            getByIdResult.Should().BeOfType<DataStoreDerivativeGetResult.Success>();

            var derivativeFromDb = (
                (DataStoreDerivativeGetResult.Success)getByIdResult
            ).DataStoreDerivativeResponse;
            derivativeFromDb.DerivativeType.Should().Be("Snapshot");
            AssertIsValidEncryptedBase64(
                derivativeFromDb.ConnectionString,
                "Server=updated;Database=UpdatedDb;"
            );
        }
    }

    [TestFixture]
    public class Given_delete_data_store_derivative : DataStoreDerivativeTests
    {
        private long _dataStoreId;
        private long _derivative1Id;
        private long _derivative2Id;

        [SetUp]
        public async Task Setup()
        {
            // Create parent instance
            var instanceResult = await _instanceRepository.InsertDataStore(
                new DataStoreInsertCommand
                {
                    DataStoreType = "Production",
                    Name = "Parent Instance",
                    ConnectionString = "Server=parent;Database=ParentDb;",
                }
            );
            _dataStoreId = ((DataStoreInsertResult.Success)instanceResult).Id;

            var insertResult1 = await _repository.InsertDataStoreDerivative(
                new DataStoreDerivativeInsertCommand()
                {
                    DataStoreId = _dataStoreId,
                    DerivativeType = "ReadReplica",
                    ConnectionString = "Server=delete;Database=DeleteDb;",
                }
            );

            _derivative1Id = ((DataStoreDerivativeInsertResult.Success)insertResult1).Id;

            var insertResult2 = await _repository.InsertDataStoreDerivative(
                new DataStoreDerivativeInsertCommand()
                {
                    DataStoreId = _dataStoreId,
                    DerivativeType = "Snapshot",
                    ConnectionString = "Server=keep;Database=KeepDb;",
                }
            );

            _derivative2Id = ((DataStoreDerivativeInsertResult.Success)insertResult2).Id;

            var deleteResult = await _repository.DeleteDataStoreDerivative(_derivative1Id);
            deleteResult.Should().BeOfType<DataStoreDerivativeDeleteResult.Success>();
        }

        [Test]
        public async Task It_should_not_retrieve_deleted_derivative_from_query()
        {
            var getResult = await _repository.QueryDataStoreDerivative(
                new PagingQuery() { Limit = 25, Offset = 0 }
            );
            getResult.Should().BeOfType<DataStoreDerivativeQueryResult.Success>();

            var derivatives = (
                (DataStoreDerivativeQueryResult.Success)getResult
            ).DataStoreDerivativeResponses;
            derivatives.Count().Should().Be(1);
            derivatives.Count(d => d.Id == _derivative1Id).Should().Be(0);
            derivatives.Count(d => d.DerivativeType == "ReadReplica").Should().Be(0);
            derivatives.Count(d => d.Id == _derivative2Id).Should().Be(1);
            derivatives.Count(d => d.DerivativeType == "Snapshot").Should().Be(1);
        }

        [Test]
        public async Task It_should_return_not_found_for_deleted_derivative_get_by_id()
        {
            var getByIdResult = await _repository.GetDataStoreDerivative(_derivative1Id);
            getByIdResult.Should().BeOfType<DataStoreDerivativeGetResult.FailureNotFound>();

            getByIdResult = await _repository.GetDataStoreDerivative(_derivative2Id);
            getByIdResult.Should().BeOfType<DataStoreDerivativeGetResult.Success>();
        }
    }

    [TestFixture]
    public class Given_update_non_existent_data_store_derivative : DataStoreDerivativeTests
    {
        [Test]
        public async Task It_should_return_failure_not_found()
        {
            var updateCommand = new DataStoreDerivativeUpdateCommand()
            {
                Id = 9999,
                DataStoreId = 1,
                DerivativeType = "ReadReplica",
                ConnectionString = "Server=fake;Database=FakeDb;",
            };

            var result = await _repository.UpdateDataStoreDerivative(updateCommand);
            result.Should().BeOfType<DataStoreDerivativeUpdateResult.FailureNotFound>();
        }
    }

    [TestFixture]
    public class Given_delete_non_existent_data_store_derivative : DataStoreDerivativeTests
    {
        [Test]
        public async Task It_should_return_failure_not_found()
        {
            var result = await _repository.DeleteDataStoreDerivative(9999);
            result.Should().BeOfType<DataStoreDerivativeDeleteResult.FailureNotFound>();
        }
    }

    [TestFixture]
    public class Given_get_non_existent_data_store_derivative : DataStoreDerivativeTests
    {
        [Test]
        public async Task It_should_return_failure_not_found()
        {
            var result = await _repository.GetDataStoreDerivative(9999);
            result.Should().BeOfType<DataStoreDerivativeGetResult.FailureNotFound>();
        }
    }

    [TestFixture]
    public class Given_insert_derivative_with_invalid_instance_id : DataStoreDerivativeTests
    {
        [Test]
        public async Task It_should_return_failure_foreign_key_violation()
        {
            DataStoreDerivativeInsertCommand derivative = new()
            {
                DataStoreId = 99999,
                DerivativeType = "ReadReplica",
                ConnectionString = "Server=test;Database=TestDb;",
            };

            var result = await _repository.InsertDataStoreDerivative(derivative);
            result.Should().BeOfType<DataStoreDerivativeInsertResult.FailureForeignKeyViolation>();
        }
    }

    [TestFixture]
    public class QueryPagingTests : DataStoreDerivativeTests
    {
        [SetUp]
        public async Task Setup()
        {
            var instanceResult = await _instanceRepository.InsertDataStore(
                new DataStoreInsertCommand
                {
                    DataStoreType = "Production",
                    Name = "Paging Parent Instance",
                    ConnectionString = "Server=parent;Database=ParentDb;",
                }
            );
            var dataStoreId = ((DataStoreInsertResult.Success)instanceResult).Id;

            foreach (var derivativeType in new[] { "Alpha", "Bravo", "Charlie" })
            {
                var insertResult = await _repository.InsertDataStoreDerivative(
                    new DataStoreDerivativeInsertCommand
                    {
                        DataStoreId = dataStoreId,
                        DerivativeType = derivativeType,
                        ConnectionString = $"Server={derivativeType};Database={derivativeType}Db;",
                    }
                );
                insertResult.Should().BeOfType<DataStoreDerivativeInsertResult.Success>();
            }
        }

        [Test]
        public async Task It_should_return_all_results_when_no_paging_params_provided()
        {
            var getResult = await _repository.QueryDataStoreDerivative(new PagingQuery());
            getResult.Should().BeOfType<DataStoreDerivativeQueryResult.Success>();
            ((DataStoreDerivativeQueryResult.Success)getResult)
                .DataStoreDerivativeResponses.Should()
                .HaveCount(3);
        }

        [Test]
        public async Task It_should_apply_limit_when_limit_is_provided()
        {
            var getResult = await _repository.QueryDataStoreDerivative(new PagingQuery { Limit = 2 });
            getResult.Should().BeOfType<DataStoreDerivativeQueryResult.Success>();
            ((DataStoreDerivativeQueryResult.Success)getResult)
                .DataStoreDerivativeResponses.Should()
                .HaveCount(2);
        }

        [Test]
        public async Task It_should_apply_offset_when_offset_is_provided()
        {
            var getResult = await _repository.QueryDataStoreDerivative(new PagingQuery { Offset = 1 });
            getResult.Should().BeOfType<DataStoreDerivativeQueryResult.Success>();
            ((DataStoreDerivativeQueryResult.Success)getResult)
                .DataStoreDerivativeResponses.Should()
                .HaveCount(2);
        }
    }

    [TestFixture]
    public class QuerySortTests : DataStoreDerivativeTests
    {
        [SetUp]
        public async Task Setup()
        {
            var instanceResult = await _instanceRepository.InsertDataStore(
                new DataStoreInsertCommand
                {
                    DataStoreType = "Production",
                    Name = "Sort Parent Instance",
                    ConnectionString = "Server=parent;Database=ParentDb;",
                }
            );
            var dataStoreId = ((DataStoreInsertResult.Success)instanceResult).Id;

            foreach (var derivativeType in new[] { "Charlie", "Alpha", "Bravo" })
            {
                var insertResult = await _repository.InsertDataStoreDerivative(
                    new DataStoreDerivativeInsertCommand
                    {
                        DataStoreId = dataStoreId,
                        DerivativeType = derivativeType,
                        ConnectionString = $"Server={derivativeType};Database={derivativeType}Db;",
                    }
                );
                insertResult.Should().BeOfType<DataStoreDerivativeInsertResult.Success>();
            }
        }

        [Test]
        public async Task It_should_return_ascending_order_by_derivative_type()
        {
            var getResult = await _repository.QueryDataStoreDerivative(
                new PagingQuery { OrderBy = "derivativeType", Direction = "ASC" }
            );
            getResult.Should().BeOfType<DataStoreDerivativeQueryResult.Success>();
            var derivativeTypes = ((DataStoreDerivativeQueryResult.Success)getResult)
                .DataStoreDerivativeResponses.Select(d => d.DerivativeType)
                .ToList();
            derivativeTypes.Should().ContainInOrder("Alpha", "Bravo", "Charlie");
        }

        [Test]
        public async Task It_should_return_descending_order_by_derivative_type()
        {
            var getResult = await _repository.QueryDataStoreDerivative(
                new PagingQuery { OrderBy = "derivativeType", Direction = "DESC" }
            );
            getResult.Should().BeOfType<DataStoreDerivativeQueryResult.Success>();
            var derivativeTypes = ((DataStoreDerivativeQueryResult.Success)getResult)
                .DataStoreDerivativeResponses.Select(d => d.DerivativeType)
                .ToList();
            derivativeTypes.Should().ContainInOrder("Charlie", "Bravo", "Alpha");
        }
    }

    [TestFixture]
    public class Given_update_derivative_with_invalid_instance_id : DataStoreDerivativeTests
    {
        private long _dataStoreId;
        private long _derivativeId;

        [SetUp]
        public async Task Setup()
        {
            // Create parent instance and derivative
            var instanceResult = await _instanceRepository.InsertDataStore(
                new DataStoreInsertCommand
                {
                    DataStoreType = "Production",
                    Name = "Parent Instance",
                    ConnectionString = "Server=parent;Database=ParentDb;",
                }
            );
            _dataStoreId = ((DataStoreInsertResult.Success)instanceResult).Id;

            var insertResult = await _repository.InsertDataStoreDerivative(
                new DataStoreDerivativeInsertCommand()
                {
                    DataStoreId = _dataStoreId,
                    DerivativeType = "ReadReplica",
                    ConnectionString = "Server=test;Database=TestDb;",
                }
            );
            _derivativeId = ((DataStoreDerivativeInsertResult.Success)insertResult).Id;
        }

        [Test]
        public async Task It_should_return_failure_foreign_key_violation()
        {
            var updateCommand = new DataStoreDerivativeUpdateCommand()
            {
                Id = _derivativeId,
                DataStoreId = 99999,
                DerivativeType = "Snapshot",
                ConnectionString = "Server=updated;Database=UpdatedDb;",
            };

            var result = await _repository.UpdateDataStoreDerivative(updateCommand);
            result.Should().BeOfType<DataStoreDerivativeUpdateResult.FailureForeignKeyViolation>();
        }
    }

    [TestFixture]
    public class Given_cascade_delete_parent_instance : DataStoreDerivativeTests
    {
        private long _dataStoreId;
        private long _derivative1Id;
        private long _derivative2Id;

        [SetUp]
        public async Task Setup()
        {
            // Create parent instance
            var instanceResult = await _instanceRepository.InsertDataStore(
                new DataStoreInsertCommand
                {
                    DataStoreType = "Production",
                    Name = "Parent Instance to Delete",
                    ConnectionString = "Server=parent;Database=ParentDb;",
                }
            );
            _dataStoreId = ((DataStoreInsertResult.Success)instanceResult).Id;

            // Create two derivatives
            var insertResult1 = await _repository.InsertDataStoreDerivative(
                new DataStoreDerivativeInsertCommand()
                {
                    DataStoreId = _dataStoreId,
                    DerivativeType = "ReadReplica",
                    ConnectionString = "Server=replica;Database=ReplicaDb;",
                }
            );
            _derivative1Id = ((DataStoreDerivativeInsertResult.Success)insertResult1).Id;

            var insertResult2 = await _repository.InsertDataStoreDerivative(
                new DataStoreDerivativeInsertCommand()
                {
                    DataStoreId = _dataStoreId,
                    DerivativeType = "Snapshot",
                    ConnectionString = "Server=snapshot;Database=SnapshotDb;",
                }
            );
            _derivative2Id = ((DataStoreDerivativeInsertResult.Success)insertResult2).Id;

            // Delete the parent instance
            var deleteResult = await _instanceRepository.DeleteDataStore(_dataStoreId);
            deleteResult.Should().BeOfType<DataStoreDeleteResult.Success>();
        }

        [Test]
        public async Task It_should_cascade_delete_all_derivatives()
        {
            // Verify that both derivatives are deleted
            var getResult1 = await _repository.GetDataStoreDerivative(_derivative1Id);
            getResult1.Should().BeOfType<DataStoreDerivativeGetResult.FailureNotFound>();

            var getResult2 = await _repository.GetDataStoreDerivative(_derivative2Id);
            getResult2.Should().BeOfType<DataStoreDerivativeGetResult.FailureNotFound>();

            // Verify that query returns empty
            var queryResult = await _repository.QueryDataStoreDerivative(
                new PagingQuery() { Limit = 25, Offset = 0 }
            );
            queryResult.Should().BeOfType<DataStoreDerivativeQueryResult.Success>();

            var derivatives = (
                (DataStoreDerivativeQueryResult.Success)queryResult
            ).DataStoreDerivativeResponses;
            derivatives.Should().BeEmpty();
        }
    }
}

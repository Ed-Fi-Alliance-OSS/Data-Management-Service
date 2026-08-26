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
using EdFi.DmsConfigurationService.DataModel.Model.Tenant;
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
            new TestAuditContext(),
            new TenantContextProvider()
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

    private async Task<int> InsertParentDataStore(string name)
    {
        var instanceResult = await _instanceRepository.InsertDataStore(
            new DataStoreInsertCommand
            {
                DataStoreType = "Production",
                Name = name,
                ConnectionString = "Server=parent;Database=ParentDb;",
            }
        );

        return instanceResult.Should().BeOfType<DataStoreInsertResult.Success>().Subject.Id;
    }

    private async Task<int> InsertDerivative(int dataStoreId, string derivativeType)
    {
        var insertResult = await _repository.InsertDataStoreDerivative(
            new DataStoreDerivativeInsertCommand
            {
                DataStoreId = dataStoreId,
                DerivativeType = derivativeType,
                ConnectionString = $"Server={derivativeType};Database={derivativeType}Db;",
            }
        );

        return insertResult.Should().BeOfType<DataStoreDerivativeInsertResult.Success>().Subject.Id;
    }

    [TestFixture]
    public class Given_insert_data_store_derivative : DataStoreDerivativeTests
    {
        private int _dataStoreId;
        private int _derivativeId;

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
        private int _dataStoreId;
        private int _derivativeId;

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
        private int _dataStoreId;
        private int _derivativeId;

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
        private int _dataStoreId;
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
        private int _dataStoreId;
        private int _derivative1Id;
        private int _derivative2Id;

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
        /// <summary>
        /// A data store holds at most one derivative of each type, and only the two real type names
        /// are storable, so a four-row paging set spans two data stores.
        /// </summary>
        [SetUp]
        public async Task Setup()
        {
            foreach (var name in new[] { "Paging Parent Instance A", "Paging Parent Instance B" })
            {
                var instanceResult = await _instanceRepository.InsertDataStore(
                    new DataStoreInsertCommand
                    {
                        DataStoreType = "Production",
                        Name = name,
                        ConnectionString = "Server=parent;Database=ParentDb;",
                    }
                );
                var dataStoreId = ((DataStoreInsertResult.Success)instanceResult).Id;

                foreach (var derivativeType in new[] { "ReadReplica", "Snapshot" })
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
        }

        [Test]
        public async Task It_should_return_all_results_when_no_paging_params_provided()
        {
            var getResult = await _repository.QueryDataStoreDerivative(new PagingQuery());
            getResult.Should().BeOfType<DataStoreDerivativeQueryResult.Success>();
            ((DataStoreDerivativeQueryResult.Success)getResult)
                .DataStoreDerivativeResponses.Should()
                .HaveCount(4);
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
                .HaveCount(3);
        }
    }

    [TestFixture]
    public class QuerySortTests : DataStoreDerivativeTests
    {
        /// <summary>
        /// Two data stores, each holding both storable derivative types, so ordering has ties to
        /// resolve. Rows are inserted in reverse of the expected ascending order, so the assertion
        /// proves the ORDER BY clause rather than the insertion order.
        /// </summary>
        [SetUp]
        public async Task Setup()
        {
            foreach (var name in new[] { "Sort Parent Instance A", "Sort Parent Instance B" })
            {
                var instanceResult = await _instanceRepository.InsertDataStore(
                    new DataStoreInsertCommand
                    {
                        DataStoreType = "Production",
                        Name = name,
                        ConnectionString = "Server=parent;Database=ParentDb;",
                    }
                );
                var dataStoreId = ((DataStoreInsertResult.Success)instanceResult).Id;

                foreach (var derivativeType in new[] { "Snapshot", "ReadReplica" })
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
            derivativeTypes.Should().ContainInOrder("ReadReplica", "ReadReplica", "Snapshot", "Snapshot");
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
            derivativeTypes.Should().ContainInOrder("Snapshot", "Snapshot", "ReadReplica", "ReadReplica");
        }
    }

    [TestFixture]
    public class Given_update_derivative_with_invalid_instance_id : DataStoreDerivativeTests
    {
        private int _dataStoreId;
        private int _derivativeId;

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
        private int _dataStoreId;
        private int _derivative1Id;
        private int _derivative2Id;

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

    [TestFixture]
    public class Given_derivative_operations_from_another_tenant : DataStoreDerivativeTests
    {
        private IDataStoreDerivativeRepository _tenantARepository = null!;
        private IDataStoreDerivativeRepository _tenantBRepository = null!;
        private int _tenantADataStoreId;
        private int _tenantADerivativeId;
        private int _tenantBDerivativeId;

        [SetUp]
        public async Task Setup()
        {
            var tenantRepository = new TenantRepository(
                MssqlTestConfiguration.DatabaseOptions,
                NullLogger<TenantRepository>.Instance,
                new TestAuditContext()
            );

            var tenantAProvider = await CreateTenantProvider(tenantRepository, "A");
            var tenantBProvider = await CreateTenantProvider(tenantRepository, "B");

            _tenantARepository = CreateDerivativeRepository(tenantAProvider);
            _tenantBRepository = CreateDerivativeRepository(tenantBProvider);

            (_tenantADataStoreId, _tenantADerivativeId) = await InsertDataStoreWithDerivative(
                tenantAProvider,
                _tenantARepository,
                "Tenant A Data Store"
            );
            (_, _tenantBDerivativeId) = await InsertDataStoreWithDerivative(
                tenantBProvider,
                _tenantBRepository,
                "Tenant B Data Store"
            );
        }

        private static async Task<TenantContextProvider> CreateTenantProvider(
            TenantRepository tenantRepository,
            string suffix
        )
        {
            var tenantName = $"DerivativeTenant{suffix}-{Guid.NewGuid()}";
            var tenantResult = await tenantRepository.InsertTenant(
                new TenantInsertCommand { Name = tenantName }
            );
            tenantResult.Should().BeOfType<TenantInsertResult.Success>();
            return new TenantContextProvider
            {
                Context = new TenantContext.Multitenant(
                    ((TenantInsertResult.Success)tenantResult).Id,
                    tenantName
                ),
            };
        }

        private static DataStoreDerivativeRepository CreateDerivativeRepository(
            TenantContextProvider tenantContextProvider
        ) =>
            new(
                MssqlTestConfiguration.DatabaseOptions,
                NullLogger<DataStoreDerivativeRepository>.Instance,
                new ConnectionStringEncryptionService(MssqlTestConfiguration.DatabaseOptions),
                new TestAuditContext(),
                tenantContextProvider
            );

        private static async Task<(int DataStoreId, int DerivativeId)> InsertDataStoreWithDerivative(
            TenantContextProvider tenantContextProvider,
            IDataStoreDerivativeRepository derivativeRepository,
            string dataStoreName
        )
        {
            var dataStoreRepository = new DataStoreRepository(
                MssqlTestConfiguration.DatabaseOptions,
                NullLogger<DataStoreRepository>.Instance,
                new ConnectionStringEncryptionService(MssqlTestConfiguration.DatabaseOptions),
                new DataStoreContextRepository(
                    MssqlTestConfiguration.DatabaseOptions,
                    NullLogger<DataStoreContextRepository>.Instance,
                    new TestAuditContext(),
                    tenantContextProvider
                ),
                derivativeRepository,
                new TestAuditContext(),
                tenantContextProvider
            );

            var dataStoreResult = await dataStoreRepository.InsertDataStore(
                new DataStoreInsertCommand
                {
                    DataStoreType = "Production",
                    Name = dataStoreName,
                    ConnectionString = "Server=tenant;Database=TenantDb;",
                }
            );
            dataStoreResult.Should().BeOfType<DataStoreInsertResult.Success>();
            var dataStoreId = ((DataStoreInsertResult.Success)dataStoreResult).Id;

            var derivativeResult = await derivativeRepository.InsertDataStoreDerivative(
                new DataStoreDerivativeInsertCommand
                {
                    DataStoreId = dataStoreId,
                    DerivativeType = "ReadReplica",
                    ConnectionString = "Server=tenantReplica;Database=TenantReplicaDb;",
                }
            );
            derivativeResult.Should().BeOfType<DataStoreDerivativeInsertResult.Success>();
            return (dataStoreId, ((DataStoreDerivativeInsertResult.Success)derivativeResult).Id);
        }

        [Test]
        public async Task It_should_not_get_another_tenants_derivative()
        {
            var result = await _tenantBRepository.GetDataStoreDerivative(_tenantADerivativeId);
            result.Should().BeOfType<DataStoreDerivativeGetResult.FailureNotFound>();
        }

        [Test]
        public async Task It_should_not_list_another_tenants_derivative_in_query()
        {
            var result = await _tenantBRepository.QueryDataStoreDerivative(
                new PagingQuery { Limit = 25, Offset = 0 }
            );
            result.Should().BeOfType<DataStoreDerivativeQueryResult.Success>();
            ((DataStoreDerivativeQueryResult.Success)result)
                .DataStoreDerivativeResponses.Should()
                .NotContain(derivative => derivative.Id == _tenantADerivativeId);
        }

        [Test]
        public async Task It_should_not_update_another_tenants_derivative()
        {
            var result = await _tenantBRepository.UpdateDataStoreDerivative(
                new DataStoreDerivativeUpdateCommand
                {
                    Id = _tenantADerivativeId,
                    DataStoreId = _tenantADataStoreId,
                    DerivativeType = "Snapshot",
                    ConnectionString = "Server=other;Database=OtherDb;",
                }
            );
            result.Should().BeOfType<DataStoreDerivativeUpdateResult.FailureNotFound>();
        }

        [Test]
        public async Task It_should_not_move_a_derivative_to_another_tenants_data_store()
        {
            var result = await _tenantBRepository.UpdateDataStoreDerivative(
                new DataStoreDerivativeUpdateCommand
                {
                    Id = _tenantBDerivativeId,
                    DataStoreId = _tenantADataStoreId,
                    DerivativeType = "Snapshot",
                    ConnectionString = "Server=other;Database=OtherDb;",
                }
            );
            result.Should().BeOfType<DataStoreDerivativeUpdateResult.FailureForeignKeyViolation>();
        }

        [Test]
        public async Task It_should_not_delete_another_tenants_derivative()
        {
            var result = await _tenantBRepository.DeleteDataStoreDerivative(_tenantADerivativeId);
            result.Should().BeOfType<DataStoreDerivativeDeleteResult.FailureNotFound>();

            var stillThere = await _tenantARepository.GetDataStoreDerivative(_tenantADerivativeId);
            stillThere.Should().BeOfType<DataStoreDerivativeGetResult.Success>();
        }

        [Test]
        public async Task It_should_not_list_another_tenants_derivatives_by_data_store()
        {
            var result = await _tenantBRepository.GetDataStoreDerivativesByDataStore(_tenantADataStoreId);
            result.Should().BeOfType<DataStoreDerivativeQueryByDataStoreResult.Success>();
            ((DataStoreDerivativeQueryByDataStoreResult.Success)result)
                .DataStoreDerivativeResponses.Should()
                .BeEmpty();
        }

        [Test]
        public async Task It_should_not_list_another_tenants_derivatives_by_data_store_ids()
        {
            var result = await _tenantBRepository.GetDataStoreDerivativesByDataStoreIds([
                _tenantADataStoreId,
            ]);
            result.Should().BeOfType<DataStoreDerivativeQueryByDataStoreIdsResult.Success>();
            ((DataStoreDerivativeQueryByDataStoreIdsResult.Success)result)
                .DataStoreDerivativeResponses.Should()
                .BeEmpty();
        }

        [Test]
        public async Task It_should_not_insert_a_derivative_under_another_tenants_data_store()
        {
            var result = await _tenantBRepository.InsertDataStoreDerivative(
                new DataStoreDerivativeInsertCommand
                {
                    DataStoreId = _tenantADataStoreId,
                    DerivativeType = "Snapshot",
                    ConnectionString = "Server=other;Database=OtherDb;",
                }
            );
            result.Should().BeOfType<DataStoreDerivativeInsertResult.FailureForeignKeyViolation>();
        }

        [Test]
        public async Task It_should_not_expose_tenant_scoped_derivatives_in_single_tenant_context()
        {
            var singleTenantRepository = CreateDerivativeRepository(new TenantContextProvider());
            var result = await singleTenantRepository.GetDataStoreDerivative(_tenantADerivativeId);
            result.Should().BeOfType<DataStoreDerivativeGetResult.FailureNotFound>();
        }
    }

    [TestFixture]
    public class Given_a_data_store_with_both_derivative_types : DataStoreDerivativeTests
    {
        private int _dataStoreId;

        [SetUp]
        public async Task Setup()
        {
            _dataStoreId = await InsertParentDataStore("Both Types Parent Instance");

            await InsertDerivative(_dataStoreId, "Snapshot");
            await InsertDerivative(_dataStoreId, "ReadReplica");
        }

        [Test]
        public async Task It_should_store_one_derivative_of_each_type()
        {
            var getResult = await _repository.GetDataStoreDerivativesByDataStore(_dataStoreId);

            string[] expectedDerivativeTypes = ["ReadReplica", "Snapshot"];

            var derivatives = getResult
                .Should()
                .BeOfType<DataStoreDerivativeQueryByDataStoreResult.Success>()
                .Subject.DataStoreDerivativeResponses;
            derivatives
                .Select(derivative => derivative.DerivativeType)
                .Should()
                .BeEquivalentTo(expectedDerivativeTypes);
        }
    }

    [TestFixture]
    public class Given_insert_of_a_duplicate_derivative_type : DataStoreDerivativeTests
    {
        private int _dataStoreId;
        private DataStoreDerivativeInsertResult _duplicateResult = null!;

        [SetUp]
        public async Task Setup()
        {
            _dataStoreId = await InsertParentDataStore("Duplicate Insert Parent Instance");
            await InsertDerivative(_dataStoreId, "Snapshot");

            _duplicateResult = await _repository.InsertDataStoreDerivative(
                new DataStoreDerivativeInsertCommand
                {
                    DataStoreId = _dataStoreId,
                    DerivativeType = "Snapshot",
                    ConnectionString = "Server=second;Database=SecondDb;",
                }
            );
        }

        [Test]
        public void It_should_return_failure_duplicate_data_store_derivative() =>
            _duplicateResult
                .Should()
                .BeOfType<DataStoreDerivativeInsertResult.FailureDuplicateDataStoreDerivative>();

        [Test]
        public void It_should_carry_the_conflicting_data_store_and_derivative_type()
        {
            var duplicate = _duplicateResult
                .Should()
                .BeOfType<DataStoreDerivativeInsertResult.FailureDuplicateDataStoreDerivative>()
                .Subject;

            duplicate.DataStoreId.Should().Be(_dataStoreId);
            duplicate.DerivativeType.Should().Be("Snapshot");
        }

        [Test]
        public async Task It_should_leave_only_the_original_derivative()
        {
            var getResult = await _repository.GetDataStoreDerivativesByDataStore(_dataStoreId);

            getResult
                .Should()
                .BeOfType<DataStoreDerivativeQueryByDataStoreResult.Success>()
                .Subject.DataStoreDerivativeResponses.Should()
                .HaveCount(1);
        }
    }

    [TestFixture]
    public class Given_update_changing_derivative_type_to_an_existing_sibling : DataStoreDerivativeTests
    {
        private int _dataStoreId;
        private int _replicaId;
        private DataStoreDerivativeUpdateResult _updateResult = null!;

        [SetUp]
        public async Task Setup()
        {
            _dataStoreId = await InsertParentDataStore("Update Type Conflict Parent Instance");
            await InsertDerivative(_dataStoreId, "Snapshot");
            _replicaId = await InsertDerivative(_dataStoreId, "ReadReplica");

            _updateResult = await _repository.UpdateDataStoreDerivative(
                new DataStoreDerivativeUpdateCommand
                {
                    Id = _replicaId,
                    DataStoreId = _dataStoreId,
                    DerivativeType = "Snapshot",
                    ConnectionString = "Server=conflict;Database=ConflictDb;",
                }
            );
        }

        [Test]
        public void It_should_return_failure_duplicate_data_store_derivative() =>
            _updateResult
                .Should()
                .BeOfType<DataStoreDerivativeUpdateResult.FailureDuplicateDataStoreDerivative>();

        [Test]
        public void It_should_carry_the_conflicting_data_store_and_derivative_type()
        {
            var duplicate = _updateResult
                .Should()
                .BeOfType<DataStoreDerivativeUpdateResult.FailureDuplicateDataStoreDerivative>()
                .Subject;

            duplicate.DataStoreId.Should().Be(_dataStoreId);
            duplicate.DerivativeType.Should().Be("Snapshot");
        }

        [Test]
        public async Task It_should_leave_the_derivative_type_unchanged()
        {
            var getResult = await _repository.GetDataStoreDerivative(_replicaId);

            getResult
                .Should()
                .BeOfType<DataStoreDerivativeGetResult.Success>()
                .Subject.DataStoreDerivativeResponse.DerivativeType.Should()
                .Be("ReadReplica");
        }
    }

    [TestFixture]
    public class Given_update_moving_a_derivative_to_a_data_store_that_already_has_the_type
        : DataStoreDerivativeTests
    {
        private int _targetDataStoreId;
        private int _sourceDataStoreId;
        private int _movedDerivativeId;
        private DataStoreDerivativeUpdateResult _updateResult = null!;

        [SetUp]
        public async Task Setup()
        {
            _targetDataStoreId = await InsertParentDataStore("Move Conflict Target Instance");
            _sourceDataStoreId = await InsertParentDataStore("Move Conflict Source Instance");

            await InsertDerivative(_targetDataStoreId, "Snapshot");
            _movedDerivativeId = await InsertDerivative(_sourceDataStoreId, "Snapshot");

            _updateResult = await _repository.UpdateDataStoreDerivative(
                new DataStoreDerivativeUpdateCommand
                {
                    Id = _movedDerivativeId,
                    DataStoreId = _targetDataStoreId,
                    DerivativeType = "Snapshot",
                    ConnectionString = "Server=moved;Database=MovedDb;",
                }
            );
        }

        [Test]
        public void It_should_return_failure_duplicate_data_store_derivative() =>
            _updateResult
                .Should()
                .BeOfType<DataStoreDerivativeUpdateResult.FailureDuplicateDataStoreDerivative>();

        [Test]
        public void It_should_carry_the_target_data_store_and_derivative_type()
        {
            var duplicate = _updateResult
                .Should()
                .BeOfType<DataStoreDerivativeUpdateResult.FailureDuplicateDataStoreDerivative>()
                .Subject;

            duplicate.DataStoreId.Should().Be(_targetDataStoreId);
            duplicate.DerivativeType.Should().Be("Snapshot");
        }

        [Test]
        public async Task It_should_leave_the_derivative_on_its_original_data_store()
        {
            var getResult = await _repository.GetDataStoreDerivative(_movedDerivativeId);

            getResult
                .Should()
                .BeOfType<DataStoreDerivativeGetResult.Success>()
                .Subject.DataStoreDerivativeResponse.DataStoreId.Should()
                .Be(_sourceDataStoreId);
        }
    }
}

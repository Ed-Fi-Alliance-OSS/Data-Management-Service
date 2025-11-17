// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.Postgresql.Repositories;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.Backend.Services;
using EdFi.DmsConfigurationService.DataModel.Model;
using EdFi.DmsConfigurationService.DataModel.Model.DmsInstance;
using EdFi.DmsConfigurationService.DataModel.Model.DmsInstanceDerivative;
using EdFi.DmsConfigurationService.DataModel.Model.DmsInstanceRouteContext;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration;

public class DmsInstanceDerivativeTests : DatabaseTest
{
    private readonly IDmsInstanceRepository _instanceRepository;
    private readonly IDmsInstanceDerivativeRepository _repository;

    public DmsInstanceDerivativeTests()
    {
        var routeContextRepository = new DmsInstanceRouteContextRepository(
            Configuration.DatabaseOptions,
            NullLogger<DmsInstanceRouteContextRepository>.Instance
        );

        _repository = new DmsInstanceDerivativeRepository(
            Configuration.DatabaseOptions,
            NullLogger<DmsInstanceDerivativeRepository>.Instance,
            new ConnectionStringEncryptionService(Configuration.DatabaseOptions)
        );

        _instanceRepository = new DmsInstanceRepository(
            Configuration.DatabaseOptions,
            NullLogger<DmsInstanceRepository>.Instance,
            new ConnectionStringEncryptionService(Configuration.DatabaseOptions),
            routeContextRepository,
            _repository
        );
    }

    [TestFixture]
    public class Given_insert_dms_instance_derivative : DmsInstanceDerivativeTests
    {
        private long _instanceId;
        private long _derivativeId;

        [SetUp]
        public async Task Setup()
        {
            // Create parent instance first
            var instanceResult = await _instanceRepository.InsertDmsInstance(
                new DmsInstanceInsertCommand
                {
                    InstanceType = "Production",
                    InstanceName = "Parent Instance",
                    ConnectionString = "Server=parent;Database=ParentDb;",
                }
            );
            _instanceId = ((DmsInstanceInsertResult.Success)instanceResult).Id;

            // Create derivative
            DmsInstanceDerivativeInsertCommand derivative = new()
            {
                InstanceId = _instanceId,
                DerivativeType = "ReadReplica",
                ConnectionString = "Server=replica;Database=ReplicaDb;User Id=user;Password=pass;",
            };

            var result = await _repository.InsertDmsInstanceDerivative(derivative);
            result.Should().BeOfType<DmsInstanceDerivativeInsertResult.Success>();
            _derivativeId = (result as DmsInstanceDerivativeInsertResult.Success)!.Id;
            _derivativeId.Should().BeGreaterThan(0);
        }

        [Test]
        public async Task It_should_retrieve_derivative_from_query()
        {
            var getResult = await _repository.QueryDmsInstanceDerivative(
                new PagingQuery() { Limit = 25, Offset = 0 }
            );
            getResult.Should().BeOfType<DmsInstanceDerivativeQueryResult.Success>();

            var derivativeFromDb = (
                (DmsInstanceDerivativeQueryResult.Success)getResult
            ).DmsInstanceDerivativeResponses.First();
            derivativeFromDb.InstanceId.Should().Be(_instanceId);
            derivativeFromDb.DerivativeType.Should().Be("ReadReplica");
            derivativeFromDb
                .ConnectionString.Should()
                .Be("Server=replica;Database=ReplicaDb;User Id=user;Password=pass;");
        }

        [Test]
        public async Task It_should_retrieve_derivative_from_get_by_id()
        {
            var getByIdResult = await _repository.GetDmsInstanceDerivative(_derivativeId);
            getByIdResult.Should().BeOfType<DmsInstanceDerivativeGetResult.Success>();

            var derivativeFromDb = (
                (DmsInstanceDerivativeGetResult.Success)getByIdResult
            ).DmsInstanceDerivativeResponse;
            derivativeFromDb.InstanceId.Should().Be(_instanceId);
            derivativeFromDb.DerivativeType.Should().Be("ReadReplica");
            derivativeFromDb
                .ConnectionString.Should()
                .Be("Server=replica;Database=ReplicaDb;User Id=user;Password=pass;");
        }
    }

    [TestFixture]
    public class Given_insert_dms_instance_derivative_with_snapshot_type : DmsInstanceDerivativeTests
    {
        private long _instanceId;
        private long _derivativeId;

        [SetUp]
        public async Task Setup()
        {
            // Create parent instance first
            var instanceResult = await _instanceRepository.InsertDmsInstance(
                new DmsInstanceInsertCommand
                {
                    InstanceType = "Production",
                    InstanceName = "Parent Instance",
                    ConnectionString = "Server=parent;Database=ParentDb;",
                }
            );
            _instanceId = ((DmsInstanceInsertResult.Success)instanceResult).Id;

            // Create derivative
            DmsInstanceDerivativeInsertCommand derivative = new()
            {
                InstanceId = _instanceId,
                DerivativeType = "Snapshot",
                ConnectionString = "Server=snapshot;Database=SnapshotDb;",
            };

            var result = await _repository.InsertDmsInstanceDerivative(derivative);
            result.Should().BeOfType<DmsInstanceDerivativeInsertResult.Success>();
            _derivativeId = (result as DmsInstanceDerivativeInsertResult.Success)!.Id;
        }

        [Test]
        public async Task It_should_retrieve_derivative_with_snapshot_type()
        {
            var getByIdResult = await _repository.GetDmsInstanceDerivative(_derivativeId);
            getByIdResult.Should().BeOfType<DmsInstanceDerivativeGetResult.Success>();

            var derivativeFromDb = (
                (DmsInstanceDerivativeGetResult.Success)getByIdResult
            ).DmsInstanceDerivativeResponse;
            derivativeFromDb.DerivativeType.Should().Be("Snapshot");
        }
    }

    [TestFixture]
    public class Given_insert_dms_instance_derivative_without_connection_string : DmsInstanceDerivativeTests
    {
        private long _instanceId;
        private long _derivativeId;

        [SetUp]
        public async Task Setup()
        {
            // Create parent instance first
            var instanceResult = await _instanceRepository.InsertDmsInstance(
                new DmsInstanceInsertCommand
                {
                    InstanceType = "Development",
                    InstanceName = "Parent Instance",
                    ConnectionString = "Server=parent;Database=ParentDb;",
                }
            );
            _instanceId = ((DmsInstanceInsertResult.Success)instanceResult).Id;

            // Create derivative without connection string
            DmsInstanceDerivativeInsertCommand derivative = new()
            {
                InstanceId = _instanceId,
                DerivativeType = "ReadReplica",
                ConnectionString = null,
            };

            var result = await _repository.InsertDmsInstanceDerivative(derivative);
            result.Should().BeOfType<DmsInstanceDerivativeInsertResult.Success>();
            _derivativeId = (result as DmsInstanceDerivativeInsertResult.Success)!.Id;
        }

        [Test]
        public async Task It_should_retrieve_derivative_with_null_connection_string()
        {
            var getByIdResult = await _repository.GetDmsInstanceDerivative(_derivativeId);
            getByIdResult.Should().BeOfType<DmsInstanceDerivativeGetResult.Success>();

            var derivativeFromDb = (
                (DmsInstanceDerivativeGetResult.Success)getByIdResult
            ).DmsInstanceDerivativeResponse;
            derivativeFromDb.ConnectionString.Should().BeNull();
        }
    }

    [TestFixture]
    public class Given_update_dms_instance_derivative : DmsInstanceDerivativeTests
    {
        private long _instanceId;
        private DmsInstanceDerivativeInsertCommand _derivativeInsert = null!;
        private DmsInstanceDerivativeUpdateCommand _derivativeUpdate = null!;

        [SetUp]
        public async Task Setup()
        {
            // Create parent instance first
            var instanceResult = await _instanceRepository.InsertDmsInstance(
                new DmsInstanceInsertCommand
                {
                    InstanceType = "Staging",
                    InstanceName = "Parent Instance",
                    ConnectionString = "Server=parent;Database=ParentDb;",
                }
            );
            _instanceId = ((DmsInstanceInsertResult.Success)instanceResult).Id;

            _derivativeInsert = new()
            {
                InstanceId = _instanceId,
                DerivativeType = "ReadReplica",
                ConnectionString = "Server=original;Database=OriginalDb;",
            };

            _derivativeUpdate = new()
            {
                InstanceId = _instanceId,
                DerivativeType = "Snapshot",
                ConnectionString = "Server=updated;Database=UpdatedDb;",
            };

            var insertResult = await _repository.InsertDmsInstanceDerivative(_derivativeInsert);
            insertResult.Should().BeOfType<DmsInstanceDerivativeInsertResult.Success>();

            _derivativeUpdate.Id = (insertResult as DmsInstanceDerivativeInsertResult.Success)!.Id;

            var updateResult = await _repository.UpdateDmsInstanceDerivative(_derivativeUpdate);
            updateResult.Should().BeOfType<DmsInstanceDerivativeUpdateResult.Success>();
        }

        [Test]
        public async Task It_should_retrieve_updated_derivative_from_query()
        {
            var getResult = await _repository.QueryDmsInstanceDerivative(
                new PagingQuery() { Limit = 25, Offset = 0 }
            );
            getResult.Should().BeOfType<DmsInstanceDerivativeQueryResult.Success>();

            var derivativeFromDb = (
                (DmsInstanceDerivativeQueryResult.Success)getResult
            ).DmsInstanceDerivativeResponses.First();
            derivativeFromDb.DerivativeType.Should().Be("Snapshot");
            derivativeFromDb.ConnectionString.Should().Be("Server=updated;Database=UpdatedDb;");
        }

        [Test]
        public async Task It_should_retrieve_updated_derivative_from_get_by_id()
        {
            var getByIdResult = await _repository.GetDmsInstanceDerivative(_derivativeUpdate.Id);
            getByIdResult.Should().BeOfType<DmsInstanceDerivativeGetResult.Success>();

            var derivativeFromDb = (
                (DmsInstanceDerivativeGetResult.Success)getByIdResult
            ).DmsInstanceDerivativeResponse;
            derivativeFromDb.DerivativeType.Should().Be("Snapshot");
            derivativeFromDb.ConnectionString.Should().Be("Server=updated;Database=UpdatedDb;");
        }
    }

    [TestFixture]
    public class Given_delete_dms_instance_derivative : DmsInstanceDerivativeTests
    {
        private long _instanceId;
        private long _derivative1Id;
        private long _derivative2Id;

        [SetUp]
        public async Task Setup()
        {
            // Create parent instance
            var instanceResult = await _instanceRepository.InsertDmsInstance(
                new DmsInstanceInsertCommand
                {
                    InstanceType = "Production",
                    InstanceName = "Parent Instance",
                    ConnectionString = "Server=parent;Database=ParentDb;",
                }
            );
            _instanceId = ((DmsInstanceInsertResult.Success)instanceResult).Id;

            var insertResult1 = await _repository.InsertDmsInstanceDerivative(
                new DmsInstanceDerivativeInsertCommand()
                {
                    InstanceId = _instanceId,
                    DerivativeType = "ReadReplica",
                    ConnectionString = "Server=delete;Database=DeleteDb;",
                }
            );

            _derivative1Id = ((DmsInstanceDerivativeInsertResult.Success)insertResult1).Id;

            var insertResult2 = await _repository.InsertDmsInstanceDerivative(
                new DmsInstanceDerivativeInsertCommand()
                {
                    InstanceId = _instanceId,
                    DerivativeType = "Snapshot",
                    ConnectionString = "Server=keep;Database=KeepDb;",
                }
            );

            _derivative2Id = ((DmsInstanceDerivativeInsertResult.Success)insertResult2).Id;

            var deleteResult = await _repository.DeleteDmsInstanceDerivative(_derivative1Id);
            deleteResult.Should().BeOfType<DmsInstanceDerivativeDeleteResult.Success>();
        }

        [Test]
        public async Task It_should_not_retrieve_deleted_derivative_from_query()
        {
            var getResult = await _repository.QueryDmsInstanceDerivative(
                new PagingQuery() { Limit = 25, Offset = 0 }
            );
            getResult.Should().BeOfType<DmsInstanceDerivativeQueryResult.Success>();

            var derivatives = (
                (DmsInstanceDerivativeQueryResult.Success)getResult
            ).DmsInstanceDerivativeResponses;
            derivatives.Count().Should().Be(1);
            derivatives.Count(d => d.Id == _derivative1Id).Should().Be(0);
            derivatives.Count(d => d.DerivativeType == "ReadReplica").Should().Be(0);
            derivatives.Count(d => d.Id == _derivative2Id).Should().Be(1);
            derivatives.Count(d => d.DerivativeType == "Snapshot").Should().Be(1);
        }

        [Test]
        public async Task It_should_return_not_found_for_deleted_derivative_get_by_id()
        {
            var getByIdResult = await _repository.GetDmsInstanceDerivative(_derivative1Id);
            getByIdResult.Should().BeOfType<DmsInstanceDerivativeGetResult.FailureNotFound>();

            getByIdResult = await _repository.GetDmsInstanceDerivative(_derivative2Id);
            getByIdResult.Should().BeOfType<DmsInstanceDerivativeGetResult.Success>();
        }
    }

    [TestFixture]
    public class Given_update_non_existent_dms_instance_derivative : DmsInstanceDerivativeTests
    {
        [Test]
        public async Task It_should_return_failure_not_found()
        {
            var updateCommand = new DmsInstanceDerivativeUpdateCommand()
            {
                Id = 9999,
                InstanceId = 1,
                DerivativeType = "ReadReplica",
                ConnectionString = "Server=fake;Database=FakeDb;",
            };

            var result = await _repository.UpdateDmsInstanceDerivative(updateCommand);
            result.Should().BeOfType<DmsInstanceDerivativeUpdateResult.FailureNotFound>();
        }
    }

    [TestFixture]
    public class Given_delete_non_existent_dms_instance_derivative : DmsInstanceDerivativeTests
    {
        [Test]
        public async Task It_should_return_failure_not_found()
        {
            var result = await _repository.DeleteDmsInstanceDerivative(9999);
            result.Should().BeOfType<DmsInstanceDerivativeDeleteResult.FailureNotFound>();
        }
    }

    [TestFixture]
    public class Given_get_non_existent_dms_instance_derivative : DmsInstanceDerivativeTests
    {
        [Test]
        public async Task It_should_return_failure_not_found()
        {
            var result = await _repository.GetDmsInstanceDerivative(9999);
            result.Should().BeOfType<DmsInstanceDerivativeGetResult.FailureNotFound>();
        }
    }

    [TestFixture]
    public class Given_insert_derivative_with_invalid_instance_id : DmsInstanceDerivativeTests
    {
        [Test]
        public async Task It_should_return_failure_foreign_key_violation()
        {
            DmsInstanceDerivativeInsertCommand derivative = new()
            {
                InstanceId = 99999,
                DerivativeType = "ReadReplica",
                ConnectionString = "Server=test;Database=TestDb;",
            };

            var result = await _repository.InsertDmsInstanceDerivative(derivative);
            result.Should().BeOfType<DmsInstanceDerivativeInsertResult.FailureForeignKeyViolation>();
        }
    }

    [TestFixture]
    public class Given_update_derivative_with_invalid_instance_id : DmsInstanceDerivativeTests
    {
        private long _instanceId;
        private long _derivativeId;

        [SetUp]
        public async Task Setup()
        {
            // Create parent instance and derivative
            var instanceResult = await _instanceRepository.InsertDmsInstance(
                new DmsInstanceInsertCommand
                {
                    InstanceType = "Production",
                    InstanceName = "Parent Instance",
                    ConnectionString = "Server=parent;Database=ParentDb;",
                }
            );
            _instanceId = ((DmsInstanceInsertResult.Success)instanceResult).Id;

            var insertResult = await _repository.InsertDmsInstanceDerivative(
                new DmsInstanceDerivativeInsertCommand()
                {
                    InstanceId = _instanceId,
                    DerivativeType = "ReadReplica",
                    ConnectionString = "Server=test;Database=TestDb;",
                }
            );
            _derivativeId = ((DmsInstanceDerivativeInsertResult.Success)insertResult).Id;
        }

        [Test]
        public async Task It_should_return_failure_foreign_key_violation()
        {
            var updateCommand = new DmsInstanceDerivativeUpdateCommand()
            {
                Id = _derivativeId,
                InstanceId = 99999,
                DerivativeType = "Snapshot",
                ConnectionString = "Server=updated;Database=UpdatedDb;",
            };

            var result = await _repository.UpdateDmsInstanceDerivative(updateCommand);
            result.Should().BeOfType<DmsInstanceDerivativeUpdateResult.FailureForeignKeyViolation>();
        }
    }

    [TestFixture]
    public class Given_cascade_delete_parent_instance : DmsInstanceDerivativeTests
    {
        private long _instanceId;
        private long _derivative1Id;
        private long _derivative2Id;

        [SetUp]
        public async Task Setup()
        {
            // Create parent instance
            var instanceResult = await _instanceRepository.InsertDmsInstance(
                new DmsInstanceInsertCommand
                {
                    InstanceType = "Production",
                    InstanceName = "Parent Instance to Delete",
                    ConnectionString = "Server=parent;Database=ParentDb;",
                }
            );
            _instanceId = ((DmsInstanceInsertResult.Success)instanceResult).Id;

            // Create two derivatives
            var insertResult1 = await _repository.InsertDmsInstanceDerivative(
                new DmsInstanceDerivativeInsertCommand()
                {
                    InstanceId = _instanceId,
                    DerivativeType = "ReadReplica",
                    ConnectionString = "Server=replica;Database=ReplicaDb;",
                }
            );
            _derivative1Id = ((DmsInstanceDerivativeInsertResult.Success)insertResult1).Id;

            var insertResult2 = await _repository.InsertDmsInstanceDerivative(
                new DmsInstanceDerivativeInsertCommand()
                {
                    InstanceId = _instanceId,
                    DerivativeType = "Snapshot",
                    ConnectionString = "Server=snapshot;Database=SnapshotDb;",
                }
            );
            _derivative2Id = ((DmsInstanceDerivativeInsertResult.Success)insertResult2).Id;

            // Delete the parent instance
            var deleteResult = await _instanceRepository.DeleteDmsInstance(_instanceId);
            deleteResult.Should().BeOfType<DmsInstanceDeleteResult.Success>();
        }

        [Test]
        public async Task It_should_cascade_delete_all_derivatives()
        {
            // Verify that both derivatives are deleted
            var getResult1 = await _repository.GetDmsInstanceDerivative(_derivative1Id);
            getResult1.Should().BeOfType<DmsInstanceDerivativeGetResult.FailureNotFound>();

            var getResult2 = await _repository.GetDmsInstanceDerivative(_derivative2Id);
            getResult2.Should().BeOfType<DmsInstanceDerivativeGetResult.FailureNotFound>();

            // Verify that query returns empty
            var queryResult = await _repository.QueryDmsInstanceDerivative(
                new PagingQuery() { Limit = 25, Offset = 0 }
            );
            queryResult.Should().BeOfType<DmsInstanceDerivativeQueryResult.Success>();

            var derivatives = (
                (DmsInstanceDerivativeQueryResult.Success)queryResult
            ).DmsInstanceDerivativeResponses;
            derivatives.Should().BeEmpty();
        }
    }
}

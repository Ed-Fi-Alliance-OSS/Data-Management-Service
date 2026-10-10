// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Configuration;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Configuration;

public class DmsConnectionStringProviderTests
{
    [TestFixture]
    public class Given_Valid_DataStore_Id
    {
        private IDataStoreProvider? _dataStoreProvider;
        private DmsConnectionStringProvider? _connectionStringProvider;
        private string? _connectionString;

        [SetUp]
        public void Setup()
        {
            _dataStoreProvider = A.Fake<IDataStoreProvider>();
            var instance = new DataStore(
                1,
                "Production",
                "Main Instance",
                "host=localhost;port=5432;username=postgres;database=edfi;",
                []
            );

            A.CallTo(() => _dataStoreProvider.GetById(1, A<string?>.Ignored)).Returns(instance);

            _connectionStringProvider = new DmsConnectionStringProvider(
                _dataStoreProvider,
                NullLogger<DmsConnectionStringProvider>.Instance
            );
            _connectionString = _connectionStringProvider.GetConnectionString(1);
        }

        [Test]
        public void It_should_return_connection_string()
        {
            _connectionString.Should().NotBeNullOrEmpty();
            _connectionString.Should().Be("host=localhost;port=5432;username=postgres;database=edfi;");
        }
    }

    [TestFixture]
    public class Given_NonExistent_DataStore_Id
    {
        private IDataStoreProvider? _dataStoreProvider;
        private DmsConnectionStringProvider? _connectionStringProvider;

        [SetUp]
        public void Setup()
        {
            _dataStoreProvider = A.Fake<IDataStoreProvider>();
            A.CallTo(() => _dataStoreProvider.GetById(999, A<string?>.Ignored)).Returns(null);
            A.CallTo(() => _dataStoreProvider.GetAll(A<string?>.Ignored)).Returns(new List<DataStore>());

            _connectionStringProvider = new DmsConnectionStringProvider(
                _dataStoreProvider,
                NullLogger<DmsConnectionStringProvider>.Instance
            );
        }

        [Test]
        public void It_should_return_null()
        {
            var result = _connectionStringProvider!.GetConnectionString(999);

            result.Should().BeNull();
        }
    }

    [TestFixture]
    public class Given_DataStore_With_Null_ConnectionString
    {
        private IDataStoreProvider? _dataStoreProvider;
        private DmsConnectionStringProvider? _connectionStringProvider;

        [SetUp]
        public void Setup()
        {
            _dataStoreProvider = A.Fake<IDataStoreProvider>();
            var instance = new DataStore(1, "Production", "Main Instance", null, []);

            A.CallTo(() => _dataStoreProvider.GetById(1, A<string?>.Ignored)).Returns(instance);

            _connectionStringProvider = new DmsConnectionStringProvider(
                _dataStoreProvider,
                NullLogger<DmsConnectionStringProvider>.Instance
            );
        }

        [Test]
        public void It_should_return_null()
        {
            var result = _connectionStringProvider!.GetConnectionString(1);

            result.Should().BeNull();
        }
    }

    [TestFixture]
    public class Given_DataStore_With_Empty_ConnectionString
    {
        private IDataStoreProvider? _dataStoreProvider;
        private DmsConnectionStringProvider? _connectionStringProvider;

        [SetUp]
        public void Setup()
        {
            _dataStoreProvider = A.Fake<IDataStoreProvider>();
            var instance = new DataStore(1, "Production", "Main Instance", "   ", []);

            A.CallTo(() => _dataStoreProvider.GetById(1, A<string?>.Ignored)).Returns(instance);

            _connectionStringProvider = new DmsConnectionStringProvider(
                _dataStoreProvider,
                NullLogger<DmsConnectionStringProvider>.Instance
            );
        }

        [Test]
        public void It_should_return_null()
        {
            var result = _connectionStringProvider!.GetConnectionString(1);

            result.Should().BeNull();
        }
    }

    [TestFixture]
    public class Given_Multiple_DataStores
    {
        private IDataStoreProvider? _dataStoreProvider;
        private DmsConnectionStringProvider? _connectionStringProvider;
        private string? _defaultConnectionString;

        [SetUp]
        public void Setup()
        {
            _dataStoreProvider = A.Fake<IDataStoreProvider>();

            var instances = new List<DataStore>
            {
                new(3, "Production", "Third Instance", "host=third;database=db3;", []),
                new(1, "Production", "First Instance", "host=first;database=db1;", []),
                new(2, "Development", "Second Instance", "host=second;database=db2;", []),
            };

            A.CallTo(() => _dataStoreProvider.GetLoadedTenantKeys())
                .Returns(new List<string> { "" }.AsReadOnly());
            A.CallTo(() => _dataStoreProvider.GetAll(A<string?>.Ignored)).Returns(instances);

            _connectionStringProvider = new DmsConnectionStringProvider(
                _dataStoreProvider,
                NullLogger<DmsConnectionStringProvider>.Instance
            );
            _defaultConnectionString = _connectionStringProvider.GetHealthCheckConnectionString();
        }

        [Test]
        public void It_should_return_connection_string_from_lowest_id_instance()
        {
            _defaultConnectionString.Should().NotBeNullOrEmpty();
            _defaultConnectionString.Should().Be("host=first;database=db1;");
        }
    }

    [TestFixture]
    public class Given_Single_DataStore
    {
        private IDataStoreProvider? _dataStoreProvider;
        private DmsConnectionStringProvider? _connectionStringProvider;
        private string? _defaultConnectionString;

        [SetUp]
        public void Setup()
        {
            _dataStoreProvider = A.Fake<IDataStoreProvider>();

            var instances = new List<DataStore>
            {
                new(5, "Production", "Only Instance", "host=only;database=dbonly;", []),
            };

            A.CallTo(() => _dataStoreProvider.GetLoadedTenantKeys())
                .Returns(new List<string> { "" }.AsReadOnly());
            A.CallTo(() => _dataStoreProvider.GetAll(A<string?>.Ignored)).Returns(instances);

            _connectionStringProvider = new DmsConnectionStringProvider(
                _dataStoreProvider,
                NullLogger<DmsConnectionStringProvider>.Instance
            );
            _defaultConnectionString = _connectionStringProvider.GetHealthCheckConnectionString();
        }

        [Test]
        public void It_should_return_connection_string_from_single_instance()
        {
            _defaultConnectionString.Should().NotBeNullOrEmpty();
            _defaultConnectionString.Should().Be("host=only;database=dbonly;");
        }
    }

    [TestFixture]
    public class Given_No_DataStores_Configured
    {
        private IDataStoreProvider? _dataStoreProvider;
        private DmsConnectionStringProvider? _connectionStringProvider;

        [SetUp]
        public void Setup()
        {
            _dataStoreProvider = A.Fake<IDataStoreProvider>();
            A.CallTo(() => _dataStoreProvider.GetLoadedTenantKeys())
                .Returns(new List<string> { "" }.AsReadOnly());
            A.CallTo(() => _dataStoreProvider.GetAll(A<string?>.Ignored)).Returns(new List<DataStore>());

            _connectionStringProvider = new DmsConnectionStringProvider(
                _dataStoreProvider,
                NullLogger<DmsConnectionStringProvider>.Instance
            );
        }

        [Test]
        public void It_should_return_null()
        {
            var result = _connectionStringProvider!.GetHealthCheckConnectionString();

            result.Should().BeNull();
        }
    }

    [TestFixture]
    public class Given_Default_DataStore_With_Null_ConnectionString
    {
        private IDataStoreProvider? _dataStoreProvider;
        private DmsConnectionStringProvider? _connectionStringProvider;

        [SetUp]
        public void Setup()
        {
            _dataStoreProvider = A.Fake<IDataStoreProvider>();

            var instances = new List<DataStore> { new(1, "Production", "Invalid Instance", null, []) };

            A.CallTo(() => _dataStoreProvider.GetLoadedTenantKeys())
                .Returns(new List<string> { "" }.AsReadOnly());
            A.CallTo(() => _dataStoreProvider.GetAll(A<string?>.Ignored)).Returns(instances);

            _connectionStringProvider = new DmsConnectionStringProvider(
                _dataStoreProvider,
                NullLogger<DmsConnectionStringProvider>.Instance
            );
        }

        [Test]
        public void It_should_return_null()
        {
            var result = _connectionStringProvider!.GetHealthCheckConnectionString();

            result.Should().BeNull();
        }
    }

    [TestFixture]
    public class Given_No_Loaded_Tenants
    {
        private IDataStoreProvider? _dataStoreProvider;
        private DmsConnectionStringProvider? _connectionStringProvider;

        [SetUp]
        public void Setup()
        {
            _dataStoreProvider = A.Fake<IDataStoreProvider>();
            A.CallTo(() => _dataStoreProvider.GetLoadedTenantKeys()).Returns(new List<string>().AsReadOnly());

            _connectionStringProvider = new DmsConnectionStringProvider(
                _dataStoreProvider,
                NullLogger<DmsConnectionStringProvider>.Instance
            );
        }

        [Test]
        public void It_should_return_null()
        {
            var result = _connectionStringProvider!.GetHealthCheckConnectionString();

            result.Should().BeNull();
        }
    }

    // Records the level of every log call, so a test can assert what a probe-frequency path logs.
    private sealed class LevelRecordingLogger : ILogger<DmsConnectionStringProvider>
    {
        public List<LogLevel> Levels { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        ) => Levels.Add(logLevel);
    }

    // A tenant can exist before its data store does (multi-tenant onboarding), and the health check
    // calls this on every probe. That state must not produce a Warning each time.
    [TestFixture]
    public class Given_A_Loaded_Tenant_With_No_DataStores
    {
        private readonly LevelRecordingLogger _logger = new();
        private string? _result;

        [SetUp]
        public void Setup()
        {
            var dataStoreProvider = A.Fake<IDataStoreProvider>();
            A.CallTo(() => dataStoreProvider.GetLoadedTenantKeys())
                .Returns(new List<string> { "tenant1" }.AsReadOnly());
            A.CallTo(() => dataStoreProvider.GetAll(A<string?>.Ignored)).Returns(new List<DataStore>());

            _result = new DmsConnectionStringProvider(
                dataStoreProvider,
                _logger
            ).GetHealthCheckConnectionString();
        }

        [Test]
        public void It_should_return_null()
        {
            _result.Should().BeNull();
        }

        [Test]
        public void It_should_not_log_a_warning()
        {
            _logger
                .Levels.Should()
                .NotContain(LogLevel.Warning)
                .And.OnlyContain(level => level == LogLevel.Debug);
        }
    }

    // Data stores exist but none can be probed: the one genuine misconfiguration, still a Warning.
    [TestFixture]
    public class Given_DataStores_None_Of_Which_Has_A_ConnectionString
    {
        private readonly LevelRecordingLogger _logger = new();
        private string? _result;

        [SetUp]
        public void Setup()
        {
            var dataStoreProvider = A.Fake<IDataStoreProvider>();
            A.CallTo(() => dataStoreProvider.GetLoadedTenantKeys())
                .Returns(new List<string> { "tenant1" }.AsReadOnly());
            A.CallTo(() => dataStoreProvider.GetAll(A<string?>.Ignored))
                .Returns(new List<DataStore> { new(1, "SchoolYear", "No Connection", null, []) });

            _result = new DmsConnectionStringProvider(
                dataStoreProvider,
                _logger
            ).GetHealthCheckConnectionString();
        }

        [Test]
        public void It_should_return_null()
        {
            _result.Should().BeNull();
        }

        [Test]
        public void It_should_log_a_warning()
        {
            _logger.Levels.Should().Contain(LogLevel.Warning);
        }
    }

    // Previously only each tenant's lowest-Id data store was considered, so a tenant whose first
    // store had no connection string was skipped even though a later store could be probed.
    [TestFixture]
    public class Given_The_Lowest_Id_DataStore_Has_No_ConnectionString
    {
        private string? _result;

        [SetUp]
        public void Setup()
        {
            var dataStoreProvider = A.Fake<IDataStoreProvider>();
            A.CallTo(() => dataStoreProvider.GetLoadedTenantKeys())
                .Returns(new List<string> { "tenant1" }.AsReadOnly());
            A.CallTo(() => dataStoreProvider.GetAll(A<string?>.Ignored))
                .Returns(
                    new List<DataStore>
                    {
                        new(2, "SchoolYear", "Has Connection", "host=second;database=db2;", []),
                        new(1, "SchoolYear", "No Connection", null, []),
                    }
                );

            _result = new DmsConnectionStringProvider(
                dataStoreProvider,
                NullLogger<DmsConnectionStringProvider>.Instance
            ).GetHealthCheckConnectionString();
        }

        [Test]
        public void It_should_return_the_next_data_store_with_a_connection_string()
        {
            _result.Should().Be("host=second;database=db2;");
        }
    }
}

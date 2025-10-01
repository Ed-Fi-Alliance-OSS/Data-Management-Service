// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Configuration;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Configuration;

public class DmsConnectionStringProviderTests
{
    [TestFixture]
    public class Given_Valid_DmsInstance_Id
    {
        private IDmsInstanceProvider? _dmsInstanceProvider;
        private DmsConnectionStringProvider? _connectionStringProvider;
        private string? _connectionString;

        [SetUp]
        public void Setup()
        {
            _dmsInstanceProvider = A.Fake<IDmsInstanceProvider>();
            var instance = new DmsInstance(
                1,
                "Production",
                "Main Instance",
                "host=localhost;port=5432;username=postgres;database=edfi;"
            );

            A.CallTo(() => _dmsInstanceProvider.GetById(1)).Returns(instance);

            _connectionStringProvider = new DmsConnectionStringProvider(
                _dmsInstanceProvider,
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
    public class Given_NonExistent_DmsInstance_Id
    {
        private IDmsInstanceProvider? _dmsInstanceProvider;
        private DmsConnectionStringProvider? _connectionStringProvider;

        [SetUp]
        public void Setup()
        {
            _dmsInstanceProvider = A.Fake<IDmsInstanceProvider>();
            A.CallTo(() => _dmsInstanceProvider.GetById(999)).Returns(null);
            A.CallTo(() => _dmsInstanceProvider.GetAll()).Returns(new List<DmsInstance>());

            _connectionStringProvider = new DmsConnectionStringProvider(
                _dmsInstanceProvider,
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
    public class Given_DmsInstance_With_Null_ConnectionString
    {
        private IDmsInstanceProvider? _dmsInstanceProvider;
        private DmsConnectionStringProvider? _connectionStringProvider;

        [SetUp]
        public void Setup()
        {
            _dmsInstanceProvider = A.Fake<IDmsInstanceProvider>();
            var instance = new DmsInstance(1, "Production", "Main Instance", null);

            A.CallTo(() => _dmsInstanceProvider.GetById(1)).Returns(instance);

            _connectionStringProvider = new DmsConnectionStringProvider(
                _dmsInstanceProvider,
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
    public class Given_DmsInstance_With_Empty_ConnectionString
    {
        private IDmsInstanceProvider? _dmsInstanceProvider;
        private DmsConnectionStringProvider? _connectionStringProvider;

        [SetUp]
        public void Setup()
        {
            _dmsInstanceProvider = A.Fake<IDmsInstanceProvider>();
            var instance = new DmsInstance(1, "Production", "Main Instance", "   ");

            A.CallTo(() => _dmsInstanceProvider.GetById(1)).Returns(instance);

            _connectionStringProvider = new DmsConnectionStringProvider(
                _dmsInstanceProvider,
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
    public class Given_Multiple_DmsInstances
    {
        private IDmsInstanceProvider? _dmsInstanceProvider;
        private DmsConnectionStringProvider? _connectionStringProvider;
        private string? _defaultConnectionString;

        [SetUp]
        public void Setup()
        {
            _dmsInstanceProvider = A.Fake<IDmsInstanceProvider>();

            var instances = new List<DmsInstance>
            {
                new(3, "Production", "Third Instance", "host=third;database=db3;"),
                new(1, "Production", "First Instance", "host=first;database=db1;"),
                new(2, "Development", "Second Instance", "host=second;database=db2;"),
            };

            A.CallTo(() => _dmsInstanceProvider.GetAll()).Returns(instances);

            _connectionStringProvider = new DmsConnectionStringProvider(
                _dmsInstanceProvider,
                NullLogger<DmsConnectionStringProvider>.Instance
            );
            _defaultConnectionString = _connectionStringProvider.GetDefaultConnectionString();
        }

        [Test]
        public void It_should_return_connection_string_from_lowest_id_instance()
        {
            _defaultConnectionString.Should().NotBeNullOrEmpty();
            _defaultConnectionString.Should().Be("host=first;database=db1;");
        }
    }

    [TestFixture]
    public class Given_Single_DmsInstance
    {
        private IDmsInstanceProvider? _dmsInstanceProvider;
        private DmsConnectionStringProvider? _connectionStringProvider;
        private string? _defaultConnectionString;

        [SetUp]
        public void Setup()
        {
            _dmsInstanceProvider = A.Fake<IDmsInstanceProvider>();

            var instances = new List<DmsInstance>
            {
                new(5, "Production", "Only Instance", "host=only;database=dbonly;"),
            };

            A.CallTo(() => _dmsInstanceProvider.GetAll()).Returns(instances);

            _connectionStringProvider = new DmsConnectionStringProvider(
                _dmsInstanceProvider,
                NullLogger<DmsConnectionStringProvider>.Instance
            );
            _defaultConnectionString = _connectionStringProvider.GetDefaultConnectionString();
        }

        [Test]
        public void It_should_return_connection_string_from_single_instance()
        {
            _defaultConnectionString.Should().NotBeNullOrEmpty();
            _defaultConnectionString.Should().Be("host=only;database=dbonly;");
        }
    }

    [TestFixture]
    public class Given_No_DmsInstances_Configured
    {
        private IDmsInstanceProvider? _dmsInstanceProvider;
        private DmsConnectionStringProvider? _connectionStringProvider;

        [SetUp]
        public void Setup()
        {
            _dmsInstanceProvider = A.Fake<IDmsInstanceProvider>();
            A.CallTo(() => _dmsInstanceProvider.GetAll()).Returns(new List<DmsInstance>());

            _connectionStringProvider = new DmsConnectionStringProvider(
                _dmsInstanceProvider,
                NullLogger<DmsConnectionStringProvider>.Instance
            );
        }

        [Test]
        public void It_should_return_null()
        {
            var result = _connectionStringProvider!.GetDefaultConnectionString();

            result.Should().BeNull();
        }
    }

    [TestFixture]
    public class Given_Default_DmsInstance_With_Null_ConnectionString
    {
        private IDmsInstanceProvider? _dmsInstanceProvider;
        private DmsConnectionStringProvider? _connectionStringProvider;

        [SetUp]
        public void Setup()
        {
            _dmsInstanceProvider = A.Fake<IDmsInstanceProvider>();

            var instances = new List<DmsInstance> { new(1, "Production", "Invalid Instance", null) };

            A.CallTo(() => _dmsInstanceProvider.GetAll()).Returns(instances);

            _connectionStringProvider = new DmsConnectionStringProvider(
                _dmsInstanceProvider,
                NullLogger<DmsConnectionStringProvider>.Instance
            );
        }

        [Test]
        public void It_should_return_null()
        {
            var result = _connectionStringProvider!.GetDefaultConnectionString();

            result.Should().BeNull();
        }
    }
}

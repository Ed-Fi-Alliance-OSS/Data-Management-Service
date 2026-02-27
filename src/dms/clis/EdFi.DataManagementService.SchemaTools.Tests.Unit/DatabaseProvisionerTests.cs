// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.SchemaTools.Provisioning;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.SchemaTools.Tests.Unit;

[TestFixture]
public class DatabaseProvisionerTests
{
    [TestFixture]
    public class Given_PgsqlDatabaseProvisioner_With_Valid_Connection_String : DatabaseProvisionerTests
    {
        private PgsqlDatabaseProvisioner _provisioner = null!;
        private string _databaseName = null!;

        [SetUp]
        public void SetUp()
        {
            _provisioner = new PgsqlDatabaseProvisioner(A.Fake<ILogger>());
            _databaseName = _provisioner.GetDatabaseName(
                "Host=localhost;Port=5432;Database=edfi_dms;Username=postgres;Password=secret"
            );
        }

        [Test]
        public void It_extracts_the_database_name()
        {
            _databaseName.Should().Be("edfi_dms");
        }
    }

    [TestFixture]
    public class Given_PgsqlDatabaseProvisioner_With_No_Database_In_Connection_String
        : DatabaseProvisionerTests
    {
        private PgsqlDatabaseProvisioner _provisioner = null!;
        private Action _action = null!;

        [SetUp]
        public void SetUp()
        {
            _provisioner = new PgsqlDatabaseProvisioner(A.Fake<ILogger>());
            _action = () =>
                _provisioner.GetDatabaseName("Host=localhost;Port=5432;Username=postgres;Password=secret");
        }

        [Test]
        public void It_throws_InvalidOperationException()
        {
            _action.Should().Throw<InvalidOperationException>();
        }

        [Test]
        public void It_includes_a_clear_error_message()
        {
            _action.Should().Throw<InvalidOperationException>().WithMessage("*database name*");
        }
    }

    [TestFixture]
    public class Given_MssqlDatabaseProvisioner_With_Valid_Connection_String : DatabaseProvisionerTests
    {
        private MssqlDatabaseProvisioner _provisioner = null!;
        private string _databaseName = null!;

        [SetUp]
        public void SetUp()
        {
            _provisioner = new MssqlDatabaseProvisioner(A.Fake<ILogger>());
            _databaseName = _provisioner.GetDatabaseName(
                "Server=localhost;Initial Catalog=edfi_dms;User Id=sa;Password=secret;TrustServerCertificate=true"
            );
        }

        [Test]
        public void It_extracts_the_database_name()
        {
            _databaseName.Should().Be("edfi_dms");
        }
    }

    [TestFixture]
    public class Given_MssqlDatabaseProvisioner_With_Database_Keyword : DatabaseProvisionerTests
    {
        private MssqlDatabaseProvisioner _provisioner = null!;
        private string _databaseName = null!;

        [SetUp]
        public void SetUp()
        {
            _provisioner = new MssqlDatabaseProvisioner(A.Fake<ILogger>());
            _databaseName = _provisioner.GetDatabaseName(
                "Server=localhost;Database=edfi_dms;User Id=sa;Password=secret;TrustServerCertificate=true"
            );
        }

        [Test]
        public void It_extracts_the_database_name()
        {
            _databaseName.Should().Be("edfi_dms");
        }
    }

    [TestFixture]
    public class Given_MssqlDatabaseProvisioner_With_No_Database_In_Connection_String
        : DatabaseProvisionerTests
    {
        private MssqlDatabaseProvisioner _provisioner = null!;
        private Action _action = null!;

        [SetUp]
        public void SetUp()
        {
            _provisioner = new MssqlDatabaseProvisioner(A.Fake<ILogger>());
            _action = () =>
                _provisioner.GetDatabaseName(
                    "Server=localhost;User Id=sa;Password=secret;TrustServerCertificate=true"
                );
        }

        [Test]
        public void It_throws_InvalidOperationException()
        {
            _action.Should().Throw<InvalidOperationException>();
        }

        [Test]
        public void It_includes_a_clear_error_message()
        {
            _action.Should().Throw<InvalidOperationException>().WithMessage("*database name*");
        }
    }

    [TestFixture]
    public class Given_Pgsql_Provisioner_CheckOrConfigureMvcc : DatabaseProvisionerTests
    {
        private PgsqlDatabaseProvisioner _provisioner = null!;

        [SetUp]
        public void SetUp()
        {
            _provisioner = new PgsqlDatabaseProvisioner(A.Fake<ILogger>());
        }

        [Test]
        public void It_is_a_no_op_when_database_was_created()
        {
            // Should not throw - PostgreSQL MVCC check is a no-op
            var action = () =>
                _provisioner.CheckOrConfigureMvcc(
                    "Host=localhost;Database=edfi_dms;Username=postgres;Password=secret",
                    databaseWasCreated: true
                );
            action.Should().NotThrow();
        }

        [Test]
        public void It_is_a_no_op_when_database_was_not_created()
        {
            // Should not throw - PostgreSQL MVCC check is a no-op
            var action = () =>
                _provisioner.CheckOrConfigureMvcc(
                    "Host=localhost;Database=edfi_dms;Username=postgres;Password=secret",
                    databaseWasCreated: false
                );
            action.Should().NotThrow();
        }
    }
}

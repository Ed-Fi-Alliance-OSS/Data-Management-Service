// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.SchemaTools.Provisioning;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.SchemaTools.Tests.Unit;

public class DatabaseProvisionerTests
{
    [TestFixture]
    public class Given_PgsqlDatabaseProvisioner_With_Valid_Connection_String
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
    public class Given_MssqlDatabaseProvisioner_With_Valid_Connection_String
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
    public class Given_MssqlDatabaseProvisioner_With_Database_Keyword
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
    public class Given_Pgsql_Provisioner_CheckOrConfigureMvcc
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

    [TestFixture]
    public class Given_Mssql_Sql_With_Go_Batch_Separators
    {
        private List<string> _batches = null!;

        [SetUp]
        public void SetUp()
        {
            var sql =
                "CREATE TABLE t1 (id INT);\nGO\nCREATE OR ALTER TRIGGER tr1\nON t1\nFOR INSERT AS\nBEGIN\n  RETURN\nEND;\nGO\nINSERT INTO t1 VALUES (1);";
            _batches = MssqlDatabaseProvisioner.SplitOnGoBatchSeparator(sql).ToList();
        }

        [Test]
        public void It_splits_into_three_batches()
        {
            _batches.Should().HaveCount(3);
        }

        [Test]
        public void It_returns_first_batch_before_go()
        {
            _batches[0].Should().Be("CREATE TABLE t1 (id INT);");
        }

        [Test]
        public void It_returns_trigger_batch()
        {
            _batches[1].Should().Contain("CREATE OR ALTER TRIGGER");
        }

        [Test]
        public void It_returns_last_batch_after_go()
        {
            _batches[2].Should().Be("INSERT INTO t1 VALUES (1);");
        }
    }

    [TestFixture]
    public class Given_Mssql_Sql_Without_Go_Separators
    {
        private List<string> _batches = null!;

        [SetUp]
        public void SetUp()
        {
            var sql = "CREATE TABLE t1 (id INT);\nCREATE TABLE t2 (id INT);";
            _batches = MssqlDatabaseProvisioner.SplitOnGoBatchSeparator(sql).ToList();
        }

        [Test]
        public void It_returns_single_batch()
        {
            _batches.Should().HaveCount(1);
        }

        [Test]
        public void It_returns_the_full_sql()
        {
            _batches[0].Should().Be("CREATE TABLE t1 (id INT);\nCREATE TABLE t2 (id INT);");
        }
    }

    [TestFixture]
    public class Given_Mssql_Sql_With_Case_Insensitive_Go
    {
        private List<string> _batches = null!;

        [SetUp]
        public void SetUp()
        {
            var sql = "SELECT 1;\ngo\nSELECT 2;\n  Go  \nSELECT 3;";
            _batches = MssqlDatabaseProvisioner.SplitOnGoBatchSeparator(sql).ToList();
        }

        [Test]
        public void It_splits_on_all_go_variants()
        {
            _batches.Should().HaveCount(3);
        }
    }

    [TestFixture]
    public class Given_Mssql_Sql_With_Go_In_Identifier
    {
        private List<string> _batches = null!;

        [SetUp]
        public void SetUp()
        {
            var sql = "CREATE TABLE category (id INT);";
            _batches = MssqlDatabaseProvisioner.SplitOnGoBatchSeparator(sql).ToList();
        }

        [Test]
        public void It_does_not_split_on_go_within_words()
        {
            _batches.Should().HaveCount(1);
        }
    }

    [TestFixture]
    public class Given_Mssql_Sql_With_Empty_Batches_Between_Go
    {
        private List<string> _batches = null!;

        [SetUp]
        public void SetUp()
        {
            var sql = "SELECT 1;\nGO\n\nGO\nSELECT 2;";
            _batches = MssqlDatabaseProvisioner.SplitOnGoBatchSeparator(sql).ToList();
        }

        [Test]
        public void It_filters_out_empty_batches()
        {
            _batches.Should().HaveCount(2);
        }
    }
}

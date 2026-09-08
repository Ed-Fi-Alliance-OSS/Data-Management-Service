// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using EdFi.DataManagementService.Backend.Mssql;
using EdFi.DataManagementService.Backend.Postgresql;
using FluentAssertions;
using Npgsql;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

/// <summary>
/// What each engine counts as an expected failure of establishing a connection. One definition per
/// engine serves both the DocumentCache read-lookup adapter and the read-path seam guard, so a change
/// here moves an unavailable cache read and an unavailable database together.
/// </summary>
[TestFixture]
[Parallelizable]
public class ConnectionAcquisitionClassifierTests
{
    /// <summary>
    /// A concrete DbException, since the type itself is abstract and SqlException cannot be
    /// constructed. The SQL Server classifier keys on the base type on purpose, so this stands in for
    /// any driver's own.
    /// </summary>
    private sealed class StubDbException(string message) : DbException(message);

    /// <summary>
    /// A null-argument failure carrying a parameter name that really exists, which is what the static
    /// analyzer requires of any ArgumentException construction.
    /// </summary>
    private static ArgumentNullException NullArgumentFailure(string? connectionString = null) =>
        new(
            nameof(connectionString),
            $"'{nameof(connectionString)}' must not be {connectionString ?? "null"}."
        );

    [TestFixture]
    [Parallelizable]
    public class Given_The_Postgresql_Classifier : ConnectionAcquisitionClassifierTests
    {
        [Test]
        public void It_accepts_the_provider_failures_of_connection_establishment()
        {
            PostgresqlConnectionAcquisitionFailure
                .IsExpected(new NpgsqlException("connection refused"))
                .Should()
                .BeTrue("catalog absence, authentication, DNS and network failures arrive as this");
            PostgresqlConnectionAcquisitionFailure
                .IsExpected(new TimeoutException("timed out"))
                .Should()
                .BeTrue();
            PostgresqlConnectionAcquisitionFailure
                .IsExpected(new FormatException("malformed value"))
                .Should()
                .BeTrue("a provider-invalid connection string fails while being parsed");
            PostgresqlConnectionAcquisitionFailure
                .IsExpected(new ArgumentException("unknown keyword"))
                .Should()
                .BeTrue("Npgsql rejects an unrecognized keyword while building the data source");
        }

        /// <summary>
        /// ArgumentNullException is the case the pattern's precedence turns on: it derives from
        /// ArgumentException, so the exclusion has to be read as applying to that arm alone.
        /// </summary>
        [Test]
        public void It_rejects_a_null_argument_even_though_it_is_an_argument_exception()
        {
            NullArgumentFailure().Should().BeAssignableTo<ArgumentException>();

            PostgresqlConnectionAcquisitionFailure
                .IsExpected(NullArgumentFailure())
                .Should()
                .BeFalse("a null argument is a programming defect, not an unreachable database");
        }

        [Test]
        public void It_rejects_defects_raised_inside_the_same_boundary()
        {
            PostgresqlConnectionAcquisitionFailure
                .IsExpected(new InvalidOperationException("no target selected"))
                .Should()
                .BeFalse();
            PostgresqlConnectionAcquisitionFailure
                .IsExpected(new NullReferenceException())
                .Should()
                .BeFalse();
            PostgresqlConnectionAcquisitionFailure
                .IsExpected(new Exception("something else"))
                .Should()
                .BeFalse();
        }

        /// <summary>
        /// A SQL Server failure must not be classified by the PostgreSQL predicate, which is what keeps
        /// the two definitions from quietly becoming one.
        /// </summary>
        [Test]
        public void It_rejects_a_bare_db_exception()
        {
            PostgresqlConnectionAcquisitionFailure
                .IsExpected(new StubDbException("some other provider"))
                .Should()
                .BeFalse();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_The_Sql_Server_Classifier : ConnectionAcquisitionClassifierTests
    {
        [Test]
        public void It_accepts_the_provider_failures_of_connection_establishment()
        {
            MssqlConnectionAcquisitionFailure
                .IsExpected(new StubDbException("login failed"))
                .Should()
                .BeTrue("SqlException is a DbException, and so is any wrapping provider's");
            MssqlConnectionAcquisitionFailure.IsExpected(new TimeoutException("timed out")).Should().BeTrue();
            MssqlConnectionAcquisitionFailure
                .IsExpected(new FormatException("malformed value"))
                .Should()
                .BeTrue();
            MssqlConnectionAcquisitionFailure
                .IsExpected(new ArgumentException("Keyword not supported"))
                .Should()
                .BeTrue("SqlConnectionStringBuilder rejects an unsupported keyword while parsing");
        }

        [Test]
        public void It_rejects_a_null_argument_even_though_it_is_an_argument_exception()
        {
            MssqlConnectionAcquisitionFailure
                .IsExpected(NullArgumentFailure())
                .Should()
                .BeFalse("a null argument is a programming defect, not an unreachable database");
        }

        [Test]
        public void It_rejects_defects_raised_inside_the_same_boundary()
        {
            MssqlConnectionAcquisitionFailure
                .IsExpected(new InvalidOperationException("no target selected"))
                .Should()
                .BeFalse();
            MssqlConnectionAcquisitionFailure.IsExpected(new NullReferenceException()).Should().BeFalse();
            MssqlConnectionAcquisitionFailure.IsExpected(new Exception("something else")).Should().BeFalse();
        }
    }
}

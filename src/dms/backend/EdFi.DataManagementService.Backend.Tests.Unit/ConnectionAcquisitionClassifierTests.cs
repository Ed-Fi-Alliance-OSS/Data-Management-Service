// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Net.Sockets;
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
                .BeTrue("catalog absence, authentication, and a refused or reset connection arrive as this");
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
            PostgresqlConnectionAcquisitionFailure
                .IsExpected(new NotSupportedException("unsupported option combination"))
                .Should()
                .BeTrue("Npgsql rejects an unsupported combination of options while building");
        }

        /// <summary>
        /// The construction-validation failures, driven through the production build rather than a
        /// hand-made exception: an unsupported combination of individually valid options is accepted by
        /// parsing and refused by Build, so the classification is only worth anything if it still
        /// matches the type Npgsql actually raises.
        /// </summary>
        [TestCase(
            "Host=snapshot-host;Database=edfi;Username=u;Password=p;Target Session Attributes=read-only",
            TestName = "single-host Target Session Attributes"
        )]
        [TestCase(
            "Host=snapshot-host,other-host;Database=edfi;Username=u;Password=p;Multiplexing=true",
            TestName = "multiplexing with several hosts"
        )]
        public void It_classifies_what_the_real_builder_refuses(string providerInvalidConnectionString)
        {
            Action build = () =>
                NpgsqlDataSourceLifetime.Instance.Build(providerInvalidConnectionString).Dispose();

            NotSupportedException failure = build.Should().Throw<NotSupportedException>().Which;

            PostgresqlConnectionAcquisitionFailure
                .IsExpected(failure)
                .Should()
                .BeTrue("a provider-invalid connection string is an unavailable database, not a defect");
        }

        /// <summary>
        /// A hostname that does not resolve - the one failure on the design's list that no other arm
        /// covers. Npgsql resolves the host before the socket-connect catch that wraps failures in
        /// NpgsqlException, so a resolution failure is raised unwrapped while a refused connection
        /// arrives wrapped. Observed against the Npgsql 8.0.4 this solution pins: an unresolvable host
        /// raises SocketException with SocketErrorCode.HostNotFound and no outer exception. The
        /// exception is constructed rather than provoked, because a test that performed a real lookup
        /// would invert its own assertion on any resolver that answers NXDOMAIN with a wildcard
        /// address.
        /// </summary>
        [Test]
        public void It_accepts_a_hostname_that_does_not_resolve()
        {
            PostgresqlConnectionAcquisitionFailure
                .IsExpected(new SocketException((int)SocketError.HostNotFound))
                .Should()
                .BeTrue("a snapshot whose host cannot be resolved is an unreachable database");
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

        /// <summary>
        /// A failure the server answered carries its SQLSTATE, which is what separates an absent
        /// catalog from a failed authentication from a broken connection in a log that may carry
        /// nothing else about the failure.
        /// </summary>
        [Test]
        public void It_describes_a_server_failure_with_its_sqlstate()
        {
            PostgresqlConnectionAcquisitionFailure
                .Describe(new PostgresException("db missing", "FATAL", "FATAL", "3D000"))
                .Should()
                .Be("PostgresException(3D000)");
        }

        /// <summary>
        /// A parse failure and a socket failure never reached a server, so there is no SQLSTATE to
        /// name and the type stands alone rather than being padded with an empty code.
        /// </summary>
        [Test]
        public void It_describes_a_failure_that_never_reached_a_server_by_type_alone()
        {
            PostgresqlConnectionAcquisitionFailure
                .Describe(new ArgumentException("Keyword not supported"))
                .Should()
                .Be("ArgumentException");
            PostgresqlConnectionAcquisitionFailure
                .Describe(new NpgsqlException("connection refused"))
                .Should()
                .Be("NpgsqlException");
        }

        /// <summary>
        /// The description is the whole of what may accompany the target kind into a log, so it must
        /// carry nothing the provider put in its own message.
        /// </summary>
        [Test]
        public void It_describes_without_quoting_the_provider_message()
        {
            PostgresqlConnectionAcquisitionFailure
                .Describe(new NpgsqlException("failed for host=secret;password=hunter2"))
                .Should()
                .NotContain("hunter2");
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

        /// <summary>
        /// A parsing ArgumentException and a wrapping provider's plain DbException have no SQL Server
        /// error number, so the type stands alone rather than being padded with a meaningless code.
        /// </summary>
        [Test]
        public void It_describes_a_failure_without_a_server_number_by_type_alone()
        {
            MssqlConnectionAcquisitionFailure
                .Describe(new ArgumentException("Keyword not supported"))
                .Should()
                .Be("ArgumentException");
            MssqlConnectionAcquisitionFailure
                .Describe(new StubDbException("some other provider"))
                .Should()
                .Be("StubDbException");
        }

        /// <summary>
        /// The description is the whole of what may accompany the target kind into a log, so it must
        /// carry nothing the provider put in its own message.
        /// </summary>
        [Test]
        public void It_describes_without_quoting_the_provider_message()
        {
            MssqlConnectionAcquisitionFailure
                .Describe(new StubDbException("login failed for Server=x;Password=hunter2"))
                .Should()
                .NotContain("hunter2");
        }
    }
}

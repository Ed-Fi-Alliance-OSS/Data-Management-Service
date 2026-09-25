// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Dapper;
using EdFi.DmsConfigurationService.Backend.Jobs;
using EdFi.DmsConfigurationService.Backend.Postgresql.Jobs;
using FluentAssertions;
using Npgsql;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration.Jobs;

public class PostgresqlJobDiagnosticsTests
{
    /// <summary>
    /// Errors raised by a real PostgreSQL server on connections that include error detail, so each error
    /// demonstrably carries row values that the diagnostic must not keep.
    /// </summary>
    [TestFixture]
    public class Given_real_PostgreSQL_errors : JobSchemaTestBase
    {
        private const string Operation = "Probe";
        private const string JobIdLiteral = "job-id-literal-4b7d";
        private const string PayloadLiteral = "payload-literal-91c2";

        private PostgresException _uniqueViolation = null!;
        private PostgresException _checkViolation = null!;
        private PostgresException _lockTimeout = null!;

        [SetUp]
        public async Task Setup()
        {
            (await TryInsertJobAsync(jobId: JobIdLiteral)).Should().BeNull();

            NpgsqlConnectionStringBuilder withDetail = new(
                Configuration.DatabaseOptions.Value.DatabaseConnection
            )
            {
                IncludeErrorDetail = true,
            };

            await using NpgsqlConnection first = new(withDetail.ConnectionString);
            await using NpgsqlConnection second = new(withDetail.ConnectionString);
            await first.OpenAsync();
            await second.OpenAsync();

            _uniqueViolation = await PostgresErrorAsync(
                first,
                """
                INSERT INTO "dmscs"."Job" ("JobId", "JobType", "PayloadVersion", "Payload", "Status", "NextAttemptAt")
                VALUES (@JobId, 'Probe.Type', 1, '{}', 'Pending', (now() AT TIME ZONE 'UTC'));
                """,
                new { JobId = JobIdLiteral }
            );

            _checkViolation = await PostgresErrorAsync(
                first,
                """
                INSERT INTO "dmscs"."Job" ("JobId", "JobType", "PayloadVersion", "Payload", "Status", "NextAttemptAt")
                VALUES (@JobId, 'Probe.Type', 1, @Payload, 'Pending', (now() AT TIME ZONE 'UTC'));
                """,
                new { JobId = Guid.NewGuid().ToString("N"), Payload = $"""["{PayloadLiteral}"]""" }
            );

            await using NpgsqlTransaction holder = await first.BeginTransactionAsync();
            await first.ExecuteAsync(
                """SELECT 1 FROM "dmscs"."Job" WHERE "JobId" = @JobId FOR UPDATE;""",
                new { JobId = JobIdLiteral },
                holder
            );

            await using NpgsqlTransaction waiter = await second.BeginTransactionAsync();
            await second.ExecuteAsync("SET LOCAL lock_timeout = '100ms';", transaction: waiter);
            _lockTimeout = await PostgresErrorAsync(
                second,
                """SELECT 1 FROM "dmscs"."Job" WHERE "JobId" = @JobId FOR UPDATE;""",
                new { JobId = JobIdLiteral },
                waiter
            );
        }

        [Test]
        public void It_keeps_the_unique_violation_code_and_no_row_values()
        {
            _uniqueViolation.Detail.Should().Contain(JobIdLiteral, "the error itself carries the row value");

            JobFailureDiagnostic diagnostic = PostgresqlJobDiagnostics.From(_uniqueViolation, Operation);

            diagnostic.Should().Be(new JobFailureDiagnostic("Npgsql.PostgresException", "23505", Operation));
            diagnostic.ToString().Should().NotContain(JobIdLiteral);
        }

        [Test]
        public void It_keeps_only_the_code_of_an_error_whose_detail_carries_a_payload_literal()
        {
            _checkViolation.Detail.Should().Contain(PayloadLiteral, "the failing row includes the payload");

            JobFailureDiagnostic diagnostic = PostgresqlJobDiagnostics.From(_checkViolation, Operation);

            diagnostic.Should().Be(new JobFailureDiagnostic("Npgsql.PostgresException", "23514", Operation));
            diagnostic.ToString().Should().NotContain(PayloadLiteral);
        }

        [Test]
        public void It_reports_a_lock_timeout_as_55P03() =>
            PostgresqlJobDiagnostics
                .From(_lockTimeout, Operation)
                .Should()
                .Be(new JobFailureDiagnostic("Npgsql.PostgresException", "55P03", Operation));

        [Test]
        public void It_reports_the_full_type_chain_and_the_inner_code_of_a_wrapped_error()
        {
            InvalidOperationException wrapped = new($"wrapper {PayloadLiteral}", _uniqueViolation);

            JobFailureDiagnostic diagnostic = PostgresqlJobDiagnostics.From(wrapped, Operation);

            diagnostic
                .Should()
                .Be(
                    new JobFailureDiagnostic(
                        "System.InvalidOperationException/Npgsql.PostgresException",
                        "23505",
                        Operation
                    )
                );
            diagnostic.ToString().Should().NotContain(PayloadLiteral).And.NotContain(JobIdLiteral);
        }

        private static async Task<PostgresException> PostgresErrorAsync(
            NpgsqlConnection connection,
            string sql,
            object parameters,
            NpgsqlTransaction? transaction = null
        )
        {
            try
            {
                await connection.ExecuteAsync(sql, parameters, transaction);
            }
            catch (PostgresException exception)
            {
                return exception;
            }

            throw new AssertionException("The statement was expected to fail.");
        }
    }

    [TestFixture]
    public class Given_errors_without_a_usable_SqlState
    {
        private JobFailureDiagnostic _nonPostgres = null!;
        private JobFailureDiagnostic _malformedSqlState = null!;

        [SetUp]
        public void Setup()
        {
            _nonPostgres = PostgresqlJobDiagnostics.From(new TimeoutException("timed out"), "Probe");
            _malformedSqlState = PostgresqlJobDiagnostics.From(
                new PostgresException("message", "ERROR", "ERROR", "value 42"),
                "Probe"
            );
        }

        [Test]
        public void It_reports_no_code_for_an_error_that_is_not_from_PostgreSQL() =>
            _nonPostgres.Should().Be(new JobFailureDiagnostic("System.TimeoutException", null, "Probe"));

        [Test]
        public void It_drops_a_code_that_is_not_a_five_character_SqlState() =>
            _malformedSqlState
                .Should()
                .Be(new JobFailureDiagnostic("Npgsql.PostgresException", null, "Probe"));
    }
}

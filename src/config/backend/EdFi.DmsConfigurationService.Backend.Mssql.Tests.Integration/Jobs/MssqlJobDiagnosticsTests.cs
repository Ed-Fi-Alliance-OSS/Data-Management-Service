// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Dapper;
using EdFi.DmsConfigurationService.Backend.Jobs;
using EdFi.DmsConfigurationService.Backend.Mssql.Jobs;
using FluentAssertions;
using Microsoft.Data.SqlClient;

namespace EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration.Jobs;

public class MssqlJobDiagnosticsTests
{
    /// <summary>
    /// Errors raised by a real SQL Server. SQL Server puts row values in the error message itself, so each
    /// test first shows the message carries the value that the diagnostic must not keep.
    /// </summary>
    [TestFixture]
    public class Given_real_SQL_Server_errors : JobSchemaTestBase
    {
        private const string Operation = "Probe";
        private const string JobIdLiteral = "job-id-literal-4b7d";
        private const string PayloadLiteral = "payload-literal-91c2";

        private SqlException _uniqueConstraintViolation = null!;
        private SqlException _uniqueIndexViolation = null!;
        private SqlException _truncation = null!;
        private SqlException _lockTimeout = null!;

        [SetUp]
        public async Task Setup()
        {
            (await TryInsertJobAsync(jobId: JobIdLiteral)).Should().BeNull();

            long scheduleId = await CreateScheduleAsync();
            DateTime occurrence = new(2026, 9, 23, 10, 0, 0, DateTimeKind.Utc);
            (await TryInsertJobAsync(sourceScheduleId: scheduleId, scheduledOccurrence: occurrence))
                .Should()
                .BeNull();

            _uniqueConstraintViolation = await SqlErrorAsync(
                Connection!,
                """
                INSERT INTO dmscs.Job (JobId, JobType, PayloadVersion, Payload, Status, NextAttemptAt)
                VALUES (@JobId, N'Probe.Type', 1, N'{}', N'Pending', SYSUTCDATETIME());
                """,
                new { JobId = JobIdLiteral }
            );

            _uniqueIndexViolation = await SqlErrorAsync(
                Connection!,
                """
                INSERT INTO dmscs.Job (
                    JobId, JobType, PayloadVersion, Payload, Status, NextAttemptAt, SourceScheduleId,
                    ScheduledOccurrence)
                VALUES (@JobId, N'Probe.Type', 1, N'{}', N'Pending', SYSUTCDATETIME(), @ScheduleId, @Occurrence);
                """,
                new
                {
                    JobId = Guid.NewGuid().ToString("N"),
                    ScheduleId = scheduleId,
                    Occurrence = occurrence,
                }
            );

            _truncation = await SqlErrorAsync(
                Connection!,
                """
                INSERT INTO dmscs.Job (JobId, JobType, PayloadVersion, Payload, Status, NextAttemptAt)
                VALUES (@JobId, N'Probe.Type', 1, @Payload, N'Pending', SYSUTCDATETIME());
                """,
                new
                {
                    JobId = Guid.NewGuid().ToString("N"),
                    Payload = $$"""{"value":"{{PayloadLiteral}}{{new string('x', 4000)}}"}""",
                }
            );

            await using SqlConnection holder = await OpenConnectionAsync();
            await using SqlTransaction holderTransaction = (SqlTransaction)
                await holder.BeginTransactionAsync();
            await holder.ExecuteAsync(
                "SELECT Id FROM dmscs.Job WITH (UPDLOCK, ROWLOCK) WHERE JobId = @JobId;",
                new { JobId = JobIdLiteral },
                holderTransaction
            );

            // Not pooled, so the session-level LOCK_TIMEOUT never reaches another test's connection.
            await using SqlConnection waiter = new(
                new SqlConnectionStringBuilder(ConnectionString) { Pooling = false }.ConnectionString
            );
            await waiter.OpenAsync();
            await waiter.ExecuteAsync("SET LOCK_TIMEOUT 100;");
            _lockTimeout = await SqlErrorAsync(
                waiter,
                "SELECT Id FROM dmscs.Job WITH (UPDLOCK, ROWLOCK) WHERE JobId = @JobId;",
                new { JobId = JobIdLiteral }
            );
        }

        [Test]
        public void It_keeps_the_unique_constraint_violation_number_and_no_row_values()
        {
            _uniqueConstraintViolation
                .Message.Should()
                .Contain(JobIdLiteral, "the error itself carries the row value");

            JobFailureDiagnostic diagnostic = MssqlJobDiagnostics.From(_uniqueConstraintViolation, Operation);

            diagnostic
                .Should()
                .Be(new JobFailureDiagnostic("Microsoft.Data.SqlClient.SqlException", "2627", Operation));
            diagnostic.ToString().Should().NotContain(JobIdLiteral);
        }

        [Test]
        public void It_keeps_the_unique_index_violation_number() =>
            MssqlJobDiagnostics
                .From(_uniqueIndexViolation, Operation)
                .Should()
                .Be(new JobFailureDiagnostic("Microsoft.Data.SqlClient.SqlException", "2601", Operation));

        [Test]
        public void It_keeps_only_the_number_of_an_error_whose_message_carries_a_payload_literal()
        {
            _truncation
                .Message.Should()
                .Contain(PayloadLiteral, "the truncated value is part of the message");

            JobFailureDiagnostic diagnostic = MssqlJobDiagnostics.From(_truncation, Operation);

            diagnostic
                .Should()
                .Be(new JobFailureDiagnostic("Microsoft.Data.SqlClient.SqlException", "2628", Operation));
            diagnostic.ToString().Should().NotContain(PayloadLiteral);
        }

        [Test]
        public void It_reports_a_lock_timeout_as_1222() =>
            MssqlJobDiagnostics
                .From(_lockTimeout, Operation)
                .Should()
                .Be(new JobFailureDiagnostic("Microsoft.Data.SqlClient.SqlException", "1222", Operation));

        [Test]
        public void It_reports_the_full_type_chain_and_the_inner_number_of_a_wrapped_error()
        {
            InvalidOperationException wrapped = new($"wrapper {PayloadLiteral}", _uniqueConstraintViolation);

            JobFailureDiagnostic diagnostic = MssqlJobDiagnostics.From(wrapped, Operation);

            diagnostic
                .Should()
                .Be(
                    new JobFailureDiagnostic(
                        "System.InvalidOperationException/Microsoft.Data.SqlClient.SqlException",
                        "2627",
                        Operation
                    )
                );
            diagnostic.ToString().Should().NotContain(PayloadLiteral).And.NotContain(JobIdLiteral);
        }

        private static async Task<SqlException> SqlErrorAsync(
            SqlConnection connection,
            string sql,
            object parameters
        )
        {
            try
            {
                await connection.ExecuteAsync(sql, parameters);
            }
            catch (SqlException exception)
            {
                return exception;
            }

            throw new AssertionException("The statement was expected to fail.");
        }
    }

    [TestFixture]
    public class Given_an_error_that_is_not_from_SQL_Server
    {
        private JobFailureDiagnostic _diagnostic = null!;

        [SetUp]
        public void Setup() =>
            _diagnostic = MssqlJobDiagnostics.From(new TimeoutException("timed out"), "Probe");

        [Test]
        public void It_reports_no_error_number() =>
            _diagnostic.Should().Be(new JobFailureDiagnostic("System.TimeoutException", null, "Probe"));
    }
}

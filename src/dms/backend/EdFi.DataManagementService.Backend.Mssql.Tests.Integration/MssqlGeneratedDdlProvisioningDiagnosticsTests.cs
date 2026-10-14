// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Reflection;
using System.Runtime.CompilerServices;
using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

[TestFixture]
[Category(MssqlCiShards.Shard4)]
public class Given_MssqlGeneratedDdlProvisioningDiagnostics
{
    private MssqlProvisioningTimingContext _context = null!;

    [SetUp]
    public void Setup() => _context = new("family", "ddl-hash", "backup-restore", "caller", "safe.cs", 17);

    [Test]
    public void It_classifies_only_an_admitted_SqlClient_command_timeout()
    {
        MssqlGeneratedDdlProvisioningDiagnostics
            .IsProvisioningCommandTimeout(CreateSqlException(-2, "secret A"), true)
            .Should()
            .BeTrue();
        MssqlGeneratedDdlProvisioningDiagnostics
            .IsProvisioningCommandTimeout(CreateSqlException(-2, "secret B"), false)
            .Should()
            .BeFalse();

        foreach (
            var exception in new Exception[]
            {
                CreateSqlException(1205, "deadlock"),
                CreateSqlException(547, "constraint"),
                CreateSqlException(50000, "custom"),
                new TimeoutException(),
                new OperationCanceledException(),
                new InvalidOperationException(),
            }
        )
        {
            MssqlGeneratedDdlProvisioningDiagnostics
                .IsProvisioningCommandTimeout(exception, true)
                .Should()
                .BeFalse();
        }
    }

    [Test]
    public void It_reports_separate_counts_and_an_actionable_unlimited_remedy()
    {
        var message = Format(0, 3, 1);

        message
            .Should()
            .StartWith("MSSQL generated-DDL provisioning timeout: suspected provisioning contention.");
        message
            .Should()
            .Contain("batch=2/5")
            .And.Contain("elapsedSeconds=301.250")
            .And.Contain("commandTimeoutSeconds=300")
            .And.Contain("admissionWaitSeconds=17.000")
            .And.Contain("admittedAtBatchStart=3")
            .And.Contain("admittedAtTimeout=1")
            .And.Contain("limit=unlimited")
            .And.Contain("MSSQL_GENERATED_DDL_PROVISION_MAX_CONCURRENCY=1")
            .And.Contain("including this operation")
            .And.Contain("excluding queued entrants")
            .And.Contain("do not prove an engine wait");
    }

    [Test]
    [SetCulture("fr-FR")]
    public void It_formats_numbers_invariantly()
    {
        var message = Format(2, 3, 1);
        message.Should().Contain("elapsedSeconds=301.250").And.Contain("admissionWaitSeconds=17.000");
    }

    [Test]
    public void It_explains_an_already_effective_limit_of_one()
    {
        MssqlGeneratedDdlProvisioningDiagnostics
            .Format(Diagnostic(1, 1, 1))
            .Should()
            .Contain("Already limited to 1 in this process")
            .And.Contain("other processes/server waits");
        Format(2, 2, 2).Should().Contain("restart the test process");
    }

    [Test]
    public void It_normalizes_context_and_falls_back_to_ddl_identity()
    {
        var diagnostic = Diagnostic(0, 1, 1) with
        {
            Context = _context with
            {
                FixtureSignature = " \r\n\t\0 ",
                CallerFilePath = "Password=secret; SELECT * FROM private",
            },
            DatabaseName = "db\r\n\t\0name",
        };
        var message = MssqlGeneratedDdlProvisioningDiagnostics.Format(diagnostic);
        message
            .Should()
            .Contain("fixture=ddl-hash")
            .And.Contain("database=db    name")
            .And.NotContain("Password=secret")
            .And.NotContain("SELECT * FROM private")
            .And.NotContain("\r")
            .And.NotContain("\n");
    }

    [Test]
    public void It_keeps_the_primary_failure_when_formatting_or_writing_fails()
    {
        var writer = new ThrowingWriter();
        Action act = () =>
            MssqlGeneratedDdlProvisioningDiagnostics.TryWrite(
                writer,
                () => throw new InvalidOperationException()
            );
        act.Should().NotThrow();
        MssqlGeneratedDdlProvisioningDiagnostics.TryWrite(writer, () => "safe");
        writer.Attempts.Should().Be(1);

        var primary = new InvalidOperationException("primary");
        var cleanup = new ApplicationException("cleanup");
        Action throwPrimary = () => MssqlLifecycleExceptionAggregator.Throw(primary, [cleanup]);
        throwPrimary
            .Should()
            .Throw<AggregateException>()
            .Which.InnerExceptions.Should()
            .Equal(primary, cleanup);
    }

    private string Format(int limit, int start, int timeout) =>
        MssqlGeneratedDdlProvisioningDiagnostics.Format(Diagnostic(limit, start, timeout));

    private MssqlGeneratedDdlTimeoutDiagnostic Diagnostic(int limit, int start, int timeout) =>
        new(
            _context,
            "dmsfp_probe",
            2,
            5,
            300,
            TimeSpan.FromSeconds(301.25),
            TimeSpan.FromSeconds(17),
            limit,
            start,
            timeout
        );

    private static SqlException CreateSqlException(int number, string message)
    {
        var error = (SqlError)RuntimeHelpers.GetUninitializedObject(typeof(SqlError));
        typeof(SqlError)
            .GetField("_number", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(error, number);
        typeof(SqlError)
            .GetField("_message", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(error, message);
        var errors = (SqlErrorCollection)RuntimeHelpers.GetUninitializedObject(typeof(SqlErrorCollection));
        typeof(SqlErrorCollection)
            .GetField("_errors", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(errors, new List<object> { error });
        var exception = (SqlException)RuntimeHelpers.GetUninitializedObject(typeof(SqlException));
        typeof(Exception)
            .GetField("_message", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(exception, message);
        typeof(SqlException)
            .GetField("_errors", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(exception, errors);
        return exception;
    }

    private sealed class ThrowingWriter : StringWriter
    {
        public int Attempts { get; private set; }

        public override void WriteLine(string? value)
        {
            Attempts++;
            throw new IOException("not emitted");
        }
    }
}

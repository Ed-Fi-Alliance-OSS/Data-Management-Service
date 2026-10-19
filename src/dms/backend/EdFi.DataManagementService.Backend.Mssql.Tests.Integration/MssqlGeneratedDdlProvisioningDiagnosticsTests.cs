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
[SetCulture("fr-FR")]
[Category(MssqlCiShards.Shard4)]
public class Given_MssqlGeneratedDdlProvisioningDiagnostics
{
    private MssqlProvisioningTimingContext _context = null!;
    private string _unlimitedMessage = "";
    private string _finiteMessage = "";
    private string _limitedMessage = "";
    private string[] _noOverlapMessages = [];
    private string _timeoutOverlapMessage = "";
    private string[] _normalizedMessages = [];

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _context = new("family", "ddl-hash", "backup-restore", "caller", "safe.cs", 17);
        _unlimitedMessage = Format(0, 3, 1);
        _finiteMessage = Format(2, 3, 1);
        _limitedMessage = Format(1, 1, 1);
        _noOverlapMessages = new[] { 0, 1, 2 }.Select(limit => Format(limit, 1, 1)).ToArray();
        _timeoutOverlapMessage = Format(0, 1, 2);
        _normalizedMessages = new[] { "/private/build/fixture.cs", @"C:\private\build\fixture.cs" }
            .Select(callerFilePath =>
                MssqlGeneratedDdlProvisioningDiagnostics.Format(
                    Diagnostic(0, 1, 1) with
                    {
                        Context = _context with
                        {
                            FixtureSignature = " \r\n\t\0 ",
                            CallerMemberName = "setup\r\n\t\0member",
                            CallerFilePath = callerFilePath,
                        },
                        DatabaseName = "db\r\n\t\0name",
                    }
                )
            )
            .ToArray();
    }

    [Test]
    public void It_reports_separate_counts_and_an_actionable_unlimited_remedy()
    {
        var message = _unlimitedMessage;

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
            .And.Contain("contextOrigin=safe.cs:caller:17")
            .And.Contain("MSSQL_GENERATED_DDL_PROVISION_MAX_CONCURRENCY=1")
            .And.Contain("including this operation")
            .And.Contain("excluding queued entrants")
            .And.Contain("do not prove an engine wait");
    }

    [Test]
    public void It_formats_numbers_invariantly()
    {
        var message = _finiteMessage;
        message.Should().Contain("elapsedSeconds=301.250").And.Contain("admissionWaitSeconds=17.000");
    }

    [Test]
    public void It_explains_an_already_effective_limit_of_one()
    {
        _limitedMessage
            .Should()
            .Contain("Already limited to 1 in this process")
            .And.Contain("standalone DDL reapplication")
            .And.Contain("other processes, and SQL Server waits");
        _finiteMessage.Should().Contain("restart the test process");
    }

    [Test]
    public void It_qualifies_contention_when_neither_sample_observes_overlap()
    {
        foreach (var message in _noOverlapMessages)
        {
            message
                .Should()
                .Contain("possible provisioning contention")
                .And.Contain("no overlap observed in sampled admitted operations")
                .And.NotContain("suspected provisioning contention.");
        }
    }

    [Test]
    public void It_reports_suspected_contention_when_overlap_is_observed_only_at_timeout()
    {
        _timeoutOverlapMessage
            .Should()
            .Contain("suspected provisioning contention.")
            .And.NotContain("no overlap observed");
    }

    [Test]
    public void It_normalizes_context_and_falls_back_to_ddl_identity()
    {
        foreach (var message in _normalizedMessages)
        {
            message
                .Should()
                .Contain("fixture=ddl-hash")
                .And.Contain("contextOrigin=fixture.cs:setup    member:17")
                .And.Contain("database=db    name")
                .And.NotContain("private")
                .And.NotContain("\r")
                .And.NotContain("\n");
        }
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
}

[TestFixture]
[Category(MssqlCiShards.Shard4)]
public class Given_MssqlGeneratedDdlProvisioningTimeoutClassification
{
    private bool[] _timeoutClassifications = [];
    private bool[] _otherClassifications = [];

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _timeoutClassifications =
        [
            MssqlGeneratedDdlProvisioningDiagnostics.IsProvisioningCommandTimeout(
                CreateSqlException(-2, "provider message"),
                true
            ),
            MssqlGeneratedDdlProvisioningDiagnostics.IsProvisioningCommandTimeout(
                CreateSqlException(-2, "provider message"),
                false
            ),
        ];
        Exception[] otherErrors =
        [
            CreateSqlException(1205, "deadlock"),
            CreateSqlException(547, "constraint"),
            CreateSqlException(50000, "custom"),
            new TimeoutException(),
            new OperationCanceledException(),
            new InvalidOperationException(),
        ];
        _otherClassifications = otherErrors
            .Select(exception =>
                MssqlGeneratedDdlProvisioningDiagnostics.IsProvisioningCommandTimeout(exception, true)
            )
            .ToArray();
    }

    [Test]
    public void It_classifies_only_an_admitted_SqlClient_command_timeout()
    {
        _timeoutClassifications.Should().Equal(true, false);
        _otherClassifications.Should().Equal(false, false, false, false, false, false);
    }

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
}

[TestFixture(false)]
[TestFixture(true)]
[Category(MssqlCiShards.Shard4)]
public class Given_MssqlGeneratedDdlProvisioningDiagnosticReporting(bool throwingWriter)
{
    private Exception _exception = null!;
    private string _output = "";
    private int _formatCalls;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _exception = new InvalidOperationException("original failure");
        _exception.Data["existing"] = "retained";
        using StringWriter writer = throwingWriter
            ? new MssqlGeneratedDdlProvisioningProbe.CaptureThenThrowWriter()
            : new StringWriter();
        MssqlGeneratedDdlProvisioningDiagnostics.TryReport(
            _exception,
            writer,
            () =>
            {
                _formatCalls++;
                return "provisioning diagnostic";
            }
        );
        _output = writer.ToString();
    }

    [Test]
    public void It_attaches_context_even_when_the_output_writer_fails() =>
        _exception.Data["MssqlGeneratedDdlProvisioningDiagnostic"].Should().Be("provisioning diagnostic");

    [Test]
    public void It_formats_once_and_emits_the_same_context()
    {
        _formatCalls.Should().Be(1);
        _output.Should().Be($"provisioning diagnostic{Environment.NewLine}");
    }

    [Test]
    public void It_retains_existing_exception_data() => _exception.Data["existing"].Should().Be("retained");
}

[TestFixture]
[Category(MssqlCiShards.Shard4)]
public class Given_MssqlGeneratedDdlProvisioningDiagnosticFormattingFailure
{
    private Exception _exception = null!;
    private string _output = "";

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _exception = new InvalidOperationException("original failure");
        using var writer = new StringWriter();
        MssqlGeneratedDdlProvisioningDiagnostics.TryReport(
            _exception,
            writer,
            () => throw new InvalidOperationException("formatting failed")
        );
        _output = writer.ToString();
    }

    [Test]
    public void It_makes_formatting_failures_nonfatal()
    {
        _exception.Data.Contains("MssqlGeneratedDdlProvisioningDiagnostic").Should().BeFalse();
        _output.Should().BeEmpty();
    }
}

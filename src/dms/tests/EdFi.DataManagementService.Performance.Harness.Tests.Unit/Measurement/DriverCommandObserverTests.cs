// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using EdFi.DataManagementService.Performance.Harness.Configuration;
using EdFi.DataManagementService.Performance.Harness.Measurement;
using FluentAssertions;
using Microsoft.Data.SqlClient;

namespace EdFi.DataManagementService.Performance.Harness.Tests.Unit.Measurement;

[TestFixture]
public class Given_The_Npgsql_Activity_Wiring
{
    [Test]
    public void It_records_activities_carrying_a_statement_tag()
    {
        using DriverCommandObserver observer = DriverCommandObserver.Start(PerfProvider.Postgresql);
        using ActivitySource source = new("Npgsql");

        using (Activity? activity = source.StartActivity("command"))
        {
            activity.Should().NotBeNull("the observer's listener must sample Npgsql activities");
            activity!.SetTag("db.statement", "SELECT 1");
        }

        observer.Commands.Should().ContainSingle().Which.CommandText.Should().Be("SELECT 1");
    }

    [Test]
    public void It_skips_activities_without_a_statement_tag()
    {
        using DriverCommandObserver observer = DriverCommandObserver.Start(PerfProvider.Postgresql);
        using ActivitySource source = new("Npgsql");

        using (source.StartActivity("connection-open"))
        {
            // No statement tag: this must not count as a database command.
        }

        observer.Commands.Should().BeEmpty();
    }

    [Test]
    public void It_ignores_other_activity_sources()
    {
        using DriverCommandObserver observer = DriverCommandObserver.Start(PerfProvider.Postgresql);
        using ActivitySource source = new("SomethingElse");

        using (Activity? activity = source.StartActivity("command"))
        {
            activity?.SetTag("db.statement", "SELECT 1");
        }

        observer.Commands.Should().BeEmpty();
    }

    [Test]
    public void It_stops_recording_after_dispose()
    {
        DriverCommandObserver observer = DriverCommandObserver.Start(PerfProvider.Postgresql);
        observer.Dispose();
        using ActivitySource source = new("Npgsql");

        using (Activity? activity = source.StartActivity("command"))
        {
            activity?.SetTag("db.statement", "SELECT 1");
        }

        observer.Commands.Should().BeEmpty();
    }
}

[TestFixture]
public class Given_The_SqlClient_Diagnostic_Wiring
{
    [Test]
    public void It_records_a_before_after_event_pair()
    {
        using DriverCommandObserver observer = DriverCommandObserver.Start(PerfProvider.Mssql);
        using DiagnosticListener listener = new("SqlClientDiagnosticListener");
        Guid operationId = Guid.NewGuid();
        using SqlCommand command = new("SELECT TOP 1 1 FROM [dms].[Document]");

        listener.Write(
            "Microsoft.Data.SqlClient.WriteCommandBefore",
            new { OperationId = operationId, Command = command }
        );
        listener.Write("Microsoft.Data.SqlClient.WriteCommandAfter", new { OperationId = operationId });

        ObservedDbCommand observed = observer.Commands.Should().ContainSingle().Subject;
        observed.CommandText.Should().Be("SELECT TOP 1 1 FROM [dms].[Document]");
        observed.ElapsedMs.Should().BeGreaterThanOrEqualTo(0);
    }

    [Test]
    public void It_drops_a_command_that_errors()
    {
        using DriverCommandObserver observer = DriverCommandObserver.Start(PerfProvider.Mssql);
        using DiagnosticListener listener = new("SqlClientDiagnosticListener");
        Guid operationId = Guid.NewGuid();
        using SqlCommand command = new("SELECT 1");

        listener.Write(
            "Microsoft.Data.SqlClient.WriteCommandBefore",
            new { OperationId = operationId, Command = command }
        );
        listener.Write("Microsoft.Data.SqlClient.WriteCommandError", new { OperationId = operationId });

        observer.Commands.Should().BeEmpty();
    }

    [Test]
    public void It_ignores_other_diagnostic_listeners()
    {
        using DriverCommandObserver observer = DriverCommandObserver.Start(PerfProvider.Mssql);
        using DiagnosticListener listener = new("SomeOtherListener");
        Guid operationId = Guid.NewGuid();
        using SqlCommand command = new("SELECT 1");

        listener.Write(
            "Microsoft.Data.SqlClient.WriteCommandBefore",
            new { OperationId = operationId, Command = command }
        );
        listener.Write("Microsoft.Data.SqlClient.WriteCommandAfter", new { OperationId = operationId });

        observer.Commands.Should().BeEmpty();
    }
}

// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Tests.E2E.Hooks;
using FluentAssertions;

namespace EdFi.DmsConfigurationService.Tests.E2E.Management;

/// <summary>
/// Read-only checks, run against the built test assembly before an isolated suite run, that the
/// suite will send its requests to the deployment the run selected, and clean a database on that
/// deployment's engine and host port, rather than the stock stack's defaults.
/// eng/docker-compose/tests/cms-isolation/Invoke-IsolatedConfigE2E.ps1 supplies the expectations and
/// requires both tests to have executed and passed; without them, as in an ordinary run, they are
/// ignored. Nothing here makes a request or opens a connection.
///
/// The database name is not checked: the harness builds the expected name from POSTGRES_DB_NAME, the
/// same variable the cleanup hooks read, so the check cannot tell whether the Configuration Service
/// uses another database. The engine is read from DMS_CONFIG_DATASTORE, which the harness sets from
/// the same value it builds the expectation from, and the API URL and the database port come from the
/// same variables the harness sets, the port from POSTGRES_PORT or MSSQL_PORT, exactly like the engine.
/// So every part agrees unless the assembly's hooks or context stop reading those variables, as a stale
/// build carrying the stock stack's defaults would, or something later in the run changes them. That is
/// what the check catches, and all it catches.
/// </summary>
[TestFixture]
[Category("E2ETargetPreflight")]
public class Given_a_suite_run_against_a_selected_deployment
{
    private static string Expected(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new IgnoreException($"{name} is set only by an isolated run.");

    [Test]
    public void It_sends_requests_to_the_selected_configuration_service()
    {
        string expected = Expected("CMS_E2E_PREFLIGHT_API_URL");
        PlaywrightContext context = new();

        context.ApiUrl.Should().Be(expected);
    }

    [Test]
    public void It_cleans_the_selected_database() =>
        SetupHooks.CleanupDatabaseTarget.Should().Be(Expected("CMS_E2E_PREFLIGHT_DB_TARGET"));
}

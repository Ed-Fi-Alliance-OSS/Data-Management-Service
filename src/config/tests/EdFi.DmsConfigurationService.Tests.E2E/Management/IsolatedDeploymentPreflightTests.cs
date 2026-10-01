// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Tests.E2E.Hooks;
using FluentAssertions;

namespace EdFi.DmsConfigurationService.Tests.E2E.Management;

/// <summary>
/// Read-only checks, run against the built test assembly before an isolated suite run, that the
/// suite will send its requests to and clean the database of the deployment the run selected, and
/// not the stock stack's defaults. eng/docker-compose/tests/cms-isolation/Invoke-IsolatedConfigE2E.ps1
/// supplies the expectations and requires both tests to have executed and passed; without them, as in
/// an ordinary run, they are ignored. Nothing here makes a request or opens a connection.
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

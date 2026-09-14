// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Tests.E2E.Cdc;

/// <summary>Read-only setup qualification, without feature-level database resets that destroy CDC
/// continuity. The E2E wrapper admits CDC before launching this explicitly selected fixture.</summary>
[TestFixture]
[Explicit("Requires a fresh stack admitted through E2ETest -EnableKafkaCdc.")]
[Category("CdcSetupSmoke")]
public class Given_CdcE2ESetup
{
    private HttpStatusCode _status;
    private string _body = "";

    [SetUp]
    public async Task Setup()
    {
        using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(30) };
        using var response = await client.GetAsync($"http://localhost:{AppSettings.DmsPort}/health");
        _status = response.StatusCode;
        _body = await response.Content.ReadAsStringAsync();
    }

    [Test]
    public void It_serves_the_started_DMS_health_endpoint() => _status.Should().Be(HttpStatusCode.OK);

    [Test]
    public void It_reports_a_healthy_database_connection() =>
        _body.Should().Contain("Database connection is healthy.");
}

// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Net.Http.Headers;
using EdFi.DataManagementService.Tests.Integration.Doubles;
using EdFi.DataManagementService.Tests.Integration.Fixtures;

namespace EdFi.DataManagementService.Tests.Integration;

/// <summary>
/// Per-test surface exposed to scenario static methods. Owns the HttpClient
/// (pre-configured with the smoke bearer token), a DbConnection for post-HTTP
/// assertions on the leased database, and the FixtureContext describing what
/// has been provisioned.
/// </summary>
public sealed class ApiIntegrationHarness : IAsyncDisposable
{
    public ApiIntegrationHarness(
        HttpClient httpClient,
        DbConnection dbConnection,
        FixtureContext fixture,
        ApiIntegrationQueryRecorder? queryRecorder = null
    )
    {
        HttpClient = httpClient;
        DbConnection = dbConnection;
        Fixture = fixture;
        QueryRecorder = queryRecorder;

        HttpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            ExternalDoublesConstants.SmokeToken
        );
    }

    public HttpClient HttpClient { get; }
    public DbConnection DbConnection { get; }
    public FixtureContext Fixture { get; }
    public ApiIntegrationQueryRecorder? QueryRecorder { get; }

    public async ValueTask DisposeAsync()
    {
        HttpClient.Dispose();
        await DbConnection.DisposeAsync();
    }
}

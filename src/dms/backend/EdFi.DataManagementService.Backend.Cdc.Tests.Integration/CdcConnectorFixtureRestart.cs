// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Integration;

internal sealed record CdcConnectorRestartAttempt(int Attempt, int HttpStatusCode);

internal static class CdcConnectorFixtureRestart
{
    internal static Task<HttpStatusCode> RunAsync(
        HttpClient client,
        string connectorName,
        Action<CdcConnectorRestartAttempt> observe,
        CancellationToken cancellationToken,
        TimeSpan timeout,
        TimeSpan retryDelay
    ) =>
        RunAsync(
            client,
            () =>
                new HttpRequestMessage(
                    HttpMethod.Post,
                    $"/connectors/{Uri.EscapeDataString(connectorName)}/restart?includeTasks=true&onlyFailed=false"
                ),
            observe,
            cancellationToken,
            timeout,
            retryDelay
        );

    internal static async Task<HttpStatusCode> RunAsync(
        HttpClient client,
        Func<HttpRequestMessage> createRequest,
        Action<CdcConnectorRestartAttempt> observe,
        CancellationToken cancellationToken,
        TimeSpan timeout,
        TimeSpan retryDelay,
        TimeProvider clock = null!
    )
    {
        clock ??= TimeProvider.System;
        using var timer = new CancellationTokenSource(timeout, clock);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timer.Token);
        int attempt = 0;
        while (true)
        {
            deadline.Token.ThrowIfCancellationRequested();
            attempt++;
            using var request = createRequest();
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                deadline.Token
            );
            observe(new(attempt, (int)response.StatusCode));
            if (response.StatusCode != HttpStatusCode.Conflict)
            {
                return response.StatusCode;
            }

            // A config update can trigger a rebalance. Only its explicit conflict response permits retry.
            await Task.Delay(retryDelay, clock, deadline.Token);
        }
    }
}

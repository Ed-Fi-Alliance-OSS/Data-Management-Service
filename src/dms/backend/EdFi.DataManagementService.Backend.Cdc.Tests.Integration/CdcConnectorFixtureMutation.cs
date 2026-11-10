// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text.Json;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Integration;

internal enum CdcFixtureMutation
{
    ConfigurationUpdate,
    TaskRestart,
    Stop,
}

internal static class CdcConnectorFixtureMutation
{
    private sealed class Evidence(CdcFixtureMutation operation)
    {
        public string Operation { get; } = operation.ToString();
        public string Outcome { get; set; } = "TransportFailure";
        public int TotalAttempts { get; set; }
        public List<CdcConnectorRestartAttempt> Attempts { get; } = [];
    }

    internal static async Task<HttpStatusCode> RunAsync(
        HttpClient client,
        Func<HttpRequestMessage> createRequest,
        CdcFixtureMutation operation,
        CancellationToken token,
        TimeProvider clock = null!
    )
    {
        var evidence = new Evidence(operation);
        try
        {
            var status = await CdcConnectorFixtureRestart.RunAsync(
                client,
                createRequest,
                attempt =>
                {
                    evidence.TotalAttempts = attempt.Attempt;
                    // Bound retained evidence independently of transport speed or future poll settings.
                    if (evidence.Attempts.Count == 128)
                    {
                        evidence.Attempts.RemoveAt(0);
                    }
                    evidence.Attempts.Add(attempt);
                },
                token,
                TimeSpan.FromSeconds(30),
                TimeSpan.FromMilliseconds(250),
                clock
            );
            evidence.Outcome = (int)status is >= 200 and < 300 ? "Accepted" : "Rejected";
            return status;
        }
        catch (OperationCanceledException)
        {
            evidence.Outcome = token.IsCancellationRequested ? "CallerCancelled" : "DeadlineExpired";
            throw;
        }
        finally
        {
            // No request URI, configuration, response body, or raw exception enters this evidence.
            // Persist without the operation token so cancellation cannot suppress the attachment.
            string path = Path.Combine(
                TestContext.CurrentContext.WorkDirectory,
                "cdc-controller-mutation-" + Guid.NewGuid().ToString("N") + ".json"
            );
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(evidence));
            TestContext.AddTestAttachment(path, "Connector mutation HTTP status and attempt evidence");
        }
    }
}

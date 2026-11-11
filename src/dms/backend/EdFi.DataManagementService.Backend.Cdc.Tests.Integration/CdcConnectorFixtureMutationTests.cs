// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Integration;

[TestFixture]
[NonParallelizable]
public sealed class Given_CdcConnectorFixtureMutation
{
    [TestCase("PUT", "config", true)]
    [TestCase("POST", "tasks/0/restart", false)]
    [TestCase("PUT", "stop", false)]
    public async Task It_recreates_each_mutation_and_stops_at_acceptance(
        string method,
        string suffix,
        bool content
    )
    {
        Queue<HttpStatusCode> responses = new([
            HttpStatusCode.Conflict,
            HttpStatusCode.Conflict,
            HttpStatusCode.OK,
        ]);
        List<HttpRequestMessage> requests = [];
        List<HttpContent> contents = [];
        List<string> bodies = [];
        List<CdcConnectorRestartAttempt> attempts = [];
        using var client = new HttpClient(
            new Handler(
                async (request, token) =>
                {
                    requests.Add(request);
                    request.Method.Method.Should().Be(method);
                    request.RequestUri!.AbsolutePath.Should().Be("/connectors/fixture%20connector/" + suffix);
                    if (content)
                    {
                        contents.Add(request.Content!);
                        bodies.Add(await request.Content!.ReadAsStringAsync(token));
                    }
                    return new HttpResponseMessage(responses.Dequeue());
                }
            )
        )
        {
            BaseAddress = new Uri("http://localhost"),
        };

        var status = await CdcConnectorFixtureRestart.RunAsync(
            client,
            () =>
                new HttpRequestMessage(new HttpMethod(method), "/connectors/fixture%20connector/" + suffix)
                {
                    Content = content
                        ? new StringContent(
                            "{\"setting\":\"private-value\"}",
                            Encoding.UTF8,
                            "application/json"
                        )
                        : null,
                },
            attempts.Add,
            CancellationToken.None,
            TimeSpan.FromSeconds(30),
            TimeSpan.Zero
        );

        status.Should().Be(HttpStatusCode.OK);
        requests.Should().HaveCount(3).And.OnlyHaveUniqueItems();
        attempts.Should().Equal(new CdcConnectorRestartAttempt(1, 409), new(2, 409), new(3, 200));
        if (content)
        {
            contents.Should().HaveCount(3).And.OnlyHaveUniqueItems();
            bodies.Should().Equal(Enumerable.Repeat("{\"setting\":\"private-value\"}", 3));
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task It_retains_sanitized_conflict_evidence_on_cancellation_and_deadline(bool callerCancels)
    {
        string directory = TestContext.CurrentContext.WorkDirectory;
        const string pattern = "cdc-controller-mutation-*.json";
        string[] before = Directory.GetFiles(directory, pattern);
        var clock = new FakeTimeProvider();
        using var caller = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        int requests = 0;
        var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new HttpClient(
            new Handler(
                async (_, token) =>
                {
                    requests++;
                    if (requests == 2)
                    {
                        secondEntered.TrySetResult();
                        await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    }
                    return new HttpResponseMessage(HttpStatusCode.Conflict)
                    {
                        Content = new StringContent("private-response"),
                    };
                }
            )
        )
        {
            BaseAddress = new Uri("http://localhost"),
        };
        var operation = CdcConnectorFixtureMutation.RunAsync(
            client,
            () =>
                new HttpRequestMessage(HttpMethod.Put, "/connectors/private-name/config")
                {
                    Content = new StringContent("private-configuration"),
                },
            CdcFixtureMutation.ConfigurationUpdate,
            caller.Token,
            clock
        );
        try
        {
            requests.Should().Be(1);
            operation.IsCompleted.Should().BeFalse();
            clock.Advance(TimeSpan.FromMilliseconds(250));
            await secondEntered.Task.WaitAsync(caller.Token);
            if (callerCancels)
            {
                await caller.CancelAsync();
            }
            else
            {
                clock.Advance(TimeSpan.FromSeconds(30) - TimeSpan.FromMilliseconds(250));
            }
            Func<Task> finish = () => operation;
            await finish.Should().ThrowAsync<OperationCanceledException>();
            requests.Should().Be(2);
        }
        finally
        {
            await caller.CancelAsync();
            await ((Task)operation).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
        string path = Directory.GetFiles(directory, pattern).Except(before).Should().ContainSingle().Subject;
        string json = await File.ReadAllTextAsync(path);
        json.Should().NotContain("private-");
        using var evidence = JsonDocument.Parse(json);
        evidence
            .RootElement.GetProperty("Outcome")
            .GetString()
            .Should()
            .Be(callerCancels ? "CallerCancelled" : "DeadlineExpired");
        evidence.RootElement.GetProperty("TotalAttempts").GetInt32().Should().Be(2);
        var attempt = evidence
            .RootElement.GetProperty("Attempts")
            .EnumerateArray()
            .Should()
            .ContainSingle()
            .Subject;
        attempt.GetProperty("Attempt").GetInt32().Should().Be(1);
        attempt.GetProperty("HttpStatusCode").GetInt32().Should().Be(409);
    }

    [Test]
    public async Task It_counts_a_dispatched_mutation_when_transport_fails_before_any_response()
    {
        string directory = TestContext.CurrentContext.WorkDirectory;
        const string pattern = "cdc-controller-mutation-*.json";
        string[] before = Directory.GetFiles(directory, pattern);
        int requests = 0;
        using var client = new HttpClient(
            new Handler(
                (_, _) =>
                {
                    requests++;
                    throw new HttpRequestException("private-transport-failure");
                }
            )
        )
        {
            BaseAddress = new Uri("http://localhost"),
        };

        Func<Task> operation = () =>
            CdcConnectorFixtureMutation.RunAsync(
                client,
                () => new HttpRequestMessage(HttpMethod.Put, "/connectors/private-name/stop"),
                CdcFixtureMutation.Stop,
                CancellationToken.None
            );
        await operation.Should().ThrowAsync<HttpRequestException>();

        requests.Should().Be(1);
        string path = Directory.GetFiles(directory, pattern).Except(before).Should().ContainSingle().Subject;
        string json = await File.ReadAllTextAsync(path);
        json.Should().NotContain("private-");
        using var evidence = JsonDocument.Parse(json);
        evidence.RootElement.GetProperty("Outcome").GetString().Should().Be("TransportFailure");
        evidence.RootElement.GetProperty("TotalAttempts").GetInt32().Should().Be(1);
        evidence.RootElement.GetProperty("Attempts").EnumerateArray().Should().BeEmpty();
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) => send(request, cancellationToken);
    }
}

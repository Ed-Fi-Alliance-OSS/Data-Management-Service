// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Integration;

[TestFixture]
public sealed class Given_CdcConnectorFixtureRestart
{
    private readonly List<CdcConnectorRestartAttempt> _attempts = [];
    private readonly List<string> _requests = [];

    [SetUp]
    public void Setup()
    {
        _attempts.Clear();
        _requests.Clear();
    }

    [TestCase(HttpStatusCode.Accepted)]
    [TestCase(HttpStatusCode.NoContent)]
    [TestCase(HttpStatusCode.OK)]
    public async Task It_retries_conflicts_until_the_restart_is_accepted(HttpStatusCode accepted)
    {
        Queue<HttpStatusCode> statuses = new([HttpStatusCode.Conflict, HttpStatusCode.Conflict, accepted]);
        using var client = CreateClient(_ => Task.FromResult(new HttpResponseMessage(statuses.Dequeue())));

        HttpStatusCode result = await RunAsync(client);

        result.Should().Be(accepted);
        _attempts
            .Should()
            .Equal(
                new CdcConnectorRestartAttempt(1, 409),
                new CdcConnectorRestartAttempt(2, 409),
                new CdcConnectorRestartAttempt(3, (int)accepted)
            );
        _requests
            .Should()
            .Equal(
                Enumerable.Repeat(
                    "POST /connectors/fixture%20connector/restart?includeTasks=true&onlyFailed=false",
                    3
                )
            );
    }

    [TestCase(HttpStatusCode.Accepted)]
    [TestCase(HttpStatusCode.NotFound)]
    [TestCase(HttpStatusCode.BadRequest)]
    [TestCase(HttpStatusCode.Unauthorized)]
    [TestCase(HttpStatusCode.InternalServerError)]
    public async Task It_returns_non_conflict_responses_without_retry(HttpStatusCode status)
    {
        using var client = CreateClient(_ => Task.FromResult(new HttpResponseMessage(status)));

        HttpStatusCode result = await RunAsync(client);

        result.Should().Be(status);
        _attempts.Should().Equal(new CdcConnectorRestartAttempt(1, (int)status));
        _requests.Should().HaveCount(1);
    }

    [Test]
    public void It_does_not_retry_transport_failures()
    {
        using var client = CreateClient(_ => throw new HttpRequestException("test failure"));

        Assert.ThrowsAsync<HttpRequestException>(async () => await RunAsync(client));

        _requests.Should().HaveCount(1);
        _attempts.Should().BeEmpty();
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task It_bounds_the_request_and_retry_delay_at_an_observed_boundary(bool inFlight)
    {
        var clock = new FakeTimeProvider();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = CreateClient(async token =>
        {
            entered.TrySetResult();
            if (inFlight)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            return new HttpResponseMessage(HttpStatusCode.Conflict);
        });
        Task<HttpStatusCode> operation = CdcConnectorFixtureRestart.RunAsync(
            client,
            () => new HttpRequestMessage(HttpMethod.Post, "/connectors/fixture/restart"),
            _attempts.Add,
            cancellation.Token,
            TimeSpan.FromSeconds(10),
            TimeSpan.FromMinutes(1),
            clock
        );
        try
        {
            await entered.Task.WaitAsync(cancellation.Token);
            _requests.Should().HaveCount(1);
            operation.IsCompleted.Should().BeFalse();
            clock.Advance(TimeSpan.FromSeconds(10));
            Func<Task> finish = () => operation;
            await finish.Should().ThrowAsync<OperationCanceledException>();
            _attempts.Should().HaveCount(inFlight ? 0 : 1);
            _requests.Should().HaveCount(1);
        }
        finally
        {
            await cancellation.CancelAsync();
            await ((Task)operation).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    [Test]
    public void It_honors_caller_cancellation_before_a_retry()
    {
        using var cancellation = new CancellationTokenSource();
        using var client = CreateClient(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict))
        );

        Assert.CatchAsync<OperationCanceledException>(async () =>
            await CdcConnectorFixtureRestart.RunAsync(
                client,
                "fixture connector",
                attempt =>
                {
                    _attempts.Add(attempt);
                    cancellation.Cancel();
                },
                cancellation.Token,
                TimeSpan.FromSeconds(30),
                TimeSpan.Zero
            )
        );

        _attempts.Should().Equal(new CdcConnectorRestartAttempt(1, 409));
        _requests.Should().HaveCount(1);
    }

    [Test]
    public void It_does_not_send_a_request_after_caller_cancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var client = CreateClient(_ => throw new AssertionException("No request is permitted."));

        Assert.CatchAsync<OperationCanceledException>(async () =>
            await CdcConnectorFixtureRestart.RunAsync(
                client,
                "fixture connector",
                _attempts.Add,
                cancellation.Token,
                TimeSpan.FromSeconds(30),
                TimeSpan.Zero
            )
        );

        _requests.Should().BeEmpty();
    }

    private Task<HttpStatusCode> RunAsync(HttpClient client) =>
        CdcConnectorFixtureRestart.RunAsync(
            client,
            "fixture connector",
            _attempts.Add,
            CancellationToken.None,
            TimeSpan.FromSeconds(30),
            TimeSpan.Zero
        );

    private HttpClient CreateClient(Func<CancellationToken, Task<HttpResponseMessage>> respond) =>
        new(
            new RestartHandler(
                (request, token) =>
                {
                    _requests.Add(
                        $"{request.Method} {request.RequestUri!.AbsolutePath}{request.RequestUri.Query}"
                    );
                    return respond(token);
                }
            )
        )
        {
            BaseAddress = new Uri("http://localhost"),
        };

    private sealed class RestartHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond
    ) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) => respond(request, cancellationToken);
    }
}

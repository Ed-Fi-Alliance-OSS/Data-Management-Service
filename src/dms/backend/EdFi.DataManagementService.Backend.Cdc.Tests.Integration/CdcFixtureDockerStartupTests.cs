// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Integration;

[TestFixture]
public sealed class Given_CdcFixtureDockerStartup
{
    [TestCase("recover", 2, 1)]
    [TestCase("exhaust", 3, 2)]
    [TestCase("unrelated", 1, 0)]
    [TestCase("cancel", 1, 1)]
    [TestCase("cleanup", 1, 1)]
    public async Task It_recovers_only_explicit_bind_failures_and_owns_cleanup(
        string scenario,
        int starts,
        int removes
    )
    {
        using var reservations = new CdcFixturePorts(5);
        using var cancellation = new CancellationTokenSource();
        List<TcpListener> competitors = [];
        List<int> attemptedPorts = [];
        List<string> calls = [];
        var cleanupFailure = new InvalidOperationException("cleanup failed");
        Exception failure = null!;
        try
        {
            await CdcFixtureDockerStartup.RunAsync(
                token =>
                {
                    calls.Add("start");
                    token.Should().Be(cancellation.Token);
                    attemptedPorts.Add(reservations.Ports[0]);
                    reservations.Release(0);
                    var competitor = new TcpListener(IPAddress.Loopback, reservations.Ports[0]);
                    competitors.Add(competitor);
                    competitor.Start();
                    if (scenario == "cancel")
                    {
                        cancellation.Cancel();
                    }
                    return Task.FromResult(
                        new DockerCommandResult(
                            scenario == "recover" && attemptedPorts.Count == 2 ? 0 : 125,
                            "private output",
                            scenario == "unrelated" ? "invalid mount config" : "bind: address already in use"
                        )
                    );
                },
                token =>
                {
                    calls.Add("remove");
                    token.CanBeCanceled.Should().BeTrue();
                    token.IsCancellationRequested.Should().BeFalse();
                    return scenario == "cleanup" ? Task.FromException(cleanupFailure) : Task.CompletedTask;
                },
                () => reservations.Replace(0),
                cancellation.Token
            );
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            foreach (var competitor in competitors)
            {
                competitor.Dispose();
            }
        }
        attemptedPorts.Should().HaveCount(starts).And.OnlyHaveUniqueItems();
        calls.Count(call => call == "remove").Should().Be(removes);
        calls
            .Should()
            .Equal(
                Enumerable.Range(0, starts + removes).Select(index => index % 2 == 0 ? "start" : "remove")
            );
        switch (scenario)
        {
            case "recover":
                failure.Should().BeNull();
                break;
            case "cancel":
                failure.Should().BeAssignableTo<OperationCanceledException>();
                break;
            case "cleanup":
                failure.Should().BeSameAs(cleanupFailure);
                break;
            default:
                failure.Should().BeOfType<InvalidOperationException>();
                failure.Message.Should().Contain("exitCode=125").And.NotContain("private output");
                break;
        }
        // Retained worker endpoints stay reserved throughout every attempt and failure path.
        foreach (int port in reservations.Ports[1..])
        {
            using var listener = new TcpListener(IPAddress.Loopback, port);
            Action bind = () => listener.Start();
            bind.Should().Throw<SocketException>();
        }
    }
}

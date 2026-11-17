// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Net.Sockets;
using FluentAssertions;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Integration;

[TestFixture(false)]
[TestFixture(true)]
public sealed class Given_CdcKafkaPolicyFixtureWorkerStartup(bool secured)
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task It_recovers_a_stolen_worker_port_and_refreshes_requests_while_the_competitor_stays_bound(
        bool metrics
    )
    {
        using var docker = new CompetingDocker(metrics);
        await using var fixture = new CdcKafkaPolicyFixture(secured, docker);
        var prepared = fixture.Request();
        AssertReserved(prepared.ConnectEndpoint.Port);
        AssertReserved(prepared.WorkerMetricsEndpoint.Port);
        await fixture.StartWorkerContainerAsync(CancellationToken.None);
        var refreshed = fixture.RefreshWorkerEndpoints(prepared);
        docker.Attempts.Should().HaveCount(2);
        docker.Removes.Should().Be(1);
        docker.Attempts[0].Should().Equal(prepared.ConnectEndpoint.Port, prepared.WorkerMetricsEndpoint.Port);
        docker
            .Attempts[1]
            .Should()
            .Equal(refreshed.ConnectEndpoint.Port, refreshed.WorkerMetricsEndpoint.Port);
        int stolen = metrics ? prepared.WorkerMetricsEndpoint.Port : prepared.ConnectEndpoint.Port;
        docker.Attempts[1].Should().NotContain(stolen);
        docker.Attempts[1].Should().OnlyHaveUniqueItems();
        AssertReserved(stolen);
        refreshed.Binding.Should().BeSameAs(prepared.Binding);
        refreshed.WorkerPolicy.Should().BeSameAs(prepared.WorkerPolicy);
        refreshed.ConnectorPolicy.Should().BeSameAs(prepared.ConnectorPolicy);
    }

    private static void AssertReserved(int port)
    {
        using var listener = new TcpListener(IPAddress.Loopback, port);
        Action bind = () => listener.Start();
        bind.Should().Throw<SocketException>();
    }

    private sealed class CompetingDocker(bool metrics) : IDockerCli, IDisposable
    {
        private TcpListener _competitor = null!;
        public List<int[]> Attempts { get; } = [];
        public int Removes { get; private set; }
        public bool IsOffline => false;

        public Task RequireDockerAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<DockerCommandResult> RunAsync(
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken
        ) => RunAllowingFailureAsync(arguments, cancellationToken);

        public Task<DockerCommandResult> RunAllowingFailureAsync(
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (arguments[0] == "rm")
            {
                Removes++;
                return Task.FromResult(new DockerCommandResult(0, "", ""));
            }
            arguments[0].Should().Be("run");
            int[] ports = arguments
                .Where((_, index) => index > 0 && arguments[index - 1] == "-p")
                .Select(mapping => int.Parse(mapping.Split(':')[1]))
                .ToArray();
            Attempts.Add(ports);
            if (Attempts.Count == 1)
            {
                _competitor = new(IPAddress.Loopback, ports[metrics ? 1 : 0]);
                _competitor.Start();
            }
            List<TcpListener> bindings = [];
            try
            {
                foreach (int port in ports)
                {
                    var listener = new TcpListener(IPAddress.Loopback, port);
                    bindings.Add(listener);
                    listener.Start();
                }
                return Task.FromResult(new DockerCommandResult(0, "", ""));
            }
            catch (SocketException)
            {
                return Task.FromResult(new DockerCommandResult(125, "", "bind: address already in use"));
            }
            finally
            {
                foreach (var listener in bindings)
                {
                    listener.Dispose();
                }
            }
        }

        public void Dispose() => _competitor?.Dispose();
    }
}

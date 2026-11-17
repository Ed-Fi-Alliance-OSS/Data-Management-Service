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
public sealed class Given_CdcFixturePorts
{
    [TestCase(3)]
    [TestCase(5)]
    public void It_owns_distinct_ports_until_each_service_binds(int count)
    {
        using var reservations = new CdcFixturePorts(count);
        reservations.Ports.Should().OnlyHaveUniqueItems();
        for (int index = 0; index < count; index++)
        {
            using var competitor = new TcpListener(IPAddress.Loopback, reservations.Ports[index]);
            Action bind = () => competitor.Start();
            bind.Should().Throw<SocketException>();
            reservations.Release(index);
            bind.Should().NotThrow();
        }
    }

    [Test]
    public void It_replaces_a_stolen_port_without_releasing_other_reservations()
    {
        using var reservations = new CdcFixturePorts(5);
        int original = reservations.Ports[0];
        reservations.Release(0);
        using var competitor = new TcpListener(IPAddress.Loopback, original);
        competitor.Start();
        reservations.Replace(0);
        reservations.Ports[0].Should().NotBe(original);
        reservations.Ports.Should().OnlyHaveUniqueItems();
        foreach (int port in reservations.Ports)
        {
            using var listener = new TcpListener(IPAddress.Loopback, port);
            Action bind = () => listener.Start();
            bind.Should().Throw<SocketException>();
        }
    }

    [Test]
    public void It_releases_all_remaining_ports_when_startup_is_abandoned()
    {
        int[] ports;
        using (var reservations = new CdcFixturePorts(5))
        {
            reservations.Release(0);
            ports = reservations.Ports;
        }
        foreach (int port in ports)
        {
            using var listener = new TcpListener(IPAddress.Loopback, port);
            Action bind = () => listener.Start();
            bind.Should().NotThrow();
        }
    }
}

// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Net.Sockets;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Integration;

/// <summary>Own every endpoint together until its service is ready to bind.</summary>
internal sealed class CdcFixturePorts : IDisposable
{
    private readonly List<TcpListener> _listeners = [];
    internal int[] Ports { get; }

    internal CdcFixturePorts(int count)
    {
        Ports = new int[count];
        try
        {
            for (int index = 0; index < count; index++)
            {
                var listener = new TcpListener(IPAddress.Loopback, 0);
                _listeners.Add(listener);
                listener.Start();
                Ports[index] = ((IPEndPoint)listener.LocalEndpoint).Port;
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal void Release(int index) => _listeners[index].Stop();

    internal void Replace(int index)
    {
        // Keep the old reservation until its replacement is owned, too.
        var replacement = new TcpListener(IPAddress.Loopback, 0);
        try
        {
            replacement.Start();
        }
        catch
        {
            replacement.Dispose();
            throw;
        }
        _listeners[index].Dispose();
        _listeners[index] = replacement;
        Ports[index] = ((IPEndPoint)replacement.LocalEndpoint).Port;
    }

    public void Dispose()
    {
        foreach (var listener in _listeners)
        {
            listener.Dispose();
        }
    }
}

// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Net.Sockets;
using System.Threading.RateLimiting;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Configuration;
using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;

/// <summary>
/// The two independent <c>/connect/*</c> budgets (DMS-1482 D2): one per source address and one per
/// <c>client_id</c> across all sources. A request must be admitted by every budget that applies to it.
/// Each budget is a fixed window with no queue, so a rejection is immediate; enforcement is per process.
/// Keys come from the transport address and the parsed <c>client_id</c> only, never from a lookup, so a
/// rejection says nothing about whether a client exists. A singleton; the container disposes it.
/// </summary>
public sealed class ConnectRateLimiter : IDisposable
{
    /// <summary>The one partition for requests whose remote address is unknown; never exempt.</summary>
    internal const string UnknownSourceKey = "unknown";

    /// <summary>Longer client ids are truncated for keying only, which can only merge partitions.</summary>
    internal const int MaxClientKeyLength = 256;

    private readonly PartitionedRateLimiter<string> _sourceLimiter;
    private readonly PartitionedRateLimiter<string> _clientLimiter;

    public ConnectRateLimiter(IOptions<ConnectRateLimitSettings> options)
    {
        ConnectRateLimitSettings settings = options.Value;
        _sourceLimiter = CreateLimiter(settings, settings.SourcePermitLimit, StringComparer.Ordinal);

        // Case-insensitive, matching RegisterClient.IsUnique and the MSSQL client lookup, so case variants
        // of one client_id cannot buy fresh budgets (reference/adr-client-id-casing.md).
        _clientLimiter = CreateLimiter(
            settings,
            settings.ClientPermitLimit,
            StringComparer.OrdinalIgnoreCase
        );
    }

    public RateLimitLease AttemptSource(HttpContext context) =>
        _sourceLimiter.AttemptAcquire(SourcePartitionKey(context.Connection.RemoteIpAddress));

    public RateLimitLease AttemptClient(string clientId) =>
        _clientLimiter.AttemptAcquire(ClientPartitionKey(clientId));

    public void Dispose()
    {
        _sourceLimiter.Dispose();
        _clientLimiter.Dispose();
    }

    /// <summary>
    /// The address after <c>UseForwardedHeaders</c>, so only a trusted proxy can change it. A native IPv6
    /// address is keyed on its /64, because a subscriber normally holds a whole /64 and could otherwise
    /// rotate addresses for fresh budgets (D6).
    /// </summary>
    internal static string SourcePartitionKey(IPAddress? address)
    {
        if (address is null)
        {
            return UnknownSourceKey;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            return address.MapToIPv4().ToString();
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            byte[] bytes = address.GetAddressBytes();
            Array.Clear(bytes, 8, 8);
            return $"{new IPAddress(bytes)}/64";
        }

        return address.ToString();
    }

    internal static string ClientPartitionKey(string clientId) =>
        clientId.Length > MaxClientKeyLength ? clientId[..MaxClientKeyLength] : clientId;

    private static PartitionedRateLimiter<string> CreateLimiter(
        ConnectRateLimitSettings settings,
        int permitLimit,
        IEqualityComparer<string> comparer
    ) =>
        PartitionedRateLimiter.Create<string, string>(
            key =>
                settings.Enabled
                    ? RateLimitPartition.GetFixedWindowLimiter(
                        key,
                        _ => new FixedWindowRateLimiterOptions
                        {
                            PermitLimit = permitLimit,
                            Window = TimeSpan.FromSeconds(settings.WindowSeconds),
                            QueueLimit = 0,
                            AutoReplenishment = true,
                        }
                    )
                    : RateLimitPartition.GetNoLimiter(key),
            comparer
        );
}

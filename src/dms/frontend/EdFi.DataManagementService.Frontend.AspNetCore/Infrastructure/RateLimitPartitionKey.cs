// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Net.Sockets;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Infrastructure;

/// <summary>
/// What a global rate-limit partition is keyed on.
/// </summary>
internal enum RateLimitPartitionKind
{
    /// <summary>
    /// The validated <c>client_id</c> of a bearer token that Core's JWT validation accepts, compared
    /// exactly and ordinally.
    /// </summary>
    Client,

    /// <summary>A normalized connection address: IPv4, or the /64 prefix of a native IPv6 address.</summary>
    Address,

    /// <summary>
    /// Requests with no connection address. They share one partition, which is never exempt.
    /// </summary>
    UnknownAddress,
}

/// <summary>
/// The key of one global rate-limit partition. The kind takes part in equality, so keys of different
/// kinds never collide whatever their values.
/// </summary>
internal readonly record struct RateLimitPartitionKey(RateLimitPartitionKind Kind, string Value)
{
    private const int Ipv6AddressLength = 16;
    private const int Ipv6PrefixLength = 8;

    private static readonly RateLimitPartitionKey _unknownAddress = new(
        RateLimitPartitionKind.UnknownAddress,
        string.Empty
    );

    /// <summary>
    /// Keys a request by the client its bearer token proves. Only a <c>client_id</c> that Core's JWT
    /// validation returned may be passed here; nothing read from the request itself is a client key.
    /// </summary>
    public static RateLimitPartitionKey ForClient(string clientId) =>
        new(RateLimitPartitionKind.Client, clientId);

    /// <summary>
    /// Keys a request by its connection address, read after forwarded headers from trusted proxies
    /// have been applied. An IPv4-mapped IPv6 address is keyed as its IPv4 address, so one client is
    /// one partition on a dual-stack socket. A native IPv6 address is keyed on its /64 prefix, with
    /// the scope ID dropped, because a single host can rotate freely through the interface identifiers
    /// of its subnet.
    /// </summary>
    public static RateLimitPartitionKey ForAddress(IPAddress? address)
    {
        if (address is null)
        {
            return _unknownAddress;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return new(RateLimitPartitionKind.Address, address.ToString());
        }

        Span<byte> bytes = stackalloc byte[Ipv6AddressLength];
        address.TryWriteBytes(bytes, out _);
        bytes[Ipv6PrefixLength..].Clear();
        return new(RateLimitPartitionKind.Address, new IPAddress(bytes).ToString());
    }
}

/// <summary>
/// Carries the partition key that <see cref="GlobalRateLimitingMiddleware"/> resolved for a request to
/// the global limiter's partitioner, which is synchronous and so cannot validate a token itself.
/// </summary>
internal sealed record RateLimitPartitionFeature(RateLimitPartitionKey Key);

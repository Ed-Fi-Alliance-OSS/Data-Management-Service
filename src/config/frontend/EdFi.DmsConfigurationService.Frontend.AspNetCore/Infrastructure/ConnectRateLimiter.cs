// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Threading.RateLimiting;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Configuration;
using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;

/// <summary>
/// The two independent <c>/connect/*</c> budgets: one per source address and one per
/// <c>client_id</c> across all sources. A request must be admitted by every budget that applies to it.
/// Each budget is a fixed window with no queue, so a rejection is immediate; enforcement is per process.
/// Keys come from the transport address and the parsed <c>client_id</c> only, never from a lookup, so a
/// rejection says nothing about whether a client exists. A singleton; the container disposes it.
/// </summary>
public sealed class ConnectRateLimiter : IDisposable
{
    /// <summary>The one partition for requests whose remote address is unknown; never exempt.</summary>
    internal const string UnknownSourceKey = "unknown";

    /// <summary>Bound the encoded linguistic key only after equivalence is established.</summary>
    internal const int MaxClientKeyLength = 256;

    private const CompareOptions ClientComparison =
        CompareOptions.IgnoreCase
        | CompareOptions.IgnoreNonSpace
        | CompareOptions.IgnoreWidth
        | CompareOptions.IgnoreKanaType;

    private readonly PartitionedRateLimiter<string> _sourceLimiter;
    private readonly PartitionedRateLimiter<string> _clientLimiter;

    public ConnectRateLimiter(IOptions<ConnectRateLimitSettings> options)
    {
        ConnectRateLimitSettings settings = options.Value;
        _sourceLimiter = CreateLimiter(settings, settings.SourcePermitLimit, StringComparer.Ordinal);

        // SQL Server's linguistic lookups can ignore more than case. Conservatively merge width,
        // accent and kana aliases for counting on every provider, without changing authentication.
        // This is not an emulation of every locale-specific SQL collation.
        _clientLimiter = CreateLimiter(settings, settings.ClientPermitLimit, StringComparer.Ordinal);
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
    /// rotate addresses for fresh budgets.
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

    internal static string ClientPartitionKey(string clientId)
    {
        CompareInfo comparison = CultureInfo.InvariantCulture.CompareInfo;
        int length = clientId.Length;
        while (length > 0)
        {
            int characterLength = length > 1 && char.IsSurrogatePair(clientId, length - 2) ? 2 : 1;
            ReadOnlySpan<char> last = clientId.AsSpan(length - characterLength, characterLength);
            // SQL pads with spaces. Also strip ignored suffix characters so adding a zero-weight
            // character after a space cannot hide that padding from trimming.
            if (
                comparison.Compare(last, " ", ClientComparison) is not 0
                && comparison.Compare(last, ReadOnlySpan<char>.Empty, ClientComparison) is not 0
            )
            {
                break;
            }
            length -= characterLength;
        }

        // Canonicalize the entire ID before bounding retained state: truncating the input first
        // would split equivalent IDs when an attacker inserts ignored characters before the bound.
        // Sort keys handle malformed UTF-16 without the exceptions Unicode normalization can raise.
        string key = Convert.ToBase64String(
            comparison.GetSortKey(clientId[..length], ClientComparison).KeyData
        );
        return key.Length > MaxClientKeyLength ? key[..MaxClientKeyLength] : key;
    }

    internal static PartitionedRateLimiter<string> CreateLimiter(
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
                    // No-op partitions never expire. Reuse one key so disabled mode does not retain
                    // state proportional to the number of source addresses or client ids seen.
                    : RateLimitPartition.GetNoLimiter(string.Empty),
            comparer
        );
}

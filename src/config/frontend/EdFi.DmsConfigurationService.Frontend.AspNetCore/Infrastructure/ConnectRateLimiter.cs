// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading.RateLimiting;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Configuration;
using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;

/// <summary>
/// The two independent <c>/connect/*</c> budgets: one per source address and one per
/// source and <c>client_id</c> pair. A request must be admitted by every budget that applies to it.
/// Each budget is a fixed window with no queue, so a rejection is immediate; enforcement is per process.
/// Keys come from the transport address and the parsed <c>client_id</c> only, never from a lookup, so a
/// rejection says nothing about whether a client exists. A singleton; the container disposes it.
/// </summary>
public sealed class ConnectRateLimiter : IDisposable
{
    /// <summary>The one partition for requests whose remote address is unknown; never exempt.</summary>
    internal const string UnknownSourceKey = "unknown";

    private const CompareOptions ClientComparison =
        CompareOptions.IgnoreCase
        | CompareOptions.IgnoreNonSpace
        | CompareOptions.IgnoreWidth
        | CompareOptions.IgnoreKanaType;

    private readonly PartitionedRateLimiter<string> _sourceLimiter;
    private readonly PartitionedRateLimiter<string> _clientLimiter;
    private readonly bool _enabled;

    public ConnectRateLimiter(IOptions<ConnectRateLimitSettings> options)
    {
        ConnectRateLimitSettings settings = options.Value;
        _enabled = settings.Enabled;
        _sourceLimiter = CreateLimiter(settings, settings.SourcePermitLimit);
        _clientLimiter = CreateLimiter(settings, settings.ClientPermitLimit);
    }

    public RateLimitLease AttemptSource(HttpContext context) =>
        _sourceLimiter.AttemptAcquire(
            _enabled ? SourcePartitionKey(context.Connection.RemoteIpAddress) : string.Empty
        );

    public RateLimitLease AttemptClient(HttpContext context, string clientId) =>
        // Neither source keys nor base64 client keys contain '|', so pairs cannot collide. Use the
        // same trusted source identity as the source gate; public attempts cannot spend a different
        // source's private refresh allowance just by naming its client ID.
        _clientLimiter.AttemptAcquire(
            _enabled
                ? $"{SourcePartitionKey(context.Connection.RemoteIpAddress)}|{ClientPartitionKey(clientId)}"
                : string.Empty
        );

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
        // Handlers validate client ID length before admission. SQL Server's linguistic lookups can
        // ignore more than case: conservatively merge width, accent and kana aliases on every provider
        // without changing authentication. This does not emulate every locale-specific SQL collation.
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

        // Hash the entire canonical key: prefix truncation merges distinct long IDs. The digest
        // bounds retained partition state independently of linguistic sort-key expansion, while the
        // handler's input length limit bounds the temporary allocation and comparison work.
        // Sort keys handle malformed UTF-16 without the exceptions Unicode normalization can raise.
        return Convert.ToBase64String(
            SHA256.HashData(comparison.GetSortKey(clientId[..length], ClientComparison).KeyData)
        );
    }

    internal static PartitionedRateLimiter<string> CreateLimiter(
        ConnectRateLimitSettings settings,
        int permitLimit
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
            StringComparer.Ordinal
        );
}

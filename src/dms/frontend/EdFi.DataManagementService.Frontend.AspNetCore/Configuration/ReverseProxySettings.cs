// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using Microsoft.Extensions.Options;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Configuration;

/// <summary>
/// Trusted reverse-proxy sources used to populate ASP.NET Core ForwardedHeadersOptions
/// when UseForwardedHeaders is true. Forwarded headers are only honored when the immediate peer
/// matches one of these proxies or networks.
/// </summary>
public class ReverseProxySettings
{
    /// <summary>
    /// When true, X-Forwarded-* headers are honored from the trusted sources below.
    /// When false (default), forwarded headers are ignored entirely.
    /// </summary>
    public bool UseForwardedHeaders { get; set; }

    /// <summary>
    /// Comma-separated list of exact trusted proxy IP addresses (IPv4 or IPv6).
    /// Example: "10.0.0.5,10.0.0.6,::1".
    /// </summary>
    public string KnownProxies { get; set; } = string.Empty;

    /// <summary>
    /// Comma-separated list of trusted proxy networks in CIDR notation.
    /// Example: "10.0.0.0/8,172.16.0.0/12".
    /// </summary>
    public string KnownNetworks { get; set; } = string.Empty;

    public string[] GetKnownProxies() => Split(KnownProxies);

    public string[] GetKnownNetworks() => Split(KnownNetworks);

    private static string[] Split(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

public class ReverseProxySettingsValidator : IValidateOptions<ReverseProxySettings>
{
    public ValidateOptionsResult Validate(string? name, ReverseProxySettings options)
    {
        foreach (string proxy in options.GetKnownProxies())
        {
            if (!IPAddress.TryParse(proxy, out _))
            {
                return ValidateOptionsResult.Fail(
                    $"AppSettings:ReverseProxy:KnownProxies contains an invalid IP address: '{proxy}'"
                );
            }
        }

        foreach (string network in options.GetKnownNetworks())
        {
            if (!System.Net.IPNetwork.TryParse(network, out _))
            {
                return ValidateOptionsResult.Fail(
                    $"AppSettings:ReverseProxy:KnownNetworks contains an invalid CIDR network: '{network}'. "
                        + "Expected CIDR notation, for example '10.0.0.0/8' or '2001:db8::/32'."
                );
            }
        }

        return ValidateOptionsResult.Success;
    }
}

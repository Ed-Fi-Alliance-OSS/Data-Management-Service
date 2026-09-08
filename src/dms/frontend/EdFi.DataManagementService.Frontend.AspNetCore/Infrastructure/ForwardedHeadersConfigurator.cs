// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using EdFi.DataManagementService.Frontend.AspNetCore.Configuration;
using Microsoft.AspNetCore.HttpOverrides;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Infrastructure;

/// <summary>
/// Configures ForwardedHeadersOptions to honor X-Forwarded-* headers only from explicitly
/// trusted reverse-proxy sources. The framework defaults (loopback) are intentionally left
/// in place, so an enabled-but-unconfigured deployment trusts loopback only rather than every
/// source. Invalid entries are skipped here; ReverseProxySettingsValidator is the authority
/// that fails startup with a clear message on malformed configuration.
/// </summary>
public static class ForwardedHeadersConfigurator
{
    public static void Configure(ForwardedHeadersOptions options, ReverseProxySettings settings)
    {
        options.ForwardedHeaders =
            ForwardedHeaders.XForwardedFor
            | ForwardedHeaders.XForwardedHost
            | ForwardedHeaders.XForwardedProto;

        foreach (string proxy in settings.GetKnownProxies())
        {
            if (IPAddress.TryParse(proxy, out IPAddress? address))
            {
                options.KnownProxies.Add(address);
            }
        }

        foreach (string network in settings.GetKnownNetworks())
        {
            if (System.Net.IPNetwork.TryParse(network, out System.Net.IPNetwork parsed))
            {
                options.KnownIPNetworks.Add(parsed);
            }
        }
    }
}

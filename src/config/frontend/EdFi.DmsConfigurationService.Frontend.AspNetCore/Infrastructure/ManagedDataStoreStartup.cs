// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.ManagedDataStores;
using EdFi.DmsConfigurationService.Backend.Services;
using EdFi.DmsConfigurationService.Secrets;
using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;

/// <summary>Static checks only. Environment/secret availability remains the provisioner's latched readiness verdict.</summary>
public static class ManagedDataStoreStartup
{
    public static void Validate(IServiceProvider services)
    {
        if (!services.GetRequiredService<DataStoreManagementOptions>().EnableDataStoreManagement)
        {
            return;
        }
        var provisioning = services.GetRequiredService<IOptions<DmsDataStoreProvisioningOptions>>().Value;
        if (!provisioning.Enabled)
        {
            Fail(
                "AppSettings:EnableDataStoreManagement requires DmsDataStoreProvisioning:Enabled=true. Set management explicitly false for an unconfigured deployment."
            );
        }
        // Also validates that the ordinary CMS catalog names a database we can protect.
        _ = services.GetRequiredService<ProtectedDatabaseNames>();
        var provider = services.GetRequiredService<IManagedDataStoreRuntimeConnectionProvider>();
        var admin = ValidateConnection(
            provisioning.AdminConnectionString,
            "DmsDataStoreProvisioning:AdminConnectionString",
            provider,
            services
        );
        var runtime = ValidateConnection(
            services
                .GetRequiredService<IOptions<ManagedDataStoreRuntimeOptions>>()
                .Value.RuntimeConnectionString,
            "DataStoreManagement:RuntimeConnectionString",
            provider,
            services
        );
        // Only components both settings make known without resolving a secret are compared. The rest is
        // left to the operation-time guard, which compares the resolved runtime snapshot with the persisted
        // administrative endpoint.
        if (admin is not null && runtime is not null && admin.ProvablyDiffersFrom(runtime))
        {
            Fail(
                "DmsDataStoreProvisioning:AdminConnectionString and DataStoreManagement:RuntimeConnectionString must name the same canonical target endpoint (host and port, or named instance). Credentials and database names may differ."
            );
        }
    }

    /// <summary>
    /// The statically known destination. When <paramref name="PortOrInstanceKnown"/> is false, the effective
    /// port or named instance depends on a secret and the endpoint carries only a parser representative for it.
    /// Its literal separator still fixes whether it is a port or a named instance. When
    /// <paramref name="HostKnown"/> is false, the host depends on a secret; the literal separator after it still
    /// fixes the addressing kind, and a literal port or named instance after that separator is known.
    /// </summary>
    private sealed record KnownEndpoint(
        ManagedDataStoreEndpoint Endpoint,
        bool PortOrInstanceKnown,
        bool HostKnown = true
    )
    {
        // Each component is compared only when both sides know it; representatives are never real values.
        // Port and named-instance addressing never canonicalize to the same endpoint.
        public bool ProvablyDiffersFrom(KnownEndpoint other) =>
            (Endpoint.Instance is null) != (other.Endpoint.Instance is null)
            || (
                HostKnown
                && other.HostKnown
                && WithoutPortOrInstance(Endpoint) != WithoutPortOrInstance(other.Endpoint)
            )
            || (
                PortOrInstanceKnown
                && other.PortOrInstanceKnown
                && (Endpoint.Port, Endpoint.Instance) != (other.Endpoint.Port, other.Endpoint.Instance)
            );

        private static ManagedDataStoreEndpoint WithoutPortOrInstance(ManagedDataStoreEndpoint endpoint) =>
            endpoint with
            {
                Port = null,
                Instance = null,
            };
    }

    /// <returns>
    /// The known endpoint, or null when the effective host depends on a secret and no literal separator fixes
    /// port or named-instance addressing.
    /// </returns>
    private static KnownEndpoint? ValidateConnection(
        string? value,
        string key,
        IManagedDataStoreRuntimeConnectionProvider provider,
        IServiceProvider services
    )
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            Fail($"{key} is required when managed data stores are enabled.");
        }
        bool hasReferences = SecretReferenceTokens.Find(value).Count != 0;
        if (hasReferences && services.GetService<ISecretResolver>() is null)
        {
            Fail($"{key} requires an ISecretResolver for secret references.");
        }
        try
        {
            bool routingUnknown = false;
            bool portUnknown = false;
            bool suffixUnknown = false;
            string locator = string.Empty;
            // References occur in individual values. Do not resolve them at this static boundary or
            // parse a token in a typed field (e.g. Port) with a driver before substitution.
            if (hasReferences)
            {
                var configured = new DbConnectionStringBuilder { ConnectionString = value };
                var literalFields = provider.CreateBuilder(string.Empty);
                var defaults = provider.CreateBuilder(string.Empty);
                foreach (string field in configured.Keys)
                {
                    // The native getter validates keyword aliases. String templates are retained.
                    // A typed secret value uses its native default only for this static check.
                    object? defaultValue = defaults[field];
                    bool secret = SecretReferenceTokens.Find((string)configured[field]).Count != 0;
                    bool portField =
                        provider.Provider == "postgresql"
                        && field.Equals("Port", StringComparison.OrdinalIgnoreCase);
                    if (portField)
                    {
                        // The last assignment decides whether the effective Port is known.
                        portUnknown = secret;
                    }
                    if (!secret || defaultValue is null or string)
                    {
                        literalFields[field] = configured[field];
                        continue;
                    }
                    // A later alias must replace an earlier literal assignment as the driver would.
                    literalFields[field] = defaultValue;
                    if (portField)
                    {
                        string port = ((string)configured[field]).Trim();
                        _ = StaticPort(MaskReferences(port.StartsWith('+') ? port[1..] : port));
                    }
                    else if (provider.Provider == "mssql")
                    {
                        string? allowed = field
                            .Replace(" ", string.Empty, StringComparison.Ordinal)
                            .ToLowerInvariant() switch
                        {
                            "multisubnetfailover" or "userinstance" => "false",
                            "applicationintent" => "ReadWrite",
                            _ => null,
                        };
                        string template = ((string)configured[field]).Trim();
                        if (
                            allowed is not null
                            && !CouldResolveTo(template, allowed)
                            && !(allowed == "false" && CouldResolveTo(template, "no"))
                        )
                        {
                            throw new ArgumentException("Unsupported literal routing option.");
                        }
                    }
                }
                string routingField = provider.Provider == "postgresql" ? "Host" : "Data Source";
                locator = (string?)literalFields[routingField] ?? string.Empty;
                // The native builder has applied aliases and last-assignment precedence to this template.
                routingUnknown = SecretReferenceTokens.Find(locator).Count != 0;
                if (routingUnknown && HasLiteralServer(locator, provider.Provider == "postgresql"))
                {
                    // Only the port or instance after a literal host and its separator is unknown. For
                    // PostgreSQL this inline port also overrides any Port assignment.
                    routingUnknown = false;
                    suffixUnknown = true;
                }
                literalFields[routingField] = StaticLocator(locator, provider.Provider == "postgresql");
                value = literalFields.ConnectionString;
            }
            if (!provider.TryParse(value, out var endpoint))
            {
                Fail($"{key} must name one verifiable endpoint for the configured target provider.");
            }
            if (routingUnknown)
            {
                // A PostgreSQL host can still supply an inline port or a socket, and a wholly secret locator any
                // routing syntax.
                return
                    provider.Provider == "mssql"
                    && HasLiteralSqlServerSeparator(locator, out bool suffixKnown)
                    ? new(endpoint, PortOrInstanceKnown: suffixKnown, HostKnown: false)
                    : null;
            }
            if (suffixUnknown)
            {
                return new(endpoint, PortOrInstanceKnown: false);
            }
            if (portUnknown)
            {
                // A literal inline host port overrides Port; otherwise the representative port decided it,
                // and only the literal host is known.
                var alternative = provider.CreateBuilder(value);
                alternative["Port"] = endpoint.Port == 1 ? 2 : 1;
                return new(
                    endpoint,
                    provider.TryParse(alternative.ConnectionString, out var other) && other == endpoint
                );
            }
            return new(endpoint, PortOrInstanceKnown: true);
        }
        catch (Exception exception)
            when (exception is ArgumentException or FormatException or OverflowException)
        {
            // Driver parser messages may repeat credentials; retain no exception or configured value.
            Fail($"{key} must be a valid connection string for the configured target provider.");
            return null;
        }
    }

    // These representatives are used ONLY by the pure endpoint parser. They never reach a
    // connection, readiness verdict, reservation or persisted descriptor. Literal components
    // retain their exact values, including unsupported flags and malformed/missing endpoints.
    private const char Unknown = '\u001f';

    private static string MaskReferences(string value)
    {
        foreach (var token in SecretReferenceTokens.Find(value).Reverse())
        {
            value = value.Remove(token.Index, token.Length).Insert(token.Index, Unknown.ToString());
        }
        return value;
    }

    private static bool CouldResolveTo(string value, string expected) =>
        Regex.IsMatch(
            expected,
            "\\A" + Regex.Escape(MaskReferences(value)).Replace(Unknown.ToString(), ".*") + "\\z",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking
        );

    /// <summary>
    /// True when every reference follows the first port or instance separator, so the SQL Server protocol
    /// prefix and the host before it are literal. <see cref="StaticLocator"/> still validates the whole locator.
    /// A PostgreSQL separator is either the ':' directly after a complete, reference-free bracketed address, or
    /// the only ':', after a host that is not a socket or text that could begin an IPv6 address.
    /// </summary>
    private static bool HasLiteralServer(string locator, bool postgresql)
    {
        string masked = MaskReferences(locator).Trim();
        if (postgresql)
        {
            if (masked.StartsWith('['))
            {
                // The bracket ends the address, so a reference after it can only supply the port.
                int closingBracket = masked.IndexOf(']');
                return closingBracket > 1
                    && !masked[..closingBracket].Contains(Unknown)
                    && masked.Length > closingBracket + 1
                    && masked[closingBracket + 1] == ':';
            }
            int colon = masked.IndexOf(':');
            return colon > 0
                && colon == masked.LastIndexOf(':')
                && masked[0] is not ('/' or '@')
                && !masked[..colon].Contains(Unknown)
                && !CouldBeIpv6Group(masked[..colon]);
        }
        int separator = masked.IndexOfAny([',', '\\']);
        return separator > 0 && !masked[..separator].Contains(Unknown);
    }

    /// <summary>At most four hex digits: text that could be one group of an IPv6 address.</summary>
    private static bool CouldBeIpv6Group(string value) =>
        value.Length <= 4 && value.All(char.IsAsciiHexDigit);

    /// <summary>
    /// True when a SQL Server locator has a literal ',' or '\' separator, which then fixes port or
    /// named-instance addressing whatever its references hold: the native parser rejects a host, port or
    /// instance that holds either separator.
    /// <paramref name="suffixKnown"/> is true when the port or instance after it is reference-free.
    /// <see cref="StaticLocator"/> has already validated the whole locator and rejects both separators together.
    /// </summary>
    private static bool HasLiteralSqlServerSeparator(string locator, out bool suffixKnown)
    {
        string masked = MaskReferences(locator).Trim();
        int separator = masked.LastIndexOfAny([',', '\\']);
        suffixKnown = separator >= 0 && !masked[(separator + 1)..].Contains(Unknown);
        return separator >= 0;
    }

    private static string StaticLocator(string value, bool postgresql)
    {
        var tokens = SecretReferenceTokens.Find(value);
        if (tokens.Count == 0)
        {
            return value;
        }
        // A control character in the original literal cannot masquerade as our internal marker.
        if (value.Trim().Any(char.IsControl))
        {
            throw new ArgumentException("Unsupported literal endpoint component.");
        }
        value = MaskReferences(value);
        if (postgresql)
        {
            value = value.Trim();
            if (value.Contains(','))
            {
                throw new ArgumentException("Unsupported literal endpoint component.");
            }
            if (value.StartsWith('/') || value.StartsWith('@'))
            {
                return value.Replace(Unknown, 'x'); // Case-sensitive filesystem or abstract Unix socket.
            }
            if (value.StartsWith(Unknown))
            {
                // An unknown prefix could select a Unix socket directory, where ':' is a path
                // character rather than a port delimiter. The separate literal Port still applies.
                return "static-validation.invalid";
            }
            int closingBracket = value.StartsWith('[') ? value.IndexOf(']') : -1;
            int port = -1;
            if (closingBracket >= 0)
            {
                if (value.Length > closingBracket + 1 && value[closingBracket + 1] == ':')
                {
                    port = closingBracket + 1;
                }
            }
            else if (value.Count(c => c == ':') == 1)
            {
                port = value.IndexOf(':');
                // When the text before the colon and every literal run after it are IPv6 groups, a reference
                // may supply the rest of an address (2001:db8${secret:rest} can resolve to 2001:db8::1), so
                // the host is unknown. Any other literal text is still validated as a port below.
                string suffix = value[(port + 1)..];
                if (
                    suffix.Contains(Unknown)
                    && CouldBeIpv6Group(value[..port])
                    && Array.TrueForAll(suffix.Split(Unknown), CouldBeIpv6Group)
                )
                {
                    return "static-validation.invalid";
                }
            }
            if (port >= 0)
            {
                return StaticHost(value[..port], postgresql) + ":" + StaticPort(value[(port + 1)..]);
            }
            return StaticHost(value, postgresql);
        }

        int protocol = value.IndexOf(':');
        if (protocol >= 0)
        {
            string prefix = value[..protocol];
            if (CouldResolveTo(prefix, "tcp"))
            {
                value = value[(protocol + 1)..];
            }
            else if (!value.StartsWith(Unknown))
            {
                throw new ArgumentException("Unsupported literal endpoint component.");
            }
        }
        else if (!"tcp:".StartsWith(value[..value.IndexOf(Unknown)], StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Unsupported literal endpoint component.");
        }
        int instance = value.IndexOf('\\');
        int comma = value.IndexOf(',');
        if (instance >= 0 && comma >= 0)
        {
            throw new ArgumentException("Unsupported literal endpoint component.");
        }
        if (instance >= 0)
        {
            string name = value[(instance + 1)..];
            if (name.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-' or Unknown)))
            {
                throw new ArgumentException("Unsupported literal endpoint component.");
            }
            return "tcp:"
                + StaticHost(value[..instance], postgresql)
                + "\\"
                + (name.Contains(Unknown) ? "instance" : name);
        }
        return "tcp:"
            + (
                comma >= 0
                    ? StaticHost(value[..comma], postgresql) + "," + StaticPort(value[(comma + 1)..])
                    : StaticHost(value, postgresql)
            );
    }

    private static string StaticHost(string host, bool postgresql)
    {
        if (!host.Contains(Unknown))
        {
            return host;
        }
        // A leading unknown PostgreSQL component might resolve to an absolute socket directory.
        // Otherwise the literal host characters must belong to DNS/IP syntax.
        if (
            (!postgresql || !host.StartsWith(Unknown))
            && host.Any(c =>
                !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-' or '[' or ']' or ':' or Unknown)
            )
        )
        {
            throw new ArgumentException("Unsupported literal endpoint component.");
        }
        if (
            (host.StartsWith('[') || (!postgresql && host.Contains(':')))
            && (
                host.Any(c => !char.IsAsciiHexDigit(c) && c is not ('.' or '[' or ']' or ':' or Unknown))
                || (host.Contains(']') && !host.EndsWith(']'))
            )
        )
        {
            throw new ArgumentException("Unsupported literal endpoint component.");
        }
        return "static-validation.invalid";
    }

    private static string StaticPort(string port)
    {
        if (!port.Contains(Unknown))
        {
            return port;
        }
        if (port.Any(c => !char.IsAsciiDigit(c) && c != Unknown))
        {
            throw new ArgumentException("Unsupported literal endpoint component.");
        }
        // Even empty/all-zero substitutions cannot rescue a literal prefix/suffix above the limit.
        if (
            !ulong.TryParse(
                port.Replace(Unknown, '0'),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out ulong minimum
            )
            || minimum > 65535
        )
        {
            throw new ArgumentException("Unsupported literal port range.");
        }
        return "5432";
    }

    [DoesNotReturn]
    private static void Fail(string message) =>
        throw new OptionsValidationException(
            nameof(DataStoreManagementOptions),
            typeof(DataStoreManagementOptions),
            [message]
        );
}

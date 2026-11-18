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
    /// </summary>
    private sealed record KnownEndpoint(ManagedDataStoreEndpoint Endpoint, bool PortOrInstanceKnown)
    {
        public bool ProvablyDiffersFrom(KnownEndpoint other) =>
            PortOrInstanceKnown && other.PortOrInstanceKnown
                ? Endpoint != other.Endpoint
                : WithoutPortOrInstance(Endpoint) != WithoutPortOrInstance(other.Endpoint);

        // Representatives are never compared as real values.
        private static ManagedDataStoreEndpoint WithoutPortOrInstance(ManagedDataStoreEndpoint endpoint) =>
            endpoint with
            {
                Port = null,
                Instance = null,
            };
    }

    /// <returns>The known endpoint, or null when the effective host depends on a secret.</returns>
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
                string locator = (string?)literalFields[routingField] ?? string.Empty;
                // The native builder has applied aliases and last-assignment precedence to this template.
                routingUnknown = SecretReferenceTokens.Find(locator).Count != 0;
                if (routingUnknown && provider.Provider == "mssql" && HasLiteralServer(locator))
                {
                    // Only the port or instance after a literal "tcp:host" and its separator is unknown.
                    // A reference inside a PostgreSQL host could add routing syntax, so none is compared.
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
                return null;
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
    /// SQL Server only: true when every reference follows the first port or instance separator, so the protocol
    /// prefix and host before it are literal. <see cref="StaticLocator"/> still validates the whole locator.
    /// </summary>
    private static bool HasLiteralServer(string locator)
    {
        string masked = MaskReferences(locator).Trim();
        int separator = masked.IndexOfAny([',', '\\']);
        return separator > 0 && !masked[..separator].Contains(Unknown);
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

// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Infrastructure;

/// <summary>
/// Warns once at startup when JwtAuthentication:RequireHttpsMetadata is false and the hosting
/// environment is not Development.
/// </summary>
/// <remarks>
/// <para>
/// The condition is exactly that: the effective value is false and
/// <see cref="HostEnvironmentEnvExtensions.IsDevelopment"/> is false. Test, Staging, Production and every
/// other name warn, whether or not AppSettings:BypassAuthorization is set and whether or not unrelated
/// configuration is invalid.
/// </para>
/// <para>
/// Program.cs calls this before every configuration gate, so a host that goes on to install the
/// invalid-configuration middleware, and so skips the startup phases, still logs the warning.
/// </para>
/// <para>
/// That call sits in the unguarded window between building the host and the first fatal startup phase,
/// so nothing here may throw on a configuration value. The flag is therefore read as one raw key and
/// parsed with <see cref="bool.TryParse(string?, out bool)"/>, which accepts what the options binder
/// accepts, rather than by binding <see cref="JwtAuthenticationOptions"/>. A value that does not parse
/// logs a notice and skips the check; the binder still reports it where the section is bound.
/// </para>
/// </remarks>
internal static class RequireHttpsMetadataStartupWarning
{
    internal const string SettingKey = "JwtAuthentication:RequireHttpsMetadata";

    internal const string Message =
        "JwtAuthentication:RequireHttpsMetadata is false and the hosting environment is not Development. "
        + "OIDC metadata and signing keys may be fetched without requiring https. "
        + "Set JwtAuthentication:RequireHttpsMetadata to true unless the identity provider is reachable only over a trusted network.";

    internal const string UnreadableMessage =
        "JwtAuthentication:RequireHttpsMetadata could not be read as a boolean, so the https-metadata startup check was skipped. "
        + "The value is validated where JwtAuthentication is bound.";

    public static void Emit(ILogger logger, IHostEnvironment environment, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(configuration);

        bool requireHttpsMetadata;
        if (configuration[SettingKey] is not string configured)
        {
            requireHttpsMetadata = new JwtAuthenticationOptions().RequireHttpsMetadata;
        }
        else if (!bool.TryParse(configured, out requireHttpsMetadata))
        {
            logger.LogWarning(UnreadableMessage);
            return;
        }

        if (!requireHttpsMetadata && !environment.IsDevelopment())
        {
            logger.LogWarning(Message);
        }
    }
}

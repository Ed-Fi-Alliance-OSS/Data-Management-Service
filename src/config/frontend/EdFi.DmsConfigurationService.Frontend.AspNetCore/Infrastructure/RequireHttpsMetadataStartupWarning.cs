// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Frontend.AspNetCore.Configuration;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;

/// <summary>
/// Warns once at startup when IdentitySettings:RequireHttpsMetadata is false and the hosting
/// environment is not Development.
/// </summary>
/// <remarks>
/// <para>
/// The condition is exactly that: the effective value is false and
/// <see cref="HostEnvironmentEnvExtensions.IsDevelopment"/> is false. Test, Staging, Production and every
/// other name warn, in both identity modes (the flag governs no metadata retrieval in self-contained mode)
/// and whether or not unrelated configuration is invalid.
/// </para>
/// <para>
/// Program.cs calls this before the DatabaseOptions gate, the token-revocation check and
/// ReportInvalidConfiguration, so a host that goes on to run in configuration-reporting mode still logs
/// the warning. That covers options-validation failures, which surface only when ReportInvalidConfiguration
/// resolves the options after this call.
/// </para>
/// <para>
/// The flag is read as one raw key and parsed with <see cref="bool.TryParse(string?, out bool)"/>, which
/// accepts what the options binder accepts, rather than by binding <see cref="IdentitySettings"/>, so it
/// is available even when another IdentitySettings value fails validation and nothing here can throw on a
/// configuration value. A value that does not parse logs a notice and skips the check.
/// </para>
/// <para>
/// During host startup that notice is not guaranteed to be reached: AddServices binds IdentitySettings
/// (ConfigureIdentityProvider) before Program.cs calls this, and a value the binder cannot convert, the
/// flag included, throws there and aborts startup before this runs. The unreadable-value branch is a
/// defensive guard for direct callers, not a startup diagnostic, and that earlier binding failure never
/// reaches configuration-reporting mode.
/// </para>
/// </remarks>
internal static class RequireHttpsMetadataStartupWarning
{
    internal const string SettingKey = "IdentitySettings:RequireHttpsMetadata";

    internal const string Message =
        "IdentitySettings:RequireHttpsMetadata is false and the hosting environment is not Development. "
        + "OIDC metadata and signing keys may be fetched without requiring https. "
        + "Set IdentitySettings:RequireHttpsMetadata to true unless the identity provider is reachable only over a trusted network.";

    internal const string UnreadableMessage =
        "IdentitySettings:RequireHttpsMetadata could not be read as a boolean, so the https-metadata startup check was skipped. "
        + "The value is validated where IdentitySettings is bound.";

    public static void Emit(ILogger logger, IHostEnvironment environment, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(configuration);

        bool requireHttpsMetadata;
        if (configuration[SettingKey] is not string configured)
        {
            requireHttpsMetadata = IdentitySettings.RequireHttpsMetadataDefault;
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

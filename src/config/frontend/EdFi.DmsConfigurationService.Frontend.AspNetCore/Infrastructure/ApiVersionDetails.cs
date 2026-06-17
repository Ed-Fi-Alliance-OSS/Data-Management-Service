// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Reflection;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;

public static class ApiVersionDetails
{
    // Keep in sync with <InformationalVersion> in src/config/Directory.Build.props; only used when
    // the assembly attribute is absent (e.g. an unstamped build).
    private const string FallbackInformationalVersion = "8.0.0";

    private static readonly Version AssemblyVersion =
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);

    /// <summary>
    /// Semantic version of the DMS Configuration Api.
    /// </summary>
    public static readonly string Version =
        $"{AssemblyVersion.Major}.{AssemblyVersion.Minor}.{AssemblyVersion.Build}";

    /// <summary>
    /// Application name
    /// </summary>
    public const string ApplicationName = "Ed-Fi API Configuration Service";

    /// <summary>
    /// Informational version description
    /// </summary>
    public static readonly string InformationalVersion = ResolveInformationalVersion();

    /// <summary>
    /// Assembly version of the DMS Configuration Api.
    /// </summary>
    public static readonly string Build = AssemblyVersion.ToString();

    private static string ResolveInformationalVersion() =>
        NormalizeInformationalVersion(
            Assembly
                .GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion
        );

    /// <summary>
    /// Strips any build-metadata suffix (e.g. "+&lt;git-sha&gt;") from the informational version so it
    /// does not leak into the response, falling back to the release version when none is present.
    /// </summary>
    internal static string NormalizeInformationalVersion(string? informationalVersion)
    {
        if (string.IsNullOrWhiteSpace(informationalVersion))
        {
            return FallbackInformationalVersion;
        }

        int metadataIndex = informationalVersion.IndexOf('+');
        return metadataIndex >= 0 ? informationalVersion[..metadataIndex] : informationalVersion;
    }
}

// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Reflection;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Content;

public interface IVersionProvider
{
    string Version { get; }

    string ApplicationName { get; }

    string InformationalVersion { get; }
}

public class VersionProvider : IVersionProvider
{
    private const string FallbackInformationalVersion = "8.0.0";

    public string Version => $"{FullVersion.Major}.{FullVersion.Minor}.{FullVersion.Build}";

    public string ApplicationName => "Ed-Fi API";

    public string InformationalVersion =>
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

    private static Version FullVersion =>
        Assembly.GetExecutingAssembly().GetName().Version
        ?? throw new InvalidOperationException("Unable to retrieve assembly version");
}

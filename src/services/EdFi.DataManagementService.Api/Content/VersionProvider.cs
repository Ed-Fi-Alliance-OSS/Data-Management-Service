// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Reflection;

namespace EdFi.DataManagementService.Api.Content;

public interface IVersionProvider
{
    string Version { get; }

    string InformationalVersion { get; }

    string Build { get; }

    string Suite { get; }
}

public class VersionProvider : IVersionProvider
{
    public string Version => $"{FullVersion.Major}.{FullVersion.Minor}";

    public string InformationalVersion => $"{FullVersion.Major}.{FullVersion.Minor}";

    public string Build => FullVersion.ToString();

    public string Suite => "DMS";

    private static Version FullVersion
    {
        get
        {
            var version =
                (Assembly.GetEntryAssembly()?.GetName().Version)
                ?? throw new Exception("Error while retrieving assembly version");
            return version;
        }
    }
}

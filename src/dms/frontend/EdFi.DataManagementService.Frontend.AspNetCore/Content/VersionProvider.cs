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
}

public class VersionProvider : IVersionProvider
{
    public string Version => $"{FullVersion.Major}.{FullVersion.Minor}.{FullVersion.MinorRevision}";

    public string ApplicationName => "Ed-Fi Alliance Data Management Service";

    private static Version FullVersion
    {
        get
        {
            var version =
                (Assembly.GetEntryAssembly()?.GetName().Version)
                ?? throw new InvalidOperationException("Unable to retrieve assembly version");
            return version;
        }
    }
}

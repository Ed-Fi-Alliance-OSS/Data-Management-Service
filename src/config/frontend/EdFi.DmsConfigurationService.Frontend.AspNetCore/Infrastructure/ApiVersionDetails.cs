// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Reflection;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;

public static class ApiVersionDetails
{
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
    public const string InformationalVersion = "8.0.0";

    /// <summary>
    /// Assembly version of the DMS Configuration Api.
    /// </summary>
    public static readonly string Build = AssemblyVersion.ToString();
}

// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Reflection;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;

public static class ApiVersionDetails
{
    /// <summary>
    /// Semantic version of the DMS Configuration Api.
    /// </summary>
    public const string Version = "0.7.0";

    /// <summary>
    /// Application name
    /// </summary>
    public const string ApplicationName = "Ed-Fi Alliance DMS Configuration Service";

    /// <summary>
    /// Informational version description
    /// </summary>
    public const string InformationalVersion = "Release Candidate 1";

    /// <summary>
    /// Assembly version of the DMS Configuration Api.
    /// </summary>
    public static readonly string Build =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? Version;
}

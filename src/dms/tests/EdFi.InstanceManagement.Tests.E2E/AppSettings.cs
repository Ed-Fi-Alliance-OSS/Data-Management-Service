// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Microsoft.Extensions.Configuration;

namespace EdFi.InstanceManagement.Tests.E2E;

public static class AppSettings
{
    private static IConfigurationRoot? _configuration;

    public static IConfigurationRoot Configuration
    {
        get
        {
            if (_configuration == null)
            {
                var builder = new ConfigurationBuilder()
                    .SetBasePath(Directory.GetCurrentDirectory())
                    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);

                _configuration = builder.Build();
            }
            return _configuration;
        }
    }

    public static string QueryHandler => Configuration["QueryHandler"] ?? "postgresql";

    public static string AuthenticationService =>
        Configuration["AuthenticationService"]
        ?? "http://localhost:8045/realms/edfi/protocol/openid-connect/token";

    public static bool EnableClaimsetReload => bool.Parse(Configuration["EnableClaimsetReload"] ?? "false");
}

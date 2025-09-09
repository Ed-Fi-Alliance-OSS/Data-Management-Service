// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Microsoft.Extensions.Configuration;

namespace EdFi.DataManagementService.Tests.E2E
{
    public static class AppSettings
    {
        private static readonly IConfiguration _configuration =
            new ConfigurationBuilder()
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                .Build() ?? throw new InvalidOperationException("Unable to read appsettings.json");

        public static bool OpenSearchEnabled =>
            !string.IsNullOrEmpty(_configuration["QueryHandler"])
            && _configuration["QueryHandler"]!.Equals(
                "opensearch",
                StringComparison.InvariantCultureIgnoreCase
            );

        public static string DmsPort = "8080"; //5198 for local
        public static string ConfigServicePort = "8081"; //5126 for local
        public static string AuthenticationService =
            _configuration["AuthenticationService"]
            ?? "http://dms-keycloak:8080/realms/edfi/protocol/openid-connect/token";
    }
}

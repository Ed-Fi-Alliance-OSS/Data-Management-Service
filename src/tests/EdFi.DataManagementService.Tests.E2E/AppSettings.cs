// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Microsoft.Extensions.Configuration;

namespace EdFi.DataManagementService.Tests.E2E
{
    public class AppSettings
    {
        private static IConfiguration? _configuration = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .Build();

        public static bool UseTestContainers => bool.TryParse(_configuration!["useTestContainers"], out _);

        public static bool OpenSearchEnabled =>
            !string.IsNullOrEmpty(_configuration!["QueryHandler"])
            && _configuration["QueryHandler"]!.Equals(
                "opensearch",
                StringComparison.InvariantCultureIgnoreCase
            );
    }
}

// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.


using System.Data;
using Microsoft.Extensions.Configuration;

namespace EdFi.DataManagementService.Backend.Postgresql.Test.Integration
{
    public static class Configuration
    {
        private static IConfiguration? _configuration;

        public static IConfiguration Config()
        {
            if (_configuration is not null)
            {
                return _configuration;
            }

            var testAppSettingsFileName = "appsettings.Test.json";

            ConfigurationBuilder configurationBuilder = new();
            configurationBuilder.AddJsonFile("appsettings.json");

            if (File.Exists(testAppSettingsFileName))
            {
                configurationBuilder.AddJsonFile(testAppSettingsFileName);
            }

            _configuration = configurationBuilder!.Build();

            return _configuration;
        }

        public static string? DatabaseConnectionString => Config().GetConnectionString("DatabaseConnection");

        public static IsolationLevel IsolationLevel =>
            Enum.Parse<IsolationLevel>(Config().GetSection("IsolationLevel").Value!);
    }
}

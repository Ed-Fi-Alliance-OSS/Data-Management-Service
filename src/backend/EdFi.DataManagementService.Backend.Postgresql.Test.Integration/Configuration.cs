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
        private static ConfigurationBuilder? _configurationBuilder;

        public static IConfiguration Config()
        {
            var testAppSettingsFileName = "appsettings.Test.json";

            _configurationBuilder = new ConfigurationBuilder();
            _configurationBuilder.AddJsonFile("appsettings.json");

            if (_configurationBuilder != null && File.Exists(testAppSettingsFileName))
            {
                _configurationBuilder.AddJsonFile(testAppSettingsFileName);
            }

            return _configurationBuilder!.Build();
        }

        public static string? DatabaseConnectionString => Config().GetConnectionString("DatabaseConnection");

        public static IsolationLevel IsolationLevel =>
            Enum.Parse<IsolationLevel>(Config().GetSection("IsolationLevel").Value!);
    }
}

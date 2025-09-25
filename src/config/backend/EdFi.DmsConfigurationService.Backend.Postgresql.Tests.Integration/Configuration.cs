// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration
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

        public static IOptions<DatabaseOptions> DatabaseOptions = Options.Create(
            new DatabaseOptions()
            {
                DatabaseConnection =
                    Config().GetSection("DatabaseSettings")["DatabaseConnection"] ?? string.Empty,
                EncryptionKey =
                    Config().GetSection("DatabaseSettings")["EncryptionKey"]
                    ?? "TestEncryptionKey123456789012345678901234567890",
            }
        );
    }
}

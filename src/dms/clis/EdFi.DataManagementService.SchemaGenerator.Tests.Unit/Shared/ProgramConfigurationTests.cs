// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace EdFi.DataManagementService.SchemaGenerator.Tests.Unit.Shared
{
    /// <summary>
    /// Simplified unit tests for configuration scenarios.
    /// </summary>
    [TestFixture]
    public class ProgramConfigurationTests
    {
        [Test]
        public void ConfigurationBuilder_ShouldLoadFromMultipleSources()
        {
            // Arrange
            var tempDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            Directory.CreateDirectory(tempDirectory);

            try
            {
                var configFile = Path.Combine(tempDirectory, "appsettings.json");
                File.WriteAllText(configFile, @"{""TestSetting"": ""FromFile""}");

                Environment.SetEnvironmentVariable("TestSetting", "FromEnvironment");

                // Act - Create a configuration like Program would
                var configuration = new ConfigurationBuilder()
                    .SetBasePath(tempDirectory)
                    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
                    .AddEnvironmentVariables()
                    .Build();

                // Assert
                configuration["TestSetting"].Should().Be("FromEnvironment"); // Environment overrides file
            }
            finally
            {
                // Cleanup
                Environment.SetEnvironmentVariable("TestSetting", null);
                if (Directory.Exists(tempDirectory))
                {
                    try
                    {
                        Directory.Delete(tempDirectory, true);
                    }
                    catch (IOException)
                    {
                        // Ignore cleanup issues
                    }
                }
            }
        }

        [Test]
        public void ConfigurationBuilder_WithMissingFile_ShouldNotThrow()
        {
            // Arrange
            var tempDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            Directory.CreateDirectory(tempDirectory);

            try
            {
                // Act & Assert - Should not throw because file is optional
                var configuration = new ConfigurationBuilder()
                    .SetBasePath(tempDirectory)
                    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
                    .AddEnvironmentVariables()
                    .Build();

                configuration.Should().NotBeNull();
            }
            finally
            {
                if (Directory.Exists(tempDirectory))
                {
                    try
                    {
                        Directory.Delete(tempDirectory, true);
                    }
                    catch (IOException)
                    {
                        // Ignore cleanup issues
                    }
                }
            }
        }

        [Test]
        public void ConfigurationBuilder_WithEnvironmentVariables_ShouldLoadCorrectly()
        {
            // Arrange
            Environment.SetEnvironmentVariable("TEST_CONFIG_VALUE", "TestValue");

            try
            {
                // Act
                var configuration = new ConfigurationBuilder()
                    .AddEnvironmentVariables()
                    .Build();

                // Assert
                configuration["TEST_CONFIG_VALUE"].Should().Be("TestValue");
            }
            finally
            {
                // Cleanup
                Environment.SetEnvironmentVariable("TEST_CONFIG_VALUE", null);
            }
        }
    }
}

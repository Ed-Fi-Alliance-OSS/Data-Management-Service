// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.SchemaGenerator.Abstractions;
using EdFi.DataManagementService.SchemaGenerator.Mssql;
using EdFi.DataManagementService.SchemaGenerator.Pgsql;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.SchemaGenerator.Tests.Unit.Shared
{
    /// <summary>
    /// Simplified unit tests for service configuration.
    /// </summary>
    [TestFixture]
    public class ProgramServiceConfigurationTests
    {
        [Test]
        public void ServiceCollection_CanRegisterDdlGeneratorStrategy()
        {
            // Arrange
            var services = new ServiceCollection();

            // Act
            services.AddScoped<IDdlGeneratorStrategy, PgsqlDdlGeneratorStrategy>();
            services.AddLogging();

            var serviceProvider = services.BuildServiceProvider();

            // Assert
            var ddlGenerator = serviceProvider.GetService<IDdlGeneratorStrategy>();
            ddlGenerator.Should().NotBeNull();
            ddlGenerator.Should().BeOfType<PgsqlDdlGeneratorStrategy>();
        }

        [Test]
        public void ServiceCollection_CanRegisterLoggerFactory()
        {
            // Arrange
            var services = new ServiceCollection();

            // Act
            services.AddLogging();
            var serviceProvider = services.BuildServiceProvider();

            // Assert
            var loggerFactory = serviceProvider.GetService<ILoggerFactory>();
            loggerFactory.Should().NotBeNull();
        }

        [Test]
        public void ServiceCollection_CanCreateLoggerForClass()
        {
            // Arrange
            var services = new ServiceCollection();
            services.AddLogging();
            var serviceProvider = services.BuildServiceProvider();

            // Act
            var logger = serviceProvider.GetService<ILogger<ProgramServiceConfigurationTests>>();

            // Assert
            logger.Should().NotBeNull();
        }

        [Test]
        public void ServiceCollection_WithMultipleRegistrations_ShouldResolveCorrectly()
        {
            // Arrange
            var services = new ServiceCollection();

            // Act
            services.AddScoped<IDdlGeneratorStrategy, PgsqlDdlGeneratorStrategy>();
            services.AddScoped<IDdlGeneratorStrategy, MssqlDdlGeneratorStrategy>();
            services.AddLogging();
            services.AddScoped(provider => provider);

            var serviceProvider = services.BuildServiceProvider();

            // Assert
            var ddlGenerators = serviceProvider.GetServices<IDdlGeneratorStrategy>();
            var loggerFactory = serviceProvider.GetService<ILoggerFactory>();
            var resolvedServiceProvider = serviceProvider.GetService<IServiceProvider>();

            ddlGenerators.Should().NotBeNull();
            ddlGenerators.Should().HaveCount(2);
            loggerFactory.Should().NotBeNull();
            resolvedServiceProvider.Should().NotBeNull();
        }

        [Test]
        public void ServiceProvider_CanDisposeCorrectly()
        {
            // Arrange
            var services = new ServiceCollection();
            services.AddScoped<IDdlGeneratorStrategy, PgsqlDdlGeneratorStrategy>();
            services.AddLogging();

            // Act & Assert
            using var serviceProvider = services.BuildServiceProvider();
            var ddlGenerator = serviceProvider.GetService<IDdlGeneratorStrategy>();
            ddlGenerator.Should().NotBeNull();
            // Should not throw when disposed
        }
    }
}

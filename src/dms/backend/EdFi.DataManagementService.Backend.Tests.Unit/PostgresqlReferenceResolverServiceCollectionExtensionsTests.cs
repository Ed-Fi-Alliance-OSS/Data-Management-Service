// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Postgresql;
using EdFi.DataManagementService.Core.External.Backend;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
[Parallelizable]
public class Given_Postgresql_Reference_Resolver_Service_Collection_Extensions
{
    [Test]
    public void It_registers_the_postgresql_reference_resolution_composition_surface()
    {
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddScoped<IRequestConnectionProvider>(_ => A.Fake<IRequestConnectionProvider>());
        services.AddPostgresqlReferenceResolver();

        bool containsLegacyProviderRegistration = services.Any(serviceDescriptor =>
            serviceDescriptor.ServiceType.Name == "NpgsqlDataSourceProvider"
            || serviceDescriptor.ImplementationType?.Name == "NpgsqlDataSourceProvider"
        );

        containsLegacyProviderRegistration.Should().BeFalse();

        using var serviceProvider = BuildServiceProvider(services);
        using var scope = serviceProvider.CreateScope();

        var resolver = scope.ServiceProvider.GetRequiredService<IReferenceResolver>();
        var factory = scope.ServiceProvider.GetRequiredService<IReferenceResolverAdapterFactory>();
        var adapter = scope.ServiceProvider.GetRequiredService<IReferenceResolverAdapter>();
        var commandExecutor = scope.ServiceProvider.GetRequiredService<IRelationalCommandExecutor>();
        var dbConnectionProvider =
            scope.ServiceProvider.GetRequiredService<IPostgresqlDbConnectionProvider>();
        var dataSourceCache = serviceProvider.GetRequiredService<PostgresqlDataSourceCache>();

        resolver.Should().BeOfType<ReferenceResolver>();
        factory.Should().BeOfType<PostgresqlReferenceResolverAdapterFactory>();
        adapter.Should().BeOfType<PostgresqlReferenceResolverAdapter>();
        commandExecutor.Should().BeOfType<PostgresqlRelationalCommandExecutor>();
        dbConnectionProvider.Should().BeOfType<PostgresqlRequestDbConnectionProvider>();
        dataSourceCache.Should().BeOfType<PostgresqlDataSourceCache>();
    }

    private static ServiceProvider BuildServiceProvider(IServiceCollection services)
    {
        return services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }
        );
    }
}

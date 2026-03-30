// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Mssql;
using EdFi.DataManagementService.Core.Configuration;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
[Parallelizable]
public class Given_Mssql_Reference_Resolver_Service_Collection_Extensions
{
    [Test]
    public void It_registers_the_mssql_reference_resolution_composition_surface()
    {
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddScoped<IDmsInstanceSelection, DmsInstanceSelection>();
        services.AddMssqlReferenceResolver();

        using var serviceProvider = BuildServiceProvider(services);
        using var scope = serviceProvider.CreateScope();

        var resolver = scope.ServiceProvider.GetRequiredService<IReferenceResolver>();
        var writeFlattener = scope.ServiceProvider.GetRequiredService<IRelationalWriteFlattener>();
        var targetContextResolver =
            scope.ServiceProvider.GetRequiredService<IRelationalWriteTargetContextResolver>();
        var terminalStage = scope.ServiceProvider.GetRequiredService<IRelationalWriteTerminalStage>();
        var factory = scope.ServiceProvider.GetRequiredService<IReferenceResolverAdapterFactory>();
        var adapter = scope.ServiceProvider.GetRequiredService<IReferenceResolverAdapter>();
        var commandExecutor = scope.ServiceProvider.GetRequiredService<IRelationalCommandExecutor>();

        resolver.Should().BeOfType<ReferenceResolver>();
        writeFlattener.Should().BeOfType<RelationalWriteFlattener>();
        targetContextResolver.Should().BeOfType<RelationalWriteTargetContextResolver>();
        terminalStage.Should().BeOfType<DefaultRelationalWriteTerminalStage>();
        factory.Should().BeOfType<MssqlReferenceResolverAdapterFactory>();
        adapter.Should().BeOfType<MssqlReferenceResolverAdapter>();
        commandExecutor.Should().BeOfType<MssqlRelationalCommandExecutor>();
    }

    private static ServiceProvider BuildServiceProvider(IServiceCollection services)
    {
        return services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }
        );
    }
}

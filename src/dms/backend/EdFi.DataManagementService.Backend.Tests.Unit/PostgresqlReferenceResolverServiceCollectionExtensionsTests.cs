// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Postgresql;
using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Old.Postgresql;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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
        services.AddSingleton<IHostApplicationLifetime, NoOpHostApplicationLifetime>();
        services.AddSingleton<NpgsqlDataSourceCache>();
        services.AddScoped<IDmsInstanceSelection, DmsInstanceSelection>();
        services.AddScoped<NpgsqlDataSourceProvider>();
        services.Configure<DatabaseOptions>(options => options.IsolationLevel = IsolationLevel.ReadCommitted);
        services.AddTestReadableProfileProjector();
        services.AddPostgresqlReferenceResolver();

        using var serviceProvider = BuildServiceProvider(services);
        using var scope = serviceProvider.CreateScope();

        var resolver = scope.ServiceProvider.GetRequiredService<IReferenceResolver>();
        var writeFlattener = scope.ServiceProvider.GetRequiredService<IRelationalWriteFlattener>();
        var currentStateLoader =
            scope.ServiceProvider.GetRequiredService<IRelationalWriteCurrentStateLoader>();
        var writeFreshnessChecker =
            scope.ServiceProvider.GetRequiredService<IRelationalWriteFreshnessChecker>();
        var noProfileMergeSynthesizer =
            scope.ServiceProvider.GetRequiredService<IRelationalWriteNoProfileMergeSynthesizer>();
        var noProfilePersister = scope.ServiceProvider.GetRequiredService<IRelationalWritePersister>();
        var targetLookupService =
            scope.ServiceProvider.GetRequiredService<IRelationalWriteTargetLookupService>();
        var targetLookupResolver =
            scope.ServiceProvider.GetRequiredService<IRelationalWriteTargetLookupResolver>();
        var writeExecutor = scope.ServiceProvider.GetRequiredService<IRelationalWriteExecutor>();
        var writeSessionFactory = scope.ServiceProvider.GetRequiredService<IRelationalWriteSessionFactory>();
        var documentHydrator = scope.ServiceProvider.GetRequiredService<IDocumentHydrator>();
        var factory = scope.ServiceProvider.GetRequiredService<IReferenceResolverAdapterFactory>();
        var adapter = scope.ServiceProvider.GetRequiredService<IReferenceResolverAdapter>();
        var commandExecutor = scope.ServiceProvider.GetRequiredService<IRelationalCommandExecutor>();
        var readMaterializer = scope.ServiceProvider.GetRequiredService<IRelationalReadMaterializer>();
        var readTargetLookupService =
            scope.ServiceProvider.GetRequiredService<IRelationalReadTargetLookupService>();
        var writeExceptionClassifier =
            scope.ServiceProvider.GetRequiredService<IRelationalWriteExceptionClassifier>();
        var writeConstraintResolver =
            scope.ServiceProvider.GetRequiredService<IRelationalWriteConstraintResolver>();

        resolver.Should().BeOfType<ReferenceResolver>();
        writeFlattener.Should().BeOfType<RelationalWriteFlattener>();
        currentStateLoader.Should().BeOfType<RelationalWriteCurrentStateLoader>();
        writeFreshnessChecker.Should().BeOfType<RelationalWriteFreshnessChecker>();
        noProfileMergeSynthesizer.Should().BeOfType<RelationalWriteNoProfileMergeSynthesizer>();
        noProfilePersister.Should().BeOfType<RelationalWriteNoProfilePersister>();
        targetLookupService.Should().BeOfType<RelationalWriteTargetLookupService>();
        targetLookupResolver.Should().BeOfType<RelationalWriteTargetLookupResolver>();
        writeExecutor.Should().BeOfType<DefaultRelationalWriteExecutor>();
        writeSessionFactory.Should().BeOfType<PostgresqlRelationalWriteSessionFactory>();
        documentHydrator.Should().BeOfType<PostgresqlDocumentHydrator>();
        factory.Should().BeOfType<PostgresqlReferenceResolverAdapterFactory>();
        adapter.Should().BeOfType<PostgresqlReferenceResolverAdapter>();
        commandExecutor.Should().BeOfType<PostgresqlRelationalCommandExecutor>();
        readMaterializer.Should().BeOfType<RelationalReadMaterializer>();
        readTargetLookupService.Should().BeOfType<RelationalReadTargetLookupService>();
        writeExceptionClassifier.Should().BeOfType<PostgresqlRelationalWriteExceptionClassifier>();
        writeConstraintResolver.Should().BeOfType<RelationalWriteConstraintResolver>();
    }

    private static ServiceProvider BuildServiceProvider(IServiceCollection services)
    {
        return services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }
        );
    }

    private sealed class NoOpHostApplicationLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication() { }
    }
}

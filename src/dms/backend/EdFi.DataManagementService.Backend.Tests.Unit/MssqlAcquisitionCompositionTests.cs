// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Mssql;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.Profile;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

/// <summary>
/// The seams must receive the acquisition boundary the composition registered, not one they construct
/// for themselves. Resolving them through the real registrations is the only way to see that: a test
/// that constructs a seam directly and hands it a substitute passes even when the production
/// constructor Microsoft DI actually selects builds its own boundary and ignores the registration.
/// </summary>
[TestFixture]
[Parallelizable]
public class MssqlAcquisitionCompositionTests
{
    private const string ConnectionString =
        "Server=localhost,1433;Database=edfi;User Id=sa;Password=abcdefgh1!;TrustServerCertificate=true;";

    /// <summary>
    /// Records the acquisitions it is asked for, then refuses to produce a connection so the assertion
    /// stays about which instance the seam reached.
    /// </summary>
    private sealed class RecordingAcquisition : IMssqlConnectionAcquisition
    {
        internal const string RefusedMessage = "Recording acquisition refuses to produce a connection.";

        public List<EffectiveDataStoreTarget> Targets { get; } = [];

        public Task<MssqlConnectionLease> AcquireLeaseAsync(
            EffectiveDataStoreTarget target,
            CancellationToken cancellationToken = default
        )
        {
            Targets.Add(target);
            throw new NotSupportedException(RefusedMessage);
        }
    }

    private static ServiceProvider BuildMssqlComposition(RecordingAcquisition acquisition)
    {
        ServiceCollection services = new();

        services.AddLogging();
        services.AddSingleton(A.Fake<IReadableProfileProjector>());
        services.AddSingleton(A.Fake<IDataStoreProvider>());

        var selection = A.Fake<IDataStoreSelection>();
        A.CallTo(() => selection.GetSelectedDataStore())
            .Returns(
                new DataStore(
                    Id: 1,
                    DataStoreType: "Test",
                    Name: "Test Instance",
                    ConnectionString: ConnectionString,
                    RouteContext: []
                )
            );
        A.CallTo(() => selection.GetEffectiveTarget())
            .Returns(EffectiveDataStoreTarget.Primary(ConnectionString));
        services.AddScoped(_ => selection);

        services.AddMssqlReferenceResolver();
        services.Replace(
            ServiceDescriptor.Singleton<IDatabaseFingerprintReader, MssqlDatabaseFingerprintReader>()
        );
        services.Replace(ServiceDescriptor.Singleton<IResourceKeyRowReader, MssqlResourceKeyRowReader>());
        services.Configure<DatabaseOptions>(options => options.IsolationLevel = IsolationLevel.ReadCommitted);

        // Replaces whatever the composition registered, so a seam that reached the registration reaches
        // this instance and a seam that built its own reaches neither.
        services.Replace(ServiceDescriptor.Singleton<IMssqlConnectionAcquisition>(acquisition));

        return services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = false }
        );
    }

    private static async Task ExpectRefusal(Func<Task> exercise)
    {
        try
        {
            await exercise();
        }
        catch (NotSupportedException exception)
            when (exception.Message == RecordingAcquisition.RefusedMessage)
        {
            // Expected: reaching the registered boundary is the assertion.
        }
    }

    [Test]
    public async Task It_supplies_the_registered_boundary_to_every_resolved_seam()
    {
        RecordingAcquisition acquisition = new();
        await using ServiceProvider serviceProvider = BuildMssqlComposition(acquisition);
        using IServiceScope scope = serviceProvider.CreateScope();
        IServiceProvider scoped = scope.ServiceProvider;

        EffectiveDataStoreTarget target = EffectiveDataStoreTarget.Primary(ConnectionString);

        await ExpectRefusal(() =>
            scoped.GetRequiredService<IDatabaseFingerprintReader>().ReadFingerprintAsync(target)
        );
        await ExpectRefusal(() =>
            scoped.GetRequiredService<IResourceKeyRowReader>().ReadResourceKeyRowsAsync(target)
        );
        await ExpectRefusal(() =>
            scoped
                .GetRequiredService<IRelationalCommandExecutor>()
                .ExecuteReaderAsync(new RelationalCommand("select 1", []), (_, _) => Task.FromResult(0))
        );
        await ExpectRefusal(() => scoped.GetRequiredService<IRelationalWriteSessionFactory>().CreateAsync());
        await ExpectRefusal(() =>
            scoped
                .GetRequiredService<IDocumentHydrator>()
                .HydrateAsync(null!, null!, new HydrationExecutionOptions(), CancellationToken.None)
        );

        acquisition
            .Targets.Should()
            .HaveCount(5, "every resolved seam must reach the registered acquisition boundary");
        acquisition.Targets.Should().AllSatisfy(recorded => recorded.Should().Be(target));
    }

    /// <summary>
    /// The seams take the boundary by constructor injection, so the composition has to register one
    /// wherever they are registered. Without this the reference-resolver registration used on its own
    /// would fail to resolve rather than silently building a second boundary.
    /// </summary>
    [Test]
    public void It_registers_an_acquisition_boundary_with_the_reference_resolver_composition()
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton(A.Fake<IReadableProfileProjector>());
        services.AddSingleton(A.Fake<IDataStoreProvider>());
        services.AddScoped<IDataStoreSelection, DataStoreSelection>();

        services.AddMssqlReferenceResolver();

        using ServiceProvider serviceProvider = services.BuildServiceProvider();

        serviceProvider
            .GetService<IMssqlConnectionAcquisition>()
            .Should()
            .BeOfType<MssqlConnectionAcquisition>();
    }
}

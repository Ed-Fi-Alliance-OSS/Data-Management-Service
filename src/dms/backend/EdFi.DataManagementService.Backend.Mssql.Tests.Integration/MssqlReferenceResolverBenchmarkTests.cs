// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using System.Globalization;
using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

/// <summary>
/// The Phase 3 cutover gate on SQL Server: the same request resolved by the old
/// <c>dms.ReferentialIdentity</c> hash resolver and by the natural-key resolver, over ONE seeded database,
/// with per-iteration wall times recorded to CSV — the mirror of
/// <c>Given_PostgresqlReferenceResolverBenchmark</c>.
/// </summary>
/// <remarks>
/// The bulk batch is 2500 references rather than PostgreSQL's 4096: that is the size the differential venue
/// already proves end to end here, and it is the size at which the old hash resolver escapes to its
/// table-valued parameter, so both arms are measured at their set-valued shape. The natural-key arm binds a
/// single <c>nvarchar(max)</c> <c>OPENJSON</c> payload per target group whatever the batch size, so what
/// this batch actually measures on that arm is payload size, not parameter count. The count is recorded in
/// every CSV row.
/// </remarks>
[TestFixture]
[Category("Benchmark")]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[Category(MssqlCiShards.Shard1)]
public class Given_MssqlReferenceResolverBenchmark
{
    private const int BulkReferenceCount = 2500;
    private const int WarmupIterations = 3;
    private const int TimedIterations = 20;

    private MssqlReferenceResolverTestDatabase _database = null!;
    private ServiceProvider _hashResolverProvider = null!;
    private ServiceProvider _naturalKeyResolverProvider = null!;
    private ReferenceResolverBenchmarkWorkload _workload = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        if (!ReferenceResolverBenchmarkRecorder.IsEnabled)
        {
            Assert.Ignore(
                $"Benchmark fixture; set {ReferenceResolverBenchmarkRecorder.EnabledVariable}=1 to run."
            );
        }

        if (!MssqlTestDatabaseHelper.IsConfigured())
        {
            Assert.Ignore(
                "SQL Server integration tests require a MssqlAdmin connection string in appsettings.Test.json"
            );
        }

        _database = await MssqlReferenceResolverTestDatabase.CreateProvisionedAsync();
        _hashResolverProvider = CreateServiceProvider(useNaturalKeyResolver: false);
        _naturalKeyResolverProvider = CreateServiceProvider(useNaturalKeyResolver: true);
        _workload = new ReferenceResolverBenchmarkWorkload(
            _database.Fixture,
            _database.MappingSet,
            BulkReferenceCount
        );

        await _database.ResetAsync();
        await _database.SeedAsync();
        await _database.SeedAsync(_workload.CreateSeedData());
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_hashResolverProvider is not null)
        {
            await _hashResolverProvider.DisposeAsync();
        }

        if (_naturalKeyResolverProvider is not null)
        {
            await _naturalKeyResolverProvider.DisposeAsync();
        }

        if (_database is not null)
        {
            await _database.DisposeAsync();
        }
    }

    [Test]
    public async Task It_measures_bulk_document_reference_resolution_on_both_resolvers()
    {
        await RunBenchmarkAsync(
            ReferenceResolverBenchmarkWorkload.BulkCase,
            _workload.BulkReferenceCount,
            _workload.CreateBulkDocumentReferences(),
            [],
            expectedDocumentHits: _workload.BulkReferenceCount,
            expectedDescriptorHits: 0
        );
    }

    [Test]
    public async Task It_measures_deep_identity_and_descriptor_resolution_on_both_resolvers()
    {
        await RunBenchmarkAsync(
            ReferenceResolverBenchmarkWorkload.DeepIdentityCase,
            ReferenceResolverBenchmarkWorkload.DeepIdentityBatchReferenceCount,
            _workload.CreateDeepIdentityDocumentReferences(),
            _workload.CreateDeepIdentityDescriptorReferences(),
            expectedDocumentHits: ReferenceResolverBenchmarkWorkload.DeepIdentityReferenceCount,
            expectedDescriptorHits: ReferenceResolverBenchmarkWorkload.DescriptorReferenceCount
        );
    }

    /// <summary>
    /// Diagnostic, not a spec case: the same mix at the size a real document write resolves, so a gate
    /// verdict can be read as "at every size" or "only in bulk".
    /// </summary>
    [Test]
    public async Task It_measures_small_batch_mixed_reference_resolution_on_both_resolvers()
    {
        await RunBenchmarkAsync(
            ReferenceResolverBenchmarkWorkload.SmallBatchCase,
            ReferenceResolverBenchmarkWorkload.SmallBatchReferenceCount,
            _workload.CreateDeepIdentityDocumentReferences(
                ReferenceResolverBenchmarkWorkload.SmallBatchReferenceCountPerKind
            ),
            _workload.CreateDeepIdentityDescriptorReferences(
                ReferenceResolverBenchmarkWorkload.SmallBatchReferenceCountPerKind
            ),
            expectedDocumentHits: ReferenceResolverBenchmarkWorkload.SmallBatchReferenceCountPerKind,
            expectedDescriptorHits: ReferenceResolverBenchmarkWorkload.SmallBatchReferenceCountPerKind
        );
    }

    private async Task RunBenchmarkAsync(
        string benchmarkCase,
        int referenceCount,
        IReadOnlyList<DocumentReference> documentReferences,
        IReadOnlyList<DescriptorReference> descriptorReferences,
        int expectedDocumentHits,
        int expectedDescriptorHits
    )
    {
        for (var warmup = 0; warmup < WarmupIterations; warmup++)
        {
            await ResolveAsync(_hashResolverProvider, documentReferences, descriptorReferences);
            await ResolveAsync(_naturalKeyResolverProvider, documentReferences, descriptorReferences);
        }

        List<TimeSpan> hashTimings = [];
        List<TimeSpan> naturalKeyTimings = [];

        for (var iteration = 1; iteration <= TimedIterations; iteration++)
        {
            hashTimings.Add(
                await MeasureAsync(
                    _hashResolverProvider,
                    ReferenceResolverBenchmarkRecorder.HashArm,
                    benchmarkCase,
                    referenceCount,
                    iteration,
                    documentReferences,
                    descriptorReferences,
                    expectedDocumentHits,
                    expectedDescriptorHits
                )
            );
            naturalKeyTimings.Add(
                await MeasureAsync(
                    _naturalKeyResolverProvider,
                    ReferenceResolverBenchmarkRecorder.NaturalKeyArm,
                    benchmarkCase,
                    referenceCount,
                    iteration,
                    documentReferences,
                    descriptorReferences,
                    expectedDocumentHits,
                    expectedDescriptorHits
                )
            );
        }

        ReportSummary(benchmarkCase, referenceCount, ReferenceResolverBenchmarkRecorder.HashArm, hashTimings);
        ReportSummary(
            benchmarkCase,
            referenceCount,
            ReferenceResolverBenchmarkRecorder.NaturalKeyArm,
            naturalKeyTimings
        );
    }

    private async Task<TimeSpan> MeasureAsync(
        ServiceProvider serviceProvider,
        string arm,
        string benchmarkCase,
        int referenceCount,
        int iteration,
        IReadOnlyList<DocumentReference> documentReferences,
        IReadOnlyList<DescriptorReference> descriptorReferences,
        int expectedDocumentHits,
        int expectedDescriptorHits
    )
    {
        using var scope = serviceProvider.CreateScope();
        var resolver = CreateResolver(scope);
        var request = CreateRequest(documentReferences, descriptorReferences);

        var stopwatch = Stopwatch.StartNew();
        var result = await resolver.ResolveAsync(request);
        stopwatch.Stop();

        AssertFullyResolved(result, arm, benchmarkCase, expectedDocumentHits, expectedDescriptorHits);

        ReferenceResolverBenchmarkRecorder.Record(
            ReferenceResolverBenchmarkRecorder.MssqlEngine,
            arm,
            benchmarkCase,
            referenceCount,
            iteration,
            stopwatch.Elapsed
        );

        return stopwatch.Elapsed;
    }

    private async Task<ResolvedReferenceSet> ResolveAsync(
        ServiceProvider serviceProvider,
        IReadOnlyList<DocumentReference> documentReferences,
        IReadOnlyList<DescriptorReference> descriptorReferences
    )
    {
        using var scope = serviceProvider.CreateScope();

        return await CreateResolver(scope)
            .ResolveAsync(CreateRequest(documentReferences, descriptorReferences));
    }

    private IReferenceResolver CreateResolver(IServiceScope scope)
    {
        var instanceSelection = scope.ServiceProvider.GetRequiredService<IDataStoreSelection>();
        instanceSelection.SetSelectedDataStore(
            new DataStore(
                Id: 1,
                DataStoreType: "test",
                Name: "MssqlReferenceResolverBenchmark",
                ConnectionString: _database.ConnectionString,
                RouteContext: []
            )
        );

        return scope.ServiceProvider.GetRequiredService<IReferenceResolver>();
    }

    private ReferenceResolverRequest CreateRequest(
        IReadOnlyList<DocumentReference> documentReferences,
        IReadOnlyList<DescriptorReference> descriptorReferences
    ) =>
        new(
            MappingSet: _database.MappingSet,
            RequestResource: _database.Fixture.RequestResource,
            DocumentReferences: documentReferences,
            DescriptorReferences: descriptorReferences
        );

    private static void AssertFullyResolved(
        ResolvedReferenceSet result,
        string arm,
        string benchmarkCase,
        int expectedDocumentHits,
        int expectedDescriptorHits
    )
    {
        result
            .SuccessfulDocumentReferencesByPath.Should()
            .HaveCount(
                expectedDocumentHits,
                "the {0} arm must resolve every document reference in the {1} batch",
                arm,
                benchmarkCase
            );
        result
            .SuccessfulDescriptorReferencesByPath.Should()
            .HaveCount(
                expectedDescriptorHits,
                "the {0} arm must resolve every descriptor reference in the {1} batch",
                arm,
                benchmarkCase
            );
        result
            .HasFailures.Should()
            .BeFalse("the {0} arm reported unresolved references for {1}", arm, benchmarkCase);
    }

    private static void ReportSummary(
        string benchmarkCase,
        int referenceCount,
        string arm,
        IReadOnlyList<TimeSpan> timings
    )
    {
        TestContext.Out.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"[benchmark] engine=mssql case={benchmarkCase} arm={arm} references={referenceCount} "
                    + $"iterations={timings.Count} "
                    + $"medianMs={ReferenceResolverBenchmarkRecorder.Median(timings).TotalMilliseconds:0.000} "
                    + $"p95Ms={ReferenceResolverBenchmarkRecorder.Percentile(timings, 0.95).TotalMilliseconds:0.000}"
            )
        );
    }

    private static ServiceProvider CreateServiceProvider(bool useNaturalKeyResolver)
    {
        var services = new ServiceCollection();

        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddScoped<IDataStoreSelection, DataStoreSelection>();
        services.AddTestReadableProfileProjector();

        if (useNaturalKeyResolver)
        {
            services.AddMssqlNaturalKeyReferenceResolver();
        }
        else
        {
            services.AddMssqlReferenceResolver();
        }

        return services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }
        );
    }
}

// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using System.Globalization;
using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

/// <summary>
/// The Phase 3 cutover gate on PostgreSQL: the same request resolved by the old
/// <c>dms.ReferentialIdentity</c> hash resolver and by the natural-key resolver, over ONE seeded database,
/// with per-iteration wall times recorded to CSV.
/// </summary>
/// <remarks>
/// Opt-in twice over — the fixture ignores itself unless <c>DMS_RESOLVER_BENCHMARK=1</c>, and the recorder
/// writes nothing unless <c>DMS_RESOLVER_BENCHMARK_PATH</c> names a file — so a default test run reports
/// these as Ignored and costs nothing. Nothing here asserts on timing: the assertions are correctness
/// invariants (both arms resolve every reference, every iteration), and the numbers are the deliverable.
/// </remarks>
[TestFixture]
[Category("Benchmark")]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
public class Given_PostgresqlReferenceResolverBenchmark
{
    /// <summary>The established bulk size for this venue (<c>LargeLookupCount</c> in the resolver suites).</summary>
    private const int BulkReferenceCount = 4096;

    private const int WarmupIterations = 3;
    private const int TimedIterations = 20;

    private PostgresqlReferenceResolverTestDatabase _database = null!;
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

        _database = await PostgresqlReferenceResolverTestDatabase.CreateProvisionedAsync();
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
            ReferenceResolverBenchmarkRecorder.PostgresqlEngine,
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
                Name: "PostgresqlReferenceResolverBenchmark",
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
                $"[benchmark] engine=postgresql case={benchmarkCase} arm={arm} references={referenceCount} "
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
        services.AddSingleton<NpgsqlDataSourceCache>();
        services.AddScoped<IDataStoreSelection, DataStoreSelection>();
        services.AddScoped<NpgsqlDataSourceProvider>();
        services.AddTestReadableProfileProjector();

        if (useNaturalKeyResolver)
        {
            services.AddPostgresqlNaturalKeyReferenceResolver();
        }
        else
        {
            services.AddPostgresqlReferenceResolver();
        }

        return services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }
        );
    }
}

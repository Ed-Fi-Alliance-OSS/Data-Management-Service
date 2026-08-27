// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Globalization;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using EdFi.DataManagementService.Core.Backend;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Extraction;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

/// <summary>
/// Measures warm, steady-state, end-to-end write latency against live PostgreSQL, which is this story's
/// acceptance gate. Explicit, because it is a measurement rather than a regression assertion: it reports
/// percentiles for comparison against the recorded baseline and would only add noise to CI.
/// </summary>
[TestFixture]
[Explicit("Latency measurement gate; run deliberately and compare against the recorded baseline.")]
[NonParallelizable]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
public class Given_A_Postgresql_Warm_Steady_State_Write_Latency_Measurement
{
    private const string FixtureRelativePath =
        "src/dms/backend/EdFi.DataManagementService.Backend.Ddl.Tests.Unit/Fixtures/focused/stable-key-update-semantics";

    private const int WarmupIterations = 20;
    private const int MeasuredIterations = 100;

    private static readonly ResourceInfo _schoolResourceInfo = new(
        ProjectName: new ProjectName("Ed-Fi"),
        ResourceName: new ResourceName("School"),
        IsDescriptor: false,
        ResourceVersion: new SemVer("1.0.0"),
        AllowIdentityUpdates: false
    );

    private MappingSet _mappingSet = null!;
    private PostgresqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        var fixture = PostgresqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(FixtureRelativePath);
        _mappingSet = fixture.MappingSet;
        _database = await PostgresqlGeneratedDdlTestDatabase.CreateProvisionedAsync(fixture.GeneratedDdl);
        _serviceProvider = CreateServiceProvider();
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_serviceProvider is not null)
        {
            await _serviceProvider.DisposeAsync();
            _serviceProvider = null!;
        }

        if (_database is not null)
        {
            await _database.DisposeAsync();
            _database = null!;
        }
    }

    [Test]
    public async Task It_reports_warm_steady_state_latency_for_the_batched_write_verbs()
    {
        var createSample = await WriteLatencyMeasurement.MeasureAsync(
            "POST create, 2 collection rows",
            async iteration =>
                (await UpsertAsync(iteration, CreateBody(iteration, addressCount: 2)))
                    .Should()
                    .BeOfType<UpsertResult.InsertSuccess>(),
            WarmupIterations,
            MeasuredIterations
        );

        var updateSample = await MeasureUpdateAsync();
        var deleteSample = await MeasureDeleteAsync();

        foreach (var sample in new[] { createSample, updateSample, deleteSample })
        {
            await TestContext.Out.WriteLineAsync(sample.ToReportLine());
        }
    }

    /// <summary>
    /// One seeded document updated repeatedly, alternating its collection row count so every iteration
    /// owes real statements rather than resolving to a guarded no-op.
    /// </summary>
    private async Task<WriteLatencySample> MeasureUpdateAsync()
    {
        const int updateSchoolId = 900_001;
        var documentUuid = new DocumentUuid(Guid.NewGuid());
        (await UpsertAsync(updateSchoolId, CreateBody(updateSchoolId, addressCount: 2), documentUuid))
            .Should()
            .BeOfType<UpsertResult.InsertSuccess>();

        return await WriteLatencyMeasurement.MeasureAsync(
            "PUT changed, 2 rows alternating to 3",
            async iteration =>
            {
                var body = CreateBody(updateSchoolId, addressCount: iteration % 2 == 0 ? 3 : 2);

                (await UpdateAsync(updateSchoolId, body, documentUuid))
                    .Should()
                    .BeOfType<UpdateResult.UpdateSuccess>();
            },
            WarmupIterations,
            MeasuredIterations
        );
    }

    /// <summary>
    /// Deletes documents seeded before the measured window, so the timed region is the delete alone.
    /// </summary>
    private async Task<WriteLatencySample> MeasureDeleteAsync()
    {
        const int deleteSchoolIdBase = 910_000;
        var documentUuids = new DocumentUuid[WarmupIterations + MeasuredIterations];

        for (var index = 0; index < documentUuids.Length; index++)
        {
            var schoolId = deleteSchoolIdBase + index;
            documentUuids[index] = new DocumentUuid(Guid.NewGuid());

            (await UpsertAsync(schoolId, CreateBody(schoolId, addressCount: 2), documentUuids[index]))
                .Should()
                .BeOfType<UpsertResult.InsertSuccess>();
        }

        return await WriteLatencyMeasurement.MeasureAsync(
            "DELETE, no precondition",
            async iteration =>
                (await DeleteAsync(documentUuids[iteration])).Should().BeOfType<DeleteResult.DeleteSuccess>(),
            WarmupIterations,
            MeasuredIterations
        );
    }

    private async Task<UpsertResult> UpsertAsync(
        int schoolId,
        JsonNode body,
        DocumentUuid? documentUuid = null
    )
    {
        using var scope = CreateSelectedScope();

        return await scope
            .ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>()
            .UpsertDocument(
                new UpsertRequest(
                    ResourceInfo: _schoolResourceInfo,
                    DocumentInfo: CreateDocumentInfo(schoolId),
                    MappingSet: _mappingSet,
                    EdfiDoc: body,
                    Headers: [],
                    TraceId: new TraceId("pg-write-latency-upsert"),
                    DocumentUuid: documentUuid ?? new DocumentUuid(Guid.NewGuid())
                )
            );
    }

    private async Task<UpdateResult> UpdateAsync(int schoolId, JsonNode body, DocumentUuid documentUuid)
    {
        using var scope = CreateSelectedScope();

        return await scope
            .ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>()
            .UpdateDocumentById(
                new UpdateRequest(
                    ResourceInfo: _schoolResourceInfo,
                    DocumentInfo: CreateDocumentInfo(schoolId),
                    MappingSet: _mappingSet,
                    EdfiDoc: body,
                    Headers: [],
                    TraceId: new TraceId("pg-write-latency-update"),
                    DocumentUuid: documentUuid
                )
            );
    }

    private async Task<DeleteResult> DeleteAsync(DocumentUuid documentUuid)
    {
        using var scope = CreateSelectedScope();

        return await scope
            .ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>()
            .DeleteDocumentById(
                new DeleteRequest(
                    DocumentUuid: documentUuid,
                    ResourceInfo: _schoolResourceInfo,
                    TraceId: new TraceId("pg-write-latency-delete"),
                    Headers: [],
                    MappingSet: _mappingSet
                )
            );
    }

    private static JsonNode CreateBody(int schoolId, int addressCount)
    {
        JsonArray addresses = [];

        for (var index = 0; index < addressCount; index++)
        {
            addresses.Add(new JsonObject { ["city"] = $"City{index}" });
        }

        return new JsonObject
        {
            ["schoolId"] = schoolId,
            ["shortName"] = "LATENCY",
            ["addresses"] = addresses,
        };
    }

    private static DocumentInfo CreateDocumentInfo(int schoolId)
    {
        var identity = new DocumentIdentity([
            new DocumentIdentityElement(
                new JsonPath("$.schoolId"),
                schoolId.ToString(CultureInfo.InvariantCulture)
            ),
        ]);

        return new DocumentInfo(
            DocumentIdentity: identity,
            ReferentialId: ReferentialIdCalculator.ReferentialIdFrom(_schoolResourceInfo, identity),
            DocumentReferences: [],
            DocumentReferenceArrays: [],
            DescriptorReferences: [],
            SuperclassIdentity: null
        );
    }

    /// <summary>
    /// The production wiring, with no recording decorator: the gate measures what a request actually pays.
    /// </summary>
    private ServiceProvider CreateServiceProvider()
    {
        ServiceCollection services = [];

        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<NpgsqlDataSourceCache>();
        services.AddScoped<IDataStoreSelection, DataStoreSelection>();
        services.AddScoped<NpgsqlDataSourceProvider>();
        services.Configure<DatabaseOptions>(options => options.IsolationLevel = IsolationLevel.ReadCommitted);
        services.AddTestReadableProfileProjector();
        services.AddScoped<RelationalDocumentStoreRepository>();
        // The reference resolver brings the document cache writer's retry adapter with it, and that
        // adapter takes the deadlock retry settings as a constructed dependency.
        services.AddSingleton(new DeadlockRetrySettings());
        services.AddPostgresqlBackendIntegrationTestServices();
        services.AddScoped<IRelationalWriteSessionFactory, PostgresqlRelationalWriteSessionFactory>();

        return services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }
        );
    }

    private IServiceScope CreateSelectedScope()
    {
        var scope = _serviceProvider.CreateScope();

        scope
            .ServiceProvider.GetRequiredService<IDataStoreSelection>()
            .SetSelectedDataStore(
                new DataStore(
                    Id: 1,
                    DataStoreType: "test",
                    Name: "PostgresqlWriteLatency",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        return scope;
    }
}

// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Postgresql;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Core.External.Interface;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NUnit.Framework;
using SetResult = EdFi.DataManagementService.Backend.External.EducationOrganizationProjectionSetResult;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit;

/// <summary>
/// The projection pipeline as the real host composes it: the production container, the real
/// <see cref="IApiService"/> and every Core step, with only the external boundaries replaced
/// (<see cref="EducationOrganizationProjectionTestHost"/>).
/// </summary>
/// <remarks>
/// The recording set reader stands where the provider reader stands, in the request scope, and reads
/// what the provider reader's acquisition reads: the request's effective target, and the PostgreSQL
/// data source the production <see cref="NpgsqlDataSourceProvider"/> leases for it. The store also
/// configures a snapshot and a read replica, and the request asks for the snapshot, so a pipeline that
/// did not pin the primary would be seen reading another database.
/// </remarks>
[TestFixture]
public class Given_A_Projection_Request_Through_The_Production_Pipeline
{
    private const long DataStoreId = 3788;
    private const string PrimaryConnectionString = "Host=primary.invalid;Database=projection_primary";
    private const string SnapshotConnectionString = "Host=snapshot.invalid;Database=projection_snapshot";
    private const string ReplicaConnectionString = "Host=replica.invalid;Database=projection_replica";

    private WebApplicationFactory<Program> _factory = null!;
    private readonly ConcurrentQueue<EffectiveDataStoreTarget> _fingerprintTargets = new();
    private readonly ConcurrentQueue<(EffectiveDataStoreTarget Target, string? Database)> _readerTargets =
        new();
    private IFrontendResponse _response = null!;

    [OneTimeSetUp]
    public async Task Setup()
    {
        var store = new DataStore(
            DataStoreId,
            "Primary",
            "Projection pipeline store",
            PrimaryConnectionString,
            [],
            RelationalProviderToken.Postgresql,
            RelationalProviderMetadataStatus.Supported,
            [
                new(DataStoreDerivativeType.Snapshot, SnapshotConnectionString),
                new(DataStoreDerivativeType.ReadReplica, ReplicaConnectionString),
            ]
        );

        _factory = EducationOrganizationProjectionTestHost.Create(
            store,
            serviceProvider => new RecordingSetReader(serviceProvider, _readerTargets),
            serviceProvider => new EducationOrganizationProjectionTestHost.MatchingFingerprintReader(
                serviceProvider,
                _fingerprintTargets
            )
        );

        IApiService apiService = _factory.Services.GetRequiredService<IApiService>();

        _response = await apiService.GetEducationOrganizationProjection(
            new FrontendRequest(
                Path: "/management/education-organizations",
                Body: null,
                Form: null,
                Headers: new Dictionary<string, string>
                {
                    ["Authorization"] = "Bearer projection-pipeline-token",
                    ["Use-Snapshot"] = "true",
                },
                QueryParameters: new Dictionary<string, string>
                {
                    ["dataStoreId"] = DataStoreId.ToString(),
                    ["limit"] = "2",
                },
                TraceId: new TraceId("projection-pipeline"),
                RouteQualifiers: []
            ),
            CancellationToken.None
        );
    }

    [OneTimeTearDown]
    public void TearDown() => _factory.Dispose();

    [Test]
    public void It_answers_the_first_page()
    {
        _response.StatusCode.Should().Be(200, _response.Body?.ToJsonString());
        _response.Body!["items"]!
            .AsArray()
            .Select(item => item!["educationOrganizationId"]!.GetValue<long>())
            .Should()
            .Equal(1, 10);
    }

    [Test]
    public void It_reads_the_set_from_the_resolved_store_primary_database()
    {
        _readerTargets.Should().ContainSingle();
        (EffectiveDataStoreTarget target, string? database) = _readerTargets.Single();

        target.Should().Be(EffectiveDataStoreTarget.Primary(PrimaryConnectionString));
        database.Should().Be("projection_primary");
    }

    [Test]
    public void It_validates_the_fingerprint_of_the_primary_database() =>
        _fingerprintTargets
            .Should()
            .Contain(EffectiveDataStoreTarget.Primary(PrimaryConnectionString))
            .And.NotContain(target => target.Kind != EffectiveTargetKind.Primary);

    /// <summary>
    /// Stands in for the provider reader inside the request scope and records what its acquisition
    /// would open: the effective target, and the database of the data source the scope's
    /// <see cref="NpgsqlDataSourceProvider"/> leases for it. No connection is opened.
    /// </summary>
    private sealed class RecordingSetReader(
        IServiceProvider scope,
        ConcurrentQueue<(EffectiveDataStoreTarget Target, string? Database)> targets
    ) : IEducationOrganizationProjectionSetReader
    {
        public Task<SetResult> ReadSetAsync(
            EducationOrganizationProjectionSetReadRequest request,
            CancellationToken cancellationToken
        )
        {
            EffectiveDataStoreTarget target = scope
                .GetRequiredService<IDataStoreSelection>()
                .GetEffectiveTarget();
            string? database = new NpgsqlConnectionStringBuilder(
                scope.GetRequiredService<NpgsqlDataSourceProvider>().DataSource.ConnectionString
            ).Database;
            targets.Enqueue((target, database));

            return Task.FromResult<SetResult>(
                new SetResult.Set([
                    new EducationOrganizationProjectionRow(
                        1,
                        "Ed-Fi:StateEducationAgency",
                        "Sea",
                        null,
                        null,
                        null,
                        null,
                        null
                    ),
                    new EducationOrganizationProjectionRow(
                        10,
                        "Ed-Fi:EducationServiceCenter",
                        "Esc",
                        null,
                        null,
                        null,
                        null,
                        1
                    ),
                    new EducationOrganizationProjectionRow(
                        100,
                        "Ed-Fi:LocalEducationAgency",
                        "Lea",
                        null,
                        null,
                        null,
                        10,
                        1
                    ),
                ])
            );
        }
    }
}

// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Globalization;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Mssql;
using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using EdFi.DataManagementService.Core.Backend;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[Category(MssqlCiShards.Shard2)]
public class Given_A_Mssql_NoProfile_Post_With_Storage_Collapsed_Sibling_Identities
{
    // Exercises the no-profile path's flatten-time storage-collapsed uniqueness check.
    // The stable-key-update-semantics fixture defines School with $.addresses[*] whose
    // array uniqueness constraint keys on "city". A nullable identity slot (city) lets
    // two siblings that differ only by absent-vs-explicit-null reach the flattener with
    // the same storage-shape key. The no-profile executor sets
    // ValidateStorageCollapsedCollectionIdentityUniqueness, which causes the flattener
    // to reject such pairs before any database write.

    private const long SchoolId = 255903;
    private static readonly DocumentUuid SchoolDocumentUuid = new(
        Guid.Parse("dddd0003-0000-0000-0000-000000000001")
    );

    private static readonly ResourceInfo SchoolResourceInfo = new(
        ProjectName: new ProjectName("Ed-Fi"),
        ResourceName: new ResourceName("School"),
        IsDescriptor: false,
        ResourceVersion: new SemVer("1.0.0"),
        AllowIdentityUpdates: false
    );

    private MssqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private MssqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        if (!MssqlTestDatabaseHelper.IsConfigured())
        {
            Assert.Ignore(
                "SQL Server integration tests require a MssqlAdmin connection string in appsettings.Test.json"
            );
        }

        _fixture = MssqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            MssqlProfileTopLevelCollectionMergeSupport.FixtureRelativePath
        );
        _mappingSet = _fixture.MappingSet;
        _database = await MssqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
    }

    [SetUp]
    public async Task SetUp()
    {
        await _database.ResetAsync();
        _serviceProvider = CreateServiceProvider();
    }

    [TearDown]
    public async Task TearDown()
    {
        if (_serviceProvider is not null)
        {
            await _serviceProvider.DisposeAsync();
            _serviceProvider = null!;
        }
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_database is not null)
        {
            await _database.DisposeAsync();
            _database = null!;
        }
    }

    [Test]
    public async Task It_returns_validation_failure_when_request_contains_absent_and_explicit_null_identity_siblings()
    {
        // Two addresses siblings whose "city" identity slot differs only by
        // absent-vs-explicit-null. Both collapse to SQL NULL in storage, so the
        // flattener must reject the pair before any database round-trip.
        var body = new JsonObject
        {
            ["schoolId"] = checked((int)SchoolId),
            ["addresses"] = new JsonArray(
                // Sibling A: city property is absent.
                new JsonObject(),
                // Sibling B: city property is present as explicit JSON null.
                new JsonObject { ["city"] = JsonValue.Create<string?>(null) }
            ),
        };

        var result = await ExecuteUpsertAsync(body);

        result.Should().BeOfType<UpsertResult.UpsertFailureValidation>();
        var failure = (UpsertResult.UpsertFailureValidation)result;
        failure.ValidationFailures.Should().ContainSingle();
        failure.ValidationFailures[0].Path.Value.Should().Be("$.addresses");
        failure
            .ValidationFailures[0]
            .Message.Should()
            .Be(
                "The 2nd item of the addresses has the same identifying values as another item earlier in the list."
            );
    }

    [Test]
    public async Task It_returns_validation_failure_when_request_contains_case_variant_identity_siblings()
    {
        // City is a string semantic-identity member, so SQL Server stores it under the
        // case-insensitive identity collation: "Austin" and "AUSTIN" would collide on the
        // collection's unique key. The flattener must reject the pair as a duplicate (400)
        // before any database write, instead of letting the unique violation surface.
        var body = new JsonObject
        {
            ["schoolId"] = checked((int)SchoolId),
            ["addresses"] = new JsonArray(
                new JsonObject { ["city"] = "Austin" },
                new JsonObject { ["city"] = "AUSTIN" }
            ),
        };

        var result = await ExecuteUpsertAsync(body);

        result.Should().BeOfType<UpsertResult.UpsertFailureValidation>();
        var failure = (UpsertResult.UpsertFailureValidation)result;
        failure.ValidationFailures.Should().ContainSingle();
        failure.ValidationFailures[0].Path.Value.Should().Be("$.addresses");
        failure
            .ValidationFailures[0]
            .Message.Should()
            .Be(
                "The 2nd item of the addresses has the same identifying values as another item earlier in the list."
            );
        (await MssqlProfileTopLevelCollectionMergeSupport.ReadDocumentCountAsync(_database)).Should().Be(0);
    }

    [Test]
    public async Task It_returns_non_unique_values_when_siblings_collide_only_under_the_database_collation()
    {
        // "Straße" and "Strasse" differ under the in-process OrdinalIgnoreCase comparer, so the
        // flattener lets them through, but the identity collation equates them and the
        // collection's unique key rejects the second row. That unattributed unique violation
        // takes the generic non-unique-values fallback instead of a 500.
        var body = new JsonObject
        {
            ["schoolId"] = checked((int)SchoolId),
            ["addresses"] = new JsonArray(
                new JsonObject { ["city"] = "Straße" },
                new JsonObject { ["city"] = "Strasse" }
            ),
        };

        var result = await ExecuteUpsertAsync(body);

        result.Should().BeOfType<UpsertResult.UpsertFailureNonUniqueValues>();
        (await MssqlProfileTopLevelCollectionMergeSupport.ReadDocumentCountAsync(_database)).Should().Be(0);
    }

    private async Task<UpsertResult> ExecuteUpsertAsync(JsonNode body)
    {
        using var scope = _serviceProvider.CreateScope();
        scope
            .ServiceProvider.GetRequiredService<IDataStoreSelection>()
            .SetSelectedDataStore(
                new DataStore(
                    Id: 1,
                    DataStoreType: "test",
                    Name: "MssqlNoProfileAmbiguousStorageCollapsed",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var identity = new DocumentIdentity([
            new DocumentIdentityElement(
                new JsonPath("$.schoolId"),
                SchoolId.ToString(CultureInfo.InvariantCulture)
            ),
        ]);
        var documentInfo = new DocumentInfo(
            DocumentIdentity: identity,
            DocumentReferences: [],
            DocumentReferenceArrays: [],
            DescriptorReferences: [],
            SuperclassIdentity: null
        );

        var upsertRequest = new UpsertRequest(
            ResourceInfo: SchoolResourceInfo,
            DocumentInfo: documentInfo,
            MappingSet: _mappingSet,
            EdfiDoc: body,
            Headers: [],
            TraceId: new TraceId("mssql-no-profile-storage-collapsed"),
            DocumentUuid: SchoolDocumentUuid
        )
        {
            ActionAuthorization =
                UpsertActionAuthorizationTestSupport.NoFurtherAuthorizationRequiredForCreateAndUpdate,
        };

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();
        return await repository.UpsertDocument(upsertRequest);
    }

    private ServiceProvider CreateServiceProvider()
    {
        ServiceCollection services = [];

        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddScoped<IDataStoreSelection, DataStoreSelection>();
        services.Configure<DatabaseOptions>(options => options.IsolationLevel = IsolationLevel.ReadCommitted);
        services.AddTestReadableProfileProjector();
        services.AddScoped<RelationalDocumentStoreRepository>();
        services.AddMssqlBackendIntegrationTestServices();

        return services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }
        );
    }
}

// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Core.Backend;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Extraction;
using EdFi.DataManagementService.Old.Postgresql;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

file sealed class NoOpHostApplicationLifetime : IHostApplicationLifetime
{
    public CancellationToken ApplicationStarted => CancellationToken.None;
    public CancellationToken ApplicationStopping => CancellationToken.None;
    public CancellationToken ApplicationStopped => CancellationToken.None;

    public void StopApplication() { }
}

file sealed class AllowAllResourceAuthorizationHandler : IResourceAuthorizationHandler
{
    public Task<ResourceAuthorizationResult> Authorize(
        DocumentSecurityElements documentSecurityElements,
        OperationType operationType,
        TraceId traceId
    ) => Task.FromResult<ResourceAuthorizationResult>(new ResourceAuthorizationResult.Authorized());
}

file sealed class NoOpUpdateCascadeHandler : IUpdateCascadeHandler
{
    public UpdateCascadeResult Cascade(
        JsonElement originalEdFiDoc,
        ProjectName originalDocumentProjectName,
        ResourceName originalDocumentResourceName,
        JsonNode modifiedEdFiDoc,
        JsonNode referencingEdFiDoc,
        long referencingDocumentId,
        short referencingDocumentPartitionKey,
        Guid referencingDocumentUuid,
        ProjectName referencingProjectName,
        ResourceName referencingResourceName
    ) =>
        new(
            OriginalEdFiDoc: referencingEdFiDoc,
            ModifiedEdFiDoc: referencingEdFiDoc,
            Id: referencingDocumentId,
            DocumentPartitionKey: referencingDocumentPartitionKey,
            DocumentUuid: referencingDocumentUuid,
            ProjectName: referencingProjectName,
            ResourceName: referencingResourceName,
            isIdentityUpdate: false
        );
}

internal sealed record PersistedDocumentRow(
    long DocumentId,
    Guid DocumentUuid,
    short ResourceKeyId,
    long ContentVersion
);

internal sealed record PersistedSchoolRow(long DocumentId, long SchoolId);

internal sealed record PersistedSchoolAddressRow(
    long CollectionItemId,
    long SchoolDocumentId,
    int Ordinal,
    string City
);

internal sealed record PersistedSchoolAddressPeriodRow(
    long CollectionItemId,
    long SchoolDocumentId,
    long ParentCollectionItemId,
    int Ordinal,
    string PeriodName
);

internal sealed record PersistedSchoolExtensionRow(long DocumentId, string CampusCode);

internal sealed record PersistedSchoolExtensionAddressRow(
    long BaseCollectionItemId,
    long SchoolDocumentId,
    string Zone
);

internal sealed record PersistedSchoolExtensionInterventionRow(
    long CollectionItemId,
    long SchoolDocumentId,
    int Ordinal,
    string InterventionCode
);

internal sealed record PersistedSchoolExtensionInterventionVisitRow(
    long CollectionItemId,
    long SchoolDocumentId,
    long ParentCollectionItemId,
    int Ordinal,
    string VisitCode
);

[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
public class Given_A_Postgresql_Relational_Write_Create_Baseline_With_A_Focused_Stable_Key_Fixture
{
    private const string FixtureRelativePath =
        "src/dms/backend/EdFi.DataManagementService.Backend.Ddl.Tests.Unit/Fixtures/focused/stable-key-extension-child-collections";

    private const string RequestBodyJson = """
        {
          "schoolId": 255901,
          "addresses": [
            {
              "city": "Austin",
              "periods": [
                {
                  "periodName": "Morning"
                },
                {
                  "periodName": "Afternoon"
                }
              ]
            },
            {
              "city": "Dallas",
              "periods": [
                {
                  "periodName": "Evening"
                }
              ]
            }
          ],
          "_ext": {
            "sample": {
              "campusCode": "North",
              "addresses": [
                {
                  "_ext": {
                    "sample": {
                      "zone": "Zone-1"
                    }
                  }
                },
                {
                  "_ext": {
                    "sample": {
                      "zone": "Zone-2"
                    }
                  }
                }
              ],
              "interventions": [
                {
                  "interventionCode": "Attendance",
                  "visits": [
                    {
                      "visitCode": "Visit-A"
                    },
                    {
                      "visitCode": "Visit-B"
                    }
                  ]
                },
                {
                  "interventionCode": "Behavior",
                  "visits": [
                    {
                      "visitCode": "Visit-C"
                    }
                  ]
                }
              ]
            }
          }
        }
        """;

    private static readonly QualifiedResourceName SchoolResource = new("Ed-Fi", "School");
    private static readonly ResourceInfo SchoolResourceInfo = new(
        ProjectName: new ProjectName("Ed-Fi"),
        ResourceName: new ResourceName("School"),
        IsDescriptor: false,
        ResourceVersion: new SemVer("1.0.0"),
        AllowIdentityUpdates: false,
        EducationOrganizationHierarchyInfo: new EducationOrganizationHierarchyInfo(false, 0, null),
        AuthorizationSecurableInfo: []
    );
    private static readonly DocumentUuid SchoolDocumentUuid = new(
        Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001")
    );

    private PostgresqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private PostgresqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;
    private UpsertResult _result = null!;
    private PersistedDocumentRow _persistedDocument = null!;
    private PersistedSchoolRow _persistedSchool = null!;
    private IReadOnlyList<PersistedSchoolAddressRow> _persistedAddresses = null!;
    private IReadOnlyList<PersistedSchoolAddressPeriodRow> _persistedAddressPeriods = null!;
    private PersistedSchoolExtensionRow _persistedSchoolExtension = null!;
    private IReadOnlyList<PersistedSchoolExtensionAddressRow> _persistedExtensionAddresses = null!;
    private IReadOnlyList<PersistedSchoolExtensionInterventionRow> _persistedInterventions = null!;
    private IReadOnlyList<PersistedSchoolExtensionInterventionVisitRow> _persistedInterventionVisits = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _fixture = PostgresqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(FixtureRelativePath);
        _mappingSet = _fixture.MappingSet;
        _database = await PostgresqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
    }

    [SetUp]
    public async Task Setup()
    {
        await _database.ResetAsync();
        _serviceProvider = CreateServiceProvider();

        _result = await ExecuteCreateAsync();

        _persistedDocument = await ReadDocumentAsync();
        _persistedSchool = await ReadSchoolAsync(_persistedDocument.DocumentId);
        _persistedAddresses = await ReadSchoolAddressesAsync(_persistedDocument.DocumentId);
        _persistedAddressPeriods = await ReadSchoolAddressPeriodsAsync(_persistedDocument.DocumentId);
        _persistedSchoolExtension = await ReadSchoolExtensionAsync(_persistedDocument.DocumentId);
        _persistedExtensionAddresses = await ReadSchoolExtensionAddressesAsync(_persistedDocument.DocumentId);
        _persistedInterventions = await ReadSchoolExtensionInterventionsAsync(_persistedDocument.DocumentId);
        _persistedInterventionVisits = await ReadSchoolExtensionInterventionVisitsAsync(
            _persistedDocument.DocumentId
        );
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
    public void It_returns_insert_success_for_the_repository_create_flow()
    {
        _result.Should().BeOfType<UpsertResult.InsertSuccess>();
        _result.As<UpsertResult.InsertSuccess>().NewDocumentUuid.Should().Be(SchoolDocumentUuid);
        _persistedDocument.DocumentUuid.Should().Be(SchoolDocumentUuid.Value);
        _persistedDocument.ResourceKeyId.Should().Be(_mappingSet.ResourceKeyIdByResource[SchoolResource]);
        _persistedDocument.ContentVersion.Should().BeGreaterThan(0);
    }

    [Test]
    public void It_persists_root_and_nested_collection_rows_with_stable_collection_ids()
    {
        _persistedSchool.DocumentId.Should().Be(_persistedDocument.DocumentId);
        _persistedSchool.SchoolId.Should().Be(255901);

        _persistedAddresses
            .Should()
            .Equal(
                new PersistedSchoolAddressRow(
                    _persistedAddresses[0].CollectionItemId,
                    _persistedDocument.DocumentId,
                    0,
                    "Austin"
                ),
                new PersistedSchoolAddressRow(
                    _persistedAddresses[1].CollectionItemId,
                    _persistedDocument.DocumentId,
                    1,
                    "Dallas"
                )
            );
        _persistedAddresses.Select(static row => row.CollectionItemId).Should().OnlyHaveUniqueItems();
        _persistedAddresses.Select(static row => row.CollectionItemId).Should().OnlyContain(id => id > 0);

        _persistedAddressPeriods
            .Should()
            .Equal(
                new PersistedSchoolAddressPeriodRow(
                    _persistedAddressPeriods[0].CollectionItemId,
                    _persistedDocument.DocumentId,
                    _persistedAddresses[0].CollectionItemId,
                    0,
                    "Morning"
                ),
                new PersistedSchoolAddressPeriodRow(
                    _persistedAddressPeriods[1].CollectionItemId,
                    _persistedDocument.DocumentId,
                    _persistedAddresses[0].CollectionItemId,
                    1,
                    "Afternoon"
                ),
                new PersistedSchoolAddressPeriodRow(
                    _persistedAddressPeriods[2].CollectionItemId,
                    _persistedDocument.DocumentId,
                    _persistedAddresses[1].CollectionItemId,
                    0,
                    "Evening"
                )
            );
        _persistedAddressPeriods.Select(static row => row.CollectionItemId).Should().OnlyHaveUniqueItems();
        _persistedAddressPeriods
            .Select(static row => row.CollectionItemId)
            .Should()
            .OnlyContain(id => id > 0);
    }

    [Test]
    public void It_persists_root_extensions_collection_extensions_and_extension_child_collections()
    {
        _persistedSchoolExtension
            .Should()
            .Be(new PersistedSchoolExtensionRow(_persistedDocument.DocumentId, "North"));

        _persistedExtensionAddresses
            .Should()
            .Equal(
                new PersistedSchoolExtensionAddressRow(
                    _persistedAddresses[0].CollectionItemId,
                    _persistedDocument.DocumentId,
                    "Zone-1"
                ),
                new PersistedSchoolExtensionAddressRow(
                    _persistedAddresses[1].CollectionItemId,
                    _persistedDocument.DocumentId,
                    "Zone-2"
                )
            );

        _persistedInterventions
            .Should()
            .Equal(
                new PersistedSchoolExtensionInterventionRow(
                    _persistedInterventions[0].CollectionItemId,
                    _persistedDocument.DocumentId,
                    0,
                    "Attendance"
                ),
                new PersistedSchoolExtensionInterventionRow(
                    _persistedInterventions[1].CollectionItemId,
                    _persistedDocument.DocumentId,
                    1,
                    "Behavior"
                )
            );
        _persistedInterventions.Select(static row => row.CollectionItemId).Should().OnlyHaveUniqueItems();
        _persistedInterventions.Select(static row => row.CollectionItemId).Should().OnlyContain(id => id > 0);

        _persistedInterventionVisits.Should().HaveCount(3);
        _persistedInterventionVisits
            .Should()
            .Equal(
                new PersistedSchoolExtensionInterventionVisitRow(
                    _persistedInterventionVisits[0].CollectionItemId,
                    _persistedDocument.DocumentId,
                    _persistedInterventions[0].CollectionItemId,
                    0,
                    "Visit-A"
                ),
                new PersistedSchoolExtensionInterventionVisitRow(
                    _persistedInterventionVisits[1].CollectionItemId,
                    _persistedDocument.DocumentId,
                    _persistedInterventions[0].CollectionItemId,
                    1,
                    "Visit-B"
                ),
                new PersistedSchoolExtensionInterventionVisitRow(
                    _persistedInterventionVisits[2].CollectionItemId,
                    _persistedDocument.DocumentId,
                    _persistedInterventions[1].CollectionItemId,
                    0,
                    "Visit-C"
                )
            );
    }

    private async Task<UpsertResult> ExecuteCreateAsync()
    {
        using var scope = _serviceProvider.CreateScope();

        scope
            .ServiceProvider.GetRequiredService<IDmsInstanceSelection>()
            .SetSelectedDmsInstance(
                new DmsInstance(
                    Id: 1,
                    InstanceType: "test",
                    InstanceName: "PostgresqlRelationalWriteCreateBaseline",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();
        return await repository.UpsertDocument(CreateUpsertRequest());
    }

    private UpsertRequest CreateUpsertRequest()
    {
        var schoolIdentity = new DocumentIdentity([
            new DocumentIdentityElement(new JsonPath("$.schoolId"), "255901"),
        ]);

        return new UpsertRequest(
            ResourceInfo: SchoolResourceInfo,
            DocumentInfo: new DocumentInfo(
                DocumentIdentity: schoolIdentity,
                ReferentialId: ReferentialIdCalculator.ReferentialIdFrom(SchoolResourceInfo, schoolIdentity),
                DocumentReferences: [],
                DocumentReferenceArrays: [],
                DescriptorReferences: [],
                SuperclassIdentity: null
            ),
            MappingSet: _mappingSet,
            EdfiDoc: JsonNode.Parse(RequestBodyJson)!,
            Headers: [],
            TraceId: new TraceId("pg-create-baseline"),
            DocumentUuid: SchoolDocumentUuid,
            DocumentSecurityElements: new([], [], [], [], []),
            UpdateCascadeHandler: new NoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new AllowAllResourceAuthorizationHandler(),
            ResourceAuthorizationPathways: []
        );
    }

    private static ServiceProvider CreateServiceProvider()
    {
        ServiceCollection services = new();

        services.AddSingleton<IHostApplicationLifetime, NoOpHostApplicationLifetime>();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<NpgsqlDataSourceCache>();
        services.AddScoped<IDmsInstanceSelection, DmsInstanceSelection>();
        services.AddScoped<NpgsqlDataSourceProvider>();
        services.Configure<DatabaseOptions>(options => options.IsolationLevel = IsolationLevel.ReadCommitted);
        services.AddTestReadableProfileProjector();
        services.AddScoped<RelationalDocumentStoreRepository>();
        services.AddPostgresqlReferenceResolver();

        return services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }
        );
    }

    private async Task<PersistedDocumentRow> ReadDocumentAsync()
    {
        var rows = await _database.QueryRowsAsync(
            """
            SELECT "DocumentId", "DocumentUuid", "ResourceKeyId", "ContentVersion"
            FROM "dms"."Document"
            WHERE "DocumentUuid" = @documentUuid;
            """,
            new NpgsqlParameter("documentUuid", SchoolDocumentUuid.Value)
        );

        return rows.Count == 1
            ? new PersistedDocumentRow(
                GetInt64(rows[0], "DocumentId"),
                GetGuid(rows[0], "DocumentUuid"),
                GetInt16(rows[0], "ResourceKeyId"),
                GetInt64(rows[0], "ContentVersion")
            )
            : throw new InvalidOperationException(
                $"Expected exactly one document row for '{SchoolDocumentUuid.Value}', but found {rows.Count}."
            );
    }

    private async Task<PersistedSchoolRow> ReadSchoolAsync(long documentId)
    {
        var rows = await _database.QueryRowsAsync(
            """
            SELECT "DocumentId", "SchoolId"
            FROM "edfi"."School"
            WHERE "DocumentId" = @documentId;
            """,
            new NpgsqlParameter("documentId", documentId)
        );

        return rows.Count == 1
            ? new PersistedSchoolRow(GetInt64(rows[0], "DocumentId"), GetInt64(rows[0], "SchoolId"))
            : throw new InvalidOperationException(
                $"Expected exactly one school row for document id '{documentId}', but found {rows.Count}."
            );
    }

    private async Task<IReadOnlyList<PersistedSchoolAddressRow>> ReadSchoolAddressesAsync(long documentId)
    {
        var rows = await _database.QueryRowsAsync(
            """
            SELECT "CollectionItemId", "School_DocumentId", "Ordinal", "City"
            FROM "edfi"."SchoolAddress"
            WHERE "School_DocumentId" = @documentId
            ORDER BY "Ordinal", "CollectionItemId";
            """,
            new NpgsqlParameter("documentId", documentId)
        );

        return rows.Select(row => new PersistedSchoolAddressRow(
                GetInt64(row, "CollectionItemId"),
                GetInt64(row, "School_DocumentId"),
                GetInt32(row, "Ordinal"),
                GetString(row, "City")
            ))
            .ToArray();
    }

    private async Task<IReadOnlyList<PersistedSchoolAddressPeriodRow>> ReadSchoolAddressPeriodsAsync(
        long documentId
    )
    {
        var rows = await _database.QueryRowsAsync(
            """
            SELECT "CollectionItemId", "School_DocumentId", "ParentCollectionItemId", "Ordinal", "PeriodName"
            FROM "edfi"."SchoolAddressPeriod"
            WHERE "School_DocumentId" = @documentId
            ORDER BY "ParentCollectionItemId", "Ordinal", "CollectionItemId";
            """,
            new NpgsqlParameter("documentId", documentId)
        );

        return rows.Select(row => new PersistedSchoolAddressPeriodRow(
                GetInt64(row, "CollectionItemId"),
                GetInt64(row, "School_DocumentId"),
                GetInt64(row, "ParentCollectionItemId"),
                GetInt32(row, "Ordinal"),
                GetString(row, "PeriodName")
            ))
            .ToArray();
    }

    private async Task<PersistedSchoolExtensionRow> ReadSchoolExtensionAsync(long documentId)
    {
        var rows = await _database.QueryRowsAsync(
            """
            SELECT "DocumentId", "CampusCode"
            FROM "sample"."SchoolExtension"
            WHERE "DocumentId" = @documentId;
            """,
            new NpgsqlParameter("documentId", documentId)
        );

        return rows.Count == 1
            ? new PersistedSchoolExtensionRow(
                GetInt64(rows[0], "DocumentId"),
                GetString(rows[0], "CampusCode")
            )
            : throw new InvalidOperationException(
                $"Expected exactly one school extension row for document id '{documentId}', but found {rows.Count}."
            );
    }

    private async Task<IReadOnlyList<PersistedSchoolExtensionAddressRow>> ReadSchoolExtensionAddressesAsync(
        long documentId
    )
    {
        var rows = await _database.QueryRowsAsync(
            """
            SELECT "BaseCollectionItemId", "School_DocumentId", "Zone"
            FROM "sample"."SchoolExtensionAddress"
            WHERE "School_DocumentId" = @documentId
            ORDER BY "BaseCollectionItemId";
            """,
            new NpgsqlParameter("documentId", documentId)
        );

        return rows.Select(row => new PersistedSchoolExtensionAddressRow(
                GetInt64(row, "BaseCollectionItemId"),
                GetInt64(row, "School_DocumentId"),
                GetString(row, "Zone")
            ))
            .ToArray();
    }

    private async Task<
        IReadOnlyList<PersistedSchoolExtensionInterventionRow>
    > ReadSchoolExtensionInterventionsAsync(long documentId)
    {
        var rows = await _database.QueryRowsAsync(
            """
            SELECT "CollectionItemId", "School_DocumentId", "Ordinal", "InterventionCode"
            FROM "sample"."SchoolExtensionIntervention"
            WHERE "School_DocumentId" = @documentId
            ORDER BY "Ordinal", "CollectionItemId";
            """,
            new NpgsqlParameter("documentId", documentId)
        );

        return rows.Select(row => new PersistedSchoolExtensionInterventionRow(
                GetInt64(row, "CollectionItemId"),
                GetInt64(row, "School_DocumentId"),
                GetInt32(row, "Ordinal"),
                GetString(row, "InterventionCode")
            ))
            .ToArray();
    }

    private async Task<
        IReadOnlyList<PersistedSchoolExtensionInterventionVisitRow>
    > ReadSchoolExtensionInterventionVisitsAsync(long documentId)
    {
        var rows = await _database.QueryRowsAsync(
            """
            SELECT "CollectionItemId", "School_DocumentId", "ParentCollectionItemId", "Ordinal", "VisitCode"
            FROM "sample"."SchoolExtensionInterventionVisit"
            WHERE "School_DocumentId" = @documentId
            ORDER BY "ParentCollectionItemId", "Ordinal", "CollectionItemId";
            """,
            new NpgsqlParameter("documentId", documentId)
        );

        return rows.Select(row => new PersistedSchoolExtensionInterventionVisitRow(
                GetInt64(row, "CollectionItemId"),
                GetInt64(row, "School_DocumentId"),
                GetInt64(row, "ParentCollectionItemId"),
                GetInt32(row, "Ordinal"),
                GetString(row, "VisitCode")
            ))
            .ToArray();
    }

    private static short GetInt16(IReadOnlyDictionary<string, object?> row, string columnName) =>
        Convert.ToInt16(GetRequiredValue(row, columnName), CultureInfo.InvariantCulture);

    private static int GetInt32(IReadOnlyDictionary<string, object?> row, string columnName) =>
        Convert.ToInt32(GetRequiredValue(row, columnName), CultureInfo.InvariantCulture);

    private static long GetInt64(IReadOnlyDictionary<string, object?> row, string columnName) =>
        Convert.ToInt64(GetRequiredValue(row, columnName), CultureInfo.InvariantCulture);

    private static Guid GetGuid(IReadOnlyDictionary<string, object?> row, string columnName) =>
        GetRequiredValue(row, columnName) is Guid value
            ? value
            : throw new InvalidOperationException($"Expected column '{columnName}' to contain a Guid value.");

    private static string GetString(IReadOnlyDictionary<string, object?> row, string columnName) =>
        GetRequiredValue(row, columnName) as string
        ?? throw new InvalidOperationException($"Expected column '{columnName}' to contain a string value.");

    private static object GetRequiredValue(IReadOnlyDictionary<string, object?> row, string columnName)
    {
        if (!row.TryGetValue(columnName, out var value) || value is null)
        {
            throw new InvalidOperationException(
                $"Expected persisted row to contain non-null column '{columnName}'."
            );
        }

        return value;
    }
}

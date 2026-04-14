// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Core.ApiSchema;
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

file sealed class AuthoritativeDs52WriteHostApplicationLifetime : IHostApplicationLifetime
{
    public CancellationToken ApplicationStarted => CancellationToken.None;
    public CancellationToken ApplicationStopping => CancellationToken.None;
    public CancellationToken ApplicationStopped => CancellationToken.None;

    public void StopApplication() { }
}

file sealed class AuthoritativeDs52WriteAllowAllResourceAuthorizationHandler : IResourceAuthorizationHandler
{
    public Task<ResourceAuthorizationResult> Authorize(
        DocumentSecurityElements documentSecurityElements,
        OperationType operationType,
        TraceId traceId
    ) => Task.FromResult<ResourceAuthorizationResult>(new ResourceAuthorizationResult.Authorized());
}

file sealed class AuthoritativeDs52WriteNoOpUpdateCascadeHandler : IUpdateCascadeHandler
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

file static class AuthoritativeDs52WriteIntegrationTestSupport
{
    public const string FixtureRelativePath = "src/dms/backend/Fixtures/authoritative/ds-5.2";

    public static ServiceProvider CreateServiceProvider()
    {
        ServiceCollection services = [];

        services.AddSingleton<IHostApplicationLifetime, AuthoritativeDs52WriteHostApplicationLifetime>();
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

    public static (ProjectSchema ProjectSchema, ResourceSchema ResourceSchema) GetResourceSchema(
        EffectiveSchemaSet effectiveSchemaSet,
        string projectEndpointName,
        string resourceName
    )
    {
        var effectiveProjectSchema = effectiveSchemaSet.ProjectsInEndpointOrder.Single(project =>
            string.Equals(
                project.ProjectEndpointName,
                projectEndpointName,
                StringComparison.OrdinalIgnoreCase
            )
        );

        var projectSchema = new ProjectSchema(effectiveProjectSchema.ProjectSchema, NullLogger.Instance);
        var resourceSchemaNode =
            projectSchema.FindResourceSchemaNodeByResourceName(new ResourceName(resourceName))
            ?? projectSchema
                .GetAllResourceSchemaNodes()
                .SingleOrDefault(node =>
                    string.Equals(
                        node["resourceName"]?.GetValue<string>(),
                        resourceName,
                        StringComparison.Ordinal
                    )
                )
            ?? throw new InvalidOperationException(
                $"Could not find resource '{resourceName}' in project '{projectEndpointName}'."
            );

        return (projectSchema, new ResourceSchema(resourceSchemaNode));
    }

    public static ResourceInfo CreateResourceInfo(
        ProjectSchema projectSchema,
        ResourceSchema resourceSchema
    ) =>
        new(
            ProjectName: projectSchema.ProjectName,
            ResourceName: resourceSchema.ResourceName,
            IsDescriptor: resourceSchema.IsDescriptor,
            ResourceVersion: projectSchema.ResourceVersion,
            AllowIdentityUpdates: resourceSchema.AllowIdentityUpdates,
            EducationOrganizationHierarchyInfo: new EducationOrganizationHierarchyInfo(false, 0, null),
            AuthorizationSecurableInfo: []
        );

    public static DocumentInfo CreateDocumentInfo(
        JsonNode requestBody,
        ResourceInfo resourceInfo,
        ResourceSchema resourceSchema,
        MappingSet mappingSet
    ) =>
        RelationalDocumentInfoTestHelper.CreateDocumentInfo(
            requestBody,
            resourceInfo,
            resourceSchema,
            mappingSet
        );

    public static short GetInt16(IReadOnlyDictionary<string, object?> row, string columnName) =>
        Convert.ToInt16(GetRequiredValue(row, columnName), CultureInfo.InvariantCulture);

    public static int GetInt32(IReadOnlyDictionary<string, object?> row, string columnName) =>
        Convert.ToInt32(GetRequiredValue(row, columnName), CultureInfo.InvariantCulture);

    public static long GetInt64(IReadOnlyDictionary<string, object?> row, string columnName) =>
        Convert.ToInt64(GetRequiredValue(row, columnName), CultureInfo.InvariantCulture);

    public static Guid GetGuid(IReadOnlyDictionary<string, object?> row, string columnName) =>
        GetRequiredValue(row, columnName) is Guid value
            ? value
            : throw new InvalidOperationException($"Expected column '{columnName}' to contain a Guid value.");

    public static string GetString(IReadOnlyDictionary<string, object?> row, string columnName) =>
        GetRequiredValue(row, columnName) as string
        ?? throw new InvalidOperationException($"Expected column '{columnName}' to contain a string value.");

    public static bool? GetNullableBoolean(IReadOnlyDictionary<string, object?> row, string columnName) =>
        row.TryGetValue(columnName, out var value)
            ? value switch
            {
                null => null,
                bool boolValue => boolValue,
                _ => throw new InvalidOperationException(
                    $"Expected column '{columnName}' to contain a boolean value."
                ),
            }
            : throw new InvalidOperationException(
                $"Expected persisted row to contain column '{columnName}'."
            );

    public static DateOnly GetDateOnly(IReadOnlyDictionary<string, object?> row, string columnName) =>
        GetRequiredValue(row, columnName) switch
        {
            DateOnly value => value,
            DateTime value => DateOnly.FromDateTime(value),
            _ => throw new InvalidOperationException(
                $"Expected column '{columnName}' to contain a DateOnly value."
            ),
        };

    public static DateOnly? GetNullableDateOnly(
        IReadOnlyDictionary<string, object?> row,
        string columnName
    ) =>
        row.TryGetValue(columnName, out var value)
            ? value switch
            {
                null => null,
                DateOnly dateOnlyValue => dateOnlyValue,
                DateTime dateTimeValue => DateOnly.FromDateTime(dateTimeValue),
                _ => throw new InvalidOperationException(
                    $"Expected column '{columnName}' to contain a DateOnly value."
                ),
            }
            : throw new InvalidOperationException(
                $"Expected persisted row to contain column '{columnName}'."
            );

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

internal sealed record AuthoritativeDs52SchoolSeedData(
    long PhysicalAddressTypeDescriptorId,
    long MailingAddressTypeDescriptorId,
    long StateAbbreviationDescriptorId,
    long EducationOrganizationCategoryDescriptorId,
    long NinthGradeLevelDescriptorId,
    long TenthGradeLevelDescriptorId
);

internal sealed record AuthoritativeDs52SchoolDocumentRow(
    long DocumentId,
    Guid DocumentUuid,
    short ResourceKeyId,
    long ContentVersion
);

internal sealed record AuthoritativeDs52SchoolRow(long DocumentId, string NameOfInstitution, long SchoolId);

internal sealed record AuthoritativeDs52SchoolEducationOrganizationCategoryRow(
    long CollectionItemId,
    int Ordinal,
    long SchoolDocumentId,
    long EducationOrganizationCategoryDescriptorId
);

internal sealed record AuthoritativeDs52SchoolGradeLevelRow(
    long CollectionItemId,
    int Ordinal,
    long SchoolDocumentId,
    long GradeLevelDescriptorId
);

internal sealed record AuthoritativeDs52SchoolAddressRow(
    long CollectionItemId,
    int Ordinal,
    long SchoolDocumentId,
    long AddressTypeDescriptorId,
    long StateAbbreviationDescriptorId,
    string City,
    string PostalCode,
    string StreetNumberName,
    bool? DoNotPublishIndicator
);

internal sealed record AuthoritativeDs52SchoolAddressPeriodRow(
    long CollectionItemId,
    int Ordinal,
    long ParentCollectionItemId,
    long SchoolDocumentId,
    DateOnly BeginDate,
    DateOnly? EndDate
);

internal sealed record AuthoritativeDs52SchoolPersistedState(
    AuthoritativeDs52SchoolDocumentRow Document,
    AuthoritativeDs52SchoolRow School,
    IReadOnlyList<AuthoritativeDs52SchoolEducationOrganizationCategoryRow> EducationOrganizationCategories,
    IReadOnlyList<AuthoritativeDs52SchoolGradeLevelRow> GradeLevels,
    IReadOnlyList<AuthoritativeDs52SchoolAddressRow> Addresses,
    IReadOnlyList<AuthoritativeDs52SchoolAddressPeriodRow> AddressPeriods
);

[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
public class Given_A_Postgresql_Relational_Write_Smoke_With_The_Authoritative_Ds52_School_Fixture
{
    private const string CreateRequestBodyJson = """
        {
          "schoolId": 255901,
          "nameOfInstitution": "Lantern High School",
          "educationOrganizationCategories": [
            {
              "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"
            }
          ],
          "gradeLevels": [
            {
              "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth grade"
            },
            {
              "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Ninth grade"
            }
          ],
          "addresses": [
            {
              "addressTypeDescriptor": "uri://ed-fi.org/AddressTypeDescriptor#Physical",
              "city": "Austin",
              "postalCode": "78701",
              "stateAbbreviationDescriptor": "uri://ed-fi.org/StateAbbreviationDescriptor#TX",
              "streetNumberName": "100 Congress Ave",
              "doNotPublishIndicator": false
            },
            {
              "addressTypeDescriptor": "uri://ed-fi.org/AddressTypeDescriptor#Mailing",
              "city": "Austin",
              "postalCode": "78702",
              "stateAbbreviationDescriptor": "uri://ed-fi.org/StateAbbreviationDescriptor#TX",
              "streetNumberName": "200 Trinity St",
              "doNotPublishIndicator": true
            }
          ]
        }
        """;

    private const string ChangedUpdateRequestBodyJson = """
        {
          "schoolId": 255901,
          "nameOfInstitution": "Lantern Collegiate Academy",
          "educationOrganizationCategories": [
            {
              "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"
            }
          ],
          "gradeLevels": [
            {
              "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Ninth grade"
            },
            {
              "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth grade"
            }
          ],
          "addresses": [
            {
              "addressTypeDescriptor": "uri://ed-fi.org/AddressTypeDescriptor#Mailing",
              "city": "Austin",
              "postalCode": "78702",
              "stateAbbreviationDescriptor": "uri://ed-fi.org/StateAbbreviationDescriptor#TX",
              "streetNumberName": "200 Trinity St",
              "doNotPublishIndicator": true
            },
            {
              "addressTypeDescriptor": "uri://ed-fi.org/AddressTypeDescriptor#Physical",
              "city": "Austin",
              "postalCode": "78701",
              "stateAbbreviationDescriptor": "uri://ed-fi.org/StateAbbreviationDescriptor#TX",
              "streetNumberName": "100 Congress Ave",
              "doNotPublishIndicator": true
            }
          ]
        }
        """;

    private static readonly QualifiedResourceName SchoolResource = new("Ed-Fi", "School");
    private static readonly DocumentUuid SchoolDocumentUuid = new(
        Guid.Parse("dddddddd-0000-0000-0000-000000000001")
    );

    private PostgresqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private PostgresqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;
    private ResourceInfo _resourceInfo = null!;
    private ResourceSchema _resourceSchema = null!;
    private AuthoritativeDs52SchoolSeedData _seedData = null!;
    private UpsertResult _createResult = null!;
    private UpdateResult _changedUpdateResult = null!;
    private UpdateResult _noOpUpdateResult = null!;
    private AuthoritativeDs52SchoolPersistedState _stateAfterCreate = null!;
    private AuthoritativeDs52SchoolPersistedState _stateAfterChangedUpdate = null!;
    private AuthoritativeDs52SchoolPersistedState _stateAfterNoOpUpdate = null!;

    [SetUp]
    public async Task Setup()
    {
        _fixture = PostgresqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            AuthoritativeDs52WriteIntegrationTestSupport.FixtureRelativePath
        );
        _mappingSet = new MappingSetCompiler().Compile(_fixture.ModelSet);
        _database = await PostgresqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
        _serviceProvider = AuthoritativeDs52WriteIntegrationTestSupport.CreateServiceProvider();

        var (projectSchema, resourceSchema) = AuthoritativeDs52WriteIntegrationTestSupport.GetResourceSchema(
            _fixture.EffectiveSchemaSet,
            "ed-fi",
            "School"
        );

        _resourceInfo = AuthoritativeDs52WriteIntegrationTestSupport.CreateResourceInfo(
            projectSchema,
            resourceSchema
        );
        _resourceSchema = resourceSchema;
        _seedData = await SeedReferenceDataAsync();

        _createResult = await ExecuteCreateAsync();
        _createResult.Should().BeOfType<UpsertResult.InsertSuccess>();
        _stateAfterCreate = await ReadPersistedStateAsync(SchoolDocumentUuid.Value);

        _changedUpdateResult = await ExecuteUpdateAsync(
            ChangedUpdateRequestBodyJson,
            "pg-authoritative-ds52-school-changed-update"
        );
        _changedUpdateResult.Should().BeOfType<UpdateResult.UpdateSuccess>();
        _stateAfterChangedUpdate = await ReadPersistedStateAsync(SchoolDocumentUuid.Value);

        _noOpUpdateResult = await ExecuteUpdateAsync(
            ChangedUpdateRequestBodyJson,
            "pg-authoritative-ds52-school-no-op-update"
        );
        _noOpUpdateResult.Should().BeOfType<UpdateResult.UpdateSuccess>();
        _stateAfterNoOpUpdate = await ReadPersistedStateAsync(SchoolDocumentUuid.Value);
    }

    [TearDown]
    public async Task TearDown()
    {
        if (_serviceProvider is not null)
        {
            await _serviceProvider.DisposeAsync();
        }

        if (_database is not null)
        {
            await _database.DisposeAsync();
        }
    }

    [Test]
    public void It_persists_authoritative_ds52_school_root_and_collection_rows_on_create()
    {
        _createResult.Should().BeOfType<UpsertResult.InsertSuccess>();
        _stateAfterCreate.Document.DocumentUuid.Should().Be(SchoolDocumentUuid.Value);
        _stateAfterCreate
            .Document.ResourceKeyId.Should()
            .Be(_mappingSet.ResourceKeyIdByResource[SchoolResource]);
        _stateAfterCreate
            .School.Should()
            .Be(
                new AuthoritativeDs52SchoolRow(
                    _stateAfterCreate.Document.DocumentId,
                    "Lantern High School",
                    255901
                )
            );
        _stateAfterCreate
            .EducationOrganizationCategories.Should()
            .Equal(
                new AuthoritativeDs52SchoolEducationOrganizationCategoryRow(
                    _stateAfterCreate.EducationOrganizationCategories[0].CollectionItemId,
                    0,
                    _stateAfterCreate.Document.DocumentId,
                    _seedData.EducationOrganizationCategoryDescriptorId
                )
            );
        _stateAfterCreate
            .GradeLevels.Should()
            .Equal(
                new AuthoritativeDs52SchoolGradeLevelRow(
                    _stateAfterCreate.GradeLevels[0].CollectionItemId,
                    0,
                    _stateAfterCreate.Document.DocumentId,
                    _seedData.TenthGradeLevelDescriptorId
                ),
                new AuthoritativeDs52SchoolGradeLevelRow(
                    _stateAfterCreate.GradeLevels[1].CollectionItemId,
                    1,
                    _stateAfterCreate.Document.DocumentId,
                    _seedData.NinthGradeLevelDescriptorId
                )
            );
        _stateAfterCreate
            .Addresses.Should()
            .Equal(
                new AuthoritativeDs52SchoolAddressRow(
                    _stateAfterCreate.Addresses[0].CollectionItemId,
                    0,
                    _stateAfterCreate.Document.DocumentId,
                    _seedData.PhysicalAddressTypeDescriptorId,
                    _seedData.StateAbbreviationDescriptorId,
                    "Austin",
                    "78701",
                    "100 Congress Ave",
                    false
                ),
                new AuthoritativeDs52SchoolAddressRow(
                    _stateAfterCreate.Addresses[1].CollectionItemId,
                    1,
                    _stateAfterCreate.Document.DocumentId,
                    _seedData.MailingAddressTypeDescriptorId,
                    _seedData.StateAbbreviationDescriptorId,
                    "Austin",
                    "78702",
                    "200 Trinity St",
                    true
                )
            );
        _stateAfterCreate.AddressPeriods.Should().BeEmpty();
    }

    [Test]
    public void It_reuses_stable_collection_item_ids_and_updates_ordinals_for_a_changed_put()
    {
        _changedUpdateResult.Should().BeOfType<UpdateResult.UpdateSuccess>();
        _changedUpdateResult
            .As<UpdateResult.UpdateSuccess>()
            .ExistingDocumentUuid.Should()
            .Be(SchoolDocumentUuid);
        _stateAfterChangedUpdate.Document.DocumentUuid.Should().Be(SchoolDocumentUuid.Value);
        _stateAfterChangedUpdate
            .Document.ContentVersion.Should()
            .BeGreaterThan(_stateAfterCreate.Document.ContentVersion);
        _stateAfterChangedUpdate
            .School.Should()
            .Be(
                new AuthoritativeDs52SchoolRow(
                    _stateAfterChangedUpdate.Document.DocumentId,
                    "Lantern Collegiate Academy",
                    255901
                )
            );

        var createdGradeLevelsByDescriptor = _stateAfterCreate.GradeLevels.ToDictionary(row =>
            row.GradeLevelDescriptorId
        );
        var createdAddressesByStreet = _stateAfterCreate.Addresses.ToDictionary(row => row.StreetNumberName);
        _stateAfterChangedUpdate
            .EducationOrganizationCategories.Should()
            .Equal(
                new AuthoritativeDs52SchoolEducationOrganizationCategoryRow(
                    _stateAfterCreate.EducationOrganizationCategories[0].CollectionItemId,
                    0,
                    _stateAfterChangedUpdate.Document.DocumentId,
                    _seedData.EducationOrganizationCategoryDescriptorId
                )
            );
        _stateAfterChangedUpdate
            .GradeLevels.Should()
            .Equal(
                new AuthoritativeDs52SchoolGradeLevelRow(
                    createdGradeLevelsByDescriptor[_seedData.NinthGradeLevelDescriptorId].CollectionItemId,
                    0,
                    _stateAfterChangedUpdate.Document.DocumentId,
                    _seedData.NinthGradeLevelDescriptorId
                ),
                new AuthoritativeDs52SchoolGradeLevelRow(
                    createdGradeLevelsByDescriptor[_seedData.TenthGradeLevelDescriptorId].CollectionItemId,
                    1,
                    _stateAfterChangedUpdate.Document.DocumentId,
                    _seedData.TenthGradeLevelDescriptorId
                )
            );
        _stateAfterChangedUpdate
            .Addresses.Should()
            .Equal(
                new AuthoritativeDs52SchoolAddressRow(
                    createdAddressesByStreet["200 Trinity St"].CollectionItemId,
                    0,
                    _stateAfterChangedUpdate.Document.DocumentId,
                    _seedData.MailingAddressTypeDescriptorId,
                    _seedData.StateAbbreviationDescriptorId,
                    "Austin",
                    "78702",
                    "200 Trinity St",
                    true
                ),
                new AuthoritativeDs52SchoolAddressRow(
                    createdAddressesByStreet["100 Congress Ave"].CollectionItemId,
                    1,
                    _stateAfterChangedUpdate.Document.DocumentId,
                    _seedData.PhysicalAddressTypeDescriptorId,
                    _seedData.StateAbbreviationDescriptorId,
                    "Austin",
                    "78701",
                    "100 Congress Ave",
                    true
                )
            );
        _stateAfterChangedUpdate.AddressPeriods.Should().BeEmpty();
    }

    [Test]
    public void It_keeps_rowsets_and_content_version_unchanged_for_a_repeat_put()
    {
        _noOpUpdateResult.Should().BeOfType<UpdateResult.UpdateSuccess>();
        _noOpUpdateResult
            .As<UpdateResult.UpdateSuccess>()
            .ExistingDocumentUuid.Should()
            .Be(SchoolDocumentUuid);
        _stateAfterNoOpUpdate.Should().BeEquivalentTo(_stateAfterChangedUpdate);
    }

    private async Task<UpsertResult> ExecuteCreateAsync()
    {
        await using var scope = _serviceProvider.CreateAsyncScope();
        SetSelectedInstance(scope.ServiceProvider);

        var requestBody = JsonNode.Parse(CreateRequestBodyJson)!;
        var request = new UpsertRequest(
            ResourceInfo: _resourceInfo,
            DocumentInfo: AuthoritativeDs52WriteIntegrationTestSupport.CreateDocumentInfo(
                requestBody,
                _resourceInfo,
                _resourceSchema,
                _mappingSet
            ),
            MappingSet: _mappingSet,
            EdfiDoc: requestBody,
            Headers: [],
            TraceId: new TraceId("pg-authoritative-ds52-school-create"),
            DocumentUuid: SchoolDocumentUuid,
            DocumentSecurityElements: new([], [], [], [], []),
            UpdateCascadeHandler: new AuthoritativeDs52WriteNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new AuthoritativeDs52WriteAllowAllResourceAuthorizationHandler(),
            ResourceAuthorizationPathways: []
        );

        return await scope
            .ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>()
            .UpsertDocument(request);
    }

    private async Task<UpdateResult> ExecuteUpdateAsync(string requestBodyJson, string traceId)
    {
        await using var scope = _serviceProvider.CreateAsyncScope();
        SetSelectedInstance(scope.ServiceProvider);

        var requestBody = JsonNode.Parse(requestBodyJson)!;
        var request = new UpdateRequest(
            ResourceInfo: _resourceInfo,
            DocumentInfo: AuthoritativeDs52WriteIntegrationTestSupport.CreateDocumentInfo(
                requestBody,
                _resourceInfo,
                _resourceSchema,
                _mappingSet
            ),
            MappingSet: _mappingSet,
            EdfiDoc: requestBody,
            Headers: [],
            TraceId: new TraceId(traceId),
            DocumentUuid: SchoolDocumentUuid,
            DocumentSecurityElements: new([], [], [], [], []),
            UpdateCascadeHandler: new AuthoritativeDs52WriteNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new AuthoritativeDs52WriteAllowAllResourceAuthorizationHandler(),
            ResourceAuthorizationPathways: []
        );

        return await scope
            .ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>()
            .UpdateDocumentById(request);
    }

    private void SetSelectedInstance(IServiceProvider serviceProvider)
    {
        serviceProvider
            .GetRequiredService<IDmsInstanceSelection>()
            .SetSelectedDmsInstance(
                new DmsInstance(
                    Id: 1,
                    InstanceType: "test",
                    InstanceName: "PostgresqlRelationalWriteAuthoritativeDs52SchoolSmoke",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );
    }

    private async Task<AuthoritativeDs52SchoolSeedData> SeedReferenceDataAsync()
    {
        var physicalAddressTypeDescriptorId = await SeedDescriptorAsync(
            Guid.Parse("10111111-1111-1111-1111-111111111111"),
            "AddressTypeDescriptor",
            "Ed-Fi:AddressTypeDescriptor",
            "uri://ed-fi.org/AddressTypeDescriptor#Physical",
            "uri://ed-fi.org/AddressTypeDescriptor",
            "Physical",
            "Physical"
        );
        var mailingAddressTypeDescriptorId = await SeedDescriptorAsync(
            Guid.Parse("20222222-2222-2222-2222-222222222222"),
            "AddressTypeDescriptor",
            "Ed-Fi:AddressTypeDescriptor",
            "uri://ed-fi.org/AddressTypeDescriptor#Mailing",
            "uri://ed-fi.org/AddressTypeDescriptor",
            "Mailing",
            "Mailing"
        );
        var stateAbbreviationDescriptorId = await SeedDescriptorAsync(
            Guid.Parse("30333333-3333-3333-3333-333333333333"),
            "StateAbbreviationDescriptor",
            "Ed-Fi:StateAbbreviationDescriptor",
            "uri://ed-fi.org/StateAbbreviationDescriptor#TX",
            "uri://ed-fi.org/StateAbbreviationDescriptor",
            "TX",
            "Texas"
        );
        var educationOrganizationCategoryDescriptorId = await SeedDescriptorAsync(
            Guid.Parse("40444444-4444-4444-4444-444444444444"),
            "EducationOrganizationCategoryDescriptor",
            "Ed-Fi:EducationOrganizationCategoryDescriptor",
            "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School",
            "uri://ed-fi.org/EducationOrganizationCategoryDescriptor",
            "School",
            "School"
        );
        var ninthGradeLevelDescriptorId = await SeedDescriptorAsync(
            Guid.Parse("50555555-5555-5555-5555-555555555555"),
            "GradeLevelDescriptor",
            "Ed-Fi:GradeLevelDescriptor",
            "uri://ed-fi.org/GradeLevelDescriptor#Ninth grade",
            "uri://ed-fi.org/GradeLevelDescriptor",
            "Ninth grade",
            "Ninth grade"
        );
        var tenthGradeLevelDescriptorId = await SeedDescriptorAsync(
            Guid.Parse("60666666-6666-6666-6666-666666666666"),
            "GradeLevelDescriptor",
            "Ed-Fi:GradeLevelDescriptor",
            "uri://ed-fi.org/GradeLevelDescriptor#Tenth grade",
            "uri://ed-fi.org/GradeLevelDescriptor",
            "Tenth grade",
            "Tenth grade"
        );

        return new(
            PhysicalAddressTypeDescriptorId: physicalAddressTypeDescriptorId,
            MailingAddressTypeDescriptorId: mailingAddressTypeDescriptorId,
            StateAbbreviationDescriptorId: stateAbbreviationDescriptorId,
            EducationOrganizationCategoryDescriptorId: educationOrganizationCategoryDescriptorId,
            NinthGradeLevelDescriptorId: ninthGradeLevelDescriptorId,
            TenthGradeLevelDescriptorId: tenthGradeLevelDescriptorId
        );
    }

    private async Task<long> SeedDescriptorAsync(
        Guid documentUuid,
        string resourceName,
        string discriminator,
        string uri,
        string @namespace,
        string codeValue,
        string shortDescription
    )
    {
        var resourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", resourceName);
        var documentId = await InsertDescriptorAsync(
            documentUuid,
            resourceKeyId,
            discriminator,
            uri,
            @namespace,
            codeValue,
            shortDescription
        );

        await InsertReferentialIdentityAsync(
            CreateDescriptorReferentialId("Ed-Fi", resourceName, uri),
            documentId,
            resourceKeyId
        );

        return documentId;
    }

    private async Task<short> GetResourceKeyIdAsync(string projectName, string resourceName)
    {
        return await _database.ExecuteScalarAsync<short>(
            """
            SELECT "ResourceKeyId"
            FROM "dms"."ResourceKey"
            WHERE "ProjectName" = @projectName
              AND "ResourceName" = @resourceName;
            """,
            new NpgsqlParameter("projectName", projectName),
            new NpgsqlParameter("resourceName", resourceName)
        );
    }

    private async Task<long> InsertDocumentAsync(Guid documentUuid, short resourceKeyId)
    {
        return await _database.ExecuteScalarAsync<long>(
            """
            INSERT INTO "dms"."Document" ("DocumentUuid", "ResourceKeyId")
            VALUES (@documentUuid, @resourceKeyId)
            RETURNING "DocumentId";
            """,
            new NpgsqlParameter("documentUuid", documentUuid),
            new NpgsqlParameter("resourceKeyId", resourceKeyId)
        );
    }

    private async Task<long> InsertDescriptorAsync(
        Guid documentUuid,
        short resourceKeyId,
        string discriminator,
        string uri,
        string @namespace,
        string codeValue,
        string shortDescription
    )
    {
        var documentId = await InsertDocumentAsync(documentUuid, resourceKeyId);

        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO "dms"."Descriptor" (
                "DocumentId",
                "Namespace",
                "CodeValue",
                "ShortDescription",
                "Description",
                "Discriminator",
                "Uri"
            )
            VALUES (
                @documentId,
                @namespace,
                @codeValue,
                @shortDescription,
                @description,
                @discriminator,
                @uri
            );
            """,
            new NpgsqlParameter("documentId", documentId),
            new NpgsqlParameter("namespace", @namespace),
            new NpgsqlParameter("codeValue", codeValue),
            new NpgsqlParameter("shortDescription", shortDescription),
            new NpgsqlParameter("description", shortDescription),
            new NpgsqlParameter("discriminator", discriminator),
            new NpgsqlParameter("uri", uri)
        );

        return documentId;
    }

    private async Task InsertReferentialIdentityAsync(
        ReferentialId referentialId,
        long documentId,
        short resourceKeyId
    )
    {
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO "dms"."ReferentialIdentity" ("ReferentialId", "DocumentId", "ResourceKeyId")
            VALUES (@referentialId, @documentId, @resourceKeyId)
            ON CONFLICT ("ReferentialId") DO NOTHING;
            """,
            new NpgsqlParameter("referentialId", referentialId.Value),
            new NpgsqlParameter("documentId", documentId),
            new NpgsqlParameter("resourceKeyId", resourceKeyId)
        );
    }

    private static ReferentialId CreateReferentialId(
        (string ProjectName, string ResourceName, bool IsDescriptor) targetResource,
        params (string IdentityJsonPath, string IdentityValue)[] identityElements
    )
    {
        return ReferentialIdCalculator.ReferentialIdFrom(
            new BaseResourceInfo(
                new ProjectName(targetResource.ProjectName),
                new ResourceName(targetResource.ResourceName),
                targetResource.IsDescriptor
            ),
            new DocumentIdentity([
                .. identityElements.Select(identityElement => new DocumentIdentityElement(
                    new JsonPath(identityElement.IdentityJsonPath),
                    identityElement.IdentityValue
                )),
            ])
        );
    }

    private static ReferentialId CreateDescriptorReferentialId(
        string projectName,
        string resourceName,
        string descriptorUri
    )
    {
        return CreateReferentialId(
            (projectName, resourceName, true),
            (DocumentIdentity.DescriptorIdentityJsonPath.Value, descriptorUri.ToLowerInvariant())
        );
    }

    private async Task<AuthoritativeDs52SchoolPersistedState> ReadPersistedStateAsync(Guid documentUuid)
    {
        var document = await ReadDocumentAsync(documentUuid);

        return new(
            Document: document,
            School: await ReadSchoolAsync(document.DocumentId),
            EducationOrganizationCategories: await ReadEducationOrganizationCategoriesAsync(
                document.DocumentId
            ),
            GradeLevels: await ReadGradeLevelsAsync(document.DocumentId),
            Addresses: await ReadAddressesAsync(document.DocumentId),
            AddressPeriods: await ReadAddressPeriodsAsync(document.DocumentId)
        );
    }

    private async Task<AuthoritativeDs52SchoolDocumentRow> ReadDocumentAsync(Guid documentUuid)
    {
        var rows = await _database.QueryRowsAsync(
            """
            SELECT "DocumentId", "DocumentUuid", "ResourceKeyId", "ContentVersion"
            FROM "dms"."Document"
            WHERE "DocumentUuid" = @documentUuid;
            """,
            new NpgsqlParameter("documentUuid", documentUuid)
        );

        return rows.Count == 1
            ? new AuthoritativeDs52SchoolDocumentRow(
                AuthoritativeDs52WriteIntegrationTestSupport.GetInt64(rows[0], "DocumentId"),
                AuthoritativeDs52WriteIntegrationTestSupport.GetGuid(rows[0], "DocumentUuid"),
                AuthoritativeDs52WriteIntegrationTestSupport.GetInt16(rows[0], "ResourceKeyId"),
                AuthoritativeDs52WriteIntegrationTestSupport.GetInt64(rows[0], "ContentVersion")
            )
            : throw new InvalidOperationException(
                $"Expected exactly one document row for '{documentUuid}', but found {rows.Count}."
            );
    }

    private async Task<AuthoritativeDs52SchoolRow> ReadSchoolAsync(long documentId)
    {
        var rows = await _database.QueryRowsAsync(
            """
            SELECT "DocumentId", "NameOfInstitution", "SchoolId"
            FROM "edfi"."School"
            WHERE "DocumentId" = @documentId;
            """,
            new NpgsqlParameter("documentId", documentId)
        );

        return rows.Count == 1
            ? new AuthoritativeDs52SchoolRow(
                AuthoritativeDs52WriteIntegrationTestSupport.GetInt64(rows[0], "DocumentId"),
                AuthoritativeDs52WriteIntegrationTestSupport.GetString(rows[0], "NameOfInstitution"),
                AuthoritativeDs52WriteIntegrationTestSupport.GetInt64(rows[0], "SchoolId")
            )
            : throw new InvalidOperationException(
                $"Expected exactly one School row for document id '{documentId}', but found {rows.Count}."
            );
    }

    private async Task<
        IReadOnlyList<AuthoritativeDs52SchoolEducationOrganizationCategoryRow>
    > ReadEducationOrganizationCategoriesAsync(long documentId)
    {
        var rows = await _database.QueryRowsAsync(
            """
            SELECT
                "CollectionItemId",
                "Ordinal",
                "School_DocumentId",
                "EducationOrganizationCategoryDescriptor_DescriptorId"
            FROM "edfi"."SchoolEducationOrganizationCategory"
            WHERE "School_DocumentId" = @documentId
            ORDER BY "Ordinal";
            """,
            new NpgsqlParameter("documentId", documentId)
        );

        return rows.Select(row => new AuthoritativeDs52SchoolEducationOrganizationCategoryRow(
                AuthoritativeDs52WriteIntegrationTestSupport.GetInt64(row, "CollectionItemId"),
                AuthoritativeDs52WriteIntegrationTestSupport.GetInt32(row, "Ordinal"),
                AuthoritativeDs52WriteIntegrationTestSupport.GetInt64(row, "School_DocumentId"),
                AuthoritativeDs52WriteIntegrationTestSupport.GetInt64(
                    row,
                    "EducationOrganizationCategoryDescriptor_DescriptorId"
                )
            ))
            .ToArray();
    }

    private async Task<IReadOnlyList<AuthoritativeDs52SchoolGradeLevelRow>> ReadGradeLevelsAsync(
        long documentId
    )
    {
        var rows = await _database.QueryRowsAsync(
            """
            SELECT
                "CollectionItemId",
                "Ordinal",
                "School_DocumentId",
                "GradeLevelDescriptor_DescriptorId"
            FROM "edfi"."SchoolGradeLevel"
            WHERE "School_DocumentId" = @documentId
            ORDER BY "Ordinal";
            """,
            new NpgsqlParameter("documentId", documentId)
        );

        return rows.Select(row => new AuthoritativeDs52SchoolGradeLevelRow(
                AuthoritativeDs52WriteIntegrationTestSupport.GetInt64(row, "CollectionItemId"),
                AuthoritativeDs52WriteIntegrationTestSupport.GetInt32(row, "Ordinal"),
                AuthoritativeDs52WriteIntegrationTestSupport.GetInt64(row, "School_DocumentId"),
                AuthoritativeDs52WriteIntegrationTestSupport.GetInt64(
                    row,
                    "GradeLevelDescriptor_DescriptorId"
                )
            ))
            .ToArray();
    }

    private async Task<IReadOnlyList<AuthoritativeDs52SchoolAddressRow>> ReadAddressesAsync(long documentId)
    {
        var rows = await _database.QueryRowsAsync(
            """
            SELECT
                "CollectionItemId",
                "Ordinal",
                "School_DocumentId",
                "AddressTypeDescriptor_DescriptorId",
                "StateAbbreviationDescriptor_DescriptorId",
                "City",
                "PostalCode",
                "StreetNumberName",
                "DoNotPublishIndicator"
            FROM "edfi"."SchoolAddress"
            WHERE "School_DocumentId" = @documentId
            ORDER BY "Ordinal";
            """,
            new NpgsqlParameter("documentId", documentId)
        );

        return rows.Select(row => new AuthoritativeDs52SchoolAddressRow(
                AuthoritativeDs52WriteIntegrationTestSupport.GetInt64(row, "CollectionItemId"),
                AuthoritativeDs52WriteIntegrationTestSupport.GetInt32(row, "Ordinal"),
                AuthoritativeDs52WriteIntegrationTestSupport.GetInt64(row, "School_DocumentId"),
                AuthoritativeDs52WriteIntegrationTestSupport.GetInt64(
                    row,
                    "AddressTypeDescriptor_DescriptorId"
                ),
                AuthoritativeDs52WriteIntegrationTestSupport.GetInt64(
                    row,
                    "StateAbbreviationDescriptor_DescriptorId"
                ),
                AuthoritativeDs52WriteIntegrationTestSupport.GetString(row, "City"),
                AuthoritativeDs52WriteIntegrationTestSupport.GetString(row, "PostalCode"),
                AuthoritativeDs52WriteIntegrationTestSupport.GetString(row, "StreetNumberName"),
                AuthoritativeDs52WriteIntegrationTestSupport.GetNullableBoolean(row, "DoNotPublishIndicator")
            ))
            .ToArray();
    }

    private async Task<IReadOnlyList<AuthoritativeDs52SchoolAddressPeriodRow>> ReadAddressPeriodsAsync(
        long documentId
    )
    {
        var rows = await _database.QueryRowsAsync(
            """
            SELECT
                "CollectionItemId",
                "Ordinal",
                "ParentCollectionItemId",
                "School_DocumentId",
                "BeginDate",
                "EndDate"
            FROM "edfi"."SchoolAddressPeriod"
            WHERE "School_DocumentId" = @documentId
            ORDER BY "ParentCollectionItemId", "Ordinal";
            """,
            new NpgsqlParameter("documentId", documentId)
        );

        return rows.Select(row => new AuthoritativeDs52SchoolAddressPeriodRow(
                AuthoritativeDs52WriteIntegrationTestSupport.GetInt64(row, "CollectionItemId"),
                AuthoritativeDs52WriteIntegrationTestSupport.GetInt32(row, "Ordinal"),
                AuthoritativeDs52WriteIntegrationTestSupport.GetInt64(row, "ParentCollectionItemId"),
                AuthoritativeDs52WriteIntegrationTestSupport.GetInt64(row, "School_DocumentId"),
                AuthoritativeDs52WriteIntegrationTestSupport.GetDateOnly(row, "BeginDate"),
                AuthoritativeDs52WriteIntegrationTestSupport.GetNullableDateOnly(row, "EndDate")
            ))
            .ToArray();
    }
}

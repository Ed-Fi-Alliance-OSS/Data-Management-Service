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

file sealed class AuthoritativeDs52ContactWriteHostApplicationLifetime : IHostApplicationLifetime
{
    public CancellationToken ApplicationStarted => CancellationToken.None;
    public CancellationToken ApplicationStopping => CancellationToken.None;
    public CancellationToken ApplicationStopped => CancellationToken.None;

    public void StopApplication() { }
}

file sealed class AuthoritativeDs52ContactWriteAllowAllResourceAuthorizationHandler
    : IResourceAuthorizationHandler
{
    public Task<ResourceAuthorizationResult> Authorize(
        DocumentSecurityElements documentSecurityElements,
        OperationType operationType,
        TraceId traceId
    ) => Task.FromResult<ResourceAuthorizationResult>(new ResourceAuthorizationResult.Authorized());
}

file sealed class AuthoritativeDs52ContactWriteNoOpUpdateCascadeHandler : IUpdateCascadeHandler
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

file static class AuthoritativeDs52ContactWriteIntegrationTestSupport
{
    public const string FixtureRelativePath = "src/dms/backend/Fixtures/authoritative/ds-5.2";

    public static ServiceProvider CreateServiceProvider()
    {
        ServiceCollection services = [];

        services.AddSingleton<
            IHostApplicationLifetime,
            AuthoritativeDs52ContactWriteHostApplicationLifetime
        >();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<NpgsqlDataSourceCache>();
        services.AddScoped<IDmsInstanceSelection, DmsInstanceSelection>();
        services.AddScoped<NpgsqlDataSourceProvider>();
        services.Configure<DatabaseOptions>(options => options.IsolationLevel = IsolationLevel.ReadCommitted);
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

internal sealed record AuthoritativeDs52ContactSeedData(
    long HomeAddressTypeDescriptorId,
    long WorkAddressTypeDescriptorId,
    long TemporaryAddressTypeDescriptorId,
    long StateAbbreviationDescriptorId
);

internal sealed record AuthoritativeDs52ContactDocumentRow(
    long DocumentId,
    Guid DocumentUuid,
    short ResourceKeyId,
    long ContentVersion
);

internal sealed record AuthoritativeDs52ContactRow(
    long DocumentId,
    string ContactUniqueId,
    string FirstName,
    string LastSurname
);

internal sealed record AuthoritativeDs52ContactAddressRow(
    long CollectionItemId,
    int Ordinal,
    long ContactDocumentId,
    long AddressTypeDescriptorId,
    long StateAbbreviationDescriptorId,
    string City,
    string PostalCode,
    string StreetNumberName,
    bool? DoNotPublishIndicator
);

internal sealed record AuthoritativeDs52ContactAddressPeriodRow(
    long CollectionItemId,
    int Ordinal,
    long ParentCollectionItemId,
    long ContactDocumentId,
    DateOnly BeginDate,
    DateOnly? EndDate
);

internal sealed record AuthoritativeDs52ContactPersistedState(
    AuthoritativeDs52ContactDocumentRow Document,
    AuthoritativeDs52ContactRow Contact,
    IReadOnlyList<AuthoritativeDs52ContactAddressRow> Addresses,
    IReadOnlyList<AuthoritativeDs52ContactAddressPeriodRow> AddressPeriods
);

[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
[NonParallelizable]
public class Given_A_Postgresql_Relational_Write_Smoke_With_The_Authoritative_Ds52_Contact_Fixture
{
    private const string ContactUniqueId = "contact-100";
    private const string FirstName = "Ava";
    private const string LastSurname = "Coleman";
    private const string HomeAddressTypeDescriptorUri = "uri://ed-fi.org/AddressTypeDescriptor#Home";
    private const string WorkAddressTypeDescriptorUri = "uri://ed-fi.org/AddressTypeDescriptor#Work";
    private const string TemporaryAddressTypeDescriptorUri =
        "uri://ed-fi.org/AddressTypeDescriptor#Temporary";
    private const string StateAbbreviationDescriptorUri = "uri://ed-fi.org/StateAbbreviationDescriptor#TX";
    private const string HomeStreetNumberName = "100 Congress Ave";
    private const string WorkStreetNumberName = "200 2nd St";
    private const string TemporaryStreetNumberName = "300 Elm St";

    private const string CreateRequestBodyJson = """
        {
          "contactUniqueId": "contact-100",
          "firstName": "Ava",
          "lastSurname": "Coleman",
          "addresses": [
            {
              "addressTypeDescriptor": "uri://ed-fi.org/AddressTypeDescriptor#Home",
              "city": "Austin",
              "postalCode": "78701",
              "stateAbbreviationDescriptor": "uri://ed-fi.org/StateAbbreviationDescriptor#TX",
              "streetNumberName": "100 Congress Ave",
              "doNotPublishIndicator": false,
              "periods": [
                {
                  "beginDate": "2020-08-01",
                  "endDate": "2021-05-31"
                },
                {
                  "beginDate": "2021-08-15",
                  "endDate": "2022-05-30"
                }
              ]
            },
            {
              "addressTypeDescriptor": "uri://ed-fi.org/AddressTypeDescriptor#Work",
              "city": "Austin",
              "postalCode": "78702",
              "stateAbbreviationDescriptor": "uri://ed-fi.org/StateAbbreviationDescriptor#TX",
              "streetNumberName": "200 2nd St",
              "doNotPublishIndicator": true,
              "periods": [
                {
                  "beginDate": "2022-06-01",
                  "endDate": "2023-06-30"
                }
              ]
            }
          ]
        }
        """;

    private const string ChangedUpdateRequestBodyJson = """
        {
          "contactUniqueId": "contact-100",
          "firstName": "Ava",
          "lastSurname": "Coleman",
          "addresses": [
            {
              "addressTypeDescriptor": "uri://ed-fi.org/AddressTypeDescriptor#Temporary",
              "city": "Dallas",
              "postalCode": "75201",
              "stateAbbreviationDescriptor": "uri://ed-fi.org/StateAbbreviationDescriptor#TX",
              "streetNumberName": "300 Elm St",
              "doNotPublishIndicator": false,
              "periods": [
                {
                  "beginDate": "2024-01-10",
                  "endDate": "2024-05-31"
                },
                {
                  "beginDate": "2024-06-01"
                }
              ]
            },
            {
              "addressTypeDescriptor": "uri://ed-fi.org/AddressTypeDescriptor#Home",
              "city": "Austin",
              "postalCode": "78701",
              "stateAbbreviationDescriptor": "uri://ed-fi.org/StateAbbreviationDescriptor#TX",
              "streetNumberName": "100 Congress Ave",
              "doNotPublishIndicator": true,
              "periods": [
                {
                  "beginDate": "2021-08-15",
                  "endDate": "2022-06-15"
                },
                {
                  "beginDate": "2023-09-01"
                }
              ]
            }
          ]
        }
        """;

    private static readonly DateOnly HomeOriginalPeriodBeginDate = new(2020, 8, 1);
    private static readonly DateOnly HomeOriginalPeriodEndDate = new(2021, 5, 31);
    private static readonly DateOnly HomeRetainedPeriodBeginDate = new(2021, 8, 15);
    private static readonly DateOnly HomeCreateRetainedPeriodEndDate = new(2022, 5, 30);
    private static readonly DateOnly HomeChangedRetainedPeriodEndDate = new(2022, 6, 15);
    private static readonly DateOnly HomeReplacementPeriodBeginDate = new(2023, 9, 1);
    private static readonly DateOnly WorkPeriodBeginDate = new(2022, 6, 1);
    private static readonly DateOnly WorkPeriodEndDate = new(2023, 6, 30);
    private static readonly DateOnly TemporaryFirstPeriodBeginDate = new(2024, 1, 10);
    private static readonly DateOnly TemporaryFirstPeriodEndDate = new(2024, 5, 31);
    private static readonly DateOnly TemporarySecondPeriodBeginDate = new(2024, 6, 1);

    private static readonly QualifiedResourceName ContactResource = new("Ed-Fi", "Contact");
    private static readonly DocumentUuid ContactDocumentUuid = new(
        Guid.Parse("eeeeeeee-0000-0000-0000-000000000001")
    );

    private PostgresqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private PostgresqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;
    private ResourceInfo _resourceInfo = null!;
    private ResourceSchema _resourceSchema = null!;
    private AuthoritativeDs52ContactSeedData _seedData = null!;
    private UpsertResult _createResult = null!;
    private UpdateResult _changedUpdateResult = null!;
    private UpdateResult _noOpUpdateResult = null!;
    private AuthoritativeDs52ContactPersistedState _stateAfterCreate = null!;
    private AuthoritativeDs52ContactPersistedState _stateAfterChangedUpdate = null!;
    private AuthoritativeDs52ContactPersistedState _stateAfterNoOpUpdate = null!;

    [SetUp]
    public async Task Setup()
    {
        _fixture = PostgresqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            AuthoritativeDs52ContactWriteIntegrationTestSupport.FixtureRelativePath
        );
        _mappingSet = new MappingSetCompiler().Compile(_fixture.ModelSet);
        _database = await PostgresqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
        _serviceProvider = AuthoritativeDs52ContactWriteIntegrationTestSupport.CreateServiceProvider();

        var (projectSchema, resourceSchema) =
            AuthoritativeDs52ContactWriteIntegrationTestSupport.GetResourceSchema(
                _fixture.EffectiveSchemaSet,
                "ed-fi",
                "Contact"
            );

        _resourceInfo = AuthoritativeDs52ContactWriteIntegrationTestSupport.CreateResourceInfo(
            projectSchema,
            resourceSchema
        );
        _resourceSchema = resourceSchema;
        _seedData = await SeedReferenceDataAsync();

        _createResult = await ExecuteCreateAsync();
        _createResult.Should().BeOfType<UpsertResult.InsertSuccess>();
        _stateAfterCreate = await ReadPersistedStateAsync(ContactDocumentUuid.Value);

        _changedUpdateResult = await ExecuteUpdateAsync(
            ChangedUpdateRequestBodyJson,
            "pg-authoritative-ds52-contact-changed-update"
        );
        _changedUpdateResult.Should().BeOfType<UpdateResult.UpdateSuccess>();
        _stateAfterChangedUpdate = await ReadPersistedStateAsync(ContactDocumentUuid.Value);

        _noOpUpdateResult = await ExecuteUpdateAsync(
            ChangedUpdateRequestBodyJson,
            "pg-authoritative-ds52-contact-no-op-update"
        );
        _noOpUpdateResult.Should().BeOfType<UpdateResult.UpdateSuccess>();
        _stateAfterNoOpUpdate = await ReadPersistedStateAsync(ContactDocumentUuid.Value);
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
    public void It_persists_authoritative_ds52_contact_addresses_and_nested_address_periods_on_create()
    {
        _createResult.Should().BeOfType<UpsertResult.InsertSuccess>();
        _stateAfterCreate.Document.DocumentUuid.Should().Be(ContactDocumentUuid.Value);
        _stateAfterCreate
            .Document.ResourceKeyId.Should()
            .Be(_mappingSet.ResourceKeyIdByResource[ContactResource]);
        _stateAfterCreate
            .Contact.Should()
            .Be(
                new AuthoritativeDs52ContactRow(
                    _stateAfterCreate.Document.DocumentId,
                    ContactUniqueId,
                    FirstName,
                    LastSurname
                )
            );

        var createdAddressesByStreet = _stateAfterCreate.Addresses.ToDictionary(row => row.StreetNumberName);
        var homeAddress = createdAddressesByStreet[HomeStreetNumberName];
        var workAddress = createdAddressesByStreet[WorkStreetNumberName];

        _stateAfterCreate
            .Addresses.Should()
            .Equal(
                new AuthoritativeDs52ContactAddressRow(
                    homeAddress.CollectionItemId,
                    0,
                    _stateAfterCreate.Document.DocumentId,
                    _seedData.HomeAddressTypeDescriptorId,
                    _seedData.StateAbbreviationDescriptorId,
                    "Austin",
                    "78701",
                    HomeStreetNumberName,
                    false
                ),
                new AuthoritativeDs52ContactAddressRow(
                    workAddress.CollectionItemId,
                    1,
                    _stateAfterCreate.Document.DocumentId,
                    _seedData.WorkAddressTypeDescriptorId,
                    _seedData.StateAbbreviationDescriptorId,
                    "Austin",
                    "78702",
                    WorkStreetNumberName,
                    true
                )
            );

        var homePeriods = GetPeriodsForParent(_stateAfterCreate.AddressPeriods, homeAddress.CollectionItemId);
        var workPeriods = GetPeriodsForParent(_stateAfterCreate.AddressPeriods, workAddress.CollectionItemId);

        homePeriods
            .Should()
            .Equal(
                new AuthoritativeDs52ContactAddressPeriodRow(
                    homePeriods[0].CollectionItemId,
                    0,
                    homeAddress.CollectionItemId,
                    _stateAfterCreate.Document.DocumentId,
                    HomeOriginalPeriodBeginDate,
                    HomeOriginalPeriodEndDate
                ),
                new AuthoritativeDs52ContactAddressPeriodRow(
                    homePeriods[1].CollectionItemId,
                    1,
                    homeAddress.CollectionItemId,
                    _stateAfterCreate.Document.DocumentId,
                    HomeRetainedPeriodBeginDate,
                    HomeCreateRetainedPeriodEndDate
                )
            );
        workPeriods
            .Should()
            .Equal(
                new AuthoritativeDs52ContactAddressPeriodRow(
                    workPeriods[0].CollectionItemId,
                    0,
                    workAddress.CollectionItemId,
                    _stateAfterCreate.Document.DocumentId,
                    WorkPeriodBeginDate,
                    WorkPeriodEndDate
                )
            );
    }

    [Test]
    public void It_reuses_stable_collection_item_ids_for_retained_addresses_and_nested_periods_on_changed_put()
    {
        _changedUpdateResult.Should().BeOfType<UpdateResult.UpdateSuccess>();
        _changedUpdateResult
            .As<UpdateResult.UpdateSuccess>()
            .ExistingDocumentUuid.Should()
            .Be(ContactDocumentUuid);
        _stateAfterChangedUpdate
            .Document.ContentVersion.Should()
            .BeGreaterThan(_stateAfterCreate.Document.ContentVersion);

        var createdAddressesByStreet = _stateAfterCreate.Addresses.ToDictionary(row => row.StreetNumberName);
        var changedAddressesByStreet = _stateAfterChangedUpdate.Addresses.ToDictionary(row =>
            row.StreetNumberName
        );
        var createdHomeAddress = createdAddressesByStreet[HomeStreetNumberName];
        var createdWorkAddress = createdAddressesByStreet[WorkStreetNumberName];
        var changedHomeAddress = changedAddressesByStreet[HomeStreetNumberName];
        var changedTemporaryAddress = changedAddressesByStreet[TemporaryStreetNumberName];

        changedHomeAddress.CollectionItemId.Should().Be(createdHomeAddress.CollectionItemId);
        changedAddressesByStreet.Keys.Should().NotContain(WorkStreetNumberName);
        changedTemporaryAddress.CollectionItemId.Should().NotBe(createdWorkAddress.CollectionItemId);

        _stateAfterChangedUpdate
            .Addresses.Should()
            .Equal(
                new AuthoritativeDs52ContactAddressRow(
                    changedTemporaryAddress.CollectionItemId,
                    0,
                    _stateAfterChangedUpdate.Document.DocumentId,
                    _seedData.TemporaryAddressTypeDescriptorId,
                    _seedData.StateAbbreviationDescriptorId,
                    "Dallas",
                    "75201",
                    TemporaryStreetNumberName,
                    false
                ),
                new AuthoritativeDs52ContactAddressRow(
                    changedHomeAddress.CollectionItemId,
                    1,
                    _stateAfterChangedUpdate.Document.DocumentId,
                    _seedData.HomeAddressTypeDescriptorId,
                    _seedData.StateAbbreviationDescriptorId,
                    "Austin",
                    "78701",
                    HomeStreetNumberName,
                    true
                )
            );

        var createdHomePeriodsByBeginDate = GetPeriodsForParent(
                _stateAfterCreate.AddressPeriods,
                createdHomeAddress.CollectionItemId
            )
            .ToDictionary(row => row.BeginDate);
        var changedHomePeriods = GetPeriodsForParent(
            _stateAfterChangedUpdate.AddressPeriods,
            changedHomeAddress.CollectionItemId
        );
        var changedHomePeriodsByBeginDate = changedHomePeriods.ToDictionary(row => row.BeginDate);
        var changedTemporaryPeriods = GetPeriodsForParent(
            _stateAfterChangedUpdate.AddressPeriods,
            changedTemporaryAddress.CollectionItemId
        );

        changedHomePeriodsByBeginDate[HomeRetainedPeriodBeginDate]
            .CollectionItemId.Should()
            .Be(createdHomePeriodsByBeginDate[HomeRetainedPeriodBeginDate].CollectionItemId);
        changedHomePeriodsByBeginDate.Keys.Should().NotContain(HomeOriginalPeriodBeginDate);
        _stateAfterChangedUpdate
            .AddressPeriods.Should()
            .NotContain(row => row.ParentCollectionItemId == createdWorkAddress.CollectionItemId);
        changedTemporaryPeriods
            .Should()
            .OnlyContain(row => row.ParentCollectionItemId == changedTemporaryAddress.CollectionItemId);

        changedHomePeriods
            .Should()
            .Equal(
                new AuthoritativeDs52ContactAddressPeriodRow(
                    createdHomePeriodsByBeginDate[HomeRetainedPeriodBeginDate].CollectionItemId,
                    0,
                    changedHomeAddress.CollectionItemId,
                    _stateAfterChangedUpdate.Document.DocumentId,
                    HomeRetainedPeriodBeginDate,
                    HomeChangedRetainedPeriodEndDate
                ),
                new AuthoritativeDs52ContactAddressPeriodRow(
                    changedHomePeriodsByBeginDate[HomeReplacementPeriodBeginDate].CollectionItemId,
                    1,
                    changedHomeAddress.CollectionItemId,
                    _stateAfterChangedUpdate.Document.DocumentId,
                    HomeReplacementPeriodBeginDate,
                    null
                )
            );
        changedTemporaryPeriods
            .Should()
            .Equal(
                new AuthoritativeDs52ContactAddressPeriodRow(
                    changedTemporaryPeriods[0].CollectionItemId,
                    0,
                    changedTemporaryAddress.CollectionItemId,
                    _stateAfterChangedUpdate.Document.DocumentId,
                    TemporaryFirstPeriodBeginDate,
                    TemporaryFirstPeriodEndDate
                ),
                new AuthoritativeDs52ContactAddressPeriodRow(
                    changedTemporaryPeriods[1].CollectionItemId,
                    1,
                    changedTemporaryAddress.CollectionItemId,
                    _stateAfterChangedUpdate.Document.DocumentId,
                    TemporarySecondPeriodBeginDate,
                    null
                )
            );
    }

    [Test]
    public void It_keeps_rowsets_and_content_version_unchanged_for_a_repeat_put()
    {
        _noOpUpdateResult.Should().BeOfType<UpdateResult.UpdateSuccess>();
        _noOpUpdateResult
            .As<UpdateResult.UpdateSuccess>()
            .ExistingDocumentUuid.Should()
            .Be(ContactDocumentUuid);
        _stateAfterNoOpUpdate.Should().BeEquivalentTo(_stateAfterChangedUpdate);
    }

    private async Task<UpsertResult> ExecuteCreateAsync()
    {
        await using var scope = _serviceProvider.CreateAsyncScope();
        SetSelectedInstance(scope.ServiceProvider);

        var requestBody = JsonNode.Parse(CreateRequestBodyJson)!;
        var request = new UpsertRequest(
            ResourceInfo: _resourceInfo,
            DocumentInfo: AuthoritativeDs52ContactWriteIntegrationTestSupport.CreateDocumentInfo(
                requestBody,
                _resourceInfo,
                _resourceSchema,
                _mappingSet
            ),
            MappingSet: _mappingSet,
            EdfiDoc: requestBody,
            Headers: [],
            TraceId: new TraceId("pg-authoritative-ds52-contact-create"),
            DocumentUuid: ContactDocumentUuid,
            DocumentSecurityElements: new([], [], [], [], []),
            UpdateCascadeHandler: new AuthoritativeDs52ContactWriteNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new AuthoritativeDs52ContactWriteAllowAllResourceAuthorizationHandler(),
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
            DocumentInfo: AuthoritativeDs52ContactWriteIntegrationTestSupport.CreateDocumentInfo(
                requestBody,
                _resourceInfo,
                _resourceSchema,
                _mappingSet
            ),
            MappingSet: _mappingSet,
            EdfiDoc: requestBody,
            Headers: [],
            TraceId: new TraceId(traceId),
            DocumentUuid: ContactDocumentUuid,
            DocumentSecurityElements: new([], [], [], [], []),
            UpdateCascadeHandler: new AuthoritativeDs52ContactWriteNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new AuthoritativeDs52ContactWriteAllowAllResourceAuthorizationHandler(),
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
                    InstanceName: "PostgresqlRelationalWriteAuthoritativeDs52ContactSmoke",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );
    }

    private async Task<AuthoritativeDs52ContactSeedData> SeedReferenceDataAsync()
    {
        var homeAddressTypeDescriptorId = await SeedDescriptorAsync(
            Guid.Parse("11111111-aaaa-aaaa-aaaa-aaaaaaaaaaa1"),
            "AddressTypeDescriptor",
            "Ed-Fi:AddressTypeDescriptor",
            HomeAddressTypeDescriptorUri,
            "uri://ed-fi.org/AddressTypeDescriptor",
            "Home",
            "Home"
        );
        var workAddressTypeDescriptorId = await SeedDescriptorAsync(
            Guid.Parse("22222222-bbbb-bbbb-bbbb-bbbbbbbbbbb2"),
            "AddressTypeDescriptor",
            "Ed-Fi:AddressTypeDescriptor",
            WorkAddressTypeDescriptorUri,
            "uri://ed-fi.org/AddressTypeDescriptor",
            "Work",
            "Work"
        );
        var temporaryAddressTypeDescriptorId = await SeedDescriptorAsync(
            Guid.Parse("33333333-cccc-cccc-cccc-ccccccccccc3"),
            "AddressTypeDescriptor",
            "Ed-Fi:AddressTypeDescriptor",
            TemporaryAddressTypeDescriptorUri,
            "uri://ed-fi.org/AddressTypeDescriptor",
            "Temporary",
            "Temporary"
        );
        var stateAbbreviationDescriptorId = await SeedDescriptorAsync(
            Guid.Parse("44444444-dddd-dddd-dddd-ddddddddddd4"),
            "StateAbbreviationDescriptor",
            "Ed-Fi:StateAbbreviationDescriptor",
            StateAbbreviationDescriptorUri,
            "uri://ed-fi.org/StateAbbreviationDescriptor",
            "TX",
            "Texas"
        );

        return new(
            HomeAddressTypeDescriptorId: homeAddressTypeDescriptorId,
            WorkAddressTypeDescriptorId: workAddressTypeDescriptorId,
            TemporaryAddressTypeDescriptorId: temporaryAddressTypeDescriptorId,
            StateAbbreviationDescriptorId: stateAbbreviationDescriptorId
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

    private async Task<AuthoritativeDs52ContactPersistedState> ReadPersistedStateAsync(Guid documentUuid)
    {
        var document = await ReadDocumentAsync(documentUuid);

        return new(
            Document: document,
            Contact: await ReadContactAsync(document.DocumentId),
            Addresses: await ReadAddressesAsync(document.DocumentId),
            AddressPeriods: await ReadAddressPeriodsAsync(document.DocumentId)
        );
    }

    private async Task<AuthoritativeDs52ContactDocumentRow> ReadDocumentAsync(Guid documentUuid)
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
            ? new AuthoritativeDs52ContactDocumentRow(
                AuthoritativeDs52ContactWriteIntegrationTestSupport.GetInt64(rows[0], "DocumentId"),
                AuthoritativeDs52ContactWriteIntegrationTestSupport.GetGuid(rows[0], "DocumentUuid"),
                AuthoritativeDs52ContactWriteIntegrationTestSupport.GetInt16(rows[0], "ResourceKeyId"),
                AuthoritativeDs52ContactWriteIntegrationTestSupport.GetInt64(rows[0], "ContentVersion")
            )
            : throw new InvalidOperationException(
                $"Expected exactly one document row for '{documentUuid}', but found {rows.Count}."
            );
    }

    private async Task<AuthoritativeDs52ContactRow> ReadContactAsync(long documentId)
    {
        var rows = await _database.QueryRowsAsync(
            """
            SELECT "DocumentId", "ContactUniqueId", "FirstName", "LastSurname"
            FROM "edfi"."Contact"
            WHERE "DocumentId" = @documentId;
            """,
            new NpgsqlParameter("documentId", documentId)
        );

        return rows.Count == 1
            ? new AuthoritativeDs52ContactRow(
                AuthoritativeDs52ContactWriteIntegrationTestSupport.GetInt64(rows[0], "DocumentId"),
                AuthoritativeDs52ContactWriteIntegrationTestSupport.GetString(rows[0], "ContactUniqueId"),
                AuthoritativeDs52ContactWriteIntegrationTestSupport.GetString(rows[0], "FirstName"),
                AuthoritativeDs52ContactWriteIntegrationTestSupport.GetString(rows[0], "LastSurname")
            )
            : throw new InvalidOperationException(
                $"Expected exactly one Contact row for document id '{documentId}', but found {rows.Count}."
            );
    }

    private async Task<IReadOnlyList<AuthoritativeDs52ContactAddressRow>> ReadAddressesAsync(long documentId)
    {
        var rows = await _database.QueryRowsAsync(
            """
            SELECT
                "CollectionItemId",
                "Ordinal",
                "Contact_DocumentId",
                "AddressTypeDescriptor_DescriptorId",
                "StateAbbreviationDescriptor_DescriptorId",
                "City",
                "PostalCode",
                "StreetNumberName",
                "DoNotPublishIndicator"
            FROM "edfi"."ContactAddress"
            WHERE "Contact_DocumentId" = @documentId
            ORDER BY "Ordinal";
            """,
            new NpgsqlParameter("documentId", documentId)
        );

        return rows.Select(row => new AuthoritativeDs52ContactAddressRow(
                AuthoritativeDs52ContactWriteIntegrationTestSupport.GetInt64(row, "CollectionItemId"),
                AuthoritativeDs52ContactWriteIntegrationTestSupport.GetInt32(row, "Ordinal"),
                AuthoritativeDs52ContactWriteIntegrationTestSupport.GetInt64(row, "Contact_DocumentId"),
                AuthoritativeDs52ContactWriteIntegrationTestSupport.GetInt64(
                    row,
                    "AddressTypeDescriptor_DescriptorId"
                ),
                AuthoritativeDs52ContactWriteIntegrationTestSupport.GetInt64(
                    row,
                    "StateAbbreviationDescriptor_DescriptorId"
                ),
                AuthoritativeDs52ContactWriteIntegrationTestSupport.GetString(row, "City"),
                AuthoritativeDs52ContactWriteIntegrationTestSupport.GetString(row, "PostalCode"),
                AuthoritativeDs52ContactWriteIntegrationTestSupport.GetString(row, "StreetNumberName"),
                AuthoritativeDs52ContactWriteIntegrationTestSupport.GetNullableBoolean(
                    row,
                    "DoNotPublishIndicator"
                )
            ))
            .ToArray();
    }

    private async Task<IReadOnlyList<AuthoritativeDs52ContactAddressPeriodRow>> ReadAddressPeriodsAsync(
        long documentId
    )
    {
        var rows = await _database.QueryRowsAsync(
            """
            SELECT
                "CollectionItemId",
                "Ordinal",
                "ParentCollectionItemId",
                "Contact_DocumentId",
                "BeginDate",
                "EndDate"
            FROM "edfi"."ContactAddressPeriod"
            WHERE "Contact_DocumentId" = @documentId
            ORDER BY "ParentCollectionItemId", "Ordinal";
            """,
            new NpgsqlParameter("documentId", documentId)
        );

        return rows.Select(row => new AuthoritativeDs52ContactAddressPeriodRow(
                AuthoritativeDs52ContactWriteIntegrationTestSupport.GetInt64(row, "CollectionItemId"),
                AuthoritativeDs52ContactWriteIntegrationTestSupport.GetInt32(row, "Ordinal"),
                AuthoritativeDs52ContactWriteIntegrationTestSupport.GetInt64(row, "ParentCollectionItemId"),
                AuthoritativeDs52ContactWriteIntegrationTestSupport.GetInt64(row, "Contact_DocumentId"),
                AuthoritativeDs52ContactWriteIntegrationTestSupport.GetDateOnly(row, "BeginDate"),
                AuthoritativeDs52ContactWriteIntegrationTestSupport.GetNullableDateOnly(row, "EndDate")
            ))
            .ToArray();
    }

    private static IReadOnlyList<AuthoritativeDs52ContactAddressPeriodRow> GetPeriodsForParent(
        IEnumerable<AuthoritativeDs52ContactAddressPeriodRow> rows,
        long parentCollectionItemId
    ) =>
        rows.Where(row => row.ParentCollectionItemId == parentCollectionItemId)
            .OrderBy(row => row.Ordinal)
            .ToArray();
}

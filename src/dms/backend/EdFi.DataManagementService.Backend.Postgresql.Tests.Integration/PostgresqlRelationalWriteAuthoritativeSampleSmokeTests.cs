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

file sealed class AuthoritativeSampleWriteHostApplicationLifetime : IHostApplicationLifetime
{
    public CancellationToken ApplicationStarted => CancellationToken.None;
    public CancellationToken ApplicationStopping => CancellationToken.None;
    public CancellationToken ApplicationStopped => CancellationToken.None;

    public void StopApplication() { }
}

file sealed class AuthoritativeSampleWriteAllowAllResourceAuthorizationHandler : IResourceAuthorizationHandler
{
    public Task<ResourceAuthorizationResult> Authorize(
        DocumentSecurityElements documentSecurityElements,
        OperationType operationType,
        TraceId traceId
    ) => Task.FromResult<ResourceAuthorizationResult>(new ResourceAuthorizationResult.Authorized());
}

file sealed class AuthoritativeSampleWriteNoOpUpdateCascadeHandler : IUpdateCascadeHandler
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

file static class AuthoritativeSampleWriteIntegrationTestSupport
{
    public const string FixtureRelativePath = "src/dms/backend/Fixtures/authoritative/sample";

    public static ServiceProvider CreateServiceProvider()
    {
        ServiceCollection services = [];

        services.AddSingleton<IHostApplicationLifetime, AuthoritativeSampleWriteHostApplicationLifetime>();
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
        ResourceSchema baseResourceSchema,
        ResourceInfo extensionResourceInfo,
        ResourceSchema extensionResourceSchema,
        MappingSet mappingSet,
        long favoriteProgramTypeDescriptorId
    )
    {
        var extensionDocumentReferences = CreateExtensionDocumentReferences(
            requestBody,
            favoriteProgramTypeDescriptorId
        );

        return RelationalDocumentInfoTestHelper.CreateDocumentInfo(
            requestBody,
            resourceInfo,
            baseResourceSchema,
            mappingSet,
            additionalSources:
            [
                new RelationalDocumentInfoExtractionSource(
                    extensionResourceInfo,
                    extensionResourceSchema,
                    UseReferenceExtraction: false,
                    UseRelationalDescriptorExtraction: false
                ),
            ],
            supplement: new RelationalDocumentInfoSupplement(
                DocumentReferences: extensionDocumentReferences,
                DocumentReferenceArrays: [],
                DescriptorReferences: CreateExtensionDescriptorReferences(requestBody)
            ),
            logger: NullLogger.Instance
        );
    }

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

    public static bool GetBoolean(IReadOnlyDictionary<string, object?> row, string columnName) =>
        GetRequiredValue(row, columnName) is bool value
            ? value
            : throw new InvalidOperationException(
                $"Expected column '{columnName}' to contain a boolean value."
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

    public static string FormatReferenceFailure(UpsertResult.UpsertFailureReference failure)
    {
        var documentFailures = failure.InvalidDocumentReferences.Select(reference =>
            $"{reference.Path.Value} -> {reference.TargetResource.ProjectName.Value}.{reference.TargetResource.ResourceName.Value} ({reference.Reason})"
        );
        var descriptorFailures = failure.InvalidDescriptorReferences.Select(reference =>
            $"{reference.Path.Value} -> {reference.TargetResource.ProjectName.Value}.{reference.TargetResource.ResourceName.Value} ({reference.Reason})"
        );

        return string.Join(" | ", documentFailures.Concat(descriptorFailures));
    }

    private static IReadOnlyList<DocumentReference> CreateExtensionDocumentReferences(
        JsonNode requestBody,
        long favoriteProgramTypeDescriptorId
    )
    {
        var favoriteProgramReference = requestBody["_ext"]?["sample"]?["favoriteProgramReference"];

        if (favoriteProgramReference is null)
        {
            return [];
        }

        var educationOrganizationId = requestBody["educationOrganizationReference"]
            ?["educationOrganizationId"]?.GetValue<long>()
            .ToString(CultureInfo.InvariantCulture);
        var programName = favoriteProgramReference["programName"]?.GetValue<string>();

        if (string.IsNullOrWhiteSpace(educationOrganizationId) || string.IsNullOrWhiteSpace(programName))
        {
            throw new InvalidOperationException(
                "Expected favoriteProgramReference and educationOrganizationReference to be present."
            );
        }

        var programResourceInfo = new BaseResourceInfo(
            new ProjectName("Ed-Fi"),
            new ResourceName("Program"),
            false
        );

        var programIdentity = new DocumentIdentity([
            new DocumentIdentityElement(
                new JsonPath("$.educationOrganizationReference.educationOrganizationId"),
                educationOrganizationId
            ),
            new DocumentIdentityElement(new JsonPath("$.programName"), programName),
            new DocumentIdentityElement(
                new JsonPath("$.programTypeDescriptor"),
                favoriteProgramTypeDescriptorId.ToString(CultureInfo.InvariantCulture)
            ),
        ]);

        return
        [
            new DocumentReference(
                ResourceInfo: programResourceInfo,
                DocumentIdentity: programIdentity,
                ReferentialId: ReferentialIdCalculator.ReferentialIdFrom(
                    programResourceInfo,
                    programIdentity
                ),
                Path: new JsonPath("$._ext.sample.favoriteProgramReference")
            ),
        ];
    }

    private static IReadOnlyList<DescriptorReference> CreateExtensionDescriptorReferences(
        JsonNode requestBody
    )
    {
        var favoriteProgramTypeDescriptor = requestBody["_ext"]
            ?["sample"]?["favoriteProgramReference"]?["programTypeDescriptor"]?.GetValue<string>();

        if (string.IsNullOrWhiteSpace(favoriteProgramTypeDescriptor))
        {
            return [];
        }

        var descriptorResourceInfo = new BaseResourceInfo(
            new ProjectName("Ed-Fi"),
            new ResourceName("ProgramTypeDescriptor"),
            true
        );

        var descriptorIdentity = new DocumentIdentity([
            new DocumentIdentityElement(
                DocumentIdentity.DescriptorIdentityJsonPath,
                favoriteProgramTypeDescriptor.ToLowerInvariant()
            ),
        ]);

        return
        [
            new DescriptorReference(
                ResourceInfo: descriptorResourceInfo,
                DocumentIdentity: descriptorIdentity,
                ReferentialId: ReferentialIdCalculator.ReferentialIdFrom(
                    descriptorResourceInfo,
                    descriptorIdentity
                ),
                Path: new JsonPath("$._ext.sample.favoriteProgramReference.programTypeDescriptor")
            ),
        ];
    }
}

internal sealed record AuthoritativeSampleWriteSeedData(
    long SchoolDocumentId,
    long StudentDocumentId,
    long PrimaryProgramDocumentId,
    long SecondaryProgramDocumentId,
    long AddressTypeDescriptorDocumentId,
    long StateAbbreviationDescriptorDocumentId,
    long FallTermDescriptorDocumentId,
    long ProgramTypeDescriptorDocumentId
);

internal sealed record AuthoritativeSampleWriteDocumentRow(
    long DocumentId,
    Guid DocumentUuid,
    short ResourceKeyId,
    long ContentVersion
);

internal sealed record AuthoritativeSampleWriteAssociationRow(
    long DocumentId,
    long EducationOrganizationDocumentId,
    long EducationOrganizationId,
    long StudentDocumentId,
    string StudentUniqueId,
    bool HispanicLatinoEthnicity
);

internal sealed record AuthoritativeSampleWriteAssociationExtensionRow(
    long DocumentId,
    long FavoriteProgramDocumentId,
    long FavoriteProgramEducationOrganizationId,
    string FavoriteProgramName,
    long FavoriteProgramTypeDescriptorId
);

internal sealed record AuthoritativeSampleWriteAssociationAddressRow(
    long CollectionItemId,
    int Ordinal,
    long StudentEducationOrganizationAssociationDocumentId,
    string City,
    string PostalCode,
    string StreetNumberName,
    bool DoNotPublishIndicator
);

internal sealed record AuthoritativeSampleWriteAssociationExtensionAddressRow(
    long BaseCollectionItemId,
    long StudentEducationOrganizationAssociationDocumentId,
    string Complex,
    bool OnBusRoute
);

internal sealed record AuthoritativeSampleWriteAssociationSchoolDistrictRow(
    long CollectionItemId,
    long BaseCollectionItemId,
    int Ordinal,
    long StudentEducationOrganizationAssociationDocumentId,
    string SchoolDistrict
);

internal sealed record AuthoritativeSampleWriteAssociationTermRow(
    long CollectionItemId,
    long BaseCollectionItemId,
    int Ordinal,
    long StudentEducationOrganizationAssociationDocumentId,
    long TermDescriptorId
);

internal sealed record AuthoritativeSampleWritePersistedState(
    AuthoritativeSampleWriteDocumentRow Document,
    AuthoritativeSampleWriteAssociationRow Association,
    AuthoritativeSampleWriteAssociationExtensionRow AssociationExtension,
    IReadOnlyList<AuthoritativeSampleWriteAssociationAddressRow> Addresses,
    IReadOnlyList<AuthoritativeSampleWriteAssociationExtensionAddressRow> ExtensionAddresses,
    IReadOnlyList<AuthoritativeSampleWriteAssociationSchoolDistrictRow> SchoolDistricts,
    IReadOnlyList<AuthoritativeSampleWriteAssociationTermRow> Terms
);

internal sealed record PropagatedReferenceIdentityCascadeSeedData(
    long SchoolDocumentId,
    long StudentDocumentId
);

internal sealed record PropagatedReferenceIdentityCascadePersistedState(
    AuthoritativeSampleWriteDocumentRow Document,
    AuthoritativeSampleWriteAssociationRow Association
);

internal sealed record PropagatedReferenceIdentityCascadeReferenceShape(
    int EducationOrganizationReferenceNonNullCount,
    int StudentReferenceNonNullCount
);

[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
[NonParallelizable]
public class Given_A_Postgresql_Relational_Write_Smoke_With_The_Authoritative_Sample_StudentEducationOrganizationAssociation_Fixture
{
    private const string CreateRequestBodyJson = """
        {
          "educationOrganizationReference": {
            "educationOrganizationId": 100
          },
          "studentReference": {
            "studentUniqueId": "10001"
          },
          "hispanicLatinoEthnicity": true,
          "addresses": [
            {
              "addressTypeDescriptor": "uri://ed-fi.org/AddressTypeDescriptor#Home",
              "city": "Austin",
              "postalCode": "78701",
              "stateAbbreviationDescriptor": "uri://ed-fi.org/StateAbbreviationDescriptor#TX",
              "streetNumberName": "100 Congress Ave",
              "doNotPublishIndicator": false,
              "_ext": {
                "sample": {
                  "complex": "Tower A",
                  "onBusRoute": true,
                  "schoolDistricts": [
                    {
                      "schoolDistrict": "District Nine"
                    }
                  ],
                  "terms": [
                    {
                      "termDescriptor": "uri://ed-fi.org/TermDescriptor#Fall"
                    }
                  ]
                }
              }
            }
          ],
          "_ext": {
            "sample": {
              "favoriteProgramReference": {
                "educationOrganizationId": 100,
                "programName": "Robotics Club",
                "programTypeDescriptor": "uri://ed-fi.org/ProgramTypeDescriptor#Extracurricular"
              }
            }
          }
        }
        """;

    private const string ChangedUpdateRequestBodyJson = """
        {
          "educationOrganizationReference": {
            "educationOrganizationId": 100
          },
          "studentReference": {
            "studentUniqueId": "10001"
          },
          "hispanicLatinoEthnicity": false,
          "addresses": [
            {
              "addressTypeDescriptor": "uri://ed-fi.org/AddressTypeDescriptor#Home",
              "city": "Austin",
              "postalCode": "78701",
              "stateAbbreviationDescriptor": "uri://ed-fi.org/StateAbbreviationDescriptor#TX",
              "streetNumberName": "100 Congress Ave",
              "doNotPublishIndicator": false,
              "_ext": {
                "sample": {
                  "complex": "Tower B",
                  "onBusRoute": false,
                  "schoolDistricts": [
                    {
                      "schoolDistrict": "District Nine"
                    }
                  ],
                  "terms": [
                    {
                      "termDescriptor": "uri://ed-fi.org/TermDescriptor#Fall"
                    }
                  ]
                }
              }
            }
          ],
          "_ext": {
            "sample": {
              "favoriteProgramReference": {
                "educationOrganizationId": 100,
                "programName": "STEM Lab",
                "programTypeDescriptor": "uri://ed-fi.org/ProgramTypeDescriptor#Extracurricular"
              }
            }
          }
        }
        """;

    private static readonly DocumentUuid AssociationDocumentUuid = new(
        Guid.Parse("eeeeeeee-0000-0000-0000-000000000001")
    );

    private PostgresqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private PostgresqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;
    private ResourceInfo _resourceInfo = null!;
    private ResourceInfo _extensionResourceInfo = null!;
    private ResourceSchema _baseResourceSchema = null!;
    private ResourceSchema _extensionResourceSchema = null!;
    private AuthoritativeSampleWriteSeedData _seedData = null!;
    private DbTableModel _schoolDistrictTable = null!;
    private UpsertResult _createResult = null!;
    private UpdateResult _changedUpdateResult = null!;
    private UpdateResult _noOpUpdateResult = null!;
    private AuthoritativeSampleWritePersistedState _stateAfterCreate = null!;
    private AuthoritativeSampleWritePersistedState _stateAfterChangedUpdate = null!;
    private AuthoritativeSampleWritePersistedState _stateAfterNoOpUpdate = null!;

    [SetUp]
    public async Task Setup()
    {
        _fixture = PostgresqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            AuthoritativeSampleWriteIntegrationTestSupport.FixtureRelativePath
        );
        _mappingSet = new MappingSetCompiler().Compile(_fixture.ModelSet);
        _database = await PostgresqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
        _serviceProvider = AuthoritativeSampleWriteIntegrationTestSupport.CreateServiceProvider();
        _schoolDistrictTable = PostgresqlGeneratedDdlModelLookup.RequireTableByScopeAndColumns(
            _fixture.ModelSet,
            "sample",
            "$.addresses[*]._ext.sample.schoolDistricts[*]",
            "StudentEducationOrganizationAssociation_DocumentId",
            "SchoolDistrict"
        );

        var (baseProjectSchema, baseResourceSchema) =
            AuthoritativeSampleWriteIntegrationTestSupport.GetResourceSchema(
                _fixture.EffectiveSchemaSet,
                "ed-fi",
                "StudentEducationOrganizationAssociation"
            );
        var (extensionProjectSchema, extensionResourceSchema) =
            AuthoritativeSampleWriteIntegrationTestSupport.GetResourceSchema(
                _fixture.EffectiveSchemaSet,
                "sample",
                "StudentEducationOrganizationAssociation"
            );

        _resourceInfo = AuthoritativeSampleWriteIntegrationTestSupport.CreateResourceInfo(
            baseProjectSchema,
            baseResourceSchema
        );
        _extensionResourceInfo = AuthoritativeSampleWriteIntegrationTestSupport.CreateResourceInfo(
            extensionProjectSchema,
            extensionResourceSchema
        );
        _baseResourceSchema = baseResourceSchema;
        _extensionResourceSchema = extensionResourceSchema;
        _seedData = await SeedReferenceDataAsync();

        _createResult = await ExecuteCreateAsync();
        if (_createResult is UpsertResult.UpsertFailureReference createReferenceFailure)
        {
            Assert.Fail(
                $"Create reference failure: {AuthoritativeSampleWriteIntegrationTestSupport.FormatReferenceFailure(createReferenceFailure)}"
            );
        }

        _createResult.Should().BeOfType<UpsertResult.InsertSuccess>();
        _stateAfterCreate = await ReadPersistedStateAsync(AssociationDocumentUuid.Value);

        _changedUpdateResult = await ExecuteChangedUpdateAsync();
        _changedUpdateResult.Should().BeOfType<UpdateResult.UpdateSuccess>();
        _stateAfterChangedUpdate = await ReadPersistedStateAsync(AssociationDocumentUuid.Value);

        _noOpUpdateResult = await ExecuteNoOpUpdateAsync();
        _noOpUpdateResult.Should().BeOfType<UpdateResult.UpdateSuccess>();
        _stateAfterNoOpUpdate = await ReadPersistedStateAsync(AssociationDocumentUuid.Value);
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
    public void It_persists_authoritative_sample_base_root_and_extension_rows_on_create()
    {
        _createResult.Should().BeOfType<UpsertResult.InsertSuccess>();
        _stateAfterCreate.Document.DocumentUuid.Should().Be(AssociationDocumentUuid.Value);
        _stateAfterCreate
            .Document.ResourceKeyId.Should()
            .Be(
                _mappingSet.ResourceKeyIdByResource[
                    new QualifiedResourceName("Ed-Fi", "StudentEducationOrganizationAssociation")
                ]
            );
        _stateAfterCreate
            .Association.Should()
            .Be(
                new AuthoritativeSampleWriteAssociationRow(
                    _stateAfterCreate.Document.DocumentId,
                    _seedData.SchoolDocumentId,
                    100,
                    _seedData.StudentDocumentId,
                    "10001",
                    true
                )
            );
        _stateAfterCreate
            .AssociationExtension.Should()
            .Be(
                new AuthoritativeSampleWriteAssociationExtensionRow(
                    _stateAfterCreate.Document.DocumentId,
                    _seedData.PrimaryProgramDocumentId,
                    100,
                    "Robotics Club",
                    _seedData.ProgramTypeDescriptorDocumentId
                )
            );
        _stateAfterCreate
            .Addresses.Should()
            .Equal(
                new AuthoritativeSampleWriteAssociationAddressRow(
                    _stateAfterCreate.Addresses[0].CollectionItemId,
                    0,
                    _stateAfterCreate.Document.DocumentId,
                    "Austin",
                    "78701",
                    "100 Congress Ave",
                    false
                )
            );
        _stateAfterCreate
            .ExtensionAddresses.Should()
            .Equal(
                new AuthoritativeSampleWriteAssociationExtensionAddressRow(
                    _stateAfterCreate.Addresses[0].CollectionItemId,
                    _stateAfterCreate.Document.DocumentId,
                    "Tower A",
                    true
                )
            );
        _stateAfterCreate
            .SchoolDistricts.Should()
            .Equal(
                new AuthoritativeSampleWriteAssociationSchoolDistrictRow(
                    _stateAfterCreate.SchoolDistricts[0].CollectionItemId,
                    _stateAfterCreate.Addresses[0].CollectionItemId,
                    0,
                    _stateAfterCreate.Document.DocumentId,
                    "District Nine"
                )
            );
        _stateAfterCreate
            .Terms.Should()
            .Equal(
                new AuthoritativeSampleWriteAssociationTermRow(
                    _stateAfterCreate.Terms[0].CollectionItemId,
                    _stateAfterCreate.Addresses[0].CollectionItemId,
                    0,
                    _stateAfterCreate.Document.DocumentId,
                    _seedData.FallTermDescriptorDocumentId
                )
            );
    }

    [Test]
    public void It_reuses_stable_collection_item_ids_and_updates_root_extension_data_for_a_changed_put()
    {
        _changedUpdateResult.Should().BeOfType<UpdateResult.UpdateSuccess>();
        _changedUpdateResult
            .As<UpdateResult.UpdateSuccess>()
            .ExistingDocumentUuid.Should()
            .Be(AssociationDocumentUuid);
        _stateAfterChangedUpdate
            .Document.ContentVersion.Should()
            .BeGreaterThan(_stateAfterCreate.Document.ContentVersion);
        _stateAfterChangedUpdate.Document.DocumentUuid.Should().Be(AssociationDocumentUuid.Value);
        _stateAfterChangedUpdate
            .Addresses[0]
            .CollectionItemId.Should()
            .Be(_stateAfterCreate.Addresses[0].CollectionItemId);
        _stateAfterChangedUpdate
            .SchoolDistricts[0]
            .CollectionItemId.Should()
            .Be(_stateAfterCreate.SchoolDistricts[0].CollectionItemId);
        _stateAfterChangedUpdate
            .Terms[0]
            .CollectionItemId.Should()
            .Be(_stateAfterCreate.Terms[0].CollectionItemId);
        _stateAfterChangedUpdate
            .Association.Should()
            .Be(
                new AuthoritativeSampleWriteAssociationRow(
                    _stateAfterChangedUpdate.Document.DocumentId,
                    _seedData.SchoolDocumentId,
                    100,
                    _seedData.StudentDocumentId,
                    "10001",
                    false
                )
            );
        _stateAfterChangedUpdate
            .AssociationExtension.Should()
            .Be(
                new AuthoritativeSampleWriteAssociationExtensionRow(
                    _stateAfterChangedUpdate.Document.DocumentId,
                    _seedData.SecondaryProgramDocumentId,
                    100,
                    "STEM Lab",
                    _seedData.ProgramTypeDescriptorDocumentId
                )
            );
        _stateAfterChangedUpdate
            .ExtensionAddresses.Should()
            .Equal(
                new AuthoritativeSampleWriteAssociationExtensionAddressRow(
                    _stateAfterChangedUpdate.Addresses[0].CollectionItemId,
                    _stateAfterChangedUpdate.Document.DocumentId,
                    "Tower B",
                    false
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
            .Be(AssociationDocumentUuid);
        _stateAfterNoOpUpdate.Should().BeEquivalentTo(_stateAfterChangedUpdate);
    }

    private async Task<UpsertResult> ExecuteCreateAsync()
    {
        await using var scope = _serviceProvider.CreateAsyncScope();

        scope
            .ServiceProvider.GetRequiredService<IDmsInstanceSelection>()
            .SetSelectedDmsInstance(
                new DmsInstance(
                    Id: 1,
                    InstanceType: "test",
                    InstanceName: "PostgresqlRelationalWriteAuthoritativeSampleSmoke",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var requestBody = JsonNode.Parse(CreateRequestBodyJson)!;
        var request = new UpsertRequest(
            ResourceInfo: _resourceInfo,
            DocumentInfo: AuthoritativeSampleWriteIntegrationTestSupport.CreateDocumentInfo(
                requestBody,
                _resourceInfo,
                _baseResourceSchema,
                _extensionResourceInfo,
                _extensionResourceSchema,
                _mappingSet,
                _seedData.ProgramTypeDescriptorDocumentId
            ),
            MappingSet: _mappingSet,
            EdfiDoc: requestBody,
            Headers: [],
            TraceId: new TraceId("pg-authoritative-sample-create"),
            DocumentUuid: AssociationDocumentUuid,
            DocumentSecurityElements: new([], [], [], [], []),
            UpdateCascadeHandler: new AuthoritativeSampleWriteNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new AuthoritativeSampleWriteAllowAllResourceAuthorizationHandler(),
            ResourceAuthorizationPathways: []
        );

        return await scope
            .ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>()
            .UpsertDocument(request);
    }

    private async Task<UpdateResult> ExecuteChangedUpdateAsync()
    {
        await using var scope = _serviceProvider.CreateAsyncScope();

        scope
            .ServiceProvider.GetRequiredService<IDmsInstanceSelection>()
            .SetSelectedDmsInstance(
                new DmsInstance(
                    Id: 1,
                    InstanceType: "test",
                    InstanceName: "PostgresqlRelationalWriteAuthoritativeSampleSmoke",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var requestBody = JsonNode.Parse(ChangedUpdateRequestBodyJson)!;
        var request = new UpdateRequest(
            ResourceInfo: _resourceInfo,
            DocumentInfo: AuthoritativeSampleWriteIntegrationTestSupport.CreateDocumentInfo(
                requestBody,
                _resourceInfo,
                _baseResourceSchema,
                _extensionResourceInfo,
                _extensionResourceSchema,
                _mappingSet,
                _seedData.ProgramTypeDescriptorDocumentId
            ),
            MappingSet: _mappingSet,
            EdfiDoc: requestBody,
            Headers: [],
            TraceId: new TraceId("pg-authoritative-sample-changed-update"),
            DocumentUuid: AssociationDocumentUuid,
            DocumentSecurityElements: new([], [], [], [], []),
            UpdateCascadeHandler: new AuthoritativeSampleWriteNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new AuthoritativeSampleWriteAllowAllResourceAuthorizationHandler(),
            ResourceAuthorizationPathways: []
        );

        return await scope
            .ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>()
            .UpdateDocumentById(request);
    }

    private async Task<UpdateResult> ExecuteNoOpUpdateAsync()
    {
        await using var scope = _serviceProvider.CreateAsyncScope();

        scope
            .ServiceProvider.GetRequiredService<IDmsInstanceSelection>()
            .SetSelectedDmsInstance(
                new DmsInstance(
                    Id: 1,
                    InstanceType: "test",
                    InstanceName: "PostgresqlRelationalWriteAuthoritativeSampleSmoke",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var requestBody = JsonNode.Parse(ChangedUpdateRequestBodyJson)!;
        var request = new UpdateRequest(
            ResourceInfo: _resourceInfo,
            DocumentInfo: AuthoritativeSampleWriteIntegrationTestSupport.CreateDocumentInfo(
                requestBody,
                _resourceInfo,
                _baseResourceSchema,
                _extensionResourceInfo,
                _extensionResourceSchema,
                _mappingSet,
                _seedData.ProgramTypeDescriptorDocumentId
            ),
            MappingSet: _mappingSet,
            EdfiDoc: requestBody,
            Headers: [],
            TraceId: new TraceId("pg-authoritative-sample-no-op-update"),
            DocumentUuid: AssociationDocumentUuid,
            DocumentSecurityElements: new([], [], [], [], []),
            UpdateCascadeHandler: new AuthoritativeSampleWriteNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new AuthoritativeSampleWriteAllowAllResourceAuthorizationHandler(),
            ResourceAuthorizationPathways: []
        );

        return await scope
            .ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>()
            .UpdateDocumentById(request);
    }

    private async Task<AuthoritativeSampleWriteSeedData> SeedReferenceDataAsync()
    {
        var educationOrganizationResourceKeyId = await GetResourceKeyIdAsync(
            "Ed-Fi",
            "EducationOrganization"
        );
        var schoolResourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "School");
        var studentResourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "Student");
        var programResourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "Program");
        var addressTypeDescriptorResourceKeyId = await GetResourceKeyIdAsync(
            "Ed-Fi",
            "AddressTypeDescriptor"
        );
        var stateAbbreviationDescriptorResourceKeyId = await GetResourceKeyIdAsync(
            "Ed-Fi",
            "StateAbbreviationDescriptor"
        );
        var termDescriptorResourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "TermDescriptor");
        var programTypeDescriptorResourceKeyId = await GetResourceKeyIdAsync(
            "Ed-Fi",
            "ProgramTypeDescriptor"
        );

        var schoolDocumentId = await InsertDocumentAsync(
            Guid.Parse("11111111-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            schoolResourceKeyId
        );
        await InsertSchoolAsync(schoolDocumentId, 100, "Alpha Academy");
        await InsertReferentialIdentityAsync(
            CreateReferentialId(("Ed-Fi", "School", false), ("$.schoolId", "100")),
            schoolDocumentId,
            schoolResourceKeyId
        );
        await InsertReferentialIdentityAsync(
            CreateReferentialId(
                ("Ed-Fi", "EducationOrganization", false),
                ("$.educationOrganizationId", "100")
            ),
            schoolDocumentId,
            educationOrganizationResourceKeyId
        );

        var studentDocumentId = await InsertDocumentAsync(
            Guid.Parse("22222222-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            studentResourceKeyId
        );
        await InsertStudentAsync(studentDocumentId, "10001", "Casey", "Cole");
        await InsertReferentialIdentityAsync(
            CreateReferentialId(("Ed-Fi", "Student", false), ("$.studentUniqueId", "10001")),
            studentDocumentId,
            studentResourceKeyId
        );

        var addressTypeDescriptorDocumentId = await InsertDescriptorAsync(
            Guid.Parse("33333333-cccc-cccc-cccc-cccccccccccc"),
            addressTypeDescriptorResourceKeyId,
            "Ed-Fi:AddressTypeDescriptor",
            "uri://ed-fi.org/AddressTypeDescriptor#Home",
            "uri://ed-fi.org/AddressTypeDescriptor",
            "Home",
            "Home"
        );
        await InsertReferentialIdentityAsync(
            CreateDescriptorReferentialId(
                "Ed-Fi",
                "AddressTypeDescriptor",
                "uri://ed-fi.org/AddressTypeDescriptor#Home"
            ),
            addressTypeDescriptorDocumentId,
            addressTypeDescriptorResourceKeyId
        );
        var stateAbbreviationDescriptorDocumentId = await InsertDescriptorAsync(
            Guid.Parse("44444444-dddd-dddd-dddd-dddddddddddd"),
            stateAbbreviationDescriptorResourceKeyId,
            "Ed-Fi:StateAbbreviationDescriptor",
            "uri://ed-fi.org/StateAbbreviationDescriptor#TX",
            "uri://ed-fi.org/StateAbbreviationDescriptor",
            "TX",
            "Texas"
        );
        await InsertReferentialIdentityAsync(
            CreateDescriptorReferentialId(
                "Ed-Fi",
                "StateAbbreviationDescriptor",
                "uri://ed-fi.org/StateAbbreviationDescriptor#TX"
            ),
            stateAbbreviationDescriptorDocumentId,
            stateAbbreviationDescriptorResourceKeyId
        );
        var fallTermDescriptorDocumentId = await InsertDescriptorAsync(
            Guid.Parse("55555555-eeee-eeee-eeee-eeeeeeeeeeee"),
            termDescriptorResourceKeyId,
            "Ed-Fi:TermDescriptor",
            "uri://ed-fi.org/TermDescriptor#Fall",
            "uri://ed-fi.org/TermDescriptor",
            "Fall",
            "Fall"
        );
        await InsertReferentialIdentityAsync(
            CreateDescriptorReferentialId("Ed-Fi", "TermDescriptor", "uri://ed-fi.org/TermDescriptor#Fall"),
            fallTermDescriptorDocumentId,
            termDescriptorResourceKeyId
        );
        var programTypeDescriptorDocumentId = await InsertDescriptorAsync(
            Guid.Parse("66666666-ffff-ffff-ffff-ffffffffffff"),
            programTypeDescriptorResourceKeyId,
            "Ed-Fi:ProgramTypeDescriptor",
            "uri://ed-fi.org/ProgramTypeDescriptor#Extracurricular",
            "uri://ed-fi.org/ProgramTypeDescriptor",
            "Extracurricular",
            "Extracurricular"
        );
        await InsertReferentialIdentityAsync(
            CreateDescriptorReferentialId(
                "Ed-Fi",
                "ProgramTypeDescriptor",
                "uri://ed-fi.org/ProgramTypeDescriptor#Extracurricular"
            ),
            programTypeDescriptorDocumentId,
            programTypeDescriptorResourceKeyId
        );

        var primaryProgramDocumentId = await InsertDocumentAsync(
            Guid.Parse("77777777-1111-1111-1111-111111111111"),
            programResourceKeyId
        );
        await InsertProgramAsync(
            primaryProgramDocumentId,
            schoolDocumentId,
            100,
            programTypeDescriptorDocumentId,
            "PRG-01",
            "Robotics Club"
        );
        await InsertReferentialIdentityAsync(
            CreateReferentialId(
                ("Ed-Fi", "Program", false),
                ("$.educationOrganizationReference.educationOrganizationId", "100"),
                ("$.programName", "Robotics Club"),
                (
                    "$.programTypeDescriptor",
                    programTypeDescriptorDocumentId.ToString(CultureInfo.InvariantCulture)
                )
            ),
            primaryProgramDocumentId,
            programResourceKeyId
        );

        var secondaryProgramDocumentId = await InsertDocumentAsync(
            Guid.Parse("88888888-2222-2222-2222-222222222222"),
            programResourceKeyId
        );
        await InsertProgramAsync(
            secondaryProgramDocumentId,
            schoolDocumentId,
            100,
            programTypeDescriptorDocumentId,
            "PRG-02",
            "STEM Lab"
        );
        await InsertReferentialIdentityAsync(
            CreateReferentialId(
                ("Ed-Fi", "Program", false),
                ("$.educationOrganizationReference.educationOrganizationId", "100"),
                ("$.programName", "STEM Lab"),
                (
                    "$.programTypeDescriptor",
                    programTypeDescriptorDocumentId.ToString(CultureInfo.InvariantCulture)
                )
            ),
            secondaryProgramDocumentId,
            programResourceKeyId
        );

        return new(
            schoolDocumentId,
            studentDocumentId,
            primaryProgramDocumentId,
            secondaryProgramDocumentId,
            addressTypeDescriptorDocumentId,
            stateAbbreviationDescriptorDocumentId,
            fallTermDescriptorDocumentId,
            programTypeDescriptorDocumentId
        );
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

    private async Task InsertSchoolAsync(long documentId, int schoolId, string nameOfInstitution)
    {
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO "edfi"."School" ("DocumentId", "NameOfInstitution", "SchoolId")
            VALUES (@documentId, @nameOfInstitution, @schoolId);
            """,
            new NpgsqlParameter("documentId", documentId),
            new NpgsqlParameter("nameOfInstitution", nameOfInstitution),
            new NpgsqlParameter("schoolId", schoolId)
        );
    }

    private async Task InsertStudentAsync(
        long documentId,
        string studentUniqueId,
        string firstName,
        string lastSurname
    )
    {
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO "edfi"."Student" ("DocumentId", "BirthDate", "FirstName", "LastSurname", "StudentUniqueId")
            VALUES (@documentId, @birthDate, @firstName, @lastSurname, @studentUniqueId);
            """,
            new NpgsqlParameter("documentId", documentId),
            new NpgsqlParameter("birthDate", new DateOnly(2010, 1, 1)),
            new NpgsqlParameter("firstName", firstName),
            new NpgsqlParameter("lastSurname", lastSurname),
            new NpgsqlParameter("studentUniqueId", studentUniqueId)
        );
    }

    private async Task InsertProgramAsync(
        long documentId,
        long educationOrganizationDocumentId,
        int educationOrganizationId,
        long programTypeDescriptorId,
        string programId,
        string programName
    )
    {
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO "edfi"."Program" (
                "DocumentId",
                "EducationOrganization_DocumentId",
                "EducationOrganization_EducationOrganizationId",
                "ProgramTypeDescriptor_DescriptorId",
                "ProgramId",
                "ProgramName"
            )
            VALUES (
                @documentId,
                @educationOrganizationDocumentId,
                @educationOrganizationId,
                @programTypeDescriptorId,
                @programId,
                @programName
            );
            """,
            new NpgsqlParameter("documentId", documentId),
            new NpgsqlParameter("educationOrganizationDocumentId", educationOrganizationDocumentId),
            new NpgsqlParameter("educationOrganizationId", educationOrganizationId),
            new NpgsqlParameter("programTypeDescriptorId", programTypeDescriptorId),
            new NpgsqlParameter("programId", programId),
            new NpgsqlParameter("programName", programName)
        );
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

    private async Task<AuthoritativeSampleWritePersistedState> ReadPersistedStateAsync(Guid documentUuid)
    {
        var document = await ReadDocumentAsync(documentUuid);

        return new(
            Document: document,
            Association: await ReadAssociationAsync(document.DocumentId),
            AssociationExtension: await ReadAssociationExtensionAsync(document.DocumentId),
            Addresses: await ReadAddressesAsync(document.DocumentId),
            ExtensionAddresses: await ReadExtensionAddressesAsync(document.DocumentId),
            SchoolDistricts: await ReadSchoolDistrictsAsync(document.DocumentId),
            Terms: await ReadTermsAsync(document.DocumentId)
        );
    }

    private async Task<AuthoritativeSampleWriteDocumentRow> ReadDocumentAsync(Guid documentUuid)
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
            ? new AuthoritativeSampleWriteDocumentRow(
                AuthoritativeSampleWriteIntegrationTestSupport.GetInt64(rows[0], "DocumentId"),
                AuthoritativeSampleWriteIntegrationTestSupport.GetGuid(rows[0], "DocumentUuid"),
                AuthoritativeSampleWriteIntegrationTestSupport.GetInt16(rows[0], "ResourceKeyId"),
                AuthoritativeSampleWriteIntegrationTestSupport.GetInt64(rows[0], "ContentVersion")
            )
            : throw new InvalidOperationException(
                $"Expected exactly one document row for '{documentUuid}', but found {rows.Count}."
            );
    }

    private async Task<AuthoritativeSampleWriteAssociationRow> ReadAssociationAsync(long documentId)
    {
        var rows = await _database.QueryRowsAsync(
            """
            SELECT
                "DocumentId",
                "EducationOrganization_DocumentId",
                "EducationOrganization_EducationOrganizationId",
                "Student_DocumentId",
                "Student_StudentUniqueId",
                "HispanicLatinoEthnicity"
            FROM "edfi"."StudentEducationOrganizationAssociation"
            WHERE "DocumentId" = @documentId;
            """,
            new NpgsqlParameter("documentId", documentId)
        );

        return rows.Count == 1
            ? new AuthoritativeSampleWriteAssociationRow(
                AuthoritativeSampleWriteIntegrationTestSupport.GetInt64(rows[0], "DocumentId"),
                AuthoritativeSampleWriteIntegrationTestSupport.GetInt64(
                    rows[0],
                    "EducationOrganization_DocumentId"
                ),
                AuthoritativeSampleWriteIntegrationTestSupport.GetInt64(
                    rows[0],
                    "EducationOrganization_EducationOrganizationId"
                ),
                AuthoritativeSampleWriteIntegrationTestSupport.GetInt64(rows[0], "Student_DocumentId"),
                AuthoritativeSampleWriteIntegrationTestSupport.GetString(rows[0], "Student_StudentUniqueId"),
                AuthoritativeSampleWriteIntegrationTestSupport.GetBoolean(rows[0], "HispanicLatinoEthnicity")
            )
            : throw new InvalidOperationException(
                $"Expected exactly one association row for document id '{documentId}', but found {rows.Count}."
            );
    }

    private async Task<AuthoritativeSampleWriteAssociationExtensionRow> ReadAssociationExtensionAsync(
        long documentId
    )
    {
        var rows = await _database.QueryRowsAsync(
            """
            SELECT
                "DocumentId",
                "FavoriteProgram_DocumentId",
                "FavoriteProgram_EducationOrganizationId",
                "FavoriteProgram_ProgramName",
                "FavoriteProgram_ProgramTypeDescriptor_DescriptorId"
            FROM "sample"."StudentEducationOrganizationAssociationExtension"
            WHERE "DocumentId" = @documentId;
            """,
            new NpgsqlParameter("documentId", documentId)
        );

        return rows.Count == 1
            ? new AuthoritativeSampleWriteAssociationExtensionRow(
                AuthoritativeSampleWriteIntegrationTestSupport.GetInt64(rows[0], "DocumentId"),
                AuthoritativeSampleWriteIntegrationTestSupport.GetInt64(
                    rows[0],
                    "FavoriteProgram_DocumentId"
                ),
                AuthoritativeSampleWriteIntegrationTestSupport.GetInt64(
                    rows[0],
                    "FavoriteProgram_EducationOrganizationId"
                ),
                AuthoritativeSampleWriteIntegrationTestSupport.GetString(
                    rows[0],
                    "FavoriteProgram_ProgramName"
                ),
                AuthoritativeSampleWriteIntegrationTestSupport.GetInt64(
                    rows[0],
                    "FavoriteProgram_ProgramTypeDescriptor_DescriptorId"
                )
            )
            : throw new InvalidOperationException(
                $"Expected exactly one extension row for document id '{documentId}', but found {rows.Count}."
            );
    }

    private async Task<IReadOnlyList<AuthoritativeSampleWriteAssociationAddressRow>> ReadAddressesAsync(
        long documentId
    )
    {
        var rows = await _database.QueryRowsAsync(
            """
            SELECT
                "CollectionItemId",
                "Ordinal",
                "StudentEducationOrganizationAssociation_DocumentId",
                "City",
                "PostalCode",
                "StreetNumberName",
                "DoNotPublishIndicator"
            FROM "edfi"."StudentEducationOrganizationAssociationAddress"
            WHERE "StudentEducationOrganizationAssociation_DocumentId" = @documentId
            ORDER BY "Ordinal";
            """,
            new NpgsqlParameter("documentId", documentId)
        );

        return rows.Select(row => new AuthoritativeSampleWriteAssociationAddressRow(
                AuthoritativeSampleWriteIntegrationTestSupport.GetInt64(row, "CollectionItemId"),
                AuthoritativeSampleWriteIntegrationTestSupport.GetInt32(row, "Ordinal"),
                AuthoritativeSampleWriteIntegrationTestSupport.GetInt64(
                    row,
                    "StudentEducationOrganizationAssociation_DocumentId"
                ),
                AuthoritativeSampleWriteIntegrationTestSupport.GetString(row, "City"),
                AuthoritativeSampleWriteIntegrationTestSupport.GetString(row, "PostalCode"),
                AuthoritativeSampleWriteIntegrationTestSupport.GetString(row, "StreetNumberName"),
                AuthoritativeSampleWriteIntegrationTestSupport.GetBoolean(row, "DoNotPublishIndicator")
            ))
            .ToArray();
    }

    private async Task<
        IReadOnlyList<AuthoritativeSampleWriteAssociationExtensionAddressRow>
    > ReadExtensionAddressesAsync(long documentId)
    {
        var rows = await _database.QueryRowsAsync(
            """
            SELECT
                "BaseCollectionItemId",
                "StudentEducationOrganizationAssociation_DocumentId",
                "Complex",
                "OnBusRoute"
            FROM "sample"."StudentEducationOrganizationAssociationExtensionAddress"
            WHERE "StudentEducationOrganizationAssociation_DocumentId" = @documentId
            ORDER BY "BaseCollectionItemId";
            """,
            new NpgsqlParameter("documentId", documentId)
        );

        return rows.Select(row => new AuthoritativeSampleWriteAssociationExtensionAddressRow(
                AuthoritativeSampleWriteIntegrationTestSupport.GetInt64(row, "BaseCollectionItemId"),
                AuthoritativeSampleWriteIntegrationTestSupport.GetInt64(
                    row,
                    "StudentEducationOrganizationAssociation_DocumentId"
                ),
                AuthoritativeSampleWriteIntegrationTestSupport.GetString(row, "Complex"),
                AuthoritativeSampleWriteIntegrationTestSupport.GetBoolean(row, "OnBusRoute")
            ))
            .ToArray();
    }

    private async Task<
        IReadOnlyList<AuthoritativeSampleWriteAssociationSchoolDistrictRow>
    > ReadSchoolDistrictsAsync(long documentId)
    {
        var rows = await _database.QueryRowsAsync(
            $"""
            SELECT
                "CollectionItemId",
                "BaseCollectionItemId",
                "Ordinal",
                "StudentEducationOrganizationAssociation_DocumentId",
                "SchoolDistrict"
            FROM "{_schoolDistrictTable.Table.Schema.Value}"."{_schoolDistrictTable.Table.Name}"
            WHERE "StudentEducationOrganizationAssociation_DocumentId" = @documentId
            ORDER BY "Ordinal";
            """,
            new NpgsqlParameter("documentId", documentId)
        );

        return rows.Select(row => new AuthoritativeSampleWriteAssociationSchoolDistrictRow(
                AuthoritativeSampleWriteIntegrationTestSupport.GetInt64(row, "CollectionItemId"),
                AuthoritativeSampleWriteIntegrationTestSupport.GetInt64(row, "BaseCollectionItemId"),
                AuthoritativeSampleWriteIntegrationTestSupport.GetInt32(row, "Ordinal"),
                AuthoritativeSampleWriteIntegrationTestSupport.GetInt64(
                    row,
                    "StudentEducationOrganizationAssociation_DocumentId"
                ),
                AuthoritativeSampleWriteIntegrationTestSupport.GetString(row, "SchoolDistrict")
            ))
            .ToArray();
    }

    private async Task<IReadOnlyList<AuthoritativeSampleWriteAssociationTermRow>> ReadTermsAsync(
        long documentId
    )
    {
        var rows = await _database.QueryRowsAsync(
            """
            SELECT
                "CollectionItemId",
                "BaseCollectionItemId",
                "Ordinal",
                "StudentEducationOrganizationAssociation_DocumentId",
                "TermDescriptor_DescriptorId"
            FROM "sample"."StudentEducationOrganizationAssociationExtensionAddressTerm"
            WHERE "StudentEducationOrganizationAssociation_DocumentId" = @documentId
            ORDER BY "Ordinal";
            """,
            new NpgsqlParameter("documentId", documentId)
        );

        return rows.Select(row => new AuthoritativeSampleWriteAssociationTermRow(
                AuthoritativeSampleWriteIntegrationTestSupport.GetInt64(row, "CollectionItemId"),
                AuthoritativeSampleWriteIntegrationTestSupport.GetInt64(row, "BaseCollectionItemId"),
                AuthoritativeSampleWriteIntegrationTestSupport.GetInt32(row, "Ordinal"),
                AuthoritativeSampleWriteIntegrationTestSupport.GetInt64(
                    row,
                    "StudentEducationOrganizationAssociation_DocumentId"
                ),
                AuthoritativeSampleWriteIntegrationTestSupport.GetInt64(row, "TermDescriptor_DescriptorId")
            ))
            .ToArray();
    }
}

[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
[NonParallelizable]
public class Given_A_Postgresql_Relational_Write_Propagated_Reference_Identity_Cascade_With_The_Authoritative_Sample_StudentEducationOrganizationAssociation_Fixture
{
    private const long EducationOrganizationId = 100;
    private const long UpdatedEducationOrganizationId = 101;
    private const string StudentUniqueId = "10001";

    private const string CreateRequestBodyJson = """
        {
          "educationOrganizationReference": {
            "educationOrganizationId": 100
          },
          "studentReference": {
            "studentUniqueId": "10001"
          },
          "hispanicLatinoEthnicity": true
        }
        """;

    private static readonly DocumentUuid AssociationDocumentUuid = new(
        Guid.Parse("eeeeeeee-0000-0000-0000-000000000011")
    );

    private PostgresqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private PostgresqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;
    private ResourceInfo _resourceInfo = null!;
    private ResourceInfo _extensionResourceInfo = null!;
    private ResourceSchema _baseResourceSchema = null!;
    private ResourceSchema _extensionResourceSchema = null!;
    private PropagatedReferenceIdentityCascadeSeedData _seedData = null!;
    private UpsertResult _createResult = null!;
    private PropagatedReferenceIdentityCascadePersistedState _stateAfterCreate = null!;
    private PropagatedReferenceIdentityCascadeReferenceShape _shapeAfterCreate = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _fixture = PostgresqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            AuthoritativeSampleWriteIntegrationTestSupport.FixtureRelativePath
        );
        _mappingSet = new MappingSetCompiler().Compile(_fixture.ModelSet);
        _database = await PostgresqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
        _serviceProvider = AuthoritativeSampleWriteIntegrationTestSupport.CreateServiceProvider();

        var (baseProjectSchema, baseResourceSchema) =
            AuthoritativeSampleWriteIntegrationTestSupport.GetResourceSchema(
                _fixture.EffectiveSchemaSet,
                "ed-fi",
                "StudentEducationOrganizationAssociation"
            );
        var (extensionProjectSchema, extensionResourceSchema) =
            AuthoritativeSampleWriteIntegrationTestSupport.GetResourceSchema(
                _fixture.EffectiveSchemaSet,
                "sample",
                "StudentEducationOrganizationAssociation"
            );

        _resourceInfo = AuthoritativeSampleWriteIntegrationTestSupport.CreateResourceInfo(
            baseProjectSchema,
            baseResourceSchema
        );
        _extensionResourceInfo = AuthoritativeSampleWriteIntegrationTestSupport.CreateResourceInfo(
            extensionProjectSchema,
            extensionResourceSchema
        );
        _baseResourceSchema = baseResourceSchema;
        _extensionResourceSchema = extensionResourceSchema;
        _seedData = await SeedReferenceDataAsync();

        _createResult = await ExecuteCreateAsync();

        if (_createResult is UpsertResult.UpsertFailureReference createReferenceFailure)
        {
            Assert.Fail(
                $"Create reference failure: {AuthoritativeSampleWriteIntegrationTestSupport.FormatReferenceFailure(createReferenceFailure)}"
            );
        }

        _createResult.Should().BeOfType<UpsertResult.InsertSuccess>();
        _stateAfterCreate = await ReadPersistedStateAsync(AssociationDocumentUuid.Value);
        _shapeAfterCreate = await ReadReferenceShapeAsync(_stateAfterCreate.Document.DocumentId);
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
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
    public void It_should_store_runtime_written_reference_identity_columns_in_all_or_none_shape()
    {
        _createResult.Should().BeOfType<UpsertResult.InsertSuccess>();
        _stateAfterCreate.Document.DocumentUuid.Should().Be(AssociationDocumentUuid.Value);
        _stateAfterCreate
            .Document.ResourceKeyId.Should()
            .Be(
                _mappingSet.ResourceKeyIdByResource[
                    new QualifiedResourceName("Ed-Fi", "StudentEducationOrganizationAssociation")
                ]
            );
        _stateAfterCreate
            .Association.Should()
            .Be(
                new AuthoritativeSampleWriteAssociationRow(
                    _stateAfterCreate.Document.DocumentId,
                    _seedData.SchoolDocumentId,
                    EducationOrganizationId,
                    _seedData.StudentDocumentId,
                    StudentUniqueId,
                    true
                )
            );
        _shapeAfterCreate.Should().Be(new PropagatedReferenceIdentityCascadeReferenceShape(2, 2));
    }

    [Test]
    public async Task It_should_cascade_abstract_reference_identity_updates_into_runtime_written_reference_columns()
    {
        await _database.ExecuteNonQueryAsync(
            """
            UPDATE "edfi"."School"
            SET "SchoolId" = @schoolId
            WHERE "DocumentId" = @documentId;
            """,
            new NpgsqlParameter("schoolId", UpdatedEducationOrganizationId),
            new NpgsqlParameter("documentId", _seedData.SchoolDocumentId)
        );

        var stateAfterCascade = await ReadPersistedStateAsync(AssociationDocumentUuid.Value);
        var shapeAfterCascade = await ReadReferenceShapeAsync(stateAfterCascade.Document.DocumentId);

        stateAfterCascade
            .Association.Should()
            .Be(
                _stateAfterCreate.Association with
                {
                    EducationOrganizationId = UpdatedEducationOrganizationId,
                }
            );
        shapeAfterCascade.Should().Be(_shapeAfterCreate);
    }

    private async Task<UpsertResult> ExecuteCreateAsync()
    {
        await using var scope = _serviceProvider.CreateAsyncScope();
        SetSelectedInstance(scope.ServiceProvider);

        var requestBody = JsonNode.Parse(CreateRequestBodyJson)!;
        var request = new UpsertRequest(
            ResourceInfo: _resourceInfo,
            DocumentInfo: AuthoritativeSampleWriteIntegrationTestSupport.CreateDocumentInfo(
                requestBody,
                _resourceInfo,
                _baseResourceSchema,
                _extensionResourceInfo,
                _extensionResourceSchema,
                _mappingSet,
                0
            ),
            MappingSet: _mappingSet,
            EdfiDoc: requestBody,
            Headers: [],
            TraceId: new TraceId("pg-propagated-reference-identity-cascade-create"),
            DocumentUuid: AssociationDocumentUuid,
            DocumentSecurityElements: new([], [], [], [], []),
            UpdateCascadeHandler: new AuthoritativeSampleWriteNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new AuthoritativeSampleWriteAllowAllResourceAuthorizationHandler(),
            ResourceAuthorizationPathways: []
        );

        return await scope
            .ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>()
            .UpsertDocument(request);
    }

    private void SetSelectedInstance(IServiceProvider serviceProvider)
    {
        serviceProvider
            .GetRequiredService<IDmsInstanceSelection>()
            .SetSelectedDmsInstance(
                new DmsInstance(
                    Id: 1,
                    InstanceType: "test",
                    InstanceName: "PostgresqlRelationalWritePropagatedReferenceIdentityCascade",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );
    }

    private async Task<PropagatedReferenceIdentityCascadeSeedData> SeedReferenceDataAsync()
    {
        var educationOrganizationResourceKeyId = await GetResourceKeyIdAsync(
            "Ed-Fi",
            "EducationOrganization"
        );
        var schoolResourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "School");
        var studentResourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "Student");

        var schoolDocumentId = await InsertDocumentAsync(
            Guid.Parse("11111111-aaaa-aaaa-aaaa-aaaaaaaaaaab"),
            schoolResourceKeyId
        );
        await InsertSchoolAsync(schoolDocumentId, EducationOrganizationId, "Alpha Academy");
        await InsertReferentialIdentityAsync(
            CreateReferentialId(
                ("Ed-Fi", "School", false),
                ("$.schoolId", EducationOrganizationId.ToString(CultureInfo.InvariantCulture))
            ),
            schoolDocumentId,
            schoolResourceKeyId
        );
        await InsertReferentialIdentityAsync(
            CreateReferentialId(
                ("Ed-Fi", "EducationOrganization", false),
                ("$.educationOrganizationId", EducationOrganizationId.ToString(CultureInfo.InvariantCulture))
            ),
            schoolDocumentId,
            educationOrganizationResourceKeyId
        );

        var studentDocumentId = await InsertDocumentAsync(
            Guid.Parse("22222222-bbbb-bbbb-bbbb-bbbbbbbbbbbc"),
            studentResourceKeyId
        );
        await InsertStudentAsync(studentDocumentId, StudentUniqueId, "Casey", "Cole");
        await InsertReferentialIdentityAsync(
            CreateReferentialId(("Ed-Fi", "Student", false), ("$.studentUniqueId", StudentUniqueId)),
            studentDocumentId,
            studentResourceKeyId
        );

        return new(schoolDocumentId, studentDocumentId);
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

    private async Task InsertSchoolAsync(long documentId, long schoolId, string nameOfInstitution)
    {
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO "edfi"."School" ("DocumentId", "NameOfInstitution", "SchoolId")
            VALUES (@documentId, @nameOfInstitution, @schoolId);
            """,
            new NpgsqlParameter("documentId", documentId),
            new NpgsqlParameter("nameOfInstitution", nameOfInstitution),
            new NpgsqlParameter("schoolId", schoolId)
        );
    }

    private async Task InsertStudentAsync(
        long documentId,
        string studentUniqueId,
        string firstName,
        string lastSurname
    )
    {
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO "edfi"."Student" ("DocumentId", "BirthDate", "FirstName", "LastSurname", "StudentUniqueId")
            VALUES (@documentId, @birthDate, @firstName, @lastSurname, @studentUniqueId);
            """,
            new NpgsqlParameter("documentId", documentId),
            new NpgsqlParameter("birthDate", new DateOnly(2010, 1, 1)),
            new NpgsqlParameter("firstName", firstName),
            new NpgsqlParameter("lastSurname", lastSurname),
            new NpgsqlParameter("studentUniqueId", studentUniqueId)
        );
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

    private async Task<PropagatedReferenceIdentityCascadePersistedState> ReadPersistedStateAsync(
        Guid documentUuid
    )
    {
        var document = await ReadDocumentAsync(documentUuid);

        return new(Document: document, Association: await ReadAssociationAsync(document.DocumentId));
    }

    private async Task<AuthoritativeSampleWriteDocumentRow> ReadDocumentAsync(Guid documentUuid)
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
            ? new AuthoritativeSampleWriteDocumentRow(
                AuthoritativeSampleWriteIntegrationTestSupport.GetInt64(rows[0], "DocumentId"),
                AuthoritativeSampleWriteIntegrationTestSupport.GetGuid(rows[0], "DocumentUuid"),
                AuthoritativeSampleWriteIntegrationTestSupport.GetInt16(rows[0], "ResourceKeyId"),
                AuthoritativeSampleWriteIntegrationTestSupport.GetInt64(rows[0], "ContentVersion")
            )
            : throw new InvalidOperationException(
                $"Expected exactly one document row for '{documentUuid}', but found {rows.Count}."
            );
    }

    private async Task<AuthoritativeSampleWriteAssociationRow> ReadAssociationAsync(long documentId)
    {
        var rows = await _database.QueryRowsAsync(
            """
            SELECT
                "DocumentId",
                "EducationOrganization_DocumentId",
                "EducationOrganization_EducationOrganizationId",
                "Student_DocumentId",
                "Student_StudentUniqueId",
                "HispanicLatinoEthnicity"
            FROM "edfi"."StudentEducationOrganizationAssociation"
            WHERE "DocumentId" = @documentId;
            """,
            new NpgsqlParameter("documentId", documentId)
        );

        return rows.Count == 1
            ? new AuthoritativeSampleWriteAssociationRow(
                AuthoritativeSampleWriteIntegrationTestSupport.GetInt64(rows[0], "DocumentId"),
                AuthoritativeSampleWriteIntegrationTestSupport.GetInt64(
                    rows[0],
                    "EducationOrganization_DocumentId"
                ),
                AuthoritativeSampleWriteIntegrationTestSupport.GetInt64(
                    rows[0],
                    "EducationOrganization_EducationOrganizationId"
                ),
                AuthoritativeSampleWriteIntegrationTestSupport.GetInt64(rows[0], "Student_DocumentId"),
                AuthoritativeSampleWriteIntegrationTestSupport.GetString(rows[0], "Student_StudentUniqueId"),
                AuthoritativeSampleWriteIntegrationTestSupport.GetBoolean(rows[0], "HispanicLatinoEthnicity")
            )
            : throw new InvalidOperationException(
                $"Expected exactly one association row for document id '{documentId}', but found {rows.Count}."
            );
    }

    private async Task<PropagatedReferenceIdentityCascadeReferenceShape> ReadReferenceShapeAsync(
        long documentId
    )
    {
        var rows = await _database.QueryRowsAsync(
            """
            SELECT
                num_nonnulls(
                    "EducationOrganization_DocumentId",
                    "EducationOrganization_EducationOrganizationId"
                ) AS "EducationOrganizationReferenceNonNullCount",
                num_nonnulls("Student_DocumentId", "Student_StudentUniqueId") AS "StudentReferenceNonNullCount"
            FROM "edfi"."StudentEducationOrganizationAssociation"
            WHERE "DocumentId" = @documentId;
            """,
            new NpgsqlParameter("documentId", documentId)
        );

        return rows.Count == 1
            ? new PropagatedReferenceIdentityCascadeReferenceShape(
                AuthoritativeSampleWriteIntegrationTestSupport.GetInt32(
                    rows[0],
                    "EducationOrganizationReferenceNonNullCount"
                ),
                AuthoritativeSampleWriteIntegrationTestSupport.GetInt32(
                    rows[0],
                    "StudentReferenceNonNullCount"
                )
            )
            : throw new InvalidOperationException(
                $"Expected exactly one association shape row for document id '{documentId}', but found {rows.Count}."
            );
    }
}

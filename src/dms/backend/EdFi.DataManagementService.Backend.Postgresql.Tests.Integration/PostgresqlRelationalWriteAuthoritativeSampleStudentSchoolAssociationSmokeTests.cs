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

file sealed class AuthoritativeSampleStudentSchoolAssociationWriteHostApplicationLifetime
    : IHostApplicationLifetime
{
    public CancellationToken ApplicationStarted => CancellationToken.None;
    public CancellationToken ApplicationStopping => CancellationToken.None;
    public CancellationToken ApplicationStopped => CancellationToken.None;

    public void StopApplication() { }
}

file sealed class AuthoritativeSampleStudentSchoolAssociationAllowAllResourceAuthorizationHandler
    : IResourceAuthorizationHandler
{
    public Task<ResourceAuthorizationResult> Authorize(
        DocumentSecurityElements documentSecurityElements,
        OperationType operationType,
        TraceId traceId
    ) => Task.FromResult<ResourceAuthorizationResult>(new ResourceAuthorizationResult.Authorized());
}

file sealed class AuthoritativeSampleStudentSchoolAssociationNoOpUpdateCascadeHandler : IUpdateCascadeHandler
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

file sealed record AuthoritativeSampleStudentSchoolAssociationRelationalGetRequest(
    DocumentUuid DocumentUuid,
    BaseResourceInfo ResourceInfo,
    MappingSet MappingSet,
    IResourceAuthorizationHandler ResourceAuthorizationHandler,
    TraceId TraceId,
    RelationalGetRequestReadMode ReadMode = RelationalGetRequestReadMode.ExternalResponse,
    ReadableProfileProjectionContext? ReadableProfileProjectionContext = null
) : IRelationalGetRequest
{
    public ResourceName ResourceName => ResourceInfo.ResourceName;
}

file static class AuthoritativeSampleStudentSchoolAssociationIntegrationTestSupport
{
    public const string FixtureRelativePath = "src/dms/backend/Fixtures/authoritative/sample";

    public static ServiceProvider CreateServiceProvider()
    {
        ServiceCollection services = [];

        services.AddSingleton<
            IHostApplicationLifetime,
            AuthoritativeSampleStudentSchoolAssociationWriteHostApplicationLifetime
        >();
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
        ResourceSchema baseResourceSchema,
        MappingSet mappingSet,
        long graduationPlanTypeDescriptorId
    )
    {
        var (alternativeGraduationPlanReferences, alternativeGraduationPlanReferenceArrays) =
            CreateAlternativeGraduationPlanDocumentReferences(requestBody, graduationPlanTypeDescriptorId);
        var documentInfo = RelationalDocumentInfoTestHelper.CreateDocumentInfo(
            requestBody,
            resourceInfo,
            baseResourceSchema,
            mappingSet,
            logger: NullLogger.Instance
        );

        return documentInfo with
        {
            DocumentReferences =
            [
                .. documentInfo.DocumentReferences.Where(reference =>
                    !reference.Path.Value.StartsWith(
                        "$.alternativeGraduationPlans[",
                        StringComparison.Ordinal
                    )
                ),
                .. alternativeGraduationPlanReferences,
            ],
            DocumentReferenceArrays =
            [
                .. documentInfo.DocumentReferenceArrays.Where(referenceArray =>
                    referenceArray.arrayPath
                    != new JsonPath("$.alternativeGraduationPlans[*].alternativeGraduationPlanReference")
                ),
                .. alternativeGraduationPlanReferenceArrays,
            ],
        };
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

    public static DateOnly GetDateOnly(IReadOnlyDictionary<string, object?> row, string columnName) =>
        GetRequiredValue(row, columnName) switch
        {
            DateOnly value => value,
            DateTime value => DateOnly.FromDateTime(value),
            _ => throw new InvalidOperationException(
                $"Expected column '{columnName}' to contain a DateOnly value."
            ),
        };

    public static DateTimeOffset GetDateTimeOffset(
        IReadOnlyDictionary<string, object?> row,
        string columnName
    ) =>
        GetRequiredValue(row, columnName) switch
        {
            DateTimeOffset value => value,
            DateTime value => new DateTimeOffset(
                DateTime.SpecifyKind(value, DateTimeKind.Utc),
                TimeSpan.Zero
            ),
            _ => throw new InvalidOperationException(
                $"Expected column '{columnName}' to contain a DateTimeOffset value."
            ),
        };

    public static string FormatReferenceFailure(UpsertResult.UpsertFailureReference failure) =>
        FormatReferenceFailure(failure.InvalidDocumentReferences, failure.InvalidDescriptorReferences);

    public static string FormatReferenceFailure(UpdateResult.UpdateFailureReference failure) =>
        FormatReferenceFailure(failure.InvalidDocumentReferences, failure.InvalidDescriptorReferences);

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

    private static string FormatReferenceFailure(
        DocumentReferenceFailure[] invalidDocumentReferences,
        DescriptorReferenceFailure[] invalidDescriptorReferences
    )
    {
        var documentFailures = invalidDocumentReferences.Select(reference =>
            $"{reference.Path.Value} -> {reference.TargetResource.ProjectName.Value}.{reference.TargetResource.ResourceName.Value} ({reference.Reason})"
        );
        var descriptorFailures = invalidDescriptorReferences.Select(reference =>
            $"{reference.Path.Value} -> {reference.TargetResource.ProjectName.Value}.{reference.TargetResource.ResourceName.Value} ({reference.Reason})"
        );

        return string.Join(" | ", documentFailures.Concat(descriptorFailures));
    }

    private static (
        IReadOnlyList<DocumentReference> DocumentReferences,
        IReadOnlyList<DocumentReferenceArray> DocumentReferenceArrays
    ) CreateAlternativeGraduationPlanDocumentReferences(
        JsonNode requestBody,
        long graduationPlanTypeDescriptorId
    )
    {
        var alternativeGraduationPlans = requestBody["alternativeGraduationPlans"] as JsonArray;

        if (alternativeGraduationPlans is null || alternativeGraduationPlans.Count == 0)
        {
            return ([], []);
        }

        var targetResourceInfo = new BaseResourceInfo(
            new ProjectName("Ed-Fi"),
            new ResourceName("GraduationPlan"),
            false
        );
        var descriptorValue = graduationPlanTypeDescriptorId.ToString(CultureInfo.InvariantCulture);
        List<DocumentReference> documentReferences = [];

        for (var index = 0; index < alternativeGraduationPlans.Count; index++)
        {
            var reference = alternativeGraduationPlans[index]?["alternativeGraduationPlanReference"];

            if (reference is null)
            {
                throw new InvalidOperationException(
                    $"Expected alternativeGraduationPlanReference at array index {index}."
                );
            }

            var educationOrganizationId = reference["educationOrganizationId"]
                ?.GetValue<long>()
                .ToString(CultureInfo.InvariantCulture);
            var graduationSchoolYear = reference["graduationSchoolYear"]
                ?.GetValue<int>()
                .ToString(CultureInfo.InvariantCulture);

            if (
                string.IsNullOrWhiteSpace(educationOrganizationId)
                || string.IsNullOrWhiteSpace(graduationSchoolYear)
            )
            {
                throw new InvalidOperationException(
                    "Expected every alternativeGraduationPlanReference to contain all identity members."
                );
            }

            var documentIdentity = new DocumentIdentity([
                new DocumentIdentityElement(
                    new JsonPath("$.educationOrganizationReference.educationOrganizationId"),
                    educationOrganizationId
                ),
                new DocumentIdentityElement(new JsonPath("$.graduationPlanTypeDescriptor"), descriptorValue),
                new DocumentIdentityElement(
                    new JsonPath("$.graduationSchoolYearTypeReference.schoolYear"),
                    graduationSchoolYear
                ),
            ]);

            documentReferences.Add(
                new DocumentReference(
                    ResourceInfo: targetResourceInfo,
                    DocumentIdentity: documentIdentity,
                    ReferentialId: ReferentialIdCalculator.ReferentialIdFrom(
                        targetResourceInfo,
                        documentIdentity
                    ),
                    Path: new JsonPath(
                        $"$.alternativeGraduationPlans[{index}].alternativeGraduationPlanReference"
                    )
                )
            );
        }

        return (
            documentReferences,
            [
                new DocumentReferenceArray(
                    new JsonPath("$.alternativeGraduationPlans[*].alternativeGraduationPlanReference"),
                    [.. documentReferences]
                ),
            ]
        );
    }
}

internal sealed record AuthoritativeSampleStudentSchoolAssociationSeedData(
    long SchoolDocumentId,
    long ConflictSchoolDocumentId,
    long CalendarDocumentId,
    long ConflictCalendarDocumentId,
    long StudentDocumentId,
    long StudentSchoolYearTypeDocumentId,
    long NinthGradeLevelDescriptorId,
    long TenthGradeLevelDescriptorId,
    long ResidentMembershipTypeDescriptorId,
    long TransferMembershipTypeDescriptorId,
    long PathwayEducationPlanDescriptorId,
    long InterventionEducationPlanDescriptorId,
    long CareerEducationPlanDescriptorId,
    long GraduationPlanTypeDescriptorId,
    long FoundationGraduationPlanDocumentId,
    long EndorsementGraduationPlanDocumentId,
    long StemGraduationPlanDocumentId
);

internal sealed record AuthoritativeSampleStudentSchoolAssociationDocumentRow(
    long DocumentId,
    Guid DocumentUuid,
    short ResourceKeyId,
    long ContentVersion
);

internal sealed record AuthoritativeSampleStudentSchoolAssociationRow(
    long DocumentId,
    long SchoolIdUnified,
    int SchoolYearUnified,
    long CalendarDocumentId,
    string CalendarCode,
    long SchoolYearDocumentId,
    long SchoolDocumentId,
    long StudentDocumentId,
    string StudentUniqueId,
    long EntryGradeLevelDescriptorId,
    DateOnly EntryDate,
    bool PrimarySchool
);

internal sealed record AuthoritativeSampleStudentSchoolAssociationExtensionRow(
    long DocumentId,
    long MembershipTypeDescriptorId
);

internal sealed record AuthoritativeSampleStudentSchoolAssociationAlternativeGraduationPlanRow(
    long CollectionItemId,
    int Ordinal,
    long StudentSchoolAssociationDocumentId,
    long AlternativeGraduationPlanDocumentId,
    long AlternativeGraduationPlanEducationOrganizationId,
    long AlternativeGraduationPlanGraduationPlanTypeDescriptorId,
    int AlternativeGraduationPlanGraduationSchoolYear
);

internal sealed record AuthoritativeSampleStudentSchoolAssociationEducationPlanRow(
    long CollectionItemId,
    int Ordinal,
    long StudentSchoolAssociationDocumentId,
    long EducationPlanDescriptorId
);

internal sealed record AuthoritativeSampleStudentSchoolAssociationPersistedState(
    AuthoritativeSampleStudentSchoolAssociationDocumentRow Document,
    AuthoritativeSampleStudentSchoolAssociationRow Association,
    AuthoritativeSampleStudentSchoolAssociationExtensionRow AssociationExtension,
    IReadOnlyList<AuthoritativeSampleStudentSchoolAssociationAlternativeGraduationPlanRow> AlternativeGraduationPlans,
    IReadOnlyList<AuthoritativeSampleStudentSchoolAssociationEducationPlanRow> EducationPlans
);

internal sealed record AuthoritativeSampleStudentSchoolAssociationRejectedWriteSnapshot(
    IReadOnlyList<Guid> DocumentUuids,
    IReadOnlyList<long> AssociationDocumentIds,
    IReadOnlyList<long> AssociationExtensionDocumentIds,
    IReadOnlyList<long> AlternativeGraduationPlanCollectionItemIds,
    IReadOnlyList<long> EducationPlanCollectionItemIds
);

[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
[NonParallelizable]
public class Given_A_Postgresql_Relational_Write_Smoke_With_The_Authoritative_Sample_StudentSchoolAssociation_Fixture
{
    private const long SchoolId = 100;
    private const long ConflictSchoolId = 200;
    private const int SchoolYear = 2024;
    private const int FoundationGraduationSchoolYear = 2026;
    private const int EndorsementGraduationSchoolYear = 2027;
    private const int StemGraduationSchoolYear = 2028;
    private const string StudentUniqueId = "10001";
    private const string CalendarCode = "MAIN";
    private const string ConflictCalendarCode = "ALT";
    private const string NinthGradeLevelDescriptorUri = "uri://ed-fi.org/GradeLevelDescriptor#Ninth grade";
    private const string TenthGradeLevelDescriptorUri = "uri://ed-fi.org/GradeLevelDescriptor#Tenth grade";
    private const string CalendarTypeDescriptorUri = "uri://ed-fi.org/CalendarTypeDescriptor#Instructional";
    private const string GraduationPlanTypeDescriptorUri =
        "uri://ed-fi.org/GraduationPlanTypeDescriptor#Foundation";
    private const string PathwayEducationPlanDescriptorUri =
        "uri://ed-fi.org/EducationPlanDescriptor#Pathway";
    private const string InterventionEducationPlanDescriptorUri =
        "uri://ed-fi.org/EducationPlanDescriptor#Intervention";
    private const string CareerEducationPlanDescriptorUri = "uri://ed-fi.org/EducationPlanDescriptor#Career";
    private const string ResidentMembershipTypeDescriptorUri =
        "uri://sample.org/MembershipTypeDescriptor#Resident";
    private const string TransferMembershipTypeDescriptorUri =
        "uri://sample.org/MembershipTypeDescriptor#Transfer";

    private const string CreateRequestBodyJson = """
        {
          "entryDate": "2024-08-20",
          "entryGradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Ninth grade",
          "primarySchool": true,
          "schoolReference": {
            "schoolId": 100
          },
          "calendarReference": {
            "calendarCode": "MAIN",
            "schoolId": 100,
            "schoolYear": 2024
          },
          "schoolYearTypeReference": {
            "schoolYear": 2024
          },
          "studentReference": {
            "studentUniqueId": "10001"
          },
          "alternativeGraduationPlans": [
            {
              "alternativeGraduationPlanReference": {
                "educationOrganizationId": 100,
                "graduationPlanTypeDescriptor": "uri://ed-fi.org/GraduationPlanTypeDescriptor#Foundation",
                "graduationSchoolYear": 2026
              }
            },
            {
              "alternativeGraduationPlanReference": {
                "educationOrganizationId": 100,
                "graduationPlanTypeDescriptor": "uri://ed-fi.org/GraduationPlanTypeDescriptor#Foundation",
                "graduationSchoolYear": 2027
              }
            }
          ],
          "educationPlans": [
            {
              "educationPlanDescriptor": "uri://ed-fi.org/EducationPlanDescriptor#Pathway"
            },
            {
              "educationPlanDescriptor": "uri://ed-fi.org/EducationPlanDescriptor#Intervention"
            }
          ],
          "_ext": {
            "sample": {
              "membershipTypeDescriptor": "uri://sample.org/MembershipTypeDescriptor#Resident"
            }
          }
        }
        """;

    private const string ChangedUpdateRequestBodyJson = """
        {
          "entryDate": "2024-08-20",
          "entryGradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth grade",
          "primarySchool": false,
          "schoolReference": {
            "schoolId": 100
          },
          "calendarReference": {
            "calendarCode": "MAIN",
            "schoolId": 100,
            "schoolYear": 2024
          },
          "schoolYearTypeReference": {
            "schoolYear": 2024
          },
          "studentReference": {
            "studentUniqueId": "10001"
          },
          "alternativeGraduationPlans": [
            {
              "alternativeGraduationPlanReference": {
                "educationOrganizationId": 100,
                "graduationPlanTypeDescriptor": "uri://ed-fi.org/GraduationPlanTypeDescriptor#Foundation",
                "graduationSchoolYear": 2027
              }
            },
            {
              "alternativeGraduationPlanReference": {
                "educationOrganizationId": 100,
                "graduationPlanTypeDescriptor": "uri://ed-fi.org/GraduationPlanTypeDescriptor#Foundation",
                "graduationSchoolYear": 2028
              }
            }
          ],
          "educationPlans": [
            {
              "educationPlanDescriptor": "uri://ed-fi.org/EducationPlanDescriptor#Intervention"
            },
            {
              "educationPlanDescriptor": "uri://ed-fi.org/EducationPlanDescriptor#Career"
            }
          ],
          "_ext": {
            "sample": {
              "membershipTypeDescriptor": "uri://sample.org/MembershipTypeDescriptor#Transfer"
            }
          }
        }
        """;

    private static readonly DateOnly EntryDate = new(2024, 8, 20);

    private static readonly DocumentUuid StudentSchoolAssociationDocumentUuid = new(
        Guid.Parse("abababab-0000-0000-0000-000000000001")
    );

    private PostgresqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private PostgresqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;
    private ResourceInfo _resourceInfo = null!;
    private ResourceSchema _baseResourceSchema = null!;
    private AuthoritativeSampleStudentSchoolAssociationSeedData _seedData = null!;
    private UpsertResult _createResult = null!;
    private UpdateResult _changedUpdateResult = null!;
    private UpdateResult _noOpUpdateResult = null!;
    private AuthoritativeSampleStudentSchoolAssociationPersistedState _stateAfterCreate = null!;
    private AuthoritativeSampleStudentSchoolAssociationPersistedState _stateAfterChangedUpdate = null!;
    private AuthoritativeSampleStudentSchoolAssociationPersistedState _stateAfterNoOpUpdate = null!;

    [SetUp]
    public async Task Setup()
    {
        _fixture = PostgresqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            AuthoritativeSampleStudentSchoolAssociationIntegrationTestSupport.FixtureRelativePath
        );
        _mappingSet = new MappingSetCompiler().Compile(_fixture.ModelSet);
        _database = await PostgresqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
        _serviceProvider =
            AuthoritativeSampleStudentSchoolAssociationIntegrationTestSupport.CreateServiceProvider();

        var (baseProjectSchema, baseResourceSchema) =
            AuthoritativeSampleStudentSchoolAssociationIntegrationTestSupport.GetResourceSchema(
                _fixture.EffectiveSchemaSet,
                "ed-fi",
                "StudentSchoolAssociation"
            );
        _resourceInfo = AuthoritativeSampleStudentSchoolAssociationIntegrationTestSupport.CreateResourceInfo(
            baseProjectSchema,
            baseResourceSchema
        );
        _baseResourceSchema = baseResourceSchema;
        _seedData = await SeedReferenceDataAsync();
        await DisableStudentSchoolAssociationReferentialIdentityTriggerAsync();

        _createResult = await ExecuteCreateAsync(
            CreateRequestBodyJson,
            StudentSchoolAssociationDocumentUuid,
            "pg-authoritative-sample-student-school-association-create"
        );

        if (_createResult is UpsertResult.UpsertFailureReference createReferenceFailure)
        {
            Assert.Fail(
                $"Create reference failure: {AuthoritativeSampleStudentSchoolAssociationIntegrationTestSupport.FormatReferenceFailure(createReferenceFailure)}"
            );
        }

        _createResult.Should().BeOfType<UpsertResult.InsertSuccess>();
        _stateAfterCreate = await ReadPersistedStateAsync(StudentSchoolAssociationDocumentUuid.Value);

        _changedUpdateResult = await ExecuteUpdateAsync(
            ChangedUpdateRequestBodyJson,
            "pg-authoritative-sample-student-school-association-changed-update"
        );

        if (_changedUpdateResult is UpdateResult.UpdateFailureReference changedUpdateReferenceFailure)
        {
            Assert.Fail(
                $"Changed update reference failure: {AuthoritativeSampleStudentSchoolAssociationIntegrationTestSupport.FormatReferenceFailure(changedUpdateReferenceFailure)}"
            );
        }

        _changedUpdateResult.Should().BeOfType<UpdateResult.UpdateSuccess>();
        _stateAfterChangedUpdate = await ReadPersistedStateAsync(StudentSchoolAssociationDocumentUuid.Value);

        _noOpUpdateResult = await ExecuteUpdateAsync(
            ChangedUpdateRequestBodyJson,
            "pg-authoritative-sample-student-school-association-no-op-update"
        );

        if (_noOpUpdateResult is UpdateResult.UpdateFailureReference noOpReferenceFailure)
        {
            Assert.Fail(
                $"No-op update reference failure: {AuthoritativeSampleStudentSchoolAssociationIntegrationTestSupport.FormatReferenceFailure(noOpReferenceFailure)}"
            );
        }

        _noOpUpdateResult.Should().BeOfType<UpdateResult.UpdateSuccess>();
        _stateAfterNoOpUpdate = await ReadPersistedStateAsync(StudentSchoolAssociationDocumentUuid.Value);
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
    public void It_extracts_descriptor_valued_collection_reference_members_from_concrete_paths_via_the_shared_document_info_helper()
    {
        var documentInfo =
            AuthoritativeSampleStudentSchoolAssociationIntegrationTestSupport.CreateDocumentInfo(
                JsonNode.Parse(CreateRequestBodyJson)!,
                _resourceInfo,
                _baseResourceSchema,
                _mappingSet,
                _seedData.GraduationPlanTypeDescriptorId
            );

        documentInfo
            .DescriptorReferences.Select(reference =>
                (
                    Path: reference.Path.Value,
                    ResourceName: reference.ResourceInfo.ResourceName.Value,
                    DescriptorValue: reference
                        .DocumentIdentity.DocumentIdentityElements.Single()
                        .IdentityValue
                )
            )
            .Should()
            .Contain(
                (
                    "$.alternativeGraduationPlans[0].alternativeGraduationPlanReference.graduationPlanTypeDescriptor",
                    "GraduationPlanTypeDescriptor",
                    GraduationPlanTypeDescriptorUri.ToLowerInvariant()
                )
            )
            .And.Contain(
                (
                    "$.alternativeGraduationPlans[1].alternativeGraduationPlanReference.graduationPlanTypeDescriptor",
                    "GraduationPlanTypeDescriptor",
                    GraduationPlanTypeDescriptorUri.ToLowerInvariant()
                )
            );
    }

    [Test]
    public void It_persists_authoritative_student_school_association_root_extension_and_child_rows_on_create()
    {
        _createResult.Should().BeOfType<UpsertResult.InsertSuccess>();
        _stateAfterCreate.Document.DocumentUuid.Should().Be(StudentSchoolAssociationDocumentUuid.Value);
        _stateAfterCreate
            .Document.ResourceKeyId.Should()
            .Be(
                _mappingSet.ResourceKeyIdByResource[
                    new QualifiedResourceName("Ed-Fi", "StudentSchoolAssociation")
                ]
            );
        _stateAfterCreate
            .Association.Should()
            .Be(
                new AuthoritativeSampleStudentSchoolAssociationRow(
                    _stateAfterCreate.Document.DocumentId,
                    SchoolId,
                    SchoolYear,
                    _seedData.CalendarDocumentId,
                    CalendarCode,
                    _seedData.StudentSchoolYearTypeDocumentId,
                    _seedData.SchoolDocumentId,
                    _seedData.StudentDocumentId,
                    StudentUniqueId,
                    _seedData.NinthGradeLevelDescriptorId,
                    EntryDate,
                    true
                )
            );
        _stateAfterCreate
            .AssociationExtension.Should()
            .Be(
                new AuthoritativeSampleStudentSchoolAssociationExtensionRow(
                    _stateAfterCreate.Document.DocumentId,
                    _seedData.ResidentMembershipTypeDescriptorId
                )
            );
        _stateAfterCreate
            .AlternativeGraduationPlans.Should()
            .Equal(
                new AuthoritativeSampleStudentSchoolAssociationAlternativeGraduationPlanRow(
                    _stateAfterCreate.AlternativeGraduationPlans[0].CollectionItemId,
                    0,
                    _stateAfterCreate.Document.DocumentId,
                    _seedData.FoundationGraduationPlanDocumentId,
                    SchoolId,
                    _seedData.GraduationPlanTypeDescriptorId,
                    FoundationGraduationSchoolYear
                ),
                new AuthoritativeSampleStudentSchoolAssociationAlternativeGraduationPlanRow(
                    _stateAfterCreate.AlternativeGraduationPlans[1].CollectionItemId,
                    1,
                    _stateAfterCreate.Document.DocumentId,
                    _seedData.EndorsementGraduationPlanDocumentId,
                    SchoolId,
                    _seedData.GraduationPlanTypeDescriptorId,
                    EndorsementGraduationSchoolYear
                )
            );
        _stateAfterCreate
            .EducationPlans.Should()
            .Equal(
                new AuthoritativeSampleStudentSchoolAssociationEducationPlanRow(
                    _stateAfterCreate.EducationPlans[0].CollectionItemId,
                    0,
                    _stateAfterCreate.Document.DocumentId,
                    _seedData.PathwayEducationPlanDescriptorId
                ),
                new AuthoritativeSampleStudentSchoolAssociationEducationPlanRow(
                    _stateAfterCreate.EducationPlans[1].CollectionItemId,
                    1,
                    _stateAfterCreate.Document.DocumentId,
                    _seedData.InterventionEducationPlanDescriptorId
                )
            );
    }

    [Test]
    public void It_reuses_stable_collection_item_ids_and_updates_authoritative_state_on_changed_put()
    {
        _changedUpdateResult.Should().BeOfType<UpdateResult.UpdateSuccess>();
        _changedUpdateResult
            .As<UpdateResult.UpdateSuccess>()
            .ExistingDocumentUuid.Should()
            .Be(StudentSchoolAssociationDocumentUuid);
        _stateAfterChangedUpdate
            .Document.ContentVersion.Should()
            .BeGreaterThan(_stateAfterCreate.Document.ContentVersion);
        _stateAfterChangedUpdate
            .Association.Should()
            .Be(
                new AuthoritativeSampleStudentSchoolAssociationRow(
                    _stateAfterChangedUpdate.Document.DocumentId,
                    SchoolId,
                    SchoolYear,
                    _seedData.CalendarDocumentId,
                    CalendarCode,
                    _seedData.StudentSchoolYearTypeDocumentId,
                    _seedData.SchoolDocumentId,
                    _seedData.StudentDocumentId,
                    StudentUniqueId,
                    _seedData.TenthGradeLevelDescriptorId,
                    EntryDate,
                    false
                )
            );
        _stateAfterChangedUpdate
            .AssociationExtension.Should()
            .Be(
                new AuthoritativeSampleStudentSchoolAssociationExtensionRow(
                    _stateAfterChangedUpdate.Document.DocumentId,
                    _seedData.TransferMembershipTypeDescriptorId
                )
            );

        var createdAlternativePlansByDocumentId = _stateAfterCreate.AlternativeGraduationPlans.ToDictionary(
            row => row.AlternativeGraduationPlanDocumentId
        );
        var changedAlternativePlansByDocumentId =
            _stateAfterChangedUpdate.AlternativeGraduationPlans.ToDictionary(row =>
                row.AlternativeGraduationPlanDocumentId
            );

        changedAlternativePlansByDocumentId[_seedData.EndorsementGraduationPlanDocumentId]
            .CollectionItemId.Should()
            .Be(
                createdAlternativePlansByDocumentId[
                    _seedData.EndorsementGraduationPlanDocumentId
                ].CollectionItemId
            );
        changedAlternativePlansByDocumentId
            .Keys.Should()
            .NotContain(_seedData.FoundationGraduationPlanDocumentId);
        changedAlternativePlansByDocumentId[_seedData.StemGraduationPlanDocumentId]
            .CollectionItemId.Should()
            .NotBe(
                createdAlternativePlansByDocumentId[
                    _seedData.FoundationGraduationPlanDocumentId
                ].CollectionItemId
            );

        _stateAfterChangedUpdate
            .AlternativeGraduationPlans.Should()
            .Equal(
                new AuthoritativeSampleStudentSchoolAssociationAlternativeGraduationPlanRow(
                    createdAlternativePlansByDocumentId[
                        _seedData.EndorsementGraduationPlanDocumentId
                    ].CollectionItemId,
                    0,
                    _stateAfterChangedUpdate.Document.DocumentId,
                    _seedData.EndorsementGraduationPlanDocumentId,
                    SchoolId,
                    _seedData.GraduationPlanTypeDescriptorId,
                    EndorsementGraduationSchoolYear
                ),
                new AuthoritativeSampleStudentSchoolAssociationAlternativeGraduationPlanRow(
                    changedAlternativePlansByDocumentId[
                        _seedData.StemGraduationPlanDocumentId
                    ].CollectionItemId,
                    1,
                    _stateAfterChangedUpdate.Document.DocumentId,
                    _seedData.StemGraduationPlanDocumentId,
                    SchoolId,
                    _seedData.GraduationPlanTypeDescriptorId,
                    StemGraduationSchoolYear
                )
            );

        var createdEducationPlansByDescriptorId = _stateAfterCreate.EducationPlans.ToDictionary(row =>
            row.EducationPlanDescriptorId
        );
        var changedEducationPlansByDescriptorId = _stateAfterChangedUpdate.EducationPlans.ToDictionary(row =>
            row.EducationPlanDescriptorId
        );

        changedEducationPlansByDescriptorId[_seedData.InterventionEducationPlanDescriptorId]
            .CollectionItemId.Should()
            .Be(
                createdEducationPlansByDescriptorId[
                    _seedData.InterventionEducationPlanDescriptorId
                ].CollectionItemId
            );
        changedEducationPlansByDescriptorId
            .Keys.Should()
            .NotContain(_seedData.PathwayEducationPlanDescriptorId);
        changedEducationPlansByDescriptorId[_seedData.CareerEducationPlanDescriptorId]
            .CollectionItemId.Should()
            .NotBe(
                createdEducationPlansByDescriptorId[
                    _seedData.PathwayEducationPlanDescriptorId
                ].CollectionItemId
            );

        _stateAfterChangedUpdate
            .EducationPlans.Should()
            .Equal(
                new AuthoritativeSampleStudentSchoolAssociationEducationPlanRow(
                    createdEducationPlansByDescriptorId[
                        _seedData.InterventionEducationPlanDescriptorId
                    ].CollectionItemId,
                    0,
                    _stateAfterChangedUpdate.Document.DocumentId,
                    _seedData.InterventionEducationPlanDescriptorId
                ),
                new AuthoritativeSampleStudentSchoolAssociationEducationPlanRow(
                    changedEducationPlansByDescriptorId[
                        _seedData.CareerEducationPlanDescriptorId
                    ].CollectionItemId,
                    1,
                    _stateAfterChangedUpdate.Document.DocumentId,
                    _seedData.CareerEducationPlanDescriptorId
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
            .Be(StudentSchoolAssociationDocumentUuid);
        _stateAfterNoOpUpdate.Should().BeEquivalentTo(_stateAfterChangedUpdate);
    }

    [Test]
    public async Task It_reads_back_the_written_document_via_relational_get_by_id_with_semantic_json_equivalence_and_metadata()
    {
        var getResult = await ExecuteGetByIdAsync(
            StudentSchoolAssociationDocumentUuid,
            "pg-authoritative-sample-student-school-association-get-by-id"
        );
        var expectedLastModifiedAt = await ReadContentLastModifiedAtAsync(
            StudentSchoolAssociationDocumentUuid.Value
        );
        var expectedDocument = CreateExpectedExternalResponse(
            ChangedUpdateRequestBodyJson,
            _stateAfterNoOpUpdate.Document,
            expectedLastModifiedAt
        );

        getResult.Should().BeOfType<GetResult.GetSuccess>();

        var success = getResult.As<GetResult.GetSuccess>();

        success.DocumentUuid.Should().Be(StudentSchoolAssociationDocumentUuid);
        success.LastModifiedTraceId.Should().BeNull();
        success.LastModifiedDate.Should().Be(expectedLastModifiedAt.UtcDateTime);
        success.EdfiDoc["id"]!
            .GetValue<string>()
            .Should()
            .Be(StudentSchoolAssociationDocumentUuid.Value.ToString());
        success.EdfiDoc["_etag"]!
            .GetValue<string>()
            .Should()
            .Be($"\"{_stateAfterNoOpUpdate.Document.ContentVersion}\"");
        success.EdfiDoc["_lastModifiedDate"]!
            .GetValue<string>()
            .Should()
            .Be(FormatExternalLastModifiedDate(expectedLastModifiedAt));
        success.EdfiDoc["alternativeGraduationPlans"]!
            .AsArray()
            .Select(plan =>
                plan?["alternativeGraduationPlanReference"]?["graduationSchoolYear"]?.GetValue<int>()
            )
            .Should()
            .Equal(EndorsementGraduationSchoolYear, StemGraduationSchoolYear);
        success.EdfiDoc["educationPlans"]!
            .AsArray()
            .Select(plan => plan?["educationPlanDescriptor"]?.GetValue<string>())
            .Should()
            .Equal(InterventionEducationPlanDescriptorUri, CareerEducationPlanDescriptorUri);
        CanonicalizeJson(success.EdfiDoc).Should().Be(CanonicalizeJson(expectedDocument));
    }

    private async Task<GetResult> ExecuteGetByIdAsync(DocumentUuid documentUuid, string traceId)
    {
        await using var scope = _serviceProvider.CreateAsyncScope();
        SetSelectedInstance(scope.ServiceProvider);

        var request = new AuthoritativeSampleStudentSchoolAssociationRelationalGetRequest(
            DocumentUuid: documentUuid,
            ResourceInfo: _resourceInfo,
            MappingSet: _mappingSet,
            ResourceAuthorizationHandler: new AuthoritativeSampleStudentSchoolAssociationAllowAllResourceAuthorizationHandler(),
            TraceId: new TraceId(traceId)
        );

        return await scope
            .ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>()
            .GetDocumentById(request);
    }

    private async Task<DateTimeOffset> ReadContentLastModifiedAtAsync(Guid documentUuid)
    {
        var rows = await _database.QueryRowsAsync(
            """
            SELECT "ContentLastModifiedAt"
            FROM "dms"."Document"
            WHERE "DocumentUuid" = @documentUuid;
            """,
            new NpgsqlParameter("documentUuid", documentUuid)
        );

        return rows.Count == 1
            ? AuthoritativeSampleStudentSchoolAssociationIntegrationTestSupport.GetDateTimeOffset(
                rows[0],
                "ContentLastModifiedAt"
            )
            : throw new InvalidOperationException(
                $"Expected exactly one document metadata row for '{documentUuid}', but found {rows.Count}."
            );
    }

    private static JsonNode CreateExpectedExternalResponse(
        string requestBodyJson,
        AuthoritativeSampleStudentSchoolAssociationDocumentRow document,
        DateTimeOffset lastModifiedAt
    )
    {
        var expectedDocument =
            JsonNode.Parse(requestBodyJson)?.AsObject()
            ?? throw new InvalidOperationException("Expected request body JSON to parse into a JSON object.");

        expectedDocument["id"] = document.DocumentUuid.ToString();
        expectedDocument["_etag"] = $"\"{document.ContentVersion}\"";
        expectedDocument["_lastModifiedDate"] = FormatExternalLastModifiedDate(lastModifiedAt);

        return expectedDocument;
    }

    private static string FormatExternalLastModifiedDate(DateTimeOffset lastModifiedAt) =>
        lastModifiedAt.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    private static string CanonicalizeJson(JsonNode node) =>
        NormalizeJsonNode(node)?.ToJsonString() ?? "null";

    private static JsonNode? NormalizeJsonNode(JsonNode? node)
    {
        return node switch
        {
            null => null,
            JsonObject jsonObject => NormalizeJsonObject(jsonObject),
            JsonArray jsonArray => NormalizeJsonArray(jsonArray),
            _ => node.DeepClone(),
        };
    }

    private static JsonObject NormalizeJsonObject(JsonObject jsonObject)
    {
        JsonObject normalized = [];

        foreach (var property in jsonObject.OrderBy(static property => property.Key, StringComparer.Ordinal))
        {
            normalized[property.Key] = NormalizeJsonNode(property.Value);
        }

        return normalized;
    }

    private static JsonArray NormalizeJsonArray(JsonArray jsonArray)
    {
        JsonArray normalized = [];

        foreach (var item in jsonArray)
        {
            normalized.Add(NormalizeJsonNode(item));
        }

        return normalized;
    }

    private async Task<UpsertResult> ExecuteCreateAsync(
        string requestBodyJson,
        DocumentUuid documentUuid,
        string traceId
    )
    {
        await using var scope = _serviceProvider.CreateAsyncScope();
        SetSelectedInstance(scope.ServiceProvider);

        var requestBody = JsonNode.Parse(requestBodyJson)!;
        var request = new UpsertRequest(
            ResourceInfo: _resourceInfo,
            DocumentInfo: AuthoritativeSampleStudentSchoolAssociationIntegrationTestSupport.CreateDocumentInfo(
                requestBody,
                _resourceInfo,
                _baseResourceSchema,
                _mappingSet,
                _seedData.GraduationPlanTypeDescriptorId
            ),
            MappingSet: _mappingSet,
            EdfiDoc: requestBody,
            Headers: [],
            TraceId: new TraceId(traceId),
            DocumentUuid: documentUuid,
            DocumentSecurityElements: new([], [], [], [], []),
            UpdateCascadeHandler: new AuthoritativeSampleStudentSchoolAssociationNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new AuthoritativeSampleStudentSchoolAssociationAllowAllResourceAuthorizationHandler(),
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
            DocumentInfo: AuthoritativeSampleStudentSchoolAssociationIntegrationTestSupport.CreateDocumentInfo(
                requestBody,
                _resourceInfo,
                _baseResourceSchema,
                _mappingSet,
                _seedData.GraduationPlanTypeDescriptorId
            ),
            MappingSet: _mappingSet,
            EdfiDoc: requestBody,
            Headers: [],
            TraceId: new TraceId(traceId),
            DocumentUuid: StudentSchoolAssociationDocumentUuid,
            DocumentSecurityElements: new([], [], [], [], []),
            UpdateCascadeHandler: new AuthoritativeSampleStudentSchoolAssociationNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new AuthoritativeSampleStudentSchoolAssociationAllowAllResourceAuthorizationHandler(),
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
                    InstanceName: "PostgresqlRelationalWriteAuthoritativeSampleStudentSchoolAssociation",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );
    }

    private async Task<AuthoritativeSampleStudentSchoolAssociationSeedData> SeedReferenceDataAsync()
    {
        var schoolResourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "School");
        var studentResourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "Student");
        var schoolYearTypeResourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "SchoolYearType");
        var calendarResourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "Calendar");
        var graduationPlanResourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "GraduationPlan");
        var calendarTypeDescriptorResourceKeyId = await GetResourceKeyIdAsync(
            "Ed-Fi",
            "CalendarTypeDescriptor"
        );
        var gradeLevelDescriptorResourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "GradeLevelDescriptor");
        var graduationPlanTypeDescriptorResourceKeyId = await GetResourceKeyIdAsync(
            "Ed-Fi",
            "GraduationPlanTypeDescriptor"
        );
        var educationPlanDescriptorResourceKeyId = await GetResourceKeyIdAsync(
            "Ed-Fi",
            "EducationPlanDescriptor"
        );
        var membershipTypeDescriptorResourceKeyId = await GetResourceKeyIdAsync(
            "Sample",
            "MembershipTypeDescriptor"
        );

        var calendarTypeDescriptorId = await SeedDescriptorAsync(
            Guid.Parse("11111111-0000-0000-0000-000000000001"),
            calendarTypeDescriptorResourceKeyId,
            "Ed-Fi",
            "CalendarTypeDescriptor",
            "Ed-Fi:CalendarTypeDescriptor",
            CalendarTypeDescriptorUri,
            "uri://ed-fi.org/CalendarTypeDescriptor",
            "Instructional",
            "Instructional"
        );
        var ninthGradeLevelDescriptorId = await SeedDescriptorAsync(
            Guid.Parse("11111111-0000-0000-0000-000000000002"),
            gradeLevelDescriptorResourceKeyId,
            "Ed-Fi",
            "GradeLevelDescriptor",
            "Ed-Fi:GradeLevelDescriptor",
            NinthGradeLevelDescriptorUri,
            "uri://ed-fi.org/GradeLevelDescriptor",
            "Ninth grade",
            "Ninth grade"
        );
        var tenthGradeLevelDescriptorId = await SeedDescriptorAsync(
            Guid.Parse("11111111-0000-0000-0000-000000000003"),
            gradeLevelDescriptorResourceKeyId,
            "Ed-Fi",
            "GradeLevelDescriptor",
            "Ed-Fi:GradeLevelDescriptor",
            TenthGradeLevelDescriptorUri,
            "uri://ed-fi.org/GradeLevelDescriptor",
            "Tenth grade",
            "Tenth grade"
        );
        var graduationPlanTypeDescriptorId = await SeedDescriptorAsync(
            Guid.Parse("11111111-0000-0000-0000-000000000004"),
            graduationPlanTypeDescriptorResourceKeyId,
            "Ed-Fi",
            "GraduationPlanTypeDescriptor",
            "Ed-Fi:GraduationPlanTypeDescriptor",
            GraduationPlanTypeDescriptorUri,
            "uri://ed-fi.org/GraduationPlanTypeDescriptor",
            "Foundation",
            "Foundation"
        );
        var pathwayEducationPlanDescriptorId = await SeedDescriptorAsync(
            Guid.Parse("11111111-0000-0000-0000-000000000005"),
            educationPlanDescriptorResourceKeyId,
            "Ed-Fi",
            "EducationPlanDescriptor",
            "Ed-Fi:EducationPlanDescriptor",
            PathwayEducationPlanDescriptorUri,
            "uri://ed-fi.org/EducationPlanDescriptor",
            "Pathway",
            "Pathway"
        );
        var interventionEducationPlanDescriptorId = await SeedDescriptorAsync(
            Guid.Parse("11111111-0000-0000-0000-000000000006"),
            educationPlanDescriptorResourceKeyId,
            "Ed-Fi",
            "EducationPlanDescriptor",
            "Ed-Fi:EducationPlanDescriptor",
            InterventionEducationPlanDescriptorUri,
            "uri://ed-fi.org/EducationPlanDescriptor",
            "Intervention",
            "Intervention"
        );
        var careerEducationPlanDescriptorId = await SeedDescriptorAsync(
            Guid.Parse("11111111-0000-0000-0000-000000000007"),
            educationPlanDescriptorResourceKeyId,
            "Ed-Fi",
            "EducationPlanDescriptor",
            "Ed-Fi:EducationPlanDescriptor",
            CareerEducationPlanDescriptorUri,
            "uri://ed-fi.org/EducationPlanDescriptor",
            "Career",
            "Career"
        );
        var residentMembershipTypeDescriptorId = await SeedDescriptorAsync(
            Guid.Parse("11111111-0000-0000-0000-000000000008"),
            membershipTypeDescriptorResourceKeyId,
            "Sample",
            "MembershipTypeDescriptor",
            "Sample:MembershipTypeDescriptor",
            ResidentMembershipTypeDescriptorUri,
            "uri://sample.org/MembershipTypeDescriptor",
            "Resident",
            "Resident"
        );
        var transferMembershipTypeDescriptorId = await SeedDescriptorAsync(
            Guid.Parse("11111111-0000-0000-0000-000000000009"),
            membershipTypeDescriptorResourceKeyId,
            "Sample",
            "MembershipTypeDescriptor",
            "Sample:MembershipTypeDescriptor",
            TransferMembershipTypeDescriptorUri,
            "uri://sample.org/MembershipTypeDescriptor",
            "Transfer",
            "Transfer"
        );

        var studentSchoolYearTypeDocumentId = await InsertDocumentAsync(
            Guid.Parse("22222222-0000-0000-0000-000000000001"),
            schoolYearTypeResourceKeyId
        );
        await InsertSchoolYearTypeAsync(studentSchoolYearTypeDocumentId, SchoolYear, true);
        await InsertReferentialIdentityAsync(
            CreateReferentialId(
                ("Ed-Fi", "SchoolYearType", false),
                ("$.schoolYear", SchoolYear.ToString(CultureInfo.InvariantCulture))
            ),
            studentSchoolYearTypeDocumentId,
            schoolYearTypeResourceKeyId
        );

        var foundationGraduationSchoolYearTypeDocumentId = await InsertDocumentAsync(
            Guid.Parse("22222222-0000-0000-0000-000000000002"),
            schoolYearTypeResourceKeyId
        );
        await InsertSchoolYearTypeAsync(
            foundationGraduationSchoolYearTypeDocumentId,
            FoundationGraduationSchoolYear,
            false
        );
        await InsertReferentialIdentityAsync(
            CreateReferentialId(
                ("Ed-Fi", "SchoolYearType", false),
                ("$.schoolYear", FoundationGraduationSchoolYear.ToString(CultureInfo.InvariantCulture))
            ),
            foundationGraduationSchoolYearTypeDocumentId,
            schoolYearTypeResourceKeyId
        );

        var endorsementGraduationSchoolYearTypeDocumentId = await InsertDocumentAsync(
            Guid.Parse("22222222-0000-0000-0000-000000000003"),
            schoolYearTypeResourceKeyId
        );
        await InsertSchoolYearTypeAsync(
            endorsementGraduationSchoolYearTypeDocumentId,
            EndorsementGraduationSchoolYear,
            false
        );
        await InsertReferentialIdentityAsync(
            CreateReferentialId(
                ("Ed-Fi", "SchoolYearType", false),
                ("$.schoolYear", EndorsementGraduationSchoolYear.ToString(CultureInfo.InvariantCulture))
            ),
            endorsementGraduationSchoolYearTypeDocumentId,
            schoolYearTypeResourceKeyId
        );

        var stemGraduationSchoolYearTypeDocumentId = await InsertDocumentAsync(
            Guid.Parse("22222222-0000-0000-0000-000000000004"),
            schoolYearTypeResourceKeyId
        );
        await InsertSchoolYearTypeAsync(
            stemGraduationSchoolYearTypeDocumentId,
            StemGraduationSchoolYear,
            false
        );
        await InsertReferentialIdentityAsync(
            CreateReferentialId(
                ("Ed-Fi", "SchoolYearType", false),
                ("$.schoolYear", StemGraduationSchoolYear.ToString(CultureInfo.InvariantCulture))
            ),
            stemGraduationSchoolYearTypeDocumentId,
            schoolYearTypeResourceKeyId
        );

        var schoolDocumentId = await InsertDocumentAsync(
            Guid.Parse("33333333-0000-0000-0000-000000000001"),
            schoolResourceKeyId
        );
        await InsertSchoolAsync(schoolDocumentId, SchoolId, "Alpha Academy");
        await InsertReferentialIdentityAsync(
            CreateReferentialId(
                ("Ed-Fi", "School", false),
                ("$.schoolId", SchoolId.ToString(CultureInfo.InvariantCulture))
            ),
            schoolDocumentId,
            schoolResourceKeyId
        );
        await InsertReferentialIdentityAsync(
            CreateReferentialId(
                ("Ed-Fi", "EducationOrganization", false),
                ("$.educationOrganizationId", SchoolId.ToString(CultureInfo.InvariantCulture))
            ),
            schoolDocumentId,
            await GetResourceKeyIdAsync("Ed-Fi", "EducationOrganization")
        );

        var conflictSchoolDocumentId = await InsertDocumentAsync(
            Guid.Parse("33333333-0000-0000-0000-000000000002"),
            schoolResourceKeyId
        );
        await InsertSchoolAsync(conflictSchoolDocumentId, ConflictSchoolId, "Beta Academy");
        await InsertReferentialIdentityAsync(
            CreateReferentialId(
                ("Ed-Fi", "School", false),
                ("$.schoolId", ConflictSchoolId.ToString(CultureInfo.InvariantCulture))
            ),
            conflictSchoolDocumentId,
            schoolResourceKeyId
        );
        await InsertReferentialIdentityAsync(
            CreateReferentialId(
                ("Ed-Fi", "EducationOrganization", false),
                ("$.educationOrganizationId", ConflictSchoolId.ToString(CultureInfo.InvariantCulture))
            ),
            conflictSchoolDocumentId,
            await GetResourceKeyIdAsync("Ed-Fi", "EducationOrganization")
        );

        var studentDocumentId = await InsertDocumentAsync(
            Guid.Parse("44444444-0000-0000-0000-000000000001"),
            studentResourceKeyId
        );
        await InsertStudentAsync(studentDocumentId, StudentUniqueId, "Maya", "Lopez");
        await InsertReferentialIdentityAsync(
            CreateReferentialId(("Ed-Fi", "Student", false), ("$.studentUniqueId", StudentUniqueId)),
            studentDocumentId,
            studentResourceKeyId
        );

        var calendarDocumentId = await InsertDocumentAsync(
            Guid.Parse("55555555-0000-0000-0000-000000000001"),
            calendarResourceKeyId
        );
        await InsertCalendarAsync(
            calendarDocumentId,
            studentSchoolYearTypeDocumentId,
            SchoolYear,
            schoolDocumentId,
            SchoolId,
            calendarTypeDescriptorId,
            CalendarCode
        );
        await InsertReferentialIdentityAsync(
            CreateReferentialId(
                ("Ed-Fi", "Calendar", false),
                ("$.calendarCode", CalendarCode),
                ("$.schoolReference.schoolId", SchoolId.ToString(CultureInfo.InvariantCulture)),
                ("$.schoolYearTypeReference.schoolYear", SchoolYear.ToString(CultureInfo.InvariantCulture))
            ),
            calendarDocumentId,
            calendarResourceKeyId
        );

        var conflictCalendarDocumentId = await InsertDocumentAsync(
            Guid.Parse("55555555-0000-0000-0000-000000000002"),
            calendarResourceKeyId
        );
        await InsertCalendarAsync(
            conflictCalendarDocumentId,
            studentSchoolYearTypeDocumentId,
            SchoolYear,
            conflictSchoolDocumentId,
            ConflictSchoolId,
            calendarTypeDescriptorId,
            ConflictCalendarCode
        );
        await InsertReferentialIdentityAsync(
            CreateReferentialId(
                ("Ed-Fi", "Calendar", false),
                ("$.calendarCode", ConflictCalendarCode),
                ("$.schoolReference.schoolId", ConflictSchoolId.ToString(CultureInfo.InvariantCulture)),
                ("$.schoolYearTypeReference.schoolYear", SchoolYear.ToString(CultureInfo.InvariantCulture))
            ),
            conflictCalendarDocumentId,
            calendarResourceKeyId
        );

        var foundationGraduationPlanDocumentId = await InsertDocumentAsync(
            Guid.Parse("66666666-0000-0000-0000-000000000001"),
            graduationPlanResourceKeyId
        );
        await InsertGraduationPlanAsync(
            foundationGraduationPlanDocumentId,
            schoolDocumentId,
            SchoolId,
            foundationGraduationSchoolYearTypeDocumentId,
            FoundationGraduationSchoolYear,
            graduationPlanTypeDescriptorId,
            26.000m
        );
        await InsertReferentialIdentityAsync(
            CreateReferentialId(
                ("Ed-Fi", "GraduationPlan", false),
                (
                    "$.educationOrganizationReference.educationOrganizationId",
                    SchoolId.ToString(CultureInfo.InvariantCulture)
                ),
                (
                    "$.graduationPlanTypeDescriptor",
                    graduationPlanTypeDescriptorId.ToString(CultureInfo.InvariantCulture)
                ),
                (
                    "$.graduationSchoolYearTypeReference.schoolYear",
                    FoundationGraduationSchoolYear.ToString(CultureInfo.InvariantCulture)
                )
            ),
            foundationGraduationPlanDocumentId,
            graduationPlanResourceKeyId
        );

        var endorsementGraduationPlanDocumentId = await InsertDocumentAsync(
            Guid.Parse("66666666-0000-0000-0000-000000000002"),
            graduationPlanResourceKeyId
        );
        await InsertGraduationPlanAsync(
            endorsementGraduationPlanDocumentId,
            schoolDocumentId,
            SchoolId,
            endorsementGraduationSchoolYearTypeDocumentId,
            EndorsementGraduationSchoolYear,
            graduationPlanTypeDescriptorId,
            27.500m
        );
        await InsertReferentialIdentityAsync(
            CreateReferentialId(
                ("Ed-Fi", "GraduationPlan", false),
                (
                    "$.educationOrganizationReference.educationOrganizationId",
                    SchoolId.ToString(CultureInfo.InvariantCulture)
                ),
                (
                    "$.graduationPlanTypeDescriptor",
                    graduationPlanTypeDescriptorId.ToString(CultureInfo.InvariantCulture)
                ),
                (
                    "$.graduationSchoolYearTypeReference.schoolYear",
                    EndorsementGraduationSchoolYear.ToString(CultureInfo.InvariantCulture)
                )
            ),
            endorsementGraduationPlanDocumentId,
            graduationPlanResourceKeyId
        );

        var stemGraduationPlanDocumentId = await InsertDocumentAsync(
            Guid.Parse("66666666-0000-0000-0000-000000000003"),
            graduationPlanResourceKeyId
        );
        await InsertGraduationPlanAsync(
            stemGraduationPlanDocumentId,
            schoolDocumentId,
            SchoolId,
            stemGraduationSchoolYearTypeDocumentId,
            StemGraduationSchoolYear,
            graduationPlanTypeDescriptorId,
            28.000m
        );
        await InsertReferentialIdentityAsync(
            CreateReferentialId(
                ("Ed-Fi", "GraduationPlan", false),
                (
                    "$.educationOrganizationReference.educationOrganizationId",
                    SchoolId.ToString(CultureInfo.InvariantCulture)
                ),
                (
                    "$.graduationPlanTypeDescriptor",
                    graduationPlanTypeDescriptorId.ToString(CultureInfo.InvariantCulture)
                ),
                (
                    "$.graduationSchoolYearTypeReference.schoolYear",
                    StemGraduationSchoolYear.ToString(CultureInfo.InvariantCulture)
                )
            ),
            stemGraduationPlanDocumentId,
            graduationPlanResourceKeyId
        );

        return new(
            schoolDocumentId,
            conflictSchoolDocumentId,
            calendarDocumentId,
            conflictCalendarDocumentId,
            studentDocumentId,
            studentSchoolYearTypeDocumentId,
            ninthGradeLevelDescriptorId,
            tenthGradeLevelDescriptorId,
            residentMembershipTypeDescriptorId,
            transferMembershipTypeDescriptorId,
            pathwayEducationPlanDescriptorId,
            interventionEducationPlanDescriptorId,
            careerEducationPlanDescriptorId,
            graduationPlanTypeDescriptorId,
            foundationGraduationPlanDocumentId,
            endorsementGraduationPlanDocumentId,
            stemGraduationPlanDocumentId
        );
    }

    private async Task DisableStudentSchoolAssociationReferentialIdentityTriggerAsync()
    {
        await _database.ExecuteNonQueryAsync(
            """
            ALTER TABLE "edfi"."StudentSchoolAssociation"
            DISABLE TRIGGER "TR_StudentSchoolAssociation_ReferentialIdentity";
            """
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

    private async Task<long> SeedDescriptorAsync(
        Guid documentUuid,
        short resourceKeyId,
        string projectName,
        string resourceName,
        string discriminator,
        string uri,
        string @namespace,
        string codeValue,
        string shortDescription
    )
    {
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
            CreateDescriptorReferentialId(projectName, resourceName, uri),
            documentId,
            resourceKeyId
        );

        return documentId;
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
            new NpgsqlParameter("birthDate", new DateOnly(2010, 5, 14)),
            new NpgsqlParameter("firstName", firstName),
            new NpgsqlParameter("lastSurname", lastSurname),
            new NpgsqlParameter("studentUniqueId", studentUniqueId)
        );
    }

    private async Task InsertSchoolYearTypeAsync(long documentId, int schoolYear, bool currentSchoolYear)
    {
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO "edfi"."SchoolYearType" (
                "DocumentId",
                "CurrentSchoolYear",
                "SchoolYear",
                "SchoolYearDescription"
            )
            VALUES (
                @documentId,
                @currentSchoolYear,
                @schoolYear,
                @schoolYearDescription
            );
            """,
            new NpgsqlParameter("documentId", documentId),
            new NpgsqlParameter("currentSchoolYear", currentSchoolYear),
            new NpgsqlParameter("schoolYear", schoolYear),
            new NpgsqlParameter("schoolYearDescription", $"{schoolYear}-{schoolYear + 1}")
        );
    }

    private async Task InsertCalendarAsync(
        long documentId,
        long schoolYearDocumentId,
        int schoolYear,
        long schoolDocumentId,
        long schoolId,
        long calendarTypeDescriptorId,
        string calendarCode
    )
    {
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO "edfi"."Calendar" (
                "DocumentId",
                "SchoolYear_DocumentId",
                "SchoolYear_SchoolYear",
                "School_DocumentId",
                "School_SchoolId",
                "CalendarTypeDescriptor_DescriptorId",
                "CalendarCode"
            )
            VALUES (
                @documentId,
                @schoolYearDocumentId,
                @schoolYear,
                @schoolDocumentId,
                @schoolId,
                @calendarTypeDescriptorId,
                @calendarCode
            );
            """,
            new NpgsqlParameter("documentId", documentId),
            new NpgsqlParameter("schoolYearDocumentId", schoolYearDocumentId),
            new NpgsqlParameter("schoolYear", schoolYear),
            new NpgsqlParameter("schoolDocumentId", schoolDocumentId),
            new NpgsqlParameter("schoolId", schoolId),
            new NpgsqlParameter("calendarTypeDescriptorId", calendarTypeDescriptorId),
            new NpgsqlParameter("calendarCode", calendarCode)
        );
    }

    private async Task InsertGraduationPlanAsync(
        long documentId,
        long educationOrganizationDocumentId,
        long educationOrganizationId,
        long graduationSchoolYearDocumentId,
        int graduationSchoolYear,
        long graduationPlanTypeDescriptorId,
        decimal totalRequiredCredits
    )
    {
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO "edfi"."GraduationPlan" (
                "DocumentId",
                "EducationOrganization_DocumentId",
                "EducationOrganization_EducationOrganizationId",
                "GraduationSchoolYear_DocumentId",
                "GraduationSchoolYear_GraduationSchoolYear",
                "GraduationPlanTypeDescriptor_DescriptorId",
                "TotalRequiredCredits"
            )
            VALUES (
                @documentId,
                @educationOrganizationDocumentId,
                @educationOrganizationId,
                @graduationSchoolYearDocumentId,
                @graduationSchoolYear,
                @graduationPlanTypeDescriptorId,
                @totalRequiredCredits
            );
            """,
            new NpgsqlParameter("documentId", documentId),
            new NpgsqlParameter("educationOrganizationDocumentId", educationOrganizationDocumentId),
            new NpgsqlParameter("educationOrganizationId", educationOrganizationId),
            new NpgsqlParameter("graduationSchoolYearDocumentId", graduationSchoolYearDocumentId),
            new NpgsqlParameter("graduationSchoolYear", graduationSchoolYear),
            new NpgsqlParameter("graduationPlanTypeDescriptorId", graduationPlanTypeDescriptorId),
            new NpgsqlParameter("totalRequiredCredits", totalRequiredCredits)
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

    private async Task<AuthoritativeSampleStudentSchoolAssociationPersistedState> ReadPersistedStateAsync(
        Guid documentUuid
    )
    {
        var document = await ReadDocumentAsync(documentUuid);

        return new(
            Document: document,
            Association: await ReadAssociationAsync(document.DocumentId),
            AssociationExtension: await ReadAssociationExtensionAsync(document.DocumentId),
            AlternativeGraduationPlans: await ReadAlternativeGraduationPlansAsync(document.DocumentId),
            EducationPlans: await ReadEducationPlansAsync(document.DocumentId)
        );
    }

    private async Task<AuthoritativeSampleStudentSchoolAssociationDocumentRow> ReadDocumentAsync(
        Guid documentUuid
    )
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
            ? new AuthoritativeSampleStudentSchoolAssociationDocumentRow(
                AuthoritativeSampleStudentSchoolAssociationIntegrationTestSupport.GetInt64(
                    rows[0],
                    "DocumentId"
                ),
                AuthoritativeSampleStudentSchoolAssociationIntegrationTestSupport.GetGuid(
                    rows[0],
                    "DocumentUuid"
                ),
                AuthoritativeSampleStudentSchoolAssociationIntegrationTestSupport.GetInt16(
                    rows[0],
                    "ResourceKeyId"
                ),
                AuthoritativeSampleStudentSchoolAssociationIntegrationTestSupport.GetInt64(
                    rows[0],
                    "ContentVersion"
                )
            )
            : throw new InvalidOperationException(
                $"Expected exactly one document row for '{documentUuid}', but found {rows.Count}."
            );
    }

    private async Task<AuthoritativeSampleStudentSchoolAssociationRow> ReadAssociationAsync(long documentId)
    {
        var rows = await _database.QueryRowsAsync(
            """
            SELECT
                "DocumentId",
                "SchoolId_Unified",
                "SchoolYear_Unified",
                "Calendar_DocumentId",
                "Calendar_CalendarCode",
                "SchoolYear_DocumentId",
                "School_DocumentId",
                "Student_DocumentId",
                "Student_StudentUniqueId",
                "EntryGradeLevelDescriptor_DescriptorId",
                "EntryDate",
                "PrimarySchool"
            FROM "edfi"."StudentSchoolAssociation"
            WHERE "DocumentId" = @documentId;
            """,
            new NpgsqlParameter("documentId", documentId)
        );

        return rows.Count == 1
            ? new AuthoritativeSampleStudentSchoolAssociationRow(
                AuthoritativeSampleStudentSchoolAssociationIntegrationTestSupport.GetInt64(
                    rows[0],
                    "DocumentId"
                ),
                AuthoritativeSampleStudentSchoolAssociationIntegrationTestSupport.GetInt64(
                    rows[0],
                    "SchoolId_Unified"
                ),
                AuthoritativeSampleStudentSchoolAssociationIntegrationTestSupport.GetInt32(
                    rows[0],
                    "SchoolYear_Unified"
                ),
                AuthoritativeSampleStudentSchoolAssociationIntegrationTestSupport.GetInt64(
                    rows[0],
                    "Calendar_DocumentId"
                ),
                AuthoritativeSampleStudentSchoolAssociationIntegrationTestSupport.GetString(
                    rows[0],
                    "Calendar_CalendarCode"
                ),
                AuthoritativeSampleStudentSchoolAssociationIntegrationTestSupport.GetInt64(
                    rows[0],
                    "SchoolYear_DocumentId"
                ),
                AuthoritativeSampleStudentSchoolAssociationIntegrationTestSupport.GetInt64(
                    rows[0],
                    "School_DocumentId"
                ),
                AuthoritativeSampleStudentSchoolAssociationIntegrationTestSupport.GetInt64(
                    rows[0],
                    "Student_DocumentId"
                ),
                AuthoritativeSampleStudentSchoolAssociationIntegrationTestSupport.GetString(
                    rows[0],
                    "Student_StudentUniqueId"
                ),
                AuthoritativeSampleStudentSchoolAssociationIntegrationTestSupport.GetInt64(
                    rows[0],
                    "EntryGradeLevelDescriptor_DescriptorId"
                ),
                AuthoritativeSampleStudentSchoolAssociationIntegrationTestSupport.GetDateOnly(
                    rows[0],
                    "EntryDate"
                ),
                AuthoritativeSampleStudentSchoolAssociationIntegrationTestSupport.GetBoolean(
                    rows[0],
                    "PrimarySchool"
                )
            )
            : throw new InvalidOperationException(
                $"Expected exactly one StudentSchoolAssociation row for document id '{documentId}', but found {rows.Count}."
            );
    }

    private async Task<AuthoritativeSampleStudentSchoolAssociationExtensionRow> ReadAssociationExtensionAsync(
        long documentId
    )
    {
        var rows = await _database.QueryRowsAsync(
            """
            SELECT "DocumentId", "MembershipTypeDescriptor_DescriptorId"
            FROM "sample"."StudentSchoolAssociationExtension"
            WHERE "DocumentId" = @documentId;
            """,
            new NpgsqlParameter("documentId", documentId)
        );

        return rows.Count == 1
            ? new AuthoritativeSampleStudentSchoolAssociationExtensionRow(
                AuthoritativeSampleStudentSchoolAssociationIntegrationTestSupport.GetInt64(
                    rows[0],
                    "DocumentId"
                ),
                AuthoritativeSampleStudentSchoolAssociationIntegrationTestSupport.GetInt64(
                    rows[0],
                    "MembershipTypeDescriptor_DescriptorId"
                )
            )
            : throw new InvalidOperationException(
                $"Expected exactly one StudentSchoolAssociationExtension row for document id '{documentId}', but found {rows.Count}."
            );
    }

    private async Task<
        IReadOnlyList<AuthoritativeSampleStudentSchoolAssociationAlternativeGraduationPlanRow>
    > ReadAlternativeGraduationPlansAsync(long documentId)
    {
        var rows = await _database.QueryRowsAsync(
            """
            SELECT
                "CollectionItemId",
                "Ordinal",
                "StudentSchoolAssociation_DocumentId",
                "AlternativeGraduationPlan_DocumentId",
                "AlternativeGraduationPlan_EducationOrganizationId",
                "AlternativeGraduationPlan_GraduationPlanTypeDescript_0b71806181",
                "AlternativeGraduationPlan_GraduationSchoolYear"
            FROM "edfi"."StudentSchoolAssociationAlternativeGraduationPlan"
            WHERE "StudentSchoolAssociation_DocumentId" = @documentId
            ORDER BY "Ordinal";
            """,
            new NpgsqlParameter("documentId", documentId)
        );

        return rows.Select(row => new AuthoritativeSampleStudentSchoolAssociationAlternativeGraduationPlanRow(
                AuthoritativeSampleStudentSchoolAssociationIntegrationTestSupport.GetInt64(
                    row,
                    "CollectionItemId"
                ),
                AuthoritativeSampleStudentSchoolAssociationIntegrationTestSupport.GetInt32(row, "Ordinal"),
                AuthoritativeSampleStudentSchoolAssociationIntegrationTestSupport.GetInt64(
                    row,
                    "StudentSchoolAssociation_DocumentId"
                ),
                AuthoritativeSampleStudentSchoolAssociationIntegrationTestSupport.GetInt64(
                    row,
                    "AlternativeGraduationPlan_DocumentId"
                ),
                AuthoritativeSampleStudentSchoolAssociationIntegrationTestSupport.GetInt64(
                    row,
                    "AlternativeGraduationPlan_EducationOrganizationId"
                ),
                AuthoritativeSampleStudentSchoolAssociationIntegrationTestSupport.GetInt64(
                    row,
                    "AlternativeGraduationPlan_GraduationPlanTypeDescript_0b71806181"
                ),
                AuthoritativeSampleStudentSchoolAssociationIntegrationTestSupport.GetInt32(
                    row,
                    "AlternativeGraduationPlan_GraduationSchoolYear"
                )
            ))
            .ToArray();
    }

    private async Task<
        IReadOnlyList<AuthoritativeSampleStudentSchoolAssociationEducationPlanRow>
    > ReadEducationPlansAsync(long documentId)
    {
        var rows = await _database.QueryRowsAsync(
            """
            SELECT
                "CollectionItemId",
                "Ordinal",
                "StudentSchoolAssociation_DocumentId",
                "EducationPlanDescriptor_DescriptorId"
            FROM "edfi"."StudentSchoolAssociationEducationPlan"
            WHERE "StudentSchoolAssociation_DocumentId" = @documentId
            ORDER BY "Ordinal";
            """,
            new NpgsqlParameter("documentId", documentId)
        );

        return rows.Select(row => new AuthoritativeSampleStudentSchoolAssociationEducationPlanRow(
                AuthoritativeSampleStudentSchoolAssociationIntegrationTestSupport.GetInt64(
                    row,
                    "CollectionItemId"
                ),
                AuthoritativeSampleStudentSchoolAssociationIntegrationTestSupport.GetInt32(row, "Ordinal"),
                AuthoritativeSampleStudentSchoolAssociationIntegrationTestSupport.GetInt64(
                    row,
                    "StudentSchoolAssociation_DocumentId"
                ),
                AuthoritativeSampleStudentSchoolAssociationIntegrationTestSupport.GetInt64(
                    row,
                    "EducationPlanDescriptor_DescriptorId"
                )
            ))
            .ToArray();
    }
}

internal sealed record PropagatedReferenceIdentityRuntimeSeedData(
    long SchoolDocumentId,
    long CalendarDocumentId,
    long AlternateCalendarDocumentId,
    long StudentDocumentId,
    long StudentSchoolYearTypeDocumentId,
    long NinthGradeLevelDescriptorId,
    long TenthGradeLevelDescriptorId,
    long ResidentMembershipTypeDescriptorId,
    long TransferMembershipTypeDescriptorId,
    long GraduationPlanTypeDescriptorId,
    long FoundationGraduationPlanDocumentId,
    long EndorsementGraduationPlanDocumentId
);

internal sealed record PropagatedReferenceIdentityRuntimePersistedState(
    AuthoritativeSampleStudentSchoolAssociationDocumentRow Document,
    AuthoritativeSampleStudentSchoolAssociationRow Association,
    AuthoritativeSampleStudentSchoolAssociationExtensionRow AssociationExtension,
    IReadOnlyList<AuthoritativeSampleStudentSchoolAssociationAlternativeGraduationPlanRow> AlternativeGraduationPlans
);

[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
[NonParallelizable]
public class Given_A_Postgresql_Relational_Write_Propagated_Reference_Identity_Runtime_With_The_Authoritative_Sample_StudentSchoolAssociation_Fixture
{
    private const long SchoolId = 100;
    private const int SchoolYear = 2024;
    private const int FoundationGraduationSchoolYear = 2026;
    private const int EndorsementGraduationSchoolYear = 2027;
    private const string StudentUniqueId = "10001";
    private const string CalendarCode = "MAIN";
    private const string AlternateCalendarCode = "ALT";
    private const string NinthGradeLevelDescriptorUri = "uri://ed-fi.org/GradeLevelDescriptor#Ninth grade";
    private const string TenthGradeLevelDescriptorUri = "uri://ed-fi.org/GradeLevelDescriptor#Tenth grade";
    private const string CalendarTypeDescriptorUri = "uri://ed-fi.org/CalendarTypeDescriptor#Instructional";
    private const string GraduationPlanTypeDescriptorUri =
        "uri://ed-fi.org/GraduationPlanTypeDescriptor#Foundation";
    private const string ResidentMembershipTypeDescriptorUri =
        "uri://sample.org/MembershipTypeDescriptor#Resident";
    private const string TransferMembershipTypeDescriptorUri =
        "uri://sample.org/MembershipTypeDescriptor#Transfer";

    private const string CreateRequestBodyJson = """
        {
          "entryDate": "2024-08-20",
          "entryGradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Ninth grade",
          "primarySchool": true,
          "schoolReference": {
            "schoolId": 100
          },
          "calendarReference": {
            "calendarCode": "MAIN",
            "schoolId": 100,
            "schoolYear": 2024
          },
          "schoolYearTypeReference": {
            "schoolYear": 2024
          },
          "studentReference": {
            "studentUniqueId": "10001"
          },
          "_ext": {
            "sample": {
              "membershipTypeDescriptor": "uri://sample.org/MembershipTypeDescriptor#Resident"
            }
          }
        }
        """;

    private const string ChangedUpdateRequestBodyJson = """
        {
          "entryDate": "2024-08-20",
          "entryGradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth grade",
          "primarySchool": false,
          "schoolReference": {
            "schoolId": 100
          },
          "calendarReference": {
            "calendarCode": "ALT",
            "schoolId": 100,
            "schoolYear": 2024
          },
          "schoolYearTypeReference": {
            "schoolYear": 2024
          },
          "studentReference": {
            "studentUniqueId": "10001"
          },
          "_ext": {
            "sample": {
              "membershipTypeDescriptor": "uri://sample.org/MembershipTypeDescriptor#Transfer"
            }
          }
        }
        """;

    private static readonly DateOnly EntryDate = new(2024, 8, 20);

    private static readonly DocumentUuid StudentSchoolAssociationDocumentUuid = new(
        Guid.Parse("abababab-0000-0000-0000-000000000002")
    );

    private PostgresqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private PostgresqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;
    private ResourceInfo _resourceInfo = null!;
    private ResourceSchema _baseResourceSchema = null!;
    private PropagatedReferenceIdentityRuntimeSeedData _seedData = null!;
    private UpsertResult _createResult = null!;
    private UpdateResult _changedUpdateResult = null!;
    private PropagatedReferenceIdentityRuntimePersistedState _stateAfterCreate = null!;
    private PropagatedReferenceIdentityRuntimePersistedState _stateAfterChangedUpdate = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _fixture = PostgresqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            AuthoritativeSampleStudentSchoolAssociationIntegrationTestSupport.FixtureRelativePath
        );
        _mappingSet = new MappingSetCompiler().Compile(_fixture.ModelSet);
        _database = await PostgresqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
        _serviceProvider =
            AuthoritativeSampleStudentSchoolAssociationIntegrationTestSupport.CreateServiceProvider();

        var (baseProjectSchema, baseResourceSchema) =
            AuthoritativeSampleStudentSchoolAssociationIntegrationTestSupport.GetResourceSchema(
                _fixture.EffectiveSchemaSet,
                "ed-fi",
                "StudentSchoolAssociation"
            );
        _resourceInfo = AuthoritativeSampleStudentSchoolAssociationIntegrationTestSupport.CreateResourceInfo(
            baseProjectSchema,
            baseResourceSchema
        );
        _baseResourceSchema = baseResourceSchema;
        _seedData = await SeedReferenceDataAsync();
        await DisableStudentSchoolAssociationReferentialIdentityTriggerAsync();

        _createResult = await ExecuteCreateAsync(
            CreateRequestBodyJson,
            StudentSchoolAssociationDocumentUuid,
            "pg-propagated-reference-identity-runtime-create"
        );

        if (_createResult is UpsertResult.UpsertFailureReference createReferenceFailure)
        {
            Assert.Fail(
                $"Create reference failure: {AuthoritativeSampleStudentSchoolAssociationIntegrationTestSupport.FormatReferenceFailure(createReferenceFailure)}"
            );
        }

        _createResult.Should().BeOfType<UpsertResult.InsertSuccess>();
        _stateAfterCreate = await ReadPersistedStateAsync(StudentSchoolAssociationDocumentUuid.Value);

        _changedUpdateResult = await ExecuteUpdateAsync(
            ChangedUpdateRequestBodyJson,
            "pg-propagated-reference-identity-runtime-changed-update"
        );

        if (_changedUpdateResult is UpdateResult.UpdateFailureReference changedUpdateReferenceFailure)
        {
            Assert.Fail(
                $"Changed update reference failure: {AuthoritativeSampleStudentSchoolAssociationIntegrationTestSupport.FormatReferenceFailure(changedUpdateReferenceFailure)}"
            );
        }

        _changedUpdateResult.Should().BeOfType<UpdateResult.UpdateSuccess>();
        _stateAfterChangedUpdate = await ReadPersistedStateAsync(StudentSchoolAssociationDocumentUuid.Value);
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
    public void It_populates_persisted_reference_identity_columns_on_create()
    {
        _createResult.Should().BeOfType<UpsertResult.InsertSuccess>();
        _stateAfterCreate
            .Association.Should()
            .Be(
                new AuthoritativeSampleStudentSchoolAssociationRow(
                    _stateAfterCreate.Document.DocumentId,
                    SchoolId,
                    SchoolYear,
                    _seedData.CalendarDocumentId,
                    CalendarCode,
                    _seedData.StudentSchoolYearTypeDocumentId,
                    _seedData.SchoolDocumentId,
                    _seedData.StudentDocumentId,
                    StudentUniqueId,
                    _seedData.NinthGradeLevelDescriptorId,
                    EntryDate,
                    true
                )
            );
        _stateAfterCreate
            .AssociationExtension.Should()
            .Be(
                new AuthoritativeSampleStudentSchoolAssociationExtensionRow(
                    _stateAfterCreate.Document.DocumentId,
                    _seedData.ResidentMembershipTypeDescriptorId
                )
            );
        _stateAfterCreate.AlternativeGraduationPlans.Should().BeEmpty();
    }

    [Test]
    public void It_repopulates_persisted_reference_identity_columns_from_resolved_references_on_changed_put()
    {
        _changedUpdateResult.Should().BeOfType<UpdateResult.UpdateSuccess>();
        _stateAfterChangedUpdate
            .Association.Should()
            .Be(
                new AuthoritativeSampleStudentSchoolAssociationRow(
                    _stateAfterChangedUpdate.Document.DocumentId,
                    SchoolId,
                    SchoolYear,
                    _seedData.AlternateCalendarDocumentId,
                    AlternateCalendarCode,
                    _seedData.StudentSchoolYearTypeDocumentId,
                    _seedData.SchoolDocumentId,
                    _seedData.StudentDocumentId,
                    StudentUniqueId,
                    _seedData.TenthGradeLevelDescriptorId,
                    EntryDate,
                    false
                )
            );
        _stateAfterChangedUpdate
            .AssociationExtension.Should()
            .Be(
                new AuthoritativeSampleStudentSchoolAssociationExtensionRow(
                    _stateAfterChangedUpdate.Document.DocumentId,
                    _seedData.TransferMembershipTypeDescriptorId
                )
            );
        _stateAfterChangedUpdate
            .Association.CalendarDocumentId.Should()
            .NotBe(_stateAfterCreate.Association.CalendarDocumentId);
        _stateAfterChangedUpdate
            .Association.CalendarCode.Should()
            .NotBe(_stateAfterCreate.Association.CalendarCode);
        _stateAfterChangedUpdate
            .Association.EntryGradeLevelDescriptorId.Should()
            .NotBe(_stateAfterCreate.Association.EntryGradeLevelDescriptorId);
        _stateAfterChangedUpdate
            .AssociationExtension.MembershipTypeDescriptorId.Should()
            .NotBe(_stateAfterCreate.AssociationExtension.MembershipTypeDescriptorId);
        _stateAfterChangedUpdate.AlternativeGraduationPlans.Should().BeEmpty();
    }

    private async Task<UpsertResult> ExecuteCreateAsync(
        string requestBodyJson,
        DocumentUuid documentUuid,
        string traceId
    )
    {
        await using var scope = _serviceProvider.CreateAsyncScope();
        SetSelectedInstance(scope.ServiceProvider);

        var requestBody = JsonNode.Parse(requestBodyJson)!;
        var request = new UpsertRequest(
            ResourceInfo: _resourceInfo,
            DocumentInfo: AuthoritativeSampleStudentSchoolAssociationIntegrationTestSupport.CreateDocumentInfo(
                requestBody,
                _resourceInfo,
                _baseResourceSchema,
                _mappingSet,
                _seedData.GraduationPlanTypeDescriptorId
            ),
            MappingSet: _mappingSet,
            EdfiDoc: requestBody,
            Headers: [],
            TraceId: new TraceId(traceId),
            DocumentUuid: documentUuid,
            DocumentSecurityElements: new([], [], [], [], []),
            UpdateCascadeHandler: new AuthoritativeSampleStudentSchoolAssociationNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new AuthoritativeSampleStudentSchoolAssociationAllowAllResourceAuthorizationHandler(),
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
            DocumentInfo: AuthoritativeSampleStudentSchoolAssociationIntegrationTestSupport.CreateDocumentInfo(
                requestBody,
                _resourceInfo,
                _baseResourceSchema,
                _mappingSet,
                _seedData.GraduationPlanTypeDescriptorId
            ),
            MappingSet: _mappingSet,
            EdfiDoc: requestBody,
            Headers: [],
            TraceId: new TraceId(traceId),
            DocumentUuid: StudentSchoolAssociationDocumentUuid,
            DocumentSecurityElements: new([], [], [], [], []),
            UpdateCascadeHandler: new AuthoritativeSampleStudentSchoolAssociationNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new AuthoritativeSampleStudentSchoolAssociationAllowAllResourceAuthorizationHandler(),
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
                    InstanceName: "PostgresqlRelationalWritePropagatedReferenceIdentityRuntime",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );
    }

    private async Task<PropagatedReferenceIdentityRuntimeSeedData> SeedReferenceDataAsync()
    {
        var schoolResourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "School");
        var studentResourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "Student");
        var schoolYearTypeResourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "SchoolYearType");
        var calendarResourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "Calendar");
        var graduationPlanResourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "GraduationPlan");
        var calendarTypeDescriptorResourceKeyId = await GetResourceKeyIdAsync(
            "Ed-Fi",
            "CalendarTypeDescriptor"
        );
        var gradeLevelDescriptorResourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "GradeLevelDescriptor");
        var graduationPlanTypeDescriptorResourceKeyId = await GetResourceKeyIdAsync(
            "Ed-Fi",
            "GraduationPlanTypeDescriptor"
        );
        var membershipTypeDescriptorResourceKeyId = await GetResourceKeyIdAsync(
            "Sample",
            "MembershipTypeDescriptor"
        );
        var educationOrganizationResourceKeyId = await GetResourceKeyIdAsync(
            "Ed-Fi",
            "EducationOrganization"
        );

        var calendarTypeDescriptorId = await SeedDescriptorAsync(
            Guid.Parse("77777777-0000-0000-0000-000000000001"),
            calendarTypeDescriptorResourceKeyId,
            "Ed-Fi",
            "CalendarTypeDescriptor",
            "Ed-Fi:CalendarTypeDescriptor",
            CalendarTypeDescriptorUri,
            "uri://ed-fi.org/CalendarTypeDescriptor",
            "Instructional",
            "Instructional"
        );
        var ninthGradeLevelDescriptorId = await SeedDescriptorAsync(
            Guid.Parse("77777777-0000-0000-0000-000000000002"),
            gradeLevelDescriptorResourceKeyId,
            "Ed-Fi",
            "GradeLevelDescriptor",
            "Ed-Fi:GradeLevelDescriptor",
            NinthGradeLevelDescriptorUri,
            "uri://ed-fi.org/GradeLevelDescriptor",
            "Ninth grade",
            "Ninth grade"
        );
        var tenthGradeLevelDescriptorId = await SeedDescriptorAsync(
            Guid.Parse("77777777-0000-0000-0000-000000000003"),
            gradeLevelDescriptorResourceKeyId,
            "Ed-Fi",
            "GradeLevelDescriptor",
            "Ed-Fi:GradeLevelDescriptor",
            TenthGradeLevelDescriptorUri,
            "uri://ed-fi.org/GradeLevelDescriptor",
            "Tenth grade",
            "Tenth grade"
        );
        var graduationPlanTypeDescriptorId = await SeedDescriptorAsync(
            Guid.Parse("77777777-0000-0000-0000-000000000004"),
            graduationPlanTypeDescriptorResourceKeyId,
            "Ed-Fi",
            "GraduationPlanTypeDescriptor",
            "Ed-Fi:GraduationPlanTypeDescriptor",
            GraduationPlanTypeDescriptorUri,
            "uri://ed-fi.org/GraduationPlanTypeDescriptor",
            "Foundation",
            "Foundation"
        );
        var residentMembershipTypeDescriptorId = await SeedDescriptorAsync(
            Guid.Parse("77777777-0000-0000-0000-000000000005"),
            membershipTypeDescriptorResourceKeyId,
            "Sample",
            "MembershipTypeDescriptor",
            "Sample:MembershipTypeDescriptor",
            ResidentMembershipTypeDescriptorUri,
            "uri://sample.org/MembershipTypeDescriptor",
            "Resident",
            "Resident"
        );
        var transferMembershipTypeDescriptorId = await SeedDescriptorAsync(
            Guid.Parse("77777777-0000-0000-0000-000000000006"),
            membershipTypeDescriptorResourceKeyId,
            "Sample",
            "MembershipTypeDescriptor",
            "Sample:MembershipTypeDescriptor",
            TransferMembershipTypeDescriptorUri,
            "uri://sample.org/MembershipTypeDescriptor",
            "Transfer",
            "Transfer"
        );

        var studentSchoolYearTypeDocumentId = await InsertDocumentAsync(
            Guid.Parse("88888888-0000-0000-0000-000000000001"),
            schoolYearTypeResourceKeyId
        );
        await InsertSchoolYearTypeAsync(studentSchoolYearTypeDocumentId, SchoolYear, true);
        await InsertReferentialIdentityAsync(
            CreateReferentialId(
                ("Ed-Fi", "SchoolYearType", false),
                ("$.schoolYear", SchoolYear.ToString(CultureInfo.InvariantCulture))
            ),
            studentSchoolYearTypeDocumentId,
            schoolYearTypeResourceKeyId
        );

        var foundationGraduationSchoolYearTypeDocumentId = await InsertDocumentAsync(
            Guid.Parse("88888888-0000-0000-0000-000000000002"),
            schoolYearTypeResourceKeyId
        );
        await InsertSchoolYearTypeAsync(
            foundationGraduationSchoolYearTypeDocumentId,
            FoundationGraduationSchoolYear,
            false
        );
        await InsertReferentialIdentityAsync(
            CreateReferentialId(
                ("Ed-Fi", "SchoolYearType", false),
                ("$.schoolYear", FoundationGraduationSchoolYear.ToString(CultureInfo.InvariantCulture))
            ),
            foundationGraduationSchoolYearTypeDocumentId,
            schoolYearTypeResourceKeyId
        );

        var endorsementGraduationSchoolYearTypeDocumentId = await InsertDocumentAsync(
            Guid.Parse("88888888-0000-0000-0000-000000000003"),
            schoolYearTypeResourceKeyId
        );
        await InsertSchoolYearTypeAsync(
            endorsementGraduationSchoolYearTypeDocumentId,
            EndorsementGraduationSchoolYear,
            false
        );
        await InsertReferentialIdentityAsync(
            CreateReferentialId(
                ("Ed-Fi", "SchoolYearType", false),
                ("$.schoolYear", EndorsementGraduationSchoolYear.ToString(CultureInfo.InvariantCulture))
            ),
            endorsementGraduationSchoolYearTypeDocumentId,
            schoolYearTypeResourceKeyId
        );

        var schoolDocumentId = await InsertDocumentAsync(
            Guid.Parse("99999999-0000-0000-0000-000000000001"),
            schoolResourceKeyId
        );
        await InsertSchoolAsync(schoolDocumentId, SchoolId, "Alpha Academy");
        await InsertReferentialIdentityAsync(
            CreateReferentialId(
                ("Ed-Fi", "School", false),
                ("$.schoolId", SchoolId.ToString(CultureInfo.InvariantCulture))
            ),
            schoolDocumentId,
            schoolResourceKeyId
        );
        await InsertReferentialIdentityAsync(
            CreateReferentialId(
                ("Ed-Fi", "EducationOrganization", false),
                ("$.educationOrganizationId", SchoolId.ToString(CultureInfo.InvariantCulture))
            ),
            schoolDocumentId,
            educationOrganizationResourceKeyId
        );

        var studentDocumentId = await InsertDocumentAsync(
            Guid.Parse("aaaaaaaa-1000-0000-0000-000000000001"),
            studentResourceKeyId
        );
        await InsertStudentAsync(studentDocumentId, StudentUniqueId, "Maya", "Lopez");
        await InsertReferentialIdentityAsync(
            CreateReferentialId(("Ed-Fi", "Student", false), ("$.studentUniqueId", StudentUniqueId)),
            studentDocumentId,
            studentResourceKeyId
        );

        var calendarDocumentId = await InsertDocumentAsync(
            Guid.Parse("aaaaaaaa-1000-0000-0000-000000000002"),
            calendarResourceKeyId
        );
        await InsertCalendarAsync(
            calendarDocumentId,
            studentSchoolYearTypeDocumentId,
            SchoolYear,
            schoolDocumentId,
            SchoolId,
            calendarTypeDescriptorId,
            CalendarCode
        );
        await InsertReferentialIdentityAsync(
            CreateReferentialId(
                ("Ed-Fi", "Calendar", false),
                ("$.calendarCode", CalendarCode),
                ("$.schoolReference.schoolId", SchoolId.ToString(CultureInfo.InvariantCulture)),
                ("$.schoolYearTypeReference.schoolYear", SchoolYear.ToString(CultureInfo.InvariantCulture))
            ),
            calendarDocumentId,
            calendarResourceKeyId
        );

        var alternateCalendarDocumentId = await InsertDocumentAsync(
            Guid.Parse("aaaaaaaa-1000-0000-0000-000000000005"),
            calendarResourceKeyId
        );
        await InsertCalendarAsync(
            alternateCalendarDocumentId,
            studentSchoolYearTypeDocumentId,
            SchoolYear,
            schoolDocumentId,
            SchoolId,
            calendarTypeDescriptorId,
            AlternateCalendarCode
        );
        await InsertReferentialIdentityAsync(
            CreateReferentialId(
                ("Ed-Fi", "Calendar", false),
                ("$.calendarCode", AlternateCalendarCode),
                ("$.schoolReference.schoolId", SchoolId.ToString(CultureInfo.InvariantCulture)),
                ("$.schoolYearTypeReference.schoolYear", SchoolYear.ToString(CultureInfo.InvariantCulture))
            ),
            alternateCalendarDocumentId,
            calendarResourceKeyId
        );

        var foundationGraduationPlanDocumentId = await InsertDocumentAsync(
            Guid.Parse("aaaaaaaa-1000-0000-0000-000000000003"),
            graduationPlanResourceKeyId
        );
        await InsertGraduationPlanAsync(
            foundationGraduationPlanDocumentId,
            schoolDocumentId,
            SchoolId,
            foundationGraduationSchoolYearTypeDocumentId,
            FoundationGraduationSchoolYear,
            graduationPlanTypeDescriptorId,
            26.000m
        );
        await InsertReferentialIdentityAsync(
            CreateReferentialId(
                ("Ed-Fi", "GraduationPlan", false),
                (
                    "$.educationOrganizationReference.educationOrganizationId",
                    SchoolId.ToString(CultureInfo.InvariantCulture)
                ),
                (
                    "$.graduationPlanTypeDescriptor",
                    graduationPlanTypeDescriptorId.ToString(CultureInfo.InvariantCulture)
                ),
                (
                    "$.graduationSchoolYearTypeReference.schoolYear",
                    FoundationGraduationSchoolYear.ToString(CultureInfo.InvariantCulture)
                )
            ),
            foundationGraduationPlanDocumentId,
            graduationPlanResourceKeyId
        );

        var endorsementGraduationPlanDocumentId = await InsertDocumentAsync(
            Guid.Parse("aaaaaaaa-1000-0000-0000-000000000004"),
            graduationPlanResourceKeyId
        );
        await InsertGraduationPlanAsync(
            endorsementGraduationPlanDocumentId,
            schoolDocumentId,
            SchoolId,
            endorsementGraduationSchoolYearTypeDocumentId,
            EndorsementGraduationSchoolYear,
            graduationPlanTypeDescriptorId,
            27.500m
        );
        await InsertReferentialIdentityAsync(
            CreateReferentialId(
                ("Ed-Fi", "GraduationPlan", false),
                (
                    "$.educationOrganizationReference.educationOrganizationId",
                    SchoolId.ToString(CultureInfo.InvariantCulture)
                ),
                (
                    "$.graduationPlanTypeDescriptor",
                    graduationPlanTypeDescriptorId.ToString(CultureInfo.InvariantCulture)
                ),
                (
                    "$.graduationSchoolYearTypeReference.schoolYear",
                    EndorsementGraduationSchoolYear.ToString(CultureInfo.InvariantCulture)
                )
            ),
            endorsementGraduationPlanDocumentId,
            graduationPlanResourceKeyId
        );

        return new(
            schoolDocumentId,
            calendarDocumentId,
            alternateCalendarDocumentId,
            studentDocumentId,
            studentSchoolYearTypeDocumentId,
            ninthGradeLevelDescriptorId,
            tenthGradeLevelDescriptorId,
            residentMembershipTypeDescriptorId,
            transferMembershipTypeDescriptorId,
            graduationPlanTypeDescriptorId,
            foundationGraduationPlanDocumentId,
            endorsementGraduationPlanDocumentId
        );
    }

    private async Task DisableStudentSchoolAssociationReferentialIdentityTriggerAsync()
    {
        await _database.ExecuteNonQueryAsync(
            """
            ALTER TABLE "edfi"."StudentSchoolAssociation"
            DISABLE TRIGGER "TR_StudentSchoolAssociation_ReferentialIdentity";
            """
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

    private async Task<long> SeedDescriptorAsync(
        Guid documentUuid,
        short resourceKeyId,
        string projectName,
        string resourceName,
        string discriminator,
        string uri,
        string @namespace,
        string codeValue,
        string shortDescription
    )
    {
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
            CreateDescriptorReferentialId(projectName, resourceName, uri),
            documentId,
            resourceKeyId
        );

        return documentId;
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
            new NpgsqlParameter("birthDate", new DateOnly(2010, 5, 14)),
            new NpgsqlParameter("firstName", firstName),
            new NpgsqlParameter("lastSurname", lastSurname),
            new NpgsqlParameter("studentUniqueId", studentUniqueId)
        );
    }

    private async Task InsertSchoolYearTypeAsync(long documentId, int schoolYear, bool currentSchoolYear)
    {
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO "edfi"."SchoolYearType" (
                "DocumentId",
                "CurrentSchoolYear",
                "SchoolYear",
                "SchoolYearDescription"
            )
            VALUES (
                @documentId,
                @currentSchoolYear,
                @schoolYear,
                @schoolYearDescription
            );
            """,
            new NpgsqlParameter("documentId", documentId),
            new NpgsqlParameter("currentSchoolYear", currentSchoolYear),
            new NpgsqlParameter("schoolYear", schoolYear),
            new NpgsqlParameter("schoolYearDescription", $"{schoolYear}-{schoolYear + 1}")
        );
    }

    private async Task InsertCalendarAsync(
        long documentId,
        long schoolYearDocumentId,
        int schoolYear,
        long schoolDocumentId,
        long schoolId,
        long calendarTypeDescriptorId,
        string calendarCode
    )
    {
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO "edfi"."Calendar" (
                "DocumentId",
                "SchoolYear_DocumentId",
                "SchoolYear_SchoolYear",
                "School_DocumentId",
                "School_SchoolId",
                "CalendarTypeDescriptor_DescriptorId",
                "CalendarCode"
            )
            VALUES (
                @documentId,
                @schoolYearDocumentId,
                @schoolYear,
                @schoolDocumentId,
                @schoolId,
                @calendarTypeDescriptorId,
                @calendarCode
            );
            """,
            new NpgsqlParameter("documentId", documentId),
            new NpgsqlParameter("schoolYearDocumentId", schoolYearDocumentId),
            new NpgsqlParameter("schoolYear", schoolYear),
            new NpgsqlParameter("schoolDocumentId", schoolDocumentId),
            new NpgsqlParameter("schoolId", schoolId),
            new NpgsqlParameter("calendarTypeDescriptorId", calendarTypeDescriptorId),
            new NpgsqlParameter("calendarCode", calendarCode)
        );
    }

    private async Task InsertGraduationPlanAsync(
        long documentId,
        long educationOrganizationDocumentId,
        long educationOrganizationId,
        long graduationSchoolYearDocumentId,
        int graduationSchoolYear,
        long graduationPlanTypeDescriptorId,
        decimal totalRequiredCredits
    )
    {
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO "edfi"."GraduationPlan" (
                "DocumentId",
                "EducationOrganization_DocumentId",
                "EducationOrganization_EducationOrganizationId",
                "GraduationSchoolYear_DocumentId",
                "GraduationSchoolYear_GraduationSchoolYear",
                "GraduationPlanTypeDescriptor_DescriptorId",
                "TotalRequiredCredits"
            )
            VALUES (
                @documentId,
                @educationOrganizationDocumentId,
                @educationOrganizationId,
                @graduationSchoolYearDocumentId,
                @graduationSchoolYear,
                @graduationPlanTypeDescriptorId,
                @totalRequiredCredits
            );
            """,
            new NpgsqlParameter("documentId", documentId),
            new NpgsqlParameter("educationOrganizationDocumentId", educationOrganizationDocumentId),
            new NpgsqlParameter("educationOrganizationId", educationOrganizationId),
            new NpgsqlParameter("graduationSchoolYearDocumentId", graduationSchoolYearDocumentId),
            new NpgsqlParameter("graduationSchoolYear", graduationSchoolYear),
            new NpgsqlParameter("graduationPlanTypeDescriptorId", graduationPlanTypeDescriptorId),
            new NpgsqlParameter("totalRequiredCredits", totalRequiredCredits)
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

    private async Task<PropagatedReferenceIdentityRuntimePersistedState> ReadPersistedStateAsync(
        Guid documentUuid
    )
    {
        var document = await ReadDocumentAsync(documentUuid);

        return new(
            Document: document,
            Association: await ReadAssociationAsync(document.DocumentId),
            AssociationExtension: await ReadAssociationExtensionAsync(document.DocumentId),
            AlternativeGraduationPlans: await ReadAlternativeGraduationPlansAsync(document.DocumentId)
        );
    }

    private async Task<AuthoritativeSampleStudentSchoolAssociationDocumentRow> ReadDocumentAsync(
        Guid documentUuid
    )
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
            ? new AuthoritativeSampleStudentSchoolAssociationDocumentRow(
                AuthoritativeSampleStudentSchoolAssociationIntegrationTestSupport.GetInt64(
                    rows[0],
                    "DocumentId"
                ),
                AuthoritativeSampleStudentSchoolAssociationIntegrationTestSupport.GetGuid(
                    rows[0],
                    "DocumentUuid"
                ),
                AuthoritativeSampleStudentSchoolAssociationIntegrationTestSupport.GetInt16(
                    rows[0],
                    "ResourceKeyId"
                ),
                AuthoritativeSampleStudentSchoolAssociationIntegrationTestSupport.GetInt64(
                    rows[0],
                    "ContentVersion"
                )
            )
            : throw new InvalidOperationException(
                $"Expected exactly one document row for '{documentUuid}', but found {rows.Count}."
            );
    }

    private async Task<AuthoritativeSampleStudentSchoolAssociationRow> ReadAssociationAsync(long documentId)
    {
        var rows = await _database.QueryRowsAsync(
            """
            SELECT
                "DocumentId",
                "SchoolId_Unified",
                "SchoolYear_Unified",
                "Calendar_DocumentId",
                "Calendar_CalendarCode",
                "SchoolYear_DocumentId",
                "School_DocumentId",
                "Student_DocumentId",
                "Student_StudentUniqueId",
                "EntryGradeLevelDescriptor_DescriptorId",
                "EntryDate",
                "PrimarySchool"
            FROM "edfi"."StudentSchoolAssociation"
            WHERE "DocumentId" = @documentId;
            """,
            new NpgsqlParameter("documentId", documentId)
        );

        return rows.Count == 1
            ? new AuthoritativeSampleStudentSchoolAssociationRow(
                AuthoritativeSampleStudentSchoolAssociationIntegrationTestSupport.GetInt64(
                    rows[0],
                    "DocumentId"
                ),
                AuthoritativeSampleStudentSchoolAssociationIntegrationTestSupport.GetInt64(
                    rows[0],
                    "SchoolId_Unified"
                ),
                AuthoritativeSampleStudentSchoolAssociationIntegrationTestSupport.GetInt32(
                    rows[0],
                    "SchoolYear_Unified"
                ),
                AuthoritativeSampleStudentSchoolAssociationIntegrationTestSupport.GetInt64(
                    rows[0],
                    "Calendar_DocumentId"
                ),
                AuthoritativeSampleStudentSchoolAssociationIntegrationTestSupport.GetString(
                    rows[0],
                    "Calendar_CalendarCode"
                ),
                AuthoritativeSampleStudentSchoolAssociationIntegrationTestSupport.GetInt64(
                    rows[0],
                    "SchoolYear_DocumentId"
                ),
                AuthoritativeSampleStudentSchoolAssociationIntegrationTestSupport.GetInt64(
                    rows[0],
                    "School_DocumentId"
                ),
                AuthoritativeSampleStudentSchoolAssociationIntegrationTestSupport.GetInt64(
                    rows[0],
                    "Student_DocumentId"
                ),
                AuthoritativeSampleStudentSchoolAssociationIntegrationTestSupport.GetString(
                    rows[0],
                    "Student_StudentUniqueId"
                ),
                AuthoritativeSampleStudentSchoolAssociationIntegrationTestSupport.GetInt64(
                    rows[0],
                    "EntryGradeLevelDescriptor_DescriptorId"
                ),
                AuthoritativeSampleStudentSchoolAssociationIntegrationTestSupport.GetDateOnly(
                    rows[0],
                    "EntryDate"
                ),
                AuthoritativeSampleStudentSchoolAssociationIntegrationTestSupport.GetBoolean(
                    rows[0],
                    "PrimarySchool"
                )
            )
            : throw new InvalidOperationException(
                $"Expected exactly one StudentSchoolAssociation row for document id '{documentId}', but found {rows.Count}."
            );
    }

    private async Task<AuthoritativeSampleStudentSchoolAssociationExtensionRow> ReadAssociationExtensionAsync(
        long documentId
    )
    {
        var rows = await _database.QueryRowsAsync(
            """
            SELECT "DocumentId", "MembershipTypeDescriptor_DescriptorId"
            FROM "sample"."StudentSchoolAssociationExtension"
            WHERE "DocumentId" = @documentId;
            """,
            new NpgsqlParameter("documentId", documentId)
        );

        return rows.Count == 1
            ? new AuthoritativeSampleStudentSchoolAssociationExtensionRow(
                AuthoritativeSampleStudentSchoolAssociationIntegrationTestSupport.GetInt64(
                    rows[0],
                    "DocumentId"
                ),
                AuthoritativeSampleStudentSchoolAssociationIntegrationTestSupport.GetInt64(
                    rows[0],
                    "MembershipTypeDescriptor_DescriptorId"
                )
            )
            : throw new InvalidOperationException(
                $"Expected exactly one StudentSchoolAssociationExtension row for document id '{documentId}', but found {rows.Count}."
            );
    }

    private async Task<
        IReadOnlyList<AuthoritativeSampleStudentSchoolAssociationAlternativeGraduationPlanRow>
    > ReadAlternativeGraduationPlansAsync(long documentId)
    {
        var rows = await _database.QueryRowsAsync(
            """
            SELECT
                "CollectionItemId",
                "Ordinal",
                "StudentSchoolAssociation_DocumentId",
                "AlternativeGraduationPlan_DocumentId",
                "AlternativeGraduationPlan_EducationOrganizationId",
                "AlternativeGraduationPlan_GraduationPlanTypeDescript_0b71806181",
                "AlternativeGraduationPlan_GraduationSchoolYear"
            FROM "edfi"."StudentSchoolAssociationAlternativeGraduationPlan"
            WHERE "StudentSchoolAssociation_DocumentId" = @documentId
            ORDER BY "Ordinal";
            """,
            new NpgsqlParameter("documentId", documentId)
        );

        return rows.Select(row => new AuthoritativeSampleStudentSchoolAssociationAlternativeGraduationPlanRow(
                AuthoritativeSampleStudentSchoolAssociationIntegrationTestSupport.GetInt64(
                    row,
                    "CollectionItemId"
                ),
                AuthoritativeSampleStudentSchoolAssociationIntegrationTestSupport.GetInt32(row, "Ordinal"),
                AuthoritativeSampleStudentSchoolAssociationIntegrationTestSupport.GetInt64(
                    row,
                    "StudentSchoolAssociation_DocumentId"
                ),
                AuthoritativeSampleStudentSchoolAssociationIntegrationTestSupport.GetInt64(
                    row,
                    "AlternativeGraduationPlan_DocumentId"
                ),
                AuthoritativeSampleStudentSchoolAssociationIntegrationTestSupport.GetInt64(
                    row,
                    "AlternativeGraduationPlan_EducationOrganizationId"
                ),
                AuthoritativeSampleStudentSchoolAssociationIntegrationTestSupport.GetInt64(
                    row,
                    "AlternativeGraduationPlan_GraduationPlanTypeDescript_0b71806181"
                ),
                AuthoritativeSampleStudentSchoolAssociationIntegrationTestSupport.GetInt32(
                    row,
                    "AlternativeGraduationPlan_GraduationSchoolYear"
                )
            ))
            .ToArray();
    }
}

[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
[NonParallelizable]
public class Given_A_Postgresql_Relational_Write_Key_Unification_Conflict_With_The_Authoritative_Sample_StudentSchoolAssociation_Fixture
{
    private const long SchoolId = 100;
    private const long ConflictSchoolId = 200;
    private const int SchoolYear = 2024;
    private const int FoundationGraduationSchoolYear = 2026;
    private const int EndorsementGraduationSchoolYear = 2027;
    private const int StemGraduationSchoolYear = 2028;
    private const string StudentUniqueId = "10001";
    private const string ConflictCalendarCode = "ALT";
    private const string NinthGradeLevelDescriptorUri = "uri://ed-fi.org/GradeLevelDescriptor#Ninth grade";
    private const string CalendarTypeDescriptorUri = "uri://ed-fi.org/CalendarTypeDescriptor#Instructional";
    private const string GraduationPlanTypeDescriptorUri =
        "uri://ed-fi.org/GraduationPlanTypeDescriptor#Foundation";
    private const string PathwayEducationPlanDescriptorUri =
        "uri://ed-fi.org/EducationPlanDescriptor#Pathway";
    private const string InterventionEducationPlanDescriptorUri =
        "uri://ed-fi.org/EducationPlanDescriptor#Intervention";
    private const string CareerEducationPlanDescriptorUri = "uri://ed-fi.org/EducationPlanDescriptor#Career";
    private const string ResidentMembershipTypeDescriptorUri =
        "uri://sample.org/MembershipTypeDescriptor#Resident";
    private const string TransferMembershipTypeDescriptorUri =
        "uri://sample.org/MembershipTypeDescriptor#Transfer";

    private const string NegativeRequestBodyJson = """
        {
          "entryDate": "2024-08-20",
          "entryGradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Ninth grade",
          "primarySchool": true,
          "schoolReference": {
            "schoolId": 100
          },
          "calendarReference": {
            "calendarCode": "ALT",
            "schoolId": 200,
            "schoolYear": 2024
          },
          "schoolYearTypeReference": {
            "schoolYear": 2024
          },
          "studentReference": {
            "studentUniqueId": "10001"
          },
          "alternativeGraduationPlans": [
            {
              "alternativeGraduationPlanReference": {
                "educationOrganizationId": 100,
                "graduationPlanTypeDescriptor": "uri://ed-fi.org/GraduationPlanTypeDescriptor#Foundation",
                "graduationSchoolYear": 2026
              }
            },
            {
              "alternativeGraduationPlanReference": {
                "educationOrganizationId": 100,
                "graduationPlanTypeDescriptor": "uri://ed-fi.org/GraduationPlanTypeDescriptor#Foundation",
                "graduationSchoolYear": 2027
              }
            }
          ],
          "educationPlans": [
            {
              "educationPlanDescriptor": "uri://ed-fi.org/EducationPlanDescriptor#Pathway"
            },
            {
              "educationPlanDescriptor": "uri://ed-fi.org/EducationPlanDescriptor#Intervention"
            }
          ],
          "_ext": {
            "sample": {
              "membershipTypeDescriptor": "uri://sample.org/MembershipTypeDescriptor#Resident"
            }
          }
        }
        """;

    private static readonly DocumentUuid RejectedDocumentUuid = new(
        Guid.Parse("abababab-0000-0000-0000-000000000002")
    );

    private PostgresqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private PostgresqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;
    private ResourceInfo _resourceInfo = null!;
    private ResourceSchema _baseResourceSchema = null!;
    private AuthoritativeSampleStudentSchoolAssociationSeedData _seedData = null!;
    private UpsertResult _result = null!;
    private AuthoritativeSampleStudentSchoolAssociationRejectedWriteSnapshot _snapshotBefore = null!;
    private AuthoritativeSampleStudentSchoolAssociationRejectedWriteSnapshot _snapshotAfter = null!;

    [SetUp]
    public async Task Setup()
    {
        _fixture = PostgresqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            AuthoritativeSampleStudentSchoolAssociationIntegrationTestSupport.FixtureRelativePath
        );
        _mappingSet = new MappingSetCompiler().Compile(_fixture.ModelSet);
        _database = await PostgresqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
        _serviceProvider =
            AuthoritativeSampleStudentSchoolAssociationIntegrationTestSupport.CreateServiceProvider();

        var (baseProjectSchema, baseResourceSchema) =
            AuthoritativeSampleStudentSchoolAssociationIntegrationTestSupport.GetResourceSchema(
                _fixture.EffectiveSchemaSet,
                "ed-fi",
                "StudentSchoolAssociation"
            );
        _resourceInfo = AuthoritativeSampleStudentSchoolAssociationIntegrationTestSupport.CreateResourceInfo(
            baseProjectSchema,
            baseResourceSchema
        );
        _baseResourceSchema = baseResourceSchema;
        _seedData = await SeedReferenceDataAsync();
        await DisableStudentSchoolAssociationReferentialIdentityTriggerAsync();

        _snapshotBefore = await ReadRejectedWriteSnapshotAsync();
        _result = await ExecuteCreateAsync(
            NegativeRequestBodyJson,
            RejectedDocumentUuid,
            "pg-authoritative-sample-student-school-association-key-unification-conflict"
        );

        if (_result is UpsertResult.UpsertFailureReference referenceFailure)
        {
            Assert.Fail(
                $"Expected validation failure but got reference failure: {AuthoritativeSampleStudentSchoolAssociationIntegrationTestSupport.FormatReferenceFailure(referenceFailure)}"
            );
        }

        _snapshotAfter = await ReadRejectedWriteSnapshotAsync();
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
    public void It_returns_a_validation_failure_and_leaves_document_and_authoritative_tables_unchanged()
    {
        _result.Should().BeOfType<UpsertResult.UpsertFailureValidation>();

        var validationFailure = _result
            .As<UpsertResult.UpsertFailureValidation>()
            .ValidationFailures.Should()
            .ContainSingle()
            .Subject;

        validationFailure.Path.Value.Should().Be("$.schoolReference.schoolId");
        validationFailure
            .Message.Should()
            .Contain("Key-unification conflict for canonical column 'SchoolId_Unified'");

        _snapshotAfter.Should().BeEquivalentTo(_snapshotBefore);
        _snapshotAfter.DocumentUuids.Should().NotContain(RejectedDocumentUuid.Value);
        _snapshotAfter.AssociationDocumentIds.Should().BeEmpty();
        _snapshotAfter.AssociationExtensionDocumentIds.Should().BeEmpty();
        _snapshotAfter.AlternativeGraduationPlanCollectionItemIds.Should().BeEmpty();
        _snapshotAfter.EducationPlanCollectionItemIds.Should().BeEmpty();
        _snapshotAfter.DocumentUuids.Count.Should().Be(_snapshotBefore.DocumentUuids.Count);
        _mappingSet
            .ResourceKeyIdByResource[new QualifiedResourceName("Ed-Fi", "StudentSchoolAssociation")]
            .Should()
            .BeGreaterThan((short)0);
        _seedData.ConflictCalendarDocumentId.Should().BeGreaterThan(0L);
    }

    private async Task<UpsertResult> ExecuteCreateAsync(
        string requestBodyJson,
        DocumentUuid documentUuid,
        string traceId
    )
    {
        await using var scope = _serviceProvider.CreateAsyncScope();
        SetSelectedInstance(scope.ServiceProvider);

        var requestBody = JsonNode.Parse(requestBodyJson)!;
        var request = new UpsertRequest(
            ResourceInfo: _resourceInfo,
            DocumentInfo: AuthoritativeSampleStudentSchoolAssociationIntegrationTestSupport.CreateDocumentInfo(
                requestBody,
                _resourceInfo,
                _baseResourceSchema,
                _mappingSet,
                _seedData.GraduationPlanTypeDescriptorId
            ),
            MappingSet: _mappingSet,
            EdfiDoc: requestBody,
            Headers: [],
            TraceId: new TraceId(traceId),
            DocumentUuid: documentUuid,
            DocumentSecurityElements: new([], [], [], [], []),
            UpdateCascadeHandler: new AuthoritativeSampleStudentSchoolAssociationNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new AuthoritativeSampleStudentSchoolAssociationAllowAllResourceAuthorizationHandler(),
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
                    InstanceName: "PostgresqlRelationalWriteAuthoritativeSampleStudentSchoolAssociationConflict",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );
    }

    private async Task<AuthoritativeSampleStudentSchoolAssociationSeedData> SeedReferenceDataAsync()
    {
        var schoolResourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "School");
        var studentResourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "Student");
        var schoolYearTypeResourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "SchoolYearType");
        var calendarResourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "Calendar");
        var graduationPlanResourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "GraduationPlan");
        var calendarTypeDescriptorResourceKeyId = await GetResourceKeyIdAsync(
            "Ed-Fi",
            "CalendarTypeDescriptor"
        );
        var gradeLevelDescriptorResourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "GradeLevelDescriptor");
        var graduationPlanTypeDescriptorResourceKeyId = await GetResourceKeyIdAsync(
            "Ed-Fi",
            "GraduationPlanTypeDescriptor"
        );
        var educationPlanDescriptorResourceKeyId = await GetResourceKeyIdAsync(
            "Ed-Fi",
            "EducationPlanDescriptor"
        );
        var membershipTypeDescriptorResourceKeyId = await GetResourceKeyIdAsync(
            "Sample",
            "MembershipTypeDescriptor"
        );

        var calendarTypeDescriptorId = await SeedDescriptorAsync(
            Guid.Parse("77777777-0000-0000-0000-000000000001"),
            calendarTypeDescriptorResourceKeyId,
            "Ed-Fi",
            "CalendarTypeDescriptor",
            "Ed-Fi:CalendarTypeDescriptor",
            CalendarTypeDescriptorUri,
            "uri://ed-fi.org/CalendarTypeDescriptor",
            "Instructional",
            "Instructional"
        );
        var ninthGradeLevelDescriptorId = await SeedDescriptorAsync(
            Guid.Parse("77777777-0000-0000-0000-000000000002"),
            gradeLevelDescriptorResourceKeyId,
            "Ed-Fi",
            "GradeLevelDescriptor",
            "Ed-Fi:GradeLevelDescriptor",
            NinthGradeLevelDescriptorUri,
            "uri://ed-fi.org/GradeLevelDescriptor",
            "Ninth grade",
            "Ninth grade"
        );
        var graduationPlanTypeDescriptorId = await SeedDescriptorAsync(
            Guid.Parse("77777777-0000-0000-0000-000000000003"),
            graduationPlanTypeDescriptorResourceKeyId,
            "Ed-Fi",
            "GraduationPlanTypeDescriptor",
            "Ed-Fi:GraduationPlanTypeDescriptor",
            GraduationPlanTypeDescriptorUri,
            "uri://ed-fi.org/GraduationPlanTypeDescriptor",
            "Foundation",
            "Foundation"
        );
        var pathwayEducationPlanDescriptorId = await SeedDescriptorAsync(
            Guid.Parse("77777777-0000-0000-0000-000000000004"),
            educationPlanDescriptorResourceKeyId,
            "Ed-Fi",
            "EducationPlanDescriptor",
            "Ed-Fi:EducationPlanDescriptor",
            PathwayEducationPlanDescriptorUri,
            "uri://ed-fi.org/EducationPlanDescriptor",
            "Pathway",
            "Pathway"
        );
        var interventionEducationPlanDescriptorId = await SeedDescriptorAsync(
            Guid.Parse("77777777-0000-0000-0000-000000000005"),
            educationPlanDescriptorResourceKeyId,
            "Ed-Fi",
            "EducationPlanDescriptor",
            "Ed-Fi:EducationPlanDescriptor",
            InterventionEducationPlanDescriptorUri,
            "uri://ed-fi.org/EducationPlanDescriptor",
            "Intervention",
            "Intervention"
        );
        var careerEducationPlanDescriptorId = await SeedDescriptorAsync(
            Guid.Parse("77777777-0000-0000-0000-000000000006"),
            educationPlanDescriptorResourceKeyId,
            "Ed-Fi",
            "EducationPlanDescriptor",
            "Ed-Fi:EducationPlanDescriptor",
            CareerEducationPlanDescriptorUri,
            "uri://ed-fi.org/EducationPlanDescriptor",
            "Career",
            "Career"
        );
        var residentMembershipTypeDescriptorId = await SeedDescriptorAsync(
            Guid.Parse("77777777-0000-0000-0000-000000000007"),
            membershipTypeDescriptorResourceKeyId,
            "Sample",
            "MembershipTypeDescriptor",
            "Sample:MembershipTypeDescriptor",
            ResidentMembershipTypeDescriptorUri,
            "uri://sample.org/MembershipTypeDescriptor",
            "Resident",
            "Resident"
        );
        var transferMembershipTypeDescriptorId = await SeedDescriptorAsync(
            Guid.Parse("77777777-0000-0000-0000-000000000008"),
            membershipTypeDescriptorResourceKeyId,
            "Sample",
            "MembershipTypeDescriptor",
            "Sample:MembershipTypeDescriptor",
            TransferMembershipTypeDescriptorUri,
            "uri://sample.org/MembershipTypeDescriptor",
            "Transfer",
            "Transfer"
        );

        var studentSchoolYearTypeDocumentId = await InsertDocumentAsync(
            Guid.Parse("88888888-0000-0000-0000-000000000001"),
            schoolYearTypeResourceKeyId
        );
        await InsertSchoolYearTypeAsync(studentSchoolYearTypeDocumentId, SchoolYear, true);
        await InsertReferentialIdentityAsync(
            CreateReferentialId(
                ("Ed-Fi", "SchoolYearType", false),
                ("$.schoolYear", SchoolYear.ToString(CultureInfo.InvariantCulture))
            ),
            studentSchoolYearTypeDocumentId,
            schoolYearTypeResourceKeyId
        );

        var foundationGraduationSchoolYearTypeDocumentId = await InsertDocumentAsync(
            Guid.Parse("88888888-0000-0000-0000-000000000002"),
            schoolYearTypeResourceKeyId
        );
        await InsertSchoolYearTypeAsync(
            foundationGraduationSchoolYearTypeDocumentId,
            FoundationGraduationSchoolYear,
            false
        );
        await InsertReferentialIdentityAsync(
            CreateReferentialId(
                ("Ed-Fi", "SchoolYearType", false),
                ("$.schoolYear", FoundationGraduationSchoolYear.ToString(CultureInfo.InvariantCulture))
            ),
            foundationGraduationSchoolYearTypeDocumentId,
            schoolYearTypeResourceKeyId
        );

        var endorsementGraduationSchoolYearTypeDocumentId = await InsertDocumentAsync(
            Guid.Parse("88888888-0000-0000-0000-000000000003"),
            schoolYearTypeResourceKeyId
        );
        await InsertSchoolYearTypeAsync(
            endorsementGraduationSchoolYearTypeDocumentId,
            EndorsementGraduationSchoolYear,
            false
        );
        await InsertReferentialIdentityAsync(
            CreateReferentialId(
                ("Ed-Fi", "SchoolYearType", false),
                ("$.schoolYear", EndorsementGraduationSchoolYear.ToString(CultureInfo.InvariantCulture))
            ),
            endorsementGraduationSchoolYearTypeDocumentId,
            schoolYearTypeResourceKeyId
        );

        var stemGraduationSchoolYearTypeDocumentId = await InsertDocumentAsync(
            Guid.Parse("88888888-0000-0000-0000-000000000004"),
            schoolYearTypeResourceKeyId
        );
        await InsertSchoolYearTypeAsync(
            stemGraduationSchoolYearTypeDocumentId,
            StemGraduationSchoolYear,
            false
        );
        await InsertReferentialIdentityAsync(
            CreateReferentialId(
                ("Ed-Fi", "SchoolYearType", false),
                ("$.schoolYear", StemGraduationSchoolYear.ToString(CultureInfo.InvariantCulture))
            ),
            stemGraduationSchoolYearTypeDocumentId,
            schoolYearTypeResourceKeyId
        );

        var schoolDocumentId = await InsertDocumentAsync(
            Guid.Parse("99999999-0000-0000-0000-000000000001"),
            schoolResourceKeyId
        );
        await InsertSchoolAsync(schoolDocumentId, SchoolId, "Alpha Academy");
        await InsertReferentialIdentityAsync(
            CreateReferentialId(
                ("Ed-Fi", "School", false),
                ("$.schoolId", SchoolId.ToString(CultureInfo.InvariantCulture))
            ),
            schoolDocumentId,
            schoolResourceKeyId
        );
        await InsertReferentialIdentityAsync(
            CreateReferentialId(
                ("Ed-Fi", "EducationOrganization", false),
                ("$.educationOrganizationId", SchoolId.ToString(CultureInfo.InvariantCulture))
            ),
            schoolDocumentId,
            await GetResourceKeyIdAsync("Ed-Fi", "EducationOrganization")
        );

        var conflictSchoolDocumentId = await InsertDocumentAsync(
            Guid.Parse("99999999-0000-0000-0000-000000000002"),
            schoolResourceKeyId
        );
        await InsertSchoolAsync(conflictSchoolDocumentId, ConflictSchoolId, "Beta Academy");
        await InsertReferentialIdentityAsync(
            CreateReferentialId(
                ("Ed-Fi", "School", false),
                ("$.schoolId", ConflictSchoolId.ToString(CultureInfo.InvariantCulture))
            ),
            conflictSchoolDocumentId,
            schoolResourceKeyId
        );
        await InsertReferentialIdentityAsync(
            CreateReferentialId(
                ("Ed-Fi", "EducationOrganization", false),
                ("$.educationOrganizationId", ConflictSchoolId.ToString(CultureInfo.InvariantCulture))
            ),
            conflictSchoolDocumentId,
            await GetResourceKeyIdAsync("Ed-Fi", "EducationOrganization")
        );

        var studentDocumentId = await InsertDocumentAsync(
            Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"),
            studentResourceKeyId
        );
        await InsertStudentAsync(studentDocumentId, StudentUniqueId, "Maya", "Lopez");
        await InsertReferentialIdentityAsync(
            CreateReferentialId(("Ed-Fi", "Student", false), ("$.studentUniqueId", StudentUniqueId)),
            studentDocumentId,
            studentResourceKeyId
        );

        var conflictCalendarDocumentId = await InsertDocumentAsync(
            Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001"),
            calendarResourceKeyId
        );
        await InsertCalendarAsync(
            conflictCalendarDocumentId,
            studentSchoolYearTypeDocumentId,
            SchoolYear,
            conflictSchoolDocumentId,
            ConflictSchoolId,
            calendarTypeDescriptorId,
            ConflictCalendarCode
        );
        await InsertReferentialIdentityAsync(
            CreateReferentialId(
                ("Ed-Fi", "Calendar", false),
                ("$.calendarCode", ConflictCalendarCode),
                ("$.schoolReference.schoolId", ConflictSchoolId.ToString(CultureInfo.InvariantCulture)),
                ("$.schoolYearTypeReference.schoolYear", SchoolYear.ToString(CultureInfo.InvariantCulture))
            ),
            conflictCalendarDocumentId,
            calendarResourceKeyId
        );

        var foundationGraduationPlanDocumentId = await InsertDocumentAsync(
            Guid.Parse("cccccccc-0000-0000-0000-000000000001"),
            graduationPlanResourceKeyId
        );
        await InsertGraduationPlanAsync(
            foundationGraduationPlanDocumentId,
            schoolDocumentId,
            SchoolId,
            foundationGraduationSchoolYearTypeDocumentId,
            FoundationGraduationSchoolYear,
            graduationPlanTypeDescriptorId,
            26.000m
        );
        await InsertReferentialIdentityAsync(
            CreateReferentialId(
                ("Ed-Fi", "GraduationPlan", false),
                (
                    "$.educationOrganizationReference.educationOrganizationId",
                    SchoolId.ToString(CultureInfo.InvariantCulture)
                ),
                (
                    "$.graduationPlanTypeDescriptor",
                    graduationPlanTypeDescriptorId.ToString(CultureInfo.InvariantCulture)
                ),
                (
                    "$.graduationSchoolYearTypeReference.schoolYear",
                    FoundationGraduationSchoolYear.ToString(CultureInfo.InvariantCulture)
                )
            ),
            foundationGraduationPlanDocumentId,
            graduationPlanResourceKeyId
        );

        var endorsementGraduationPlanDocumentId = await InsertDocumentAsync(
            Guid.Parse("cccccccc-0000-0000-0000-000000000002"),
            graduationPlanResourceKeyId
        );
        await InsertGraduationPlanAsync(
            endorsementGraduationPlanDocumentId,
            schoolDocumentId,
            SchoolId,
            endorsementGraduationSchoolYearTypeDocumentId,
            EndorsementGraduationSchoolYear,
            graduationPlanTypeDescriptorId,
            27.500m
        );
        await InsertReferentialIdentityAsync(
            CreateReferentialId(
                ("Ed-Fi", "GraduationPlan", false),
                (
                    "$.educationOrganizationReference.educationOrganizationId",
                    SchoolId.ToString(CultureInfo.InvariantCulture)
                ),
                (
                    "$.graduationPlanTypeDescriptor",
                    graduationPlanTypeDescriptorId.ToString(CultureInfo.InvariantCulture)
                ),
                (
                    "$.graduationSchoolYearTypeReference.schoolYear",
                    EndorsementGraduationSchoolYear.ToString(CultureInfo.InvariantCulture)
                )
            ),
            endorsementGraduationPlanDocumentId,
            graduationPlanResourceKeyId
        );

        var stemGraduationPlanDocumentId = await InsertDocumentAsync(
            Guid.Parse("cccccccc-0000-0000-0000-000000000003"),
            graduationPlanResourceKeyId
        );
        await InsertGraduationPlanAsync(
            stemGraduationPlanDocumentId,
            schoolDocumentId,
            SchoolId,
            stemGraduationSchoolYearTypeDocumentId,
            StemGraduationSchoolYear,
            graduationPlanTypeDescriptorId,
            28.000m
        );
        await InsertReferentialIdentityAsync(
            CreateReferentialId(
                ("Ed-Fi", "GraduationPlan", false),
                (
                    "$.educationOrganizationReference.educationOrganizationId",
                    SchoolId.ToString(CultureInfo.InvariantCulture)
                ),
                (
                    "$.graduationPlanTypeDescriptor",
                    graduationPlanTypeDescriptorId.ToString(CultureInfo.InvariantCulture)
                ),
                (
                    "$.graduationSchoolYearTypeReference.schoolYear",
                    StemGraduationSchoolYear.ToString(CultureInfo.InvariantCulture)
                )
            ),
            stemGraduationPlanDocumentId,
            graduationPlanResourceKeyId
        );

        return new(
            schoolDocumentId,
            conflictSchoolDocumentId,
            0,
            conflictCalendarDocumentId,
            studentDocumentId,
            studentSchoolYearTypeDocumentId,
            ninthGradeLevelDescriptorId,
            0,
            residentMembershipTypeDescriptorId,
            transferMembershipTypeDescriptorId,
            pathwayEducationPlanDescriptorId,
            interventionEducationPlanDescriptorId,
            careerEducationPlanDescriptorId,
            graduationPlanTypeDescriptorId,
            foundationGraduationPlanDocumentId,
            endorsementGraduationPlanDocumentId,
            stemGraduationPlanDocumentId
        );
    }

    private async Task DisableStudentSchoolAssociationReferentialIdentityTriggerAsync()
    {
        await _database.ExecuteNonQueryAsync(
            """
            ALTER TABLE "edfi"."StudentSchoolAssociation"
            DISABLE TRIGGER "TR_StudentSchoolAssociation_ReferentialIdentity";
            """
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

    private async Task<long> SeedDescriptorAsync(
        Guid documentUuid,
        short resourceKeyId,
        string projectName,
        string resourceName,
        string discriminator,
        string uri,
        string @namespace,
        string codeValue,
        string shortDescription
    )
    {
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
            CreateDescriptorReferentialId(projectName, resourceName, uri),
            documentId,
            resourceKeyId
        );

        return documentId;
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
            new NpgsqlParameter("birthDate", new DateOnly(2010, 5, 14)),
            new NpgsqlParameter("firstName", firstName),
            new NpgsqlParameter("lastSurname", lastSurname),
            new NpgsqlParameter("studentUniqueId", studentUniqueId)
        );
    }

    private async Task InsertSchoolYearTypeAsync(long documentId, int schoolYear, bool currentSchoolYear)
    {
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO "edfi"."SchoolYearType" (
                "DocumentId",
                "CurrentSchoolYear",
                "SchoolYear",
                "SchoolYearDescription"
            )
            VALUES (
                @documentId,
                @currentSchoolYear,
                @schoolYear,
                @schoolYearDescription
            );
            """,
            new NpgsqlParameter("documentId", documentId),
            new NpgsqlParameter("currentSchoolYear", currentSchoolYear),
            new NpgsqlParameter("schoolYear", schoolYear),
            new NpgsqlParameter("schoolYearDescription", $"{schoolYear}-{schoolYear + 1}")
        );
    }

    private async Task InsertCalendarAsync(
        long documentId,
        long schoolYearDocumentId,
        int schoolYear,
        long schoolDocumentId,
        long schoolId,
        long calendarTypeDescriptorId,
        string calendarCode
    )
    {
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO "edfi"."Calendar" (
                "DocumentId",
                "SchoolYear_DocumentId",
                "SchoolYear_SchoolYear",
                "School_DocumentId",
                "School_SchoolId",
                "CalendarTypeDescriptor_DescriptorId",
                "CalendarCode"
            )
            VALUES (
                @documentId,
                @schoolYearDocumentId,
                @schoolYear,
                @schoolDocumentId,
                @schoolId,
                @calendarTypeDescriptorId,
                @calendarCode
            );
            """,
            new NpgsqlParameter("documentId", documentId),
            new NpgsqlParameter("schoolYearDocumentId", schoolYearDocumentId),
            new NpgsqlParameter("schoolYear", schoolYear),
            new NpgsqlParameter("schoolDocumentId", schoolDocumentId),
            new NpgsqlParameter("schoolId", schoolId),
            new NpgsqlParameter("calendarTypeDescriptorId", calendarTypeDescriptorId),
            new NpgsqlParameter("calendarCode", calendarCode)
        );
    }

    private async Task InsertGraduationPlanAsync(
        long documentId,
        long educationOrganizationDocumentId,
        long educationOrganizationId,
        long graduationSchoolYearDocumentId,
        int graduationSchoolYear,
        long graduationPlanTypeDescriptorId,
        decimal totalRequiredCredits
    )
    {
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO "edfi"."GraduationPlan" (
                "DocumentId",
                "EducationOrganization_DocumentId",
                "EducationOrganization_EducationOrganizationId",
                "GraduationSchoolYear_DocumentId",
                "GraduationSchoolYear_GraduationSchoolYear",
                "GraduationPlanTypeDescriptor_DescriptorId",
                "TotalRequiredCredits"
            )
            VALUES (
                @documentId,
                @educationOrganizationDocumentId,
                @educationOrganizationId,
                @graduationSchoolYearDocumentId,
                @graduationSchoolYear,
                @graduationPlanTypeDescriptorId,
                @totalRequiredCredits
            );
            """,
            new NpgsqlParameter("documentId", documentId),
            new NpgsqlParameter("educationOrganizationDocumentId", educationOrganizationDocumentId),
            new NpgsqlParameter("educationOrganizationId", educationOrganizationId),
            new NpgsqlParameter("graduationSchoolYearDocumentId", graduationSchoolYearDocumentId),
            new NpgsqlParameter("graduationSchoolYear", graduationSchoolYear),
            new NpgsqlParameter("graduationPlanTypeDescriptorId", graduationPlanTypeDescriptorId),
            new NpgsqlParameter("totalRequiredCredits", totalRequiredCredits)
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

    private async Task<AuthoritativeSampleStudentSchoolAssociationRejectedWriteSnapshot> ReadRejectedWriteSnapshotAsync()
    {
        return new(
            DocumentUuids: await ReadGuidListAsync(
                """
                SELECT "DocumentUuid"
                FROM "dms"."Document"
                ORDER BY "DocumentUuid";
                """,
                "DocumentUuid"
            ),
            AssociationDocumentIds: await ReadInt64ListAsync(
                """
                SELECT "DocumentId"
                FROM "edfi"."StudentSchoolAssociation"
                ORDER BY "DocumentId";
                """,
                "DocumentId"
            ),
            AssociationExtensionDocumentIds: await ReadInt64ListAsync(
                """
                SELECT "DocumentId"
                FROM "sample"."StudentSchoolAssociationExtension"
                ORDER BY "DocumentId";
                """,
                "DocumentId"
            ),
            AlternativeGraduationPlanCollectionItemIds: await ReadInt64ListAsync(
                """
                SELECT "CollectionItemId"
                FROM "edfi"."StudentSchoolAssociationAlternativeGraduationPlan"
                ORDER BY "CollectionItemId";
                """,
                "CollectionItemId"
            ),
            EducationPlanCollectionItemIds: await ReadInt64ListAsync(
                """
                SELECT "CollectionItemId"
                FROM "edfi"."StudentSchoolAssociationEducationPlan"
                ORDER BY "CollectionItemId";
                """,
                "CollectionItemId"
            )
        );
    }

    private async Task<IReadOnlyList<Guid>> ReadGuidListAsync(string sql, string columnName)
    {
        var rows = await _database.QueryRowsAsync(sql);

        return rows.Select(row =>
                AuthoritativeSampleStudentSchoolAssociationIntegrationTestSupport.GetGuid(row, columnName)
            )
            .ToArray();
    }

    private async Task<IReadOnlyList<long>> ReadInt64ListAsync(string sql, string columnName)
    {
        var rows = await _database.QueryRowsAsync(sql);

        return rows.Select(row =>
                AuthoritativeSampleStudentSchoolAssociationIntegrationTestSupport.GetInt64(row, columnName)
            )
            .ToArray();
    }
}

// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Globalization;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.Etag;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Backend.Postgresql;
using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.Backend;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Extraction;
using EdFi.DataManagementService.Core.Profile;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

file static class AuthoritativeSampleStudentSchoolAssociationIntegrationTestSupport
{
    public const string FixtureRelativePath = "src/dms/backend/Fixtures/authoritative/sample";

    public static ServiceProvider CreateServiceProvider()
    {
        ServiceCollection services = [];

        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<NpgsqlDataSourceCache>();
        services.AddScoped<IDataStoreSelection, DataStoreSelection>();
        services.AddScoped<NpgsqlDataSourceProvider>();
        services.Configure<DatabaseOptions>(options => options.IsolationLevel = IsolationLevel.ReadCommitted);
        services.AddSingleton<IReadableProfileProjector, ReadableProfileProjector>();
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
            AllowIdentityUpdates: resourceSchema.AllowIdentityUpdates
        );

    public static DocumentInfo CreateDocumentInfo(
        JsonNode requestBody,
        ResourceInfo resourceInfo,
        ResourceSchema baseResourceSchema,
        MappingSet mappingSet
    )
    {
        var (alternativeGraduationPlanReferences, alternativeGraduationPlanReferenceArrays) =
            CreateAlternativeGraduationPlanDocumentReferences(requestBody);
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
    ) CreateAlternativeGraduationPlanDocumentReferences(JsonNode requestBody)
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
            var graduationPlanTypeDescriptor = reference["graduationPlanTypeDescriptor"]
                ?.GetValue<string>()
                .ToLowerInvariant();

            if (
                string.IsNullOrWhiteSpace(educationOrganizationId)
                || string.IsNullOrWhiteSpace(graduationSchoolYear)
                || string.IsNullOrWhiteSpace(graduationPlanTypeDescriptor)
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
                new DocumentIdentityElement(
                    new JsonPath("$.graduationPlanTypeDescriptor"),
                    graduationPlanTypeDescriptor
                ),
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
public class Given_A_Postgresql_Relational_Write_Smoke_With_The_Authoritative_Sample_StudentSchoolAssociation_Fixture
{
    // The readable profile name in effect for the projected read. Threaded into the served _etag's
    // profileCode so the projected representation carries a distinct strong validator (profile is
    // state-significant per adr-etag-from-content-version.md). Backend-direct integration tests must
    // set ProfileName explicitly; Core populates it in production.
    private const string ReadableProfileName = "sample-readable-profile";

    private static readonly ContentTypeDefinition ReadableProfileContentType = new(
        MemberSelection.IncludeOnly,
        [],
        [],
        [
            new CollectionRule(
                "alternativeGraduationPlans",
                MemberSelection.IncludeOnly,
                null,
                [],
                [
                    new ObjectRule(
                        "alternativeGraduationPlanReference",
                        MemberSelection.IncludeOnly,
                        null,
                        [new PropertyRule("graduationSchoolYear")],
                        null,
                        null,
                        null
                    ),
                ],
                null,
                null,
                null
            ),
        ],
        [
            new ExtensionRule(
                "sample",
                MemberSelection.IncludeOnly,
                null,
                [new PropertyRule("membershipTypeDescriptor")],
                null,
                null
            ),
        ]
    );

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
    private GetResult _getResultAfterCreate = null!;
    private GetResult _profiledGetResultAfterCreate = null!;
    private GetResult _getResultAfterChangedUpdate = null!;
    private GetResult _getResultAfterNoOpUpdate = null!;
    private AuthoritativeSampleStudentSchoolAssociationPersistedState _stateAfterCreate = null!;
    private AuthoritativeSampleStudentSchoolAssociationPersistedState _stateAfterChangedUpdate = null!;
    private AuthoritativeSampleStudentSchoolAssociationPersistedState _stateAfterNoOpUpdate = null!;
    private DateTimeOffset _lastModifiedAtAfterCreate;
    private DateTimeOffset _lastModifiedAtAfterNoOpUpdate;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _fixture = PostgresqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            AuthoritativeSampleStudentSchoolAssociationIntegrationTestSupport.FixtureRelativePath,
            strict: true
        );
        _mappingSet = _fixture.MappingSet;
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
        _lastModifiedAtAfterCreate = await ReadContentLastModifiedAtAsync(
            StudentSchoolAssociationDocumentUuid.Value
        );
        _getResultAfterCreate = await ExecuteGetByIdAsync(
            StudentSchoolAssociationDocumentUuid,
            "pg-authoritative-sample-student-school-association-get-after-create"
        );
        _profiledGetResultAfterCreate = await ExecuteGetByIdAsync(
            StudentSchoolAssociationDocumentUuid,
            "pg-authoritative-sample-student-school-association-get-after-create-readable-profile",
            CreateReadableProfileProjectionContext()
        );

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
        _getResultAfterChangedUpdate = await ExecuteGetByIdAsync(
            StudentSchoolAssociationDocumentUuid,
            "pg-authoritative-sample-student-school-association-get-after-changed-update"
        );

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
        _lastModifiedAtAfterNoOpUpdate = await ReadContentLastModifiedAtAsync(
            StudentSchoolAssociationDocumentUuid.Value
        );
        _getResultAfterNoOpUpdate = await ExecuteGetByIdAsync(
            StudentSchoolAssociationDocumentUuid,
            "pg-authoritative-sample-student-school-association-get-after-no-op-update"
        );
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
    public void It_extracts_descriptor_valued_collection_reference_members_from_concrete_paths_via_the_shared_document_info_helper()
    {
        var documentInfo =
            AuthoritativeSampleStudentSchoolAssociationIntegrationTestSupport.CreateDocumentInfo(
                JsonNode.Parse(CreateRequestBodyJson)!,
                _resourceInfo,
                _baseResourceSchema,
                _mappingSet
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
    public void It_returns_the_create_etag_from_follow_up_get_by_id() =>
        RelationalGetIntegrationTestHelper.AssertWriteResultEtagParity(_createResult, _getResultAfterCreate);

    [Test]
    public void It_returns_the_changed_put_etag_from_follow_up_get_by_id() =>
        RelationalGetIntegrationTestHelper.AssertWriteResultEtagParity(
            _changedUpdateResult,
            _getResultAfterChangedUpdate
        );

    [Test]
    public void It_returns_the_repeat_put_etag_from_follow_up_get_by_id() =>
        RelationalGetIntegrationTestHelper.AssertWriteResultEtagParity(
            _noOpUpdateResult,
            _getResultAfterNoOpUpdate
        );

    [Test]
    public async Task It_matches_ResourceLinks_IfMatch_against_the_current_relational_state()
    {
        _getResultAfterNoOpUpdate.Should().BeOfType<GetResult.GetSuccess>();

        var currentResponse = ((GetResult.GetSuccess)_getResultAfterNoOpUpdate).EdfiDoc;
        var currentEtag = currentResponse["_etag"]!.GetValue<string>();

        // The served etag encodes link mode in its variantKey, but If-Match compares the
        // state-significant projection, which ignores linkFlag. An etag captured under the opposite
        // link mode is a different opaque string yet still satisfies the precondition.
        var oppositeLinkModeEtag = FlipLinkFlag(currentEtag);
        oppositeLinkModeEtag.Should().NotBe(currentEtag);

        var result = await CheckIfMatchAsync(oppositeLinkModeEtag);

        result.Should().NotBeNull();
        result!.IsSatisfied.Should().BeTrue();
    }

    [Test]
    public void It_reads_back_the_written_document_via_relational_get_by_id_with_readable_profile_projection()
    {
        var expectedDocument = CreateExpectedReadableProfileExternalResponse(
            CreateRequestBodyJson,
            StudentSchoolAssociationDocumentUuid.Value,
            _lastModifiedAtAfterCreate
        );

        _profiledGetResultAfterCreate.Should().BeOfType<GetResult.GetSuccess>();

        var success = (GetResult.GetSuccess)_profiledGetResultAfterCreate;
        var unprojectedSuccess = (GetResult.GetSuccess)_getResultAfterCreate;

        success.DocumentUuid.Should().Be(StudentSchoolAssociationDocumentUuid);
        success.LastModifiedTraceId.Should().BeNull();
        success.LastModifiedDate.Should().Be(_lastModifiedAtAfterCreate.UtcDateTime);
        success.EdfiDoc["educationPlans"].Should().BeNull();
        success.EdfiDoc["entryGradeLevelDescriptor"].Should().BeNull();
        success.EdfiDoc["alternativeGraduationPlans"]!
            .AsArray()
            .Select(plan =>
                plan?["alternativeGraduationPlanReference"]?["graduationSchoolYear"]?.GetValue<int>()
            )
            .Should()
            .Equal((int?)FoundationGraduationSchoolYear, EndorsementGraduationSchoolYear);
        success.EdfiDoc["alternativeGraduationPlans"]!
            .AsArray()
            .Select(plan =>
                plan?["alternativeGraduationPlanReference"]?["educationOrganizationId"]?.GetValue<long>()
            )
            .Should()
            .Equal((long?)null, null);
        RelationalGetIntegrationTestHelper.AssertComposedEtag(success.EdfiDoc["_etag"]!.GetValue<string>());
        // The served _etag is a strong validator of the projected representation. A readable-profile
        // projection changes the served bytes, so its _etag MUST differ from the unprojected read's —
        // profile is state-significant per adr-etag-from-content-version.md (a deliberate reversal of
        // the earlier profile-insensitive contract). They differ only in the profileCode component.
        success.EdfiDoc["_etag"]!
            .GetValue<string>()
            .Should()
            .NotBe(
                unprojectedSuccess.EdfiDoc["_etag"]!.GetValue<string>(),
                "a readable-profile projection yields a distinct strong-validator etag (profile is state-significant)"
            );
        RelationalGetIntegrationTestHelper
            .CanonicalizeJson(success.EdfiDoc)
            .Should()
            .Be(RelationalGetIntegrationTestHelper.CanonicalizeJson(expectedDocument));
    }

    [Test]
    public async Task It_reads_back_the_written_document_via_relational_get_by_id_with_semantic_json_equivalence_and_metadata()
    {
        var expectedDocument = RelationalGetIntegrationTestHelper.CreateExpectedExternalResponse(
            ChangedUpdateRequestBodyJson,
            _resourceInfo,
            _mappingSet,
            _stateAfterNoOpUpdate.Document.DocumentUuid,
            _lastModifiedAtAfterNoOpUpdate
        );

        RelationalGetIntegrationTestHelper.AssertStudentSchoolAssociationExternalResponse(
            _getResultAfterNoOpUpdate,
            StudentSchoolAssociationDocumentUuid,
            _lastModifiedAtAfterNoOpUpdate,
            expectedDocument,
            [EndorsementGraduationSchoolYear, StemGraduationSchoolYear],
            [InterventionEducationPlanDescriptorUri, CareerEducationPlanDescriptorUri]
        );
    }

    private async Task<GetResult> ExecuteGetByIdAsync(
        DocumentUuid documentUuid,
        string traceId,
        ReadableProfileProjectionContext? readableProfileProjectionContext = null
    )
    {
        await using var scope = _serviceProvider.CreateAsyncScope();
        SetSelectedInstance(scope.ServiceProvider);

        var request = new IntegrationRelationalGetRequest(
            DocumentUuid: documentUuid,
            ResourceInfo: _resourceInfo,
            MappingSet: _mappingSet,
            AuthorizationStrategyEvaluators: [],
            TraceId: new TraceId(traceId),
            ReadableProfileProjectionContext: readableProfileProjectionContext
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
            FROM "edfi"."StudentSchoolAssociation"
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

    private ReadableProfileProjectionContext CreateReadableProfileProjectionContext() =>
        new(
            ReadableProfileContentType,
            IReadableProfileProjector.ExtractIdentityPropertyNames(_baseResourceSchema.IdentityJsonPaths)
        )
        {
            ProfileName = ReadableProfileName,
        };

    private JsonObject CreateExpectedReadableProfileExternalResponse(
        string requestBodyJson,
        Guid documentUuid,
        DateTimeOffset lastModifiedAt
    )
    {
        var expectedDocument = RelationalGetIntegrationTestHelper.CreateExpectedExternalResponse(
            requestBodyJson,
            _resourceInfo,
            _mappingSet,
            documentUuid,
            lastModifiedAt
        );
        var identityPropertyNames = IReadableProfileProjector.ExtractIdentityPropertyNames(
            _baseResourceSchema.IdentityJsonPaths
        );
        HashSet<string> retainedTopLevelPropertyNames =
        [
            .. identityPropertyNames,
            "id",
            "_etag",
            "_lastModifiedDate",
            "alternativeGraduationPlans",
            "_ext",
        ];

        foreach (string propertyName in expectedDocument.Select(static property => property.Key).ToList())
        {
            if (!retainedTopLevelPropertyNames.Contains(propertyName))
            {
                expectedDocument.Remove(propertyName);
            }
        }

        var alternativeGraduationPlans =
            expectedDocument["alternativeGraduationPlans"] as JsonArray
            ?? throw new InvalidOperationException(
                "Expected projected document to retain alternativeGraduationPlans."
            );

        foreach (JsonNode? item in alternativeGraduationPlans)
        {
            var planObject =
                item as JsonObject
                ?? throw new InvalidOperationException(
                    "Expected alternativeGraduationPlans items to be JSON objects."
                );
            var referenceObject =
                planObject["alternativeGraduationPlanReference"] as JsonObject
                ?? throw new InvalidOperationException(
                    "Expected projected plan items to retain alternativeGraduationPlanReference."
                );

            foreach (string propertyName in referenceObject.Select(static property => property.Key).ToList())
            {
                if (!string.Equals(propertyName, "graduationSchoolYear", StringComparison.Ordinal))
                {
                    referenceObject.Remove(propertyName);
                }
            }

            foreach (string propertyName in planObject.Select(static property => property.Key).ToList())
            {
                if (
                    !string.Equals(
                        propertyName,
                        "alternativeGraduationPlanReference",
                        StringComparison.Ordinal
                    )
                )
                {
                    planObject.Remove(propertyName);
                }
            }
        }

        var extensionObject =
            expectedDocument["_ext"] as JsonObject
            ?? throw new InvalidOperationException("Expected projected document to retain _ext.");
        var sampleExtension =
            extensionObject["sample"] as JsonObject
            ?? throw new InvalidOperationException(
                "Expected projected document to retain the sample extension namespace."
            );

        foreach (string propertyName in sampleExtension.Select(static property => property.Key).ToList())
        {
            if (!string.Equals(propertyName, "membershipTypeDescriptor", StringComparison.Ordinal))
            {
                sampleExtension.Remove(propertyName);
            }
        }

        return expectedDocument;
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
                _mappingSet
            ),
            MappingSet: _mappingSet,
            EdfiDoc: requestBody,
            Headers: [],
            TraceId: new TraceId(traceId),
            DocumentUuid: documentUuid
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
                _mappingSet
            ),
            MappingSet: _mappingSet,
            EdfiDoc: requestBody,
            Headers: [],
            TraceId: new TraceId(traceId),
            DocumentUuid: StudentSchoolAssociationDocumentUuid
        );

        return await scope
            .ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>()
            .UpdateDocumentById(request);
    }

    private async Task<RelationalCurrentEtagPreconditionCheckResult?> CheckIfMatchAsync(string ifMatchValue)
    {
        await using var scope = _serviceProvider.CreateAsyncScope();
        SetSelectedInstance(scope.ServiceProvider);

        var resource = new QualifiedResourceName(
            _resourceInfo.ProjectName.Value,
            _resourceInfo.ResourceName.Value
        );
        // Resolve the target the way the production PUT path does — a root-table uuid probe — so the
        // harness feeds the precondition checker the same target context the repository would.
        var targetLookupResult = await scope
            .ServiceProvider.GetRequiredService<IRelationalWriteTargetLookupService>()
            .ResolveForPutByRootTableAsync(
                _mappingSet.GetWritePlanOrThrow(resource).Model.Root.Table,
                StudentSchoolAssociationDocumentUuid
            );

        targetLookupResult.Should().BeOfType<RelationalWriteTargetLookupResult.ExistingDocument>();

        var existingTarget = (RelationalWriteTargetLookupResult.ExistingDocument)targetLookupResult;
        await using var writeSession = await scope
            .ServiceProvider.GetRequiredService<IRelationalWriteSessionFactory>()
            .CreateAsync();

        try
        {
            return await scope
                .ServiceProvider.GetRequiredService<IRelationalCurrentEtagPreconditionChecker>()
                .CheckAsync(
                    new RelationalCurrentEtagPreconditionCheckRequest(
                        _mappingSet,
                        _mappingSet.GetReadPlanOrThrow(resource),
                        new RelationalWriteTargetContext.ExistingDocument(
                            existingTarget.DocumentId,
                            existingTarget.DocumentUuid,
                            existingTarget.ObservedContentVersion
                        ),
                        new WritePrecondition.IfMatch(ifMatchValue)
                    ),
                    writeSession
                );
        }
        finally
        {
            await writeSession.RollbackAsync();
        }
    }

    // Flips the linkFlag component ("l" <-> "n") of a composed etag so tests can present an etag
    // captured under the opposite link mode.
    private static string FlipLinkFlag(string etag)
    {
        string[] components = etag.Split(VariantKey.ComponentSeparator);
        if (components.Length != VariantKey.ComponentCount)
        {
            throw new InvalidOperationException($"Unexpected etag variant key in '{etag}'.");
        }

        components[^2] = components[^2] switch
        {
            "l" => "n",
            "n" => "l",
            _ => throw new InvalidOperationException($"Unexpected etag link flag in '{etag}'."),
        };

        return string.Join(VariantKey.ComponentSeparator, components);
    }

    private void SetSelectedInstance(IServiceProvider serviceProvider)
    {
        serviceProvider
            .GetRequiredService<IDataStoreSelection>()
            .SetSelectedDataStore(
                new DataStore(
                    Id: 1,
                    DataStoreType: "test",
                    Name: "PostgresqlRelationalWriteAuthoritativeSampleStudentSchoolAssociation",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );
    }

    private async Task<AuthoritativeSampleStudentSchoolAssociationSeedData> SeedReferenceDataAsync()
    {
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
            "CalendarTypeDescriptor",
            CalendarTypeDescriptorUri,
            "uri://ed-fi.org/CalendarTypeDescriptor",
            "Instructional",
            "Instructional"
        );
        var ninthGradeLevelDescriptorId = await SeedDescriptorAsync(
            Guid.Parse("11111111-0000-0000-0000-000000000002"),
            gradeLevelDescriptorResourceKeyId,
            "GradeLevelDescriptor",
            NinthGradeLevelDescriptorUri,
            "uri://ed-fi.org/GradeLevelDescriptor",
            "Ninth grade",
            "Ninth grade"
        );
        var tenthGradeLevelDescriptorId = await SeedDescriptorAsync(
            Guid.Parse("11111111-0000-0000-0000-000000000003"),
            gradeLevelDescriptorResourceKeyId,
            "GradeLevelDescriptor",
            TenthGradeLevelDescriptorUri,
            "uri://ed-fi.org/GradeLevelDescriptor",
            "Tenth grade",
            "Tenth grade"
        );
        var graduationPlanTypeDescriptorId = await SeedDescriptorAsync(
            Guid.Parse("11111111-0000-0000-0000-000000000004"),
            graduationPlanTypeDescriptorResourceKeyId,
            "GraduationPlanTypeDescriptor",
            GraduationPlanTypeDescriptorUri,
            "uri://ed-fi.org/GraduationPlanTypeDescriptor",
            "Foundation",
            "Foundation"
        );
        var pathwayEducationPlanDescriptorId = await SeedDescriptorAsync(
            Guid.Parse("11111111-0000-0000-0000-000000000005"),
            educationPlanDescriptorResourceKeyId,
            "EducationPlanDescriptor",
            PathwayEducationPlanDescriptorUri,
            "uri://ed-fi.org/EducationPlanDescriptor",
            "Pathway",
            "Pathway"
        );
        var interventionEducationPlanDescriptorId = await SeedDescriptorAsync(
            Guid.Parse("11111111-0000-0000-0000-000000000006"),
            educationPlanDescriptorResourceKeyId,
            "EducationPlanDescriptor",
            InterventionEducationPlanDescriptorUri,
            "uri://ed-fi.org/EducationPlanDescriptor",
            "Intervention",
            "Intervention"
        );
        var careerEducationPlanDescriptorId = await SeedDescriptorAsync(
            Guid.Parse("11111111-0000-0000-0000-000000000007"),
            educationPlanDescriptorResourceKeyId,
            "EducationPlanDescriptor",
            CareerEducationPlanDescriptorUri,
            "uri://ed-fi.org/EducationPlanDescriptor",
            "Career",
            "Career"
        );
        var residentMembershipTypeDescriptorId = await SeedDescriptorAsync(
            Guid.Parse("11111111-0000-0000-0000-000000000008"),
            membershipTypeDescriptorResourceKeyId,
            "MembershipTypeDescriptor",
            ResidentMembershipTypeDescriptorUri,
            "uri://sample.org/MembershipTypeDescriptor",
            "Resident",
            "Resident"
        );
        var transferMembershipTypeDescriptorId = await SeedDescriptorAsync(
            Guid.Parse("11111111-0000-0000-0000-000000000009"),
            membershipTypeDescriptorResourceKeyId,
            "MembershipTypeDescriptor",
            TransferMembershipTypeDescriptorUri,
            "uri://sample.org/MembershipTypeDescriptor",
            "Transfer",
            "Transfer"
        );

        // Each seeded resource root row is its own document: the root INSERT hands back the
        // DocumentId its DEFAULT drew from dms.DocumentIdSequence.
        var studentSchoolYearTypeDocumentId = await InsertSchoolYearTypeAsync(
            Guid.Parse("22222222-0000-0000-0000-000000000001"),
            SchoolYear,
            true
        );

        var foundationGraduationSchoolYearTypeDocumentId = await InsertSchoolYearTypeAsync(
            Guid.Parse("22222222-0000-0000-0000-000000000002"),
            FoundationGraduationSchoolYear,
            false
        );

        var endorsementGraduationSchoolYearTypeDocumentId = await InsertSchoolYearTypeAsync(
            Guid.Parse("22222222-0000-0000-0000-000000000003"),
            EndorsementGraduationSchoolYear,
            false
        );

        var stemGraduationSchoolYearTypeDocumentId = await InsertSchoolYearTypeAsync(
            Guid.Parse("22222222-0000-0000-0000-000000000004"),
            StemGraduationSchoolYear,
            false
        );

        var schoolDocumentId = await InsertSchoolAsync(
            Guid.Parse("33333333-0000-0000-0000-000000000001"),
            SchoolId,
            "Alpha Academy"
        );

        var conflictSchoolDocumentId = await InsertSchoolAsync(
            Guid.Parse("33333333-0000-0000-0000-000000000002"),
            ConflictSchoolId,
            "Beta Academy"
        );

        var studentDocumentId = await InsertStudentAsync(
            Guid.Parse("44444444-0000-0000-0000-000000000001"),
            StudentUniqueId,
            "Maya",
            "Lopez"
        );

        var calendarDocumentId = await InsertCalendarAsync(
            Guid.Parse("55555555-0000-0000-0000-000000000001"),
            studentSchoolYearTypeDocumentId,
            SchoolYear,
            schoolDocumentId,
            SchoolId,
            calendarTypeDescriptorId,
            CalendarCode
        );

        var conflictCalendarDocumentId = await InsertCalendarAsync(
            Guid.Parse("55555555-0000-0000-0000-000000000002"),
            studentSchoolYearTypeDocumentId,
            SchoolYear,
            conflictSchoolDocumentId,
            ConflictSchoolId,
            calendarTypeDescriptorId,
            ConflictCalendarCode
        );

        var foundationGraduationPlanDocumentId = await InsertGraduationPlanAsync(
            Guid.Parse("66666666-0000-0000-0000-000000000001"),
            schoolDocumentId,
            SchoolId,
            foundationGraduationSchoolYearTypeDocumentId,
            FoundationGraduationSchoolYear,
            graduationPlanTypeDescriptorId,
            26.000m
        );

        var endorsementGraduationPlanDocumentId = await InsertGraduationPlanAsync(
            Guid.Parse("66666666-0000-0000-0000-000000000002"),
            schoolDocumentId,
            SchoolId,
            endorsementGraduationSchoolYearTypeDocumentId,
            EndorsementGraduationSchoolYear,
            graduationPlanTypeDescriptorId,
            27.500m
        );

        var stemGraduationPlanDocumentId = await InsertGraduationPlanAsync(
            Guid.Parse("66666666-0000-0000-0000-000000000003"),
            schoolDocumentId,
            SchoolId,
            stemGraduationSchoolYearTypeDocumentId,
            StemGraduationSchoolYear,
            graduationPlanTypeDescriptorId,
            28.000m
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

    private async Task<long> SeedDescriptorAsync(
        Guid documentUuid,
        short resourceKeyId,
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
        // dms.Descriptor is the descriptor's document row and originates its own DocumentId.
        return await _database.ExecuteScalarAsync<long>(
            """
            INSERT INTO "dms"."Descriptor" (
                "DocumentUuid",
                "ResourceKeyId",
                "Namespace",
                "CodeValue",
                "ShortDescription",
                "Description",
                "Discriminator",
                "Uri"
            )
            VALUES (
                @documentUuid,
                @resourceKeyId,
                @namespace,
                @codeValue,
                @shortDescription,
                @description,
                @discriminator,
                @uri
            )
            RETURNING "DocumentId";
            """,
            new NpgsqlParameter("documentUuid", documentUuid),
            new NpgsqlParameter("resourceKeyId", resourceKeyId),
            new NpgsqlParameter("namespace", @namespace),
            new NpgsqlParameter("codeValue", codeValue),
            new NpgsqlParameter("shortDescription", shortDescription),
            new NpgsqlParameter("description", shortDescription),
            new NpgsqlParameter("discriminator", discriminator),
            new NpgsqlParameter("uri", uri)
        );
    }

    private async Task<long> InsertSchoolAsync(Guid documentUuid, long schoolId, string nameOfInstitution)
    {
        return await _database.ExecuteScalarAsync<long>(
            """
            INSERT INTO "edfi"."School" ("DocumentUuid", "NameOfInstitution", "SchoolId")
            VALUES (@documentUuid, @nameOfInstitution, @schoolId)
            RETURNING "DocumentId";
            """,
            new NpgsqlParameter("documentUuid", documentUuid),
            new NpgsqlParameter("nameOfInstitution", nameOfInstitution),
            new NpgsqlParameter("schoolId", schoolId)
        );
    }

    private async Task<long> InsertStudentAsync(
        Guid documentUuid,
        string studentUniqueId,
        string firstName,
        string lastSurname
    )
    {
        return await _database.ExecuteScalarAsync<long>(
            """
            INSERT INTO "edfi"."Student" ("DocumentUuid", "BirthDate", "FirstName", "LastSurname", "StudentUniqueId")
            VALUES (@documentUuid, @birthDate, @firstName, @lastSurname, @studentUniqueId)
            RETURNING "DocumentId";
            """,
            new NpgsqlParameter("documentUuid", documentUuid),
            new NpgsqlParameter("birthDate", new DateOnly(2010, 5, 14)),
            new NpgsqlParameter("firstName", firstName),
            new NpgsqlParameter("lastSurname", lastSurname),
            new NpgsqlParameter("studentUniqueId", studentUniqueId)
        );
    }

    private async Task<long> InsertSchoolYearTypeAsync(
        Guid documentUuid,
        int schoolYear,
        bool currentSchoolYear
    )
    {
        return await _database.ExecuteScalarAsync<long>(
            """
            INSERT INTO "edfi"."SchoolYearType" (
                "DocumentUuid",
                "CurrentSchoolYear",
                "SchoolYear",
                "SchoolYearDescription"
            )
            VALUES (
                @documentUuid,
                @currentSchoolYear,
                @schoolYear,
                @schoolYearDescription
            )
            RETURNING "DocumentId";
            """,
            new NpgsqlParameter("documentUuid", documentUuid),
            new NpgsqlParameter("currentSchoolYear", currentSchoolYear),
            new NpgsqlParameter("schoolYear", schoolYear),
            new NpgsqlParameter("schoolYearDescription", $"{schoolYear}-{schoolYear + 1}")
        );
    }

    private async Task<long> InsertCalendarAsync(
        Guid documentUuid,
        long schoolYearDocumentId,
        int schoolYear,
        long schoolDocumentId,
        long schoolId,
        long calendarTypeDescriptorId,
        string calendarCode
    )
    {
        return await _database.ExecuteScalarAsync<long>(
            """
            INSERT INTO "edfi"."Calendar" (
                "DocumentUuid",
                "SchoolYear_DocumentId",
                "SchoolYear_SchoolYear",
                "School_DocumentId",
                "School_SchoolId",
                "CalendarTypeDescriptor_DescriptorId",
                "CalendarCode"
            )
            VALUES (
                @documentUuid,
                @schoolYearDocumentId,
                @schoolYear,
                @schoolDocumentId,
                @schoolId,
                @calendarTypeDescriptorId,
                @calendarCode
            )
            RETURNING "DocumentId";
            """,
            new NpgsqlParameter("documentUuid", documentUuid),
            new NpgsqlParameter("schoolYearDocumentId", schoolYearDocumentId),
            new NpgsqlParameter("schoolYear", schoolYear),
            new NpgsqlParameter("schoolDocumentId", schoolDocumentId),
            new NpgsqlParameter("schoolId", schoolId),
            new NpgsqlParameter("calendarTypeDescriptorId", calendarTypeDescriptorId),
            new NpgsqlParameter("calendarCode", calendarCode)
        );
    }

    private async Task<long> InsertGraduationPlanAsync(
        Guid documentUuid,
        long educationOrganizationDocumentId,
        long educationOrganizationId,
        long graduationSchoolYearDocumentId,
        int graduationSchoolYear,
        long graduationPlanTypeDescriptorId,
        decimal totalRequiredCredits
    )
    {
        return await _database.ExecuteScalarAsync<long>(
            """
            INSERT INTO "edfi"."GraduationPlan" (
                "DocumentUuid",
                "EducationOrganization_DocumentId",
                "EducationOrganization_EducationOrganizationId",
                "GraduationSchoolYear_DocumentId",
                "GraduationSchoolYear_GraduationSchoolYear",
                "GraduationPlanTypeDescriptor_DescriptorId",
                "TotalRequiredCredits"
            )
            VALUES (
                @documentUuid,
                @educationOrganizationDocumentId,
                @educationOrganizationId,
                @graduationSchoolYearDocumentId,
                @graduationSchoolYear,
                @graduationPlanTypeDescriptorId,
                @totalRequiredCredits
            )
            RETURNING "DocumentId";
            """,
            new NpgsqlParameter("documentUuid", documentUuid),
            new NpgsqlParameter("educationOrganizationDocumentId", educationOrganizationDocumentId),
            new NpgsqlParameter("educationOrganizationId", educationOrganizationId),
            new NpgsqlParameter("graduationSchoolYearDocumentId", graduationSchoolYearDocumentId),
            new NpgsqlParameter("graduationSchoolYear", graduationSchoolYear),
            new NpgsqlParameter("graduationPlanTypeDescriptorId", graduationPlanTypeDescriptorId),
            new NpgsqlParameter("totalRequiredCredits", totalRequiredCredits)
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
            SELECT root."DocumentId", root."DocumentUuid", root."ContentVersion"
            FROM "edfi"."StudentSchoolAssociation" root
            WHERE root."DocumentUuid" = @documentUuid;
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
            AuthoritativeSampleStudentSchoolAssociationIntegrationTestSupport.FixtureRelativePath,
            strict: true
        );
        _mappingSet = _fixture.MappingSet;
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
                _mappingSet
            ),
            MappingSet: _mappingSet,
            EdfiDoc: requestBody,
            Headers: [],
            TraceId: new TraceId(traceId),
            DocumentUuid: documentUuid
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
                _mappingSet
            ),
            MappingSet: _mappingSet,
            EdfiDoc: requestBody,
            Headers: [],
            TraceId: new TraceId(traceId),
            DocumentUuid: StudentSchoolAssociationDocumentUuid
        );

        return await scope
            .ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>()
            .UpdateDocumentById(request);
    }

    private void SetSelectedInstance(IServiceProvider serviceProvider)
    {
        serviceProvider
            .GetRequiredService<IDataStoreSelection>()
            .SetSelectedDataStore(
                new DataStore(
                    Id: 1,
                    DataStoreType: "test",
                    Name: "PostgresqlRelationalWritePropagatedReferenceIdentityRuntime",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );
    }

    private async Task<PropagatedReferenceIdentityRuntimeSeedData> SeedReferenceDataAsync()
    {
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

        var calendarTypeDescriptorId = await SeedDescriptorAsync(
            Guid.Parse("77777777-0000-0000-0000-000000000001"),
            calendarTypeDescriptorResourceKeyId,
            "CalendarTypeDescriptor",
            CalendarTypeDescriptorUri,
            "uri://ed-fi.org/CalendarTypeDescriptor",
            "Instructional",
            "Instructional"
        );
        var ninthGradeLevelDescriptorId = await SeedDescriptorAsync(
            Guid.Parse("77777777-0000-0000-0000-000000000002"),
            gradeLevelDescriptorResourceKeyId,
            "GradeLevelDescriptor",
            NinthGradeLevelDescriptorUri,
            "uri://ed-fi.org/GradeLevelDescriptor",
            "Ninth grade",
            "Ninth grade"
        );
        var tenthGradeLevelDescriptorId = await SeedDescriptorAsync(
            Guid.Parse("77777777-0000-0000-0000-000000000003"),
            gradeLevelDescriptorResourceKeyId,
            "GradeLevelDescriptor",
            TenthGradeLevelDescriptorUri,
            "uri://ed-fi.org/GradeLevelDescriptor",
            "Tenth grade",
            "Tenth grade"
        );
        var graduationPlanTypeDescriptorId = await SeedDescriptorAsync(
            Guid.Parse("77777777-0000-0000-0000-000000000004"),
            graduationPlanTypeDescriptorResourceKeyId,
            "GraduationPlanTypeDescriptor",
            GraduationPlanTypeDescriptorUri,
            "uri://ed-fi.org/GraduationPlanTypeDescriptor",
            "Foundation",
            "Foundation"
        );
        var residentMembershipTypeDescriptorId = await SeedDescriptorAsync(
            Guid.Parse("77777777-0000-0000-0000-000000000005"),
            membershipTypeDescriptorResourceKeyId,
            "MembershipTypeDescriptor",
            ResidentMembershipTypeDescriptorUri,
            "uri://sample.org/MembershipTypeDescriptor",
            "Resident",
            "Resident"
        );
        var transferMembershipTypeDescriptorId = await SeedDescriptorAsync(
            Guid.Parse("77777777-0000-0000-0000-000000000006"),
            membershipTypeDescriptorResourceKeyId,
            "MembershipTypeDescriptor",
            TransferMembershipTypeDescriptorUri,
            "uri://sample.org/MembershipTypeDescriptor",
            "Transfer",
            "Transfer"
        );

        // Each seeded resource root row is its own document: the root INSERT hands back the
        // DocumentId its DEFAULT drew from dms.DocumentIdSequence.
        var studentSchoolYearTypeDocumentId = await InsertSchoolYearTypeAsync(
            Guid.Parse("88888888-0000-0000-0000-000000000001"),
            SchoolYear,
            true
        );

        var foundationGraduationSchoolYearTypeDocumentId = await InsertSchoolYearTypeAsync(
            Guid.Parse("88888888-0000-0000-0000-000000000002"),
            FoundationGraduationSchoolYear,
            false
        );

        var endorsementGraduationSchoolYearTypeDocumentId = await InsertSchoolYearTypeAsync(
            Guid.Parse("88888888-0000-0000-0000-000000000003"),
            EndorsementGraduationSchoolYear,
            false
        );

        var schoolDocumentId = await InsertSchoolAsync(
            Guid.Parse("99999999-0000-0000-0000-000000000001"),
            SchoolId,
            "Alpha Academy"
        );

        var studentDocumentId = await InsertStudentAsync(
            Guid.Parse("aaaaaaaa-1000-0000-0000-000000000001"),
            StudentUniqueId,
            "Maya",
            "Lopez"
        );

        var calendarDocumentId = await InsertCalendarAsync(
            Guid.Parse("aaaaaaaa-1000-0000-0000-000000000002"),
            studentSchoolYearTypeDocumentId,
            SchoolYear,
            schoolDocumentId,
            SchoolId,
            calendarTypeDescriptorId,
            CalendarCode
        );

        var alternateCalendarDocumentId = await InsertCalendarAsync(
            Guid.Parse("aaaaaaaa-1000-0000-0000-000000000005"),
            studentSchoolYearTypeDocumentId,
            SchoolYear,
            schoolDocumentId,
            SchoolId,
            calendarTypeDescriptorId,
            AlternateCalendarCode
        );

        var foundationGraduationPlanDocumentId = await InsertGraduationPlanAsync(
            Guid.Parse("aaaaaaaa-1000-0000-0000-000000000003"),
            schoolDocumentId,
            SchoolId,
            foundationGraduationSchoolYearTypeDocumentId,
            FoundationGraduationSchoolYear,
            graduationPlanTypeDescriptorId,
            26.000m
        );

        var endorsementGraduationPlanDocumentId = await InsertGraduationPlanAsync(
            Guid.Parse("aaaaaaaa-1000-0000-0000-000000000004"),
            schoolDocumentId,
            SchoolId,
            endorsementGraduationSchoolYearTypeDocumentId,
            EndorsementGraduationSchoolYear,
            graduationPlanTypeDescriptorId,
            27.500m
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

    private async Task<long> SeedDescriptorAsync(
        Guid documentUuid,
        short resourceKeyId,
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
        // dms.Descriptor is the descriptor's document row and originates its own DocumentId.
        return await _database.ExecuteScalarAsync<long>(
            """
            INSERT INTO "dms"."Descriptor" (
                "DocumentUuid",
                "ResourceKeyId",
                "Namespace",
                "CodeValue",
                "ShortDescription",
                "Description",
                "Discriminator",
                "Uri"
            )
            VALUES (
                @documentUuid,
                @resourceKeyId,
                @namespace,
                @codeValue,
                @shortDescription,
                @description,
                @discriminator,
                @uri
            )
            RETURNING "DocumentId";
            """,
            new NpgsqlParameter("documentUuid", documentUuid),
            new NpgsqlParameter("resourceKeyId", resourceKeyId),
            new NpgsqlParameter("namespace", @namespace),
            new NpgsqlParameter("codeValue", codeValue),
            new NpgsqlParameter("shortDescription", shortDescription),
            new NpgsqlParameter("description", shortDescription),
            new NpgsqlParameter("discriminator", discriminator),
            new NpgsqlParameter("uri", uri)
        );
    }

    private async Task<long> InsertSchoolAsync(Guid documentUuid, long schoolId, string nameOfInstitution)
    {
        return await _database.ExecuteScalarAsync<long>(
            """
            INSERT INTO "edfi"."School" ("DocumentUuid", "NameOfInstitution", "SchoolId")
            VALUES (@documentUuid, @nameOfInstitution, @schoolId)
            RETURNING "DocumentId";
            """,
            new NpgsqlParameter("documentUuid", documentUuid),
            new NpgsqlParameter("nameOfInstitution", nameOfInstitution),
            new NpgsqlParameter("schoolId", schoolId)
        );
    }

    private async Task<long> InsertStudentAsync(
        Guid documentUuid,
        string studentUniqueId,
        string firstName,
        string lastSurname
    )
    {
        return await _database.ExecuteScalarAsync<long>(
            """
            INSERT INTO "edfi"."Student" ("DocumentUuid", "BirthDate", "FirstName", "LastSurname", "StudentUniqueId")
            VALUES (@documentUuid, @birthDate, @firstName, @lastSurname, @studentUniqueId)
            RETURNING "DocumentId";
            """,
            new NpgsqlParameter("documentUuid", documentUuid),
            new NpgsqlParameter("birthDate", new DateOnly(2010, 5, 14)),
            new NpgsqlParameter("firstName", firstName),
            new NpgsqlParameter("lastSurname", lastSurname),
            new NpgsqlParameter("studentUniqueId", studentUniqueId)
        );
    }

    private async Task<long> InsertSchoolYearTypeAsync(
        Guid documentUuid,
        int schoolYear,
        bool currentSchoolYear
    )
    {
        return await _database.ExecuteScalarAsync<long>(
            """
            INSERT INTO "edfi"."SchoolYearType" (
                "DocumentUuid",
                "CurrentSchoolYear",
                "SchoolYear",
                "SchoolYearDescription"
            )
            VALUES (
                @documentUuid,
                @currentSchoolYear,
                @schoolYear,
                @schoolYearDescription
            )
            RETURNING "DocumentId";
            """,
            new NpgsqlParameter("documentUuid", documentUuid),
            new NpgsqlParameter("currentSchoolYear", currentSchoolYear),
            new NpgsqlParameter("schoolYear", schoolYear),
            new NpgsqlParameter("schoolYearDescription", $"{schoolYear}-{schoolYear + 1}")
        );
    }

    private async Task<long> InsertCalendarAsync(
        Guid documentUuid,
        long schoolYearDocumentId,
        int schoolYear,
        long schoolDocumentId,
        long schoolId,
        long calendarTypeDescriptorId,
        string calendarCode
    )
    {
        return await _database.ExecuteScalarAsync<long>(
            """
            INSERT INTO "edfi"."Calendar" (
                "DocumentUuid",
                "SchoolYear_DocumentId",
                "SchoolYear_SchoolYear",
                "School_DocumentId",
                "School_SchoolId",
                "CalendarTypeDescriptor_DescriptorId",
                "CalendarCode"
            )
            VALUES (
                @documentUuid,
                @schoolYearDocumentId,
                @schoolYear,
                @schoolDocumentId,
                @schoolId,
                @calendarTypeDescriptorId,
                @calendarCode
            )
            RETURNING "DocumentId";
            """,
            new NpgsqlParameter("documentUuid", documentUuid),
            new NpgsqlParameter("schoolYearDocumentId", schoolYearDocumentId),
            new NpgsqlParameter("schoolYear", schoolYear),
            new NpgsqlParameter("schoolDocumentId", schoolDocumentId),
            new NpgsqlParameter("schoolId", schoolId),
            new NpgsqlParameter("calendarTypeDescriptorId", calendarTypeDescriptorId),
            new NpgsqlParameter("calendarCode", calendarCode)
        );
    }

    private async Task<long> InsertGraduationPlanAsync(
        Guid documentUuid,
        long educationOrganizationDocumentId,
        long educationOrganizationId,
        long graduationSchoolYearDocumentId,
        int graduationSchoolYear,
        long graduationPlanTypeDescriptorId,
        decimal totalRequiredCredits
    )
    {
        return await _database.ExecuteScalarAsync<long>(
            """
            INSERT INTO "edfi"."GraduationPlan" (
                "DocumentUuid",
                "EducationOrganization_DocumentId",
                "EducationOrganization_EducationOrganizationId",
                "GraduationSchoolYear_DocumentId",
                "GraduationSchoolYear_GraduationSchoolYear",
                "GraduationPlanTypeDescriptor_DescriptorId",
                "TotalRequiredCredits"
            )
            VALUES (
                @documentUuid,
                @educationOrganizationDocumentId,
                @educationOrganizationId,
                @graduationSchoolYearDocumentId,
                @graduationSchoolYear,
                @graduationPlanTypeDescriptorId,
                @totalRequiredCredits
            )
            RETURNING "DocumentId";
            """,
            new NpgsqlParameter("documentUuid", documentUuid),
            new NpgsqlParameter("educationOrganizationDocumentId", educationOrganizationDocumentId),
            new NpgsqlParameter("educationOrganizationId", educationOrganizationId),
            new NpgsqlParameter("graduationSchoolYearDocumentId", graduationSchoolYearDocumentId),
            new NpgsqlParameter("graduationSchoolYear", graduationSchoolYear),
            new NpgsqlParameter("graduationPlanTypeDescriptorId", graduationPlanTypeDescriptorId),
            new NpgsqlParameter("totalRequiredCredits", totalRequiredCredits)
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
            SELECT root."DocumentId", root."DocumentUuid", root."ContentVersion"
            FROM "edfi"."StudentSchoolAssociation" root
            WHERE root."DocumentUuid" = @documentUuid;
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

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _fixture = PostgresqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            AuthoritativeSampleStudentSchoolAssociationIntegrationTestSupport.FixtureRelativePath,
            strict: true
        );
        _mappingSet = _fixture.MappingSet;
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
                _mappingSet
            ),
            MappingSet: _mappingSet,
            EdfiDoc: requestBody,
            Headers: [],
            TraceId: new TraceId(traceId),
            DocumentUuid: documentUuid
        );

        return await scope
            .ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>()
            .UpsertDocument(request);
    }

    private void SetSelectedInstance(IServiceProvider serviceProvider)
    {
        serviceProvider
            .GetRequiredService<IDataStoreSelection>()
            .SetSelectedDataStore(
                new DataStore(
                    Id: 1,
                    DataStoreType: "test",
                    Name: "PostgresqlRelationalWriteAuthoritativeSampleStudentSchoolAssociationConflict",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );
    }

    private async Task<AuthoritativeSampleStudentSchoolAssociationSeedData> SeedReferenceDataAsync()
    {
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
            "CalendarTypeDescriptor",
            CalendarTypeDescriptorUri,
            "uri://ed-fi.org/CalendarTypeDescriptor",
            "Instructional",
            "Instructional"
        );
        var ninthGradeLevelDescriptorId = await SeedDescriptorAsync(
            Guid.Parse("77777777-0000-0000-0000-000000000002"),
            gradeLevelDescriptorResourceKeyId,
            "GradeLevelDescriptor",
            NinthGradeLevelDescriptorUri,
            "uri://ed-fi.org/GradeLevelDescriptor",
            "Ninth grade",
            "Ninth grade"
        );
        var graduationPlanTypeDescriptorId = await SeedDescriptorAsync(
            Guid.Parse("77777777-0000-0000-0000-000000000003"),
            graduationPlanTypeDescriptorResourceKeyId,
            "GraduationPlanTypeDescriptor",
            GraduationPlanTypeDescriptorUri,
            "uri://ed-fi.org/GraduationPlanTypeDescriptor",
            "Foundation",
            "Foundation"
        );
        var pathwayEducationPlanDescriptorId = await SeedDescriptorAsync(
            Guid.Parse("77777777-0000-0000-0000-000000000004"),
            educationPlanDescriptorResourceKeyId,
            "EducationPlanDescriptor",
            PathwayEducationPlanDescriptorUri,
            "uri://ed-fi.org/EducationPlanDescriptor",
            "Pathway",
            "Pathway"
        );
        var interventionEducationPlanDescriptorId = await SeedDescriptorAsync(
            Guid.Parse("77777777-0000-0000-0000-000000000005"),
            educationPlanDescriptorResourceKeyId,
            "EducationPlanDescriptor",
            InterventionEducationPlanDescriptorUri,
            "uri://ed-fi.org/EducationPlanDescriptor",
            "Intervention",
            "Intervention"
        );
        var careerEducationPlanDescriptorId = await SeedDescriptorAsync(
            Guid.Parse("77777777-0000-0000-0000-000000000006"),
            educationPlanDescriptorResourceKeyId,
            "EducationPlanDescriptor",
            CareerEducationPlanDescriptorUri,
            "uri://ed-fi.org/EducationPlanDescriptor",
            "Career",
            "Career"
        );
        var residentMembershipTypeDescriptorId = await SeedDescriptorAsync(
            Guid.Parse("77777777-0000-0000-0000-000000000007"),
            membershipTypeDescriptorResourceKeyId,
            "MembershipTypeDescriptor",
            ResidentMembershipTypeDescriptorUri,
            "uri://sample.org/MembershipTypeDescriptor",
            "Resident",
            "Resident"
        );
        var transferMembershipTypeDescriptorId = await SeedDescriptorAsync(
            Guid.Parse("77777777-0000-0000-0000-000000000008"),
            membershipTypeDescriptorResourceKeyId,
            "MembershipTypeDescriptor",
            TransferMembershipTypeDescriptorUri,
            "uri://sample.org/MembershipTypeDescriptor",
            "Transfer",
            "Transfer"
        );

        // Each seeded resource root row is its own document: the root INSERT hands back the
        // DocumentId its DEFAULT drew from dms.DocumentIdSequence.
        var studentSchoolYearTypeDocumentId = await InsertSchoolYearTypeAsync(
            Guid.Parse("88888888-0000-0000-0000-000000000001"),
            SchoolYear,
            true
        );

        var foundationGraduationSchoolYearTypeDocumentId = await InsertSchoolYearTypeAsync(
            Guid.Parse("88888888-0000-0000-0000-000000000002"),
            FoundationGraduationSchoolYear,
            false
        );

        var endorsementGraduationSchoolYearTypeDocumentId = await InsertSchoolYearTypeAsync(
            Guid.Parse("88888888-0000-0000-0000-000000000003"),
            EndorsementGraduationSchoolYear,
            false
        );

        var stemGraduationSchoolYearTypeDocumentId = await InsertSchoolYearTypeAsync(
            Guid.Parse("88888888-0000-0000-0000-000000000004"),
            StemGraduationSchoolYear,
            false
        );

        var schoolDocumentId = await InsertSchoolAsync(
            Guid.Parse("99999999-0000-0000-0000-000000000001"),
            SchoolId,
            "Alpha Academy"
        );

        var conflictSchoolDocumentId = await InsertSchoolAsync(
            Guid.Parse("99999999-0000-0000-0000-000000000002"),
            ConflictSchoolId,
            "Beta Academy"
        );

        var studentDocumentId = await InsertStudentAsync(
            Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"),
            StudentUniqueId,
            "Maya",
            "Lopez"
        );

        var conflictCalendarDocumentId = await InsertCalendarAsync(
            Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001"),
            studentSchoolYearTypeDocumentId,
            SchoolYear,
            conflictSchoolDocumentId,
            ConflictSchoolId,
            calendarTypeDescriptorId,
            ConflictCalendarCode
        );

        var foundationGraduationPlanDocumentId = await InsertGraduationPlanAsync(
            Guid.Parse("cccccccc-0000-0000-0000-000000000001"),
            schoolDocumentId,
            SchoolId,
            foundationGraduationSchoolYearTypeDocumentId,
            FoundationGraduationSchoolYear,
            graduationPlanTypeDescriptorId,
            26.000m
        );

        var endorsementGraduationPlanDocumentId = await InsertGraduationPlanAsync(
            Guid.Parse("cccccccc-0000-0000-0000-000000000002"),
            schoolDocumentId,
            SchoolId,
            endorsementGraduationSchoolYearTypeDocumentId,
            EndorsementGraduationSchoolYear,
            graduationPlanTypeDescriptorId,
            27.500m
        );

        var stemGraduationPlanDocumentId = await InsertGraduationPlanAsync(
            Guid.Parse("cccccccc-0000-0000-0000-000000000003"),
            schoolDocumentId,
            SchoolId,
            stemGraduationSchoolYearTypeDocumentId,
            StemGraduationSchoolYear,
            graduationPlanTypeDescriptorId,
            28.000m
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

    private async Task<long> SeedDescriptorAsync(
        Guid documentUuid,
        short resourceKeyId,
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
        // dms.Descriptor is the descriptor's document row and originates its own DocumentId.
        return await _database.ExecuteScalarAsync<long>(
            """
            INSERT INTO "dms"."Descriptor" (
                "DocumentUuid",
                "ResourceKeyId",
                "Namespace",
                "CodeValue",
                "ShortDescription",
                "Description",
                "Discriminator",
                "Uri"
            )
            VALUES (
                @documentUuid,
                @resourceKeyId,
                @namespace,
                @codeValue,
                @shortDescription,
                @description,
                @discriminator,
                @uri
            )
            RETURNING "DocumentId";
            """,
            new NpgsqlParameter("documentUuid", documentUuid),
            new NpgsqlParameter("resourceKeyId", resourceKeyId),
            new NpgsqlParameter("namespace", @namespace),
            new NpgsqlParameter("codeValue", codeValue),
            new NpgsqlParameter("shortDescription", shortDescription),
            new NpgsqlParameter("description", shortDescription),
            new NpgsqlParameter("discriminator", discriminator),
            new NpgsqlParameter("uri", uri)
        );
    }

    private async Task<long> InsertSchoolAsync(Guid documentUuid, long schoolId, string nameOfInstitution)
    {
        return await _database.ExecuteScalarAsync<long>(
            """
            INSERT INTO "edfi"."School" ("DocumentUuid", "NameOfInstitution", "SchoolId")
            VALUES (@documentUuid, @nameOfInstitution, @schoolId)
            RETURNING "DocumentId";
            """,
            new NpgsqlParameter("documentUuid", documentUuid),
            new NpgsqlParameter("nameOfInstitution", nameOfInstitution),
            new NpgsqlParameter("schoolId", schoolId)
        );
    }

    private async Task<long> InsertStudentAsync(
        Guid documentUuid,
        string studentUniqueId,
        string firstName,
        string lastSurname
    )
    {
        return await _database.ExecuteScalarAsync<long>(
            """
            INSERT INTO "edfi"."Student" ("DocumentUuid", "BirthDate", "FirstName", "LastSurname", "StudentUniqueId")
            VALUES (@documentUuid, @birthDate, @firstName, @lastSurname, @studentUniqueId)
            RETURNING "DocumentId";
            """,
            new NpgsqlParameter("documentUuid", documentUuid),
            new NpgsqlParameter("birthDate", new DateOnly(2010, 5, 14)),
            new NpgsqlParameter("firstName", firstName),
            new NpgsqlParameter("lastSurname", lastSurname),
            new NpgsqlParameter("studentUniqueId", studentUniqueId)
        );
    }

    private async Task<long> InsertSchoolYearTypeAsync(
        Guid documentUuid,
        int schoolYear,
        bool currentSchoolYear
    )
    {
        return await _database.ExecuteScalarAsync<long>(
            """
            INSERT INTO "edfi"."SchoolYearType" (
                "DocumentUuid",
                "CurrentSchoolYear",
                "SchoolYear",
                "SchoolYearDescription"
            )
            VALUES (
                @documentUuid,
                @currentSchoolYear,
                @schoolYear,
                @schoolYearDescription
            )
            RETURNING "DocumentId";
            """,
            new NpgsqlParameter("documentUuid", documentUuid),
            new NpgsqlParameter("currentSchoolYear", currentSchoolYear),
            new NpgsqlParameter("schoolYear", schoolYear),
            new NpgsqlParameter("schoolYearDescription", $"{schoolYear}-{schoolYear + 1}")
        );
    }

    private async Task<long> InsertCalendarAsync(
        Guid documentUuid,
        long schoolYearDocumentId,
        int schoolYear,
        long schoolDocumentId,
        long schoolId,
        long calendarTypeDescriptorId,
        string calendarCode
    )
    {
        return await _database.ExecuteScalarAsync<long>(
            """
            INSERT INTO "edfi"."Calendar" (
                "DocumentUuid",
                "SchoolYear_DocumentId",
                "SchoolYear_SchoolYear",
                "School_DocumentId",
                "School_SchoolId",
                "CalendarTypeDescriptor_DescriptorId",
                "CalendarCode"
            )
            VALUES (
                @documentUuid,
                @schoolYearDocumentId,
                @schoolYear,
                @schoolDocumentId,
                @schoolId,
                @calendarTypeDescriptorId,
                @calendarCode
            )
            RETURNING "DocumentId";
            """,
            new NpgsqlParameter("documentUuid", documentUuid),
            new NpgsqlParameter("schoolYearDocumentId", schoolYearDocumentId),
            new NpgsqlParameter("schoolYear", schoolYear),
            new NpgsqlParameter("schoolDocumentId", schoolDocumentId),
            new NpgsqlParameter("schoolId", schoolId),
            new NpgsqlParameter("calendarTypeDescriptorId", calendarTypeDescriptorId),
            new NpgsqlParameter("calendarCode", calendarCode)
        );
    }

    private async Task<long> InsertGraduationPlanAsync(
        Guid documentUuid,
        long educationOrganizationDocumentId,
        long educationOrganizationId,
        long graduationSchoolYearDocumentId,
        int graduationSchoolYear,
        long graduationPlanTypeDescriptorId,
        decimal totalRequiredCredits
    )
    {
        return await _database.ExecuteScalarAsync<long>(
            """
            INSERT INTO "edfi"."GraduationPlan" (
                "DocumentUuid",
                "EducationOrganization_DocumentId",
                "EducationOrganization_EducationOrganizationId",
                "GraduationSchoolYear_DocumentId",
                "GraduationSchoolYear_GraduationSchoolYear",
                "GraduationPlanTypeDescriptor_DescriptorId",
                "TotalRequiredCredits"
            )
            VALUES (
                @documentUuid,
                @educationOrganizationDocumentId,
                @educationOrganizationId,
                @graduationSchoolYearDocumentId,
                @graduationSchoolYear,
                @graduationPlanTypeDescriptorId,
                @totalRequiredCredits
            )
            RETURNING "DocumentId";
            """,
            new NpgsqlParameter("documentUuid", documentUuid),
            new NpgsqlParameter("educationOrganizationDocumentId", educationOrganizationDocumentId),
            new NpgsqlParameter("educationOrganizationId", educationOrganizationId),
            new NpgsqlParameter("graduationSchoolYearDocumentId", graduationSchoolYearDocumentId),
            new NpgsqlParameter("graduationSchoolYear", graduationSchoolYear),
            new NpgsqlParameter("graduationPlanTypeDescriptorId", graduationPlanTypeDescriptorId),
            new NpgsqlParameter("totalRequiredCredits", totalRequiredCredits)
        );
    }

    private async Task<AuthoritativeSampleStudentSchoolAssociationRejectedWriteSnapshot> ReadRejectedWriteSnapshotAsync()
    {
        return new(
            // The StudentSchoolAssociation root row is the document the rejected write would have
            // created, so its DocumentUuid column is where a partial write would show up.
            DocumentUuids: await ReadGuidListAsync(
                """
                SELECT "DocumentUuid"
                FROM "edfi"."StudentSchoolAssociation"
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

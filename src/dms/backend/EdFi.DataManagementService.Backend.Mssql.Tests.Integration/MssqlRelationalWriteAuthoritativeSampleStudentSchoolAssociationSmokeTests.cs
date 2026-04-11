// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Mssql;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.Backend;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Extraction;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

file sealed class MssqlStudentSchoolAssociationAllowAllResourceAuthorizationHandler
    : IResourceAuthorizationHandler
{
    public Task<ResourceAuthorizationResult> Authorize(
        DocumentSecurityElements documentSecurityElements,
        OperationType operationType,
        TraceId traceId
    ) => Task.FromResult<ResourceAuthorizationResult>(new ResourceAuthorizationResult.Authorized());
}

file sealed class MssqlStudentSchoolAssociationNoOpUpdateCascadeHandler : IUpdateCascadeHandler
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

file sealed record MssqlStudentSchoolAssociationRelationalGetRequest(
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

file static class MssqlStudentSchoolAssociationIntegrationTestSupport
{
    public const string FixtureRelativePath = "src/dms/backend/Fixtures/authoritative/sample";

    public static ServiceProvider CreateServiceProvider()
    {
        ServiceCollection services = [];

        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddScoped<IDmsInstanceSelection, DmsInstanceSelection>();
        services.Configure<DatabaseOptions>(options => options.IsolationLevel = IsolationLevel.ReadCommitted);
        services.AddScoped<RelationalDocumentStoreRepository>();
        services.AddMssqlReferenceResolver();

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
        MappingSet mappingSet,
        long graduationPlanTypeDescriptorId
    )
    {
        var (alternativeGraduationPlanReferences, alternativeGraduationPlanReferenceArrays) =
            CreateAlternativeGraduationPlanDocumentReferences(requestBody, graduationPlanTypeDescriptorId);
        var documentInfo = RelationalDocumentInfoTestHelper.CreateDocumentInfo(
            requestBody,
            resourceInfo,
            resourceSchema,
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

    public static long GetInt64(IReadOnlyDictionary<string, object?> row, string columnName) =>
        Convert.ToInt64(GetRequiredValue(row, columnName), CultureInfo.InvariantCulture);

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

    public static ReferentialId CreateReferentialId(
        (string ProjectName, string ResourceName, bool IsDescriptor) targetResource,
        params (string IdentityJsonPath, string IdentityValue)[] identityElements
    ) =>
        ReferentialIdCalculator.ReferentialIdFrom(
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

    public static ReferentialId CreateDescriptorReferentialId(
        string projectName,
        string resourceName,
        string descriptorUri
    ) =>
        CreateReferentialId(
            (projectName, resourceName, true),
            (DocumentIdentity.DescriptorIdentityJsonPath.Value, descriptorUri.ToLowerInvariant())
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

internal sealed record MssqlStudentSchoolAssociationSeedData(long GraduationPlanTypeDescriptorId);

internal sealed record MssqlStudentSchoolAssociationDocumentMetadata(
    Guid DocumentUuid,
    long ContentVersion,
    DateTimeOffset ContentLastModifiedAt
);

[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[NonParallelizable]
public class Given_A_Mssql_Relational_Write_Then_Read_Smoke_With_The_Authoritative_Sample_StudentSchoolAssociation_Fixture
{
    private const long SchoolId = 100;
    private const int SchoolYear = 2024;
    private const int FoundationGraduationSchoolYear = 2026;
    private const int EndorsementGraduationSchoolYear = 2027;
    private const string StudentUniqueId = "10001";
    private const string CalendarCode = "MAIN";
    private const string NinthGradeLevelDescriptorUri = "uri://ed-fi.org/GradeLevelDescriptor#Ninth grade";
    private const string CalendarTypeDescriptorUri = "uri://ed-fi.org/CalendarTypeDescriptor#Instructional";
    private const string GraduationPlanTypeDescriptorUri =
        "uri://ed-fi.org/GraduationPlanTypeDescriptor#Foundation";
    private const string PathwayEducationPlanDescriptorUri =
        "uri://ed-fi.org/EducationPlanDescriptor#Pathway";
    private const string InterventionEducationPlanDescriptorUri =
        "uri://ed-fi.org/EducationPlanDescriptor#Intervention";
    private const string ResidentMembershipTypeDescriptorUri =
        "uri://sample.org/MembershipTypeDescriptor#Resident";

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

    private static readonly DocumentUuid StudentSchoolAssociationDocumentUuid = new(
        Guid.Parse("abababab-0000-0000-0000-000000000001")
    );

    private MssqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private MssqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;
    private ResourceInfo _resourceInfo = null!;
    private ResourceSchema _resourceSchema = null!;
    private MssqlStudentSchoolAssociationSeedData _seedData = null!;
    private UpsertResult _createResult = null!;
    private MssqlStudentSchoolAssociationDocumentMetadata _documentMetadata = null!;

    [SetUp]
    public async Task Setup()
    {
        if (!MssqlTestDatabaseHelper.IsConfigured())
        {
            Assert.Ignore(
                "SQL Server integration tests require a MssqlAdmin connection string in appsettings.Test.json"
            );
        }

        _fixture = MssqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            MssqlStudentSchoolAssociationIntegrationTestSupport.FixtureRelativePath
        );
        _mappingSet = new MappingSetCompiler().Compile(_fixture.ModelSet);
        _database = await MssqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
        _serviceProvider = MssqlStudentSchoolAssociationIntegrationTestSupport.CreateServiceProvider();

        var (projectSchema, resourceSchema) =
            MssqlStudentSchoolAssociationIntegrationTestSupport.GetResourceSchema(
                _fixture.EffectiveSchemaSet,
                "ed-fi",
                "StudentSchoolAssociation"
            );

        _resourceInfo = MssqlStudentSchoolAssociationIntegrationTestSupport.CreateResourceInfo(
            projectSchema,
            resourceSchema
        );
        _resourceSchema = resourceSchema;
        _seedData = await SeedReferenceDataAsync();
        await DisableStudentSchoolAssociationReferentialIdentityTriggerAsync();

        var requestBody =
            JsonNode.Parse(CreateRequestBodyJson)
            ?? throw new InvalidOperationException("Expected create request JSON to parse.");
        var documentInfo = MssqlStudentSchoolAssociationIntegrationTestSupport.CreateDocumentInfo(
            requestBody,
            _resourceInfo,
            _resourceSchema,
            _mappingSet,
            _seedData.GraduationPlanTypeDescriptorId
        );

        _createResult = await ExecuteCreateAsync(
            requestBody,
            documentInfo,
            StudentSchoolAssociationDocumentUuid,
            "mssql-authoritative-sample-student-school-association-create"
        );

        if (_createResult is UpsertResult.UpsertFailureReference createReferenceFailure)
        {
            Assert.Fail(
                $"Create reference failure: {MssqlStudentSchoolAssociationIntegrationTestSupport.FormatReferenceFailure(createReferenceFailure)}"
            );
        }

        _createResult.Should().BeOfType<UpsertResult.InsertSuccess>();
        _documentMetadata = await ReadDocumentMetadataAsync(StudentSchoolAssociationDocumentUuid.Value);
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
    public async Task It_reads_back_the_written_document_via_relational_get_by_id_with_semantic_json_equivalence_and_metadata()
    {
        var getResult = await ExecuteGetByIdAsync(
            StudentSchoolAssociationDocumentUuid,
            "mssql-authoritative-sample-student-school-association-get-by-id"
        );
        var expectedDocument = CreateExpectedExternalResponse(CreateRequestBodyJson, _documentMetadata);

        getResult.Should().BeOfType<GetResult.GetSuccess>();

        var success = getResult.As<GetResult.GetSuccess>();

        success.DocumentUuid.Should().Be(StudentSchoolAssociationDocumentUuid);
        success.LastModifiedTraceId.Should().BeNull();
        success.LastModifiedDate.Should().Be(_documentMetadata.ContentLastModifiedAt.UtcDateTime);
        success.EdfiDoc["id"]!
            .GetValue<string>()
            .Should()
            .Be(StudentSchoolAssociationDocumentUuid.Value.ToString());
        success.EdfiDoc["_etag"]!.GetValue<string>().Should().Be($"\"{_documentMetadata.ContentVersion}\"");
        success.EdfiDoc["_lastModifiedDate"]!
            .GetValue<string>()
            .Should()
            .Be(FormatExternalLastModifiedDate(_documentMetadata.ContentLastModifiedAt));
        success.EdfiDoc["alternativeGraduationPlans"]!
            .AsArray()
            .Select(plan =>
                plan?["alternativeGraduationPlanReference"]?["graduationSchoolYear"]?.GetValue<int>()
            )
            .Should()
            .Equal(FoundationGraduationSchoolYear, EndorsementGraduationSchoolYear);
        success.EdfiDoc["educationPlans"]!
            .AsArray()
            .Select(plan => plan?["educationPlanDescriptor"]?.GetValue<string>())
            .Should()
            .Equal(PathwayEducationPlanDescriptorUri, InterventionEducationPlanDescriptorUri);
        CanonicalizeJson(success.EdfiDoc).Should().Be(CanonicalizeJson(expectedDocument));
    }

    private async Task<UpsertResult> ExecuteCreateAsync(
        JsonNode requestBody,
        DocumentInfo documentInfo,
        DocumentUuid documentUuid,
        string traceId
    )
    {
        await using var scope = _serviceProvider.CreateAsyncScope();
        SetSelectedInstance(scope.ServiceProvider);

        var request = new UpsertRequest(
            ResourceInfo: _resourceInfo,
            DocumentInfo: documentInfo,
            MappingSet: _mappingSet,
            EdfiDoc: requestBody,
            Headers: [],
            TraceId: new TraceId(traceId),
            DocumentUuid: documentUuid,
            DocumentSecurityElements: new([], [], [], [], []),
            UpdateCascadeHandler: new MssqlStudentSchoolAssociationNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new MssqlStudentSchoolAssociationAllowAllResourceAuthorizationHandler(),
            ResourceAuthorizationPathways: []
        );

        return await scope
            .ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>()
            .UpsertDocument(request);
    }

    private async Task<GetResult> ExecuteGetByIdAsync(DocumentUuid documentUuid, string traceId)
    {
        await using var scope = _serviceProvider.CreateAsyncScope();
        SetSelectedInstance(scope.ServiceProvider);

        var request = new MssqlStudentSchoolAssociationRelationalGetRequest(
            DocumentUuid: documentUuid,
            ResourceInfo: _resourceInfo,
            MappingSet: _mappingSet,
            ResourceAuthorizationHandler: new MssqlStudentSchoolAssociationAllowAllResourceAuthorizationHandler(),
            TraceId: new TraceId(traceId)
        );

        return await scope
            .ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>()
            .GetDocumentById(request);
    }

    private void SetSelectedInstance(IServiceProvider serviceProvider)
    {
        serviceProvider
            .GetRequiredService<IDmsInstanceSelection>()
            .SetSelectedDmsInstance(
                new DmsInstance(
                    Id: 1,
                    InstanceType: "test",
                    InstanceName: "MssqlRelationalWriteAuthoritativeSampleStudentSchoolAssociation",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );
    }

    private async Task<MssqlStudentSchoolAssociationSeedData> SeedReferenceDataAsync()
    {
        var schoolResourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "School");
        var educationOrganizationResourceKeyId = await GetResourceKeyIdAsync(
            "Ed-Fi",
            "EducationOrganization"
        );
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
        await SeedDescriptorAsync(
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
        await SeedDescriptorAsync(
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
        await SeedDescriptorAsync(
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
        await SeedDescriptorAsync(
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

        var studentSchoolYearTypeDocumentId = await InsertDocumentAsync(
            Guid.Parse("88888888-0000-0000-0000-000000000001"),
            schoolYearTypeResourceKeyId
        );
        await InsertSchoolYearTypeAsync(studentSchoolYearTypeDocumentId, SchoolYear, true);
        await UpsertReferentialIdentityAsync(
            MssqlStudentSchoolAssociationIntegrationTestSupport.CreateReferentialId(
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
        await UpsertReferentialIdentityAsync(
            MssqlStudentSchoolAssociationIntegrationTestSupport.CreateReferentialId(
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
        await UpsertReferentialIdentityAsync(
            MssqlStudentSchoolAssociationIntegrationTestSupport.CreateReferentialId(
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
        await ExecuteWithTriggersTemporarilyDisabledAsync(
            "edfi",
            "School",
            async () =>
            {
                await InsertSchoolAsync(schoolDocumentId, (int)SchoolId, "Alpha Academy");
                await InsertEducationOrganizationIdentityAsync(
                    schoolDocumentId,
                    (int)SchoolId,
                    "Ed-Fi:School"
                );
            }
        );
        await UpsertReferentialIdentityAsync(
            MssqlStudentSchoolAssociationIntegrationTestSupport.CreateReferentialId(
                ("Ed-Fi", "School", false),
                ("$.schoolId", SchoolId.ToString(CultureInfo.InvariantCulture))
            ),
            schoolDocumentId,
            schoolResourceKeyId
        );
        await UpsertReferentialIdentityAsync(
            MssqlStudentSchoolAssociationIntegrationTestSupport.CreateReferentialId(
                ("Ed-Fi", "EducationOrganization", false),
                ("$.educationOrganizationId", SchoolId.ToString(CultureInfo.InvariantCulture))
            ),
            schoolDocumentId,
            educationOrganizationResourceKeyId
        );

        var studentDocumentId = await InsertDocumentAsync(
            Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"),
            studentResourceKeyId
        );
        await InsertStudentAsync(studentDocumentId, StudentUniqueId, "Maya", "Lopez");
        await UpsertReferentialIdentityAsync(
            MssqlStudentSchoolAssociationIntegrationTestSupport.CreateReferentialId(
                ("Ed-Fi", "Student", false),
                ("$.studentUniqueId", StudentUniqueId)
            ),
            studentDocumentId,
            studentResourceKeyId
        );

        var calendarDocumentId = await InsertDocumentAsync(
            Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001"),
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
        await UpsertReferentialIdentityAsync(
            MssqlStudentSchoolAssociationIntegrationTestSupport.CreateReferentialId(
                ("Ed-Fi", "Calendar", false),
                ("$.calendarCode", CalendarCode),
                ("$.schoolReference.schoolId", SchoolId.ToString(CultureInfo.InvariantCulture)),
                ("$.schoolYearTypeReference.schoolYear", SchoolYear.ToString(CultureInfo.InvariantCulture))
            ),
            calendarDocumentId,
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
        await UpsertReferentialIdentityAsync(
            MssqlStudentSchoolAssociationIntegrationTestSupport.CreateReferentialId(
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
        await UpsertReferentialIdentityAsync(
            MssqlStudentSchoolAssociationIntegrationTestSupport.CreateReferentialId(
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

        return new(graduationPlanTypeDescriptorId);
    }

    private async Task DisableStudentSchoolAssociationReferentialIdentityTriggerAsync()
    {
        await _database.ExecuteNonQueryAsync(
            """
            DISABLE TRIGGER [TR_StudentSchoolAssociation_ReferentialIdentity]
            ON [edfi].[StudentSchoolAssociation];
            """
        );
    }

    private async Task<MssqlStudentSchoolAssociationDocumentMetadata> ReadDocumentMetadataAsync(
        Guid documentUuid
    )
    {
        var row = (
            await _database.QueryRowsAsync(
                """
                SELECT [DocumentUuid], [ContentVersion], [ContentLastModifiedAt]
                FROM [dms].[Document]
                WHERE [DocumentUuid] = @documentUuid;
                """,
                new SqlParameter("@documentUuid", documentUuid)
            )
        ).Single();

        return new(
            documentUuid,
            MssqlStudentSchoolAssociationIntegrationTestSupport.GetInt64(row, "ContentVersion"),
            MssqlStudentSchoolAssociationIntegrationTestSupport.GetDateTimeOffset(
                row,
                "ContentLastModifiedAt"
            )
        );
    }

    private async Task<short> GetResourceKeyIdAsync(string projectName, string resourceName)
    {
        return await _database.ExecuteScalarAsync<short>(
            """
            SELECT [ResourceKeyId]
            FROM [dms].[ResourceKey]
            WHERE [ProjectName] = @projectName
              AND [ResourceName] = @resourceName;
            """,
            new SqlParameter("@projectName", projectName),
            new SqlParameter("@resourceName", resourceName)
        );
    }

    private async Task<long> InsertDocumentAsync(Guid documentUuid, short resourceKeyId)
    {
        return await _database.ExecuteScalarAsync<long>(
            """
            DECLARE @Inserted TABLE ([DocumentId] bigint);
            INSERT INTO [dms].[Document] ([DocumentUuid], [ResourceKeyId])
            OUTPUT INSERTED.[DocumentId] INTO @Inserted ([DocumentId])
            VALUES (@documentUuid, @resourceKeyId);
            SELECT TOP (1) [DocumentId] FROM @Inserted;
            """,
            new SqlParameter("@documentUuid", documentUuid),
            new SqlParameter("@resourceKeyId", resourceKeyId)
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

        await UpsertReferentialIdentityAsync(
            MssqlStudentSchoolAssociationIntegrationTestSupport.CreateDescriptorReferentialId(
                projectName,
                resourceName,
                uri
            ),
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
            INSERT INTO [dms].[Descriptor] (
                [DocumentId],
                [Namespace],
                [CodeValue],
                [ShortDescription],
                [Description],
                [Discriminator],
                [Uri]
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
            new SqlParameter("@documentId", documentId),
            new SqlParameter("@namespace", @namespace),
            new SqlParameter("@codeValue", codeValue),
            new SqlParameter("@shortDescription", shortDescription),
            new SqlParameter("@description", shortDescription),
            new SqlParameter("@discriminator", discriminator),
            new SqlParameter("@uri", uri)
        );

        return documentId;
    }

    private async Task InsertSchoolYearTypeAsync(long documentId, int schoolYear, bool currentSchoolYear)
    {
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO [edfi].[SchoolYearType] (
                [DocumentId],
                [CurrentSchoolYear],
                [SchoolYear],
                [SchoolYearDescription]
            )
            VALUES (
                @documentId,
                @currentSchoolYear,
                @schoolYear,
                @schoolYearDescription
            );
            """,
            new SqlParameter("@documentId", documentId),
            new SqlParameter("@currentSchoolYear", currentSchoolYear),
            new SqlParameter("@schoolYear", schoolYear),
            new SqlParameter("@schoolYearDescription", $"{schoolYear}-{schoolYear + 1}")
        );
    }

    private async Task InsertSchoolAsync(long documentId, int schoolId, string nameOfInstitution)
    {
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO [edfi].[School] ([DocumentId], [NameOfInstitution], [SchoolId])
            VALUES (@documentId, @nameOfInstitution, @schoolId);
            """,
            new SqlParameter("@documentId", documentId),
            new SqlParameter("@nameOfInstitution", nameOfInstitution),
            new SqlParameter("@schoolId", schoolId)
        );
    }

    private async Task InsertEducationOrganizationIdentityAsync(
        long documentId,
        int educationOrganizationId,
        string discriminator
    )
    {
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO [edfi].[EducationOrganizationIdentity] (
                [DocumentId],
                [EducationOrganizationId],
                [Discriminator]
            )
            VALUES (@documentId, @educationOrganizationId, @discriminator);
            """,
            new SqlParameter("@documentId", documentId),
            new SqlParameter("@educationOrganizationId", educationOrganizationId),
            new SqlParameter("@discriminator", discriminator)
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
            INSERT INTO [edfi].[Student] ([DocumentId], [BirthDate], [FirstName], [LastSurname], [StudentUniqueId])
            VALUES (@documentId, @birthDate, @firstName, @lastSurname, @studentUniqueId);
            """,
            new SqlParameter("@documentId", documentId),
            new SqlParameter("@birthDate", new DateOnly(2010, 5, 14)),
            new SqlParameter("@firstName", firstName),
            new SqlParameter("@lastSurname", lastSurname),
            new SqlParameter("@studentUniqueId", studentUniqueId)
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
            INSERT INTO [edfi].[Calendar] (
                [DocumentId],
                [SchoolYear_DocumentId],
                [SchoolYear_SchoolYear],
                [School_DocumentId],
                [School_SchoolId],
                [CalendarTypeDescriptor_DescriptorId],
                [CalendarCode]
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
            new SqlParameter("@documentId", documentId),
            new SqlParameter("@schoolYearDocumentId", schoolYearDocumentId),
            new SqlParameter("@schoolYear", schoolYear),
            new SqlParameter("@schoolDocumentId", schoolDocumentId),
            new SqlParameter("@schoolId", schoolId),
            new SqlParameter("@calendarTypeDescriptorId", calendarTypeDescriptorId),
            new SqlParameter("@calendarCode", calendarCode)
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
            INSERT INTO [edfi].[GraduationPlan] (
                [DocumentId],
                [EducationOrganization_DocumentId],
                [EducationOrganization_EducationOrganizationId],
                [GraduationSchoolYear_DocumentId],
                [GraduationSchoolYear_GraduationSchoolYear],
                [GraduationPlanTypeDescriptor_DescriptorId],
                [TotalRequiredCredits]
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
            new SqlParameter("@documentId", documentId),
            new SqlParameter("@educationOrganizationDocumentId", educationOrganizationDocumentId),
            new SqlParameter("@educationOrganizationId", educationOrganizationId),
            new SqlParameter("@graduationSchoolYearDocumentId", graduationSchoolYearDocumentId),
            new SqlParameter("@graduationSchoolYear", graduationSchoolYear),
            new SqlParameter("@graduationPlanTypeDescriptorId", graduationPlanTypeDescriptorId),
            new SqlParameter("@totalRequiredCredits", totalRequiredCredits)
        );
    }

    private async Task UpsertReferentialIdentityAsync(
        ReferentialId referentialId,
        long documentId,
        short resourceKeyId
    )
    {
        await _database.ExecuteNonQueryAsync(
            """
            DELETE FROM [dms].[ReferentialIdentity]
            WHERE [DocumentId] = @documentId
              AND [ResourceKeyId] = @resourceKeyId;

            DELETE FROM [dms].[ReferentialIdentity]
            WHERE [ReferentialId] = @referentialId;

            INSERT INTO [dms].[ReferentialIdentity] ([ReferentialId], [DocumentId], [ResourceKeyId])
            VALUES (@referentialId, @documentId, @resourceKeyId);
            """,
            new SqlParameter("@referentialId", referentialId.Value),
            new SqlParameter("@documentId", documentId),
            new SqlParameter("@resourceKeyId", resourceKeyId)
        );
    }

    private async Task ExecuteWithTriggersTemporarilyDisabledAsync(
        string schema,
        string table,
        Func<Task> action
    )
    {
        await _database.ExecuteNonQueryAsync($"""DISABLE TRIGGER ALL ON [{schema}].[{table}];""");

        try
        {
            await action();
        }
        finally
        {
            await _database.ExecuteNonQueryAsync($"""ENABLE TRIGGER ALL ON [{schema}].[{table}];""");
        }
    }

    private static JsonNode CreateExpectedExternalResponse(
        string requestBodyJson,
        MssqlStudentSchoolAssociationDocumentMetadata metadata
    )
    {
        var expectedDocument =
            JsonNode.Parse(requestBodyJson)?.AsObject()
            ?? throw new InvalidOperationException("Expected request body JSON to parse into a JSON object.");

        expectedDocument["id"] = metadata.DocumentUuid.ToString();
        expectedDocument["_etag"] = $"\"{metadata.ContentVersion}\"";
        expectedDocument["_lastModifiedDate"] = FormatExternalLastModifiedDate(
            metadata.ContentLastModifiedAt
        );

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
}

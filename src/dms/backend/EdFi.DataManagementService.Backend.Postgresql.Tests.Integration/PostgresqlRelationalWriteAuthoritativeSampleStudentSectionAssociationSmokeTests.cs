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

file sealed class AuthoritativeSampleStudentSectionAssociationWriteHostApplicationLifetime
    : IHostApplicationLifetime
{
    public CancellationToken ApplicationStarted => CancellationToken.None;
    public CancellationToken ApplicationStopping => CancellationToken.None;
    public CancellationToken ApplicationStopped => CancellationToken.None;

    public void StopApplication() { }
}

file sealed class AuthoritativeSampleStudentSectionAssociationAllowAllResourceAuthorizationHandler
    : IResourceAuthorizationHandler
{
    public Task<ResourceAuthorizationResult> Authorize(
        DocumentSecurityElements documentSecurityElements,
        OperationType operationType,
        TraceId traceId
    ) => Task.FromResult<ResourceAuthorizationResult>(new ResourceAuthorizationResult.Authorized());
}

file sealed class AuthoritativeSampleStudentSectionAssociationNoOpUpdateCascadeHandler : IUpdateCascadeHandler
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

file static class AuthoritativeSampleStudentSectionAssociationIntegrationTestSupport
{
    public const string FixtureRelativePath = "src/dms/backend/Fixtures/authoritative/sample";

    public static ServiceProvider CreateServiceProvider()
    {
        ServiceCollection services = [];

        services.AddSingleton<
            IHostApplicationLifetime,
            AuthoritativeSampleStudentSectionAssociationWriteHostApplicationLifetime
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
        ResourceInfo extensionResourceInfo,
        ResourceSchema extensionResourceSchema,
        MappingSet mappingSet,
        long programTypeDescriptorId
    )
    {
        var (extensionDocumentReferences, extensionDocumentReferenceArrays) =
            CreateExtensionDocumentReferences(requestBody, programTypeDescriptorId);
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
                DocumentReferenceArrays: extensionDocumentReferenceArrays,
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

    public static DateOnly GetDateOnly(IReadOnlyDictionary<string, object?> row, string columnName) =>
        GetRequiredValue(row, columnName) switch
        {
            DateOnly value => value,
            DateTime value => DateOnly.FromDateTime(value),
            _ => throw new InvalidOperationException(
                $"Expected column '{columnName}' to contain a DateOnly value."
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
    ) CreateExtensionDocumentReferences(JsonNode requestBody, long programTypeDescriptorId)
    {
        var relatedAssociations =
            requestBody["_ext"]?["sample"]?["relatedGeneralStudentProgramAssociations"] as JsonArray;

        if (relatedAssociations is null || relatedAssociations.Count == 0)
        {
            return ([], []);
        }

        var targetResourceInfo = new BaseResourceInfo(
            new ProjectName("Ed-Fi"),
            new ResourceName("GeneralStudentProgramAssociation"),
            false
        );
        var descriptorValue = programTypeDescriptorId.ToString(CultureInfo.InvariantCulture);
        List<DocumentReference> documentReferences = [];

        for (var index = 0; index < relatedAssociations.Count; index++)
        {
            var reference = relatedAssociations[index]?["relatedGeneralStudentProgramAssociationReference"];

            if (reference is null)
            {
                throw new InvalidOperationException(
                    $"Expected relatedGeneralStudentProgramAssociationReference at array index {index}."
                );
            }

            var beginDate = reference["beginDate"]?.GetValue<string>();
            var educationOrganizationId = reference["educationOrganizationId"]
                ?.GetValue<long>()
                .ToString(CultureInfo.InvariantCulture);
            var programEducationOrganizationId = reference["programEducationOrganizationId"]
                ?.GetValue<long>()
                .ToString(CultureInfo.InvariantCulture);
            var programName = reference["programName"]?.GetValue<string>();
            var studentUniqueId = reference["studentUniqueId"]?.GetValue<string>();

            if (
                string.IsNullOrWhiteSpace(beginDate)
                || string.IsNullOrWhiteSpace(educationOrganizationId)
                || string.IsNullOrWhiteSpace(programEducationOrganizationId)
                || string.IsNullOrWhiteSpace(programName)
                || string.IsNullOrWhiteSpace(studentUniqueId)
            )
            {
                throw new InvalidOperationException(
                    "Expected every relatedGeneralStudentProgramAssociationReference to contain all identity members."
                );
            }

            var documentIdentity = new DocumentIdentity([
                new DocumentIdentityElement(new JsonPath("$.beginDate"), beginDate),
                new DocumentIdentityElement(
                    new JsonPath("$.educationOrganizationReference.educationOrganizationId"),
                    educationOrganizationId
                ),
                new DocumentIdentityElement(
                    new JsonPath("$.programReference.educationOrganizationId"),
                    programEducationOrganizationId
                ),
                new DocumentIdentityElement(new JsonPath("$.programReference.programName"), programName),
                new DocumentIdentityElement(
                    new JsonPath("$.programReference.programTypeDescriptor"),
                    descriptorValue
                ),
                new DocumentIdentityElement(
                    new JsonPath("$.studentReference.studentUniqueId"),
                    studentUniqueId
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
                        $"$._ext.sample.relatedGeneralStudentProgramAssociations[{index}].relatedGeneralStudentProgramAssociationReference"
                    )
                )
            );
        }

        return (
            documentReferences,
            [
                new DocumentReferenceArray(
                    new JsonPath(
                        "$._ext.sample.relatedGeneralStudentProgramAssociations[*].relatedGeneralStudentProgramAssociationReference"
                    ),
                    [.. documentReferences]
                ),
            ]
        );
    }

    private static IReadOnlyList<DescriptorReference> CreateExtensionDescriptorReferences(
        JsonNode requestBody
    )
    {
        var relatedAssociations =
            requestBody["_ext"]?["sample"]?["relatedGeneralStudentProgramAssociations"] as JsonArray;

        if (relatedAssociations is null || relatedAssociations.Count == 0)
        {
            return [];
        }

        var descriptorResourceInfo = new BaseResourceInfo(
            new ProjectName("Ed-Fi"),
            new ResourceName("ProgramTypeDescriptor"),
            true
        );
        List<DescriptorReference> descriptorReferences = [];

        for (var index = 0; index < relatedAssociations.Count; index++)
        {
            var descriptorUri = relatedAssociations[index]
                ?["relatedGeneralStudentProgramAssociationReference"]?[
                    "programTypeDescriptor"
                ]?.GetValue<string>();

            if (string.IsNullOrWhiteSpace(descriptorUri))
            {
                throw new InvalidOperationException(
                    "Expected every relatedGeneralStudentProgramAssociationReference to contain programTypeDescriptor."
                );
            }

            var descriptorIdentity = new DocumentIdentity([
                new DocumentIdentityElement(
                    DocumentIdentity.DescriptorIdentityJsonPath,
                    descriptorUri.ToLowerInvariant()
                ),
            ]);

            descriptorReferences.Add(
                new DescriptorReference(
                    ResourceInfo: descriptorResourceInfo,
                    DocumentIdentity: descriptorIdentity,
                    ReferentialId: ReferentialIdCalculator.ReferentialIdFrom(
                        descriptorResourceInfo,
                        descriptorIdentity
                    ),
                    Path: new JsonPath(
                        $"$._ext.sample.relatedGeneralStudentProgramAssociations[{index}].relatedGeneralStudentProgramAssociationReference.programTypeDescriptor"
                    )
                )
            );
        }

        return descriptorReferences;
    }
}

internal sealed record AuthoritativeSampleStudentSectionAssociationSeedData(
    long SchoolDocumentId,
    long StudentDocumentId,
    long ProgramTypeDescriptorDocumentId,
    long SectionDocumentId,
    long RoboticsClubAssociationDocumentId,
    long StemLabAssociationDocumentId,
    long DesignStudioAssociationDocumentId,
    long ArtsMentorshipAssociationDocumentId
);

internal sealed record AuthoritativeSampleStudentSectionAssociationDocumentRow(
    long DocumentId,
    Guid DocumentUuid,
    short ResourceKeyId,
    long ContentVersion
);

internal sealed record AuthoritativeSampleStudentSectionAssociationRow(
    long DocumentId,
    long SectionDocumentId,
    string SectionLocalCourseCode,
    long SectionSchoolId,
    int SectionSchoolYear,
    string SectionSectionIdentifier,
    string SectionSessionName,
    long StudentDocumentId,
    string StudentUniqueId,
    DateOnly BeginDate
);

internal sealed record AuthoritativeSampleStudentSectionAssociationExtensionRow(long DocumentId);

internal sealed record AuthoritativeSampleStudentSectionAssociationProgramAssociationRow(
    long CollectionItemId,
    int Ordinal,
    long StudentSectionAssociationDocumentId,
    long RelatedGeneralStudentProgramAssociationDocumentId,
    DateOnly RelatedGeneralStudentProgramAssociationBeginDate,
    long RelatedGeneralStudentProgramAssociationEducationOrganizationId,
    long RelatedGeneralStudentProgramAssociationProgramEducationOrganizationId,
    string RelatedGeneralStudentProgramAssociationProgramName,
    long RelatedGeneralStudentProgramAssociationProgramTypeDescriptorId,
    string RelatedGeneralStudentProgramAssociationStudentUniqueId
);

internal sealed record AuthoritativeSampleStudentSectionAssociationPersistedState(
    AuthoritativeSampleStudentSectionAssociationDocumentRow Document,
    AuthoritativeSampleStudentSectionAssociationRow Association,
    AuthoritativeSampleStudentSectionAssociationExtensionRow AssociationExtension,
    IReadOnlyList<AuthoritativeSampleStudentSectionAssociationProgramAssociationRow> RelatedGeneralStudentProgramAssociations
);

[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
[NonParallelizable]
public class Given_A_Postgresql_Relational_Write_Smoke_With_The_Authoritative_Sample_StudentSectionAssociation_Fixture
{
    private const long SchoolId = 100;
    private const int SchoolYear = 2024;
    private const string StudentUniqueId = "10001";
    private const string SessionName = "Fall Semester";
    private const string LocalCourseCode = "ALG-1-ADV";
    private const string SectionIdentifier = "ALG-1-ADV-01";
    private const string ProgramTypeDescriptorUri = "uri://ed-fi.org/ProgramTypeDescriptor#Extracurricular";
    private const string TermDescriptorUri = "uri://ed-fi.org/TermDescriptor#Fall";
    private const string RoboticsClubProgramName = "Robotics Club";
    private const string StemLabProgramName = "STEM Lab";
    private const string DesignStudioProgramName = "Design Studio";
    private const string ArtsMentorshipProgramName = "Arts Mentorship";

    private const string CreateRequestBodyJson = """
        {
          "beginDate": "2024-08-20",
          "sectionReference": {
            "localCourseCode": "ALG-1-ADV",
            "schoolId": 100,
            "schoolYear": 2024,
            "sectionIdentifier": "ALG-1-ADV-01",
            "sessionName": "Fall Semester"
          },
          "studentReference": {
            "studentUniqueId": "10001"
          },
          "_ext": {
            "sample": {
              "relatedGeneralStudentProgramAssociations": [
                {
                  "relatedGeneralStudentProgramAssociationReference": {
                    "beginDate": "2024-08-20",
                    "educationOrganizationId": 100,
                    "programEducationOrganizationId": 100,
                    "programName": "Robotics Club",
                    "programTypeDescriptor": "uri://ed-fi.org/ProgramTypeDescriptor#Extracurricular",
                    "studentUniqueId": "10001"
                  }
                },
                {
                  "relatedGeneralStudentProgramAssociationReference": {
                    "beginDate": "2024-08-20",
                    "educationOrganizationId": 100,
                    "programEducationOrganizationId": 100,
                    "programName": "STEM Lab",
                    "programTypeDescriptor": "uri://ed-fi.org/ProgramTypeDescriptor#Extracurricular",
                    "studentUniqueId": "10001"
                  }
                },
                {
                  "relatedGeneralStudentProgramAssociationReference": {
                    "beginDate": "2024-08-20",
                    "educationOrganizationId": 100,
                    "programEducationOrganizationId": 100,
                    "programName": "Design Studio",
                    "programTypeDescriptor": "uri://ed-fi.org/ProgramTypeDescriptor#Extracurricular",
                    "studentUniqueId": "10001"
                  }
                }
              ]
            }
          }
        }
        """;

    private const string ChangedUpdateRequestBodyJson = """
        {
          "beginDate": "2024-08-20",
          "sectionReference": {
            "localCourseCode": "ALG-1-ADV",
            "schoolId": 100,
            "schoolYear": 2024,
            "sectionIdentifier": "ALG-1-ADV-01",
            "sessionName": "Fall Semester"
          },
          "studentReference": {
            "studentUniqueId": "10001"
          },
          "_ext": {
            "sample": {
              "relatedGeneralStudentProgramAssociations": [
                {
                  "relatedGeneralStudentProgramAssociationReference": {
                    "beginDate": "2024-08-20",
                    "educationOrganizationId": 100,
                    "programEducationOrganizationId": 100,
                    "programName": "Design Studio",
                    "programTypeDescriptor": "uri://ed-fi.org/ProgramTypeDescriptor#Extracurricular",
                    "studentUniqueId": "10001"
                  }
                },
                {
                  "relatedGeneralStudentProgramAssociationReference": {
                    "beginDate": "2024-08-20",
                    "educationOrganizationId": 100,
                    "programEducationOrganizationId": 100,
                    "programName": "Robotics Club",
                    "programTypeDescriptor": "uri://ed-fi.org/ProgramTypeDescriptor#Extracurricular",
                    "studentUniqueId": "10001"
                  }
                },
                {
                  "relatedGeneralStudentProgramAssociationReference": {
                    "beginDate": "2024-08-20",
                    "educationOrganizationId": 100,
                    "programEducationOrganizationId": 100,
                    "programName": "Arts Mentorship",
                    "programTypeDescriptor": "uri://ed-fi.org/ProgramTypeDescriptor#Extracurricular",
                    "studentUniqueId": "10001"
                  }
                }
              ]
            }
          }
        }
        """;

    private static readonly DateOnly AssociationBeginDate = new(2024, 8, 20);

    private static readonly DocumentUuid StudentSectionAssociationDocumentUuid = new(
        Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001")
    );

    private PostgresqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private PostgresqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;
    private ResourceInfo _resourceInfo = null!;
    private ResourceInfo _extensionResourceInfo = null!;
    private ResourceSchema _baseResourceSchema = null!;
    private ResourceSchema _extensionResourceSchema = null!;
    private AuthoritativeSampleStudentSectionAssociationSeedData _seedData = null!;
    private DbTableModel _relatedGeneralStudentProgramAssociationsTable = null!;
    private UpsertResult _createResult = null!;
    private UpdateResult _changedUpdateResult = null!;
    private UpdateResult _noOpUpdateResult = null!;
    private AuthoritativeSampleStudentSectionAssociationPersistedState _stateAfterCreate = null!;
    private AuthoritativeSampleStudentSectionAssociationPersistedState _stateAfterChangedUpdate = null!;
    private AuthoritativeSampleStudentSectionAssociationPersistedState _stateAfterNoOpUpdate = null!;

    [SetUp]
    public async Task Setup()
    {
        _fixture = PostgresqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            AuthoritativeSampleStudentSectionAssociationIntegrationTestSupport.FixtureRelativePath
        );
        _mappingSet = new MappingSetCompiler().Compile(_fixture.ModelSet);
        _database = await PostgresqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
        _serviceProvider =
            AuthoritativeSampleStudentSectionAssociationIntegrationTestSupport.CreateServiceProvider();
        _relatedGeneralStudentProgramAssociationsTable =
            PostgresqlGeneratedDdlModelLookup.RequireTableByScopeAndColumns(
                _fixture.ModelSet,
                "sample",
                "$._ext.sample.relatedGeneralStudentProgramAssociations[*]",
                "StudentSectionAssociation_DocumentId",
                "RelatedGeneralStudentProgramAssociation_DocumentId"
            );

        var (baseProjectSchema, baseResourceSchema) =
            AuthoritativeSampleStudentSectionAssociationIntegrationTestSupport.GetResourceSchema(
                _fixture.EffectiveSchemaSet,
                "ed-fi",
                "StudentSectionAssociation"
            );
        var (extensionProjectSchema, extensionResourceSchema) =
            AuthoritativeSampleStudentSectionAssociationIntegrationTestSupport.GetResourceSchema(
                _fixture.EffectiveSchemaSet,
                "sample",
                "StudentSectionAssociation"
            );

        _resourceInfo = AuthoritativeSampleStudentSectionAssociationIntegrationTestSupport.CreateResourceInfo(
            baseProjectSchema,
            baseResourceSchema
        );
        _extensionResourceInfo =
            AuthoritativeSampleStudentSectionAssociationIntegrationTestSupport.CreateResourceInfo(
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
                $"Create reference failure: {AuthoritativeSampleStudentSectionAssociationIntegrationTestSupport.FormatReferenceFailure(createReferenceFailure)}"
            );
        }

        _createResult.Should().BeOfType<UpsertResult.InsertSuccess>();
        _stateAfterCreate = await ReadPersistedStateAsync(StudentSectionAssociationDocumentUuid.Value);

        _changedUpdateResult = await ExecuteChangedUpdateAsync();

        if (_changedUpdateResult is UpdateResult.UpdateFailureReference changedUpdateReferenceFailure)
        {
            Assert.Fail(
                $"Changed update reference failure: {AuthoritativeSampleStudentSectionAssociationIntegrationTestSupport.FormatReferenceFailure(changedUpdateReferenceFailure)}"
            );
        }

        _changedUpdateResult.Should().BeOfType<UpdateResult.UpdateSuccess>();
        _stateAfterChangedUpdate = await ReadPersistedStateAsync(StudentSectionAssociationDocumentUuid.Value);

        _noOpUpdateResult = await ExecuteNoOpUpdateAsync();

        if (_noOpUpdateResult is UpdateResult.UpdateFailureReference noOpReferenceFailure)
        {
            Assert.Fail(
                $"No-op update reference failure: {AuthoritativeSampleStudentSectionAssociationIntegrationTestSupport.FormatReferenceFailure(noOpReferenceFailure)}"
            );
        }

        _noOpUpdateResult.Should().BeOfType<UpdateResult.UpdateSuccess>();
        _stateAfterNoOpUpdate = await ReadPersistedStateAsync(StudentSectionAssociationDocumentUuid.Value);
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
    public void It_persists_the_authoritative_sample_root_extension_and_extension_child_collection_rows_on_create()
    {
        _createResult.Should().BeOfType<UpsertResult.InsertSuccess>();
        _stateAfterCreate.Document.DocumentUuid.Should().Be(StudentSectionAssociationDocumentUuid.Value);
        _stateAfterCreate
            .Document.ResourceKeyId.Should()
            .Be(
                _mappingSet.ResourceKeyIdByResource[
                    new QualifiedResourceName("Ed-Fi", "StudentSectionAssociation")
                ]
            );
        _stateAfterCreate
            .Association.Should()
            .Be(
                new AuthoritativeSampleStudentSectionAssociationRow(
                    _stateAfterCreate.Document.DocumentId,
                    _seedData.SectionDocumentId,
                    LocalCourseCode,
                    SchoolId,
                    SchoolYear,
                    SectionIdentifier,
                    SessionName,
                    _seedData.StudentDocumentId,
                    StudentUniqueId,
                    AssociationBeginDate
                )
            );
        _stateAfterCreate
            .AssociationExtension.Should()
            .Be(
                new AuthoritativeSampleStudentSectionAssociationExtensionRow(
                    _stateAfterCreate.Document.DocumentId
                )
            );
        _stateAfterCreate
            .RelatedGeneralStudentProgramAssociations.Should()
            .Equal(
                new AuthoritativeSampleStudentSectionAssociationProgramAssociationRow(
                    _stateAfterCreate.RelatedGeneralStudentProgramAssociations[0].CollectionItemId,
                    0,
                    _stateAfterCreate.Document.DocumentId,
                    _seedData.RoboticsClubAssociationDocumentId,
                    AssociationBeginDate,
                    SchoolId,
                    SchoolId,
                    RoboticsClubProgramName,
                    _seedData.ProgramTypeDescriptorDocumentId,
                    StudentUniqueId
                ),
                new AuthoritativeSampleStudentSectionAssociationProgramAssociationRow(
                    _stateAfterCreate.RelatedGeneralStudentProgramAssociations[1].CollectionItemId,
                    1,
                    _stateAfterCreate.Document.DocumentId,
                    _seedData.StemLabAssociationDocumentId,
                    AssociationBeginDate,
                    SchoolId,
                    SchoolId,
                    StemLabProgramName,
                    _seedData.ProgramTypeDescriptorDocumentId,
                    StudentUniqueId
                ),
                new AuthoritativeSampleStudentSectionAssociationProgramAssociationRow(
                    _stateAfterCreate.RelatedGeneralStudentProgramAssociations[2].CollectionItemId,
                    2,
                    _stateAfterCreate.Document.DocumentId,
                    _seedData.DesignStudioAssociationDocumentId,
                    AssociationBeginDate,
                    SchoolId,
                    SchoolId,
                    DesignStudioProgramName,
                    _seedData.ProgramTypeDescriptorDocumentId,
                    StudentUniqueId
                )
            );
    }

    [Test]
    public void It_reuses_stable_collection_item_ids_when_extension_children_are_reordered_removed_and_replaced()
    {
        _changedUpdateResult.Should().BeOfType<UpdateResult.UpdateSuccess>();
        _changedUpdateResult
            .As<UpdateResult.UpdateSuccess>()
            .ExistingDocumentUuid.Should()
            .Be(StudentSectionAssociationDocumentUuid);
        _stateAfterChangedUpdate
            .Document.ContentVersion.Should()
            .BeGreaterThan(_stateAfterCreate.Document.ContentVersion);

        var createRowsByProgramName = _stateAfterCreate.RelatedGeneralStudentProgramAssociations.ToDictionary(
            row => row.RelatedGeneralStudentProgramAssociationProgramName
        );
        var changedRowsByProgramName =
            _stateAfterChangedUpdate.RelatedGeneralStudentProgramAssociations.ToDictionary(row =>
                row.RelatedGeneralStudentProgramAssociationProgramName
            );

        changedRowsByProgramName[DesignStudioProgramName]
            .CollectionItemId.Should()
            .Be(createRowsByProgramName[DesignStudioProgramName].CollectionItemId);
        changedRowsByProgramName[RoboticsClubProgramName]
            .CollectionItemId.Should()
            .Be(createRowsByProgramName[RoboticsClubProgramName].CollectionItemId);
        changedRowsByProgramName.Keys.Should().NotContain(StemLabProgramName);
        changedRowsByProgramName[ArtsMentorshipProgramName]
            .CollectionItemId.Should()
            .NotBe(createRowsByProgramName[StemLabProgramName].CollectionItemId);

        _stateAfterChangedUpdate
            .RelatedGeneralStudentProgramAssociations.Should()
            .Equal(
                new AuthoritativeSampleStudentSectionAssociationProgramAssociationRow(
                    createRowsByProgramName[DesignStudioProgramName].CollectionItemId,
                    0,
                    _stateAfterChangedUpdate.Document.DocumentId,
                    _seedData.DesignStudioAssociationDocumentId,
                    AssociationBeginDate,
                    SchoolId,
                    SchoolId,
                    DesignStudioProgramName,
                    _seedData.ProgramTypeDescriptorDocumentId,
                    StudentUniqueId
                ),
                new AuthoritativeSampleStudentSectionAssociationProgramAssociationRow(
                    createRowsByProgramName[RoboticsClubProgramName].CollectionItemId,
                    1,
                    _stateAfterChangedUpdate.Document.DocumentId,
                    _seedData.RoboticsClubAssociationDocumentId,
                    AssociationBeginDate,
                    SchoolId,
                    SchoolId,
                    RoboticsClubProgramName,
                    _seedData.ProgramTypeDescriptorDocumentId,
                    StudentUniqueId
                ),
                new AuthoritativeSampleStudentSectionAssociationProgramAssociationRow(
                    changedRowsByProgramName[ArtsMentorshipProgramName].CollectionItemId,
                    2,
                    _stateAfterChangedUpdate.Document.DocumentId,
                    _seedData.ArtsMentorshipAssociationDocumentId,
                    AssociationBeginDate,
                    SchoolId,
                    SchoolId,
                    ArtsMentorshipProgramName,
                    _seedData.ProgramTypeDescriptorDocumentId,
                    StudentUniqueId
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
            .Be(StudentSectionAssociationDocumentUuid);
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
                    InstanceName: "PostgresqlRelationalWriteAuthoritativeSampleStudentSectionAssociation",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var requestBody = JsonNode.Parse(CreateRequestBodyJson)!;
        var request = new UpsertRequest(
            ResourceInfo: _resourceInfo,
            DocumentInfo: AuthoritativeSampleStudentSectionAssociationIntegrationTestSupport.CreateDocumentInfo(
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
            TraceId: new TraceId("pg-authoritative-sample-student-section-association-create"),
            DocumentUuid: StudentSectionAssociationDocumentUuid,
            DocumentSecurityElements: new([], [], [], [], []),
            UpdateCascadeHandler: new AuthoritativeSampleStudentSectionAssociationNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new AuthoritativeSampleStudentSectionAssociationAllowAllResourceAuthorizationHandler(),
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
                    InstanceName: "PostgresqlRelationalWriteAuthoritativeSampleStudentSectionAssociation",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var requestBody = JsonNode.Parse(ChangedUpdateRequestBodyJson)!;
        var request = new UpdateRequest(
            ResourceInfo: _resourceInfo,
            DocumentInfo: AuthoritativeSampleStudentSectionAssociationIntegrationTestSupport.CreateDocumentInfo(
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
            TraceId: new TraceId("pg-authoritative-sample-student-section-association-changed-update"),
            DocumentUuid: StudentSectionAssociationDocumentUuid,
            DocumentSecurityElements: new([], [], [], [], []),
            UpdateCascadeHandler: new AuthoritativeSampleStudentSectionAssociationNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new AuthoritativeSampleStudentSectionAssociationAllowAllResourceAuthorizationHandler(),
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
                    InstanceName: "PostgresqlRelationalWriteAuthoritativeSampleStudentSectionAssociation",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var requestBody = JsonNode.Parse(ChangedUpdateRequestBodyJson)!;
        var request = new UpdateRequest(
            ResourceInfo: _resourceInfo,
            DocumentInfo: AuthoritativeSampleStudentSectionAssociationIntegrationTestSupport.CreateDocumentInfo(
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
            TraceId: new TraceId("pg-authoritative-sample-student-section-association-no-op"),
            DocumentUuid: StudentSectionAssociationDocumentUuid,
            DocumentSecurityElements: new([], [], [], [], []),
            UpdateCascadeHandler: new AuthoritativeSampleStudentSectionAssociationNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new AuthoritativeSampleStudentSectionAssociationAllowAllResourceAuthorizationHandler(),
            ResourceAuthorizationPathways: []
        );

        return await scope
            .ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>()
            .UpdateDocumentById(request);
    }

    private async Task<AuthoritativeSampleStudentSectionAssociationSeedData> SeedReferenceDataAsync()
    {
        var educationOrganizationResourceKeyId = await GetResourceKeyIdAsync(
            "Ed-Fi",
            "EducationOrganization"
        );
        var schoolResourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "School");
        var studentResourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "Student");
        var schoolYearTypeResourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "SchoolYearType");
        var termDescriptorResourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "TermDescriptor");
        var programTypeDescriptorResourceKeyId = await GetResourceKeyIdAsync(
            "Ed-Fi",
            "ProgramTypeDescriptor"
        );
        var courseResourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "Course");
        var sessionResourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "Session");
        var courseOfferingResourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "CourseOffering");
        var sectionResourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "Section");
        var programResourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "Program");
        var generalStudentProgramAssociationResourceKeyId = await GetResourceKeyIdAsync(
            "Ed-Fi",
            "GeneralStudentProgramAssociation"
        );
        var studentArtProgramAssociationResourceKeyId = await GetResourceKeyIdAsync(
            "Sample",
            "StudentArtProgramAssociation"
        );

        var schoolDocumentId = await InsertDocumentAsync(
            Guid.Parse("11111111-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
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
            Guid.Parse("22222222-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            studentResourceKeyId
        );
        await InsertStudentAsync(studentDocumentId, StudentUniqueId, "Casey", "Cole");
        await InsertReferentialIdentityAsync(
            CreateReferentialId(("Ed-Fi", "Student", false), ("$.studentUniqueId", StudentUniqueId)),
            studentDocumentId,
            studentResourceKeyId
        );

        var schoolYearTypeDocumentId = await InsertDocumentAsync(
            Guid.Parse("33333333-cccc-cccc-cccc-cccccccccccc"),
            schoolYearTypeResourceKeyId
        );
        await InsertSchoolYearTypeAsync(schoolYearTypeDocumentId, SchoolYear);

        var termDescriptorDocumentId = await InsertDescriptorAsync(
            Guid.Parse("44444444-dddd-dddd-dddd-dddddddddddd"),
            termDescriptorResourceKeyId,
            "Ed-Fi:TermDescriptor",
            TermDescriptorUri,
            "uri://ed-fi.org/TermDescriptor",
            "Fall",
            "Fall"
        );
        await InsertReferentialIdentityAsync(
            CreateDescriptorReferentialId("Ed-Fi", "TermDescriptor", TermDescriptorUri),
            termDescriptorDocumentId,
            termDescriptorResourceKeyId
        );

        var programTypeDescriptorDocumentId = await InsertDescriptorAsync(
            Guid.Parse("55555555-eeee-eeee-eeee-eeeeeeeeeeee"),
            programTypeDescriptorResourceKeyId,
            "Ed-Fi:ProgramTypeDescriptor",
            ProgramTypeDescriptorUri,
            "uri://ed-fi.org/ProgramTypeDescriptor",
            "Extracurricular",
            "Extracurricular"
        );
        await InsertReferentialIdentityAsync(
            CreateDescriptorReferentialId("Ed-Fi", "ProgramTypeDescriptor", ProgramTypeDescriptorUri),
            programTypeDescriptorDocumentId,
            programTypeDescriptorResourceKeyId
        );

        var courseDocumentId = await InsertDocumentAsync(
            Guid.Parse("66666666-ffff-ffff-ffff-ffffffffffff"),
            courseResourceKeyId
        );
        await InsertCourseAsync(courseDocumentId, schoolDocumentId, SchoolId, "ALG-1", "Algebra I");

        var sessionDocumentId = await InsertDocumentAsync(
            Guid.Parse("77777777-1111-1111-1111-111111111111"),
            sessionResourceKeyId
        );
        await InsertSessionAsync(
            sessionDocumentId,
            schoolYearTypeDocumentId,
            SchoolYear,
            schoolDocumentId,
            SchoolId,
            termDescriptorDocumentId,
            SessionName
        );

        var courseOfferingDocumentId = await InsertDocumentAsync(
            Guid.Parse("88888888-2222-2222-2222-222222222222"),
            courseOfferingResourceKeyId
        );
        await InsertCourseOfferingAsync(
            courseOfferingDocumentId,
            SchoolId,
            courseDocumentId,
            "ALG-1",
            SchoolId,
            schoolDocumentId,
            sessionDocumentId,
            SchoolYear,
            SessionName,
            LocalCourseCode
        );

        var sectionDocumentId = await InsertDocumentAsync(
            Guid.Parse("99999999-3333-3333-3333-333333333333"),
            sectionResourceKeyId
        );
        await InsertSectionAsync(
            sectionDocumentId,
            SchoolId,
            courseOfferingDocumentId,
            LocalCourseCode,
            SchoolYear,
            SessionName,
            SectionIdentifier
        );
        var createRequestBody = JsonNode.Parse(CreateRequestBodyJson)!;
        var (baseDocumentReferences, _) = _baseResourceSchema.ExtractReferences(
            createRequestBody,
            NullLogger.Instance,
            ReferenceExtractionMode.RelationalWriteValidation
        );
        var sectionReference = baseDocumentReferences.Single(reference =>
            reference.Path == new JsonPath("$.sectionReference")
        );
        await InsertReferentialIdentityAsync(
            sectionReference.ReferentialId,
            sectionDocumentId,
            sectionResourceKeyId
        );

        var roboticsProgramDocumentId = await SeedProgramAsync(
            Guid.Parse("aaaaaaaa-4444-4444-4444-444444444444"),
            programResourceKeyId,
            schoolDocumentId,
            programTypeDescriptorDocumentId,
            "PRG-01",
            RoboticsClubProgramName
        );
        var stemLabProgramDocumentId = await SeedProgramAsync(
            Guid.Parse("bbbbbbbb-5555-5555-5555-555555555555"),
            programResourceKeyId,
            schoolDocumentId,
            programTypeDescriptorDocumentId,
            "PRG-02",
            StemLabProgramName
        );
        var designStudioProgramDocumentId = await SeedProgramAsync(
            Guid.Parse("cccccccc-6666-6666-6666-666666666666"),
            programResourceKeyId,
            schoolDocumentId,
            programTypeDescriptorDocumentId,
            "PRG-03",
            DesignStudioProgramName
        );
        var artsMentorshipProgramDocumentId = await SeedProgramAsync(
            Guid.Parse("dddddddd-7777-7777-7777-777777777777"),
            programResourceKeyId,
            schoolDocumentId,
            programTypeDescriptorDocumentId,
            "PRG-04",
            ArtsMentorshipProgramName
        );

        var roboticsClubAssociationDocumentId = await SeedStudentArtProgramAssociationAsync(
            Guid.Parse("eeeeeeee-8888-8888-8888-888888888888"),
            studentArtProgramAssociationResourceKeyId,
            generalStudentProgramAssociationResourceKeyId,
            schoolDocumentId,
            roboticsProgramDocumentId,
            programTypeDescriptorDocumentId,
            RoboticsClubProgramName,
            studentDocumentId
        );
        var stemLabAssociationDocumentId = await SeedStudentArtProgramAssociationAsync(
            Guid.Parse("ffffffff-9999-9999-9999-999999999999"),
            studentArtProgramAssociationResourceKeyId,
            generalStudentProgramAssociationResourceKeyId,
            schoolDocumentId,
            stemLabProgramDocumentId,
            programTypeDescriptorDocumentId,
            StemLabProgramName,
            studentDocumentId
        );
        var designStudioAssociationDocumentId = await SeedStudentArtProgramAssociationAsync(
            Guid.Parse("10101010-aaaa-bbbb-cccc-dddddddddddd"),
            studentArtProgramAssociationResourceKeyId,
            generalStudentProgramAssociationResourceKeyId,
            schoolDocumentId,
            designStudioProgramDocumentId,
            programTypeDescriptorDocumentId,
            DesignStudioProgramName,
            studentDocumentId
        );
        var artsMentorshipAssociationDocumentId = await SeedStudentArtProgramAssociationAsync(
            Guid.Parse("20202020-bbbb-cccc-dddd-eeeeeeeeeeee"),
            studentArtProgramAssociationResourceKeyId,
            generalStudentProgramAssociationResourceKeyId,
            schoolDocumentId,
            artsMentorshipProgramDocumentId,
            programTypeDescriptorDocumentId,
            ArtsMentorshipProgramName,
            studentDocumentId
        );

        return new(
            schoolDocumentId,
            studentDocumentId,
            programTypeDescriptorDocumentId,
            sectionDocumentId,
            roboticsClubAssociationDocumentId,
            stemLabAssociationDocumentId,
            designStudioAssociationDocumentId,
            artsMentorshipAssociationDocumentId
        );
    }

    private async Task<long> SeedProgramAsync(
        Guid documentUuid,
        short programResourceKeyId,
        long schoolDocumentId,
        long programTypeDescriptorDocumentId,
        string programId,
        string programName
    )
    {
        var programDocumentId = await InsertDocumentAsync(documentUuid, programResourceKeyId);
        await InsertProgramAsync(
            programDocumentId,
            schoolDocumentId,
            SchoolId,
            programTypeDescriptorDocumentId,
            programId,
            programName
        );
        await InsertReferentialIdentityAsync(
            CreateReferentialId(
                ("Ed-Fi", "Program", false),
                (
                    "$.educationOrganizationReference.educationOrganizationId",
                    SchoolId.ToString(CultureInfo.InvariantCulture)
                ),
                ("$.programName", programName),
                (
                    "$.programTypeDescriptor",
                    programTypeDescriptorDocumentId.ToString(CultureInfo.InvariantCulture)
                )
            ),
            programDocumentId,
            programResourceKeyId
        );

        return programDocumentId;
    }

    private async Task<long> SeedStudentArtProgramAssociationAsync(
        Guid documentUuid,
        short studentArtProgramAssociationResourceKeyId,
        short generalStudentProgramAssociationResourceKeyId,
        long schoolDocumentId,
        long programDocumentId,
        long programTypeDescriptorDocumentId,
        string programName,
        long studentDocumentId
    )
    {
        var documentId = await InsertDocumentAsync(documentUuid, studentArtProgramAssociationResourceKeyId);
        await InsertStudentArtProgramAssociationAsync(
            documentId,
            schoolDocumentId,
            SchoolId,
            programDocumentId,
            SchoolId,
            programName,
            programTypeDescriptorDocumentId,
            studentDocumentId,
            StudentUniqueId,
            AssociationBeginDate
        );
        await InsertGeneralStudentProgramAssociationIdentityAsync(
            documentId,
            AssociationBeginDate,
            SchoolId,
            SchoolId,
            programName,
            programTypeDescriptorDocumentId,
            StudentUniqueId
        );
        await InsertReferentialIdentityAsync(
            CreateReferentialId(
                ("Ed-Fi", "GeneralStudentProgramAssociation", false),
                ("$.beginDate", FormatDate(AssociationBeginDate)),
                (
                    "$.educationOrganizationReference.educationOrganizationId",
                    SchoolId.ToString(CultureInfo.InvariantCulture)
                ),
                (
                    "$.programReference.educationOrganizationId",
                    SchoolId.ToString(CultureInfo.InvariantCulture)
                ),
                ("$.programReference.programName", programName),
                (
                    "$.programReference.programTypeDescriptor",
                    programTypeDescriptorDocumentId.ToString(CultureInfo.InvariantCulture)
                ),
                ("$.studentReference.studentUniqueId", StudentUniqueId)
            ),
            documentId,
            generalStudentProgramAssociationResourceKeyId
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

    private async Task InsertSchoolYearTypeAsync(long documentId, int schoolYear)
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
            new NpgsqlParameter("currentSchoolYear", true),
            new NpgsqlParameter("schoolYear", schoolYear),
            new NpgsqlParameter("schoolYearDescription", $"{schoolYear}-{schoolYear + 1}")
        );
    }

    private async Task InsertCourseAsync(
        long documentId,
        long educationOrganizationDocumentId,
        long educationOrganizationId,
        string courseCode,
        string courseTitle
    )
    {
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO "edfi"."Course" (
                "DocumentId",
                "EducationOrganization_DocumentId",
                "EducationOrganization_EducationOrganizationId",
                "CourseCode",
                "CourseTitle",
                "NumberOfParts"
            )
            VALUES (
                @documentId,
                @educationOrganizationDocumentId,
                @educationOrganizationId,
                @courseCode,
                @courseTitle,
                @numberOfParts
            );
            """,
            new NpgsqlParameter("documentId", documentId),
            new NpgsqlParameter("educationOrganizationDocumentId", educationOrganizationDocumentId),
            new NpgsqlParameter("educationOrganizationId", educationOrganizationId),
            new NpgsqlParameter("courseCode", courseCode),
            new NpgsqlParameter("courseTitle", courseTitle),
            new NpgsqlParameter("numberOfParts", 1)
        );
    }

    private async Task InsertSessionAsync(
        long documentId,
        long schoolYearDocumentId,
        int schoolYear,
        long schoolDocumentId,
        long schoolId,
        long termDescriptorId,
        string sessionName
    )
    {
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO "edfi"."Session" (
                "DocumentId",
                "SchoolYear_DocumentId",
                "SchoolYear_SchoolYear",
                "School_DocumentId",
                "School_SchoolId",
                "TermDescriptor_DescriptorId",
                "BeginDate",
                "EndDate",
                "SessionName",
                "TotalInstructionalDays"
            )
            VALUES (
                @documentId,
                @schoolYearDocumentId,
                @schoolYear,
                @schoolDocumentId,
                @schoolId,
                @termDescriptorId,
                @beginDate,
                @endDate,
                @sessionName,
                @totalInstructionalDays
            );
            """,
            new NpgsqlParameter("documentId", documentId),
            new NpgsqlParameter("schoolYearDocumentId", schoolYearDocumentId),
            new NpgsqlParameter("schoolYear", schoolYear),
            new NpgsqlParameter("schoolDocumentId", schoolDocumentId),
            new NpgsqlParameter("schoolId", schoolId),
            new NpgsqlParameter("termDescriptorId", termDescriptorId),
            new NpgsqlParameter("beginDate", new DateOnly(2024, 8, 12)),
            new NpgsqlParameter("endDate", new DateOnly(2024, 12, 20)),
            new NpgsqlParameter("sessionName", sessionName),
            new NpgsqlParameter("totalInstructionalDays", 90)
        );
    }

    private async Task InsertCourseOfferingAsync(
        long documentId,
        long schoolId,
        long courseDocumentId,
        string courseCode,
        long courseEducationOrganizationId,
        long schoolDocumentId,
        long sessionDocumentId,
        int schoolYear,
        string sessionName,
        string localCourseCode
    )
    {
        await RunWithDisabledTriggerAsync(
            "edfi",
            "CourseOffering",
            "TR_CourseOffering_ReferentialIdentity",
            async () =>
                await _database.ExecuteNonQueryAsync(
                    """
                    INSERT INTO "edfi"."CourseOffering" (
                        "DocumentId",
                        "SchoolId_Unified",
                        "Course_DocumentId",
                        "Course_CourseCode",
                        "Course_EducationOrganizationId",
                        "School_DocumentId",
                        "Session_DocumentId",
                        "Session_SchoolYear",
                        "Session_SessionName",
                        "LocalCourseCode"
                    )
                    VALUES (
                        @documentId,
                        @schoolId,
                        @courseDocumentId,
                        @courseCode,
                        @courseEducationOrganizationId,
                        @schoolDocumentId,
                        @sessionDocumentId,
                        @schoolYear,
                        @sessionName,
                        @localCourseCode
                    );
                    """,
                    new NpgsqlParameter("documentId", documentId),
                    new NpgsqlParameter("schoolId", schoolId),
                    new NpgsqlParameter("courseDocumentId", courseDocumentId),
                    new NpgsqlParameter("courseCode", courseCode),
                    new NpgsqlParameter("courseEducationOrganizationId", courseEducationOrganizationId),
                    new NpgsqlParameter("schoolDocumentId", schoolDocumentId),
                    new NpgsqlParameter("sessionDocumentId", sessionDocumentId),
                    new NpgsqlParameter("schoolYear", schoolYear),
                    new NpgsqlParameter("sessionName", sessionName),
                    new NpgsqlParameter("localCourseCode", localCourseCode)
                )
        );
    }

    private async Task InsertSectionAsync(
        long documentId,
        long schoolId,
        long courseOfferingDocumentId,
        string localCourseCode,
        int schoolYear,
        string sessionName,
        string sectionIdentifier
    )
    {
        await RunWithDisabledTriggerAsync(
            "edfi",
            "Section",
            "TR_Section_ReferentialIdentity",
            async () =>
                await _database.ExecuteNonQueryAsync(
                    """
                    INSERT INTO "edfi"."Section" (
                        "DocumentId",
                        "SchoolId_Unified",
                        "CourseOffering_DocumentId",
                        "CourseOffering_LocalCourseCode",
                        "CourseOffering_SchoolYear",
                        "CourseOffering_SessionName",
                        "SectionIdentifier",
                        "SectionName"
                    )
                    VALUES (
                        @documentId,
                        @schoolId,
                        @courseOfferingDocumentId,
                        @localCourseCode,
                        @schoolYear,
                        @sessionName,
                        @sectionIdentifier,
                        @sectionName
                    );
                    """,
                    new NpgsqlParameter("documentId", documentId),
                    new NpgsqlParameter("schoolId", schoolId),
                    new NpgsqlParameter("courseOfferingDocumentId", courseOfferingDocumentId),
                    new NpgsqlParameter("localCourseCode", localCourseCode),
                    new NpgsqlParameter("schoolYear", schoolYear),
                    new NpgsqlParameter("sessionName", sessionName),
                    new NpgsqlParameter("sectionIdentifier", sectionIdentifier),
                    new NpgsqlParameter("sectionName", "Advanced Algebra I")
                )
        );
    }

    private async Task InsertProgramAsync(
        long documentId,
        long educationOrganizationDocumentId,
        long educationOrganizationId,
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

    private async Task InsertStudentArtProgramAssociationAsync(
        long documentId,
        long educationOrganizationDocumentId,
        long educationOrganizationId,
        long programDocumentId,
        long programEducationOrganizationId,
        string programName,
        long programTypeDescriptorId,
        long studentDocumentId,
        string studentUniqueId,
        DateOnly beginDate
    )
    {
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO "sample"."StudentArtProgramAssociation" (
                "DocumentId",
                "EducationOrganization_DocumentId",
                "EducationOrganization_EducationOrganizationId",
                "ProgramProgram_DocumentId",
                "ProgramProgram_EducationOrganizationId",
                "ProgramProgram_ProgramName",
                "ProgramProgram_ProgramTypeDescriptor_DescriptorId",
                "Student_DocumentId",
                "Student_StudentUniqueId",
                "BeginDate",
                "PrivateArtProgram"
            )
            VALUES (
                @documentId,
                @educationOrganizationDocumentId,
                @educationOrganizationId,
                @programDocumentId,
                @programEducationOrganizationId,
                @programName,
                @programTypeDescriptorId,
                @studentDocumentId,
                @studentUniqueId,
                @beginDate,
                @privateArtProgram
            );
            """,
            new NpgsqlParameter("documentId", documentId),
            new NpgsqlParameter("educationOrganizationDocumentId", educationOrganizationDocumentId),
            new NpgsqlParameter("educationOrganizationId", educationOrganizationId),
            new NpgsqlParameter("programDocumentId", programDocumentId),
            new NpgsqlParameter("programEducationOrganizationId", programEducationOrganizationId),
            new NpgsqlParameter("programName", programName),
            new NpgsqlParameter("programTypeDescriptorId", programTypeDescriptorId),
            new NpgsqlParameter("studentDocumentId", studentDocumentId),
            new NpgsqlParameter("studentUniqueId", studentUniqueId),
            new NpgsqlParameter("beginDate", beginDate),
            new NpgsqlParameter("privateArtProgram", false)
        );
    }

    private async Task InsertGeneralStudentProgramAssociationIdentityAsync(
        long documentId,
        DateOnly beginDate,
        long educationOrganizationId,
        long programEducationOrganizationId,
        string programName,
        long programTypeDescriptorId,
        string studentUniqueId
    )
    {
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO "edfi"."GeneralStudentProgramAssociationIdentity" (
                "DocumentId",
                "BeginDate",
                "EducationOrganizationReferenceEducationOrganizationId",
                "ProgramReferenceEducationOrganizationId",
                "ProgramReferenceProgramName",
                "ProgramReferenceProgramTypeDescriptor",
                "StudentReferenceStudentUniqueId",
                "Discriminator"
            )
            VALUES (
                @documentId,
                @beginDate,
                @educationOrganizationId,
                @programEducationOrganizationId,
                @programName,
                @programTypeDescriptorId,
                @studentUniqueId,
                @discriminator
            )
            ON CONFLICT ("DocumentId") DO NOTHING;
            """,
            new NpgsqlParameter("documentId", documentId),
            new NpgsqlParameter("beginDate", beginDate),
            new NpgsqlParameter("educationOrganizationId", educationOrganizationId),
            new NpgsqlParameter("programEducationOrganizationId", programEducationOrganizationId),
            new NpgsqlParameter("programName", programName),
            new NpgsqlParameter("programTypeDescriptorId", programTypeDescriptorId),
            new NpgsqlParameter("studentUniqueId", studentUniqueId),
            new NpgsqlParameter("discriminator", "Sample:StudentArtProgramAssociation")
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

    private static string FormatDate(DateOnly value) =>
        value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private async Task RunWithDisabledTriggerAsync(
        string schema,
        string tableName,
        string triggerName,
        Func<Task> action
    )
    {
        await _database.ExecuteNonQueryAsync(
            $"""ALTER TABLE "{schema}"."{tableName}" DISABLE TRIGGER "{triggerName}";"""
        );

        try
        {
            await action();
        }
        finally
        {
            await _database.ExecuteNonQueryAsync(
                $"""ALTER TABLE "{schema}"."{tableName}" ENABLE TRIGGER "{triggerName}";"""
            );
        }
    }

    private async Task<AuthoritativeSampleStudentSectionAssociationPersistedState> ReadPersistedStateAsync(
        Guid documentUuid
    )
    {
        var document = await ReadDocumentAsync(documentUuid);

        return new(
            Document: document,
            Association: await ReadAssociationAsync(document.DocumentId),
            AssociationExtension: await ReadAssociationExtensionAsync(document.DocumentId),
            RelatedGeneralStudentProgramAssociations: await ReadProgramAssociationsAsync(document.DocumentId)
        );
    }

    private async Task<AuthoritativeSampleStudentSectionAssociationDocumentRow> ReadDocumentAsync(
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
            ? new AuthoritativeSampleStudentSectionAssociationDocumentRow(
                AuthoritativeSampleStudentSectionAssociationIntegrationTestSupport.GetInt64(
                    rows[0],
                    "DocumentId"
                ),
                AuthoritativeSampleStudentSectionAssociationIntegrationTestSupport.GetGuid(
                    rows[0],
                    "DocumentUuid"
                ),
                AuthoritativeSampleStudentSectionAssociationIntegrationTestSupport.GetInt16(
                    rows[0],
                    "ResourceKeyId"
                ),
                AuthoritativeSampleStudentSectionAssociationIntegrationTestSupport.GetInt64(
                    rows[0],
                    "ContentVersion"
                )
            )
            : throw new InvalidOperationException(
                $"Expected exactly one document row for '{documentUuid}', but found {rows.Count}."
            );
    }

    private async Task<AuthoritativeSampleStudentSectionAssociationRow> ReadAssociationAsync(long documentId)
    {
        var rows = await _database.QueryRowsAsync(
            """
            SELECT
                "DocumentId",
                "Section_DocumentId",
                "Section_LocalCourseCode",
                "Section_SchoolId",
                "Section_SchoolYear",
                "Section_SectionIdentifier",
                "Section_SessionName",
                "Student_DocumentId",
                "Student_StudentUniqueId",
                "BeginDate"
            FROM "edfi"."StudentSectionAssociation"
            WHERE "DocumentId" = @documentId;
            """,
            new NpgsqlParameter("documentId", documentId)
        );

        return rows.Count == 1
            ? new AuthoritativeSampleStudentSectionAssociationRow(
                AuthoritativeSampleStudentSectionAssociationIntegrationTestSupport.GetInt64(
                    rows[0],
                    "DocumentId"
                ),
                AuthoritativeSampleStudentSectionAssociationIntegrationTestSupport.GetInt64(
                    rows[0],
                    "Section_DocumentId"
                ),
                AuthoritativeSampleStudentSectionAssociationIntegrationTestSupport.GetString(
                    rows[0],
                    "Section_LocalCourseCode"
                ),
                AuthoritativeSampleStudentSectionAssociationIntegrationTestSupport.GetInt64(
                    rows[0],
                    "Section_SchoolId"
                ),
                AuthoritativeSampleStudentSectionAssociationIntegrationTestSupport.GetInt32(
                    rows[0],
                    "Section_SchoolYear"
                ),
                AuthoritativeSampleStudentSectionAssociationIntegrationTestSupport.GetString(
                    rows[0],
                    "Section_SectionIdentifier"
                ),
                AuthoritativeSampleStudentSectionAssociationIntegrationTestSupport.GetString(
                    rows[0],
                    "Section_SessionName"
                ),
                AuthoritativeSampleStudentSectionAssociationIntegrationTestSupport.GetInt64(
                    rows[0],
                    "Student_DocumentId"
                ),
                AuthoritativeSampleStudentSectionAssociationIntegrationTestSupport.GetString(
                    rows[0],
                    "Student_StudentUniqueId"
                ),
                AuthoritativeSampleStudentSectionAssociationIntegrationTestSupport.GetDateOnly(
                    rows[0],
                    "BeginDate"
                )
            )
            : throw new InvalidOperationException(
                $"Expected exactly one StudentSectionAssociation row for document id '{documentId}', but found {rows.Count}."
            );
    }

    private async Task<AuthoritativeSampleStudentSectionAssociationExtensionRow> ReadAssociationExtensionAsync(
        long documentId
    )
    {
        var rows = await _database.QueryRowsAsync(
            """
            SELECT "DocumentId"
            FROM "sample"."StudentSectionAssociationExtension"
            WHERE "DocumentId" = @documentId;
            """,
            new NpgsqlParameter("documentId", documentId)
        );

        return rows.Count == 1
            ? new AuthoritativeSampleStudentSectionAssociationExtensionRow(
                AuthoritativeSampleStudentSectionAssociationIntegrationTestSupport.GetInt64(
                    rows[0],
                    "DocumentId"
                )
            )
            : throw new InvalidOperationException(
                $"Expected exactly one StudentSectionAssociationExtension row for document id '{documentId}', but found {rows.Count}."
            );
    }

    private async Task<
        IReadOnlyList<AuthoritativeSampleStudentSectionAssociationProgramAssociationRow>
    > ReadProgramAssociationsAsync(long documentId)
    {
        var rows = await _database.QueryRowsAsync(
            $"""
            SELECT
                "CollectionItemId",
                "Ordinal",
                "StudentSectionAssociation_DocumentId",
                "RelatedGeneralStudentProgramAssociation_DocumentId",
                "RelatedGeneralStudentProgramAssociation_BeginDate",
                "RelatedGeneralStudentProgramAssociation_EducationOrganizationId",
                "RelatedGeneralStudentProgramAssociation_ProgramEduca_79002f6014",
                "RelatedGeneralStudentProgramAssociation_ProgramName",
                "RelatedGeneralStudentProgramAssociation_ProgramTypeD_abfb5157a1",
                "RelatedGeneralStudentProgramAssociation_StudentUniqueId"
            FROM "{_relatedGeneralStudentProgramAssociationsTable.Table.Schema.Value}"."{_relatedGeneralStudentProgramAssociationsTable.Table.Name}"
            WHERE "StudentSectionAssociation_DocumentId" = @documentId
            ORDER BY "Ordinal";
            """,
            new NpgsqlParameter("documentId", documentId)
        );

        return rows.Select(row => new AuthoritativeSampleStudentSectionAssociationProgramAssociationRow(
                AuthoritativeSampleStudentSectionAssociationIntegrationTestSupport.GetInt64(
                    row,
                    "CollectionItemId"
                ),
                AuthoritativeSampleStudentSectionAssociationIntegrationTestSupport.GetInt32(row, "Ordinal"),
                AuthoritativeSampleStudentSectionAssociationIntegrationTestSupport.GetInt64(
                    row,
                    "StudentSectionAssociation_DocumentId"
                ),
                AuthoritativeSampleStudentSectionAssociationIntegrationTestSupport.GetInt64(
                    row,
                    "RelatedGeneralStudentProgramAssociation_DocumentId"
                ),
                AuthoritativeSampleStudentSectionAssociationIntegrationTestSupport.GetDateOnly(
                    row,
                    "RelatedGeneralStudentProgramAssociation_BeginDate"
                ),
                AuthoritativeSampleStudentSectionAssociationIntegrationTestSupport.GetInt64(
                    row,
                    "RelatedGeneralStudentProgramAssociation_EducationOrganizationId"
                ),
                AuthoritativeSampleStudentSectionAssociationIntegrationTestSupport.GetInt64(
                    row,
                    "RelatedGeneralStudentProgramAssociation_ProgramEduca_79002f6014"
                ),
                AuthoritativeSampleStudentSectionAssociationIntegrationTestSupport.GetString(
                    row,
                    "RelatedGeneralStudentProgramAssociation_ProgramName"
                ),
                AuthoritativeSampleStudentSectionAssociationIntegrationTestSupport.GetInt64(
                    row,
                    "RelatedGeneralStudentProgramAssociation_ProgramTypeD_abfb5157a1"
                ),
                AuthoritativeSampleStudentSectionAssociationIntegrationTestSupport.GetString(
                    row,
                    "RelatedGeneralStudentProgramAssociation_StudentUniqueId"
                )
            ))
            .ToArray();
    }
}

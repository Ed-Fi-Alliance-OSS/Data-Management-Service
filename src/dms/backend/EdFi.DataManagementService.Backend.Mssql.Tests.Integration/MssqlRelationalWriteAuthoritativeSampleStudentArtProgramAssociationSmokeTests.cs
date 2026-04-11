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

file sealed class MssqlStudentArtProgramAssociationAllowAllResourceAuthorizationHandler
    : IResourceAuthorizationHandler
{
    public Task<ResourceAuthorizationResult> Authorize(
        DocumentSecurityElements documentSecurityElements,
        OperationType operationType,
        TraceId traceId
    ) => Task.FromResult<ResourceAuthorizationResult>(new ResourceAuthorizationResult.Authorized());
}

file sealed class MssqlStudentArtProgramAssociationNoOpUpdateCascadeHandler : IUpdateCascadeHandler
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

file static class MssqlStudentArtProgramAssociationIntegrationTestSupport
{
    public const string FixtureRelativePath = "src/dms/backend/Fixtures/authoritative/sample";

    public static ServiceProvider CreateServiceProvider()
    {
        ServiceCollection services = [];

        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddScoped<IDmsInstanceSelection, DmsInstanceSelection>();
        services.Configure<DatabaseOptions>(options => options.IsolationLevel = IsolationLevel.ReadCommitted);
        services.AddTestReadableProfileProjector();
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
        long programTypeDescriptorId
    )
    {
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
                    reference.Path != new JsonPath("$.programReference")
                ),
                CreateProgramDocumentReference(requestBody, programTypeDescriptorId),
            ],
        };
    }

    public static string FormatReferenceFailure(UpsertResult.UpsertFailureReference failure) =>
        FormatReferenceFailure(failure.InvalidDocumentReferences, failure.InvalidDescriptorReferences);

    public static string FormatReferenceFailure(UpdateResult.UpdateFailureReference failure) =>
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

    private static DocumentReference CreateProgramDocumentReference(
        JsonNode requestBody,
        long programTypeDescriptorId
    )
    {
        var educationOrganizationId = requestBody["programReference"]
            ?["educationOrganizationId"]?.GetValue<long>()
            .ToString(CultureInfo.InvariantCulture);
        var programName = requestBody["programReference"]?["programName"]?.GetValue<string>();

        if (string.IsNullOrWhiteSpace(educationOrganizationId) || string.IsNullOrWhiteSpace(programName))
        {
            throw new InvalidOperationException(
                "Expected programReference to include educationOrganizationId and programName."
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
                programTypeDescriptorId.ToString(CultureInfo.InvariantCulture)
            ),
        ]);

        return new(
            ResourceInfo: programResourceInfo,
            DocumentIdentity: programIdentity,
            ReferentialId: ReferentialIdCalculator.ReferentialIdFrom(programResourceInfo, programIdentity),
            Path: new JsonPath("$.programReference")
        );
    }

    public static long GetInt64(IReadOnlyDictionary<string, object?> row, string columnName) =>
        Convert.ToInt64(GetRequiredValue(row, columnName), CultureInfo.InvariantCulture);

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

        return string.Join("; ", documentFailures.Concat(descriptorFailures));
    }

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

internal sealed record MssqlStudentArtProgramAssociationSeedData(
    long SchoolDocumentId,
    long StudentDocumentId,
    long ExtracurricularProgramTypeDescriptorId,
    long RoboticsClubProgramDocumentId
);

internal sealed record MssqlStudentArtProgramAssociationPersistedState(
    long DocumentId,
    long EducationOrganizationDocumentId,
    long EducationOrganizationId,
    long ProgramDocumentId,
    long ProgramEducationOrganizationId,
    string ProgramName,
    long ProgramTypeDescriptorId,
    long StudentDocumentId,
    string StudentUniqueId,
    DateOnly BeginDate,
    bool PrivateArtProgram
);

[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[NonParallelizable]
public class Given_A_Mssql_Relational_Write_Smoke_With_The_Authoritative_Sample_StudentArtProgramAssociation_Fixture
{
    private const long EducationOrganizationId = 100;
    private const string StudentUniqueId = "10001";
    private const string RoboticsClubProgramName = "Robotics Club";
    private const string ExtracurricularProgramTypeDescriptorUri =
        "uri://ed-fi.org/ProgramTypeDescriptor#Extracurricular";

    private const string CreateRequestBodyJson = """
        {
          "beginDate": "2024-08-20",
          "educationOrganizationReference": {
            "educationOrganizationId": 100
          },
          "privateArtProgram": true,
          "programReference": {
            "educationOrganizationId": 100,
            "programName": "Robotics Club",
            "programTypeDescriptor": "uri://ed-fi.org/ProgramTypeDescriptor#Extracurricular"
          },
          "studentReference": {
            "studentUniqueId": "10001"
          },
          "styles": [
            {
              "style": "Abstract"
            }
          ]
        }
        """;

    private static readonly DateOnly BeginDate = new(2024, 8, 20);
    private static readonly DocumentUuid AssociationDocumentUuid = new(
        Guid.Parse("abababab-0000-0000-0000-000000000001")
    );

    private MssqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private MssqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;
    private ResourceInfo _resourceInfo = null!;
    private ResourceSchema _resourceSchema = null!;
    private MssqlStudentArtProgramAssociationSeedData _seedData = null!;
    private DocumentInfo _createDocumentInfo = null!;
    private UpsertResult _createResult = null!;
    private MssqlStudentArtProgramAssociationPersistedState _stateAfterCreate = null!;

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
            MssqlStudentArtProgramAssociationIntegrationTestSupport.FixtureRelativePath
        );
        _mappingSet = new MappingSetCompiler().Compile(_fixture.ModelSet);
        _database = await MssqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
        _serviceProvider = MssqlStudentArtProgramAssociationIntegrationTestSupport.CreateServiceProvider();

        var (projectSchema, resourceSchema) =
            MssqlStudentArtProgramAssociationIntegrationTestSupport.GetResourceSchema(
                _fixture.EffectiveSchemaSet,
                "sample",
                "StudentArtProgramAssociation"
            );

        _resourceInfo = MssqlStudentArtProgramAssociationIntegrationTestSupport.CreateResourceInfo(
            projectSchema,
            resourceSchema
        );
        _resourceSchema = resourceSchema;
        _seedData = await SeedReferenceDataAsync();

        var createRequestBody = JsonNode.Parse(CreateRequestBodyJson)!;
        _createDocumentInfo = MssqlStudentArtProgramAssociationIntegrationTestSupport.CreateDocumentInfo(
            createRequestBody,
            _resourceInfo,
            _resourceSchema,
            _mappingSet,
            _seedData.ExtracurricularProgramTypeDescriptorId
        );

        _createResult = await ExecuteCreateAsync(
            createRequestBody,
            _createDocumentInfo,
            AssociationDocumentUuid,
            "mssql-student-art-program-association-create"
        );

        if (_createResult is UpsertResult.UpsertFailureReference createReferenceFailure)
        {
            Assert.Fail(
                $"Create reference failure: {MssqlStudentArtProgramAssociationIntegrationTestSupport.FormatReferenceFailure(createReferenceFailure)}"
            );
        }

        _createResult.Should().BeOfType<UpsertResult.InsertSuccess>();
        _stateAfterCreate = await ReadPersistedStateAsync(AssociationDocumentUuid.Value);
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
    public void It_extracts_descriptor_backed_root_reference_members_via_the_shared_document_info_helper()
    {
        _createDocumentInfo
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
                    "$.programReference.programTypeDescriptor",
                    "ProgramTypeDescriptor",
                    ExtracurricularProgramTypeDescriptorUri.ToLowerInvariant()
                )
            );
    }

    [Test]
    public void It_populates_root_reference_columns_from_descriptor_backed_reference_members_on_create()
    {
        _createResult.Should().BeOfType<UpsertResult.InsertSuccess>();
        _stateAfterCreate
            .Should()
            .Be(
                new MssqlStudentArtProgramAssociationPersistedState(
                    DocumentId: _stateAfterCreate.DocumentId,
                    EducationOrganizationDocumentId: _seedData.SchoolDocumentId,
                    EducationOrganizationId: EducationOrganizationId,
                    ProgramDocumentId: _seedData.RoboticsClubProgramDocumentId,
                    ProgramEducationOrganizationId: EducationOrganizationId,
                    ProgramName: RoboticsClubProgramName,
                    ProgramTypeDescriptorId: _seedData.ExtracurricularProgramTypeDescriptorId,
                    StudentDocumentId: _seedData.StudentDocumentId,
                    StudentUniqueId: StudentUniqueId,
                    BeginDate: BeginDate,
                    PrivateArtProgram: true
                )
            );
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
            UpdateCascadeHandler: new MssqlStudentArtProgramAssociationNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new MssqlStudentArtProgramAssociationAllowAllResourceAuthorizationHandler(),
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
                    InstanceName: "MssqlRelationalWriteAuthoritativeSampleStudentArtProgramAssociation",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );
    }

    private async Task<MssqlStudentArtProgramAssociationSeedData> SeedReferenceDataAsync()
    {
        var educationOrganizationResourceKeyId = await GetResourceKeyIdAsync(
            "Ed-Fi",
            "EducationOrganization"
        );
        var schoolResourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "School");
        var studentResourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "Student");
        var programResourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "Program");
        var programTypeDescriptorResourceKeyId = await GetResourceKeyIdAsync(
            "Ed-Fi",
            "ProgramTypeDescriptor"
        );

        var schoolDocumentId = await InsertDocumentAsync(
            Guid.Parse("11111111-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            schoolResourceKeyId
        );
        await ExecuteWithTriggersTemporarilyDisabledAsync(
            "edfi",
            "School",
            async () =>
                await InsertSchoolAsync(schoolDocumentId, (int)EducationOrganizationId, "Alpha Academy")
        );
        await InsertEducationOrganizationIdentityAsync(
            schoolDocumentId,
            (int)EducationOrganizationId,
            "Ed-Fi:School"
        );
        await UpsertReferentialIdentityAsync(
            MssqlStudentArtProgramAssociationIntegrationTestSupport.CreateReferentialId(
                ("Ed-Fi", "School", false),
                ("$.schoolId", EducationOrganizationId.ToString(CultureInfo.InvariantCulture))
            ),
            schoolDocumentId,
            schoolResourceKeyId
        );
        await UpsertReferentialIdentityAsync(
            MssqlStudentArtProgramAssociationIntegrationTestSupport.CreateReferentialId(
                ("Ed-Fi", "EducationOrganization", false),
                ("$.educationOrganizationId", EducationOrganizationId.ToString(CultureInfo.InvariantCulture))
            ),
            schoolDocumentId,
            educationOrganizationResourceKeyId
        );

        var studentDocumentId = await InsertDocumentAsync(
            Guid.Parse("22222222-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            studentResourceKeyId
        );
        await InsertStudentAsync(studentDocumentId, StudentUniqueId, "Casey", "Cole");
        await UpsertReferentialIdentityAsync(
            MssqlStudentArtProgramAssociationIntegrationTestSupport.CreateReferentialId(
                ("Ed-Fi", "Student", false),
                ("$.studentUniqueId", StudentUniqueId)
            ),
            studentDocumentId,
            studentResourceKeyId
        );

        var extracurricularProgramTypeDescriptorId = await InsertDescriptorAsync(
            Guid.Parse("33333333-cccc-cccc-cccc-ccccccccccc1"),
            programTypeDescriptorResourceKeyId,
            "Ed-Fi:ProgramTypeDescriptor",
            ExtracurricularProgramTypeDescriptorUri,
            "uri://ed-fi.org/ProgramTypeDescriptor",
            "Extracurricular",
            "Extracurricular"
        );
        await UpsertReferentialIdentityAsync(
            MssqlStudentArtProgramAssociationIntegrationTestSupport.CreateDescriptorReferentialId(
                "Ed-Fi",
                "ProgramTypeDescriptor",
                ExtracurricularProgramTypeDescriptorUri
            ),
            extracurricularProgramTypeDescriptorId,
            programTypeDescriptorResourceKeyId
        );

        var roboticsClubProgramDocumentId = await InsertDocumentAsync(
            Guid.Parse("44444444-dddd-dddd-dddd-ddddddddddd1"),
            programResourceKeyId
        );
        await InsertProgramAsync(
            roboticsClubProgramDocumentId,
            schoolDocumentId,
            (int)EducationOrganizationId,
            extracurricularProgramTypeDescriptorId,
            "PRG-01",
            RoboticsClubProgramName
        );
        await UpsertReferentialIdentityAsync(
            MssqlStudentArtProgramAssociationIntegrationTestSupport.CreateReferentialId(
                ("Ed-Fi", "Program", false),
                (
                    "$.educationOrganizationReference.educationOrganizationId",
                    EducationOrganizationId.ToString(CultureInfo.InvariantCulture)
                ),
                ("$.programName", RoboticsClubProgramName),
                (
                    "$.programTypeDescriptor",
                    extracurricularProgramTypeDescriptorId.ToString(CultureInfo.InvariantCulture)
                )
            ),
            roboticsClubProgramDocumentId,
            programResourceKeyId
        );

        return new(
            SchoolDocumentId: schoolDocumentId,
            StudentDocumentId: studentDocumentId,
            ExtracurricularProgramTypeDescriptorId: extracurricularProgramTypeDescriptorId,
            RoboticsClubProgramDocumentId: roboticsClubProgramDocumentId
        );
    }

    private async Task<MssqlStudentArtProgramAssociationPersistedState> ReadPersistedStateAsync(
        Guid documentUuid
    )
    {
        var row = (
            await _database.QueryRowsAsync(
                """
                SELECT
                    association.[DocumentId],
                    association.[EducationOrganization_DocumentId],
                    association.[EducationOrganization_EducationOrganizationId],
                    association.[ProgramProgram_DocumentId],
                    association.[ProgramProgram_EducationOrganizationId],
                    association.[ProgramProgram_ProgramName],
                    association.[ProgramProgram_ProgramTypeDescriptor_DescriptorId],
                    association.[Student_DocumentId],
                    association.[Student_StudentUniqueId],
                    association.[BeginDate],
                    association.[PrivateArtProgram]
                FROM [dms].[Document] document
                INNER JOIN [sample].[StudentArtProgramAssociation] association
                    ON association.[DocumentId] = document.[DocumentId]
                WHERE document.[DocumentUuid] = @documentUuid;
                """,
                new SqlParameter("@documentUuid", documentUuid)
            )
        ).Single();

        return new(
            DocumentId: MssqlStudentArtProgramAssociationIntegrationTestSupport.GetInt64(row, "DocumentId"),
            EducationOrganizationDocumentId: MssqlStudentArtProgramAssociationIntegrationTestSupport.GetInt64(
                row,
                "EducationOrganization_DocumentId"
            ),
            EducationOrganizationId: MssqlStudentArtProgramAssociationIntegrationTestSupport.GetInt64(
                row,
                "EducationOrganization_EducationOrganizationId"
            ),
            ProgramDocumentId: MssqlStudentArtProgramAssociationIntegrationTestSupport.GetInt64(
                row,
                "ProgramProgram_DocumentId"
            ),
            ProgramEducationOrganizationId: MssqlStudentArtProgramAssociationIntegrationTestSupport.GetInt64(
                row,
                "ProgramProgram_EducationOrganizationId"
            ),
            ProgramName: MssqlStudentArtProgramAssociationIntegrationTestSupport.GetString(
                row,
                "ProgramProgram_ProgramName"
            ),
            ProgramTypeDescriptorId: MssqlStudentArtProgramAssociationIntegrationTestSupport.GetInt64(
                row,
                "ProgramProgram_ProgramTypeDescriptor_DescriptorId"
            ),
            StudentDocumentId: MssqlStudentArtProgramAssociationIntegrationTestSupport.GetInt64(
                row,
                "Student_DocumentId"
            ),
            StudentUniqueId: MssqlStudentArtProgramAssociationIntegrationTestSupport.GetString(
                row,
                "Student_StudentUniqueId"
            ),
            BeginDate: MssqlStudentArtProgramAssociationIntegrationTestSupport.GetDateOnly(row, "BeginDate"),
            PrivateArtProgram: MssqlStudentArtProgramAssociationIntegrationTestSupport.GetBoolean(
                row,
                "PrivateArtProgram"
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
            INSERT INTO [edfi].[EducationOrganizationIdentity] ([DocumentId], [EducationOrganizationId], [Discriminator])
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
            new SqlParameter("@birthDate", new DateOnly(2010, 1, 1)),
            new SqlParameter("@firstName", firstName),
            new SqlParameter("@lastSurname", lastSurname),
            new SqlParameter("@studentUniqueId", studentUniqueId)
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
            INSERT INTO [edfi].[Program] (
                [DocumentId],
                [EducationOrganization_DocumentId],
                [EducationOrganization_EducationOrganizationId],
                [ProgramTypeDescriptor_DescriptorId],
                [ProgramId],
                [ProgramName]
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
            new SqlParameter("@documentId", documentId),
            new SqlParameter("@educationOrganizationDocumentId", educationOrganizationDocumentId),
            new SqlParameter("@educationOrganizationId", educationOrganizationId),
            new SqlParameter("@programTypeDescriptorId", programTypeDescriptorId),
            new SqlParameter("@programId", programId),
            new SqlParameter("@programName", programName)
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
}

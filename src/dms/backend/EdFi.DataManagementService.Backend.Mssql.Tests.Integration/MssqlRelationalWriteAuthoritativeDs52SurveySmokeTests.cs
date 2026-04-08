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

file sealed class MssqlSurveyRuntimeAllowAllResourceAuthorizationHandler : IResourceAuthorizationHandler
{
    public Task<ResourceAuthorizationResult> Authorize(
        DocumentSecurityElements documentSecurityElements,
        OperationType operationType,
        TraceId traceId
    ) => Task.FromResult<ResourceAuthorizationResult>(new ResourceAuthorizationResult.Authorized());
}

file sealed class MssqlSurveyRuntimeNoOpUpdateCascadeHandler : IUpdateCascadeHandler
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

file static class MssqlSurveyRuntimeIntegrationTestSupport
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
        ResourceSchema resourceSchema
    )
    {
        var (documentIdentity, superclassIdentity) = resourceSchema.ExtractIdentities(
            requestBody,
            NullLogger.Instance
        );
        var (documentReferences, documentReferenceArrays) = resourceSchema.ExtractReferences(
            requestBody,
            NullLogger.Instance,
            ReferenceExtractionMode.RelationalWriteValidation
        );
        var descriptorReferences = resourceSchema.ExtractDescriptors(requestBody, NullLogger.Instance);

        return new(
            DocumentIdentity: documentIdentity,
            ReferentialId: ReferentialIdCalculator.ReferentialIdFrom(resourceInfo, documentIdentity),
            DocumentReferences: [.. documentReferences],
            DocumentReferenceArrays: [.. documentReferenceArrays],
            DescriptorReferences: [.. descriptorReferences],
            SuperclassIdentity: superclassIdentity
        );
    }

    public static string FormatReferenceFailure(UpsertResult.UpsertFailureReference failure) =>
        FormatReferenceFailure(failure.InvalidDocumentReferences, failure.InvalidDescriptorReferences);

    public static string FormatReferenceFailure(UpdateResult.UpdateFailureReference failure) =>
        FormatReferenceFailure(failure.InvalidDocumentReferences, failure.InvalidDescriptorReferences);

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
}

internal sealed record MssqlSurveyRuntimeSeedData(
    long SchoolYearTypeDocumentId,
    long FallSessionDocumentId,
    long SpringSessionDocumentId
);

internal sealed record SurveyRuntimePersistedState(
    long DocumentId,
    long SchoolYearDocumentId,
    int SchoolYearUnified,
    long SessionDocumentId,
    int SessionSchoolId,
    string SessionSessionName,
    string Namespace,
    string SurveyIdentifier,
    string SurveyTitle
);

[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[NonParallelizable]
public class Given_A_Mssql_Relational_Write_Propagated_Reference_Identity_Runtime_With_The_Authoritative_DS52_Survey_Fixture
{
    private const int SchoolId = 100;
    private const int SchoolYear = 2025;
    private const string SurveyNamespace = "uri://ed-fi.org/Survey";
    private const string SurveyIdentifier = "student-climate";
    private const string SurveyTitle = "Student Climate Survey";
    private const string UpdatedSurveyTitle = "Student Climate Survey Updated";
    private const string FallSessionName = "Fall";
    private const string SpringSessionName = "Spring";

    private const string CreateRequestBodyJson = """
        {
          "namespace": "uri://ed-fi.org/Survey",
          "surveyIdentifier": "student-climate",
          "surveyTitle": "Student Climate Survey",
          "schoolYearTypeReference": {
            "schoolYear": 2025
          },
          "sessionReference": {
            "schoolId": 100,
            "schoolYear": 2025,
            "sessionName": "Fall"
          }
        }
        """;

    private const string ChangedUpdateRequestBodyJson = """
        {
          "namespace": "uri://ed-fi.org/Survey",
          "surveyIdentifier": "student-climate",
          "surveyTitle": "Student Climate Survey Updated",
          "schoolYearTypeReference": {
            "schoolYear": 2025
          },
          "sessionReference": {
            "schoolId": 100,
            "schoolYear": 2025,
            "sessionName": "Spring"
          }
        }
        """;

    private static readonly DocumentUuid SurveyDocumentUuid = new(
        Guid.Parse("dddddddd-0000-0000-0000-000000000001")
    );

    private MssqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private MssqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;
    private ResourceInfo _resourceInfo = null!;
    private ResourceSchema _resourceSchema = null!;
    private MssqlSurveyRuntimeSeedData _seedData = null!;
    private UpsertResult _createResult = null!;
    private UpdateResult _changedUpdateResult = null!;
    private SurveyRuntimePersistedState _stateAfterCreate = null!;
    private SurveyRuntimePersistedState _stateAfterChangedUpdate = null!;

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
            MssqlSurveyRuntimeIntegrationTestSupport.FixtureRelativePath
        );
        _mappingSet = new MappingSetCompiler().Compile(_fixture.ModelSet);
        _database = await MssqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
        _serviceProvider = MssqlSurveyRuntimeIntegrationTestSupport.CreateServiceProvider();

        var (projectSchema, resourceSchema) = MssqlSurveyRuntimeIntegrationTestSupport.GetResourceSchema(
            _fixture.EffectiveSchemaSet,
            "ed-fi",
            "Survey"
        );

        _resourceInfo = MssqlSurveyRuntimeIntegrationTestSupport.CreateResourceInfo(
            projectSchema,
            resourceSchema
        );
        _resourceSchema = resourceSchema;
        _seedData = await SeedReferenceDataAsync();
        await SeedReferenceResolutionRowsAsync();

        _createResult = await ExecuteCreateAsync(
            CreateRequestBodyJson,
            SurveyDocumentUuid,
            "mssql-propagated-reference-identity-runtime-create"
        );

        if (_createResult is UpsertResult.UpsertFailureReference createReferenceFailure)
        {
            Assert.Fail(
                $"Create reference failure: {MssqlSurveyRuntimeIntegrationTestSupport.FormatReferenceFailure(createReferenceFailure)}"
            );
        }

        _createResult.Should().BeOfType<UpsertResult.InsertSuccess>();
        _stateAfterCreate = await ReadPersistedStateAsync(SurveyDocumentUuid.Value);

        _changedUpdateResult = await ExecuteUpdateAsync(
            ChangedUpdateRequestBodyJson,
            "mssql-propagated-reference-identity-runtime-changed-update"
        );

        if (_changedUpdateResult is UpdateResult.UpdateFailureReference changedUpdateReferenceFailure)
        {
            Assert.Fail(
                $"Changed update reference failure: {MssqlSurveyRuntimeIntegrationTestSupport.FormatReferenceFailure(changedUpdateReferenceFailure)}"
            );
        }

        _changedUpdateResult.Should().BeOfType<UpdateResult.UpdateSuccess>();
        _stateAfterChangedUpdate = await ReadPersistedStateAsync(SurveyDocumentUuid.Value);
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
    public void It_populates_persisted_reference_identity_columns_on_create()
    {
        _createResult.Should().BeOfType<UpsertResult.InsertSuccess>();
        _stateAfterCreate
            .Should()
            .Be(
                new SurveyRuntimePersistedState(
                    DocumentId: _stateAfterCreate.DocumentId,
                    SchoolYearDocumentId: _seedData.SchoolYearTypeDocumentId,
                    SchoolYearUnified: SchoolYear,
                    SessionDocumentId: _seedData.FallSessionDocumentId,
                    SessionSchoolId: SchoolId,
                    SessionSessionName: FallSessionName,
                    Namespace: SurveyNamespace,
                    SurveyIdentifier: SurveyIdentifier,
                    SurveyTitle: SurveyTitle
                )
            );
    }

    [Test]
    public void It_repopulates_persisted_reference_identity_columns_from_resolved_references_on_changed_put()
    {
        _changedUpdateResult.Should().BeOfType<UpdateResult.UpdateSuccess>();
        _stateAfterChangedUpdate
            .Should()
            .Be(
                new SurveyRuntimePersistedState(
                    DocumentId: _stateAfterCreate.DocumentId,
                    SchoolYearDocumentId: _seedData.SchoolYearTypeDocumentId,
                    SchoolYearUnified: SchoolYear,
                    SessionDocumentId: _seedData.SpringSessionDocumentId,
                    SessionSchoolId: SchoolId,
                    SessionSessionName: SpringSessionName,
                    Namespace: SurveyNamespace,
                    SurveyIdentifier: SurveyIdentifier,
                    SurveyTitle: UpdatedSurveyTitle
                )
            );
        _stateAfterChangedUpdate.SessionDocumentId.Should().NotBe(_stateAfterCreate.SessionDocumentId);
        _stateAfterChangedUpdate.SessionSessionName.Should().NotBe(_stateAfterCreate.SessionSessionName);
        _stateAfterChangedUpdate.SurveyTitle.Should().NotBe(_stateAfterCreate.SurveyTitle);
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
            DocumentInfo: MssqlSurveyRuntimeIntegrationTestSupport.CreateDocumentInfo(
                requestBody,
                _resourceInfo,
                _resourceSchema
            ),
            MappingSet: _mappingSet,
            EdfiDoc: requestBody,
            Headers: [],
            TraceId: new TraceId(traceId),
            DocumentUuid: documentUuid,
            DocumentSecurityElements: new([], [], [], [], []),
            UpdateCascadeHandler: new MssqlSurveyRuntimeNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new MssqlSurveyRuntimeAllowAllResourceAuthorizationHandler(),
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
            DocumentInfo: MssqlSurveyRuntimeIntegrationTestSupport.CreateDocumentInfo(
                requestBody,
                _resourceInfo,
                _resourceSchema
            ),
            MappingSet: _mappingSet,
            EdfiDoc: requestBody,
            Headers: [],
            TraceId: new TraceId(traceId),
            DocumentUuid: SurveyDocumentUuid,
            DocumentSecurityElements: new([], [], [], [], []),
            UpdateCascadeHandler: new MssqlSurveyRuntimeNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new MssqlSurveyRuntimeAllowAllResourceAuthorizationHandler(),
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
                    InstanceName: "MssqlRelationalWritePropagatedReferenceIdentityRuntime",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );
    }

    private async Task<MssqlSurveyRuntimeSeedData> SeedReferenceDataAsync()
    {
        var schoolYearTypeResourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "SchoolYearType");
        var schoolResourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "School");
        var sessionResourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "Session");
        var termDescriptorResourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "TermDescriptor");

        var fallTermDescriptorDocumentId = await InsertDescriptorAsync(
            Guid.Parse("55555555-0000-0000-0000-000000000001"),
            termDescriptorResourceKeyId,
            "Ed-Fi:TermDescriptor",
            "uri://ed-fi.org/TermDescriptor#Fall",
            "uri://ed-fi.org/TermDescriptor",
            "Fall",
            "Fall"
        );
        var springTermDescriptorDocumentId = await InsertDescriptorAsync(
            Guid.Parse("55555555-0000-0000-0000-000000000002"),
            termDescriptorResourceKeyId,
            "Ed-Fi:TermDescriptor",
            "uri://ed-fi.org/TermDescriptor#Spring",
            "uri://ed-fi.org/TermDescriptor",
            "Spring",
            "Spring"
        );

        var schoolYearTypeDocumentId = await InsertDocumentAsync(
            Guid.Parse("99999999-0000-0000-0000-000000000001"),
            schoolYearTypeResourceKeyId
        );
        await InsertSchoolYearTypeAsync(schoolYearTypeDocumentId, SchoolYear);

        var schoolDocumentId = await InsertDocumentAsync(
            Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"),
            schoolResourceKeyId
        );
        await ExecuteWithTriggersTemporarilyDisabledAsync(
            "edfi",
            "School",
            async () => await InsertSchoolAsync(schoolDocumentId, SchoolId, "Sample High School")
        );

        var fallSessionDocumentId = await InsertDocumentAsync(
            Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001"),
            sessionResourceKeyId
        );
        await InsertSessionAsync(
            fallSessionDocumentId,
            schoolYearTypeDocumentId,
            SchoolYear,
            schoolDocumentId,
            SchoolId,
            fallTermDescriptorDocumentId,
            new DateOnly(2025, 8, 1),
            new DateOnly(2025, 12, 31),
            FallSessionName,
            90
        );

        var springSessionDocumentId = await InsertDocumentAsync(
            Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002"),
            sessionResourceKeyId
        );
        await InsertSessionAsync(
            springSessionDocumentId,
            schoolYearTypeDocumentId,
            SchoolYear,
            schoolDocumentId,
            SchoolId,
            springTermDescriptorDocumentId,
            new DateOnly(2026, 1, 5),
            new DateOnly(2026, 5, 22),
            SpringSessionName,
            95
        );

        return new(schoolYearTypeDocumentId, fallSessionDocumentId, springSessionDocumentId);
    }

    private async Task SeedReferenceResolutionRowsAsync()
    {
        var schoolYearTypeResourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "SchoolYearType");
        var sessionResourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "Session");

        var createRequestBody = JsonNode.Parse(CreateRequestBodyJson)!;
        var createDocumentInfo = MssqlSurveyRuntimeIntegrationTestSupport.CreateDocumentInfo(
            createRequestBody,
            _resourceInfo,
            _resourceSchema
        );

        await UpsertReferentialIdentityAsync(
            createDocumentInfo
                .DocumentReferences.Single(reference => reference.Path.Value == "$.schoolYearTypeReference")
                .ReferentialId,
            _seedData.SchoolYearTypeDocumentId,
            schoolYearTypeResourceKeyId
        );
        await UpsertReferentialIdentityAsync(
            createDocumentInfo
                .DocumentReferences.Single(reference => reference.Path.Value == "$.sessionReference")
                .ReferentialId,
            _seedData.FallSessionDocumentId,
            sessionResourceKeyId
        );

        var changedUpdateRequestBody = JsonNode.Parse(ChangedUpdateRequestBodyJson)!;
        var changedUpdateDocumentInfo = MssqlSurveyRuntimeIntegrationTestSupport.CreateDocumentInfo(
            changedUpdateRequestBody,
            _resourceInfo,
            _resourceSchema
        );

        await UpsertReferentialIdentityAsync(
            changedUpdateDocumentInfo
                .DocumentReferences.Single(reference => reference.Path.Value == "$.schoolYearTypeReference")
                .ReferentialId,
            _seedData.SchoolYearTypeDocumentId,
            schoolYearTypeResourceKeyId
        );
        await UpsertReferentialIdentityAsync(
            changedUpdateDocumentInfo
                .DocumentReferences.Single(reference => reference.Path.Value == "$.sessionReference")
                .ReferentialId,
            _seedData.SpringSessionDocumentId,
            sessionResourceKeyId
        );
    }

    private async Task<SurveyRuntimePersistedState> ReadPersistedStateAsync(Guid documentUuid)
    {
        var row = (
            await _database.QueryRowsAsync(
                """
                SELECT
                    survey.[DocumentId],
                    survey.[SchoolYear_DocumentId],
                    survey.[SchoolYear_Unified],
                    survey.[Session_DocumentId],
                    survey.[Session_SchoolId],
                    survey.[Session_SessionName],
                    survey.[Namespace],
                    survey.[SurveyIdentifier],
                    survey.[SurveyTitle]
                FROM [dms].[Document] document
                INNER JOIN [edfi].[Survey] survey
                    ON survey.[DocumentId] = document.[DocumentId]
                WHERE document.[DocumentUuid] = @documentUuid;
                """,
                new SqlParameter("@documentUuid", documentUuid)
            )
        ).Single();

        return new(
            DocumentId: Convert.ToInt64(row["DocumentId"], CultureInfo.InvariantCulture),
            SchoolYearDocumentId: Convert.ToInt64(row["SchoolYear_DocumentId"], CultureInfo.InvariantCulture),
            SchoolYearUnified: Convert.ToInt32(row["SchoolYear_Unified"], CultureInfo.InvariantCulture),
            SessionDocumentId: Convert.ToInt64(row["Session_DocumentId"], CultureInfo.InvariantCulture),
            SessionSchoolId: Convert.ToInt32(row["Session_SchoolId"], CultureInfo.InvariantCulture),
            SessionSessionName: ReadRequiredString(row["Session_SessionName"]),
            Namespace: ReadRequiredString(row["Namespace"]),
            SurveyIdentifier: ReadRequiredString(row["SurveyIdentifier"]),
            SurveyTitle: ReadRequiredString(row["SurveyTitle"])
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

    private async Task InsertSchoolYearTypeAsync(long documentId, int schoolYear)
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
            new SqlParameter("@currentSchoolYear", true),
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

    private async Task InsertSessionAsync(
        long documentId,
        long schoolYearDocumentId,
        int schoolYear,
        long schoolDocumentId,
        int schoolId,
        long termDescriptorDocumentId,
        DateOnly beginDate,
        DateOnly endDate,
        string sessionName,
        int totalInstructionalDays
    )
    {
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO [edfi].[Session] (
                [DocumentId],
                [SchoolYear_DocumentId],
                [SchoolYear_SchoolYear],
                [School_DocumentId],
                [School_SchoolId],
                [TermDescriptor_DescriptorId],
                [BeginDate],
                [EndDate],
                [SessionName],
                [TotalInstructionalDays]
            )
            VALUES (
                @documentId,
                @schoolYearDocumentId,
                @schoolYear,
                @schoolDocumentId,
                @schoolId,
                @termDescriptorDocumentId,
                @beginDate,
                @endDate,
                @sessionName,
                @totalInstructionalDays
            );
            """,
            new SqlParameter("@documentId", documentId),
            new SqlParameter("@schoolYearDocumentId", schoolYearDocumentId),
            new SqlParameter("@schoolYear", schoolYear),
            new SqlParameter("@schoolDocumentId", schoolDocumentId),
            new SqlParameter("@schoolId", schoolId),
            new SqlParameter("@termDescriptorDocumentId", termDescriptorDocumentId),
            new SqlParameter("@beginDate", beginDate),
            new SqlParameter("@endDate", endDate),
            new SqlParameter("@sessionName", sessionName),
            new SqlParameter("@totalInstructionalDays", totalInstructionalDays)
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

    private static string ReadRequiredString(object? value) =>
        value as string ?? throw new InvalidOperationException("Expected a non-null string value.");
}

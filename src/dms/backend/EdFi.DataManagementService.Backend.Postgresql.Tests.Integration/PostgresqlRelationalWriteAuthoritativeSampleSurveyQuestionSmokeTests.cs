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

file sealed class AuthoritativeSampleSurveyQuestionWriteHostApplicationLifetime : IHostApplicationLifetime
{
    public CancellationToken ApplicationStarted => CancellationToken.None;
    public CancellationToken ApplicationStopping => CancellationToken.None;
    public CancellationToken ApplicationStopped => CancellationToken.None;

    public void StopApplication() { }
}

file sealed class AuthoritativeSampleSurveyQuestionAllowAllResourceAuthorizationHandler
    : IResourceAuthorizationHandler
{
    public Task<ResourceAuthorizationResult> Authorize(
        DocumentSecurityElements documentSecurityElements,
        OperationType operationType,
        TraceId traceId
    ) => Task.FromResult<ResourceAuthorizationResult>(new ResourceAuthorizationResult.Authorized());
}

file sealed class AuthoritativeSampleSurveyQuestionNoOpUpdateCascadeHandler : IUpdateCascadeHandler
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

file static class AuthoritativeSampleSurveyQuestionIntegrationTestSupport
{
    public const string FixtureRelativePath = "src/dms/backend/Fixtures/authoritative/sample";

    public static ServiceProvider CreateServiceProvider()
    {
        ServiceCollection services = [];

        services.AddSingleton<
            IHostApplicationLifetime,
            AuthoritativeSampleSurveyQuestionWriteHostApplicationLifetime
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
}

internal sealed record AuthoritativeSampleSurveyQuestionSeedData(
    long SchoolYearTypeDocumentId,
    long SurveyDocumentId,
    long SurveySectionDocumentId,
    long QuestionFormDescriptorId
);

internal sealed record AuthoritativeSampleSurveyQuestionDocumentRow(
    long DocumentId,
    Guid DocumentUuid,
    short ResourceKeyId,
    long ContentVersion
);

internal sealed record AuthoritativeSampleSurveyQuestionRow(
    long DocumentId,
    string NamespaceUnified,
    string SurveyIdentifierUnified,
    long SurveySectionDocumentId,
    string SurveySectionNamespace,
    string SurveySectionSurveyIdentifier,
    string SurveySectionTitle,
    long SurveyDocumentId,
    string SurveyNamespace,
    string SurveySurveyIdentifier,
    long QuestionFormDescriptorId,
    string QuestionCode,
    string QuestionText
);

internal sealed record AuthoritativeSampleSurveyQuestionMatriceRow(
    long CollectionItemId,
    int Ordinal,
    long SurveyQuestionDocumentId,
    string MatrixElement,
    int MaxRawScore,
    int MinRawScore
);

internal sealed record AuthoritativeSampleSurveyQuestionResponseChoiceRow(
    long CollectionItemId,
    int Ordinal,
    long SurveyQuestionDocumentId,
    int NumericValue,
    int SortOrder,
    string TextValue
);

internal sealed record AuthoritativeSampleSurveyQuestionPersistedState(
    AuthoritativeSampleSurveyQuestionDocumentRow Document,
    AuthoritativeSampleSurveyQuestionRow SurveyQuestion,
    IReadOnlyList<AuthoritativeSampleSurveyQuestionMatriceRow> Matrices,
    IReadOnlyList<AuthoritativeSampleSurveyQuestionResponseChoiceRow> ResponseChoices
);

[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
[NonParallelizable]
public class Given_A_Postgresql_Relational_Write_Smoke_With_The_Authoritative_Sample_SurveyQuestion_Fixture
{
    private const int SchoolYear = 2024;
    private const string SchoolYearDescription = "2024-2025";
    private const string SurveyNamespace = "uri://sample.org/Survey";
    private const string SurveyIdentifier = "climate-2024";
    private const string SurveyTitle = "Climate and Culture Survey";
    private const string SurveySectionTitle = "Student Experience";
    private const string QuestionFormDescriptorUri = "uri://ed-fi.org/QuestionFormDescriptor#Matrix";
    private const string QuestionCode = "Q-01";
    private const string CreateQuestionText = "How would you rate each area?";
    private const string ChangedQuestionText = "How would you rate each area this semester?";

    private const string CreateRequestBodyJson = """
        {
          "questionCode": "Q-01",
          "questionFormDescriptor": "uri://ed-fi.org/QuestionFormDescriptor#Matrix",
          "questionText": "How would you rate each area?",
          "surveyReference": {
            "namespace": "uri://sample.org/Survey",
            "surveyIdentifier": "climate-2024"
          },
          "surveySectionReference": {
            "namespace": "uri://sample.org/Survey",
            "surveyIdentifier": "climate-2024",
            "surveySectionTitle": "Student Experience"
          },
          "matrices": [
            {
              "matrixElement": "Safety",
              "maxRawScore": 5,
              "minRawScore": 1
            },
            {
              "matrixElement": "Belonging",
              "maxRawScore": 4,
              "minRawScore": 1
            },
            {
              "matrixElement": "Engagement",
              "maxRawScore": 3,
              "minRawScore": 0
            }
          ],
          "responseChoices": [
            {
              "numericValue": 1,
              "sortOrder": 10,
              "textValue": "Strongly disagree"
            },
            {
              "numericValue": 2,
              "sortOrder": 20,
              "textValue": "Disagree"
            },
            {
              "numericValue": 3,
              "sortOrder": 30,
              "textValue": "Agree"
            }
          ]
        }
        """;

    private const string ChangedUpdateRequestBodyJson = """
        {
          "questionCode": "Q-01",
          "questionFormDescriptor": "uri://ed-fi.org/QuestionFormDescriptor#Matrix",
          "questionText": "How would you rate each area this semester?",
          "surveyReference": {
            "namespace": "uri://sample.org/Survey",
            "surveyIdentifier": "climate-2024"
          },
          "surveySectionReference": {
            "namespace": "uri://sample.org/Survey",
            "surveyIdentifier": "climate-2024",
            "surveySectionTitle": "Student Experience"
          },
          "matrices": [
            {
              "matrixElement": "Engagement",
              "maxRawScore": 6,
              "minRawScore": 0
            },
            {
              "matrixElement": "Belonging",
              "maxRawScore": 5,
              "minRawScore": 2
            },
            {
              "matrixElement": "Growth",
              "maxRawScore": 4,
              "minRawScore": 1
            }
          ],
          "responseChoices": [
            {
              "numericValue": 4,
              "sortOrder": 30,
              "textValue": "Agree"
            },
            {
              "numericValue": 2,
              "sortOrder": 20,
              "textValue": "Not sure"
            },
            {
              "numericValue": 5,
              "sortOrder": 40,
              "textValue": "Strongly agree"
            }
          ]
        }
        """;

    private static readonly DocumentUuid SurveyQuestionDocumentUuid = new(
        Guid.Parse("eeeeeeee-0000-0000-0000-000000000001")
    );

    private PostgresqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private PostgresqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;
    private ResourceInfo _resourceInfo = null!;
    private ResourceSchema _resourceSchema = null!;
    private AuthoritativeSampleSurveyQuestionSeedData _seedData = null!;
    private UpsertResult _createResult = null!;
    private UpdateResult _changedUpdateResult = null!;
    private UpdateResult _noOpUpdateResult = null!;
    private AuthoritativeSampleSurveyQuestionPersistedState _stateAfterCreate = null!;
    private AuthoritativeSampleSurveyQuestionPersistedState _stateAfterChangedUpdate = null!;
    private AuthoritativeSampleSurveyQuestionPersistedState _stateAfterNoOpUpdate = null!;

    [SetUp]
    public async Task Setup()
    {
        _fixture = PostgresqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            AuthoritativeSampleSurveyQuestionIntegrationTestSupport.FixtureRelativePath
        );
        _mappingSet = new MappingSetCompiler().Compile(_fixture.ModelSet);
        _database = await PostgresqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
        _serviceProvider = AuthoritativeSampleSurveyQuestionIntegrationTestSupport.CreateServiceProvider();

        var (projectSchema, resourceSchema) =
            AuthoritativeSampleSurveyQuestionIntegrationTestSupport.GetResourceSchema(
                _fixture.EffectiveSchemaSet,
                "ed-fi",
                "SurveyQuestion"
            );

        _resourceInfo = AuthoritativeSampleSurveyQuestionIntegrationTestSupport.CreateResourceInfo(
            projectSchema,
            resourceSchema
        );
        _resourceSchema = resourceSchema;
        _seedData = await SeedReferenceDataAsync();
        await DisableSurveyQuestionReferentialIdentityTriggerAsync();

        _createResult = await ExecuteCreateAsync();
        if (_createResult is UpsertResult.UpsertFailureReference createReferenceFailure)
        {
            Assert.Fail(
                $"Create reference failure: {AuthoritativeSampleSurveyQuestionIntegrationTestSupport.FormatReferenceFailure(createReferenceFailure)}"
            );
        }

        _createResult.Should().BeOfType<UpsertResult.InsertSuccess>();
        _stateAfterCreate = await ReadPersistedStateAsync(SurveyQuestionDocumentUuid.Value);

        _changedUpdateResult = await ExecuteUpdateAsync(
            ChangedUpdateRequestBodyJson,
            "pg-authoritative-sample-survey-question-changed-update"
        );
        if (_changedUpdateResult is UpdateResult.UpdateFailureReference changedUpdateReferenceFailure)
        {
            Assert.Fail(
                $"Changed update reference failure: {AuthoritativeSampleSurveyQuestionIntegrationTestSupport.FormatReferenceFailure(changedUpdateReferenceFailure)}"
            );
        }

        _changedUpdateResult.Should().BeOfType<UpdateResult.UpdateSuccess>();
        _stateAfterChangedUpdate = await ReadPersistedStateAsync(SurveyQuestionDocumentUuid.Value);

        _noOpUpdateResult = await ExecuteUpdateAsync(
            ChangedUpdateRequestBodyJson,
            "pg-authoritative-sample-survey-question-no-op-update"
        );
        if (_noOpUpdateResult is UpdateResult.UpdateFailureReference noOpReferenceFailure)
        {
            Assert.Fail(
                $"No-op update reference failure: {AuthoritativeSampleSurveyQuestionIntegrationTestSupport.FormatReferenceFailure(noOpReferenceFailure)}"
            );
        }

        _noOpUpdateResult.Should().BeOfType<UpdateResult.UpdateSuccess>();
        _stateAfterNoOpUpdate = await ReadPersistedStateAsync(SurveyQuestionDocumentUuid.Value);
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
    public void It_persists_authoritative_sample_survey_question_root_and_child_rows_on_create()
    {
        _createResult.Should().BeOfType<UpsertResult.InsertSuccess>();
        _stateAfterCreate.Document.DocumentUuid.Should().Be(SurveyQuestionDocumentUuid.Value);
        _stateAfterCreate
            .Document.ResourceKeyId.Should()
            .Be(_mappingSet.ResourceKeyIdByResource[new QualifiedResourceName("Ed-Fi", "SurveyQuestion")]);
        _stateAfterCreate
            .SurveyQuestion.Should()
            .Be(
                new AuthoritativeSampleSurveyQuestionRow(
                    _stateAfterCreate.Document.DocumentId,
                    SurveyNamespace,
                    SurveyIdentifier,
                    _seedData.SurveySectionDocumentId,
                    SurveyNamespace,
                    SurveyIdentifier,
                    SurveySectionTitle,
                    _seedData.SurveyDocumentId,
                    SurveyNamespace,
                    SurveyIdentifier,
                    _seedData.QuestionFormDescriptorId,
                    QuestionCode,
                    CreateQuestionText
                )
            );
        _stateAfterCreate
            .Matrices.Should()
            .Equal(
                new AuthoritativeSampleSurveyQuestionMatriceRow(
                    _stateAfterCreate.Matrices[0].CollectionItemId,
                    0,
                    _stateAfterCreate.Document.DocumentId,
                    "Safety",
                    5,
                    1
                ),
                new AuthoritativeSampleSurveyQuestionMatriceRow(
                    _stateAfterCreate.Matrices[1].CollectionItemId,
                    1,
                    _stateAfterCreate.Document.DocumentId,
                    "Belonging",
                    4,
                    1
                ),
                new AuthoritativeSampleSurveyQuestionMatriceRow(
                    _stateAfterCreate.Matrices[2].CollectionItemId,
                    2,
                    _stateAfterCreate.Document.DocumentId,
                    "Engagement",
                    3,
                    0
                )
            );
        _stateAfterCreate
            .ResponseChoices.Should()
            .Equal(
                new AuthoritativeSampleSurveyQuestionResponseChoiceRow(
                    _stateAfterCreate.ResponseChoices[0].CollectionItemId,
                    0,
                    _stateAfterCreate.Document.DocumentId,
                    1,
                    10,
                    "Strongly disagree"
                ),
                new AuthoritativeSampleSurveyQuestionResponseChoiceRow(
                    _stateAfterCreate.ResponseChoices[1].CollectionItemId,
                    1,
                    _stateAfterCreate.Document.DocumentId,
                    2,
                    20,
                    "Disagree"
                ),
                new AuthoritativeSampleSurveyQuestionResponseChoiceRow(
                    _stateAfterCreate.ResponseChoices[2].CollectionItemId,
                    2,
                    _stateAfterCreate.Document.DocumentId,
                    3,
                    30,
                    "Agree"
                )
            );
    }

    [Test]
    public void It_reuses_stable_collection_item_ids_for_retained_matrices_and_response_choices_on_changed_put()
    {
        _changedUpdateResult.Should().BeOfType<UpdateResult.UpdateSuccess>();
        _changedUpdateResult
            .As<UpdateResult.UpdateSuccess>()
            .ExistingDocumentUuid.Should()
            .Be(SurveyQuestionDocumentUuid);
        _stateAfterChangedUpdate
            .Document.ContentVersion.Should()
            .BeGreaterThan(_stateAfterCreate.Document.ContentVersion);
        _stateAfterChangedUpdate
            .SurveyQuestion.Should()
            .Be(
                new AuthoritativeSampleSurveyQuestionRow(
                    _stateAfterChangedUpdate.Document.DocumentId,
                    SurveyNamespace,
                    SurveyIdentifier,
                    _seedData.SurveySectionDocumentId,
                    SurveyNamespace,
                    SurveyIdentifier,
                    SurveySectionTitle,
                    _seedData.SurveyDocumentId,
                    SurveyNamespace,
                    SurveyIdentifier,
                    _seedData.QuestionFormDescriptorId,
                    QuestionCode,
                    ChangedQuestionText
                )
            );

        var createdMatricesByElement = _stateAfterCreate.Matrices.ToDictionary(row => row.MatrixElement);
        var changedMatricesByElement = _stateAfterChangedUpdate.Matrices.ToDictionary(row =>
            row.MatrixElement
        );

        changedMatricesByElement["Engagement"]
            .CollectionItemId.Should()
            .Be(createdMatricesByElement["Engagement"].CollectionItemId);
        changedMatricesByElement["Belonging"]
            .CollectionItemId.Should()
            .Be(createdMatricesByElement["Belonging"].CollectionItemId);
        changedMatricesByElement.Keys.Should().NotContain("Safety");
        changedMatricesByElement["Growth"]
            .CollectionItemId.Should()
            .NotBe(createdMatricesByElement["Safety"].CollectionItemId);

        _stateAfterChangedUpdate
            .Matrices.Should()
            .Equal(
                new AuthoritativeSampleSurveyQuestionMatriceRow(
                    createdMatricesByElement["Engagement"].CollectionItemId,
                    0,
                    _stateAfterChangedUpdate.Document.DocumentId,
                    "Engagement",
                    6,
                    0
                ),
                new AuthoritativeSampleSurveyQuestionMatriceRow(
                    createdMatricesByElement["Belonging"].CollectionItemId,
                    1,
                    _stateAfterChangedUpdate.Document.DocumentId,
                    "Belonging",
                    5,
                    2
                ),
                new AuthoritativeSampleSurveyQuestionMatriceRow(
                    changedMatricesByElement["Growth"].CollectionItemId,
                    2,
                    _stateAfterChangedUpdate.Document.DocumentId,
                    "Growth",
                    4,
                    1
                )
            );

        var createdResponseChoicesBySortOrder = _stateAfterCreate.ResponseChoices.ToDictionary(row =>
            row.SortOrder
        );
        var changedResponseChoicesBySortOrder = _stateAfterChangedUpdate.ResponseChoices.ToDictionary(row =>
            row.SortOrder
        );

        changedResponseChoicesBySortOrder[30]
            .CollectionItemId.Should()
            .Be(createdResponseChoicesBySortOrder[30].CollectionItemId);
        changedResponseChoicesBySortOrder[20]
            .CollectionItemId.Should()
            .Be(createdResponseChoicesBySortOrder[20].CollectionItemId);
        changedResponseChoicesBySortOrder.Keys.Should().NotContain(10);
        changedResponseChoicesBySortOrder[40]
            .CollectionItemId.Should()
            .NotBe(createdResponseChoicesBySortOrder[10].CollectionItemId);

        _stateAfterChangedUpdate
            .ResponseChoices.Should()
            .Equal(
                new AuthoritativeSampleSurveyQuestionResponseChoiceRow(
                    createdResponseChoicesBySortOrder[30].CollectionItemId,
                    0,
                    _stateAfterChangedUpdate.Document.DocumentId,
                    4,
                    30,
                    "Agree"
                ),
                new AuthoritativeSampleSurveyQuestionResponseChoiceRow(
                    createdResponseChoicesBySortOrder[20].CollectionItemId,
                    1,
                    _stateAfterChangedUpdate.Document.DocumentId,
                    2,
                    20,
                    "Not sure"
                ),
                new AuthoritativeSampleSurveyQuestionResponseChoiceRow(
                    changedResponseChoicesBySortOrder[40].CollectionItemId,
                    2,
                    _stateAfterChangedUpdate.Document.DocumentId,
                    5,
                    40,
                    "Strongly agree"
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
            .Be(SurveyQuestionDocumentUuid);
        _stateAfterNoOpUpdate.Should().BeEquivalentTo(_stateAfterChangedUpdate);
    }

    private async Task<UpsertResult> ExecuteCreateAsync()
    {
        await using var scope = _serviceProvider.CreateAsyncScope();
        SetSelectedInstance(scope.ServiceProvider);

        var requestBody = JsonNode.Parse(CreateRequestBodyJson)!;
        var request = new UpsertRequest(
            ResourceInfo: _resourceInfo,
            DocumentInfo: AuthoritativeSampleSurveyQuestionIntegrationTestSupport.CreateDocumentInfo(
                requestBody,
                _resourceInfo,
                _resourceSchema,
                _mappingSet
            ),
            MappingSet: _mappingSet,
            EdfiDoc: requestBody,
            Headers: [],
            TraceId: new TraceId("pg-authoritative-sample-survey-question-create"),
            DocumentUuid: SurveyQuestionDocumentUuid,
            DocumentSecurityElements: new([], [], [], [], []),
            UpdateCascadeHandler: new AuthoritativeSampleSurveyQuestionNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new AuthoritativeSampleSurveyQuestionAllowAllResourceAuthorizationHandler(),
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
            DocumentInfo: AuthoritativeSampleSurveyQuestionIntegrationTestSupport.CreateDocumentInfo(
                requestBody,
                _resourceInfo,
                _resourceSchema,
                _mappingSet
            ),
            MappingSet: _mappingSet,
            EdfiDoc: requestBody,
            Headers: [],
            TraceId: new TraceId(traceId),
            DocumentUuid: SurveyQuestionDocumentUuid,
            DocumentSecurityElements: new([], [], [], [], []),
            UpdateCascadeHandler: new AuthoritativeSampleSurveyQuestionNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new AuthoritativeSampleSurveyQuestionAllowAllResourceAuthorizationHandler(),
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
                    InstanceName: "PostgresqlRelationalWriteAuthoritativeSampleSurveyQuestionSmoke",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );
    }

    private async Task<AuthoritativeSampleSurveyQuestionSeedData> SeedReferenceDataAsync()
    {
        var schoolYearTypeResourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "SchoolYearType");
        var surveyResourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "Survey");
        var surveySectionResourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "SurveySection");
        var questionFormDescriptorResourceKeyId = await GetResourceKeyIdAsync(
            "Ed-Fi",
            "QuestionFormDescriptor"
        );

        var questionFormDescriptorId = await SeedDescriptorAsync(
            Guid.Parse("11111111-0000-0000-0000-000000000001"),
            questionFormDescriptorResourceKeyId,
            "QuestionFormDescriptor",
            "Ed-Fi:QuestionFormDescriptor",
            QuestionFormDescriptorUri,
            "uri://ed-fi.org/QuestionFormDescriptor",
            "Matrix",
            "Matrix"
        );

        var schoolYearTypeDocumentId = await InsertDocumentAsync(
            Guid.Parse("22222222-0000-0000-0000-000000000001"),
            schoolYearTypeResourceKeyId
        );
        await InsertSchoolYearTypeAsync(schoolYearTypeDocumentId, SchoolYear, SchoolYearDescription);
        await InsertReferentialIdentityAsync(
            CreateReferentialId(
                ("Ed-Fi", "SchoolYearType", false),
                ("$.schoolYear", SchoolYear.ToString(CultureInfo.InvariantCulture))
            ),
            schoolYearTypeDocumentId,
            schoolYearTypeResourceKeyId
        );

        var surveyDocumentId = await InsertDocumentAsync(
            Guid.Parse("33333333-0000-0000-0000-000000000001"),
            surveyResourceKeyId
        );
        await InsertSurveyAsync(
            surveyDocumentId,
            schoolYearTypeDocumentId,
            SurveyNamespace,
            SurveyIdentifier,
            SurveyTitle
        );
        await InsertReferentialIdentityAsync(
            CreateReferentialId(
                ("Ed-Fi", "Survey", false),
                ("$.namespace", SurveyNamespace),
                ("$.surveyIdentifier", SurveyIdentifier)
            ),
            surveyDocumentId,
            surveyResourceKeyId
        );

        var surveySectionDocumentId = await InsertDocumentAsync(
            Guid.Parse("44444444-0000-0000-0000-000000000001"),
            surveySectionResourceKeyId
        );
        await InsertSurveySectionAsync(
            surveySectionDocumentId,
            surveyDocumentId,
            SurveyNamespace,
            SurveyIdentifier,
            SurveySectionTitle
        );
        await InsertReferentialIdentityAsync(
            CreateReferentialId(
                ("Ed-Fi", "SurveySection", false),
                ("$.surveyReference.namespace", SurveyNamespace),
                ("$.surveyReference.surveyIdentifier", SurveyIdentifier),
                ("$.surveySectionTitle", SurveySectionTitle)
            ),
            surveySectionDocumentId,
            surveySectionResourceKeyId
        );

        return new(
            SchoolYearTypeDocumentId: schoolYearTypeDocumentId,
            SurveyDocumentId: surveyDocumentId,
            SurveySectionDocumentId: surveySectionDocumentId,
            QuestionFormDescriptorId: questionFormDescriptorId
        );
    }

    private async Task DisableSurveyQuestionReferentialIdentityTriggerAsync()
    {
        await _database.ExecuteNonQueryAsync(
            """
            ALTER TABLE "edfi"."SurveyQuestion"
            DISABLE TRIGGER "TR_SurveyQuestion_ReferentialIdentity";
            """
        );
    }

    private async Task<long> SeedDescriptorAsync(
        Guid documentUuid,
        short resourceKeyId,
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

    private async Task InsertSchoolYearTypeAsync(
        long documentId,
        int schoolYear,
        string schoolYearDescription
    )
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
            new NpgsqlParameter("schoolYearDescription", schoolYearDescription)
        );
    }

    private async Task InsertSurveyAsync(
        long documentId,
        long schoolYearTypeDocumentId,
        string surveyNamespace,
        string surveyIdentifier,
        string surveyTitle
    )
    {
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO "edfi"."Survey" (
                "DocumentId",
                "SchoolYear_Unified",
                "SchoolYear_DocumentId",
                "Namespace",
                "SurveyIdentifier",
                "SurveyTitle"
            )
            VALUES (
                @documentId,
                @schoolYear,
                @schoolYearDocumentId,
                @namespace,
                @surveyIdentifier,
                @surveyTitle
            );
            """,
            new NpgsqlParameter("documentId", documentId),
            new NpgsqlParameter("schoolYear", SchoolYear),
            new NpgsqlParameter("schoolYearDocumentId", schoolYearTypeDocumentId),
            new NpgsqlParameter("namespace", surveyNamespace),
            new NpgsqlParameter("surveyIdentifier", surveyIdentifier),
            new NpgsqlParameter("surveyTitle", surveyTitle)
        );
    }

    private async Task InsertSurveySectionAsync(
        long documentId,
        long surveyDocumentId,
        string surveyNamespace,
        string surveyIdentifier,
        string surveySectionTitle
    )
    {
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO "edfi"."SurveySection" (
                "DocumentId",
                "Survey_DocumentId",
                "Survey_Namespace",
                "Survey_SurveyIdentifier",
                "SurveySectionTitle"
            )
            VALUES (
                @documentId,
                @surveyDocumentId,
                @surveyNamespace,
                @surveyIdentifier,
                @surveySectionTitle
            );
            """,
            new NpgsqlParameter("documentId", documentId),
            new NpgsqlParameter("surveyDocumentId", surveyDocumentId),
            new NpgsqlParameter("surveyNamespace", surveyNamespace),
            new NpgsqlParameter("surveyIdentifier", surveyIdentifier),
            new NpgsqlParameter("surveySectionTitle", surveySectionTitle)
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

    private async Task<AuthoritativeSampleSurveyQuestionPersistedState> ReadPersistedStateAsync(
        Guid documentUuid
    )
    {
        var document = await ReadDocumentAsync(documentUuid);

        return new(
            Document: document,
            SurveyQuestion: await ReadSurveyQuestionAsync(document.DocumentId),
            Matrices: await ReadMatricesAsync(document.DocumentId),
            ResponseChoices: await ReadResponseChoicesAsync(document.DocumentId)
        );
    }

    private async Task<AuthoritativeSampleSurveyQuestionDocumentRow> ReadDocumentAsync(Guid documentUuid)
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
            ? new AuthoritativeSampleSurveyQuestionDocumentRow(
                AuthoritativeSampleSurveyQuestionIntegrationTestSupport.GetInt64(rows[0], "DocumentId"),
                AuthoritativeSampleSurveyQuestionIntegrationTestSupport.GetGuid(rows[0], "DocumentUuid"),
                AuthoritativeSampleSurveyQuestionIntegrationTestSupport.GetInt16(rows[0], "ResourceKeyId"),
                AuthoritativeSampleSurveyQuestionIntegrationTestSupport.GetInt64(rows[0], "ContentVersion")
            )
            : throw new InvalidOperationException(
                $"Expected exactly one document row for '{documentUuid}', but found {rows.Count}."
            );
    }

    private async Task<AuthoritativeSampleSurveyQuestionRow> ReadSurveyQuestionAsync(long documentId)
    {
        var rows = await _database.QueryRowsAsync(
            """
            SELECT
                "DocumentId",
                "Namespace_Unified",
                "SurveyIdentifier_Unified",
                "SurveySection_DocumentId",
                "SurveySection_Namespace",
                "SurveySection_SurveyIdentifier",
                "SurveySection_SurveySectionTitle",
                "Survey_DocumentId",
                "Survey_Namespace",
                "Survey_SurveyIdentifier",
                "QuestionFormDescriptor_DescriptorId",
                "QuestionCode",
                "QuestionText"
            FROM "edfi"."SurveyQuestion"
            WHERE "DocumentId" = @documentId;
            """,
            new NpgsqlParameter("documentId", documentId)
        );

        return rows.Count == 1
            ? new AuthoritativeSampleSurveyQuestionRow(
                AuthoritativeSampleSurveyQuestionIntegrationTestSupport.GetInt64(rows[0], "DocumentId"),
                AuthoritativeSampleSurveyQuestionIntegrationTestSupport.GetString(
                    rows[0],
                    "Namespace_Unified"
                ),
                AuthoritativeSampleSurveyQuestionIntegrationTestSupport.GetString(
                    rows[0],
                    "SurveyIdentifier_Unified"
                ),
                AuthoritativeSampleSurveyQuestionIntegrationTestSupport.GetInt64(
                    rows[0],
                    "SurveySection_DocumentId"
                ),
                AuthoritativeSampleSurveyQuestionIntegrationTestSupport.GetString(
                    rows[0],
                    "SurveySection_Namespace"
                ),
                AuthoritativeSampleSurveyQuestionIntegrationTestSupport.GetString(
                    rows[0],
                    "SurveySection_SurveyIdentifier"
                ),
                AuthoritativeSampleSurveyQuestionIntegrationTestSupport.GetString(
                    rows[0],
                    "SurveySection_SurveySectionTitle"
                ),
                AuthoritativeSampleSurveyQuestionIntegrationTestSupport.GetInt64(
                    rows[0],
                    "Survey_DocumentId"
                ),
                AuthoritativeSampleSurveyQuestionIntegrationTestSupport.GetString(
                    rows[0],
                    "Survey_Namespace"
                ),
                AuthoritativeSampleSurveyQuestionIntegrationTestSupport.GetString(
                    rows[0],
                    "Survey_SurveyIdentifier"
                ),
                AuthoritativeSampleSurveyQuestionIntegrationTestSupport.GetInt64(
                    rows[0],
                    "QuestionFormDescriptor_DescriptorId"
                ),
                AuthoritativeSampleSurveyQuestionIntegrationTestSupport.GetString(rows[0], "QuestionCode"),
                AuthoritativeSampleSurveyQuestionIntegrationTestSupport.GetString(rows[0], "QuestionText")
            )
            : throw new InvalidOperationException(
                $"Expected exactly one SurveyQuestion row for document id '{documentId}', but found {rows.Count}."
            );
    }

    private async Task<IReadOnlyList<AuthoritativeSampleSurveyQuestionMatriceRow>> ReadMatricesAsync(
        long documentId
    )
    {
        var rows = await _database.QueryRowsAsync(
            """
            SELECT
                "CollectionItemId",
                "Ordinal",
                "SurveyQuestion_DocumentId",
                "MatrixElement",
                "MaxRawScore",
                "MinRawScore"
            FROM "edfi"."SurveyQuestionMatrice"
            WHERE "SurveyQuestion_DocumentId" = @documentId
            ORDER BY "Ordinal";
            """,
            new NpgsqlParameter("documentId", documentId)
        );

        return rows.Select(row => new AuthoritativeSampleSurveyQuestionMatriceRow(
                AuthoritativeSampleSurveyQuestionIntegrationTestSupport.GetInt64(row, "CollectionItemId"),
                AuthoritativeSampleSurveyQuestionIntegrationTestSupport.GetInt32(row, "Ordinal"),
                AuthoritativeSampleSurveyQuestionIntegrationTestSupport.GetInt64(
                    row,
                    "SurveyQuestion_DocumentId"
                ),
                AuthoritativeSampleSurveyQuestionIntegrationTestSupport.GetString(row, "MatrixElement"),
                AuthoritativeSampleSurveyQuestionIntegrationTestSupport.GetInt32(row, "MaxRawScore"),
                AuthoritativeSampleSurveyQuestionIntegrationTestSupport.GetInt32(row, "MinRawScore")
            ))
            .ToArray();
    }

    private async Task<
        IReadOnlyList<AuthoritativeSampleSurveyQuestionResponseChoiceRow>
    > ReadResponseChoicesAsync(long documentId)
    {
        var rows = await _database.QueryRowsAsync(
            """
            SELECT
                "CollectionItemId",
                "Ordinal",
                "SurveyQuestion_DocumentId",
                "NumericValue",
                "SortOrder",
                "TextValue"
            FROM "edfi"."SurveyQuestionResponseChoice"
            WHERE "SurveyQuestion_DocumentId" = @documentId
            ORDER BY "Ordinal";
            """,
            new NpgsqlParameter("documentId", documentId)
        );

        return rows.Select(row => new AuthoritativeSampleSurveyQuestionResponseChoiceRow(
                AuthoritativeSampleSurveyQuestionIntegrationTestSupport.GetInt64(row, "CollectionItemId"),
                AuthoritativeSampleSurveyQuestionIntegrationTestSupport.GetInt32(row, "Ordinal"),
                AuthoritativeSampleSurveyQuestionIntegrationTestSupport.GetInt64(
                    row,
                    "SurveyQuestion_DocumentId"
                ),
                AuthoritativeSampleSurveyQuestionIntegrationTestSupport.GetInt32(row, "NumericValue"),
                AuthoritativeSampleSurveyQuestionIntegrationTestSupport.GetInt32(row, "SortOrder"),
                AuthoritativeSampleSurveyQuestionIntegrationTestSupport.GetString(row, "TextValue")
            ))
            .ToArray();
    }
}

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
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Backend.Postgresql;
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
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

internal sealed class PostgresqlRelationalQueryHostApplicationLifetime : IHostApplicationLifetime
{
    public CancellationToken ApplicationStarted => CancellationToken.None;
    public CancellationToken ApplicationStopping => CancellationToken.None;
    public CancellationToken ApplicationStopped => CancellationToken.None;

    public void StopApplication() { }
}

internal sealed class PostgresqlRelationalQueryAllowAllResourceAuthorizationHandler
    : IResourceAuthorizationHandler
{
    public Task<ResourceAuthorizationResult> Authorize(
        DocumentSecurityElements documentSecurityElements,
        OperationType operationType,
        TraceId traceId
    ) => Task.FromResult<ResourceAuthorizationResult>(new ResourceAuthorizationResult.Authorized());
}

internal sealed class PostgresqlRelationalQueryNoOpUpdateCascadeHandler : IUpdateCascadeHandler
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

internal sealed class PostgresqlRelationalQueryExecutionRecorder
{
    public List<PageKeysetSpec> HydrationKeysets { get; } = [];
    public List<long> PageMaterializedDocumentIds { get; } = [];
    public int SingleDocumentMaterializationCallCount { get; private set; }
    public int PageMaterializationCallCount { get; private set; }

    public void Reset()
    {
        HydrationKeysets.Clear();
        PageMaterializedDocumentIds.Clear();
        SingleDocumentMaterializationCallCount = 0;
        PageMaterializationCallCount = 0;
    }

    public void RecordSingleDocumentMaterialization()
    {
        SingleDocumentMaterializationCallCount++;
    }

    public void RecordPageMaterialization(IReadOnlyList<MaterializedDocument> materializedDocuments)
    {
        PageMaterializationCallCount++;
        PageMaterializedDocumentIds.AddRange(
            materializedDocuments.Select(static document => document.DocumentMetadata.DocumentId)
        );
    }
}

internal sealed class RecordingPostgresqlDocumentHydrator(
    NpgsqlDataSourceProvider dataSourceProvider,
    PostgresqlRelationalQueryExecutionRecorder recorder
) : IDocumentHydrator
{
    private readonly NpgsqlDataSourceProvider _dataSourceProvider =
        dataSourceProvider ?? throw new ArgumentNullException(nameof(dataSourceProvider));
    private readonly PostgresqlRelationalQueryExecutionRecorder _recorder =
        recorder ?? throw new ArgumentNullException(nameof(recorder));

    public async Task<HydratedPage> HydrateAsync(
        ResourceReadPlan plan,
        PageKeysetSpec keyset,
        CancellationToken ct
    )
    {
        _recorder.HydrationKeysets.Add(keyset);

        await using var connection = await _dataSourceProvider.DataSource.OpenConnectionAsync(ct);

        return await HydrationExecutor.ExecuteAsync(connection, plan, keyset, SqlDialect.Pgsql, null, ct);
    }
}

internal sealed class RecordingRelationalReadMaterializer(PostgresqlRelationalQueryExecutionRecorder recorder)
    : IRelationalReadMaterializer
{
    private readonly PostgresqlRelationalQueryExecutionRecorder _recorder =
        recorder ?? throw new ArgumentNullException(nameof(recorder));
    private readonly RelationalReadMaterializer _inner = new();

    public JsonNode Materialize(RelationalReadMaterializationRequest request)
    {
        _recorder.RecordSingleDocumentMaterialization();
        return _inner.Materialize(request);
    }

    public IReadOnlyList<MaterializedDocument> MaterializePage(
        RelationalReadPageMaterializationRequest request
    )
    {
        var materializedDocuments = _inner.MaterializePage(request);
        _recorder.RecordPageMaterialization(materializedDocuments);
        return materializedDocuments;
    }
}

internal sealed class ThrowingRelationalReadTargetLookupService : IRelationalReadTargetLookupService
{
    public Task<RelationalReadTargetLookupResult> ResolveForGetByIdAsync(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        DocumentUuid documentUuid,
        CancellationToken cancellationToken = default
    )
    {
        throw new AssertionException(
            "Relational query execution should not route through get-by-id read target lookup."
        );
    }
}

internal sealed record QuerySchoolSeed(DocumentUuid DocumentUuid, int SchoolId, string NameOfInstitution);

internal sealed record PersistedQuerySchool(
    long DocumentId,
    Guid DocumentUuid,
    int SchoolId,
    string NameOfInstitution
);

internal sealed record PersistedCourseTranscript(
    long DocumentId,
    Guid DocumentUuid,
    string StudentUniqueId,
    string CourseCode
);

[TestFixture]
[NonParallelizable]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
public class Given_A_Postgresql_Relational_Query_With_The_Authoritative_Ds52_School_Fixture
{
    private const string FixtureRelativePath = "src/dms/backend/Fixtures/authoritative/ds-5.2";
    private const int MaximumPageSize = 500;
    private const int CourseTranscriptSchoolYear = 2026;
    private const string CourseTranscriptStudentUniqueId = "CT-STUDENT-001";
    private const string CourseTranscriptCourseCode = "ALG-1";
    private const string TermDescriptorUri = "uri://ed-fi.org/TermDescriptor#Fall Semester";
    private const string CourseAttemptResultDescriptorUri =
        "uri://ed-fi.org/CourseAttemptResultDescriptor#Pass";
    private static readonly QualifiedResourceName SchoolResource = new("Ed-Fi", "School");
    private static readonly QualifiedResourceName CourseTranscriptResource = new("Ed-Fi", "CourseTranscript");
    private static readonly QuerySchoolSeed[] _schoolSeeds =
    [
        new(
            new DocumentUuid(Guid.Parse("dddddddd-0000-0000-0000-000000000011")),
            255901,
            "Lantern High School"
        ),
        new(
            new DocumentUuid(Guid.Parse("dddddddd-0000-0000-0000-000000000022")),
            255902,
            "Summit High School"
        ),
        new(
            new DocumentUuid(Guid.Parse("dddddddd-0000-0000-0000-000000000033")),
            255903,
            "Cedar High School"
        ),
    ];

    private PostgresqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private PostgresqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;
    private PostgresqlRelationalQueryExecutionRecorder _recorder = null!;
    private ResourceInfo _resourceInfo = null!;
    private ResourceInfo _courseTranscriptResourceInfo = null!;
    private ResourceSchema _resourceSchema = null!;
    private IReadOnlyList<PersistedQuerySchool> _persistedSchoolsInDocumentOrder = null!;
    private PersistedCourseTranscript _persistedCourseTranscript = null!;
    private long _termDescriptorId;
    private long _courseAttemptResultDescriptorId;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _fixture = PostgresqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(FixtureRelativePath);
        _mappingSet = _fixture.MappingSet;
        _database = await PostgresqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
        _serviceProvider = CreateServiceProvider();
        _recorder = _serviceProvider.GetRequiredService<PostgresqlRelationalQueryExecutionRecorder>();

        var (projectSchema, resourceSchema) = GetResourceSchema(
            _fixture.EffectiveSchemaSet,
            "ed-fi",
            "School"
        );

        _resourceInfo = CreateResourceInfo(projectSchema, resourceSchema);
        _resourceSchema = resourceSchema;

        var (courseTranscriptProjectSchema, courseTranscriptResourceSchema) = GetResourceSchema(
            _fixture.EffectiveSchemaSet,
            "ed-fi",
            "CourseTranscript"
        );
        _courseTranscriptResourceInfo = CreateResourceInfo(
            courseTranscriptProjectSchema,
            courseTranscriptResourceSchema
        );

        await SeedReferenceDataAsync();

        foreach (var schoolSeed in _schoolSeeds)
        {
            var createResult = await ExecuteCreateAsync(schoolSeed);
            createResult.Should().BeOfType<UpsertResult.InsertSuccess>();
        }

        _persistedSchoolsInDocumentOrder = await ReadPersistedSchoolsInDocumentOrderAsync();
        _persistedSchoolsInDocumentOrder.Should().HaveCount(3);
        _persistedCourseTranscript = await SeedCourseTranscriptGraphAsync(
            _persistedSchoolsInDocumentOrder[0]
        );
        _recorder.Reset();
    }

    [SetUp]
    public void Setup()
    {
        _recorder.Reset();
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
    public async Task It_filters_by_a_scalar_field_and_returns_matching_total_count()
    {
        var expectedSchool = _persistedSchoolsInDocumentOrder[1];

        var result = await ExecuteQueryAsync(
            [
                CreateQueryElement(
                    "nameOfInstitution",
                    "$.nameOfInstitution",
                    expectedSchool.NameOfInstitution
                ),
            ],
            limit: 25,
            offset: 0,
            totalCount: true,
            traceId: "pg-query-scalar-filter"
        );

        var success = result.Should().BeOfType<QueryResult.QuerySuccess>().Subject;

        success.TotalCount.Should().Be(1);
        success.EdfiDocs.Should().HaveCount(1);
        success.EdfiDocs[0]!["id"]!.GetValue<string>().Should().Be(expectedSchool.DocumentUuid.ToString());
        success.EdfiDocs[0]!["schoolId"]!.GetValue<long>().Should().Be(expectedSchool.SchoolId);
        success.EdfiDocs[0]!["nameOfInstitution"]!
            .GetValue<string>()
            .Should()
            .Be(expectedSchool.NameOfInstitution);

        var keyset = AssertSingleQueryHydration();
        keyset.Plan.TotalCountSql.Should().NotBeNull();
        AssertPageMaterialization(expectedSchool.DocumentId);
    }

    [Test]
    public async Task It_pages_in_document_id_order_and_only_materializes_the_requested_page()
    {
        var firstPageResult = await ExecuteQueryAsync(
            [],
            limit: 2,
            offset: 0,
            totalCount: true,
            traceId: "pg-query-page-1"
        );

        var firstPageSuccess = firstPageResult.Should().BeOfType<QueryResult.QuerySuccess>().Subject;

        firstPageSuccess.TotalCount.Should().Be(3);
        firstPageSuccess
            .EdfiDocs.Select(document => document!["id"]!.GetValue<string>())
            .Should()
            .Equal(
                _persistedSchoolsInDocumentOrder[0].DocumentUuid.ToString(),
                _persistedSchoolsInDocumentOrder[1].DocumentUuid.ToString()
            );
        AssertSchoolQueryDocument(firstPageSuccess.EdfiDocs[0], _persistedSchoolsInDocumentOrder[0]);
        AssertSchoolQueryDocument(firstPageSuccess.EdfiDocs[1], _persistedSchoolsInDocumentOrder[1]);
        AssertSingleQueryHydration().Plan.TotalCountSql.Should().NotBeNull();
        AssertPageMaterialization(
            _persistedSchoolsInDocumentOrder[0].DocumentId,
            _persistedSchoolsInDocumentOrder[1].DocumentId
        );

        _recorder.Reset();

        var secondPageResult = await ExecuteQueryAsync(
            [],
            limit: 2,
            offset: 2,
            totalCount: true,
            traceId: "pg-query-page-2"
        );

        var secondPageSuccess = secondPageResult.Should().BeOfType<QueryResult.QuerySuccess>().Subject;

        secondPageSuccess.TotalCount.Should().Be(3);
        secondPageSuccess
            .EdfiDocs.Select(document => document!["id"]!.GetValue<string>())
            .Should()
            .Equal(_persistedSchoolsInDocumentOrder[2].DocumentUuid.ToString());
        AssertSchoolQueryDocument(secondPageSuccess.EdfiDocs[0], _persistedSchoolsInDocumentOrder[2]);
        AssertSingleQueryHydration().Plan.TotalCountSql.Should().NotBeNull();
        AssertPageMaterialization(_persistedSchoolsInDocumentOrder[2].DocumentId);
    }

    [Test]
    public async Task It_filters_by_id_using_the_special_case_document_uuid_query_path()
    {
        var expectedSchool = _persistedSchoolsInDocumentOrder[2];

        var result = await ExecuteQueryAsync(
            [CreateQueryElement("id", "$.id", expectedSchool.DocumentUuid.ToString())],
            limit: 25,
            offset: 0,
            totalCount: true,
            traceId: "pg-query-id-filter"
        );

        var success = result.Should().BeOfType<QueryResult.QuerySuccess>().Subject;

        success.TotalCount.Should().Be(1);
        success.EdfiDocs.Should().HaveCount(1);
        success.EdfiDocs[0]!["id"]!.GetValue<string>().Should().Be(expectedSchool.DocumentUuid.ToString());
        success.EdfiDocs[0]!["nameOfInstitution"]!
            .GetValue<string>()
            .Should()
            .Be(expectedSchool.NameOfInstitution);

        AssertSingleQueryHydration();
        AssertPageMaterialization(expectedSchool.DocumentId);
    }

    [Test]
    public async Task It_filters_course_transcripts_by_virtual_student_unique_id_reference_alias_without_reference_join()
    {
        var result = await ExecuteQueryAsync(
            [
                CreateQueryElement(
                    "studentUniqueId",
                    "$.studentReference.studentAcademicRecordUniqueId",
                    CourseTranscriptStudentUniqueId
                ),
            ],
            limit: 25,
            offset: 0,
            totalCount: true,
            traceId: "pg-query-course-transcript-student-alias",
            resourceInfo: _courseTranscriptResourceInfo
        );

        var success = result.Should().BeOfType<QueryResult.QuerySuccess>().Subject;

        success.TotalCount.Should().Be(1);
        success.EdfiDocs.Should().HaveCount(1);
        AssertCourseTranscriptQueryDocument(success.EdfiDocs[0], _persistedCourseTranscript);

        var keyset = AssertSingleQueryHydration();
        AssertCourseTranscriptReferenceAliasSql(keyset);
        keyset.ParameterValues["studentUniqueId"].Should().Be(CourseTranscriptStudentUniqueId);
        AssertPageMaterialization(_persistedCourseTranscript.DocumentId);
    }

    private static ServiceProvider CreateServiceProvider()
    {
        ServiceCollection services = [];

        services.AddSingleton<IHostApplicationLifetime, PostgresqlRelationalQueryHostApplicationLifetime>();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<NpgsqlDataSourceCache>();
        services.AddScoped<IDmsInstanceSelection, DmsInstanceSelection>();
        services.AddScoped<NpgsqlDataSourceProvider>();
        services.Configure<DatabaseOptions>(options => options.IsolationLevel = IsolationLevel.ReadCommitted);
        services.AddTestReadableProfileProjector();
        services.AddScoped<RelationalDocumentStoreRepository>();
        services.AddSingleton<PostgresqlRelationalQueryExecutionRecorder>();
        services.AddPostgresqlReferenceResolver();
        services.Replace(ServiceDescriptor.Scoped<IDocumentHydrator, RecordingPostgresqlDocumentHydrator>());
        services.Replace(
            ServiceDescriptor.Scoped<IRelationalReadMaterializer, RecordingRelationalReadMaterializer>()
        );
        services.Replace(
            ServiceDescriptor.Scoped<
                IRelationalReadTargetLookupService,
                ThrowingRelationalReadTargetLookupService
            >()
        );

        return services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }
        );
    }

    private static (ProjectSchema ProjectSchema, ResourceSchema ResourceSchema) GetResourceSchema(
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

    private static ResourceInfo CreateResourceInfo(
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

    private async Task SeedReferenceDataAsync()
    {
        await SeedDescriptorAsync(
            Guid.Parse("10111111-1111-1111-1111-111111111111"),
            "AddressTypeDescriptor",
            "Ed-Fi:AddressTypeDescriptor",
            "uri://ed-fi.org/AddressTypeDescriptor#Physical",
            "uri://ed-fi.org/AddressTypeDescriptor",
            "Physical",
            "Physical"
        );
        await SeedDescriptorAsync(
            Guid.Parse("20222222-2222-2222-2222-222222222222"),
            "AddressTypeDescriptor",
            "Ed-Fi:AddressTypeDescriptor",
            "uri://ed-fi.org/AddressTypeDescriptor#Mailing",
            "uri://ed-fi.org/AddressTypeDescriptor",
            "Mailing",
            "Mailing"
        );
        await SeedDescriptorAsync(
            Guid.Parse("30333333-3333-3333-3333-333333333333"),
            "StateAbbreviationDescriptor",
            "Ed-Fi:StateAbbreviationDescriptor",
            "uri://ed-fi.org/StateAbbreviationDescriptor#TX",
            "uri://ed-fi.org/StateAbbreviationDescriptor",
            "TX",
            "Texas"
        );
        await SeedDescriptorAsync(
            Guid.Parse("40444444-4444-4444-4444-444444444444"),
            "EducationOrganizationCategoryDescriptor",
            "Ed-Fi:EducationOrganizationCategoryDescriptor",
            "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School",
            "uri://ed-fi.org/EducationOrganizationCategoryDescriptor",
            "School",
            "School"
        );
        await SeedDescriptorAsync(
            Guid.Parse("50555555-5555-5555-5555-555555555555"),
            "GradeLevelDescriptor",
            "Ed-Fi:GradeLevelDescriptor",
            "uri://ed-fi.org/GradeLevelDescriptor#Ninth grade",
            "uri://ed-fi.org/GradeLevelDescriptor",
            "Ninth grade",
            "Ninth grade"
        );
        await SeedDescriptorAsync(
            Guid.Parse("60666666-6666-6666-6666-666666666666"),
            "GradeLevelDescriptor",
            "Ed-Fi:GradeLevelDescriptor",
            "uri://ed-fi.org/GradeLevelDescriptor#Tenth grade",
            "uri://ed-fi.org/GradeLevelDescriptor",
            "Tenth grade",
            "Tenth grade"
        );
        _termDescriptorId = await SeedDescriptorAsync(
            Guid.Parse("70777777-7777-7777-7777-777777777777"),
            "TermDescriptor",
            "Ed-Fi:TermDescriptor",
            TermDescriptorUri,
            "uri://ed-fi.org/TermDescriptor",
            "Fall Semester",
            "Fall Semester"
        );
        _courseAttemptResultDescriptorId = await SeedDescriptorAsync(
            Guid.Parse("80888888-8888-8888-8888-888888888888"),
            "CourseAttemptResultDescriptor",
            "Ed-Fi:CourseAttemptResultDescriptor",
            CourseAttemptResultDescriptorUri,
            "uri://ed-fi.org/CourseAttemptResultDescriptor",
            "Pass",
            "Pass"
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

    private async Task<UpsertResult> ExecuteCreateAsync(QuerySchoolSeed schoolSeed)
    {
        await using var scope = _serviceProvider.CreateAsyncScope();
        SetSelectedInstance(scope.ServiceProvider);

        var requestBody = CreateSchoolRequestBody(schoolSeed);
        var request = new UpsertRequest(
            ResourceInfo: _resourceInfo,
            DocumentInfo: RelationalDocumentInfoTestHelper.CreateDocumentInfo(
                requestBody,
                _resourceInfo,
                _resourceSchema,
                _mappingSet
            ),
            MappingSet: _mappingSet,
            EdfiDoc: requestBody,
            Headers: [],
            TraceId: new TraceId($"pg-query-seed-{schoolSeed.SchoolId}"),
            DocumentUuid: schoolSeed.DocumentUuid,
            DocumentSecurityElements: new([], [], [], [], []),
            UpdateCascadeHandler: new PostgresqlRelationalQueryNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new PostgresqlRelationalQueryAllowAllResourceAuthorizationHandler(),
            ResourceAuthorizationPathways: []
        );

        return await scope
            .ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>()
            .UpsertDocument(request);
    }

    private async Task<QueryResult> ExecuteQueryAsync(
        QueryElement[] queryElements,
        int? limit,
        int? offset,
        bool totalCount,
        string traceId,
        ResourceInfo? resourceInfo = null
    )
    {
        await using var scope = _serviceProvider.CreateAsyncScope();
        SetSelectedInstance(scope.ServiceProvider);

        var effectiveResourceInfo = resourceInfo ?? _resourceInfo;
        var request = new RelationalQueryRequest(
            ResourceInfo: effectiveResourceInfo,
            MappingSet: _mappingSet,
            QueryElements: queryElements,
            AuthorizationSecurableInfo: effectiveResourceInfo.AuthorizationSecurableInfo,
            AuthorizationStrategyEvaluators: [],
            PaginationParameters: new PaginationParameters(
                Limit: limit,
                Offset: offset,
                TotalCount: totalCount,
                MaximumPageSize: MaximumPageSize
            ),
            TraceId: new TraceId(traceId)
        );

        return await scope
            .ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>()
            .QueryDocuments(request);
    }

    private async Task<IReadOnlyList<PersistedQuerySchool>> ReadPersistedSchoolsInDocumentOrderAsync()
    {
        var resourceKeyId = _mappingSet.ResourceKeyIdByResource[SchoolResource];
        var physicalSchema = _mappingSet.ReadPlansByResource[SchoolResource].Model.PhysicalSchema.Value;
        var rows = await _database.QueryRowsAsync(
            $"""
            SELECT
                doc."DocumentId",
                doc."DocumentUuid",
                school."SchoolId",
                school."NameOfInstitution"
            FROM "dms"."Document" doc
            INNER JOIN "{physicalSchema}"."School" school
                ON school."DocumentId" = doc."DocumentId"
            WHERE doc."ResourceKeyId" = @resourceKeyId
            ORDER BY doc."DocumentId";
            """,
            new NpgsqlParameter("resourceKeyId", resourceKeyId)
        );

        return
        [
            .. rows.Select(row => new PersistedQuerySchool(
                DocumentId: GetRequiredInt64(row, "DocumentId"),
                DocumentUuid: GetRequiredGuid(row, "DocumentUuid"),
                SchoolId: GetRequiredInt32(row, "SchoolId"),
                NameOfInstitution: GetRequiredString(row, "NameOfInstitution")
            )),
        ];
    }

    private async Task<PersistedCourseTranscript> SeedCourseTranscriptGraphAsync(PersistedQuerySchool school)
    {
        var educationOrganizationId = Convert.ToInt64(school.SchoolId, CultureInfo.InvariantCulture);
        await InsertEducationOrganizationIdentityAsync(school.DocumentId, educationOrganizationId);

        var studentResourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "Student");
        var studentDocumentId = await InsertDocumentAsync(
            Guid.Parse("eeeeeeee-0000-0000-0000-000000000101"),
            studentResourceKeyId
        );
        await InsertStudentAsync(studentDocumentId, CourseTranscriptStudentUniqueId);

        var schoolYearResourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "SchoolYearType");
        var schoolYearDocumentId = await InsertDocumentAsync(
            Guid.Parse("eeeeeeee-0000-0000-0000-000000000102"),
            schoolYearResourceKeyId
        );
        await InsertSchoolYearTypeAsync(schoolYearDocumentId, CourseTranscriptSchoolYear);

        var courseResourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "Course");
        var courseDocumentId = await InsertDocumentAsync(
            Guid.Parse("eeeeeeee-0000-0000-0000-000000000103"),
            courseResourceKeyId
        );
        await InsertCourseAsync(
            courseDocumentId,
            school.DocumentId,
            educationOrganizationId,
            CourseTranscriptCourseCode
        );

        var studentAcademicRecordResourceKeyId = await GetResourceKeyIdAsync(
            "Ed-Fi",
            "StudentAcademicRecord"
        );
        var studentAcademicRecordDocumentId = await InsertDocumentAsync(
            Guid.Parse("eeeeeeee-0000-0000-0000-000000000104"),
            studentAcademicRecordResourceKeyId
        );
        await InsertStudentAcademicRecordAsync(
            studentAcademicRecordDocumentId,
            school.DocumentId,
            educationOrganizationId,
            schoolYearDocumentId,
            studentDocumentId
        );

        var courseTranscriptResourceKeyId = _mappingSet.ResourceKeyIdByResource[CourseTranscriptResource];
        var courseTranscriptDocumentUuid = Guid.Parse("eeeeeeee-0000-0000-0000-000000000105");
        var courseTranscriptDocumentId = await InsertDocumentAsync(
            courseTranscriptDocumentUuid,
            courseTranscriptResourceKeyId
        );
        await InsertCourseTranscriptAsync(
            courseTranscriptDocumentId,
            courseDocumentId,
            studentAcademicRecordDocumentId,
            educationOrganizationId
        );

        return new PersistedCourseTranscript(
            courseTranscriptDocumentId,
            courseTranscriptDocumentUuid,
            CourseTranscriptStudentUniqueId,
            CourseTranscriptCourseCode
        );
    }

    private static JsonNode CreateSchoolRequestBody(QuerySchoolSeed schoolSeed)
    {
        return JsonNode.Parse(
            $$"""
            {
              "schoolId": {{schoolSeed.SchoolId}},
              "nameOfInstitution": "{{schoolSeed.NameOfInstitution}}",
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
            """
        )!;
    }

    private PageKeysetSpec.Query AssertSingleQueryHydration()
    {
        _recorder.HydrationKeysets.Should().ContainSingle();
        _recorder.HydrationKeysets[0].Should().BeOfType<PageKeysetSpec.Query>();
        return (PageKeysetSpec.Query)_recorder.HydrationKeysets[0];
    }

    private void AssertPageMaterialization(params long[] expectedDocumentIds)
    {
        _recorder.PageMaterializationCallCount.Should().Be(1);
        _recorder.SingleDocumentMaterializationCallCount.Should().Be(0);
        _recorder.PageMaterializedDocumentIds.Should().Equal(expectedDocumentIds);
    }

    private static void AssertCourseTranscriptReferenceAliasSql(PageKeysetSpec.Query keyset)
    {
        keyset.Plan.PageDocumentIdSql.Should().Contain("FROM \"edfi\".\"CourseTranscript\" r");
        keyset
            .Plan.PageDocumentIdSql.Should()
            .Contain("r.\"StudentAcademicRecord_StudentUniqueId\" = @studentUniqueId");
        keyset.Plan.PageDocumentIdSql.Should().NotContain("JOIN \"edfi\".\"Student\"");
        keyset.Plan.PageDocumentIdSql.Should().NotContain("JOIN \"edfi\".\"StudentAcademicRecord\"");
        keyset.Plan.PageDocumentIdSql.Should().NotContain("JOIN \"dms\".\"Document\"");
    }

    private static void AssertCourseTranscriptQueryDocument(
        JsonNode? document,
        PersistedCourseTranscript expectedCourseTranscript
    )
    {
        document.Should().NotBeNull();
        document!["id"]!.GetValue<string>().Should().Be(expectedCourseTranscript.DocumentUuid.ToString());
        document["courseAttemptResultDescriptor"]!
            .GetValue<string>()
            .Should()
            .Be(CourseAttemptResultDescriptorUri);
        document["courseReference"]!["courseCode"]!
            .GetValue<string>()
            .Should()
            .Be(expectedCourseTranscript.CourseCode);
        document["studentAcademicRecordReference"]!["studentUniqueId"]!
            .GetValue<string>()
            .Should()
            .Be(expectedCourseTranscript.StudentUniqueId);
    }

    private static void AssertSchoolQueryDocument(JsonNode? document, PersistedQuerySchool expectedSchool)
    {
        document.Should().NotBeNull();
        document!["id"]!.GetValue<string>().Should().Be(expectedSchool.DocumentUuid.ToString());
        document["schoolId"]!.GetValue<long>().Should().Be(expectedSchool.SchoolId);
        document["nameOfInstitution"]!.GetValue<string>().Should().Be(expectedSchool.NameOfInstitution);

        document["educationOrganizationCategories"]!
            .AsArray()
            .Select(category => category!["educationOrganizationCategoryDescriptor"]!.GetValue<string>())
            .Should()
            .Equal("uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School");
        document["gradeLevels"]!
            .AsArray()
            .Select(gradeLevel => gradeLevel!["gradeLevelDescriptor"]!.GetValue<string>())
            .Should()
            .Equal(
                "uri://ed-fi.org/GradeLevelDescriptor#Tenth grade",
                "uri://ed-fi.org/GradeLevelDescriptor#Ninth grade"
            );
        document["addresses"]!
            .AsArray()
            .Select(address => new
            {
                AddressTypeDescriptor = address!["addressTypeDescriptor"]!.GetValue<string>(),
                StateAbbreviationDescriptor = address["stateAbbreviationDescriptor"]!.GetValue<string>(),
                City = address["city"]!.GetValue<string>(),
                PostalCode = address["postalCode"]!.GetValue<string>(),
                StreetNumberName = address["streetNumberName"]!.GetValue<string>(),
                DoNotPublishIndicator = address["doNotPublishIndicator"]!.GetValue<bool>(),
            })
            .Should()
            .Equal(
                new
                {
                    AddressTypeDescriptor = "uri://ed-fi.org/AddressTypeDescriptor#Physical",
                    StateAbbreviationDescriptor = "uri://ed-fi.org/StateAbbreviationDescriptor#TX",
                    City = "Austin",
                    PostalCode = "78701",
                    StreetNumberName = "100 Congress Ave",
                    DoNotPublishIndicator = false,
                },
                new
                {
                    AddressTypeDescriptor = "uri://ed-fi.org/AddressTypeDescriptor#Mailing",
                    StateAbbreviationDescriptor = "uri://ed-fi.org/StateAbbreviationDescriptor#TX",
                    City = "Austin",
                    PostalCode = "78702",
                    StreetNumberName = "200 Trinity St",
                    DoNotPublishIndicator = true,
                }
            );
    }

    private void SetSelectedInstance(IServiceProvider serviceProvider)
    {
        serviceProvider
            .GetRequiredService<IDmsInstanceSelection>()
            .SetSelectedDmsInstance(
                new DmsInstance(
                    Id: 1,
                    InstanceType: "test",
                    InstanceName: "PostgresqlRelationalQueryExecution",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );
    }

    private async Task InsertEducationOrganizationIdentityAsync(long documentId, long educationOrganizationId)
    {
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO "edfi"."EducationOrganizationIdentity" (
                "DocumentId",
                "EducationOrganizationId",
                "Discriminator"
            )
            VALUES (@documentId, @educationOrganizationId, @discriminator)
            ON CONFLICT DO NOTHING;
            """,
            new NpgsqlParameter("documentId", documentId),
            new NpgsqlParameter("educationOrganizationId", educationOrganizationId),
            new NpgsqlParameter("discriminator", "Ed-Fi:School")
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

    private async Task InsertStudentAsync(long documentId, string studentUniqueId)
    {
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO "edfi"."Student" (
                "DocumentId",
                "BirthDate",
                "FirstName",
                "LastSurname",
                "StudentUniqueId"
            )
            VALUES (@documentId, @birthDate, @firstName, @lastSurname, @studentUniqueId);
            """,
            new NpgsqlParameter("documentId", documentId),
            new NpgsqlParameter("birthDate", new DateOnly(2010, 1, 1)),
            new NpgsqlParameter("firstName", "Query"),
            new NpgsqlParameter("lastSurname", "Student"),
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
            VALUES (@documentId, @currentSchoolYear, @schoolYear, @schoolYearDescription);
            """,
            new NpgsqlParameter("documentId", documentId),
            new NpgsqlParameter("currentSchoolYear", true),
            new NpgsqlParameter("schoolYear", schoolYear),
            new NpgsqlParameter("schoolYearDescription", $"{schoolYear} School Year")
        );
    }

    private async Task InsertCourseAsync(
        long documentId,
        long educationOrganizationDocumentId,
        long educationOrganizationId,
        string courseCode
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
            new NpgsqlParameter("courseTitle", "Algebra I"),
            new NpgsqlParameter("numberOfParts", 1)
        );
    }

    private async Task InsertStudentAcademicRecordAsync(
        long documentId,
        long educationOrganizationDocumentId,
        long educationOrganizationId,
        long schoolYearDocumentId,
        long studentDocumentId
    )
    {
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO "edfi"."StudentAcademicRecord" (
                "DocumentId",
                "EducationOrganization_DocumentId",
                "EducationOrganization_EducationOrganizationId",
                "SchoolYear_DocumentId",
                "SchoolYear_SchoolYear",
                "Student_DocumentId",
                "Student_StudentUniqueId",
                "TermDescriptor_DescriptorId"
            )
            VALUES (
                @documentId,
                @educationOrganizationDocumentId,
                @educationOrganizationId,
                @schoolYearDocumentId,
                @schoolYear,
                @studentDocumentId,
                @studentUniqueId,
                @termDescriptorId
            );
            """,
            new NpgsqlParameter("documentId", documentId),
            new NpgsqlParameter("educationOrganizationDocumentId", educationOrganizationDocumentId),
            new NpgsqlParameter("educationOrganizationId", educationOrganizationId),
            new NpgsqlParameter("schoolYearDocumentId", schoolYearDocumentId),
            new NpgsqlParameter("schoolYear", CourseTranscriptSchoolYear),
            new NpgsqlParameter("studentDocumentId", studentDocumentId),
            new NpgsqlParameter("studentUniqueId", CourseTranscriptStudentUniqueId),
            new NpgsqlParameter("termDescriptorId", _termDescriptorId)
        );
    }

    private async Task InsertCourseTranscriptAsync(
        long documentId,
        long courseDocumentId,
        long studentAcademicRecordDocumentId,
        long educationOrganizationId
    )
    {
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO "edfi"."CourseTranscript" (
                "DocumentId",
                "CourseCourse_DocumentId",
                "CourseCourse_CourseCode",
                "CourseCourse_EducationOrganizationId",
                "StudentAcademicRecord_DocumentId",
                "StudentAcademicRecord_EducationOrganizationId",
                "StudentAcademicRecord_SchoolYear",
                "StudentAcademicRecord_StudentUniqueId",
                "StudentAcademicRecord_TermDescriptor_DescriptorId",
                "CourseAttemptResultDescriptor_DescriptorId"
            )
            VALUES (
                @documentId,
                @courseDocumentId,
                @courseCode,
                @educationOrganizationId,
                @studentAcademicRecordDocumentId,
                @educationOrganizationId,
                @schoolYear,
                @studentUniqueId,
                @termDescriptorId,
                @courseAttemptResultDescriptorId
            );
            """,
            new NpgsqlParameter("documentId", documentId),
            new NpgsqlParameter("courseDocumentId", courseDocumentId),
            new NpgsqlParameter("courseCode", CourseTranscriptCourseCode),
            new NpgsqlParameter("educationOrganizationId", educationOrganizationId),
            new NpgsqlParameter("studentAcademicRecordDocumentId", studentAcademicRecordDocumentId),
            new NpgsqlParameter("schoolYear", CourseTranscriptSchoolYear),
            new NpgsqlParameter("studentUniqueId", CourseTranscriptStudentUniqueId),
            new NpgsqlParameter("termDescriptorId", _termDescriptorId),
            new NpgsqlParameter("courseAttemptResultDescriptorId", _courseAttemptResultDescriptorId)
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

    private static ReferentialId CreateDescriptorReferentialId(
        string projectName,
        string resourceName,
        string descriptorUri
    )
    {
        return ReferentialIdCalculator.ReferentialIdFrom(
            new BaseResourceInfo(new ProjectName(projectName), new ResourceName(resourceName), true),
            new DocumentIdentity([
                new DocumentIdentityElement(
                    DocumentIdentity.DescriptorIdentityJsonPath,
                    descriptorUri.ToLowerInvariant()
                ),
            ])
        );
    }

    private static QueryElement CreateQueryElement(string queryFieldName, string path, string value)
    {
        return new QueryElement(queryFieldName, [new JsonPath(path)], value, "string");
    }

    private static long GetRequiredInt64(IReadOnlyDictionary<string, object?> row, string columnName) =>
        Convert.ToInt64(GetRequiredValue(row, columnName), CultureInfo.InvariantCulture);

    private static int GetRequiredInt32(IReadOnlyDictionary<string, object?> row, string columnName) =>
        Convert.ToInt32(GetRequiredValue(row, columnName), CultureInfo.InvariantCulture);

    private static Guid GetRequiredGuid(IReadOnlyDictionary<string, object?> row, string columnName)
    {
        return GetRequiredValue(row, columnName) is Guid value
            ? value
            : throw new InvalidOperationException($"Expected column '{columnName}' to contain a Guid value.");
    }

    private static string GetRequiredString(IReadOnlyDictionary<string, object?> row, string columnName)
    {
        return GetRequiredValue(row, columnName) as string
            ?? throw new InvalidOperationException(
                $"Expected column '{columnName}' to contain a string value."
            );
    }

    private static object GetRequiredValue(IReadOnlyDictionary<string, object?> row, string columnName)
    {
        if (!row.TryGetValue(columnName, out var value) || value is null)
        {
            throw new InvalidOperationException($"Expected row to contain non-null column '{columnName}'.");
        }

        return value;
    }
}

// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Globalization;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
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
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

internal sealed class PostgresqlRelationalQueryExecutionRecorder
{
    public List<PageKeysetSpec> HydrationKeysets { get; } = [];
    public List<long> PageMaterializedDocumentIds { get; } = [];
    public Func<CancellationToken, Task>? BeforeNextHydrationAsync { get; set; }
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

    public async Task InvokeBeforeHydrationAsync(CancellationToken cancellationToken)
    {
        var beforeHydrationAsync = BeforeNextHydrationAsync;

        if (beforeHydrationAsync is null)
        {
            return;
        }

        BeforeNextHydrationAsync = null;
        await beforeHydrationAsync(cancellationToken);
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
        HydrationExecutionOptions executionOptions,
        CancellationToken ct
    )
    {
        await _recorder.InvokeBeforeHydrationAsync(ct);
        _recorder.HydrationKeysets.Add(keyset);

        await using var connection = await _dataSourceProvider.DataSource.OpenConnectionAsync(ct);

        return await HydrationExecutor.ExecuteAsync(
            connection,
            plan,
            keyset,
            SqlDialect.Pgsql,
            transaction: null,
            executionOptions,
            ct
        );
    }
}

internal sealed class RecordingRelationalReadMaterializer(PostgresqlRelationalQueryExecutionRecorder recorder)
    : IRelationalReadMaterializer
{
    private readonly PostgresqlRelationalQueryExecutionRecorder _recorder =
        recorder ?? throw new ArgumentNullException(nameof(recorder));
    private readonly RelationalReadMaterializer _inner = new(
        new IntegrationFixtureSlugResolver(),
        Microsoft.Extensions.Options.Options.Create(new ResourceLinksOptions()),
        new EdFi.DataManagementService.Backend.Etag.ServedEtagComposer()
    );

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

    public void StripReferenceLinks(JsonNode document, ResourceReadPlan readPlan) =>
        _inner.StripReferenceLinks(document, readPlan);
}

internal sealed class IntegrationFixtureSlugResolver : IDocumentLinkSlugResolver
{
    public DocumentLinkSlugTriple Resolve(MappingSet mappingSet, string discriminator) =>
        new(ProjectEndpointName: "test", EndpointName: "tests", ResourceName: "Test");
}

internal sealed class ThrowingRelationalReadTargetLookupService : IRelationalReadTargetLookupService
{
    public Task<RelationalReadTargetLookupResult> ResolveForGetByIdAsync(
        DbTableName rootTable,
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
    string NameOfInstitution,
    long ContentVersion
);

[TestFixture]
[NonParallelizable]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
public class Given_A_Postgresql_Relational_Query_With_The_Authoritative_Ds52_School_Fixture
{
    private const string FixtureRelativePath = "src/dms/backend/Fixtures/authoritative/ds-5.2";
    private const int MaximumPageSize = 500;

    /// <summary>
    /// The descriptor URIs the descriptor-filter tests query by. <c>schoolTypeDescriptor</c> is a
    /// root-level descriptor on <c>edfi.School</c>, so the compiled query capability binds it to
    /// <c>RelationalQueryFieldTarget.DescriptorIdColumn</c> — the only query target that resolves a
    /// reference at request time.
    /// </summary>
    private const string RegularSchoolTypeDescriptorUri = "uri://ed-fi.org/SchoolTypeDescriptor#Regular";
    private const string AlternativeSchoolTypeDescriptorUri =
        "uri://ed-fi.org/SchoolTypeDescriptor#Alternative";
    private const string CaseVariantRegularSchoolTypeDescriptorUri =
        "URI://ED-FI.org/SchoolTYPEDescriptor#rEgUlAr";
    private const string UnseededSchoolTypeDescriptorUri = "uri://ed-fi.org/SchoolTypeDescriptor#Nonexistent";
    private const string CharterStatusDescriptorUri =
        "uri://ed-fi.org/CharterStatusDescriptor#School Charter";

    private static readonly QualifiedResourceName SchoolResource = new("Ed-Fi", "School");
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

    /// <summary>
    /// Two of the three seeded schools share a school type so a descriptor-URI filter has to both
    /// include and exclude.
    /// </summary>
    private static readonly Dictionary<int, string> _schoolTypeDescriptorUriBySchoolId = new()
    {
        [255901] = RegularSchoolTypeDescriptorUri,
        [255902] = AlternativeSchoolTypeDescriptorUri,
        [255903] = RegularSchoolTypeDescriptorUri,
    };

    private PostgresqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private PostgresqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;
    private PostgresqlRelationalQueryExecutionRecorder _recorder = null!;
    private ResourceInfo _resourceInfo = null!;
    private ResourceSchema _resourceSchema = null!;
    private IReadOnlyList<PersistedQuerySchool> _persistedSchoolsInDocumentOrder = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _fixture = PostgresqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            FixtureRelativePath,
            strict: true
        );
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

        await SeedReferenceDataAsync();

        foreach (var schoolSeed in _schoolSeeds)
        {
            var createResult = await ExecuteCreateAsync(schoolSeed);
            createResult.Should().BeOfType<UpsertResult.InsertSuccess>();
        }

        _persistedSchoolsInDocumentOrder = await ReadPersistedSchoolsInDocumentOrderAsync();
        _persistedSchoolsInDocumentOrder.Should().HaveCount(3);
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
    public async Task It_filters_by_a_descriptor_uri_and_returns_only_matching_resources()
    {
        var expectedSchools = _persistedSchoolsInDocumentOrder
            .Where(school =>
                _schoolTypeDescriptorUriBySchoolId[school.SchoolId] == RegularSchoolTypeDescriptorUri
            )
            .ToArray();

        expectedSchools.Should().HaveCount(2);

        var result = await ExecuteQueryAsync(
            [
                CreateQueryElement(
                    "schoolTypeDescriptor",
                    "$.schoolTypeDescriptor",
                    RegularSchoolTypeDescriptorUri
                ),
            ],
            limit: 25,
            offset: 0,
            totalCount: true,
            traceId: "pg-query-descriptor-filter"
        );

        var success = result.Should().BeOfType<QueryResult.QuerySuccess>().Subject;

        success.TotalCount.Should().Be(2);
        success
            .EdfiDocs.Select(document => document!["id"]!.GetValue<string>())
            .Should()
            .Equal(expectedSchools.Select(school => school.DocumentUuid.ToString()));
        success
            .EdfiDocs.Select(document => document!["schoolTypeDescriptor"]!.GetValue<string>())
            .Should()
            .AllBe(RegularSchoolTypeDescriptorUri);

        AssertSingleQueryHydration().Plan.TotalCountSql.Should().NotBeNull();
        AssertPageMaterialization(expectedSchools.Select(school => school.DocumentId).ToArray());
    }

    [Test]
    public async Task It_matches_a_case_variant_descriptor_uri_against_the_stored_descriptor()
    {
        // The stored descriptor URI keeps its original casing; the preprocessor lower-cases the query
        // value before resolution, so a case-variant filter has to return exactly the same page.
        var expectedSchools = _persistedSchoolsInDocumentOrder
            .Where(school =>
                _schoolTypeDescriptorUriBySchoolId[school.SchoolId] == RegularSchoolTypeDescriptorUri
            )
            .ToArray();

        CaseVariantRegularSchoolTypeDescriptorUri.Should().NotBe(RegularSchoolTypeDescriptorUri);
        CaseVariantRegularSchoolTypeDescriptorUri
            .ToLowerInvariant()
            .Should()
            .Be(RegularSchoolTypeDescriptorUri.ToLowerInvariant());

        var result = await ExecuteQueryAsync(
            [
                CreateQueryElement(
                    "schoolTypeDescriptor",
                    "$.schoolTypeDescriptor",
                    CaseVariantRegularSchoolTypeDescriptorUri
                ),
            ],
            limit: 25,
            offset: 0,
            totalCount: true,
            traceId: "pg-query-descriptor-filter-case-variant"
        );

        var success = result.Should().BeOfType<QueryResult.QuerySuccess>().Subject;

        success.TotalCount.Should().Be(2);
        success
            .EdfiDocs.Select(document => document!["id"]!.GetValue<string>())
            .Should()
            .Equal(expectedSchools.Select(school => school.DocumentUuid.ToString()));
        success
            .EdfiDocs.Select(document => document!["schoolTypeDescriptor"]!.GetValue<string>())
            .Should()
            .AllBe(RegularSchoolTypeDescriptorUri);

        AssertPageMaterialization(expectedSchools.Select(school => school.DocumentId).ToArray());
    }

    [Test]
    public async Task It_returns_an_empty_page_for_a_descriptor_uri_that_does_not_exist()
    {
        var totalCountResult = await ExecuteQueryAsync(
            [
                CreateQueryElement(
                    "schoolTypeDescriptor",
                    "$.schoolTypeDescriptor",
                    UnseededSchoolTypeDescriptorUri
                ),
            ],
            limit: 25,
            offset: 0,
            totalCount: true,
            traceId: "pg-query-descriptor-filter-missing-total-count"
        );

        totalCountResult.Should().BeEquivalentTo(new QueryResult.QuerySuccess([], 0));
        AssertNoQueryExecution();

        _recorder.Reset();

        var withoutTotalCountResult = await ExecuteQueryAsync(
            [
                CreateQueryElement(
                    "schoolTypeDescriptor",
                    "$.schoolTypeDescriptor",
                    UnseededSchoolTypeDescriptorUri
                ),
            ],
            limit: 25,
            offset: 0,
            totalCount: false,
            traceId: "pg-query-descriptor-filter-missing"
        );

        withoutTotalCountResult.Should().BeEquivalentTo(new QueryResult.QuerySuccess([], null));
        AssertNoQueryExecution();
    }

    [Test]
    public async Task It_returns_an_empty_page_for_a_descriptor_uri_of_another_descriptor_type()
    {
        // The URI exists, but as a CharterStatusDescriptor rather than the SchoolTypeDescriptor the
        // query field targets. The referential-id resolver reported this as Missing and the natural-key
        // resolver reports DescriptorTypeMismatch; both short-circuit to the same 200 empty page, so the
        // reason-code delta stays invisible at the query surface.
        var result = await ExecuteQueryAsync(
            [
                CreateQueryElement(
                    "schoolTypeDescriptor",
                    "$.schoolTypeDescriptor",
                    CharterStatusDescriptorUri
                ),
            ],
            limit: 25,
            offset: 0,
            totalCount: true,
            traceId: "pg-query-descriptor-filter-wrong-type"
        );

        result.Should().BeEquivalentTo(new QueryResult.QuerySuccess([], 0));
        AssertNoQueryExecution();
    }

    [Test]
    public async Task It_returns_only_resources_inside_the_change_version_window()
    {
        // The stamping triggers assign strictly increasing ContentVersion values in insert order,
        // so a window spanning only the middle school's stamp excludes the first and last.
        var middleSchool = _persistedSchoolsInDocumentOrder[1];

        var result = await ExecuteQueryAsync(
            [],
            limit: 25,
            offset: 0,
            totalCount: true,
            traceId: "pg-query-change-version-window",
            changeVersionRange: new ChangeVersionRange(
                middleSchool.ContentVersion,
                middleSchool.ContentVersion
            )
        );

        var success = result.Should().BeOfType<QueryResult.QuerySuccess>().Subject;

        success.TotalCount.Should().Be(1);
        success.EdfiDocs.Should().HaveCount(1);
        success.EdfiDocs[0]!["id"]!.GetValue<string>().Should().Be(middleSchool.DocumentUuid.ToString());
        AssertPageMaterialization(middleSchool.DocumentId);
    }

    [Test]
    public async Task It_returns_resources_at_or_above_min_change_version_and_excludes_older_resources()
    {
        var lastSchool = _persistedSchoolsInDocumentOrder[^1];

        var result = await ExecuteQueryAsync(
            [],
            limit: 25,
            offset: 0,
            totalCount: true,
            traceId: "pg-query-change-version-min-only",
            changeVersionRange: new ChangeVersionRange(lastSchool.ContentVersion, null)
        );

        var success = result.Should().BeOfType<QueryResult.QuerySuccess>().Subject;

        success.TotalCount.Should().Be(1);
        success.EdfiDocs.Should().HaveCount(1);
        success.EdfiDocs[0]!["id"]!.GetValue<string>().Should().Be(lastSchool.DocumentUuid.ToString());
    }

    [Test]
    public async Task It_returns_an_empty_page_when_the_change_version_window_excludes_all_resources()
    {
        var lastSchool = _persistedSchoolsInDocumentOrder[^1];

        var result = await ExecuteQueryAsync(
            [],
            limit: 25,
            offset: 0,
            totalCount: true,
            traceId: "pg-query-change-version-exclusion",
            changeVersionRange: new ChangeVersionRange(lastSchool.ContentVersion + 1, null)
        );

        var success = result.Should().BeOfType<QueryResult.QuerySuccess>().Subject;

        success.TotalCount.Should().Be(0);
        success.EdfiDocs.Should().BeEmpty();
    }

    [Test]
    public async Task It_composes_the_change_version_window_with_a_query_filter()
    {
        // The window covers every seeded school; the scalar filter then narrows to one. A second
        // query keeps the filter but shrinks the window below the match, proving both predicates apply.
        var middleSchool = _persistedSchoolsInDocumentOrder[1];
        var allVersionsWindow = new ChangeVersionRange(
            _persistedSchoolsInDocumentOrder[0].ContentVersion,
            _persistedSchoolsInDocumentOrder[^1].ContentVersion
        );

        var matchingResult = await ExecuteQueryAsync(
            [CreateQueryElement("nameOfInstitution", "$.nameOfInstitution", middleSchool.NameOfInstitution)],
            limit: 25,
            offset: 0,
            totalCount: true,
            traceId: "pg-query-change-version-composed-match",
            changeVersionRange: allVersionsWindow
        );

        var matchingSuccess = matchingResult.Should().BeOfType<QueryResult.QuerySuccess>().Subject;
        matchingSuccess.TotalCount.Should().Be(1);
        matchingSuccess.EdfiDocs.Should().HaveCount(1);
        matchingSuccess.EdfiDocs[0]!["id"]!
            .GetValue<string>()
            .Should()
            .Be(middleSchool.DocumentUuid.ToString());

        _recorder.Reset();

        var excludedResult = await ExecuteQueryAsync(
            [CreateQueryElement("nameOfInstitution", "$.nameOfInstitution", middleSchool.NameOfInstitution)],
            limit: 25,
            offset: 0,
            totalCount: true,
            traceId: "pg-query-change-version-composed-excluded",
            changeVersionRange: new ChangeVersionRange(null, middleSchool.ContentVersion - 1)
        );

        var excludedSuccess = excludedResult.Should().BeOfType<QueryResult.QuerySuccess>().Subject;
        excludedSuccess.TotalCount.Should().Be(0);
        excludedSuccess.EdfiDocs.Should().BeEmpty();
    }

    private static ServiceProvider CreateServiceProvider()
    {
        ServiceCollection services = [];

        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<NpgsqlDataSourceCache>();
        services.AddScoped<IDataStoreSelection, DataStoreSelection>();
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
            AllowIdentityUpdates: resourceSchema.AllowIdentityUpdates
        );

    private async Task SeedReferenceDataAsync()
    {
        await SeedDescriptorAsync(
            Guid.Parse("10111111-1111-1111-1111-111111111111"),
            "AddressTypeDescriptor",
            "AddressTypeDescriptor",
            "uri://ed-fi.org/AddressTypeDescriptor#Physical",
            "uri://ed-fi.org/AddressTypeDescriptor",
            "Physical",
            "Physical"
        );
        await SeedDescriptorAsync(
            Guid.Parse("20222222-2222-2222-2222-222222222222"),
            "AddressTypeDescriptor",
            "AddressTypeDescriptor",
            "uri://ed-fi.org/AddressTypeDescriptor#Mailing",
            "uri://ed-fi.org/AddressTypeDescriptor",
            "Mailing",
            "Mailing"
        );
        await SeedDescriptorAsync(
            Guid.Parse("30333333-3333-3333-3333-333333333333"),
            "StateAbbreviationDescriptor",
            "StateAbbreviationDescriptor",
            "uri://ed-fi.org/StateAbbreviationDescriptor#TX",
            "uri://ed-fi.org/StateAbbreviationDescriptor",
            "TX",
            "Texas"
        );
        await SeedDescriptorAsync(
            Guid.Parse("40444444-4444-4444-4444-444444444444"),
            "EducationOrganizationCategoryDescriptor",
            "EducationOrganizationCategoryDescriptor",
            "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School",
            "uri://ed-fi.org/EducationOrganizationCategoryDescriptor",
            "School",
            "School"
        );
        await SeedDescriptorAsync(
            Guid.Parse("50555555-5555-5555-5555-555555555555"),
            "GradeLevelDescriptor",
            "GradeLevelDescriptor",
            "uri://ed-fi.org/GradeLevelDescriptor#Ninth grade",
            "uri://ed-fi.org/GradeLevelDescriptor",
            "Ninth grade",
            "Ninth grade"
        );
        await SeedDescriptorAsync(
            Guid.Parse("60666666-6666-6666-6666-666666666666"),
            "GradeLevelDescriptor",
            "GradeLevelDescriptor",
            "uri://ed-fi.org/GradeLevelDescriptor#Tenth grade",
            "uri://ed-fi.org/GradeLevelDescriptor",
            "Tenth grade",
            "Tenth grade"
        );
        await SeedDescriptorAsync(
            Guid.Parse("70777777-7777-7777-7777-777777777777"),
            "SchoolTypeDescriptor",
            "SchoolTypeDescriptor",
            RegularSchoolTypeDescriptorUri,
            "uri://ed-fi.org/SchoolTypeDescriptor",
            "Regular",
            "Regular"
        );
        await SeedDescriptorAsync(
            Guid.Parse("80888888-8888-8888-8888-888888888888"),
            "SchoolTypeDescriptor",
            "SchoolTypeDescriptor",
            AlternativeSchoolTypeDescriptorUri,
            "uri://ed-fi.org/SchoolTypeDescriptor",
            "Alternative",
            "Alternative"
        );

        // Seeded only so the wrong-descriptor-type filter names a URI that really exists. No school
        // references it, so a resource query filtered by it can only ever produce an empty page.
        await SeedDescriptorAsync(
            Guid.Parse("90999999-9999-9999-9999-999999999999"),
            "CharterStatusDescriptor",
            "CharterStatusDescriptor",
            CharterStatusDescriptorUri,
            "uri://ed-fi.org/CharterStatusDescriptor",
            "School Charter",
            "School Charter"
        );
    }

    private async Task SeedDescriptorAsync(
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
            DocumentUuid: schoolSeed.DocumentUuid
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
        ChangeVersionRange? changeVersionRange = null
    )
    {
        await using var scope = _serviceProvider.CreateAsyncScope();
        SetSelectedInstance(scope.ServiceProvider);

        var request = new RelationalQueryRequest(
            ResourceInfo: _resourceInfo,
            AuthorizationContext: new RelationalAuthorizationContext([]),
            MappingSet: _mappingSet,
            QueryElements: queryElements,
            AuthorizationStrategyEvaluators: [],
            PaginationParameters: new PaginationParameters(
                Limit: limit,
                Offset: offset,
                TotalCount: totalCount,
                MaximumPageSize: MaximumPageSize
            ),
            TraceId: new TraceId(traceId),
            ChangeVersionRange: changeVersionRange
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
                school."NameOfInstitution",
                school."ContentVersion"
            FROM "dms"."Document" doc
            INNER JOIN "{physicalSchema}"."School" school
                ON school."DocumentId" = doc."DocumentId"
            INNER JOIN "dms"."Descriptor" schoolType
                ON schoolType."DocumentId" = school."SchoolTypeDescriptor_DescriptorId"
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
                NameOfInstitution: GetRequiredString(row, "NameOfInstitution"),
                ContentVersion: GetRequiredInt64(row, "ContentVersion")
            )),
        ];
    }

    private static JsonNode CreateSchoolRequestBody(QuerySchoolSeed schoolSeed)
    {
        return JsonNode.Parse(
            $$"""
            {
              "schoolId": {{schoolSeed.SchoolId}},
              "nameOfInstitution": "{{schoolSeed.NameOfInstitution}}",
              "schoolTypeDescriptor": "{{_schoolTypeDescriptorUriBySchoolId[schoolSeed.SchoolId]}}",
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

    /// <summary>
    /// Pins the preprocessing short-circuit: an empty page produced before planning never hydrates and
    /// never materializes, which is what distinguishes it from a page query that simply matched no rows.
    /// </summary>
    private void AssertNoQueryExecution()
    {
        _recorder.HydrationKeysets.Should().BeEmpty();
        _recorder.PageMaterializationCallCount.Should().Be(0);
        _recorder.SingleDocumentMaterializationCallCount.Should().Be(0);
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
            .GetRequiredService<IDataStoreSelection>()
            .SetSelectedDataStore(
                new DataStore(
                    Id: 1,
                    DataStoreType: "test",
                    Name: "PostgresqlRelationalQueryExecution",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
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

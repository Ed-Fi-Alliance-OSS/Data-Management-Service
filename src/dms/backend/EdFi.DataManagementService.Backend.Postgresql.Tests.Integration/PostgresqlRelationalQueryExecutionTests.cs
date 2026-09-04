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
    public DocumentLinkSlugTriple Resolve(MappingSet mappingSet, short resourceKeyId) =>
        new(ProjectEndpointName: "test", EndpointName: "tests", ResourceName: "Test");
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
        // Re-read state so document assertions reflect current field values regardless of
        // which tests (including ordering tests that update a school) ran first.
        var currentSchools = await ReadPersistedSchoolsInDocumentOrderAsync();

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
            .Equal(currentSchools[0].DocumentUuid.ToString(), currentSchools[1].DocumentUuid.ToString());
        AssertSchoolQueryDocument(firstPageSuccess.EdfiDocs[0], currentSchools[0]);
        AssertSchoolQueryDocument(firstPageSuccess.EdfiDocs[1], currentSchools[1]);
        AssertSingleQueryHydration().Plan.TotalCountSql.Should().NotBeNull();
        AssertPageMaterialization(currentSchools[0].DocumentId, currentSchools[1].DocumentId);

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
            .Equal(currentSchools[2].DocumentUuid.ToString());
        AssertSchoolQueryDocument(secondPageSuccess.EdfiDocs[0], currentSchools[2]);
        AssertSingleQueryHydration().Plan.TotalCountSql.Should().NotBeNull();
        AssertPageMaterialization(currentSchools[2].DocumentId);
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
    public async Task It_returns_only_resources_inside_the_change_version_window()
    {
        // Re-read state so the window anchors on current ContentVersions regardless of
        // which tests (including ordering tests that update a school) ran first.
        var currentSchools = await ReadPersistedSchoolsInDocumentOrderAsync();
        var middleByContentVersion = currentSchools.OrderBy(s => s.ContentVersion).ElementAt(1);

        var result = await ExecuteQueryAsync(
            [],
            limit: 25,
            offset: 0,
            totalCount: true,
            traceId: "pg-query-change-version-window",
            changeVersionRange: new ChangeVersionRange(
                middleByContentVersion.ContentVersion,
                middleByContentVersion.ContentVersion
            )
        );

        var success = result.Should().BeOfType<QueryResult.QuerySuccess>().Subject;

        success.TotalCount.Should().Be(1);
        success.EdfiDocs.Should().HaveCount(1);
        success.EdfiDocs[0]!["id"]!
            .GetValue<string>()
            .Should()
            .Be(middleByContentVersion.DocumentUuid.ToString());
        AssertPageMaterialization(middleByContentVersion.DocumentId);
    }

    [Test]
    public async Task It_returns_resources_at_or_above_min_change_version_and_excludes_older_resources()
    {
        var currentSchools = await ReadPersistedSchoolsInDocumentOrderAsync();
        var middleSchool = currentSchools.OrderBy(s => s.ContentVersion).ElementAt(1);

        var result = await ExecuteQueryAsync(
            [],
            limit: 25,
            offset: 0,
            totalCount: true,
            traceId: "pg-query-change-version-min-only",
            changeVersionRange: new ChangeVersionRange(middleSchool.ContentVersion, null)
        );

        var success = result.Should().BeOfType<QueryResult.QuerySuccess>().Subject;

        success.TotalCount.Should().Be(2);
        success.EdfiDocs.Should().HaveCount(2);
        success
            .EdfiDocs.Select(document => document!["id"]!.GetValue<string>())
            .Should()
            .BeEquivalentTo(
                middleSchool.DocumentUuid.ToString(),
                currentSchools.OrderBy(s => s.ContentVersion).Last().DocumentUuid.ToString()
            );
    }

    [Test]
    public async Task It_returns_an_empty_page_when_the_change_version_window_excludes_all_resources()
    {
        var currentSchools = await ReadPersistedSchoolsInDocumentOrderAsync();
        var maxContentVersion = currentSchools.Max(s => s.ContentVersion);

        var result = await ExecuteQueryAsync(
            [],
            limit: 25,
            offset: 0,
            totalCount: true,
            traceId: "pg-query-change-version-exclusion",
            changeVersionRange: new ChangeVersionRange(maxContentVersion + 1, null)
        );

        var success = result.Should().BeOfType<QueryResult.QuerySuccess>().Subject;

        success.TotalCount.Should().Be(0);
        success.EdfiDocs.Should().BeEmpty();
    }

    [Test]
    public async Task It_composes_the_change_version_window_with_a_query_filter()
    {
        // The window starts at a real row's version, and the scalar filter selects that same row.
        // A second query keeps the filter but shrinks the window below the match, proving both
        // predicates apply and that the lower bound is inclusive.
        var firstSchool = _persistedSchoolsInDocumentOrder[0];
        var allVersionsWindow = new ChangeVersionRange(
            firstSchool.ContentVersion,
            _persistedSchoolsInDocumentOrder[^1].ContentVersion
        );

        var matchingResult = await ExecuteQueryAsync(
            [CreateQueryElement("nameOfInstitution", "$.nameOfInstitution", firstSchool.NameOfInstitution)],
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
            .Be(firstSchool.DocumentUuid.ToString());

        _recorder.Reset();

        var excludedResult = await ExecuteQueryAsync(
            [CreateQueryElement("nameOfInstitution", "$.nameOfInstitution", firstSchool.NameOfInstitution)],
            limit: 25,
            offset: 0,
            totalCount: true,
            traceId: "pg-query-change-version-composed-excluded",
            changeVersionRange: new ChangeVersionRange(null, firstSchool.ContentVersion - 1)
        );

        var excludedSuccess = excludedResult.Should().BeOfType<QueryResult.QuerySuccess>().Subject;
        excludedSuccess.TotalCount.Should().Be(0);
        excludedSuccess.EdfiDocs.Should().BeEmpty();
    }

    /// <summary>
    /// Re-upserts the first seeded school with a changed name so its ContentVersion becomes the
    /// largest, making ContentVersion order diverge from DocumentId order, then returns fresh state.
    /// </summary>
    private async Task<IReadOnlyList<PersistedQuerySchool>> UpdateFirstSchoolAndReadStateAsync()
    {
        var seed = _schoolSeeds[0];
        var updateResult = await ExecuteCreateAsync(
            seed with
            {
                NameOfInstitution = $"{seed.NameOfInstitution} (updated)",
            }
        );
        updateResult.Should().BeOfType<UpsertResult.UpdateSuccess>();

        var refreshedSchools = await ReadPersistedSchoolsInDocumentOrderAsync();
        refreshedSchools.Should().HaveCount(3);
        refreshedSchools[0]
            .ContentVersion.Should()
            .BeGreaterThan(
                refreshedSchools[^1].ContentVersion,
                "the update must move the first school's ContentVersion past every other school's"
            );

        return refreshedSchools;
    }

    /// <summary>
    /// Walks a windowed collection one document per page.
    /// </summary>
    /// <remarks>
    /// The anchor is a required argument rather than derived from
    /// <paramref name="changeVersionRange" />, because deriving it here would reimplement the Core
    /// resolver these tests exist to check the consequences of. DocumentId is the enum's zero value, so
    /// a defaulted anchor would also let a case that means to prove ContentVersion ordering pass
    /// against DocumentId ordering it never asked for.
    /// </remarks>
    private async Task<IReadOnlyList<Guid>> WalkPagesAsync(
        ChangeVersionRange changeVersionRange,
        PageOrderingMode pageOrderingMode,
        int pageCount,
        string traceIdPrefix
    )
    {
        var walkedDocumentUuids = new List<Guid>();

        for (var offset = 0; offset < pageCount; offset++)
        {
            var result = await ExecuteQueryAsync(
                [],
                limit: 1,
                offset: offset,
                totalCount: offset == 0,
                traceId: $"{traceIdPrefix}-{offset}",
                changeVersionRange: changeVersionRange,
                pageOrderingMode: pageOrderingMode
            );

            var success = result.Should().BeOfType<QueryResult.QuerySuccess>().Subject;

            if (offset == 0)
            {
                success.TotalCount.Should().Be(pageCount);
            }

            success.EdfiDocs.Should().HaveCount(1);
            walkedDocumentUuids.Add(Guid.Parse(success.EdfiDocs[0]!["id"]!.GetValue<string>()));
        }

        return walkedDocumentUuids;
    }

    [Test]
    public async Task It_pages_a_bounded_change_version_window_by_content_version()
    {
        var refreshedSchools = await UpdateFirstSchoolAndReadStateAsync();
        var byContentVersion = refreshedSchools.OrderBy(s => s.ContentVersion).ToArray();
        var byDocumentId = refreshedSchools.Select(s => s.DocumentUuid).ToArray();
        var expectedProgression = byContentVersion.Select(s => s.DocumentUuid).ToArray();
        expectedProgression.Should().NotEqual(byDocumentId, "the scenario must discriminate the orders");

        var walkedDocumentUuids = await WalkPagesAsync(
            new ChangeVersionRange(byContentVersion[0].ContentVersion, byContentVersion[^1].ContentVersion),
            PageOrderingMode.ContentVersion,
            pageCount: refreshedSchools.Count,
            traceIdPrefix: "pg-cv-ordering-bounded"
        );

        walkedDocumentUuids.Should().Equal(expectedProgression);
        walkedDocumentUuids.Should().OnlyHaveUniqueItems();
    }

    [Test]
    public async Task It_pages_a_max_only_change_version_window_by_content_version()
    {
        var refreshedSchools = await UpdateFirstSchoolAndReadStateAsync();
        var byContentVersion = refreshedSchools.OrderBy(s => s.ContentVersion).ToArray();
        var expectedProgression = byContentVersion.Select(s => s.DocumentUuid).ToArray();

        var walkedDocumentUuids = await WalkPagesAsync(
            new ChangeVersionRange(null, byContentVersion[^1].ContentVersion),
            PageOrderingMode.ContentVersion,
            pageCount: refreshedSchools.Count,
            traceIdPrefix: "pg-cv-ordering-max-only"
        );

        walkedDocumentUuids.Should().Equal(expectedProgression);
    }

    [Test]
    public async Task It_pages_a_min_only_change_version_window_by_document_id()
    {
        var refreshedSchools = await UpdateFirstSchoolAndReadStateAsync();
        var minContentVersion = refreshedSchools.Min(s => s.ContentVersion);
        var expectedProgression = refreshedSchools.Select(s => s.DocumentUuid).ToArray();

        var walkedDocumentUuids = await WalkPagesAsync(
            new ChangeVersionRange(minContentVersion, null),
            PageOrderingMode.DocumentId,
            pageCount: refreshedSchools.Count,
            traceIdPrefix: "pg-cv-ordering-min-only"
        );

        walkedDocumentUuids.Should().Equal(expectedProgression);
    }

    /// <summary>
    /// The combination a change-version read served from a frozen snapshot resolves: an open-ended
    /// window paged under the ContentVersion anchor. Against live data this shape keeps DocumentId,
    /// because an update can move a row later within the still-open window; nothing moves in a frozen
    /// source, so the anchor that makes the window a range seek becomes safe to use.
    /// </summary>
    /// <remarks>
    /// The expectation cannot be reached by accident. The fixture re-upserts the first school, so its
    /// ContentVersion order and its DocumentId order differ — asserted here rather than assumed — and
    /// the same window paged under the other anchor returns the other progression, which is exactly
    /// what It_pages_a_min_only_change_version_window_by_document_id asserts.
    /// </remarks>
    [Test]
    public async Task It_pages_a_min_only_change_version_window_by_content_version()
    {
        var refreshedSchools = await UpdateFirstSchoolAndReadStateAsync();
        var byContentVersion = refreshedSchools.OrderBy(s => s.ContentVersion).ToArray();
        var inDocumentOrder = refreshedSchools.Select(s => s.DocumentUuid).ToArray();
        var expectedProgression = byContentVersion.Select(s => s.DocumentUuid).ToArray();

        expectedProgression
            .Should()
            .NotEqual(inDocumentOrder, "the scenario must discriminate the two orders");

        var walkedDocumentUuids = await WalkPagesAsync(
            new ChangeVersionRange(byContentVersion[0].ContentVersion, null),
            PageOrderingMode.ContentVersion,
            pageCount: refreshedSchools.Count,
            traceIdPrefix: "pg-cv-ordering-min-only-content-version"
        );

        walkedDocumentUuids.Should().Equal(expectedProgression);
        walkedDocumentUuids.Should().OnlyHaveUniqueItems();
    }

    /// <summary>
    /// The ordering a bounded window is paged in is the one the request carries, not one derived from
    /// the window here: a request whose anchor is DocumentId over a bounded window — what a deployment
    /// running with the legacy ordering switch produces — pages in DocumentId order.
    /// </summary>
    /// <remarks>
    /// This is the case whose expectation cannot be reached by accident: the fixture's ContentVersion
    /// order differs from its DocumentId order, so the same window paged under the other anchor returns
    /// the other progression, which is what the bounded-window test above asserts.
    /// </remarks>
    [Test]
    public async Task It_pages_a_bounded_window_by_document_id_when_the_request_anchors_on_it()
    {
        var refreshedSchools = await UpdateFirstSchoolAndReadStateAsync();
        var byContentVersion = refreshedSchools.OrderBy(s => s.ContentVersion).ToArray();
        var expectedProgression = refreshedSchools.Select(s => s.DocumentUuid).ToArray();

        var walkedDocumentUuids = await WalkPagesAsync(
            new ChangeVersionRange(byContentVersion[0].ContentVersion, byContentVersion[^1].ContentVersion),
            PageOrderingMode.DocumentId,
            pageCount: refreshedSchools.Count,
            traceIdPrefix: "pg-cv-ordering-document-id-anchor"
        );

        walkedDocumentUuids.Should().Equal(expectedProgression);
    }

    // A cursor walk over the seeded collection: each page selects from the range the previous page's
    // boundary opened, and the walk ends on a page that selects nothing. Every document is returned
    // exactly once, which is the property a client depends on and the one an off-by-one boundary breaks.
    [Test]
    public async Task It_walks_every_document_exactly_once_across_cursor_pages()
    {
        var expectedDocumentIds = _persistedSchoolsInDocumentOrder
            .Select(static school => school.DocumentId)
            .ToArray();

        List<long> walkedDocumentIds = [];
        var range = CursorRange.From(1);
        var pageCount = 0;

        while (pageCount++ < expectedDocumentIds.Length + 1)
        {
            _recorder.Reset();
            var success = (QueryResult.QuerySuccess)
                await ExecuteCursorQueryAsync(range, pageSize: 2, traceId: $"pg-cursor-walk-{pageCount}");

            // One command per page, and no count SQL on any of them.
            var keyset = AssertSingleQueryHydration();
            keyset.Plan.TotalCountSql.Should().BeNull();
            success.TotalCount.Should().BeNull();

            walkedDocumentIds.AddRange(_recorder.PageMaterializedDocumentIds);

            if (success.HighestSelectedAnchor is not { } highestSelectedDocumentId)
            {
                success.EdfiDocs.Should().BeEmpty();
                break;
            }

            range = new CursorRange(highestSelectedDocumentId + 1, range.InclusiveMaximum);
        }

        walkedDocumentIds.Should().Equal(expectedDocumentIds);
    }

    [Test]
    public async Task It_selects_one_document_per_page_at_page_size_one()
    {
        var success = (QueryResult.QuerySuccess)
            await ExecuteCursorQueryAsync(CursorRange.From(1), pageSize: 1, traceId: "pg-cursor-size-1");

        success.HighestSelectedAnchor.Should().Be(_persistedSchoolsInDocumentOrder[0].DocumentId);
        AssertPageMaterialization(_persistedSchoolsInDocumentOrder[0].DocumentId);
    }

    [Test]
    public async Task It_selects_the_whole_collection_at_the_configured_maximum_page_size()
    {
        var success = (QueryResult.QuerySuccess)
            await ExecuteCursorQueryAsync(
                CursorRange.From(1),
                pageSize: MaximumPageSize,
                traceId: "pg-cursor-size-max"
            );

        success.HighestSelectedAnchor.Should().Be(_persistedSchoolsInDocumentOrder[^1].DocumentId);
        AssertPageMaterialization([
            .. _persistedSchoolsInDocumentOrder.Select(static school => school.DocumentId),
        ]);
    }

    // A zero page size selects nothing and cannot advance a walk, by contract rather than by arithmetic.
    [Test]
    public async Task It_selects_nothing_at_page_size_zero()
    {
        var success = (QueryResult.QuerySuccess)
            await ExecuteCursorQueryAsync(CursorRange.From(1), pageSize: 0, traceId: "pg-cursor-size-0");

        success.HighestSelectedAnchor.Should().BeNull();
        success.EdfiDocs.Should().BeEmpty();
    }

    // An inverted range is the terminal condition of a bounded walk, not an error.
    [Test]
    public async Task It_selects_nothing_from_an_inverted_range()
    {
        var lastDocumentId = _persistedSchoolsInDocumentOrder[^1].DocumentId;

        var success = (QueryResult.QuerySuccess)
            await ExecuteCursorQueryAsync(
                new CursorRange(lastDocumentId + 1, lastDocumentId),
                pageSize: 25,
                traceId: "pg-cursor-inverted"
            );

        success.HighestSelectedAnchor.Should().BeNull();
        success.EdfiDocs.Should().BeEmpty();
    }

    // Range bounds are seek positions, not identities. Neither bound here is a stored DocumentId, and
    // the page still selects exactly the documents inside the range — which is what keeps a walk correct
    // across the identity gaps deletes leave behind.
    [Test]
    public async Task It_seeks_within_bounds_that_are_not_stored_document_ids()
    {
        var firstDocumentId = _persistedSchoolsInDocumentOrder[0].DocumentId;
        var lastDocumentId = _persistedSchoolsInDocumentOrder[^1].DocumentId;
        var storedDocumentIds = _persistedSchoolsInDocumentOrder
            .Select(static school => school.DocumentId)
            .ToArray();

        storedDocumentIds.Should().NotContain(firstDocumentId - 1).And.NotContain(lastDocumentId + 1);

        var success = (QueryResult.QuerySuccess)
            await ExecuteCursorQueryAsync(
                new CursorRange(firstDocumentId - 1, lastDocumentId + 1),
                pageSize: 25,
                traceId: "pg-cursor-unstored-bounds"
            );

        success.HighestSelectedAnchor.Should().Be(lastDocumentId);
        AssertPageMaterialization(storedDocumentIds);
    }

    // A bound that excludes the first stored id starts the page at the next one, so a continuation that
    // resumes at maximum+1 cannot re-return the document it already delivered.
    [Test]
    public async Task It_excludes_documents_below_the_inclusive_minimum()
    {
        var firstDocumentId = _persistedSchoolsInDocumentOrder[0].DocumentId;

        var success = (QueryResult.QuerySuccess)
            await ExecuteCursorQueryAsync(
                CursorRange.From(firstDocumentId + 1),
                pageSize: 25,
                traceId: "pg-cursor-excludes-below-minimum"
            );

        success.HighestSelectedAnchor.Should().Be(_persistedSchoolsInDocumentOrder[^1].DocumentId);
        AssertPageMaterialization([
            .. _persistedSchoolsInDocumentOrder.Skip(1).Select(static school => school.DocumentId),
        ]);
    }

    [Test]
    public async Task It_composes_a_query_filter_with_the_cursor_range()
    {
        var targetSchool = _persistedSchoolsInDocumentOrder[1];

        var success = (QueryResult.QuerySuccess)
            await ExecuteCursorQueryAsync(
                CursorRange.From(1),
                pageSize: 25,
                traceId: "pg-cursor-filter",
                queryElements:
                [
                    new QueryElement(
                        "nameOfInstitution",
                        [new JsonPath("$.nameOfInstitution")],
                        targetSchool.NameOfInstitution,
                        "string"
                    ),
                ]
            );

        success.HighestSelectedAnchor.Should().Be(targetSchool.DocumentId);
        AssertPageMaterialization(targetSchool.DocumentId);
    }

    // Against current data a min-only window still orders by DocumentId, so a cursor page inside it
    // continues normally. The snapshot half of the rule is
    // It_pages_a_min_only_change_version_window_by_content_version above.
    [Test]
    public async Task It_composes_a_min_only_change_version_window_with_the_cursor_range()
    {
        var lowestContentVersion = _persistedSchoolsInDocumentOrder.Min(static school =>
            school.ContentVersion
        );

        var success = (QueryResult.QuerySuccess)
            await ExecuteCursorQueryAsync(
                CursorRange.From(1),
                pageSize: 25,
                traceId: "pg-cursor-min-window",
                changeVersionRange: new ChangeVersionRange(lowestContentVersion, null)
            );

        success.HighestSelectedAnchor.Should().Be(_persistedSchoolsInDocumentOrder[^1].DocumentId);
    }

    // The ordering is supplied to the planner here rather than resolved from the window, so what this
    // pins is composition: a max-bearing window has to narrow the rows a DocumentId-anchored cursor
    // page selects without silently re-ordering it, and the page must still anchor a continuation in
    // the units it was planned for. Which anchor a max-bearing window resolves to in production is
    // ChangeQueryPageOrderingPolicy's decision, not this fixture's.
    [Test]
    public async Task It_composes_a_max_bearing_change_version_window_with_the_cursor_range()
    {
        var highestContentVersion = _persistedSchoolsInDocumentOrder.Max(static school =>
            school.ContentVersion
        );

        var success = (QueryResult.QuerySuccess)
            await ExecuteCursorQueryAsync(
                CursorRange.From(1),
                pageSize: 25,
                traceId: "pg-cursor-max-window",
                changeVersionRange: new ChangeVersionRange(null, highestContentVersion)
            );

        success.HighestSelectedAnchor.Should().Be(_persistedSchoolsInDocumentOrder[^1].DocumentId);
        AssertSingleQueryHydration().Plan.PageDocumentIdSql.Should().Contain("@cursorMin");
    }

    // A traditional page over the same window is ordered by ContentVersion, so the maximum it reports is
    // that window's highest ContentVersion and not a DocumentId at all.
    [Test]
    public async Task It_reports_a_content_version_boundary_for_a_windowed_traditional_page()
    {
        // Read fresh rather than from the seeded snapshot: other cases in this fixture update a school,
        // which moves its ContentVersion without moving its DocumentId, so only the live values bound
        // the window this page is selected over.
        var currentSchools = await ReadPersistedSchoolsInDocumentOrderAsync();
        var highestContentVersion = currentSchools.Max(static school => school.ContentVersion);

        var success = (QueryResult.QuerySuccess)
            await ExecuteQueryAsync(
                [],
                limit: MaximumPageSize,
                offset: 0,
                totalCount: false,
                traceId: "pg-traditional-max-window",
                changeVersionRange: new ChangeVersionRange(null, highestContentVersion),
                pageOrderingMode: PageOrderingMode.ContentVersion
            );

        success.HighestSelectedAnchor.Should().Be(highestContentVersion);
    }

    // The boundary set must be anchored on identifiers the caller can actually reach, and every range
    // but the last must close one before the next begins, or a client walking the partitions in parallel
    // would miss or repeat documents.
    [Test]
    public async Task It_partitions_the_school_candidate_set_into_contiguous_ranges_on_real_document_ids()
    {
        var currentSchools = await ReadPersistedSchoolsInDocumentOrderAsync();

        var result = await ExecutePartitionsAsync(
            _resourceInfo,
            [],
            requestedPartitionCount: 3,
            minimumPartitionSize: 1,
            traceId: "pg-partitions-all"
        );

        var success = result.Should().BeOfType<PartitionResult.PartitionSuccess>().Subject;

        success
            .Ranges.Select(range => range.InclusiveMinimum)
            .Should()
            .Equal(currentSchools.Select(school => school.DocumentId));
        success.Ranges[^1].InclusiveMaximum.Should().Be(long.MaxValue);

        for (var index = 0; index + 1 < success.Ranges.Count; index++)
        {
            success
                .Ranges[index]
                .InclusiveMaximum.Should()
                .Be(success.Ranges[index + 1].InclusiveMinimum - 1);
        }

        _recorder.HydrationKeysets.Should().BeEmpty("a boundary calculation hydrates nothing");
        _recorder.PageMaterializationCallCount.Should().Be(0);
    }

    [Test]
    public async Task It_partitions_only_the_filtered_candidate_set()
    {
        var currentSchools = await ReadPersistedSchoolsInDocumentOrderAsync();
        var targetSchool = currentSchools[1];

        var result = await ExecutePartitionsAsync(
            _resourceInfo,
            [CreateQueryElement("nameOfInstitution", "$.nameOfInstitution", targetSchool.NameOfInstitution)],
            requestedPartitionCount: 5,
            minimumPartitionSize: 1,
            traceId: "pg-partitions-filtered"
        );

        result
            .Should()
            .BeOfType<PartitionResult.PartitionSuccess>()
            .Which.Ranges.Should()
            .Equal(new CursorRange(targetSchool.DocumentId, long.MaxValue));
    }

    [Test]
    public async Task It_returns_no_partitions_when_the_change_version_window_excludes_every_school()
    {
        var currentSchools = await ReadPersistedSchoolsInDocumentOrderAsync();
        var maxContentVersion = currentSchools.Max(static school => school.ContentVersion);

        var result = await ExecutePartitionsAsync(
            _resourceInfo,
            [],
            requestedPartitionCount: 4,
            minimumPartitionSize: 1,
            traceId: "pg-partitions-empty-window",
            changeVersionRange: new ChangeVersionRange(maxContentVersion + 1, null)
        );

        result.Should().BeOfType<PartitionResult.PartitionSuccess>().Which.Ranges.Should().BeEmpty();
    }

    [Test]
    public async Task It_partitions_descriptors_over_the_shared_descriptor_table()
    {
        var (descriptorProjectSchema, descriptorResourceSchema) = GetResourceSchema(
            _fixture.EffectiveSchemaSet,
            "ed-fi",
            "GradeLevelDescriptor"
        );
        var descriptorResourceInfo = CreateResourceInfo(descriptorProjectSchema, descriptorResourceSchema);
        var expectedDocumentIds = await ReadDescriptorDocumentIdsAsync("GradeLevelDescriptor");

        expectedDocumentIds
            .Should()
            .HaveCount(2, "the seeded grade levels are what the descriptor boundaries are calculated over");

        var result = await ExecutePartitionsAsync(
            descriptorResourceInfo,
            [],
            requestedPartitionCount: 2,
            minimumPartitionSize: 1,
            traceId: "pg-partitions-descriptor"
        );

        var success = result.Should().BeOfType<PartitionResult.PartitionSuccess>().Subject;

        success.Ranges.Select(range => range.InclusiveMinimum).Should().Equal(expectedDocumentIds);
        success.Ranges[^1].InclusiveMaximum.Should().Be(long.MaxValue);
    }

    private async Task<PartitionResult> ExecutePartitionsAsync(
        ResourceInfo resourceInfo,
        QueryElement[] queryElements,
        int requestedPartitionCount,
        long minimumPartitionSize,
        string traceId,
        ChangeVersionRange? changeVersionRange = null
    )
    {
        await using var scope = _serviceProvider.CreateAsyncScope();
        SetSelectedInstance(scope.ServiceProvider);

        var request = new RelationalPartitionRequest(
            ResourceInfo: resourceInfo,
            AuthorizationContext: new RelationalAuthorizationContext([]),
            MappingSet: _mappingSet,
            QueryElements: queryElements,
            AuthorizationStrategyEvaluators: [],
            RequestedPartitionCount: requestedPartitionCount,
            MinimumPartitionSize: minimumPartitionSize,
            TraceId: new TraceId(traceId),
            PageOrderingMode: PageOrderingMode.DocumentId,
            ChangeVersionRange: changeVersionRange
        );

        return await scope
            .ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>()
            .QueryPartitions(request);
    }

    private async Task<IReadOnlyList<long>> ReadDescriptorDocumentIdsAsync(string resourceName)
    {
        var resourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", resourceName);
        var rows = await _database.QueryRowsAsync(
            """
            SELECT "DocumentId"
            FROM "dms"."Descriptor"
            WHERE "ResourceKeyId" = @resourceKeyId
            ORDER BY "DocumentId";
            """,
            new NpgsqlParameter("resourceKeyId", resourceKeyId)
        );

        return [.. rows.Select(row => GetRequiredInt64(row, "DocumentId"))];
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
        services.AddPostgresqlBackendIntegrationTestServices();
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
        ChangeVersionRange? changeVersionRange = null,
        PageOrderingMode pageOrderingMode = PageOrderingMode.DocumentId
    ) =>
        await ExecuteQueryAsync(
            new CollectionPaging.Traditional(
                new PaginationParameters(
                    Limit: limit,
                    Offset: offset,
                    TotalCount: totalCount,
                    MaximumPageSize: MaximumPageSize
                )
            ),
            queryElements,
            traceId,
            changeVersionRange,
            pageOrderingMode
        );

    private async Task<QueryResult> ExecuteCursorQueryAsync(
        CursorRange range,
        int pageSize,
        string traceId,
        QueryElement[]? queryElements = null,
        ChangeVersionRange? changeVersionRange = null
    ) =>
        await ExecuteQueryAsync(
            new CollectionPaging.Cursor(range, new PageSize(pageSize)),
            queryElements ?? [],
            traceId,
            changeVersionRange
        );

    private async Task<QueryResult> ExecuteQueryAsync(
        CollectionPaging paging,
        QueryElement[] queryElements,
        string traceId,
        ChangeVersionRange? changeVersionRange = null,
        PageOrderingMode pageOrderingMode = PageOrderingMode.DocumentId
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
            Paging: paging,
            TraceId: new TraceId(traceId),
            ChangeVersionRange: changeVersionRange,
            PageOrderingMode: pageOrderingMode
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
                "ResourceKeyId",
                "Namespace",
                "CodeValue",
                "ShortDescription",
                "Description",
                "Discriminator",
                "Uri"
            )
            VALUES (
                @documentId,
                @resourceKeyId,
                @namespace,
                @codeValue,
                @shortDescription,
                @description,
                @discriminator,
                @uri
            );
            """,
            new NpgsqlParameter("documentId", documentId),
            new NpgsqlParameter("resourceKeyId", resourceKeyId),
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

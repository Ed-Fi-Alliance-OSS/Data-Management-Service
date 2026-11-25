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
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

/// <summary>
/// Identification-code collection filters executed against PostgreSQL over the authoritative Data
/// Standard 5.2 schema: every supplied predicate must hold for one collection element, a resource matched
/// by several elements is returned once and counted once, and every paging mode and replayed partition
/// selects the same filtered set, with and without a change-version window.
/// </summary>
[TestFixture]
[NonParallelizable]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
public class Given_A_Postgresql_Identification_Code_Query_With_The_Authoritative_Ds52_Fixture
{
    private const string FixtureRelativePath = "src/dms/backend/Fixtures/authoritative/ds-5.2";
    private const int MaximumPageSize = 500;

    private const string SchoolCodePath = "$.identificationCodes[*].identificationCode";
    private const string SchoolSystemPath =
        "$.identificationCodes[*].educationOrganizationIdentificationSystemDescriptor";
    private const string StaffCodePath = "$.identificationCodes[*].identificationCode";
    private const string StaffSystemPath = "$.identificationCodes[*].staffIdentificationSystemDescriptor";
    private const string StaffAssigningOrganizationPath =
        "$.identificationCodes[*].assigningOrganizationIdentificationCode";

    private const string Nces = "uri://ed-fi.org/EducationOrganizationIdentificationSystemDescriptor#NCES";
    private const string Sea = "uri://ed-fi.org/EducationOrganizationIdentificationSystemDescriptor#SEA";
    private const string StateStaffSystem = "uri://ed-fi.org/StaffIdentificationSystemDescriptor#State";
    private const string DistrictStaffSystem = "uri://ed-fi.org/StaffIdentificationSystemDescriptor#District";

    private const string RootCourseCatalogUrl = "https://root.example.org/catalog";
    private const string ItemCourseCatalogUrl = "https://item.example.org/catalog";

    private static readonly QualifiedResourceName SchoolResource = new("Ed-Fi", "School");
    private static readonly QualifiedResourceName CourseTranscriptResource = new("Ed-Fi", "CourseTranscript");

    // Seeded in this order, so ContentVersion increases in this order too.
    private static readonly Guid AlphaUuid = Guid.Parse("d8100000-0000-0000-0000-000000000001");
    private static readonly Guid BravoUuid = Guid.Parse("d8100000-0000-0000-0000-000000000002");
    private static readonly Guid CharlieUuid = Guid.Parse("d8100000-0000-0000-0000-000000000003");
    private static readonly Guid DeltaUuid = Guid.Parse("d8100000-0000-0000-0000-000000000004");
    private static readonly Guid EchoUuid = Guid.Parse("d8100000-0000-0000-0000-000000000005");
    private static readonly Guid FoxtrotUuid = Guid.Parse("d8100000-0000-0000-0000-000000000006");
    private static readonly Guid StaffAUuid = Guid.Parse("d8100000-0000-0000-0000-0000000000a1");
    private static readonly Guid StaffBUuid = Guid.Parse("d8100000-0000-0000-0000-0000000000b2");
    private static readonly Guid CourseTranscriptUuid = Guid.Parse("d8100000-0000-0000-0000-0000000000c7");

    private static readonly (Guid Uuid, long SchoolId, string Name, string? Codes)[] _schoolSeeds =
    [
        (
            AlphaUuid,
            810001,
            "Alpha",
            $$"""[{ "identificationCode": "C-100", "educationOrganizationIdentificationSystemDescriptor": "{{Nces}}" }]"""
        ),
        // The same code under two systems: two collection rows match a code-only filter.
        (
            BravoUuid,
            810002,
            "Bravo",
            $$"""[{ "identificationCode": "C-200", "educationOrganizationIdentificationSystemDescriptor": "{{Nces}}" }, { "identificationCode": "C-200", "educationOrganizationIdentificationSystemDescriptor": "{{Sea}}" }]"""
        ),
        (CharlieUuid, 810003, "Charlie", null),
        (DeltaUuid, 810004, "Delta", "[]"),
        (
            EchoUuid,
            810005,
            "Echo",
            $$"""[{ "identificationCode": "C-100", "educationOrganizationIdentificationSystemDescriptor": "{{Sea}}" }]"""
        ),
        // C-600 and SEA both occur, but never on the same element.
        (
            FoxtrotUuid,
            810006,
            "Foxtrot",
            $$"""[{ "identificationCode": "C-600", "educationOrganizationIdentificationSystemDescriptor": "{{Nces}}" }, { "identificationCode": "C-601", "educationOrganizationIdentificationSystemDescriptor": "{{Sea}}" }]"""
        ),
    ];

    private PostgresqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private PostgresqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;
    private Dictionary<string, (ResourceInfo Info, ResourceSchema Schema)> _resources = null!;

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
        _resources = new Dictionary<string, (ResourceInfo, ResourceSchema)>(StringComparer.Ordinal);

        foreach (
            var resourceName in new[]
            {
                "School",
                "Staff",
                "Student",
                "SchoolYearType",
                "Course",
                "StudentAcademicRecord",
                "CourseTranscript",
            }
        )
        {
            _resources[resourceName] = GetResource(resourceName);
        }

        await SeedDescriptorsAsync();

        foreach (var (uuid, schoolId, name, codes) in _schoolSeeds)
        {
            await UpsertAsync("School", uuid, SchoolBody(schoolId, name, codes));
        }

        await UpsertAsync(
            "Staff",
            StaffAUuid,
            StaffBody(
                "S-A",
                $$"""[{ "identificationCode": "X", "staffIdentificationSystemDescriptor": "{{StateStaffSystem}}", "assigningOrganizationIdentificationCode": "O1" }]"""
            )
        );
        await UpsertAsync(
            "Staff",
            StaffBUuid,
            StaffBody(
                "S-B",
                $$"""[{ "identificationCode": "X", "staffIdentificationSystemDescriptor": "{{StateStaffSystem}}", "assigningOrganizationIdentificationCode": "O2" }, { "identificationCode": "Y", "staffIdentificationSystemDescriptor": "{{DistrictStaffSystem}}", "assigningOrganizationIdentificationCode": "O1" }]"""
            )
        );

        await SeedCourseTranscriptAsync();
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
    public async Task It_returns_only_resources_with_a_matching_collection_element()
    {
        var success = await OffsetPageAsync(
            "School",
            [Element("identificationCode", SchoolCodePath, "C-600")]
        );

        Ids(success).Should().Equal(FoxtrotUuid);
        success.TotalCount.Should().Be(1);
    }

    [Test]
    public async Task It_returns_an_empty_page_when_no_element_matches()
    {
        var success = await OffsetPageAsync(
            "School",
            [Element("identificationCode", SchoolCodePath, "C-999")]
        );

        Ids(success).Should().BeEmpty();
        success.TotalCount.Should().Be(0);
    }

    [Test]
    public async Task It_returns_a_resource_matched_by_several_elements_once_and_counts_it_once()
    {
        var success = await OffsetPageAsync(
            "School",
            [Element("identificationCode", SchoolCodePath, "C-200")]
        );

        Ids(success).Should().Equal(BravoUuid);
        success.TotalCount.Should().Be(1);
    }

    [Test]
    public async Task It_excludes_resources_with_a_missing_or_empty_collection()
    {
        var success = await OffsetPageAsync(
            "School",
            [Element("educationOrganizationIdentificationSystemDescriptor", SchoolSystemPath, Nces)]
        );

        Ids(success).Should().Equal(AlphaUuid, BravoUuid, FoxtrotUuid);
        success.TotalCount.Should().Be(3);
    }

    [Test]
    public async Task It_narrows_a_code_by_its_system_on_the_same_element()
    {
        var codeOnly = await OffsetPageAsync(
            "School",
            [Element("identificationCode", SchoolCodePath, "C-100")]
        );
        var codeAndSystem = await OffsetPageAsync(
            "School",
            [
                Element("identificationCode", SchoolCodePath, "C-100"),
                Element("educationOrganizationIdentificationSystemDescriptor", SchoolSystemPath, Sea),
            ]
        );

        Ids(codeOnly).Should().Equal(AlphaUuid, EchoUuid);
        Ids(codeAndSystem).Should().Equal(EchoUuid);
        codeAndSystem.TotalCount.Should().Be(1);
    }

    [Test]
    public async Task It_does_not_match_a_code_and_system_that_occur_only_on_different_elements()
    {
        var differentElements = await OffsetPageAsync(
            "School",
            [
                Element("identificationCode", SchoolCodePath, "C-600"),
                Element("educationOrganizationIdentificationSystemDescriptor", SchoolSystemPath, Sea),
            ]
        );
        var sameElement = await OffsetPageAsync(
            "School",
            [
                Element("identificationCode", SchoolCodePath, "C-601"),
                Element("educationOrganizationIdentificationSystemDescriptor", SchoolSystemPath, Sea),
            ]
        );

        Ids(differentElements).Should().BeEmpty();
        differentElements.TotalCount.Should().Be(0);
        Ids(sameElement).Should().Equal(FoxtrotUuid);
    }

    [Test]
    public async Task It_combines_a_collection_filter_with_a_root_filter()
    {
        var success = await OffsetPageAsync(
            "School",
            [
                Element("identificationCode", SchoolCodePath, "C-100"),
                Element("nameOfInstitution", "$.nameOfInstitution", "Alpha"),
            ]
        );

        Ids(success).Should().Equal(AlphaUuid);
        success.TotalCount.Should().Be(1);
    }

    [TestCase("uri://ed-fi.org/EducationOrganizationIdentificationSystemDescriptor#Unknown")]
    [TestCase("uri://ed-fi.org/NoFragment")]
    public async Task It_returns_an_empty_page_for_a_descriptor_that_does_not_resolve(string descriptor)
    {
        var success = await OffsetPageAsync(
            "School",
            [Element("educationOrganizationIdentificationSystemDescriptor", SchoolSystemPath, descriptor)]
        );

        Ids(success).Should().BeEmpty();
        success.TotalCount.Should().Be(0);
    }

    [Test]
    public async Task It_matches_a_non_descriptor_qualifier_alone_and_on_the_same_element_as_the_code()
    {
        var qualifierOnly = await OffsetPageAsync(
            "Staff",
            [Element("assigningOrganizationIdentificationCode", StaffAssigningOrganizationPath, "O1")]
        );
        var codeAndQualifier = await OffsetPageAsync(
            "Staff",
            [
                Element("identificationCode", StaffCodePath, "X"),
                Element("assigningOrganizationIdentificationCode", StaffAssigningOrganizationPath, "O1"),
            ]
        );
        var otherElement = await OffsetPageAsync(
            "Staff",
            [
                Element("identificationCode", StaffCodePath, "Y"),
                Element("assigningOrganizationIdentificationCode", StaffAssigningOrganizationPath, "O1"),
                Element("staffIdentificationSystemDescriptor", StaffSystemPath, DistrictStaffSystem),
            ]
        );

        Ids(qualifierOnly).Should().Equal(StaffAUuid, StaffBUuid);
        qualifierOnly.TotalCount.Should().Be(2);
        // Staff B has code X and qualifier O1, but on different elements.
        Ids(codeAndQualifier).Should().Equal(StaffAUuid);
        codeAndQualifier.TotalCount.Should().Be(1);
        Ids(otherElement).Should().Equal(StaffBUuid);
    }

    [Test]
    public async Task It_keeps_the_course_transcript_root_meaning_of_the_colliding_names()
    {
        var capability = _mappingSet.GetQueryCapabilityOrThrow(CourseTranscriptResource);

        capability
            .SupportedFieldsByQueryField["courseCatalogURL"]
            .Target.Should()
            .BeOfType<RelationalQueryFieldTarget.RootColumn>();
        capability
            .SupportedFieldsByQueryField["assigningOrganizationIdentificationCode"]
            .Target.Should()
            .BeOfType<RelationalQueryFieldTarget.RootColumn>();

        (await QueryTranscriptsAsync("courseCatalogURL", "$.courseCatalogURL", RootCourseCatalogUrl))
            .Should()
            .Equal(CourseTranscriptUuid);
        (await QueryTranscriptsAsync("courseCatalogURL", "$.courseCatalogURL", ItemCourseCatalogUrl))
            .Should()
            .BeEmpty("the collection element's catalog URL is not what this filter means");
        (
            await QueryTranscriptsAsync(
                "assigningOrganizationIdentificationCode",
                "$.assigningOrganizationIdentificationCode",
                "ROOT-ORG"
            )
        )
            .Should()
            .Equal(CourseTranscriptUuid);
        (
            await QueryTranscriptsAsync(
                "assigningOrganizationIdentificationCode",
                "$.assigningOrganizationIdentificationCode",
                "ITEM-ORG"
            )
        )
            .Should()
            .BeEmpty("the collection element's assigning organization is not what this filter means");
        (
            await QueryTranscriptsAsync(
                "identificationCode",
                "$.alternativeCourseIdentificationCodes[*].identificationCode",
                "ALT-1"
            )
        )
            .Should()
            .Equal(CourseTranscriptUuid);
    }

    [Test]
    public async Task It_selects_the_same_filtered_set_in_every_paging_mode_and_partition_replay()
    {
        QueryElement[] filter =
        [
            Element("educationOrganizationIdentificationSystemDescriptor", SchoolSystemPath, Nces),
        ];
        Guid[] expected = [AlphaUuid, BravoUuid, FoxtrotUuid];

        await AssertEveryModeSelectsAsync(filter, window: null, expected);
    }

    [Test]
    public async Task It_selects_the_same_filtered_set_in_every_paging_mode_and_partition_replay_inside_a_change_version_window()
    {
        var anchors = await ReadSchoolAnchorsAsync();
        var window = new ChangeVersionRange(
            anchors[BravoUuid].ContentVersion,
            anchors[FoxtrotUuid].ContentVersion
        );
        QueryElement[] filter =
        [
            Element("educationOrganizationIdentificationSystemDescriptor", SchoolSystemPath, Nces),
        ];

        // Alpha matches the filter but falls below the window; Charlie, Delta and Echo fall inside the
        // window but do not match the filter.
        await AssertEveryModeSelectsAsync(filter, window, [BravoUuid, FoxtrotUuid]);
    }

    /// <summary>
    /// Candidate-SQL evidence only, not a cache hit: the document cache selects candidates with a metadata
    /// batch built from the same planned page and count SQL. Executing that batch here shows it selects
    /// the same documents and count as the relational path; whether a request actually takes the cache
    /// page-hit path is proved over HTTP with cache telemetry.
    /// </summary>
    [Test]
    public async Task It_selects_the_same_candidates_and_count_with_the_document_cache_candidate_batch()
    {
        var readPlan = _mappingSet.GetReadPlanOrThrow(SchoolResource);
        var capability = _mappingSet.GetQueryCapabilityOrThrow(SchoolResource);
        var keyset = new RelationalQueryPageKeysetPlanner(SqlDialect.Pgsql).Plan(
            readPlan.Model.Root,
            new RelationalQueryPreprocessingResult(
                new RelationalQueryPreprocessingOutcome.Continue(),
                [
                    new PreprocessedRelationalQueryElement(
                        Element("identificationCode", SchoolCodePath, "C-100"),
                        capability.SupportedFieldsByQueryField["identificationCode"],
                        new PreprocessedRelationalQueryValue.Raw("C-100")
                    ),
                ]
            ),
            new CollectionPaging.Traditional(
                new PaginationParameters(
                    Limit: 25,
                    Offset: 0,
                    TotalCount: true,
                    MaximumPageSize: MaximumPageSize
                )
            ),
            resourceTables: readPlan.Model.TablesInDependencyOrder
        );
        var batch = HydrationBatchBuilder.BuildCandidateMetadataBatch(readPlan, keyset, SqlDialect.Pgsql);

        batch.Should().Contain("EXISTS (SELECT 1 FROM \"edfi\".\"SchoolIdentificationCode\" ci");

        var (candidateUuids, candidateCount) = await ExecuteCandidateBatchAsync(batch, keyset);
        var relational = await OffsetPageAsync(
            "School",
            [Element("identificationCode", SchoolCodePath, "C-100")]
        );

        candidateUuids.Should().Equal(AlphaUuid, EchoUuid);
        candidateUuids.Should().Equal(Ids(relational));
        candidateCount.Should().Be(relational.TotalCount);
    }

    /// <summary>
    /// Records the PostgreSQL plans of representative filters for the step report. Seeded volumes are far
    /// too small for the planner's choice to mean anything, so only what every valid plan must do is
    /// asserted: read both the root table and the collection table. PostgreSQL may run the EXISTS as a
    /// hash semi-join or as an inner join over de-duplicated collection owners; both return each root
    /// row once, which the result tests above establish.
    /// </summary>
    [TestCase("School", "identificationCode", SchoolCodePath, "C-100")]
    [TestCase("Staff", "assigningOrganizationIdentificationCode", StaffAssigningOrganizationPath, "O1")]
    public async Task It_records_the_query_plan_of_a_collection_filter(
        string resourceName,
        string queryFieldName,
        string path,
        string value
    )
    {
        var resource = new QualifiedResourceName("Ed-Fi", resourceName);
        var readPlan = _mappingSet.GetReadPlanOrThrow(resource);
        var capability = _mappingSet.GetQueryCapabilityOrThrow(resource);
        var keyset = new RelationalQueryPageKeysetPlanner(SqlDialect.Pgsql).Plan(
            readPlan.Model.Root,
            new RelationalQueryPreprocessingResult(
                new RelationalQueryPreprocessingOutcome.Continue(),
                [
                    new PreprocessedRelationalQueryElement(
                        Element(queryFieldName, path, value),
                        capability.SupportedFieldsByQueryField[queryFieldName],
                        new PreprocessedRelationalQueryValue.Raw(value)
                    ),
                ]
            ),
            new CollectionPaging.Traditional(
                new PaginationParameters(
                    Limit: 25,
                    Offset: 0,
                    TotalCount: true,
                    MaximumPageSize: MaximumPageSize
                )
            ),
            resourceTables: readPlan.Model.TablesInDependencyOrder
        );

        var pagePlan = await ExplainAsync(keyset.Plan.PageDocumentIdSql, keyset);
        var countPlan = await ExplainAsync(keyset.Plan.TotalCountSql!, keyset);

        foreach (var plan in new[] { pagePlan, countPlan })
        {
            plan.Should().Contain($"\"Relation Name\": \"{resourceName}IdentificationCode\"");
            plan.Should().Contain($"\"Relation Name\": \"{resourceName}\"");
        }
    }

    private async Task AssertEveryModeSelectsAsync(
        QueryElement[] filter,
        ChangeVersionRange? window,
        IReadOnlyList<Guid> expectedInDocumentIdOrder
    )
    {
        var anchors = await ReadSchoolAnchorsAsync();
        var expectedInContentVersionOrder = expectedInDocumentIdOrder
            .OrderBy(uuid => anchors[uuid].ContentVersion)
            .ToArray();

        // Offset pages of one document each, with the count on every page.
        List<Guid> offsetWalk = [];

        for (var offset = 0; offset < expectedInDocumentIdOrder.Count + 1; offset++)
        {
            var page = await OffsetPageAsync("School", filter, limit: 1, offset: offset, window: window);
            page.TotalCount.Should().Be(expectedInDocumentIdOrder.Count);
            offsetWalk.AddRange(Ids(page));
        }

        offsetWalk.Should().Equal(expectedInDocumentIdOrder);

        (await CursorWalkAsync(filter, window, PageOrderingMode.DocumentId))
            .Should()
            .Equal(expectedInDocumentIdOrder);
        (await CursorWalkAsync(filter, window, PageOrderingMode.ContentVersion))
            .Should()
            .Equal(expectedInContentVersionOrder);
        (
            await PartitionReplayAsync(
                filter,
                window,
                PageOrderingMode.DocumentId,
                [.. expectedInDocumentIdOrder.Select(uuid => anchors[uuid].DocumentId).Order()]
            )
        )
            .Should()
            .Equal(expectedInDocumentIdOrder);
        (
            await PartitionReplayAsync(
                filter,
                window,
                PageOrderingMode.ContentVersion,
                [.. expectedInDocumentIdOrder.Select(uuid => anchors[uuid].ContentVersion).Order()]
            )
        )
            .Should()
            .Equal(expectedInContentVersionOrder);
    }

    /// <summary>
    /// Walks cursor pages of one document each, continuing after the highest anchor each page selected,
    /// until a page comes back empty.
    /// </summary>
    private async Task<IReadOnlyList<Guid>> CursorWalkAsync(
        QueryElement[] filter,
        ChangeVersionRange? window,
        PageOrderingMode orderingMode
    )
    {
        List<Guid> walked = [];
        var range = CursorRange.From(0);

        for (var guard = 0; guard < 20; guard++)
        {
            var page = (QueryResult.QuerySuccess)
                await QueryAsync(
                    "School",
                    new CollectionPaging.Cursor(range, new PageSize(1)),
                    filter,
                    window,
                    orderingMode
                );

            if (page.EdfiDocs.Count == 0)
            {
                return walked;
            }

            walked.AddRange(Ids(page));
            page.HighestSelectedAnchor.Should().NotBeNull();
            range = CursorRange.From(page.HighestSelectedAnchor!.Value + 1);
        }

        throw new InvalidOperationException("The cursor walk did not end.");
    }

    /// <summary>
    /// Computes partition boundaries over the filtered set and checks them against the expected starts,
    /// then replays every partition as one cursor page in the same anchor, concatenating the pages in
    /// partition order.
    /// </summary>
    /// <remarks>
    /// The replay alone cannot show the boundaries were calculated over the filtered set: boundaries
    /// over every school still replay to the expected documents, because each page reapplies the filter
    /// and window. The boundaries are therefore checked directly. Every expected set here is smaller than
    /// the requested partition count, so with a minimum partition size of one each filtered candidate
    /// starts its own partition, and the expected starts are exactly the persisted anchors of the
    /// expected documents.
    /// </remarks>
    /// <param name="expectedAscendingStarts">
    /// The persisted anchor values of the expected documents, ascending, read independently of the
    /// partition result.
    /// </param>
    private async Task<IReadOnlyList<Guid>> PartitionReplayAsync(
        QueryElement[] filter,
        ChangeVersionRange? window,
        PageOrderingMode orderingMode,
        IReadOnlyList<long> expectedAscendingStarts
    )
    {
        const int RequestedPartitionCount = 5;

        expectedAscendingStarts
            .Count.Should()
            .BeLessThan(RequestedPartitionCount, "each expected document must start its own partition");

        await using var scope = _serviceProvider.CreateAsyncScope();
        SetSelectedInstance(scope.ServiceProvider);

        var partitions = await scope
            .ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>()
            .QueryPartitions(
                new RelationalPartitionRequest(
                    ResourceInfo: _resources["School"].Info,
                    AuthorizationContext: new RelationalAuthorizationContext([]),
                    MappingSet: _mappingSet,
                    QueryElements: filter,
                    AuthorizationStrategyEvaluators: [],
                    RequestedPartitionCount: RequestedPartitionCount,
                    MinimumPartitionSize: 1,
                    TraceId: new TraceId("pg-ident-code-partitions"),
                    PageOrderingMode: orderingMode,
                    ChangeVersionRange: window
                )
            );

        var ranges = partitions.Should().BeOfType<PartitionResult.PartitionSuccess>().Subject.Ranges;

        ranges
            .Select(static range => range.InclusiveMinimum)
            .Should()
            .Equal(expectedAscendingStarts, "boundaries are calculated over the filtered, windowed set");
        ranges
            .Select(static range => range.InclusiveMaximum)
            .Should()
            .Equal(
                [.. expectedAscendingStarts.Skip(1).Select(static nextStart => nextStart - 1), long.MaxValue],
                "each range closes before the next start and the last is unbounded above"
            );

        List<Guid> replayed = [];

        foreach (var range in ranges)
        {
            var page = (QueryResult.QuerySuccess)
                await QueryAsync(
                    "School",
                    new CollectionPaging.Cursor(range, new PageSize(MaximumPageSize)),
                    filter,
                    window,
                    orderingMode
                );
            replayed.AddRange(Ids(page));
        }

        return replayed;
    }

    private async Task<IReadOnlyList<Guid>> QueryTranscriptsAsync(
        string queryFieldName,
        string path,
        string value
    ) => Ids(await OffsetPageAsync("CourseTranscript", [Element(queryFieldName, path, value)]));

    private async Task<QueryResult.QuerySuccess> OffsetPageAsync(
        string resourceName,
        QueryElement[] filter,
        int limit = 25,
        int offset = 0,
        ChangeVersionRange? window = null
    )
    {
        var result = await QueryAsync(
            resourceName,
            new CollectionPaging.Traditional(
                new PaginationParameters(
                    Limit: limit,
                    Offset: offset,
                    TotalCount: true,
                    MaximumPageSize: MaximumPageSize
                )
            ),
            filter,
            window,
            PageOrderingMode.DocumentId
        );

        return result.Should().BeOfType<QueryResult.QuerySuccess>().Subject;
    }

    private async Task<QueryResult> QueryAsync(
        string resourceName,
        CollectionPaging paging,
        QueryElement[] filter,
        ChangeVersionRange? window,
        PageOrderingMode orderingMode
    )
    {
        await using var scope = _serviceProvider.CreateAsyncScope();
        SetSelectedInstance(scope.ServiceProvider);

        return await scope
            .ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>()
            .QueryDocuments(
                new RelationalQueryRequest(
                    ResourceInfo: _resources[resourceName].Info,
                    AuthorizationContext: new RelationalAuthorizationContext([]),
                    MappingSet: _mappingSet,
                    QueryElements: filter,
                    AuthorizationStrategyEvaluators: [],
                    Paging: paging,
                    TraceId: new TraceId("pg-ident-code-query"),
                    ChangeVersionRange: window,
                    PageOrderingMode: orderingMode
                )
            );
    }

    private async Task<(IReadOnlyList<Guid> Uuids, int? TotalCount)> ExecuteCandidateBatchAsync(
        string batch,
        PageKeysetSpec.Query keyset
    )
    {
        await using var connection = new NpgsqlConnection($"{_database.ConnectionString};Pooling=false");
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(batch, connection);
        command.Parameters.AddRange(BindParameters(keyset));

        await using var reader = await command.ExecuteReaderAsync();
        List<List<Dictionary<string, object?>>> resultSets = [];

        do
        {
            List<Dictionary<string, object?>> rows = [];

            while (await reader.ReadAsync())
            {
                rows.Add(
                    Enumerable
                        .Range(0, reader.FieldCount)
                        .ToDictionary(reader.GetName, object? (ordinal) => reader.GetValue(ordinal))
                );
            }

            if (reader.FieldCount > 0)
            {
                resultSets.Add(rows);
            }
        } while (await reader.NextResultAsync());

        var countRow = resultSets.Single(rows => rows.Count == 1 && rows[0].Count == 1)[0];
        var metadataRows = resultSets.Single(rows => rows.Count > 0 && rows[0].ContainsKey("DocumentUuid"));

        return (
            [
                .. metadataRows
                    .OrderBy(row => Convert.ToInt64(row["DocumentId"], CultureInfo.InvariantCulture))
                    .Select(row => (Guid)row["DocumentUuid"]!),
            ],
            Convert.ToInt32(countRow.Values.Single(), CultureInfo.InvariantCulture)
        );
    }

    private async Task<string> ExplainAsync(string sql, PageKeysetSpec.Query keyset)
    {
        var rows = await _database.QueryRowsAsync($"EXPLAIN (FORMAT JSON) {sql}", BindParameters(keyset));
        var plan = rows.Single()["QUERY PLAN"]?.ToString() ?? string.Empty;

        await TestContext.Out.WriteLineAsync(sql);
        await TestContext.Out.WriteLineAsync(plan);

        return plan;
    }

    private static NpgsqlParameter[] BindParameters(PageKeysetSpec.Query keyset) =>
        [
            .. keyset.ParameterValues.Select(static parameter => new NpgsqlParameter(
                parameter.Key,
                parameter.Value ?? DBNull.Value
            )),
        ];

    private async Task<Dictionary<Guid, (long DocumentId, long ContentVersion)>> ReadSchoolAnchorsAsync()
    {
        var rows = await _database.QueryRowsAsync(
            """
            SELECT doc."DocumentUuid", school."DocumentId", school."ContentVersion"
            FROM "edfi"."School" school
            INNER JOIN "dms"."Document" doc ON doc."DocumentId" = school."DocumentId";
            """
        );

        return rows.ToDictionary(
            static row => (Guid)row["DocumentUuid"]!,
            static row =>
                (
                    Convert.ToInt64(row["DocumentId"], CultureInfo.InvariantCulture),
                    Convert.ToInt64(row["ContentVersion"], CultureInfo.InvariantCulture)
                )
        );
    }

    private static Guid[] Ids(QueryResult.QuerySuccess success) =>
        [.. success.EdfiDocs.Select(static document => Guid.Parse(document!["id"]!.GetValue<string>()))];

    private static QueryElement Element(string queryFieldName, string path, string value) =>
        new(queryFieldName, [new JsonPath(path)], value, "string");

    private static JsonNode SchoolBody(long schoolId, string name, string? identificationCodes)
    {
        var codes = identificationCodes is null ? "" : $"\"identificationCodes\": {identificationCodes},";

        return JsonNode.Parse(
            $$"""
            {
              "schoolId": {{schoolId}},
              "nameOfInstitution": "{{name}}",
              {{codes}}
              "educationOrganizationCategories": [
                { "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School" }
              ],
              "gradeLevels": [{ "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Ninth grade" }]
            }
            """
        )!;
    }

    private static JsonNode StaffBody(string staffUniqueId, string identificationCodes) =>
        JsonNode.Parse(
            $$"""
            {
              "staffUniqueId": "{{staffUniqueId}}",
              "firstName": "{{staffUniqueId}}",
              "lastSurname": "Staff",
              "identificationCodes": {{identificationCodes}}
            }
            """
        )!;

    /// <summary>
    /// A transcript whose root catalog URL and assigning organization differ from those of its
    /// alternative course identification code, with the documents it references.
    /// </summary>
    private async Task SeedCourseTranscriptAsync()
    {
        await UpsertAsync(
            "Student",
            Guid.Parse("d8100000-0000-0000-0000-0000000000c1"),
            JsonNode.Parse(
                """{ "studentUniqueId": "ST-1", "firstName": "Student", "lastSurname": "One", "birthDate": "2010-01-01" }"""
            )!
        );
        await UpsertAsync(
            "SchoolYearType",
            Guid.Parse("d8100000-0000-0000-0000-0000000000c2"),
            JsonNode.Parse(
                """{ "schoolYear": 2026, "currentSchoolYear": true, "schoolYearDescription": "2025-2026" }"""
            )!
        );
        await UpsertAsync(
            "Course",
            Guid.Parse("d8100000-0000-0000-0000-0000000000c3"),
            JsonNode.Parse(
                """
                {
                  "courseCode": "ALG-1",
                  "courseTitle": "Algebra I",
                  "numberOfParts": 1,
                  "educationOrganizationReference": { "educationOrganizationId": 810001 },
                  "identificationCodes": [
                    { "identificationCode": "ALG-1", "courseIdentificationSystemDescriptor": "uri://ed-fi.org/CourseIdentificationSystemDescriptor#LEA course code" }
                  ]
                }
                """
            )!
        );
        await UpsertAsync(
            "StudentAcademicRecord",
            Guid.Parse("d8100000-0000-0000-0000-0000000000c4"),
            JsonNode.Parse(
                """
                {
                  "studentReference": { "studentUniqueId": "ST-1" },
                  "educationOrganizationReference": { "educationOrganizationId": 810001 },
                  "schoolYearTypeReference": { "schoolYear": 2026 },
                  "termDescriptor": "uri://ed-fi.org/TermDescriptor#Fall Semester"
                }
                """
            )!
        );
        await UpsertAsync(
            "CourseTranscript",
            CourseTranscriptUuid,
            JsonNode.Parse(
                $$"""
                {
                  "courseReference": { "courseCode": "ALG-1", "educationOrganizationId": 810001 },
                  "studentAcademicRecordReference": {
                    "educationOrganizationId": 810001,
                    "schoolYear": 2026,
                    "studentUniqueId": "ST-1",
                    "termDescriptor": "uri://ed-fi.org/TermDescriptor#Fall Semester"
                  },
                  "courseAttemptResultDescriptor": "uri://ed-fi.org/CourseAttemptResultDescriptor#Pass",
                  "courseCatalogURL": "{{RootCourseCatalogUrl}}",
                  "assigningOrganizationIdentificationCode": "ROOT-ORG",
                  "alternativeCourseIdentificationCodes": [
                    {
                      "identificationCode": "ALT-1",
                      "courseIdentificationSystemDescriptor": "uri://ed-fi.org/CourseIdentificationSystemDescriptor#State course code",
                      "courseCatalogURL": "{{ItemCourseCatalogUrl}}",
                      "assigningOrganizationIdentificationCode": "ITEM-ORG"
                    }
                  ]
                }
                """
            )!
        );
    }

    private async Task UpsertAsync(string resourceName, Guid documentUuid, JsonNode body)
    {
        await using var scope = _serviceProvider.CreateAsyncScope();
        SetSelectedInstance(scope.ServiceProvider);

        var (resourceInfo, resourceSchema) = _resources[resourceName];
        var result = await scope
            .ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>()
            .UpsertDocument(
                new UpsertRequest(
                    ResourceInfo: resourceInfo,
                    DocumentInfo: RelationalDocumentInfoTestHelper.CreateDocumentInfo(
                        body,
                        resourceInfo,
                        resourceSchema,
                        _mappingSet
                    ),
                    MappingSet: _mappingSet,
                    EdfiDoc: body,
                    Headers: [],
                    TraceId: new TraceId($"pg-ident-code-seed-{resourceName}"),
                    DocumentUuid: new DocumentUuid(documentUuid)
                )
                {
                    ActionAuthorization =
                        UpsertActionAuthorizationTestSupport.NoFurtherAuthorizationRequiredForCreateAndUpdate,
                }
            );

        result.Should().BeOfType<UpsertResult.InsertSuccess>($"seeding {resourceName} {documentUuid}");
    }

    private async Task SeedDescriptorsAsync()
    {
        (string ResourceName, string CodeValue)[] descriptors =
        [
            ("EducationOrganizationCategoryDescriptor", "School"),
            ("GradeLevelDescriptor", "Ninth grade"),
            ("EducationOrganizationIdentificationSystemDescriptor", "NCES"),
            ("EducationOrganizationIdentificationSystemDescriptor", "SEA"),
            ("StaffIdentificationSystemDescriptor", "State"),
            ("StaffIdentificationSystemDescriptor", "District"),
            ("TermDescriptor", "Fall Semester"),
            ("CourseAttemptResultDescriptor", "Pass"),
            ("CourseIdentificationSystemDescriptor", "LEA course code"),
            ("CourseIdentificationSystemDescriptor", "State course code"),
        ];

        await _database.ExecuteNonQueryAsync(CompactDescriptorSeedSupport.PostgresqlSeparateDocumentIdsSql);

        foreach (var (resourceName, codeValue) in descriptors)
        {
            var resourceKeyId = _mappingSet.ResourceKeyIdByResource[
                new QualifiedResourceName("Ed-Fi", resourceName)
            ];
            var documentId = await _database.ExecuteScalarAsync<long>(
                """
                INSERT INTO "dms"."Document" ("DocumentUuid", "ResourceKeyId")
                VALUES (@documentUuid, @resourceKeyId)
                RETURNING "DocumentId";
                """,
                new NpgsqlParameter("documentUuid", Guid.NewGuid()),
                new NpgsqlParameter("resourceKeyId", resourceKeyId)
            );

            await _database.ExecuteNonQueryAsync(
                """
                INSERT INTO "dms"."Descriptor" ("DocumentId", "ResourceKeyId", "Namespace", "CodeValue", "ShortDescription", "Description")
                VALUES (@documentId, @resourceKeyId, @namespace, @codeValue, @codeValue, @codeValue);
                """,
                new NpgsqlParameter("documentId", documentId),
                new NpgsqlParameter("resourceKeyId", resourceKeyId),
                new NpgsqlParameter("namespace", $"uri://ed-fi.org/{resourceName}"),
                new NpgsqlParameter("codeValue", codeValue)
            );
        }
    }

    private (ResourceInfo Info, ResourceSchema Schema) GetResource(string resourceName)
    {
        var effectiveProjectSchema = _fixture.EffectiveSchemaSet.ProjectsInEndpointOrder.Single(project =>
            string.Equals(project.ProjectEndpointName, "ed-fi", StringComparison.OrdinalIgnoreCase)
        );
        var projectSchema = new ProjectSchema(effectiveProjectSchema.ProjectSchema, NullLogger.Instance);
        var resourceSchema = new ResourceSchema(
            projectSchema.FindResourceSchemaNodeByResourceName(new ResourceName(resourceName))
                ?? throw new InvalidOperationException($"Could not find resource '{resourceName}'.")
        );

        return (
            new ResourceInfo(
                ProjectName: projectSchema.ProjectName,
                ResourceName: resourceSchema.ResourceName,
                IsDescriptor: resourceSchema.IsDescriptor,
                ResourceVersion: projectSchema.ResourceVersion,
                AllowIdentityUpdates: resourceSchema.AllowIdentityUpdates
            ),
            resourceSchema
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
                    Name: "PostgresqlIdentificationCodeQuery",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );
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
        services.AddPostgresqlBackendIntegrationTestServices();
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
}

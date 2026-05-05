// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Postgresql;
using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Core.Backend;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Old.Postgresql;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

[TestFixture]
[NonParallelizable]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
public class Given_A_Postgresql_DescriptorRead_Query_Request
{
    private const string FixtureRelativePath = "src/dms/backend/Fixtures/authoritative/ds-5.2";
    private const int MaximumPageSize = 500;
    private const string SharedPagingDescription = "Shared paging count";

    private static readonly QualifiedResourceName SchoolTypeDescriptorResource = new(
        "Ed-Fi",
        "SchoolTypeDescriptor"
    );

    private static readonly DescriptorReadSeed AlternativeSeed = new(
        DocumentUuid: new DocumentUuid(Guid.Parse("30000000-0000-0000-0000-000000000201")),
        Namespace: "uri://ed-fi.org/SchoolTypeDescriptor/Alternative",
        CodeValue: "Alternative",
        ShortDescription: "Alternative",
        Description: "Alternative school type",
        EffectiveBeginDate: new DateOnly(2025, 1, 2),
        EffectiveEndDate: new DateOnly(2025, 12, 31)
    );

    private static readonly DescriptorReadSeed MixedCaseSeed = new(
        DocumentUuid: new DocumentUuid(Guid.Parse("30000000-0000-0000-0000-000000000202")),
        Namespace: "uri://ed-fi.org/SchoolTypeDescriptor/MiXeDNamespace",
        CodeValue: "MiXeDCase",
        ShortDescription: "MiXeD Short",
        Description: "MiXeD Description",
        EffectiveBeginDate: new DateOnly(2026, 2, 3),
        EffectiveEndDate: new DateOnly(2026, 8, 4)
    );

    private static readonly DescriptorReadSeed StoredInNamespaceSeed = new(
        DocumentUuid: new DocumentUuid(Guid.Parse("30000000-0000-0000-0000-000000000203")),
        Namespace: "uri://ed-fi.org/SchoolTypeDescriptor#StoredInNamespace",
        CodeValue: "NamespaceCode",
        ShortDescription: "Namespace Stored",
        Description: "Namespace stored in Namespace",
        EffectiveBeginDate: new DateOnly(2027, 3, 4),
        EffectiveEndDate: new DateOnly(2027, 9, 5)
    );

    private static readonly DescriptorReadSeed[] DescriptorSeeds =
    [
        AlternativeSeed,
        MixedCaseSeed,
        StoredInNamespaceSeed,
    ];

    private static readonly DescriptorReadSeed PagingFirstSeed = new(
        DocumentUuid: new DocumentUuid(Guid.Parse("30000000-0000-0000-0000-000000000204")),
        Namespace: "uri://ed-fi.org/SchoolTypeDescriptor/PagingZulu",
        CodeValue: "Zulu",
        ShortDescription: "Zulu",
        Description: SharedPagingDescription,
        EffectiveBeginDate: new DateOnly(2028, 1, 2),
        EffectiveEndDate: new DateOnly(2028, 6, 3)
    );

    private static readonly DescriptorReadSeed PagingSecondSeed = new(
        DocumentUuid: new DocumentUuid(Guid.Parse("30000000-0000-0000-0000-000000000205")),
        Namespace: "uri://ed-fi.org/SchoolTypeDescriptor/PagingAlpha",
        CodeValue: "Alpha",
        ShortDescription: "Alpha",
        Description: SharedPagingDescription,
        EffectiveBeginDate: new DateOnly(2028, 2, 3),
        EffectiveEndDate: new DateOnly(2028, 7, 4)
    );

    private static readonly DescriptorReadSeed PagingThirdSeed = new(
        DocumentUuid: new DocumentUuid(Guid.Parse("30000000-0000-0000-0000-000000000206")),
        Namespace: "uri://ed-fi.org/SchoolTypeDescriptor/PagingBeta",
        CodeValue: "Beta",
        ShortDescription: "Beta",
        Description: "Distinct paging count",
        EffectiveBeginDate: new DateOnly(2028, 3, 4),
        EffectiveEndDate: new DateOnly(2028, 8, 5)
    );

    private static readonly DescriptorReadSeed[] PagingDescriptorSeeds =
    [
        PagingFirstSeed,
        PagingSecondSeed,
        PagingThirdSeed,
    ];

    private PostgresqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private PostgresqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;
    private ResourceInfo _resourceInfo = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _fixture = PostgresqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(FixtureRelativePath);
        _mappingSet = _fixture.MappingSet;
        _database = await PostgresqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
        _serviceProvider = CreateServiceProvider();
        _resourceInfo = DescriptorReadIntegrationTestSupport.CreateResourceInfo(
            _fixture.EffectiveSchemaSet,
            "ed-fi",
            "SchoolTypeDescriptor"
        );
    }

    [SetUp]
    public async Task Setup()
    {
        await _database.ResetAsync();
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_serviceProvider is not null)
        {
            await _serviceProvider.DisposeAsync();
            _serviceProvider = null!;
        }

        if (_database is not null)
        {
            await _database.DisposeAsync();
            _database = null!;
        }
    }

    [TestCaseSource(nameof(SupportedFilterCases))]
    public async Task It_filters_descriptor_queries_by_supported_fields(
        string queryFieldName,
        string path,
        string value,
        string type,
        DocumentUuid expectedDocumentUuid
    )
    {
        await SeedDescriptorsAsync();

        var result = await ExecuteQueryAsync(
            [CreateQueryElement(queryFieldName, path, value, type)],
            $"pg-descriptor-query-{queryFieldName}"
        );

        AssertSingleDescriptorMatch(result, FindSeedOrThrow(expectedDocumentUuid));
    }

    [Test]
    public async Task It_returns_an_empty_page_for_an_invalid_descriptor_query_id()
    {
        await SeedDescriptorsAsync();

        var result = await ExecuteQueryAsync(
            [CreateQueryElement("id", "$.id", "not-a-guid")],
            "pg-descriptor-query-invalid-id",
            totalCount: true
        );

        AssertEmptyPage(result, expectedTotalCount: 0);
    }

    [Test]
    public async Task It_treats_namespace_as_an_exact_column_match_instead_of_uri_plus_code_value()
    {
        await SeedDescriptorsAsync();

        var result = await ExecuteQueryAsync(
            [
                CreateQueryElement(
                    "namespace",
                    "$.namespace",
                    $"{AlternativeSeed.Namespace}#{AlternativeSeed.CodeValue}"
                ),
            ],
            "pg-descriptor-query-namespace-strict"
        );

        AssertEmptyPage(result);
    }

    [TestCaseSource(nameof(CaseSensitiveStringFilterCases))]
    public async Task It_treats_descriptor_string_filter_values_as_case_sensitive(
        string queryFieldName,
        string path,
        string value
    )
    {
        await SeedDescriptorsAsync();

        var result = await ExecuteQueryAsync(
            [CreateQueryElement(queryFieldName, path, value)],
            $"pg-descriptor-query-case-{queryFieldName}"
        );

        AssertEmptyPage(result);
    }

    [Test]
    public async Task It_pages_descriptor_queries_in_document_id_order_across_multiple_pages_and_reports_total_count()
    {
        await SeedDescriptorsAsync(PagingDescriptorSeeds);

        var firstPage = await ExecuteQueryAsync(
            [],
            "pg-descriptor-query-page-1",
            totalCount: true,
            limit: 2,
            offset: 0
        );
        var secondPage = await ExecuteQueryAsync(
            [],
            "pg-descriptor-query-page-2",
            totalCount: true,
            limit: 2,
            offset: 2
        );

        AssertDescriptorPage(firstPage, [PagingFirstSeed, PagingSecondSeed], expectedTotalCount: 3);
        AssertDescriptorPage(secondPage, [PagingThirdSeed], expectedTotalCount: 3);
    }

    [Test]
    public async Task It_reports_filtered_total_count_as_the_unpaged_filtered_row_count()
    {
        await SeedDescriptorsAsync(PagingDescriptorSeeds);

        var result = await ExecuteQueryAsync(
            [CreateQueryElement("description", "$.description", SharedPagingDescription)],
            "pg-descriptor-query-filtered-total-count",
            totalCount: true,
            limit: 1,
            offset: 1
        );

        AssertDescriptorPage(result, [PagingSecondSeed], expectedTotalCount: 2);
    }

    [Test]
    public async Task It_does_not_fail_when_total_count_is_requested_and_a_corrupt_descriptor_document_is_outside_the_selected_page()
    {
        await SeedDescriptorsAsync([PagingFirstSeed]);

        var resourceKeyId = DescriptorReadIntegrationTestSupport.GetDescriptorResourceKeyIdOrThrow(
            _mappingSet,
            SchoolTypeDescriptorResource
        );
        await PostgresqlDescriptorReadTestSupport.InsertDocumentAsync(
            _database,
            new DocumentUuid(Guid.Parse("30000000-0000-0000-0000-000000000207")),
            resourceKeyId
        );

        var result = await ExecuteQueryAsync(
            [],
            "pg-descriptor-query-corrupt-outside-page",
            totalCount: true,
            limit: 1,
            offset: 0
        );

        AssertDescriptorPage(result, [PagingFirstSeed], expectedTotalCount: 2);
    }

    [Test]
    public async Task It_returns_an_unknown_failure_when_the_selected_descriptor_query_document_has_no_descriptor_row()
    {
        await SeedDescriptorsAsync([PagingFirstSeed]);

        var resourceKeyId = DescriptorReadIntegrationTestSupport.GetDescriptorResourceKeyIdOrThrow(
            _mappingSet,
            SchoolTypeDescriptorResource
        );
        var corruptDocumentId = await PostgresqlDescriptorReadTestSupport.InsertDocumentAsync(
            _database,
            new DocumentUuid(Guid.Parse("30000000-0000-0000-0000-000000000208")),
            resourceKeyId
        );

        var result = await ExecuteQueryAsync([], "pg-descriptor-query-corrupt-selected", limit: 1, offset: 1);

        var failure = result.Should().BeOfType<QueryResult.UnknownFailure>().Subject;
        failure.FailureMessage.Should().Contain($"DocumentId {corruptDocumentId}");
        failure.FailureMessage.Should().Contain("dms.Descriptor.Namespace must not be null.");
    }

    private static IEnumerable<TestCaseData> SupportedFilterCases()
    {
        yield return new TestCaseData(
            "id",
            "$.id",
            MixedCaseSeed.DocumentUuid.Value.ToString(),
            "string",
            MixedCaseSeed.DocumentUuid
        ).SetName("It_filters_by_id");
        yield return new TestCaseData(
            "namespace",
            "$.namespace",
            StoredInNamespaceSeed.Namespace,
            "string",
            StoredInNamespaceSeed.DocumentUuid
        ).SetName("It_filters_by_namespace");
        yield return new TestCaseData(
            "codeValue",
            "$.codeValue",
            MixedCaseSeed.CodeValue,
            "string",
            MixedCaseSeed.DocumentUuid
        ).SetName("It_filters_by_code_value");
        yield return new TestCaseData(
            "shortDescription",
            "$.shortDescription",
            MixedCaseSeed.ShortDescription,
            "string",
            MixedCaseSeed.DocumentUuid
        ).SetName("It_filters_by_short_description");
        yield return new TestCaseData(
            "description",
            "$.description",
            MixedCaseSeed.Description!,
            "string",
            MixedCaseSeed.DocumentUuid
        ).SetName("It_filters_by_description");
        yield return new TestCaseData(
            "effectiveBeginDate",
            "$.effectiveBeginDate",
            FormatDate(AlternativeSeed.EffectiveBeginDate),
            "date",
            AlternativeSeed.DocumentUuid
        ).SetName("It_filters_by_effective_begin_date");
        yield return new TestCaseData(
            "effectiveEndDate",
            "$.effectiveEndDate",
            FormatDate(StoredInNamespaceSeed.EffectiveEndDate),
            "date",
            StoredInNamespaceSeed.DocumentUuid
        ).SetName("It_filters_by_effective_end_date");
    }

    private static IEnumerable<TestCaseData> CaseSensitiveStringFilterCases()
    {
        yield return new TestCaseData(
            "namespace",
            "$.namespace",
            MixedCaseSeed.Namespace.ToLowerInvariant()
        ).SetName("It_requires_exact_case_for_namespace");
        yield return new TestCaseData(
            "codeValue",
            "$.codeValue",
            MixedCaseSeed.CodeValue.ToLowerInvariant()
        ).SetName("It_requires_exact_case_for_code_value");
        yield return new TestCaseData(
            "shortDescription",
            "$.shortDescription",
            MixedCaseSeed.ShortDescription.ToLowerInvariant()
        ).SetName("It_requires_exact_case_for_short_description");
        yield return new TestCaseData(
            "description",
            "$.description",
            MixedCaseSeed.Description!.ToLowerInvariant()
        ).SetName("It_requires_exact_case_for_description");
    }

    private ServiceProvider CreateServiceProvider()
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
        services.AddPostgresqlReferenceResolver();

        return services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }
        );
    }

    private async Task SeedDescriptorsAsync(IEnumerable<DescriptorReadSeed>? seeds = null)
    {
        foreach (var seed in seeds ?? DescriptorSeeds)
        {
            await PostgresqlDescriptorReadTestSupport.SeedDescriptorAsync(
                _database,
                _mappingSet,
                SchoolTypeDescriptorResource,
                seed
            );
        }
    }

    private async Task<QueryResult> ExecuteQueryAsync(
        QueryElement[] queryElements,
        string traceId,
        bool totalCount = false,
        int limit = 25,
        int offset = 0
    )
    {
        await using var scope = _serviceProvider.CreateAsyncScope();
        SetSelectedInstance(scope.ServiceProvider);

        var request = new RelationalQueryRequest(
            ResourceInfo: _resourceInfo,
            MappingSet: _mappingSet,
            QueryElements: queryElements,
            AuthorizationSecurableInfo: _resourceInfo.AuthorizationSecurableInfo,
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

    private void SetSelectedInstance(IServiceProvider serviceProvider)
    {
        serviceProvider
            .GetRequiredService<IDmsInstanceSelection>()
            .SetSelectedDmsInstance(
                new DmsInstance(
                    Id: 1,
                    InstanceType: "test",
                    InstanceName: "PostgresqlDescriptorReadQuery",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );
    }

    private static DescriptorReadSeed FindSeedOrThrow(DocumentUuid documentUuid)
    {
        return DescriptorSeeds.SingleOrDefault(seed => seed.DocumentUuid == documentUuid)
            ?? throw new InvalidOperationException(
                $"Expected test seed with DocumentUuid '{documentUuid.Value}' was not found."
            );
    }

    private static QueryElement CreateQueryElement(
        string queryFieldName,
        string path,
        string value,
        string type = "string"
    )
    {
        return new QueryElement(queryFieldName, [new JsonPath(path)], value, type);
    }

    private static void AssertSingleDescriptorMatch(QueryResult result, DescriptorReadSeed expectedSeed)
    {
        AssertDescriptorPage(result, [expectedSeed]);
    }

    private static void AssertDescriptorPage(
        QueryResult result,
        IReadOnlyList<DescriptorReadSeed> expectedSeeds,
        int? expectedTotalCount = null
    )
    {
        var success = result.Should().BeOfType<QueryResult.QuerySuccess>().Subject;
        success.TotalCount.Should().Be(expectedTotalCount);
        success.EdfiDocs.Should().HaveCount(expectedSeeds.Count);

        for (var i = 0; i < expectedSeeds.Count; i++)
        {
            AssertDescriptorDocument(success.EdfiDocs[i]!.AsObject(), expectedSeeds[i]);
        }
    }

    private static void AssertDescriptorDocument(JsonObject document, DescriptorReadSeed expectedSeed)
    {
        document["id"]!.GetValue<string>().Should().Be(expectedSeed.DocumentUuid.Value.ToString());
        document["namespace"]!.GetValue<string>().Should().Be(expectedSeed.Namespace);
        document["codeValue"]!.GetValue<string>().Should().Be(expectedSeed.CodeValue);
        document["shortDescription"]!.GetValue<string>().Should().Be(expectedSeed.ShortDescription);
        document["description"]!.GetValue<string>().Should().Be(expectedSeed.Description!);
        document["effectiveBeginDate"]!
            .GetValue<string>()
            .Should()
            .Be(FormatDate(expectedSeed.EffectiveBeginDate));
        document["effectiveEndDate"]!
            .GetValue<string>()
            .Should()
            .Be(FormatDate(expectedSeed.EffectiveEndDate));
    }

    private static void AssertEmptyPage(QueryResult result, int? expectedTotalCount = null)
    {
        var success = result.Should().BeOfType<QueryResult.QuerySuccess>().Subject;
        success.EdfiDocs.Should().BeEmpty();
        success.TotalCount.Should().Be(expectedTotalCount);
    }

    private static string FormatDate(DateOnly? value)
    {
        return value?.ToString("yyyy-MM-dd")
            ?? throw new InvalidOperationException(
                "Expected non-null DateOnly value for descriptor query test data."
            );
    }
}

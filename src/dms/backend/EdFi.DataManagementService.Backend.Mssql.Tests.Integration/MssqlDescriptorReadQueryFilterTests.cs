// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Mssql;
using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Core.Backend;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

[TestFixture]
[NonParallelizable]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
public class Given_A_Mssql_DescriptorRead_Query_Request
{
    private const string FixtureRelativePath = "src/dms/backend/Fixtures/authoritative/sample";
    private const int MaximumPageSize = 500;

    private static readonly QualifiedResourceName SchoolTypeDescriptorResource = new(
        "Ed-Fi",
        "SchoolTypeDescriptor"
    );

    private static readonly DescriptorReadSeed AlternativeSeed = new(
        DocumentUuid: new DocumentUuid(Guid.Parse("40000000-0000-0000-0000-000000000201")),
        Namespace: "uri://ed-fi.org/SchoolTypeDescriptor/Alternative",
        CodeValue: "Alternative",
        ShortDescription: "Alternative",
        Description: "Alternative school type",
        EffectiveBeginDate: new DateOnly(2025, 1, 2),
        EffectiveEndDate: new DateOnly(2025, 12, 31)
    );

    private static readonly DescriptorReadSeed MixedCaseSeed = new(
        DocumentUuid: new DocumentUuid(Guid.Parse("40000000-0000-0000-0000-000000000202")),
        Namespace: "uri://ed-fi.org/SchoolTypeDescriptor/MiXeDNamespace",
        CodeValue: "MiXeDCase",
        ShortDescription: "MiXeD Short",
        Description: "MiXeD Description",
        EffectiveBeginDate: new DateOnly(2026, 2, 3),
        EffectiveEndDate: new DateOnly(2026, 8, 4)
    );

    private static readonly DescriptorReadSeed StoredInNamespaceSeed = new(
        DocumentUuid: new DocumentUuid(Guid.Parse("40000000-0000-0000-0000-000000000203")),
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

    private MssqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private MssqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;
    private ResourceInfo _resourceInfo = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        if (!MssqlTestDatabaseHelper.IsConfigured())
        {
            Assert.Ignore(
                "SQL Server integration tests require a MssqlAdmin connection string in appsettings.Test.json"
            );
        }

        _fixture = MssqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(FixtureRelativePath);
        _mappingSet = _fixture.MappingSet;
        _database = await MssqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
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
            $"mssql-descriptor-query-{queryFieldName}"
        );

        AssertSingleDescriptorMatch(result, FindSeedOrThrow(expectedDocumentUuid));
    }

    [Test]
    public async Task It_returns_an_empty_page_for_an_invalid_descriptor_query_id()
    {
        await SeedDescriptorsAsync();

        var result = await ExecuteQueryAsync(
            [CreateQueryElement("id", "$.id", "not-a-guid")],
            "mssql-descriptor-query-invalid-id",
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
            "mssql-descriptor-query-namespace-strict"
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
            $"mssql-descriptor-query-case-{queryFieldName}"
        );

        AssertEmptyPage(result);
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

    private async Task SeedDescriptorsAsync()
    {
        foreach (var seed in DescriptorSeeds)
        {
            await MssqlDescriptorReadTestSupport.SeedDescriptorAsync(
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
        bool totalCount = false
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
                Limit: 25,
                Offset: 0,
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
                    InstanceName: "MssqlDescriptorReadQuery",
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
        var success = result.Should().BeOfType<QueryResult.QuerySuccess>().Subject;
        success.TotalCount.Should().BeNull();
        success.EdfiDocs.Should().HaveCount(1);

        var document = success.EdfiDocs[0]!.AsObject();
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

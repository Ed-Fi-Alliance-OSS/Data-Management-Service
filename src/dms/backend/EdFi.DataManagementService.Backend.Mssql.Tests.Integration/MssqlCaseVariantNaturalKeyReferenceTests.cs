// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Globalization;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Mssql;
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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

/// <summary>
/// The SQL Server collation delta on REFERENCE RESOLUTION, asserted rather than diffed: a document
/// reference whose string identity value differs from the stored row only by case RESOLVES here, so the
/// write succeeds and binds the referenced document.
/// </summary>
/// <remarks>
/// Plan decision 14 moved string identity comparison out of C# and into the database, so the dialect's
/// collation now decides whether a reference resolves. SQL Server's default case-insensitive collation
/// makes <c>UX_Sponsor_NK</c> match a case variant; the generated DDL pins no collation on identity
/// columns, so this is the database default speaking.
/// <para>
/// The twin is <c>Given_A_Postgresql_Relational_Post_With_A_Case_Variant_String_Reference_Identity</c>,
/// where the same request is a MISS and the write is refused as an unresolved reference. Together they are
/// the reference-resolution counterpart of the upsert-detection pair
/// (<c>Given_A_Mssql_Relational_Post_With_A_Case_Variant_String_Natural_Key</c> and its PostgreSQL twin);
/// each test also pins the exact-casing control so the case of the identity value is provably the only
/// variable.
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[Category(MssqlCiShards.Shard2)]
public class Given_A_Mssql_Relational_Post_With_A_Case_Variant_String_Reference_Identity
{
    private const string FixtureRelativePath =
        "src/dms/backend/EdFi.DataManagementService.Backend.IntegrationFixtures/profile-collection-aligned-extension-with-doc-ref";

    private const int ParentResourceId = 4242;
    private const string StoredSponsorName = "Acme Education Sponsor";
    private const string CaseVariantSponsorName = "ACME EDUCATION SPONSOR";
    private const string ParentCode = "P-001";

    private static readonly DocumentUuid SponsorDocumentUuid = new(
        Guid.Parse("dddd0005-0000-0000-0000-000000000001")
    );
    private static readonly DocumentUuid ParentResourceDocumentUuid = new(
        Guid.Parse("dddd0005-0000-0000-0000-000000000002")
    );

    private MssqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private MssqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;
    private ResourceInfo _sponsorResourceInfo = null!;
    private ResourceSchema _sponsorResourceSchema = null!;
    private ResourceInfo _parentResourceInfo = null!;
    private ResourceSchema _parentResourceSchema = null!;
    private ResourceInfo _alignedExtParentResourceInfo = null!;
    private ResourceSchema _alignedExtParentResourceSchema = null!;

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
            FixtureRelativePath,
            strict: true
        );
        _mappingSet = _fixture.MappingSet;
        _database = await MssqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);

        (ProjectSchema sponsorProjectSchema, ResourceSchema sponsorSchema) =
            MssqlCaseVariantReferenceSchemaLookup.GetResourceSchema(
                _fixture.EffectiveSchemaSet,
                "ed-fi",
                "Sponsor"
            );
        _sponsorResourceInfo = MssqlCaseVariantReferenceSchemaLookup.CreateResourceInfo(
            sponsorProjectSchema,
            sponsorSchema
        );
        _sponsorResourceSchema = sponsorSchema;

        (ProjectSchema parentProjectSchema, ResourceSchema parentSchema) =
            MssqlCaseVariantReferenceSchemaLookup.GetResourceSchema(
                _fixture.EffectiveSchemaSet,
                "ed-fi",
                "ParentResource"
            );
        _parentResourceInfo = MssqlCaseVariantReferenceSchemaLookup.CreateResourceInfo(
            parentProjectSchema,
            parentSchema
        );
        _parentResourceSchema = parentSchema;

        (ProjectSchema alignedProjectSchema, ResourceSchema alignedSchema) =
            MssqlCaseVariantReferenceSchemaLookup.GetResourceSchema(
                _fixture.EffectiveSchemaSet,
                "aligned",
                "ParentResource"
            );
        _alignedExtParentResourceInfo = MssqlCaseVariantReferenceSchemaLookup.CreateResourceInfo(
            alignedProjectSchema,
            alignedSchema
        );
        _alignedExtParentResourceSchema = alignedSchema;
    }

    [SetUp]
    public async Task SetUp()
    {
        await _database.ResetAsync();
        _serviceProvider = CreateServiceProvider();

        var seeded = await UpsertSponsorAsync();
        seeded
            .Should()
            .BeOfType<UpsertResult.InsertSuccess>("the seeded Sponsor establishes the stored casing");
    }

    [TearDown]
    public async Task TearDown()
    {
        if (_serviceProvider is not null)
        {
            await _serviceProvider.DisposeAsync();
            _serviceProvider = null!;
        }
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_database is not null)
        {
            await _database.DisposeAsync();
            _database = null!;
        }
    }

    [Test]
    public async Task It_resolves_an_exact_case_string_reference_identity()
    {
        var result = await UpsertParentResourceAsync(StoredSponsorName);

        result
            .Should()
            .BeOfType<UpsertResult.InsertSuccess>(
                "the control: with the stored casing the reference resolves on every dialect"
            );

        var boundSponsors = await ReadBoundSponsorsAsync();
        boundSponsors.Should().ContainSingle().Which.SponsorName.Should().Be(StoredSponsorName);
    }

    [Test]
    public async Task It_resolves_a_case_variant_string_reference_identity()
    {
        var result = await UpsertParentResourceAsync(CaseVariantSponsorName);

        result
            .Should()
            .BeOfType<UpsertResult.InsertSuccess>(
                "SQL Server's case-insensitive collation makes UX_Sponsor_NK match the stored row, so the "
                    + "case-variant identity value resolves the reference"
            );

        // The PostgreSQL delta, stated as its negation on this dialect.
        result.Should().NotBeOfType<UpsertResult.UpsertFailureReference>();

        var boundSponsors = await ReadBoundSponsorsAsync();
        var bound = boundSponsors.Should().ContainSingle().Subject;
        bound
            .SponsorName.Should()
            .Be(
                CaseVariantSponsorName,
                "the bound column carries the request's casing; only the lookup was case-insensitive"
            );
        bound
            .DocumentId.Should()
            .Be(
                await ReadSeededSponsorDocumentIdAsync(),
                "the case-variant reference must bind the one seeded Sponsor document"
            );
    }

    private async Task<IReadOnlyList<(long DocumentId, string SponsorName)>> ReadBoundSponsorsAsync()
    {
        List<(long, string)> bound = [];

        await using var connection = new Microsoft.Data.SqlClient.SqlConnection(_database.ConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT [Sponsor_DocumentId], [Sponsor_SponsorName]
            FROM [aligned].[ParentResourceExtensionParent]
            WHERE [Sponsor_DocumentId] IS NOT NULL
            ORDER BY [BaseCollectionItemId];
            """;

        await using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            bound.Add((reader.GetInt64(0), reader.GetString(1)));
        }

        return bound;
    }

    private async Task<long> ReadSeededSponsorDocumentIdAsync()
    {
        await using var connection = new Microsoft.Data.SqlClient.SqlConnection(_database.ConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT [DocumentId] FROM [edfi].[Sponsor];";

        var documentId = await command.ExecuteScalarAsync();

        return Convert.ToInt64(documentId, CultureInfo.InvariantCulture);
    }

    private async Task<UpsertResult> UpsertSponsorAsync()
    {
        await using AsyncServiceScope scope = _serviceProvider.CreateAsyncScope();
        SetSelectedInstance(scope.ServiceProvider);

        JsonNode requestBody = MssqlCaseVariantReferenceBodies.CreateSponsorRequestBody(StoredSponsorName);
        UpsertRequest request = new(
            ResourceInfo: _sponsorResourceInfo,
            DocumentInfo: RelationalDocumentInfoTestHelper.CreateDocumentInfo(
                requestBody,
                _sponsorResourceInfo,
                _sponsorResourceSchema,
                _mappingSet
            ),
            MappingSet: _mappingSet,
            EdfiDoc: requestBody,
            Headers: [],
            TraceId: new TraceId("mssql-case-variant-reference-seed-sponsor"),
            DocumentUuid: SponsorDocumentUuid
        );

        return await scope
            .ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>()
            .UpsertDocument(request);
    }

    private async Task<UpsertResult> UpsertParentResourceAsync(string referencedSponsorName)
    {
        await using AsyncServiceScope scope = _serviceProvider.CreateAsyncScope();
        SetSelectedInstance(scope.ServiceProvider);

        JsonNode requestBody = MssqlCaseVariantReferenceBodies.CreateParentResourceRequestBody(
            ParentResourceId,
            ParentCode,
            referencedSponsorName
        );
        UpsertRequest request = new(
            ResourceInfo: _parentResourceInfo,
            DocumentInfo: RelationalDocumentInfoTestHelper.CreateDocumentInfo(
                requestBody,
                _parentResourceInfo,
                _parentResourceSchema,
                _mappingSet,
                additionalSources:
                [
                    new RelationalDocumentInfoExtractionSource(
                        _alignedExtParentResourceInfo,
                        _alignedExtParentResourceSchema,
                        UseReferenceExtraction: false,
                        UseRelationalDescriptorExtraction: false
                    ),
                ],
                supplement: new RelationalDocumentInfoSupplement(
                    DocumentReferences: MssqlCaseVariantReferenceBodies.BuildAlignedSponsorReferences(
                        requestBody
                    ),
                    DocumentReferenceArrays: [],
                    DescriptorReferences: []
                )
            ),
            MappingSet: _mappingSet,
            EdfiDoc: requestBody,
            Headers: [],
            TraceId: new TraceId("mssql-case-variant-reference-post-parentresource"),
            DocumentUuid: ParentResourceDocumentUuid
        );

        return await scope
            .ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>()
            .UpsertDocument(request);
    }

    private ServiceProvider CreateServiceProvider()
    {
        ServiceCollection services = [];

        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddScoped<IDataStoreSelection, DataStoreSelection>();
        services.Configure<DatabaseOptions>(options => options.IsolationLevel = IsolationLevel.ReadCommitted);
        services.AddTestReadableProfileProjector();
        services.AddScoped<RelationalDocumentStoreRepository>();

        // The production registration: the natural-key resolver is the IReferenceResolver.
        services.AddMssqlReferenceResolver();

        return services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }
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
                    Name: "MssqlCaseVariantNaturalKeyReference",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );
    }
}

/// <summary>
/// Request bodies and the hand-built Sponsor reference shared by the SQL Server case-variant reference pin.
/// The aligned extension exposes the binding shape but not the full mapping pipeline, so the reference is
/// supplied through <see cref="RelationalDocumentInfoSupplement" /> rather than extracted — the same
/// technique <c>Given_A_Mssql_ParentResource_With_Collection_Aligned_Extension_Sponsor_Reference</c> uses
/// on this fixture.
/// </summary>
internal static class MssqlCaseVariantReferenceBodies
{
    public static JsonNode CreateSponsorRequestBody(string sponsorName) =>
        new JsonObject { ["sponsorName"] = sponsorName };

    public static JsonNode CreateParentResourceRequestBody(
        int parentResourceId,
        string parentCode,
        string referencedSponsorName
    ) =>
        new JsonObject
        {
            ["parentResourceId"] = parentResourceId,
            ["parents"] = new JsonArray(
                new JsonObject
                {
                    ["parentCode"] = parentCode,
                    ["parentName"] = "Parent One",
                    ["_ext"] = new JsonObject
                    {
                        ["aligned"] = new JsonObject
                        {
                            ["sponsorReference"] = new JsonObject { ["sponsorName"] = referencedSponsorName },
                        },
                    },
                }
            ),
        };

    public static IReadOnlyList<DocumentReference> BuildAlignedSponsorReferences(JsonNode requestBody)
    {
        ArgumentNullException.ThrowIfNull(requestBody);

        var parents = requestBody["parents"]?.AsArray();

        if (parents is null)
        {
            return [];
        }

        List<DocumentReference> references = [];

        for (var index = 0; index < parents.Count; index++)
        {
            var sponsorName = parents[index]
                ?["_ext"]?["aligned"]?["sponsorReference"]?["sponsorName"]?.GetValue<string>();

            if (string.IsNullOrWhiteSpace(sponsorName))
            {
                continue;
            }

            var sponsorResourceInfo = new BaseResourceInfo(
                new ProjectName("Ed-Fi"),
                new ResourceName("Sponsor"),
                IsDescriptor: false
            );
            var sponsorIdentity = new DocumentIdentity([
                new DocumentIdentityElement(new JsonPath("$.sponsorName"), sponsorName),
            ]);

            references.Add(
                new DocumentReference(
                    ResourceInfo: sponsorResourceInfo,
                    DocumentIdentity: sponsorIdentity,
                    ReferentialId: ReferentialIdCalculator.ReferentialIdFrom(
                        sponsorResourceInfo,
                        sponsorIdentity
                    ),
                    Path: new JsonPath(
                        $"$.parents[{index.ToString(CultureInfo.InvariantCulture)}]._ext.aligned.sponsorReference"
                    )
                )
            );
        }

        return references;
    }
}

/// <summary>
/// Resolves a fixture resource's <see cref="ProjectSchema" />/<see cref="ResourceSchema" /> pair by
/// endpoint and resource name.
/// </summary>
internal static class MssqlCaseVariantReferenceSchemaLookup
{
    public static (ProjectSchema ProjectSchema, ResourceSchema ResourceSchema) GetResourceSchema(
        EffectiveSchemaSet effectiveSchemaSet,
        string projectEndpointName,
        string resourceName
    )
    {
        ArgumentNullException.ThrowIfNull(effectiveSchemaSet);

        EffectiveProjectSchema effectiveProjectSchema = effectiveSchemaSet.ProjectsInEndpointOrder.Single(
            project =>
                string.Equals(
                    project.ProjectEndpointName,
                    projectEndpointName,
                    StringComparison.OrdinalIgnoreCase
                )
        );

        ProjectSchema projectSchema = new(effectiveProjectSchema.ProjectSchema, NullLogger.Instance);
        JsonNode resourceSchemaNode =
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
            AllowIdentityUpdates: resourceSchema.AllowIdentityUpdates
        );
}

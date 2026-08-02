// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Globalization;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

/// <summary>
/// The PostgreSQL control for the SQL Server collation delta on REFERENCE RESOLUTION: a document reference
/// whose string identity value differs from the stored row only by case is a MISS here, so the write is
/// refused as an unresolved reference.
/// </summary>
/// <remarks>
/// Plan decision 14 moved string identity comparison out of C# and into the database, so the dialect's
/// collation now decides whether a reference resolves. PostgreSQL's default collations are deterministic:
/// <c>UX_Sponsor_NK</c> compares byte-for-byte and a case variant addresses no row.
/// <para>
/// The twin is <c>Given_A_Mssql_Relational_Post_With_A_Case_Variant_String_Reference_Identity</c>, where the
/// same request resolves under SQL Server's default case-insensitive collation and the write succeeds.
/// Together they are the reference-resolution counterpart of the upsert-detection pair
/// (<c>Given_A_Postgresql_Relational_Post_With_A_Case_Variant_String_Natural_Key</c> and its SQL Server
/// twin); each test also pins the exact-casing control so the case of the identity value is provably the
/// only variable.
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
public class Given_A_Postgresql_Relational_Post_With_A_Case_Variant_String_Reference_Identity
{
    private const string FixtureRelativePath =
        "src/dms/backend/EdFi.DataManagementService.Backend.IntegrationFixtures/profile-collection-aligned-extension-with-doc-ref";

    private const int ParentResourceId = 4242;
    private const string StoredSponsorName = "Acme Education Sponsor";
    private const string CaseVariantSponsorName = "ACME EDUCATION SPONSOR";
    private const string ParentCode = "P-001";

    private static readonly DocumentUuid SponsorDocumentUuid = new(
        Guid.Parse("cccc0005-0000-0000-0000-000000000001")
    );
    private static readonly DocumentUuid ParentResourceDocumentUuid = new(
        Guid.Parse("cccc0005-0000-0000-0000-000000000002")
    );

    private PostgresqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private PostgresqlGeneratedDdlTestDatabase _database = null!;
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
        _fixture = PostgresqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            FixtureRelativePath,
            strict: true
        );
        _mappingSet = _fixture.MappingSet;
        _database = await PostgresqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);

        (ProjectSchema sponsorProjectSchema, ResourceSchema sponsorSchema) =
            CaseVariantReferenceSchemaLookup.GetResourceSchema(
                _fixture.EffectiveSchemaSet,
                "ed-fi",
                "Sponsor"
            );
        _sponsorResourceInfo = CaseVariantReferenceSchemaLookup.CreateResourceInfo(
            sponsorProjectSchema,
            sponsorSchema
        );
        _sponsorResourceSchema = sponsorSchema;

        (ProjectSchema parentProjectSchema, ResourceSchema parentSchema) =
            CaseVariantReferenceSchemaLookup.GetResourceSchema(
                _fixture.EffectiveSchemaSet,
                "ed-fi",
                "ParentResource"
            );
        _parentResourceInfo = CaseVariantReferenceSchemaLookup.CreateResourceInfo(
            parentProjectSchema,
            parentSchema
        );
        _parentResourceSchema = parentSchema;

        (ProjectSchema alignedProjectSchema, ResourceSchema alignedSchema) =
            CaseVariantReferenceSchemaLookup.GetResourceSchema(
                _fixture.EffectiveSchemaSet,
                "aligned",
                "ParentResource"
            );
        _alignedExtParentResourceInfo = CaseVariantReferenceSchemaLookup.CreateResourceInfo(
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

        var boundSponsorNames = await ReadBoundSponsorNamesAsync();
        boundSponsorNames.Should().ContainSingle().Which.Should().Be(StoredSponsorName);
    }

    [Test]
    public async Task It_misses_a_case_variant_string_reference_identity()
    {
        var result = await UpsertParentResourceAsync(CaseVariantSponsorName);

        var failure = result
            .Should()
            .BeOfType<UpsertResult.UpsertFailureReference>(
                "PostgreSQL's deterministic collation makes UX_Sponsor_NK compare byte-for-byte, so a "
                    + "case-variant identity value addresses no Sponsor row"
            )
            .Subject;

        failure
            .InvalidDocumentReferences.Select(reference =>
                (reference.Path.Value, reference.TargetResource.ResourceName.Value, reference.Reason)
            )
            .Should()
            .Equal(
                (
                    "$.parents[0]._ext.aligned.sponsorReference",
                    "Sponsor",
                    DocumentReferenceFailureReason.Missing
                )
            );

        // The SQL Server delta, stated as its negation on this dialect.
        result.Should().NotBeOfType<UpsertResult.InsertSuccess>();

        var boundSponsorNames = await ReadBoundSponsorNamesAsync();
        boundSponsorNames.Should().BeEmpty("a refused POST must not persist the referencing document");
    }

    private async Task<IReadOnlyList<string>> ReadBoundSponsorNamesAsync()
    {
        List<string> sponsorNames = [];

        await using NpgsqlConnection connection = new(_database.ConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT "Sponsor_SponsorName"
            FROM aligned."ParentResourceExtensionParent"
            WHERE "Sponsor_SponsorName" IS NOT NULL
            ORDER BY "BaseCollectionItemId";
            """;

        await using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            sponsorNames.Add(reader.GetString(0));
        }

        return sponsorNames;
    }

    private async Task<UpsertResult> UpsertSponsorAsync()
    {
        await using AsyncServiceScope scope = _serviceProvider.CreateAsyncScope();
        SetSelectedInstance(scope.ServiceProvider);

        JsonNode requestBody = CaseVariantReferenceBodies.CreateSponsorRequestBody(StoredSponsorName);
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
            TraceId: new TraceId("pgsql-case-variant-reference-seed-sponsor"),
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

        JsonNode requestBody = CaseVariantReferenceBodies.CreateParentResourceRequestBody(
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
                    DocumentReferences: CaseVariantReferenceBodies.BuildAlignedSponsorReferences(requestBody),
                    DocumentReferenceArrays: [],
                    DescriptorReferences: []
                )
            ),
            MappingSet: _mappingSet,
            EdfiDoc: requestBody,
            Headers: [],
            TraceId: new TraceId("pgsql-case-variant-reference-post-parentresource"),
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
        services.AddSingleton<NpgsqlDataSourceCache>();
        services.AddScoped<IDataStoreSelection, DataStoreSelection>();
        services.AddScoped<NpgsqlDataSourceProvider>();
        services.Configure<DatabaseOptions>(options => options.IsolationLevel = IsolationLevel.ReadCommitted);
        services.AddTestReadableProfileProjector();
        services.AddScoped<RelationalDocumentStoreRepository>();

        // The production registration: the natural-key resolver is the IReferenceResolver.
        services.AddPostgresqlReferenceResolver();

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
                    Name: "PostgresqlCaseVariantNaturalKeyReference",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );
    }
}

/// <summary>
/// Request bodies and the hand-built Sponsor reference shared by the PostgreSQL case-variant reference pin.
/// The aligned extension exposes the binding shape but not the full mapping pipeline, so the reference is
/// supplied through <see cref="RelationalDocumentInfoSupplement" /> rather than extracted — the same
/// technique <c>Given_A_Postgresql_ParentResource_With_Collection_Aligned_Extension_Sponsor_Reference</c>
/// uses on this fixture.
/// </summary>
internal static class CaseVariantReferenceBodies
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
internal static class CaseVariantReferenceSchemaLookup
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

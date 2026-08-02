// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Mssql;
using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Backend.Tests.Integration.Common;
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
/// The SQL Server collation delta on the WRITE path, asserted rather than diffed — the upsert-detection
/// counterpart of
/// <c>Given_A_Mssql_Relational_Post_With_A_Case_Variant_String_Reference_Identity</c>, which pins the same
/// collation deciding whether a document REFERENCE resolves.
/// </summary>
/// <remarks>
/// Plan decision 14 moved string identity comparison out of C# and into the database. Reference resolution
/// was the first consequence; upsert detection is the second, and it changes a status code. A POST whose
/// string natural key differs from a stored row only by case now seeks <c>UX_Program_NK</c> under SQL
/// Server's default case-insensitive collation, matches, and resolves to an EXISTING document — where the
/// UUIDv5 hash resolved to none. The write then merges the request's casing over the stored row, the
/// immutable-identity guard compares merged against current <b>ordinally</b>, sees a difference, and
/// refuses the write.
/// <para>
/// The net effect is <b>409 → 400</b> on SQL Server: the old flow created a new document that lost to the
/// <c>UX_Program_NK</c> unique constraint (an identity conflict), the new flow refuses up front as an
/// immutable-identity violation. Both refuse the write and neither mutates stored state, and the new
/// outcome is the one PUT already produced for a case-variant identity edit — so POST and PUT are now
/// consistent on this dialect. PostgreSQL is unaffected (case-sensitive comparison, still a create); its
/// twin is <c>Given_A_Postgresql_Relational_Post_With_A_Case_Variant_String_Natural_Key</c>.
/// </para>
/// </remarks>
[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[Category(MssqlCiShards.Shard2)]
public class Given_A_Mssql_Relational_Post_With_A_Case_Variant_String_Natural_Key
{
    private const string StoredProgramName = "Career Readiness";
    private const string CaseVariantProgramName = "CAREER READINESS";

    private static readonly DocumentUuid StoredProgramDocumentUuid = new(
        Guid.Parse("dddd0004-0000-0000-0000-000000000001")
    );
    private static readonly DocumentUuid CaseVariantProgramDocumentUuid = new(
        Guid.Parse("dddd0004-0000-0000-0000-000000000002")
    );

    private static readonly ResourceInfo ProgramResourceInfo = new(
        ProjectName: new ProjectName("Ed-Fi"),
        ResourceName: new ResourceName("Program"),
        IsDescriptor: false,
        ResourceVersion: new SemVer("1.0.0"),
        AllowIdentityUpdates: false
    );

    private MssqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private MssqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;

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
            MssqlProfileTopLevelCollectionMergeSupport.FixtureRelativePath
        );
        _mappingSet = _fixture.MappingSet;
        _database = await MssqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
    }

    [SetUp]
    public async Task SetUp()
    {
        await _database.ResetAsync();
        _serviceProvider = CreateServiceProvider();
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
    public async Task It_refuses_a_case_variant_natural_key_post_as_an_immutable_identity_violation()
    {
        var created = await ExecuteUpsertAsync(StoredProgramName, StoredProgramDocumentUuid);
        created.Should().BeOfType<UpsertResult.InsertSuccess>("the first POST establishes the stored casing");

        var caseVariant = await ExecuteUpsertAsync(CaseVariantProgramName, CaseVariantProgramDocumentUuid);

        caseVariant
            .Should()
            .BeOfType<UpsertResult.UpsertFailureImmutableIdentity>(
                "SQL Server's case-insensitive collation makes UX_Program_NK match the stored row, so the "
                    + "POST resolves to an existing document whose identity the request would rewrite"
            );

        // Tripwire on the old outcome: the hash probe resolved to no target, inserted, and lost to
        // UX_Program_NK as an identity conflict. That path is gone, and this is the delta worth pinning.
        caseVariant.Should().NotBeOfType<UpsertResult.UpsertFailureIdentityConflict>();
        caseVariant.Should().NotBeOfType<UpsertResult.InsertSuccess>();
        caseVariant.Should().NotBeOfType<UpsertResult.UpdateSuccess>();
    }

    [Test]
    public async Task It_leaves_the_stored_casing_untouched_when_a_case_variant_post_is_refused()
    {
        await ExecuteUpsertAsync(StoredProgramName, StoredProgramDocumentUuid);
        await ExecuteUpsertAsync(CaseVariantProgramName, CaseVariantProgramDocumentUuid);

        var storedNames = await ReadProgramNamesAsync();

        storedNames
            .Should()
            .Equal(
                [StoredProgramName],
                "a refused POST must neither insert a second row nor rewrite the stored casing"
            );
    }

    private async Task<IReadOnlyList<string>> ReadProgramNamesAsync()
    {
        List<string> programNames = [];

        await using var connection = new Microsoft.Data.SqlClient.SqlConnection(_database.ConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT [ProgramName] FROM [edfi].[Program] ORDER BY [DocumentId];";

        await using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            programNames.Add(reader.GetString(0));
        }

        return programNames;
    }

    private async Task<UpsertResult> ExecuteUpsertAsync(string programName, DocumentUuid documentUuid)
    {
        using var scope = _serviceProvider.CreateScope();
        scope
            .ServiceProvider.GetRequiredService<IDataStoreSelection>()
            .SetSelectedDataStore(
                new DataStore(
                    Id: 1,
                    DataStoreType: "test",
                    Name: "MssqlCaseVariantNaturalKeyPost",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var identity = new DocumentIdentity([
            new DocumentIdentityElement(new JsonPath("$.programName"), programName),
        ]);
        var documentInfo = new DocumentInfo(
            DocumentIdentity: identity,
            ReferentialId: ReferentialIdCalculator.ReferentialIdFrom(ProgramResourceInfo, identity),
            DocumentReferences: [],
            DocumentReferenceArrays: [],
            DescriptorReferences: [],
            SuperclassIdentity: null
        );

        var upsertRequest = new UpsertRequest(
            ResourceInfo: ProgramResourceInfo,
            DocumentInfo: documentInfo,
            MappingSet: _mappingSet,
            EdfiDoc: new JsonObject { ["programName"] = programName },
            Headers: [],
            TraceId: new TraceId("mssql-case-variant-natural-key-post"),
            DocumentUuid: documentUuid
        );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();
        return await repository.UpsertDocument(upsertRequest);
    }

    private ServiceProvider CreateServiceProvider()
    {
        ServiceCollection services = [];

        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddScoped<IDataStoreSelection, DataStoreSelection>();
        services.Configure<DatabaseOptions>(options => options.IsolationLevel = IsolationLevel.ReadCommitted);
        services.AddTestReadableProfileProjector();
        services.AddScoped<RelationalDocumentStoreRepository>();

        // The production registration: the natural-key resolver wins the IReferenceResolver slot.
        services.AddMssqlNaturalKeyReferenceResolver();

        return services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }
        );
    }
}

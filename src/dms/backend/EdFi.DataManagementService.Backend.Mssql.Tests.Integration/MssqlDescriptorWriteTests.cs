// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Mssql;
using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Profile;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[Category(MssqlCiShards.Shard3)]
public class Given_MssqlDescriptorWriteHandler
{
    private MssqlReferenceResolverTestDatabase _database = null!;
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

        _database = await MssqlReferenceResolverTestDatabase.CreateProvisionedAsync();
    }

    [SetUp]
    public async Task Setup()
    {
        await _database.ResetAsync();
        await _database.SeedAsync();
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
    public async Task It_inserts_a_new_descriptor_via_post_under_wildcard_if_none_match()
    {
        var handler = ResolveHandler();
        var request = CreatePostRequest(
            _database.Fixture.SchoolTypeDescriptorResource,
            """
            {
                "namespace": "uri://ed-fi.org/SchoolTypeDescriptor",
                "codeValue": "GuardedCreate",
                "shortDescription": "Guarded Create"
            }
            """
        ) with
        {
            WritePrecondition = new WritePrecondition.IfNoneMatch("*", IsWildcard: true),
        };

        var result = await handler.HandlePostAsync(request);

        result.Should().BeOfType<UpsertResult.InsertSuccess>();
    }

    [Test]
    public async Task It_returns_precondition_failed_for_existing_descriptor_post_under_wildcard_if_none_match()
    {
        var handler = ResolveHandler();
        var resource = _database.Fixture.SchoolTypeDescriptorResource;
        const string body = """
            {
                "namespace": "uri://ed-fi.org/SchoolTypeDescriptor",
                "codeValue": "GuardedExistingPost",
                "shortDescription": "Guarded Existing Post"
            }
            """;
        var createResult = await handler.HandlePostAsync(CreatePostRequest(resource, body));
        createResult.Should().BeOfType<UpsertResult.InsertSuccess>();
        var request = CreatePostRequest(resource, body) with
        {
            WritePrecondition = new WritePrecondition.IfNoneMatch("*", IsWildcard: true),
        };

        var result = await handler.HandlePostAsync(request);

        result
            .Should()
            .BeOfType<UpsertResult.UpsertFailureETagMisMatch>()
            .Which.Reason.Should()
            .Be(ETagPreconditionFailureReason.CurrentRepresentationMatchesIfNoneMatch);
    }

    [Test]
    public async Task It_returns_precondition_failed_for_existing_descriptor_put_under_wildcard_if_none_match()
    {
        var handler = ResolveHandler();
        var resource = _database.Fixture.SchoolTypeDescriptorResource;
        const string body = """
            {
                "namespace": "uri://ed-fi.org/SchoolTypeDescriptor",
                "codeValue": "GuardedExistingPut",
                "shortDescription": "Guarded Existing Put"
            }
            """;
        var createResult = await handler.HandlePostAsync(CreatePostRequest(resource, body));
        var documentUuid = ((UpsertResult.InsertSuccess)createResult).NewDocumentUuid;
        var request = CreatePutRequest(resource, documentUuid, body) with
        {
            WritePrecondition = new WritePrecondition.IfNoneMatch("*", IsWildcard: true),
        };

        var result = await handler.HandlePutAsync(request);

        result
            .Should()
            .BeOfType<UpdateResult.UpdateFailureETagMisMatch>()
            .Which.Reason.Should()
            .Be(ETagPreconditionFailureReason.CurrentRepresentationMatchesIfNoneMatch);
    }

    [Test]
    public async Task It_returns_not_exists_for_missing_descriptor_put_under_wildcard_if_none_match()
    {
        var handler = ResolveHandler();
        var request = CreatePutRequest(
            _database.Fixture.SchoolTypeDescriptorResource,
            new DocumentUuid(Guid.NewGuid()),
            """
            {
                "namespace": "uri://ed-fi.org/SchoolTypeDescriptor",
                "codeValue": "GuardedMissingPut",
                "shortDescription": "Guarded Missing Put"
            }
            """
        ) with
        {
            WritePrecondition = new WritePrecondition.IfNoneMatch("*", IsWildcard: true),
        };

        var result = await handler.HandlePutAsync(request);

        result.Should().BeOfType<UpdateResult.UpdateFailureNotExists>();
    }

    [Test]
    public async Task It_updates_tracking_stamps_when_descriptor_representation_changes()
    {
        var handler = ResolveHandler();

        var createRequest = CreatePostRequest(
            _database.Fixture.SchoolTypeDescriptorResource,
            """
            {
                "namespace": "uri://ed-fi.org/SchoolTypeDescriptor",
                "codeValue": "Regular",
                "shortDescription": "Regular"
            }
            """
        );
        var insertResult = await handler.HandlePostAsync(createRequest);
        var documentUuid = ((UpsertResult.InsertSuccess)insertResult).NewDocumentUuid;

        var stampsAfterInsert = await ReadTrackingStampsAsync(documentUuid);

        var putRequest = CreatePutRequest(
            _database.Fixture.SchoolTypeDescriptorResource,
            documentUuid,
            """
            {
                "namespace": "uri://ed-fi.org/SchoolTypeDescriptor",
                "codeValue": "Regular",
                "shortDescription": "Regular School",
                "description": "Updated description"
            }
            """
        );
        await handler.HandlePutAsync(putRequest);

        var stampsAfterUpdate = await ReadTrackingStampsAsync(documentUuid);

        stampsAfterUpdate.ContentVersion.Should().BeGreaterThan(stampsAfterInsert.ContentVersion);
        stampsAfterUpdate.ContentLastModifiedAt.Should().BeOnOrAfter(stampsAfterInsert.ContentLastModifiedAt);
    }

    [Test]
    public async Task It_preserves_tracking_stamps_on_no_op_put()
    {
        var handler = ResolveHandler();

        var createRequest = CreatePostRequest(
            _database.Fixture.SchoolTypeDescriptorResource,
            """
            {
                "namespace": "uri://ed-fi.org/SchoolTypeDescriptor",
                "codeValue": "Regular",
                "shortDescription": "Regular"
            }
            """
        );
        var insertResult = await handler.HandlePostAsync(createRequest);
        var documentUuid = ((UpsertResult.InsertSuccess)insertResult).NewDocumentUuid;

        var stampsAfterInsert = await ReadTrackingStampsAsync(documentUuid);

        var putRequest = CreatePutRequest(
            _database.Fixture.SchoolTypeDescriptorResource,
            documentUuid,
            """
            {
                "namespace": "uri://ed-fi.org/SchoolTypeDescriptor",
                "codeValue": "Regular",
                "shortDescription": "Regular"
            }
            """
        );
        await handler.HandlePutAsync(putRequest);

        var stampsAfterNoOp = await ReadTrackingStampsAsync(documentUuid);

        stampsAfterNoOp.ContentVersion.Should().Be(stampsAfterInsert.ContentVersion);
        stampsAfterNoOp.ContentLastModifiedAt.Should().Be(stampsAfterInsert.ContentLastModifiedAt);
    }

    [Test]
    public async Task It_returns_descriptor_write_etags_matching_follow_up_get_by_id()
    {
        using var scope = CreateConfiguredScope();
        var writeHandler = scope.ServiceProvider.GetRequiredService<IDescriptorWriteHandler>();
        var readHandler = scope.ServiceProvider.GetRequiredService<IDescriptorReadHandler>();

        var createRequest = CreatePostRequest(
            _database.Fixture.SchoolTypeDescriptorResource,
            """
            {
                "namespace": "uri://ed-fi.org/SchoolTypeDescriptor",
                "codeValue": "Parity",
                "shortDescription": "Parity"
            }
            """
        );
        var createResult = await writeHandler.HandlePostAsync(createRequest);
        var documentUuid = ((UpsertResult.InsertSuccess)createResult).NewDocumentUuid;

        ((UpsertResult.InsertSuccess)createResult)
            .ETag.Should()
            .MatchRegex(
                @"^\d+-[a-z0-9]{1,8}\.j\._\.n$",
                "the descriptor write etag is composed as {ContentVersion}-{schemaEpoch}.j._.n (no profile, links off)"
            );
        RelationalGetIntegrationTestHelper.AssertWriteResultEtagParity(
            createResult,
            await GetDescriptorByIdAsync(
                readHandler,
                _database.Fixture.SchoolTypeDescriptorResource,
                documentUuid
            )
        );

        var upsertRequest = CreatePostRequest(
            _database.Fixture.SchoolTypeDescriptorResource,
            """
            {
                "namespace": "uri://ed-fi.org/SchoolTypeDescriptor",
                "codeValue": "Parity",
                "shortDescription": "Parity Upsert",
                "description": "Updated through POST"
            }
            """
        );
        var upsertResult = await writeHandler.HandlePostAsync(upsertRequest);

        RelationalGetIntegrationTestHelper.AssertWriteResultEtagParity(
            upsertResult,
            await GetDescriptorByIdAsync(
                readHandler,
                _database.Fixture.SchoolTypeDescriptorResource,
                documentUuid
            )
        );

        var putRequest = CreatePutRequest(
            _database.Fixture.SchoolTypeDescriptorResource,
            documentUuid,
            """
            {
                "namespace": "uri://ed-fi.org/SchoolTypeDescriptor",
                "codeValue": "Parity",
                "shortDescription": "Parity PUT",
                "description": "Updated through PUT"
            }
            """
        );
        var putResult = await writeHandler.HandlePutAsync(putRequest);

        RelationalGetIntegrationTestHelper.AssertWriteResultEtagParity(
            putResult,
            await GetDescriptorByIdAsync(
                readHandler,
                _database.Fixture.SchoolTypeDescriptorResource,
                documentUuid
            )
        );
    }

    [Test]
    public async Task It_returns_profiled_descriptor_write_etags_matching_a_follow_up_profiled_get()
    {
        const string profileName = "E2E-Test-SchoolTypeDescriptor-Profile";

        using var scope = CreateConfiguredScope();
        var writeHandler = scope.ServiceProvider.GetRequiredService<IDescriptorWriteHandler>();
        var readHandler = scope.ServiceProvider.GetRequiredService<IDescriptorReadHandler>();
        var resource = _database.Fixture.SchoolTypeDescriptorResource;
        var profileContext = CreateReadableProfileProjectionContext(profileName);

        var createRequest = CreatePostRequest(
            resource,
            """
            {
                "namespace": "uri://ed-fi.org/SchoolTypeDescriptor",
                "codeValue": "ProfiledParity",
                "shortDescription": "Profiled Parity"
            }
            """,
            profileName
        );
        var createResult = await writeHandler.HandlePostAsync(createRequest);
        var documentUuid = ((UpsertResult.InsertSuccess)createResult).NewDocumentUuid;

        // The profiled POST etag matches a follow-up profiled GET of the same representation.
        RelationalGetIntegrationTestHelper.AssertWriteResultEtagParity(
            createResult,
            await GetDescriptorByIdAsync(readHandler, resource, documentUuid, profileContext)
        );

        // ...and is a distinct strong validator from the unprofiled representation's etag.
        var unprofiledGet = await GetDescriptorByIdAsync(readHandler, resource, documentUuid);
        RelationalGetIntegrationTestHelper
            .ReadResultEtag(unprofiledGet)
            .Should()
            .NotBe(((UpsertResult.InsertSuccess)createResult).ETag);

        var putRequest = CreatePutRequest(
            resource,
            documentUuid,
            """
            {
                "namespace": "uri://ed-fi.org/SchoolTypeDescriptor",
                "codeValue": "ProfiledParity",
                "shortDescription": "Profiled Parity PUT",
                "description": "Updated through profiled PUT"
            }
            """,
            profileName
        );
        var putResult = await writeHandler.HandlePutAsync(putRequest);

        RelationalGetIntegrationTestHelper.AssertWriteResultEtagParity(
            putResult,
            await GetDescriptorByIdAsync(readHandler, resource, documentUuid, profileContext)
        );
    }

    [Test]
    public async Task It_deletes_a_descriptor_when_if_match_exactly_matches_the_current_etag()
    {
        var handler = ResolveHandler();
        var createRequest = CreatePostRequest(
            _database.Fixture.SchoolTypeDescriptorResource,
            """
            {
                "namespace": "uri://ed-fi.org/SchoolTypeDescriptor",
                "codeValue": "Regular",
                "shortDescription": "Regular"
            }
            """
        );
        var insertResult = await handler.HandlePostAsync(createRequest);
        var success = (UpsertResult.InsertSuccess)insertResult;
        success.ETag.Should().NotBeNull();

        var result = await handler.HandleDeleteAsync(
            CreateDeleteRequest(
                _database.Fixture.SchoolTypeDescriptorResource,
                success.NewDocumentUuid,
                new WritePrecondition.IfMatch(success.ETag!)
            )
        );

        result.Should().BeOfType<DeleteResult.DeleteSuccess>();
    }

    [Test]
    public async Task It_returns_precondition_failed_when_descriptor_delete_if_match_mismatches()
    {
        var handler = ResolveHandler();
        var createRequest = CreatePostRequest(
            _database.Fixture.SchoolTypeDescriptorResource,
            """
            {
                "namespace": "uri://ed-fi.org/SchoolTypeDescriptor",
                "codeValue": "Regular",
                "shortDescription": "Regular"
            }
            """
        );
        var insertResult = await handler.HandlePostAsync(createRequest);
        var documentUuid = ((UpsertResult.InsertSuccess)insertResult).NewDocumentUuid;

        var result = await handler.HandleDeleteAsync(
            CreateDeleteRequest(
                _database.Fixture.SchoolTypeDescriptorResource,
                documentUuid,
                new WritePrecondition.IfMatch("\"stale-etag\"")
            )
        );

        result.Should().BeOfType<DeleteResult.DeleteFailureETagMisMatch>();

        await using var connection = new SqlConnection(_database.ConnectionString);
        await connection.OpenAsync();
        await using var probe = connection.CreateCommand();
        probe.CommandText = "SELECT 1 FROM [dms].[Document] WHERE [DocumentUuid] = @documentUuid";
        probe.Parameters.Add(new SqlParameter("@documentUuid", documentUuid.Value));
        var stillThere = await probe.ExecuteScalarAsync();
        stillThere.Should().NotBeNull("mismatched If-Match must not delete the descriptor row");
    }

    // ── Helper methods ──────────────────────────────────────────────────

    private async Task<GetResult> GetDescriptorByIdAsync(
        IDescriptorReadHandler handler,
        QualifiedResourceName resource,
        DocumentUuid documentUuid,
        ReadableProfileProjectionContext? readableProfileProjectionContext = null
    )
    {
        return await handler
            .HandleGetByIdAsync(
                new DescriptorGetByIdRequest(
                    _database.MappingSet,
                    resource,
                    documentUuid,
                    RelationalGetRequestReadMode.ExternalResponse,
                    authorizationStrategyEvaluators: [],
                    readableProfileProjectionContext: readableProfileProjectionContext,
                    traceId: new TraceId("test-trace")
                )
            )
            .ConfigureAwait(false);
    }

    // A readable projection context whose ProfileName drives the served etag's profileCode. The
    // content-type selection is immaterial to the etag (only ProfileName + ContentVersion +
    // schemaEpoch feed it), so an IncludeAll pass-through with the descriptor identity fields is enough.
    private static ReadableProfileProjectionContext CreateReadableProfileProjectionContext(
        string profileName
    ) =>
        new(
            new ContentTypeDefinition(MemberSelection.IncludeAll, [], [], [], []),
            new HashSet<string>(StringComparer.Ordinal) { "namespace", "codeValue" }
        )
        {
            ProfileName = profileName,
        };

    private DescriptorDeleteRequest CreateDeleteRequest(
        QualifiedResourceName resource,
        DocumentUuid documentUuid,
        WritePrecondition? writePrecondition = null
    )
    {
        return new DescriptorDeleteRequest(
            _database.MappingSet,
            resource,
            documentUuid,
            new TraceId("test-trace")
        )
        {
            WritePrecondition = writePrecondition ?? new WritePrecondition.None(),
        };
    }

    private async Task<DocumentTrackingStamps> ReadTrackingStampsAsync(DocumentUuid documentUuid)
    {
        await using var connection = new SqlConnection(_database.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT [ContentVersion], [ContentLastModifiedAt]
            FROM [dms].[Document]
            WHERE [DocumentUuid] = @documentUuid;
            """;
        command.Parameters.Add(new SqlParameter("@documentUuid", documentUuid.Value));
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue("document should exist");

        return new DocumentTrackingStamps(reader.GetInt64(0), reader.GetDateTime(1));
    }

    private sealed record DocumentTrackingStamps(long ContentVersion, DateTime ContentLastModifiedAt);

    private IServiceScope CreateConfiguredScope()
    {
        var scope = _serviceProvider.CreateScope();
        var instanceSelection = scope.ServiceProvider.GetRequiredService<IDataStoreSelection>();
        instanceSelection.SetSelectedDataStore(
            new DataStore(
                Id: 1,
                DataStoreType: "test",
                Name: "MssqlDescriptorWriteIntegration",
                ConnectionString: _database.ConnectionString,
                RouteContext: []
            )
        );

        return scope;
    }

    private IDescriptorWriteHandler ResolveHandler()
    {
        var scope = CreateConfiguredScope();

        return scope.ServiceProvider.GetRequiredService<IDescriptorWriteHandler>();
    }

    private DescriptorWriteRequest CreatePostRequest(
        QualifiedResourceName resource,
        string bodyJson,
        string? profileName = null
    )
    {
        var body = JsonNode.Parse(bodyJson)!;
        var descriptorDoc = new Core.Model.DescriptorDocument(body);
        var resourceInfo = new BaseResourceInfo(
            new ProjectName(resource.ProjectName),
            new ResourceName(resource.ResourceName),
            true
        );
        var identity = descriptorDoc.ToDocumentIdentity();
        var referentialId = Core.Extraction.ReferentialIdCalculator.ReferentialIdFrom(resourceInfo, identity);

        return new DescriptorWriteRequest(
            _database.MappingSet,
            resource,
            body,
            new DocumentUuid(Guid.NewGuid()),
            referentialId,
            new TraceId("test-trace")
        )
        {
            ProfileName = profileName,
        };
    }

    private DescriptorWriteRequest CreatePutRequest(
        QualifiedResourceName resource,
        DocumentUuid documentUuid,
        string bodyJson,
        string? profileName = null
    )
    {
        return new DescriptorWriteRequest(
            _database.MappingSet,
            resource,
            JsonNode.Parse(bodyJson)!,
            documentUuid,
            referentialId: null,
            new TraceId("test-trace")
        )
        {
            ProfileName = profileName,
        };
    }

    private static ServiceProvider CreateServiceProvider()
    {
        var services = new ServiceCollection();

        services.Configure<DatabaseOptions>(options => options.IsolationLevel = IsolationLevel.ReadCommitted);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddScoped<IDataStoreSelection, DataStoreSelection>();
        services.AddTestReadableProfileProjector();
        services.AddMssqlReferenceResolver();

        return services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }
        );
    }
}

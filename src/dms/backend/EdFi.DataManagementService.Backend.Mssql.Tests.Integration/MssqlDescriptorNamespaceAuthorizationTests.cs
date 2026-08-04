// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

/// <summary>
/// Real-SQL-Server coverage for NamespaceBased authorization on descriptor resources (DMS-1286). Descriptors
/// store their securable namespace on the shared <c>dms.Descriptor</c> root table and bypass the generic
/// resource write executor, so their read and write handlers carry their own namespace seams and must be
/// certified separately from the ordinary-resource paths.
/// </summary>
/// <remarks>
/// Every operation is issued through <c>RelationalDocumentStoreRepository</c>, which routes descriptor
/// resources into the production <c>DescriptorReadHandler</c> and <c>DescriptorWriteHandler</c> and carries the
/// authorization context with them, so the routing seam is exercised rather than bypassed.
/// <para>
/// A <em>missing</em> proposed descriptor namespace is deliberately not covered here: <c>namespace</c> is
/// required on every Ed-Fi descriptor, so JSON schema validation is the production boundary for that case and
/// <c>DescriptorWriteBodyExtractor</c> treats its absence as an internal pipeline bug rather than an
/// authorization outcome. The missing-proposed-value authorization path is covered on the ordinary-resource
/// write suite, whose synthetic resource has an optional namespace.
/// </para>
/// <para>
/// A descriptor's identity is its uri — <c>namespace#codeValue</c> — so a POST carrying a namespace other than
/// the stored one addresses a different descriptor and takes the create path. A proposed-value denial on the
/// upsert-as-update path is therefore structurally unreachable; that value source is covered by descriptor POST
/// create and descriptor PUT, which targets by document uuid and can legitimately carry a changed namespace.
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
[Category("Authorization")]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[Category("RelationalNamespace")]
[Category(MssqlCiShards.Shard4)]
public class Given_A_Mssql_Descriptor_Namespace_Authorization_With_The_Authoritative_Sample_Fixture
{
    private const string FixtureRelativePath = "src/dms/backend/Fixtures/authoritative/sample";
    private const string ProjectEndpointName = "ed-fi";
    private const string ResourceName = "SchoolTypeDescriptor";
    private const string AuthorizedPrefix =
        RelationshipAuthorizationCrudTestSupport.AuthorizedNamespacePrefix;
    private const string SecondAuthorizedPrefix =
        RelationshipAuthorizationCrudTestSupport.SecondAuthorizedNamespacePrefix;
    private const string UnauthorizedPrefix =
        RelationshipAuthorizationCrudTestSupport.UnauthorizedNamespacePrefix;
    private const string AuthorizedNamespace = AuthorizedPrefix + ResourceName;
    private const string SecondAuthorizedNamespace = SecondAuthorizedPrefix + ResourceName;
    private const string UnauthorizedNamespace = UnauthorizedPrefix + ResourceName;
    private const string StaleETag = "\"stale-etag\"";

    private static readonly IReadOnlyList<string> _configuredPrefixes =
        RelationshipAuthorizationCrudTestSupport.ConfiguredNamespacePrefixes;
    private static readonly IReadOnlyList<string> _namespaceStrategy =
        RelationshipAuthorizationCrudTestSupport.NamespaceBasedStrategyNames;

    /// <summary>
    /// The descriptor's own tracking columns, which live on <c>dms.Descriptor</c> rather than
    /// <c>dms.Document</c>. A denial snapshot that omitted them could not prove descriptor tracking state was
    /// preserved, so their presence is asserted whenever a snapshot is taken.
    /// </summary>
    private static readonly string[] _requiredDescriptorTrackingColumns =
    [
        "ResourceKeyId",
        "ContentVersion",
        "ContentLastModifiedAt",
    ];

    private MssqlRelationalQueryAuthorizationTestContext _context = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        if (!MssqlTestDatabaseHelper.IsConfigured())
        {
            Assert.Ignore(
                "SQL Server integration tests require a MssqlAdmin connection string in appsettings.Test.json"
            );
        }

        _context = new MssqlRelationalQueryAuthorizationTestContext();
        await _context.InitializeAsync(FixtureRelativePath, strict: true, replaceReadTargetLookup: false);
    }

    [SetUp]
    public async Task SetUp()
    {
        // A descriptor row is self-contained, so no reference data is needed after the reset.
        await _context.Database.ResetAsync();
        _context.ResetRecorder();
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_context is not null)
        {
            await _context.DisposeAsync();
        }
    }

    // ── Reads ───────────────────────────────────────────────────────────

    [Test]
    public async Task It_authorizes_descriptor_get_by_id_for_a_matching_namespace()
    {
        var documentUuid = await SeedDescriptorAsync("Authorized", AuthorizedNamespace);

        var result = await GetByIdAsync(documentUuid);

        var success = result.Should().BeOfType<GetResult.GetSuccess>().Subject;
        success.DocumentUuid.Should().Be(documentUuid);
        success.EdfiDoc["namespace"]!.GetValue<string>().Should().Be(AuthorizedNamespace);
    }

    [Test]
    public async Task It_denies_descriptor_get_by_id_for_a_mismatching_namespace_without_exposing_the_document()
    {
        var documentUuid = await SeedDescriptorAsync("Denied", UnauthorizedNamespace);

        var result = await GetByIdAsync(documentUuid);

        // The denial result carries no document at all, so the stored descriptor is never exposed.
        AssertNamespaceDenied(
            result.Should().BeOfType<GetResult.GetFailureNamespaceNotAuthorized>().Subject.NamespaceFailure,
            NamespaceAuthorizationFailureValueSource.Stored
        );
    }

    [Test]
    public async Task It_filters_descriptor_get_many_to_authorized_namespaces_before_paging_and_total_count()
    {
        // Seeded in document order: authorized, denied, authorized, denied, authorized. Skipping one
        // authorized row must yield the second and third authorized rows; paging before filtering would
        // instead window the unfiltered rows 2 and 3 and return a single authorized row.
        var first = await SeedDescriptorAsync("First", AuthorizedNamespace);
        await SeedDescriptorAsync("Second", UnauthorizedNamespace);
        var third = await SeedDescriptorAsync("Third", SecondAuthorizedNamespace);
        await SeedDescriptorAsync("Fourth", UnauthorizedNamespace);
        var fifth = await SeedDescriptorAsync("Fifth", AuthorizedNamespace);
        _context.ResetRecorder();

        var pagedResult = await QueryAsync(limit: 2, offset: 1);

        var pagedSuccess = pagedResult.Should().BeOfType<QueryResult.QuerySuccess>().Subject;
        AssertReturnedDocuments(pagedSuccess, third, fifth);
        pagedSuccess.TotalCount.Should().Be(3, "totalCount must count only namespace-authorized descriptors");

        var unpagedResult = await QueryAsync();

        AssertReturnedDocuments(
            unpagedResult.Should().BeOfType<QueryResult.QuerySuccess>().Subject,
            first,
            third,
            fifth
        );
    }

    // ── POST create ─────────────────────────────────────────────────────

    [Test]
    public async Task It_authorizes_descriptor_post_create_and_inserts_document_and_descriptor_rows()
    {
        var documentUuid = new DocumentUuid(Guid.Parse("c1c1c1c1-0000-0000-0000-000000000001"));

        var result = await UpsertAsync(
            CreateDescriptorBody("CreateAuthorized", AuthorizedNamespace),
            documentUuid
        );

        result.Should().BeOfType<UpsertResult.InsertSuccess>();
        var state = await ReadStateAsync(documentUuid);
        state.Descriptor["Namespace"].Should().Be(AuthorizedNamespace);
        state.Descriptor["CodeValue"].Should().Be("CreateAuthorized");

        // Pins the stored uri to the value the descriptor-absence probes key on, so those probes cannot pass
        // by querying a uri the production write path never stores.
        state.Descriptor["Uri"].Should().Be(DescriptorUri("CreateAuthorized", AuthorizedNamespace));
    }

    [Test]
    public async Task It_denies_descriptor_post_create_with_a_mismatching_proposed_namespace_and_writes_nothing()
    {
        var documentUuid = new DocumentUuid(Guid.Parse("c1c1c1c1-0000-0000-0000-000000000002"));

        var result = await UpsertAsync(
            CreateDescriptorBody("CreateDenied", UnauthorizedNamespace),
            documentUuid
        );

        AssertNamespaceDenied(
            result
                .Should()
                .BeOfType<UpsertResult.UpsertFailureNamespaceNotAuthorized>()
                .Subject.NamespaceFailure,
            NamespaceAuthorizationFailureValueSource.Proposed
        );
        await AssertNoDescriptorRowsExistAsync(
            documentUuid,
            DescriptorUri("CreateDenied", UnauthorizedNamespace)
        );
        _context.AssertNoPersistenceAfterNamespaceAuthorization();
    }

    // ── PUT ─────────────────────────────────────────────────────────────

    [Test]
    public async Task It_authorizes_descriptor_put_and_updates_the_descriptor_row()
    {
        var documentUuid = await SeedDescriptorAsync("PutAuthorized", AuthorizedNamespace);
        var before = await ReadStateAsync(documentUuid);
        _context.ResetRecorder();

        var result = await UpdateAsync(
            CreateDescriptorBody("PutAuthorized", AuthorizedNamespace, "Updated short description"),
            documentUuid
        );

        result.Should().BeOfType<UpdateResult.UpdateSuccess>();
        var after = await ReadStateAsync(documentUuid);
        after.Descriptor["ShortDescription"].Should().Be("Updated short description");
        Convert
            .ToInt64(after.Document["ContentVersion"])
            .Should()
            .BeGreaterThan(Convert.ToInt64(before.Document["ContentVersion"]));
    }

    [TestCase(
        false,
        NamespaceAuthorizationFailureValueSource.Stored,
        TestName = "It_denies_descriptor_put_on_an_unauthorized_stored_namespace_even_when_the_proposed_namespace_matches"
    )]
    [TestCase(
        true,
        NamespaceAuthorizationFailureValueSource.Proposed,
        TestName = "It_denies_descriptor_put_with_a_mismatching_proposed_namespace_after_stored_authorization_passes"
    )]
    public async Task It_denies_descriptor_put_and_leaves_document_and_descriptor_rows_unchanged(
        bool storedAuthorized,
        NamespaceAuthorizationFailureValueSource expectedValueSource
    )
    {
        // Each case pairs one authorized side with one unauthorized side, so a proposed failure is only
        // reachable once the stored check passed — which is what establishes the stored-then-proposed order.
        const string CodeValue = "PutDenied";
        var storedNamespace = storedAuthorized ? AuthorizedNamespace : UnauthorizedNamespace;
        var proposedNamespace = storedAuthorized ? UnauthorizedNamespace : AuthorizedNamespace;
        var documentUuid = await SeedDescriptorAsync(CodeValue, storedNamespace);
        var before = await ReadStateAsync(documentUuid);
        _context.ResetRecorder();

        var result = await UpdateAsync(
            CreateDescriptorBody(CodeValue, proposedNamespace, "Denied short description"),
            documentUuid
        );

        AssertNamespaceDenied(
            result
                .Should()
                .BeOfType<UpdateResult.UpdateFailureNamespaceNotAuthorized>()
                .Subject.NamespaceFailure,
            expectedValueSource
        );
        await AssertStateUnchangedAsync(documentUuid, before);
    }

    // ── POST-as-update ──────────────────────────────────────────────────

    [Test]
    public async Task It_authorizes_descriptor_post_as_update_and_updates_the_existing_descriptor()
    {
        const string CodeValue = "UpsertAuthorized";
        var existingUuid = await SeedDescriptorAsync(CodeValue, AuthorizedNamespace);
        var candidateUuid = new DocumentUuid(Guid.Parse("c2c2c2c2-0000-0000-0000-000000000001"));
        var before = await ReadStateAsync(existingUuid);
        _context.ResetRecorder();

        var result = await UpsertAsync(
            CreateDescriptorBody(CodeValue, AuthorizedNamespace, "Upserted short description"),
            candidateUuid
        );

        var success = result.Should().BeOfType<UpsertResult.UpdateSuccess>().Subject;
        success.ExistingDocumentUuid.Should().Be(existingUuid);
        var after = await ReadStateAsync(existingUuid);
        after.Descriptor["ShortDescription"].Should().Be("Upserted short description");
        Convert
            .ToInt64(after.Document["ContentVersion"])
            .Should()
            .BeGreaterThan(Convert.ToInt64(before.Document["ContentVersion"]));
        (await _context.CountDocumentRowsAsync(candidateUuid))
            .Should()
            .Be(0, "the candidate uuid must not create a second descriptor document");
    }

    [Test]
    public async Task It_denies_descriptor_post_as_update_on_an_unauthorized_stored_namespace_and_leaves_rows_unchanged()
    {
        // A descriptor's identity is its uri — namespace#codeValue — so a POST carrying a different namespace
        // addresses a different descriptor and takes the create path, whose proposed-value denial is covered
        // above. An upsert that resolves to an existing target therefore always carries the stored namespace,
        // which makes attributing this denial to the stored value the ordering proof: had the proposed check
        // run first, the identical proposed value would have reported Proposed instead.
        const string CodeValue = "UpsertDenied";
        var existingUuid = await SeedDescriptorAsync(CodeValue, UnauthorizedNamespace);
        var candidateUuid = new DocumentUuid(Guid.Parse("c2c2c2c2-0000-0000-0000-000000000002"));
        var before = await ReadStateAsync(existingUuid);
        _context.ResetRecorder();

        var result = await UpsertAsync(
            CreateDescriptorBody(CodeValue, UnauthorizedNamespace, "Denied short description"),
            candidateUuid
        );

        AssertNamespaceDenied(
            result
                .Should()
                .BeOfType<UpsertResult.UpsertFailureNamespaceNotAuthorized>()
                .Subject.NamespaceFailure,
            NamespaceAuthorizationFailureValueSource.Stored
        );
        await AssertStateUnchangedAsync(existingUuid, before);
        (await _context.CountDocumentRowsAsync(candidateUuid)).Should().Be(0);
    }

    // ── DELETE ──────────────────────────────────────────────────────────

    [Test]
    public async Task It_authorizes_descriptor_delete_and_removes_document_and_descriptor_rows()
    {
        var documentUuid = await SeedDescriptorAsync("DeleteAuthorized", AuthorizedNamespace);
        var documentId = await GetDocumentIdAsync(documentUuid);
        _context.ResetRecorder();

        var result = await DeleteAsync(documentUuid);

        result.Should().BeOfType<DeleteResult.DeleteSuccess>();
        await AssertNoDescriptorRowsExistAsync(
            documentUuid,
            DescriptorUri("DeleteAuthorized", AuthorizedNamespace),
            documentId
        );
    }

    [Test]
    public async Task It_returns_403_before_if_match_and_preserves_the_descriptor_when_both_would_fail()
    {
        var documentUuid = await SeedDescriptorAsync("DeleteCollision", UnauthorizedNamespace);
        var before = await ReadStateAsync(documentUuid);
        _context.ResetRecorder();

        var result = await DeleteAsync(documentUuid, ifMatch: StaleETag);

        AssertNamespaceDenied(
            result
                .Should()
                .BeOfType<DeleteResult.DeleteFailureNamespaceNotAuthorized>()
                .Subject.NamespaceFailure,
            NamespaceAuthorizationFailureValueSource.Stored
        );
        await AssertStateUnchangedAsync(documentUuid, before);
    }

    [Test]
    public async Task It_returns_412_for_a_stale_descriptor_delete_if_match_once_namespace_authorization_passes()
    {
        var documentUuid = await SeedDescriptorAsync("DeletePrecondition", AuthorizedNamespace);
        var before = await ReadStateAsync(documentUuid);
        _context.ResetRecorder();

        var result = await DeleteAsync(documentUuid, ifMatch: StaleETag);

        result
            .Should()
            .BeOfType<DeleteResult.DeleteFailureETagMisMatch>(
                "the precondition result must survive unchanged once authorization succeeds"
            );
        await AssertStateUnchangedAsync(documentUuid, before);
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    /// <summary>The descriptor's stored uri, which keys <c>dms.Descriptor</c> independently of the document.</summary>
    private static string DescriptorUri(string codeValue, string @namespace) => $"{@namespace}#{codeValue}";

    private static JsonNode CreateDescriptorBody(
        string codeValue,
        string @namespace,
        string? shortDescription = null
    ) =>
        new JsonObject
        {
            ["namespace"] = @namespace,
            ["codeValue"] = codeValue,
            ["shortDescription"] = shortDescription ?? codeValue,
        };

    /// <summary>
    /// Seeds an existing descriptor through the production POST path with no strategies configured, so any
    /// stored namespace — authorized or not — is established without first passing namespace authorization.
    /// </summary>
    private async Task<DocumentUuid> SeedDescriptorAsync(string codeValue, string @namespace)
    {
        var documentUuid = new DocumentUuid(Guid.NewGuid());

        var result = await _context.UpsertDescriptorAsync(
            ProjectEndpointName,
            ResourceName,
            CreateDescriptorBody(codeValue, @namespace),
            documentUuid,
            [],
            []
        );

        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(result);

        return documentUuid;
    }

    private async Task<UpsertResult> UpsertAsync(
        JsonNode requestBody,
        DocumentUuid documentUuid,
        string? ifMatch = null
    ) =>
        await _context.UpsertDescriptorAsync(
            ProjectEndpointName,
            ResourceName,
            requestBody,
            documentUuid,
            [],
            _namespaceStrategy,
            _configuredPrefixes,
            ifMatch
        );

    private async Task<UpdateResult> UpdateAsync(
        JsonNode requestBody,
        DocumentUuid documentUuid,
        string? ifMatch = null
    ) =>
        await _context.UpdateDescriptorByIdAsync(
            ProjectEndpointName,
            ResourceName,
            requestBody,
            documentUuid,
            [],
            _namespaceStrategy,
            _configuredPrefixes,
            ifMatch
        );

    private async Task<GetResult> GetByIdAsync(DocumentUuid documentUuid) =>
        await _context.GetByIdAsync(
            ProjectEndpointName,
            ResourceName,
            documentUuid,
            [],
            _namespaceStrategy,
            namespacePrefixes: _configuredPrefixes
        );

    private async Task<QueryResult> QueryAsync(int? limit = null, int? offset = null) =>
        await _context.QueryAsync(
            ProjectEndpointName,
            ResourceName,
            [],
            _namespaceStrategy,
            limit: limit,
            offset: offset,
            namespacePrefixes: _configuredPrefixes
        );

    private async Task<DeleteResult> DeleteAsync(DocumentUuid documentUuid, string? ifMatch = null) =>
        await _context.DeleteByIdAsync(
            ProjectEndpointName,
            ResourceName,
            documentUuid,
            [],
            _namespaceStrategy,
            ifMatch,
            namespacePrefixes: _configuredPrefixes
        );

    /// <summary>
    /// Snapshots every column of both authoritative rows. The projection is <c>SELECT *</c> rather than a
    /// column list so the comparison covers the descriptor's own tracking state — <c>ResourceKeyId</c>,
    /// <c>ContentVersion</c>, and <c>ContentLastModifiedAt</c> — and cannot silently narrow when a column is
    /// added to either table.
    /// </summary>
    private async Task<DescriptorAuthorizationState> ReadStateAsync(DocumentUuid documentUuid)
    {
        var documentId = await GetDocumentIdAsync(documentUuid);
        var state = new DescriptorAuthorizationState(
            await ReadFullRowAsync("Document", documentId),
            await ReadFullRowAsync("Descriptor", documentId)
        );

        // Guard the snapshot's completeness so a future narrowing of the projection fails here rather than
        // quietly weakening every unchanged-state assertion below.
        state
            .Descriptor.Keys.Should()
            .Contain(
                _requiredDescriptorTrackingColumns,
                "the descriptor snapshot must include the descriptor's own tracking columns"
            );

        return state;
    }

    /// <summary>
    /// Both authoritative descriptor rows — the <c>dms.Document</c> stamps and the complete
    /// <c>dms.Descriptor</c> row including its tracking columns — are exactly what they were, and the write
    /// session issued no persistence after authorization.
    /// </summary>
    private async Task AssertStateUnchangedAsync(
        DocumentUuid documentUuid,
        DescriptorAuthorizationState before
    )
    {
        var after = await ReadStateAsync(documentUuid);

        after.Should().BeEquivalentTo(before);
        _context.AssertNoPersistenceAfterNamespaceAuthorization();
    }

    /// <summary>
    /// Proves no descriptor row survives, keyed independently of <c>dms.Document</c>. Joining through the
    /// document would be vacuous once the document itself is gone, so the descriptor table is probed directly
    /// by its uri and — when the row previously existed — by the captured <c>DocumentId</c>.
    /// </summary>
    private async Task AssertNoDescriptorRowsExistAsync(
        DocumentUuid documentUuid,
        string descriptorUri,
        long? previousDocumentId = null
    )
    {
        (await _context.CountDocumentRowsAsync(documentUuid)).Should().Be(0);

        (
            await _context.Database.ExecuteScalarAsync<long>(
                """
                SELECT COUNT_BIG(*) FROM [dms].[Descriptor] WHERE [Uri] = @uri;
                """,
                new SqlParameter("@uri", descriptorUri)
            )
        ).Should().Be(0, "no dms.Descriptor row may remain for the descriptor uri");

        if (previousDocumentId is not { } documentId)
        {
            return;
        }

        (
            await _context.Database.ExecuteScalarAsync<long>(
                """
                SELECT COUNT_BIG(*) FROM [dms].[Descriptor] WHERE [DocumentId] = @documentId;
                """,
                new SqlParameter("@documentId", documentId)
            )
        ).Should().Be(0, "no dms.Descriptor row may remain for the deleted document id");
    }

    private async Task<IReadOnlyDictionary<string, object?>> ReadFullRowAsync(
        string tableName,
        long documentId
    )
    {
        var rows = await _context.Database.QueryRowsAsync(
            $"""
            SELECT * FROM [dms].[{tableName}] WHERE [DocumentId] = @documentId;
            """,
            new SqlParameter("@documentId", documentId)
        );

        return rows.Count == 1
            ? rows[0]
            : throw new InvalidOperationException(
                $"Expected exactly one dms.{tableName} row for DocumentId {documentId}, but found {rows.Count}."
            );
    }

    private async Task<long> GetDocumentIdAsync(DocumentUuid documentUuid) =>
        await _context.Database.ExecuteScalarAsync<long>(
            """
            SELECT [DocumentId] FROM [dms].[Document] WHERE [DocumentUuid] = @documentUuid;
            """,
            new SqlParameter("@documentUuid", documentUuid.Value)
        );

    private static void AssertReturnedDocuments(
        QueryResult.QuerySuccess success,
        params DocumentUuid[] expectedDocumentUuids
    ) =>
        success
            .EdfiDocs.Select(static document => document!["id"]!.GetValue<string>())
            .Should()
            .Equal(expectedDocumentUuids.Select(static documentUuid => documentUuid.Value.ToString()));

    private static void AssertNamespaceDenied(
        NamespaceAuthorizationFailure failure,
        NamespaceAuthorizationFailureValueSource expectedValueSource
    )
    {
        failure.FailureKind.Should().Be(NamespaceAuthorizationFailureKind.NamespaceMismatch);
        failure.ValueSource.Should().Be(expectedValueSource);
        failure.StrategyName.Should().Be(RelationshipAuthorizationCrudTestSupport.NamespaceBased);
        failure.ConfiguredNamespacePrefixes.Should().Equal(_configuredPrefixes);
    }

    private sealed record DescriptorAuthorizationState(
        IReadOnlyDictionary<string, object?> Document,
        IReadOnlyDictionary<string, object?> Descriptor
    );
}

// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Tests.Integration.Fixtures;
using EdFi.DataManagementService.Tests.Integration.Postgresql;
using EdFi.DataManagementService.Tests.Integration.Scenarios;

namespace EdFi.DataManagementService.Tests.Integration.Tests.Postgresql;

/// <summary>
/// PostgreSQL end-to-end proof that a cursor walk bounded by a max-bearing change-version window is
/// anchored on <c>ContentVersion</c> and still delivers the window exactly once. The bounds, the
/// keyset, and the anchor carried out of selection are all compiled per provider, so the walk is
/// observed on both engines.
/// </summary>
/// <remarks>
/// Leases the descriptor runtime fixture because its ApiSchema declares both the regular resource and
/// the descriptor these walks page over; the walks seed the documents they assert on themselves.
/// </remarks>
public sealed class Given_Postgresql_WindowedCursorAnchoring : PostgresqlApiIntegrationTestBase
{
    protected override FixtureKey Fixture => FixtureKey.DescriptorRuntime;

    [Test]
    public Task It_walks_a_windowed_regular_resource_collection_exactly_once() =>
        WindowedCursorAnchoringScenario.It_walks_a_windowed_regular_resource_collection_exactly_once(Harness);

    [Test]
    public Task It_walks_a_windowed_descriptor_collection_exactly_once() =>
        WindowedCursorAnchoringScenario.It_walks_a_windowed_descriptor_collection_exactly_once(Harness);

    [Test]
    public Task It_drops_a_document_updated_past_the_window_maximum_mid_walk() =>
        WindowedCursorAnchoringScenario.It_drops_a_document_updated_past_the_window_maximum_mid_walk(Harness);

    [Test]
    public Task It_continues_past_a_page_whose_documents_were_deleted_mid_walk() =>
        WindowedCursorAnchoringScenario.It_continues_past_a_page_whose_documents_were_deleted_mid_walk(
            Harness
        );

    [Test]
    public Task It_keeps_the_document_id_anchor_for_a_min_only_walk() =>
        WindowedCursorAnchoringScenario.It_keeps_the_document_id_anchor_for_a_min_only_walk(Harness);

    [Test]
    public Task It_rejects_a_windowed_partition_token_replayed_without_the_window() =>
        WindowedCursorAnchoringScenario.It_rejects_a_windowed_partition_token_replayed_without_the_window(
            Harness
        );
}

/// <summary>
/// The same windowed walk read through the production authorization path, so the anchor is proven to
/// change which column the walk is bounded on without changing which rows it may see.
/// </summary>
[Category("Authorization")]
public sealed class Given_Postgresql_WindowedCursorAnchoring_NamespaceAuthorization
    : PostgresqlApiIntegrationTestBase
{
    protected override FixtureKey Fixture => FixtureKey.AuthorizationQuery;

    protected override bool BypassAuthorization => false;

    protected override IReadOnlyList<string> ClientNamespacePrefixes =>
        [CursorPartitionAuthorizationMatrixSupport.AuthorizedNamespacePrefix];

    protected override IClaimSetProvider CreateClaimSetProvider(FixtureContext fixture) =>
        CursorPartitionAuthorizationMatrixSupport.CreateNamespaceReadClaimSetProvider(fixture);

    [Test]
    public Task It_excludes_unauthorized_documents_from_a_windowed_walk() =>
        WindowedCursorAnchoringScenario.It_excludes_unauthorized_documents_from_a_windowed_walk(Harness);
}

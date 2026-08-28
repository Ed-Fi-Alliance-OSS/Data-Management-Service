// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Telemetry;
using EdFi.DataManagementService.Tests.Integration.Fixtures;
using EdFi.DataManagementService.Tests.Integration.Mssql;
using EdFi.DataManagementService.Tests.Integration.Scenarios;

namespace EdFi.DataManagementService.Tests.Integration.Tests.Mssql;

/// <summary>
/// SQL Server twin of the cursor walk proof. A walk only ever follows the continuation it was handed, and
/// both the page selection and the keyset that anchors that continuation come from real SQL Server query
/// execution, so the answer is provider-specific and is observed on both engines.
/// </summary>
/// <remarks>
/// Leases the descriptor runtime fixture because its ApiSchema declares both the regular resource and the
/// descriptor these walks page over; the walks seed the documents they assert on themselves.
/// </remarks>
public sealed class Given_Mssql_CursorPagingExecution : MssqlApiIntegrationTestBase
{
    protected override FixtureKey Fixture => FixtureKey.DescriptorRuntime;

    /// <summary>
    /// Counts the database commands a read really issued, which is what the telemetry case below asserts
    /// against the design's per-operation literal.
    /// </summary>
    protected override bool CaptureQueryPlans => true;

    [Test]
    public Task It_walks_a_regular_resource_collection_by_cursor() =>
        CursorPagingExecutionScenario.It_walks_a_regular_resource_collection_by_cursor(Harness);

    [Test]
    public Task It_walks_a_descriptor_collection_by_cursor() =>
        CursorPagingExecutionScenario.It_walks_a_descriptor_collection_by_cursor(Harness);

    [Test]
    public Task It_enters_a_cursor_walk_from_a_traditional_page() =>
        CursorPagingExecutionScenario.It_enters_a_cursor_walk_from_a_traditional_page(Harness);

    [Test]
    public Task It_continues_a_windowed_traditional_page_on_its_content_version_anchor() =>
        CursorPagingExecutionScenario.It_continues_a_windowed_traditional_page_on_its_content_version_anchor(
            Harness
        );

    [Test]
    public Task It_emits_bounded_telemetry_across_a_cursor_walk() =>
        CursorPagingExecutionScenario.It_emits_bounded_telemetry_across_a_cursor_walk(
            Harness,
            CollectionPagingTelemetryLabel.SqlServerProvider
        );

    [Test]
    public Task It_records_an_early_empty_without_a_database_command() =>
        CursorPagingExecutionScenario.It_records_an_early_empty_without_a_database_command(
            Harness,
            CollectionPagingTelemetryLabel.SqlServerProvider
        );

    [Test]
    public Task It_records_a_validation_rejection_without_reaching_the_backend() =>
        CursorPagingExecutionScenario.It_records_a_validation_rejection_without_reaching_the_backend(
            Harness,
            CollectionPagingTelemetryLabel.SqlServerProvider
        );
}

// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using FluentAssertions;
using NUnit.Framework;
using static EdFi.DataManagementService.Backend.Plans.Tests.Unit.HydrationBatchBuilderTestHelper;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

[TestFixture]
public class Given_HydrationReader_With_Document_Reference_Lookup_Result_Sets
{
    [Test]
    public async Task It_reads_lookup_rows_using_the_compiled_result_shape()
    {
        var lookupPlan = BuildLookupPlan();

        using var reader = HydrationDescriptorResultTestHelper.CreateReader(
            CreateDocumentReferenceLookupTable(
                (101L, Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"), (short)7),
                (202L, Guid.Parse("cccccccc-4444-5555-6666-dddddddddddd"), (short)11)
            )
        );

        var result = await HydrationReader.ReadDocumentReferenceLookupRowsAsync(
            reader,
            lookupPlan,
            CancellationToken.None
        );

        result
            .Rows.Should()
            .Equal(
                new DocumentReferenceLookupRow(
                    DocumentId: 101L,
                    DocumentUuid: Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"),
                    ResourceKeyId: 7
                ),
                new DocumentReferenceLookupRow(
                    DocumentId: 202L,
                    DocumentUuid: Guid.Parse("cccccccc-4444-5555-6666-dddddddddddd"),
                    ResourceKeyId: 11
                )
            );
    }

    [Test]
    public async Task It_rejects_lookup_result_sets_with_an_unexpected_column_count()
    {
        var lookupPlan = BuildLookupPlan();
        var incomplete = new DataTable();
        incomplete.Columns.Add("DocumentId", typeof(long));
        incomplete.Columns.Add("DocumentUuid", typeof(Guid));
        incomplete.Rows.Add(1L, Guid.Empty);

        using var reader = HydrationDescriptorResultTestHelper.CreateReader(incomplete);

        var act = () =>
            HydrationReader.ReadDocumentReferenceLookupRowsAsync(reader, lookupPlan, CancellationToken.None);

        var exception = await act.Should().ThrowAsync<InvalidOperationException>();
        exception
            .Which.Message.Should()
            .Be("Document-reference lookup result set has 2 columns but expected 3.");
    }

    internal static DocumentReferenceLookupPlan BuildLookupPlan() =>
        new(
            SelectByKeysetSql: "SELECT lookup rows FROM lookup;",
            ResultShape: new DocumentReferenceLookupResultShape(
                DocumentIdOrdinal: 0,
                DocumentUuidOrdinal: 1,
                ResourceKeyIdOrdinal: 2
            ),
            SourcesInOrder:
            [
                new DocumentReferenceLookupSource(
                    Table: new DbTableName(new DbSchemaName("edfi"), "School"),
                    FkColumn: new DbColumnName("School_DocumentId")
                ),
            ]
        );

    internal static DataTable CreateDocumentReferenceLookupTable(
        params (long DocumentId, Guid DocumentUuid, short ResourceKeyId)[] rows
    )
    {
        var table = new DataTable();
        table.Columns.Add("DocumentId", typeof(long));
        table.Columns.Add("DocumentUuid", typeof(Guid));
        table.Columns.Add("ResourceKeyId", typeof(short));

        foreach (var row in rows)
        {
            table.Rows.Add(row.DocumentId, row.DocumentUuid, row.ResourceKeyId);
        }

        return table;
    }
}

[TestFixture]
public class Given_HydrationBatchBuilder_With_Document_Reference_Lookup
{
    [Test]
    public void It_appends_lookup_sql_after_descriptor_projections_when_plan_property_is_populated()
    {
        var descriptorPlans = new[]
        {
            new DescriptorProjectionPlan(
                SelectByKeysetSql: "SELECT descriptor rows FROM root_descriptor;",
                ResultShape: new DescriptorProjectionResultShape(DescriptorIdOrdinal: 0, UriOrdinal: 1),
                SourcesInOrder: []
            ),
        };
        var lookupPlan = new DocumentReferenceLookupPlan(
            SelectByKeysetSql: "SELECT lookup rows FROM lookup;",
            ResultShape: new DocumentReferenceLookupResultShape(0, 1, 2),
            SourcesInOrder:
            [
                new DocumentReferenceLookupSource(
                    Table: new DbTableName(new DbSchemaName("edfi"), "School"),
                    FkColumn: new DbColumnName("School_DocumentId")
                ),
            ]
        );

        var batch = HydrationBatchBuilder.Build(
            BuildTestReadPlan(SqlDialect.Pgsql, descriptorPlans, lookupPlan),
            new PageKeysetSpec.Single(42L),
            SqlDialect.Pgsql
        );

        var descriptorIndex = batch.IndexOf(
            "SELECT descriptor rows FROM root_descriptor;",
            StringComparison.Ordinal
        );
        var lookupIndex = batch.IndexOf("SELECT lookup rows FROM lookup;", StringComparison.Ordinal);

        descriptorIndex.Should().BePositive();
        lookupIndex.Should().BeGreaterThan(descriptorIndex);
    }

    [Test]
    public void It_does_not_emit_lookup_sql_when_plan_property_is_null()
    {
        var batch = HydrationBatchBuilder.Build(
            BuildTestReadPlan(SqlDialect.Pgsql),
            new PageKeysetSpec.Single(42L),
            SqlDialect.Pgsql
        );

        batch.Should().NotContain("SELECT lookup rows FROM lookup;");
    }

    [Test]
    public void It_appends_lookup_sql_even_when_descriptor_projection_is_disabled()
    {
        var descriptorPlans = new[]
        {
            new DescriptorProjectionPlan(
                SelectByKeysetSql: "SELECT descriptor rows FROM root_descriptor;",
                ResultShape: new DescriptorProjectionResultShape(DescriptorIdOrdinal: 0, UriOrdinal: 1),
                SourcesInOrder: []
            ),
        };
        var lookupPlan = new DocumentReferenceLookupPlan(
            SelectByKeysetSql: "SELECT lookup rows FROM lookup;",
            ResultShape: new DocumentReferenceLookupResultShape(0, 1, 2),
            SourcesInOrder:
            [
                new DocumentReferenceLookupSource(
                    Table: new DbTableName(new DbSchemaName("edfi"), "School"),
                    FkColumn: new DbColumnName("School_DocumentId")
                ),
            ]
        );

        var batch = HydrationBatchBuilder.Build(
            BuildTestReadPlan(SqlDialect.Pgsql, descriptorPlans, lookupPlan),
            new PageKeysetSpec.Single(42L),
            SqlDialect.Pgsql,
            new HydrationExecutionOptions(IncludeDescriptorProjection: false)
        );

        batch.Should().NotContain("SELECT descriptor rows FROM root_descriptor;");
        batch.Should().Contain("SELECT lookup rows FROM lookup;");
    }
}

[TestFixture]
public class Given_HydrationExecutor_With_Document_Reference_Lookup
{
    [Test]
    public async Task It_surfaces_lookup_rows_on_the_hydrated_page_when_plan_carries_a_lookup()
    {
        var lookupPlan = Given_HydrationReader_With_Document_Reference_Lookup_Result_Sets.BuildLookupPlan();

        var command = new RecordingDbCommand(
            HydrationDescriptorResultTestHelper.CreateReader(
                CreateDocumentMetadataTable(
                    (
                        42L,
                        Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"),
                        44L,
                        45L,
                        new DateTimeOffset(2026, 4, 2, 12, 0, 0, TimeSpan.Zero),
                        new DateTimeOffset(2026, 4, 2, 12, 1, 0, TimeSpan.Zero)
                    )
                ),
                CreateRootTableRows((42L, 255901)),
                CreateChildTableRows((100L, 42L, 0, "Springfield")),
                Given_HydrationReader_With_Document_Reference_Lookup_Result_Sets.CreateDocumentReferenceLookupTable(
                    (101L, Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"), (short)7),
                    (202L, Guid.Parse("cccccccc-4444-5555-6666-dddddddddddd"), (short)11)
                )
            )
        );

        var connection = new RecordingDbConnection(command);

        var result = await HydrationExecutor.ExecuteAsync(
            connection,
            BuildTestReadPlan(SqlDialect.Pgsql, descriptorProjectionPlans: null, lookupPlan),
            new PageKeysetSpec.Single(42L),
            SqlDialect.Pgsql,
            CancellationToken.None
        );

        result.DocumentReferenceLookup.Should().NotBeNull();
        result
            .DocumentReferenceLookup!.Rows.Should()
            .Equal(
                new DocumentReferenceLookupRow(
                    DocumentId: 101L,
                    DocumentUuid: Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"),
                    ResourceKeyId: 7
                ),
                new DocumentReferenceLookupRow(
                    DocumentId: 202L,
                    DocumentUuid: Guid.Parse("cccccccc-4444-5555-6666-dddddddddddd"),
                    ResourceKeyId: 11
                )
            );
    }

    [Test]
    public async Task It_leaves_lookup_null_when_execution_option_opts_out_even_if_plan_has_lookup()
    {
        // Write-path callers (current-state load, committed readback) pass
        // IncludeDocumentReferenceLookup: false. The batch builder omits the lookup SQL, and
        // the executor must not try to advance past a result set that was never emitted.
        var lookupPlan = Given_HydrationReader_With_Document_Reference_Lookup_Result_Sets.BuildLookupPlan();

        var command = new RecordingDbCommand(
            HydrationDescriptorResultTestHelper.CreateReader(
                CreateDocumentMetadataTable(
                    (
                        42L,
                        Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"),
                        44L,
                        45L,
                        new DateTimeOffset(2026, 4, 2, 12, 0, 0, TimeSpan.Zero),
                        new DateTimeOffset(2026, 4, 2, 12, 1, 0, TimeSpan.Zero)
                    )
                ),
                CreateRootTableRows((42L, 255901)),
                CreateChildTableRows((100L, 42L, 0, "Springfield"))
            )
        );

        var connection = new RecordingDbConnection(command);

        var result = await HydrationExecutor.ExecuteAsync(
            connection,
            BuildTestReadPlan(SqlDialect.Pgsql, descriptorProjectionPlans: null, lookupPlan),
            new PageKeysetSpec.Single(42L),
            SqlDialect.Pgsql,
            new HydrationExecutionOptions(
                IncludeDescriptorProjection: true,
                IncludeDocumentReferenceLookup: false
            ),
            CancellationToken.None
        );

        result.DocumentReferenceLookup.Should().BeNull();
    }

    [Test]
    public async Task It_leaves_lookup_null_when_plan_property_is_null()
    {
        var command = new RecordingDbCommand(
            HydrationDescriptorResultTestHelper.CreateReader(
                CreateDocumentMetadataTable(
                    (
                        42L,
                        Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"),
                        44L,
                        45L,
                        new DateTimeOffset(2026, 4, 2, 12, 0, 0, TimeSpan.Zero),
                        new DateTimeOffset(2026, 4, 2, 12, 1, 0, TimeSpan.Zero)
                    )
                ),
                CreateRootTableRows((42L, 255901)),
                CreateChildTableRows((100L, 42L, 0, "Springfield"))
            )
        );

        var connection = new RecordingDbConnection(command);

        var result = await HydrationExecutor.ExecuteAsync(
            connection,
            BuildTestReadPlan(SqlDialect.Pgsql),
            new PageKeysetSpec.Single(42L),
            SqlDialect.Pgsql,
            CancellationToken.None
        );

        result.DocumentReferenceLookup.Should().BeNull();
    }

    private static DataTable CreateDocumentMetadataTable(
        params (
            long DocumentId,
            Guid DocumentUuid,
            long ContentVersion,
            long IdentityVersion,
            DateTimeOffset ContentLastModifiedAt,
            DateTimeOffset IdentityLastModifiedAt
        )[] rows
    )
    {
        var table = new DataTable();
        table.Columns.Add("DocumentId", typeof(long));
        table.Columns.Add("DocumentUuid", typeof(Guid));
        table.Columns.Add("ContentVersion", typeof(long));
        table.Columns.Add("IdentityVersion", typeof(long));
        table.Columns.Add("ContentLastModifiedAt", typeof(DateTimeOffset));
        table.Columns.Add("IdentityLastModifiedAt", typeof(DateTimeOffset));

        foreach (var row in rows)
        {
            table.Rows.Add(
                row.DocumentId,
                row.DocumentUuid,
                row.ContentVersion,
                row.IdentityVersion,
                row.ContentLastModifiedAt,
                row.IdentityLastModifiedAt
            );
        }

        return table;
    }

    private static DataTable CreateRootTableRows(params (long DocumentId, int SchoolId)[] rows)
    {
        var table = new DataTable();
        table.Columns.Add("DocumentId", typeof(long));
        table.Columns.Add("SchoolId", typeof(int));

        foreach (var row in rows)
        {
            table.Rows.Add(row.DocumentId, row.SchoolId);
        }

        return table;
    }

    private static DataTable CreateChildTableRows(
        params (long CollectionItemId, long SchoolDocumentId, int Ordinal, string City)[] rows
    )
    {
        var table = new DataTable();
        table.Columns.Add("CollectionItemId", typeof(long));
        table.Columns.Add("School_DocumentId", typeof(long));
        table.Columns.Add("Ordinal", typeof(int));
        table.Columns.Add("City", typeof(string));

        foreach (var row in rows)
        {
            table.Rows.Add(row.CollectionItemId, row.SchoolDocumentId, row.Ordinal, row.City);
        }

        return table;
    }
}

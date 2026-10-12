// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Data.Common;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using FluentAssertions;
using NUnit.Framework;
using static EdFi.DataManagementService.Backend.Plans.Tests.Unit.HydrationBatchBuilderTestHelper;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

[TestFixture(SqlDialect.Pgsql, false)]
[TestFixture(SqlDialect.Pgsql, true)]
[TestFixture(SqlDialect.Mssql, false)]
[TestFixture(SqlDialect.Mssql, true)]
public class Given_Consistent_Hydration(SqlDialect dialect, bool fastPath)
{
    private readonly List<string> _commands = [];
    private ResourceReadPlan _plan = null!;
    private HydrationExecutionOptions _options = null!;

    [SetUp]
    public void Setup()
    {
        _commands.Clear();
        _plan = BuildTestReadPlan(dialect, includeSingleDocumentSql: true);
        _options = new(
            IncludeDescriptorProjection: false,
            IncludeDocumentReferenceLookup: false,
            UseSingleDocumentFastPath: fastPath
        );
    }

    [Test]
    public async Task It_keeps_a_stable_read_in_one_batch_with_one_timestamp_select()
    {
        HydratedPage page = await ExecuteAsync(_ => Reader(10, 10));
        page.DocumentMetadata.Should().ContainSingle().Which.ContentVersion.Should().Be(10);
        _commands.Should().ContainSingle();
        _commands[0].Split("ContentLastModifiedAt").Should().HaveCount(2);
        string fence = _commands[0][_commands[0].LastIndexOf("SELECT d.", StringComparison.Ordinal)..];
        fence
            .Should()
            .Contain("ContentVersion")
            .And.NotContain("edfi")
            .And.NotContain("ContentLastModifiedAt");
        fence.Should().Contain(fastPath ? "@DocumentId" : "INNER JOIN");
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task It_discards_the_entire_changed_or_deleted_attempt(bool deleted)
    {
        long finalVersion = deleted ? 0 : 11;
        HydratedPage page = await ExecuteAsync(attempt =>
            attempt == 1 ? Reader(10, finalVersion) : Reader(11, 11)
        );
        _commands.Should().HaveCount(2);
        page.DocumentMetadata.Should().ContainSingle().Which.ContentVersion.Should().Be(11);
        page.DocumentMetadata[0].ContentLastModifiedAt.Should().Be(ModifiedAt(11));
        page.TableRowsInDependencyOrder[0].Rows.Should().ContainSingle().Which[1].Should().Be(11);
    }

    [Test]
    public async Task It_bounds_retries_without_returning_an_unstable_representation()
    {
        Func<Task> act = async () => await ExecuteAsync(attempt => Reader(attempt, attempt + 1));
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*stable representation*");
        _commands.Should().HaveCount(3);
    }

    [Test]
    public async Task It_rejects_a_missing_fence_result_instead_of_serving_unverified_content()
    {
        Func<Task> act = async () => await ExecuteAsync(_ => Reader(10, 10, includeFence: false));
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*fence result set*");
        _commands.Should().ContainSingle();
    }

    [Test]
    public async Task It_honors_cancellation_before_opening_a_command()
    {
        Func<Task> act = async () => await ExecuteAsync(_ => Reader(10, 10), new CancellationToken(true));
        await act.Should().ThrowAsync<OperationCanceledException>();
        _commands.Should().BeEmpty();
    }

    private Task<HydratedPage> ExecuteAsync(Func<int, DbDataReader> reader, CancellationToken ct = default) =>
        ConsistentHydrationExecutor.ExecuteAsync(
            sql =>
            {
                _commands.Add(sql);
                return new RecordingDbCommand(reader(_commands.Count)) { CommandText = sql };
            },
            _plan,
            new PageKeysetSpec.Single(42),
            dialect,
            _options,
            ct
        );

    private static DateTimeOffset ModifiedAt(long version) =>
        new DateTimeOffset(2001, 1, 1, 0, 0, 0, TimeSpan.Zero).AddDays(version);

    private static DbDataReader Reader(long version, long finalVersion, bool includeFence = true)
    {
        DataTable metadata = new();
        metadata.Columns.Add("DocumentId", typeof(long));
        metadata.Columns.Add("DocumentUuid", typeof(Guid));
        metadata.Columns.Add("ContentVersion", typeof(long));
        metadata.Columns.Add("ContentLastModifiedAt", typeof(DateTimeOffset));
        metadata.Columns.Add("ResourceKeyId", typeof(short));
        metadata.Rows.Add(
            42L,
            Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"),
            version,
            ModifiedAt(version),
            (short)1
        );
        DataTable root = new();
        root.Columns.Add("DocumentId", typeof(long));
        root.Columns.Add("SchoolId", typeof(int));
        root.Rows.Add(42L, (int)version);
        DataTable child = new();
        child.Columns.Add("CollectionItemId", typeof(long));
        child.Columns.Add("School_DocumentId", typeof(long));
        child.Columns.Add("Ordinal", typeof(int));
        child.Columns.Add("City", typeof(string));
        DataTable fence = new();
        fence.Columns.Add("DocumentId", typeof(long));
        fence.Columns.Add("ContentVersion", typeof(long));
        if (finalVersion != 0)
        {
            fence.Rows.Add(42L, finalVersion);
        }
        return new DataTableReader(includeFence ? [metadata, root, child, fence] : [metadata, root, child]);
    }
}

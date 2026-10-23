// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Text.Json.Nodes;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Common;

/// <summary>
/// Fresh schema tests shared by providers; verdicts for database equality remain provider-specific.
/// Each test runs on a runtime connection, separate from provisioning, and rolls back its changes.
/// </summary>
public abstract class CompactDescriptorSchemaTests
{
    protected abstract string Dialect { get; }
    protected abstract DbConnection CreateConnection();
    protected abstract Task<IAsyncDisposable> ProvisionAsync();

    private IAsyncDisposable _database = default!;
    private DbConnection _connection = default!;
    private DbTransaction _transaction = default!;
    private string _repositoryRoot = "";
    private string _manifestPath = "";
    private string _assertionSql = "";
    private string _templateAssertionSql = "";
    private short _resourceKeyId;
    private short _otherResourceKeyId;
    private bool IsPgsql => Dialect == "pgsql";

    private string Q(string name) => IsPgsql ? $"\"{name}\"" : $"[{name}]";

    private string Table(string schema, string name) => $"{Q(schema)}.{Q(name)}";

    private string Descriptor => Table("dms", "Descriptor");
    private string Document => Table("dms", "Document");

    [OneTimeSetUp]
    public async Task OneTimeSetup()
    {
        _repositoryRoot = FixturePathResolver.FindRepositoryRoot(TestContext.CurrentContext.TestDirectory);
        _manifestPath = Path.Combine(
            _repositoryRoot,
            CompactDescriptorCatalogAssertions.FixtureRelativePath,
            "expected",
            $"relational-model.{Dialect}.manifest.json"
        );
        _assertionSql = await CompactDescriptorCatalogAssertions.RenderAsync(
            _repositoryRoot,
            Dialect,
            _manifestPath
        );
        _templateAssertionSql = await CompactDescriptorCatalogAssertions.RenderTemplateSchemaAsync(
            _repositoryRoot,
            Dialect,
            _manifestPath
        );
        _database = await ProvisionAsync();
    }

    [SetUp]
    public async Task Setup()
    {
        _connection = CreateConnection();
        await _connection.OpenAsync();
        _transaction = await _connection.BeginTransactionAsync();
        var keys = await RowsAsync(
            $"SELECT {Q("ResourceKeyId")} FROM {Table("dms", "ResourceKey")} WHERE {Q("ResourceName")} = 'SchoolTypeDescriptor' ORDER BY {Q("ProjectName")};"
        );
        _resourceKeyId = Convert.ToInt16(keys[0][0]);
        _otherResourceKeyId = Convert.ToInt16(keys[1][0]);
    }

    [TearDown]
    public async Task TearDown()
    {
        if (_transaction is not null)
        {
            await _transaction.RollbackAsync();
            await _transaction.DisposeAsync();
        }
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
        }
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_database is not null)
        {
            await _database.DisposeAsync();
        }
    }

    private DbCommand CreateCommand(string sql, params (string Name, object Value)[] values)
    {
        var command = _connection.CreateCommand();
        command.Transaction = _transaction;
        command.CommandText = sql;
        command.CommandTimeout = 60;
        foreach (var (name, value) in values)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }
        return command;
    }

    private async Task ExecuteAsync(string sql, params (string Name, object Value)[] values)
    {
        await using var command = CreateCommand(sql, values);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<List<object[]>> RowsAsync(string sql, params (string Name, object Value)[] values)
    {
        await using var command = CreateCommand(sql, values);
        await using var reader = await command.ExecuteReaderAsync();
        List<object[]> rows = [];
        while (await reader.ReadAsync())
        {
            object[] row = new object[reader.FieldCount];
            reader.GetValues(row);
            rows.Add(row);
        }
        return rows;
    }

    private async Task<long> InsertDocumentAsync(short resourceKeyId)
    {
        var insert = $"INSERT INTO {Document} ({Q("DocumentUuid")}, {Q("ResourceKeyId")})";
        var sql = IsPgsql
            ? $"{insert} VALUES (@uuid, @key) RETURNING {Q("DocumentId")};"
            : $"DECLARE @ids table (Id bigint); {insert} OUTPUT inserted.DocumentId INTO @ids VALUES (@uuid, @key); SELECT Id FROM @ids;";
        return Convert.ToInt64(
            (await RowsAsync(sql, ("uuid", Guid.NewGuid()), ("key", resourceKeyId))).Single()[0]
        );
    }

    private async Task<int> InsertDescriptorAsync(long documentId, short key, string ns, string code)
    {
        var insert =
            $"INSERT INTO {Descriptor} ({Q("DocumentId")}, {Q("ResourceKeyId")}, {Q("Namespace")}, {Q("CodeValue")}, {Q("ShortDescription")})";
        var sql = IsPgsql
            ? $"{insert} VALUES (@id, @key, @ns, @code, 'Description') RETURNING {Q("DescriptorId")};"
            : $"DECLARE @ids table (Id int); {insert} OUTPUT inserted.DescriptorId INTO @ids VALUES (@id, @key, @ns, @code, N'Description'); SELECT Id FROM @ids;";
        return Convert.ToInt32(
            (await RowsAsync(sql, ("id", documentId), ("key", key), ("ns", ns), ("code", code))).Single()[0]
        );
    }

    private async Task<(long DocumentId, int DescriptorId)> SeedAsync(
        string ns = "uri://Case",
        string code = "A#B",
        bool otherType = false
    )
    {
        var key = otherType ? _otherResourceKeyId : _resourceKeyId;
        var id = await InsertDocumentAsync(key);
        return (id, await InsertDescriptorAsync(id, key, ns, code));
    }

    private async Task<object[]> SnapshotAsync(long id) =>
        (
            await RowsAsync(
                $"SELECT d.{Q("DocumentId")}, s.{Q("DescriptorId")}, d.{Q("ContentVersion")}, s.{Q("ContentVersion")}, s.{Q("ContentLastModifiedAt")}, s.{Q("Namespace")}, s.{Q("CodeValue")} FROM {Document} d JOIN {Descriptor} s ON d.{Q("DocumentId")} = s.{Q("DocumentId")} WHERE d.{Q("DocumentId")} = @id;",
                ("id", id)
            )
        ).Single();

    [Test]
    public async Task It_passes_reusable_catalog_assertions_with_document_version_and_owner_timestamp()
    {
        await ExecuteAsync(_assertionSql);
        (await RowsAsync($"SELECT {Q("ContentVersion")} FROM {Document};")).Should().BeEmpty();
    }

    [Test]
    public async Task It_passes_the_combined_template_schema_gate()
    {
        await ExecuteAsync(_templateAssertionSql);
    }

    [TestCase("document_timestamp", "*forbidden column dms.Document.ContentLastModifiedAt*")]
    [TestCase("missing_document", "*missing table dms.Document*")]
    [TestCase("missing_version", "*missing/invalid stored column dms.Document.ContentVersion*")]
    [TestCase("missing_root", "*missing owner table edfi.Student*")]
    [TestCase("missing_root_timestamp", "*edfi.Student.ContentLastModifiedAt*")]
    [TestCase("missing_descriptor", "*missing owner table dms.Descriptor*")]
    [TestCase("missing_descriptor_timestamp", "*dms.Descriptor.ContentLastModifiedAt*")]
    [TestCase("nullable_root_timestamp", "*edfi.Student.ContentLastModifiedAt*")]
    public async Task It_rejects_incompatible_timestamp_catalogs_even_with_the_same_v3_fingerprint(
        string mutation,
        string diagnostic
    )
    {
        await AssertCatalogMutationRejectedAsync(
            mutation,
            "*Timestamp baseline:" + diagnostic,
            _templateAssertionSql
        );
    }

    [TestCase("missing_document_unique")]
    [TestCase("missing_fk")]
    [TestCase("wide_history")]
    public async Task It_rejects_timestamp_correct_templates_missing_compact_descriptor_features(
        string mutation
    )
    {
        await AssertCatalogMutationRejectedAsync(
            mutation,
            "*Compact descriptor baseline:*",
            _templateAssertionSql
        );
    }

    [Test]
    public async Task It_allocates_independent_compact_ids_and_preserves_keys_and_exact_stamp_ownership_on_updates()
    {
        await ExecuteAsync(
            IsPgsql
                ? $"ALTER TABLE {Document} ALTER COLUMN {Q("DocumentId")} RESTART WITH 2147484000;"
                : "DBCC CHECKIDENT ('dms.Document', RESEED, 2147484000) WITH NO_INFOMSGS;"
        );
        var (id, compactId) = await SeedAsync();
        id.Should().BeGreaterThan(int.MaxValue);
        compactId.Should().BePositive().And.BeLessThan(int.MaxValue);
        ((long)compactId).Should().NotBe(id);
        await ExecuteAsync(
            $"UPDATE {Descriptor} SET {Q("ContentLastModifiedAt")} = @past WHERE {Q("DocumentId")} = @id;",
            ("past", new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
            ("id", id)
        );
        var before = await SnapshotAsync(id);
        await ExecuteAsync(
            $"UPDATE {Descriptor} SET {Q("Namespace")} = {Q("Namespace")}, {Q("CodeValue")} = {Q("CodeValue")} WHERE {Q("DescriptorId")} = @id;",
            ("id", compactId)
        );
        (await SnapshotAsync(id)).Should().Equal(before);

        // Same whole URI, different stored components: the representation must receive one stamp.
        await ExecuteAsync(
            $"UPDATE {Descriptor} SET {Q("Namespace")} = 'uri://Case#A', {Q("CodeValue")} = 'B' WHERE {Q("DescriptorId")} = @id;",
            ("id", compactId)
        );
        var changed = await SnapshotAsync(id);
        changed[0].Should().Be(id);
        changed[1].Should().Be(compactId);
        Convert.ToInt64(changed[2]).Should().Be(Convert.ToInt64(before[2]) + 1);
        changed[3].Should().Be(changed[2]);
        Convert.ToDateTime(changed[4]).Should().BeAfter(Convert.ToDateTime(before[4]));
        changed[5].Should().Be("uri://Case#A");
        changed[6].Should().Be("B");

        await ExecuteAsync(
            $"UPDATE {Descriptor} SET {Q("Namespace")} = 'uri://case#A' WHERE {Q("DescriptorId")} = @id;",
            ("id", compactId)
        );
        var casing = await SnapshotAsync(id);
        casing[0].Should().Be(id);
        casing[1].Should().Be(compactId);
        Convert.ToInt64(casing[2]).Should().Be(Convert.ToInt64(changed[2]) + 1);
        casing[3].Should().Be(casing[2]);
        await ExecuteAsync(
            $"UPDATE {Descriptor} SET {Q("ShortDescription")} = {Q("ShortDescription")} WHERE {Q("DescriptorId")} = @id;",
            ("id", compactId)
        );
        (await SnapshotAsync(id)).Should().Equal(casing);
    }

    [Test]
    public async Task It_rejects_duplicate_whole_uri_component_pairs_within_one_type()
    {
        await SeedAsync();
        var id = await InsertDocumentAsync(_resourceKeyId);
        Func<Task> insert = async () => await InsertDescriptorAsync(id, _resourceKeyId, "uri://Case#A", "B");
        await insert.Should().ThrowAsync<DbException>();
    }

    [Test]
    public async Task It_allows_identical_whole_uri_under_same_named_types_in_different_projects()
    {
        var first = await SeedAsync();
        var second = await SeedAsync("uri://Case#A", "B", otherType: true);
        first.DescriptorId.Should().NotBe(second.DescriptorId);
        first.DocumentId.Should().NotBe(second.DocumentId);
        (
            await RowsAsync(
                $"SELECT {Q("Namespace")}, {Q("CodeValue")} FROM {Descriptor} ORDER BY {Q("DescriptorId")};"
            )
        )
            .Count.Should()
            .Be(2);
    }

    // Verdicts against a seeded ("uri://Case", "A#B") under the same type. A case variant collides on both
    // engines through the lowered whole-URI index; trailing-space equality is SQL Server's padded comparison
    // only; an internal pre-delimiter space is a different URI everywhere.
    [TestCase("uri://case", "A#B", false, true, true)]
    [TestCase("uri://Case", "A#B ", false, false, true)]
    [TestCase("uri://Case ", "A#B", true, false, false)]
    public async Task It_applies_provider_uri_collation_verdicts_and_internal_pre_delimiter_spaces(
        string ns,
        string code,
        bool alwaysDistinct,
        bool collidesOnPostgresql,
        bool collidesOnSqlServer
    )
    {
        await SeedAsync();
        await ExecuteAsync(
            IsPgsql
                ? "CREATE TEMP TABLE former_uri (uri varchar(306) NOT NULL UNIQUE); INSERT INTO former_uri VALUES ('uri://Case#A#B');"
                : "CREATE TABLE #former_uri (uri nvarchar(306) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL UNIQUE); INSERT INTO #former_uri VALUES (N'uri://Case#A#B');"
        );
        var equal = Convert.ToInt32(
            (
                await RowsAsync(
                    IsPgsql
                        ? "SELECT CASE WHEN uri = @uri THEN 1 ELSE 0 END FROM former_uri;"
                        : "SELECT CASE WHEN uri = @uri THEN 1 ELSE 0 END FROM #former_uri;",
                    ("uri", ns + "#" + code)
                )
            ).Single()[0]
        );
        equal.Should().Be(IsPgsql || alwaysDistinct ? 0 : 1);
        var id = await InsertDocumentAsync(_resourceKeyId);
        if (IsPgsql ? collidesOnPostgresql : collidesOnSqlServer)
        {
            Func<Task> insert = async () => await InsertDescriptorAsync(id, _resourceKeyId, ns, code);
            var rejection = await insert.Should().ThrowAsync<DbException>();
            if (IsPgsql)
            {
                // Only the lowered index rejects case variants on PostgreSQL.
                rejection.WithMessage("*UX_Descriptor_UriLowered_ResourceKeyId*");
            }
        }
        else
        {
            await InsertDescriptorAsync(id, _resourceKeyId, ns, code);
            (await SnapshotAsync(id))[5].Should().Be(ns);
        }
    }

    [TestCase("missing_column")]
    [TestCase("missing_fk")]
    [TestCase("missing_composite_fk")]
    [TestCase("missing_composite_unique")]
    [TestCase("missing_index")]
    [TestCase("missing_history_index")]
    [TestCase("wide_history")]
    [TestCase("old_discriminator")]
    [TestCase("persisted_uri")]
    [TestCase("old_core")]
    [TestCase("missing_document_unique")]
    [TestCase("missing_document_fk")]
    [TestCase("cascading_document_fk")]
    [TestCase("missing_lowered_uri_index")]
    [TestCase("stale_lowered_uri")]
    [TestCase("wrong_uri_expression")]
    [TestCase("history_live_fk")]
    [TestCase("unpinned_identity_collation")]
    public async Task It_rejects_missing_or_stale_physical_features_even_with_the_same_v3_fingerprint(
        string mutation
    )
    {
        await AssertCatalogMutationRejectedAsync(mutation, "*Compact descriptor baseline:*");
    }

    [Test]
    public async Task It_rejects_the_obsolete_unlowered_uri_index_even_with_the_same_v3_fingerprint()
    {
        await AssertCatalogMutationRejectedAsync(
            "obsolete_unlowered_uri_index",
            "*Compact descriptor baseline: obsolete descriptor storage/index remains*"
        );
    }

    public static IEnumerable<string> DescriptorAliasMutations(string dialect)
    {
        yield return "renamed_alias";
        yield return "missing_alias";
        yield return "wide_alias";
        yield return "stored_alias";
        if (dialect == "mssql")
        {
            yield return "nonpersisted_alias";
        }
    }

    protected Task AssertDescriptorAliasMutationAsync(string mutation) =>
        AssertCatalogMutationRejectedAsync(mutation, "*Compact descriptor baseline:*descriptor alias*");

    private Task AssertCatalogMutationRejectedAsync(string mutation, string expectedMessage) =>
        AssertCatalogMutationRejectedAsync(mutation, expectedMessage, _assertionSql);

    private async Task AssertCatalogMutationRejectedAsync(
        string mutation,
        string expectedMessage,
        string assertionSql
    )
    {
        await ExecuteAsync(assertionSql);
        var model = JsonNode.Parse(await File.ReadAllTextAsync(_manifestPath))!;
        model["relational_mapping_version"]!.GetValue<string>().Should().Be("v3");
        var fingerprint = (
            await RowsAsync($"SELECT {Q("EffectiveSchemaHash")} FROM {Table("dms", "EffectiveSchema")};")
        ).Single()[0];
        var path = Path.Combine(
            _repositoryRoot,
            "eng/DatabaseTemplates/tests/fixtures",
            $"{mutation}.{Dialect}.sql"
        );
        await ExecuteAsync(await File.ReadAllTextAsync(path));
        (await RowsAsync($"SELECT {Q("EffectiveSchemaHash")} FROM {Table("dms", "EffectiveSchema")};"))
            .Single()[0]
            .Should()
            .Be(fingerprint);
        Func<Task> assert = () => ExecuteAsync(assertionSql);
        await assert.Should().ThrowAsync<DbException>().WithMessage(expectedMessage);
    }

    [TestCase("missing")]
    [TestCase("incomplete")]
    [TestCase("wrong_dialect")]
    [TestCase("wide_column")]
    [TestCase("wrong_target")]
    [TestCase("pre_compact")]
    [TestCase("missing_history")]
    [TestCase("schema_set")]
    [TestCase("wrong_hash")]
    [TestCase("omitted_resource")]
    public async Task It_rejects_missing_incomplete_mismatched_or_pre_compact_manifest_inputs(string mutation)
    {
        var model = JsonNode.Parse(await File.ReadAllTextAsync(_manifestPath))!.AsObject();
        var details = model["resource_details"]!.AsArray();
        var table = details
            .SelectMany(d => d!["tables"]!.AsArray())
            .First(t => t!["columns"]!.AsArray().Any(c => c!["kind"]!.GetValue<string>() == "DescriptorFk"));
        var column = table!["columns"]!
            .AsArray()
            .First(c =>
                c!["kind"]!.GetValue<string>() == "DescriptorFk"
                && c["storage"]!["kind"]!.GetValue<string>() == "Stored"
            )!;
        switch (mutation)
        {
            case "incomplete":
                details.RemoveAt(0);
                break;
            case "wrong_dialect":
                model["dialect"] = IsPgsql ? "mssql" : "pgsql";
                break;
            case "wide_column":
                column["type"]!["kind"] = "Int64";
                break;
            case "wrong_target":
                table["constraints"]!
                    .AsArray()
                    .First(c =>
                        c!["kind"]!.GetValue<string>() == "ForeignKey"
                        && c["target_table"]!["name"]!.GetValue<string>() == "Descriptor"
                    )!["target_columns"]![0] = "DocumentId";
                break;
            case "pre_compact":
                details.First(d => d!["storage_kind"]!.GetValue<string>() == "SharedDescriptorTable")![
                    "shared_descriptor_table"
                ]!["key_columns"]![0]!["name"] = "DocumentId";
                break;
            case "missing_history":
                model["tracked_change_tables"] = new JsonArray();
                break;
            case "schema_set":
                model["projects"]![1]!["project_version"] = "99.0.0";
                break;
            case "omitted_resource":
                details.Remove(
                    details.Single(d => d!["resource"]!["project_name"]!.GetValue<string>() == "Sample")
                );
                var resources = model["resources"]!.AsArray();
                resources.Remove(resources.Single(r => r!["project_name"]!.GetValue<string>() == "Sample"));
                break;
            case "wrong_hash":
                model["effective_schema_hash"] = new string('a', 64);
                break;
        }
        var temporary = Path.Combine(Path.GetTempPath(), $"compact-descriptor-{Guid.NewGuid():N}.json");
        try
        {
            if (mutation != "missing")
            {
                await File.WriteAllTextAsync(temporary, model.ToJsonString());
            }
            if (mutation is "schema_set" or "wrong_hash" or "omitted_resource")
            {
                var sql = await CompactDescriptorCatalogAssertions.RenderAsync(
                    _repositoryRoot,
                    Dialect,
                    temporary
                );
                Func<Task> assert = () => ExecuteAsync(sql);
                await assert.Should().ThrowAsync<DbException>().WithMessage("*schema set mismatch*");
            }
            else
            {
                Func<Task> render = async () =>
                    await CompactDescriptorCatalogAssertions.RenderAsync(_repositoryRoot, Dialect, temporary);
                await render
                    .Should()
                    .ThrowAsync<InvalidOperationException>()
                    .WithMessage("*manifest rejected*");
            }
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}

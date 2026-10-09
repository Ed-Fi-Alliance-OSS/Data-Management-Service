// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Tests.Integration.Doubles;
using FluentAssertions;

namespace EdFi.DataManagementService.Tests.Integration.Scenarios;

/// <summary>HTTP history contracts with independent compact and owning document keys.</summary>
internal static class CompactDescriptorHistoryScenario
{
    public const string CustomViewStrategy = "SchoolTypeDescriptorWithCompactHistoryAccess";
    public const string IncludingDeletesStrategy = CustomViewStrategy + "IncludingDeletes";
    private const string Descriptors = "/data/ed-fi/schoolTypeDescriptors";
    private const string AcademicDescriptors = "/data/ed-fi/academicSubjectDescriptors";
    private const string SampleDescriptors = "/data/sample/schoolTypeDescriptors";
    private const string Items = "/data/ed-fi/compactDescriptorItems";
    private const string Bridges = "/data/ed-fi/compactDescriptorBridges";

    public static async Task It_routes_deleted_descriptors_by_qualified_resource_key_without_live_rows(
        ApiIntegrationHarness harness
    )
    {
        string ns = CreateNamespace();
        Descriptor[] descriptors =
        [
            await CreateDescriptorAsync(harness, Descriptors, ns),
            await CreateDescriptorAsync(harness, AcademicDescriptors, ns),
            await CreateDescriptorAsync(harness, SampleDescriptors, ns),
        ];
        descriptors.Select(row => row.ResourceKeyId).Should().OnlyHaveUniqueItems();
        descriptors.Select(row => row.DescriptorId).Should().OnlyHaveUniqueItems();
        foreach (Descriptor descriptor in descriptors)
        {
            await DeleteAsync(harness, descriptor.Path);
            await AssertGoneAsync(harness, descriptor);
            long version = await DescriptorDeleteVersionAsync(harness, descriptor);
            await AssertDeleteAsync(
                harness,
                descriptor.Endpoint,
                descriptor.Path,
                DescriptorKey(descriptor),
                version
            );
            (await HistoryAsync(harness, descriptor.Endpoint, "keyChanges")).Should().BeEmpty();
        }
        (await ScalarAsync(harness, "SELECT COUNT(*) FROM \"tracked_changes_edfi\".\"Descriptor\""))
            .Should()
            .Be(3);

        Descriptor recreated = await CreateDescriptorAsync(harness, SampleDescriptors, ns);
        recreated.DescriptorId.Should().NotBe(descriptors[2].DescriptorId);
        recreated.DocumentId.Should().NotBe(descriptors[2].DocumentId);
        (await HistoryAsync(harness, SampleDescriptors, "deletes")).Should().BeEmpty();
        foreach (Descriptor descriptor in descriptors[..2])
        {
            await AssertDeleteAsync(
                harness,
                descriptor.Endpoint,
                descriptor.Path,
                DescriptorKey(descriptor),
                await DescriptorDeleteVersionAsync(harness, descriptor)
            );
        }
    }

    public static async Task It_preserves_provider_component_comparisons_for_descriptor_recreation(
        ApiIntegrationHarness harness
    )
    {
        string ns = CreateNamespace();
        // This history consumer still compares stored components, under the provider collation.
        (
            string OldNamespace,
            string OldCode,
            string NewNamespace,
            string NewCode,
            bool Suppressed
        )[] cases =
        [
            (ns + "/case", "MiXeD", ns + "/case", "MIXED", !IsPostgresql(harness)),
            (ns + "/space ", "Code", ns + "/space", "Code", !IsPostgresql(harness)),
            (ns + "/components", "Campus#Code", ns + "/components#Campus", "Code", false),
            (ns + "/exact", "Extra#Code", ns + "/exact", "Extra#Code", true),
        ];
        foreach (var test in cases)
        {
            Descriptor deleted = await CreateDescriptorAsync(
                harness,
                Descriptors,
                test.OldNamespace,
                test.OldCode
            );
            await DeleteAsync(harness, deleted.Path);
            await AssertGoneAsync(harness, deleted);
            long version = await DescriptorDeleteVersionAsync(harness, deleted);
            await CreateDescriptorAsync(harness, SampleDescriptors, test.OldNamespace, test.OldCode);
            await AssertDeleteAsync(harness, Descriptors, deleted.Path, DescriptorKey(deleted), version);
            Descriptor recreated = await CreateDescriptorAsync(
                harness,
                Descriptors,
                test.NewNamespace,
                test.NewCode
            );
            recreated.DescriptorId.Should().NotBe(deleted.DescriptorId);
            JsonArray history = await HistoryAsync(harness, Descriptors, "deletes", version);
            if (test.Suppressed)
            {
                history.Should().BeEmpty();
            }
            else
            {
                AssertHistory(history, deleted.Path, version, ("keyValues", DescriptorKey(deleted)));
            }
        }
    }

    public static async Task It_snapshots_direct_and_copied_descriptor_identities_for_resource_histories(
        ApiIntegrationHarness harness
    )
    {
        Descriptor before = await CreateDescriptorAsync(harness, Descriptors, CreateNamespace(), "Old#MiXeD");
        Descriptor after = await CreateDescriptorAsync(
            harness,
            Descriptors,
            CreateNamespace() + " ",
            "New#MiXeD"
        );
        string item = await PostAsync(harness, Items, Item("item", before.Uri.ToUpperInvariant()));
        string bridge = await PostAsync(harness, Bridges, Bridge(before.Uri.ToLowerInvariant()));
        await AssertResourceKeysAsync(harness, item, bridge, before.DescriptorId);
        await PutAsync(harness, item, Item("item", after.Uri.ToLowerInvariant()));
        await AssertResourceKeysAsync(harness, item, bridge, after.DescriptorId);

        foreach (
            var target in new[]
            {
                (Items, item, "CompactDescriptorItem"),
                (Bridges, bridge, "CompactDescriptorBridge"),
            }
        )
        {
            long version = await ResourceHistoryVersionAsync(
                harness,
                target.Item3,
                target.Item2,
                keyChange: true
            );
            JsonObject oldKey = target.Item1 == Items ? ItemKey(before.Uri) : BridgeKey(before.Uri);
            JsonObject newKey = target.Item1 == Items ? ItemKey(after.Uri) : BridgeKey(after.Uri);
            AssertHistory(
                await HistoryAsync(harness, target.Item1, "keyChanges"),
                target.Item2,
                version,
                ("oldKeyValues", oldKey),
                ("newKeyValues", newKey)
            );
        }
        long newest = await NewestVersionAsync(harness);
        (await PostAsync(harness, Items, Item("item", after.Uri.ToUpperInvariant()), HttpStatusCode.OK))
            .Should()
            .Be(item);
        await PutAsync(harness, item, Item("item", after.Uri));
        (await NewestVersionAsync(harness)).Should().Be(newest);
        (await HistoryAsync(harness, Items, "keyChanges", minimum: newest + 1)).Should().BeEmpty();
        (await HistoryAsync(harness, Bridges, "keyChanges", minimum: newest + 1)).Should().BeEmpty();

        await DeleteAsync(harness, bridge);
        await DeleteAsync(harness, item);
        await DeleteAsync(harness, before.Path);
        await DeleteAsync(harness, after.Path);
        await AssertGoneAsync(harness, before);
        await AssertGoneAsync(harness, after);
        await AssertDeleteAsync(
            harness,
            Items,
            item,
            ItemKey(after.Uri),
            await ResourceHistoryVersionAsync(harness, "CompactDescriptorItem", item, keyChange: false)
        );
        await AssertDeleteAsync(
            harness,
            Bridges,
            bridge,
            BridgeKey(after.Uri),
            await ResourceHistoryVersionAsync(harness, "CompactDescriptorBridge", bridge, keyChange: false)
        );
        // Key-change snapshots survive the loss of both descriptor rows too.
        JsonObject itemChange = (await HistoryAsync(harness, Items, "keyChanges")).Single()!.AsObject();
        AssertJson(itemChange["oldKeyValues"]!, ItemKey(before.Uri));
        AssertJson(itemChange["newKeyValues"]!, ItemKey(after.Uri));
    }

    public static async Task It_recreates_resources_using_compact_descriptor_identity_joins(
        ApiIntegrationHarness harness
    )
    {
        string ns = CreateNamespace();
        (string OldNamespace, string OldCode, string NewNamespace, string NewCode, bool Suppressed)[] cases =
        [
            (ns + "/exact", "Extra#Code", ns + "/exact", "Extra#Code", true),
            (ns + "/case", "MiXeD", ns + "/case", "MIXED", !IsPostgresql(harness)),
            (ns + "/space ", "Code", ns + "/space", "Code", !IsPostgresql(harness)),
            (ns + "/components", "Campus#Code", ns + "/components#Campus", "Code", false),
        ];
        foreach (var test in cases)
        {
            Descriptor descriptor = await CreateDescriptorAsync(
                harness,
                Descriptors,
                test.OldNamespace,
                test.OldCode
            );
            await CreateDescriptorAsync(harness, SampleDescriptors, test.OldNamespace, test.OldCode);
            string deleted = await PostAsync(harness, Items, Item("item", descriptor.Uri));
            await DeleteAsync(harness, deleted);
            long version = await ResourceHistoryVersionAsync(
                harness,
                "CompactDescriptorItem",
                deleted,
                keyChange: false
            );
            await DeleteAsync(harness, descriptor.Path);
            await AssertGoneAsync(harness, descriptor);
            await AssertDeleteAsync(harness, Items, deleted, ItemKey(descriptor.Uri), version);
            Descriptor replacement = await CreateDescriptorAsync(
                harness,
                Descriptors,
                test.NewNamespace,
                test.NewCode
            );
            replacement.DescriptorId.Should().NotBe(descriptor.DescriptorId);
            string recreated = await PostAsync(
                harness,
                Items,
                Item("item", replacement.Uri.ToUpperInvariant())
            );
            recreated.Should().NotBe(deleted);
            await using DbCommand command = Command(
                harness,
                "SELECT root.\"SchoolTypeDescriptor_DescriptorId\" FROM \"edfi\".\"CompactDescriptorItem\" root JOIN \"dms\".\"Document\" document ON document.\"DocumentId\" = root.\"DocumentId\" WHERE document.\"DocumentUuid\" = @uuid",
                ("@uuid", Uuid(recreated))
            );
            (await command.ExecuteScalarAsync()).Should().Be(replacement.DescriptorId);
            JsonArray history = await HistoryAsync(harness, Items, "deletes", version);
            if (test.Suppressed)
            {
                history.Should().BeEmpty();
            }
            else
            {
                AssertHistory(history, deleted, version, ("keyValues", ItemKey(descriptor.Uri)));
            }
        }
    }

    public static async Task It_keeps_descriptor_component_updates_and_no_ops_out_of_delete_and_key_history(
        ApiIntegrationHarness harness
    )
    {
        Descriptor original = await CreateDescriptorAsync(
            harness,
            Descriptors,
            CreateNamespace(),
            "Campus#MiXeD"
        );
        long initial = await NewestVersionAsync(harness);
        string ns = original.Namespace + "#Campus";
        (await PostAsync(harness, Descriptors, DescriptorBody(ns, "MiXeD"), HttpStatusCode.OK))
            .Should()
            .Be(original.Path);
        Descriptor components = await ReadDescriptorAsync(harness, original.Path, Descriptors, ns, "MiXeD");
        components.DescriptorId.Should().Be(original.DescriptorId);
        components.DocumentId.Should().Be(original.DocumentId);
        components.Version.Should().BeGreaterThan(initial);
        components.Uri.Should().Be(original.Uri);
        (await HistoryAsync(harness, Descriptors, "deletes")).Should().BeEmpty();
        (await HistoryAsync(harness, Descriptors, "keyChanges")).Should().BeEmpty();
        long newest = await NewestVersionAsync(harness);
        string etag = await EtagAsync(harness, original.Path);
        (await PostAsync(harness, Descriptors, DescriptorBody(ns, "MiXeD"), HttpStatusCode.OK))
            .Should()
            .Be(original.Path);
        await PutAsync(harness, original.Path, DescriptorBody(ns, "MiXeD"));
        (await ReadDescriptorAsync(harness, original.Path, Descriptors, ns, "MiXeD")).Should().Be(components);
        (await NewestVersionAsync(harness)).Should().Be(newest);
        (await EtagAsync(harness, original.Path)).Should().Be(etag);
        (await GetArrayAsync(harness, $"{Descriptors}?minChangeVersion={newest + 1}")).Should().BeEmpty();

        await DeleteAsync(harness, original.Path);
        await AssertGoneAsync(harness, components);
        await AssertDeleteAsync(
            harness,
            Descriptors,
            original.Path,
            DescriptorKey(components),
            await DescriptorDeleteVersionAsync(harness, components)
        );
        (await HistoryAsync(harness, Descriptors, "keyChanges")).Should().BeEmpty();
    }

    public static async Task It_authorizes_retained_namespaces_and_old_descriptor_identity_snapshots(
        ApiIntegrationHarness harness,
        MutableNamespacePrefixJwtValidationService identity
    )
    {
        string allowedPrefix = CreateNamespace() + "/allowed/";
        string deniedPrefix = CreateNamespace() + "/denied/";
        Descriptor allowed = await CreateDescriptorAsync(harness, Descriptors, allowedPrefix + "Type");
        Descriptor denied = await CreateDescriptorAsync(harness, Descriptors, deniedPrefix + "Type");
        string item = await PostAsync(harness, Items, Item("item", allowed.Uri, allowed.Namespace));
        await PutAsync(harness, item, Item("item", denied.Uri, denied.Namespace));
        await DeleteAsync(harness, item);
        await DeleteAsync(harness, allowed.Path);
        await DeleteAsync(harness, denied.Path);
        await AssertGoneAsync(harness, allowed);
        await AssertGoneAsync(harness, denied);

        identity.SetNamespacePrefixes([allowedPrefix]);
        await AssertDeleteAsync(
            harness,
            Descriptors,
            allowed.Path,
            DescriptorKey(allowed),
            await DescriptorDeleteVersionAsync(harness, allowed)
        );
        long keyVersion = await ResourceHistoryVersionAsync(
            harness,
            "CompactDescriptorItem",
            item,
            keyChange: true
        );
        AssertHistory(
            await HistoryAsync(harness, Items, "keyChanges"),
            item,
            keyVersion,
            ("oldKeyValues", ItemKey(allowed.Uri)),
            ("newKeyValues", ItemKey(denied.Uri))
        );
        (await HistoryAsync(harness, Items, "deletes")).Should().BeEmpty();

        identity.SetNamespacePrefixes([deniedPrefix]);
        await AssertDeleteAsync(
            harness,
            Descriptors,
            denied.Path,
            DescriptorKey(denied),
            await DescriptorDeleteVersionAsync(harness, denied)
        );
        (await HistoryAsync(harness, Items, "keyChanges")).Should().BeEmpty();
        await AssertDeleteAsync(
            harness,
            Items,
            item,
            ItemKey(denied.Uri),
            await ResourceHistoryVersionAsync(harness, "CompactDescriptorItem", item, keyChange: false)
        );
        identity.SetNamespacePrefixes([CreateNamespace() + "/unrelated/"]);
        (await HistoryAsync(harness, Descriptors, "deletes")).Should().BeEmpty();
        (await HistoryAsync(harness, Items, "deletes")).Should().BeEmpty();
        (await HistoryAsync(harness, Items, "keyChanges")).Should().BeEmpty();
    }

    public static Task It_authorizes_descriptor_history_live_seeks_by_owning_document_membership(
        ApiIntegrationHarness harness
    ) => AssertCustomViewAsync(harness, includingDeletes: false);

    public static Task It_authorizes_descriptor_history_tombstone_probes_by_qualified_document_membership(
        ApiIntegrationHarness harness
    ) => AssertCustomViewAsync(harness, includingDeletes: true);

    private static async Task AssertCustomViewAsync(ApiIntegrationHarness harness, bool includingDeletes)
    {
        string strategy = includingDeletes ? IncludingDeletesStrategy : CustomViewStrategy;
        Descriptor allowed = await CreateDescriptorAsync(harness, Descriptors, CreateNamespace());
        Descriptor denied = await CreateDescriptorAsync(harness, Descriptors, CreateNamespace());
        Descriptor sample = await CreateDescriptorAsync(harness, SampleDescriptors, allowed.Namespace);
        string item = await PostAsync(harness, Items, Item("item", allowed.Uri));
        string bridge = await PostAsync(harness, Bridges, Bridge(allowed.Uri));
        await AssertResourceKeysAsync(harness, item, bridge, allowed.DescriptorId);
        await PutAsync(harness, item, Item("item", denied.Uri));
        await SetViewAsync(harness, strategy, allowed.DocumentId);
        long version = await ResourceHistoryVersionAsync(
            harness,
            "CompactDescriptorItem",
            item,
            keyChange: true
        );
        AssertHistory(
            await HistoryAsync(harness, Items, "keyChanges"),
            item,
            version,
            ("oldKeyValues", ItemKey(allowed.Uri)),
            ("newKeyValues", ItemKey(denied.Uri))
        );
        foreach (
            long wrongDocumentId in new[] { (long)allowed.DescriptorId, denied.DocumentId, sample.DocumentId }
        )
        {
            await SetViewAsync(harness, strategy, wrongDocumentId);
            (await HistoryAsync(harness, Items, "keyChanges")).Should().BeEmpty();
            (await HistoryAsync(harness, Bridges, "keyChanges")).Should().BeEmpty();
        }
        // Restore the old basis while it is live, then retain the membership after its deletion.
        await SetViewAsync(harness, strategy, allowed.DocumentId);
        AssertHistory(
            await HistoryAsync(harness, Bridges, "keyChanges"),
            bridge,
            await ResourceHistoryVersionAsync(harness, "CompactDescriptorBridge", bridge, keyChange: true),
            ("oldKeyValues", BridgeKey(allowed.Uri)),
            ("newKeyValues", BridgeKey(denied.Uri))
        );
        await DeleteAsync(harness, bridge);
        await DeleteAsync(harness, item);
        await SetViewAsync(harness, strategy, denied.DocumentId);
        long itemDelete = await ResourceHistoryVersionAsync(
            harness,
            "CompactDescriptorItem",
            item,
            keyChange: false
        );
        long bridgeDelete = await ResourceHistoryVersionAsync(
            harness,
            "CompactDescriptorBridge",
            bridge,
            keyChange: false
        );
        await AssertDeleteAsync(harness, Items, item, ItemKey(denied.Uri), itemDelete);
        await AssertDeleteAsync(harness, Bridges, bridge, BridgeKey(denied.Uri), bridgeDelete);
        await DeleteAsync(harness, allowed.Path);
        await DeleteAsync(harness, denied.Path);
        await DeleteAsync(harness, sample.Path);
        await AssertGoneAsync(harness, denied);
        // A descriptor self-basis checks the tombstone's document key directly.
        await AssertDeleteAsync(
            harness,
            Descriptors,
            denied.Path,
            DescriptorKey(denied),
            await DescriptorDeleteVersionAsync(harness, denied)
        );
        foreach (
            var target in new[]
            {
                (Items, item, ItemKey(denied.Uri), itemDelete),
                (Bridges, bridge, BridgeKey(denied.Uri), bridgeDelete),
            }
        )
        {
            JsonArray history = await HistoryAsync(harness, target.Item1, "deletes");
            if (includingDeletes)
            {
                AssertHistory(history, target.Item2, target.Item4, ("keyValues", target.Item3));
            }
            else
            {
                history.Should().BeEmpty();
            }
        }
        await SetViewAsync(harness, strategy, allowed.DocumentId);
        JsonArray oldKeyChanges = await HistoryAsync(harness, Items, "keyChanges");
        if (includingDeletes)
        {
            AssertHistory(
                oldKeyChanges,
                item,
                version,
                ("oldKeyValues", ItemKey(allowed.Uri)),
                ("newKeyValues", ItemKey(denied.Uri))
            );
        }
        else
        {
            oldKeyChanges.Should().BeEmpty();
        }
        // The deleted Sample type has the same components, but cannot supply the Ed-Fi basis.
        await SetViewAsync(harness, strategy, sample.DocumentId);
        (await HistoryAsync(harness, Items, "keyChanges")).Should().BeEmpty();
        (await HistoryAsync(harness, Items, "deletes")).Should().BeEmpty();
        (await HistoryAsync(harness, Descriptors, "deletes")).Should().BeEmpty();
    }

    private static string CreateNamespace() => $"uri://ed-fi.org/DMS-1404/history/{Guid.NewGuid():N}";

    private static bool IsPostgresql(ApiIntegrationHarness harness) =>
        harness.DbConnection.GetType().Name == "NpgsqlConnection";

    private static JsonObject DescriptorBody(string ns, string code) =>
        new()
        {
            ["namespace"] = ns,
            ["codeValue"] = code,
            ["shortDescription"] = "Compact descriptor history",
        };

    private static JsonObject Item(string code, string uri, string ns = "uri://ed-fi.org/DMS-1404/history") =>
        new()
        {
            ["itemCode"] = code,
            ["schoolTypeDescriptor"] = uri,
            ["namespace"] = ns,
        };

    private static JsonObject Bridge(string uri) =>
        new()
        {
            ["bridgeCode"] = "bridge",
            ["itemReference"] = new JsonObject { ["itemCode"] = "item", ["schoolTypeDescriptor"] = uri },
        };

    private static JsonObject ItemKey(string uri) =>
        new() { ["itemCode"] = "item", ["schoolTypeDescriptor"] = uri };

    private static JsonObject BridgeKey(string uri) =>
        new()
        {
            ["bridgeCode"] = "bridge",
            ["itemCode"] = "item",
            ["schoolTypeDescriptor"] = uri,
        };

    private static JsonObject DescriptorKey(Descriptor descriptor) =>
        new() { ["namespace"] = descriptor.Namespace, ["codeValue"] = descriptor.CodeValue };

    private static async Task<Descriptor> CreateDescriptorAsync(
        ApiIntegrationHarness harness,
        string endpoint,
        string ns,
        string code = "MiXeD"
    )
    {
        await ExecuteAsync(
            harness,
            IsPostgresql(harness)
                ? CompactDescriptorSeedSupport.PostgresqlSeparateDocumentIdsSql
                : CompactDescriptorSeedSupport.MssqlSeparateDocumentIdsSql
        );
        string path = await PostAsync(harness, endpoint, DescriptorBody(ns, code));
        return await ReadDescriptorAsync(harness, path, endpoint, ns, code);
    }

    private static async Task<Descriptor> ReadDescriptorAsync(
        ApiIntegrationHarness harness,
        string path,
        string endpoint,
        string ns,
        string code
    )
    {
        await using DbCommand command = Command(
            harness,
            "SELECT descriptor.\"DescriptorId\", descriptor.\"DocumentId\", descriptor.\"ResourceKeyId\", document.\"ContentVersion\", document.\"ContentLastModifiedAt\", descriptor.\"Namespace\", descriptor.\"CodeValue\", descriptor.\"ContentVersion\", descriptor.\"ContentLastModifiedAt\" FROM \"dms\".\"Descriptor\" descriptor JOIN \"dms\".\"Document\" document ON document.\"DocumentId\" = descriptor.\"DocumentId\" WHERE document.\"DocumentUuid\" = @uuid",
            ("@uuid", Uuid(path))
        );
        await using DbDataReader reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        int descriptorId = reader.GetInt32(0);
        long documentId = reader.GetInt64(1);
        documentId.Should().BeGreaterThan(int.MaxValue);
        documentId.Should().NotBe(descriptorId);
        reader.GetString(5).Should().Be(ns);
        reader.GetString(6).Should().Be(code);
        reader.GetInt64(7).Should().Be(reader.GetInt64(3));
        reader.GetValue(8).Should().Be(reader.GetValue(4));
        return new(
            path,
            endpoint,
            ns,
            code,
            descriptorId,
            documentId,
            reader.GetInt16(2),
            reader.GetInt64(3),
            reader.GetValue(4)
        );
    }

    private static async Task AssertGoneAsync(ApiIntegrationHarness harness, Descriptor descriptor)
    {
        (
            await ScalarAsync(
                harness,
                "SELECT COUNT(*) FROM \"dms\".\"Descriptor\" WHERE \"DescriptorId\" = @id",
                ("@id", descriptor.DescriptorId)
            )
        )
            .Should()
            .Be(0);
        (
            await ScalarAsync(
                harness,
                "SELECT COUNT(*) FROM \"dms\".\"Document\" WHERE \"DocumentId\" = @id",
                ("@id", descriptor.DocumentId)
            )
        )
            .Should()
            .Be(0);
        (
            await ScalarAsync(
                harness,
                "SELECT COUNT(*) FROM \"dms\".\"ReferentialIdentity\" WHERE \"DocumentId\" = @id",
                ("@id", descriptor.DocumentId)
            )
        )
            .Should()
            .Be(0);
    }

    private static async Task<long> DescriptorDeleteVersionAsync(
        ApiIntegrationHarness harness,
        Descriptor descriptor
    )
    {
        await using DbCommand command = Command(
            harness,
            "SELECT \"DocumentId\", \"ResourceKeyId\", \"ChangeVersion\" FROM \"tracked_changes_edfi\".\"Descriptor\" WHERE \"Id\" = @uuid",
            ("@uuid", Uuid(descriptor.Path))
        );
        await using DbDataReader reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        reader.GetInt64(0).Should().Be(descriptor.DocumentId);
        reader.GetInt16(1).Should().Be(descriptor.ResourceKeyId);
        long version = reader.GetInt64(2);
        version.Should().BeGreaterThan(descriptor.Version);
        (await reader.ReadAsync()).Should().BeFalse();
        return version;
    }

    private static async Task<long> ResourceHistoryVersionAsync(
        ApiIntegrationHarness harness,
        string resource,
        string path,
        bool keyChange
    )
    {
        await using DbCommand command = Command(
            harness,
            $"SELECT MAX(\"ChangeVersion\") FROM \"tracked_changes_edfi\".\"{resource}\" WHERE \"Id\" = @uuid AND \"New{(resource == "CompactDescriptorItem" ? "ItemCode" : "BridgeCode")}\" IS {(keyChange ? "NOT " : "")}NULL",
            ("@uuid", Uuid(path))
        );
        object value = (await command.ExecuteScalarAsync())!;
        value.Should().NotBe(DBNull.Value);
        return Convert.ToInt64(value);
    }

    private static async Task AssertResourceKeysAsync(
        ApiIntegrationHarness harness,
        string item,
        string bridge,
        int descriptorId
    )
    {
        foreach (
            var target in new[]
            {
                ("CompactDescriptorItem", "SchoolTypeDescriptor_DescriptorId", item),
                ("CompactDescriptorBridge", "Item_SchoolTypeDescriptor_DescriptorId", bridge),
            }
        )
        {
            await using DbCommand command = Command(
                harness,
                $"SELECT root.\"{target.Item2}\", root.\"DocumentId\" FROM \"edfi\".\"{target.Item1}\" root JOIN \"dms\".\"Document\" document ON document.\"DocumentId\" = root.\"DocumentId\" WHERE document.\"DocumentUuid\" = @uuid",
                ("@uuid", Uuid(target.Item3))
            );
            await using DbDataReader reader = await command.ExecuteReaderAsync();
            (await reader.ReadAsync()).Should().BeTrue();
            reader.GetInt32(0).Should().Be(descriptorId);
            reader.GetInt64(1).Should().BeGreaterThan(int.MaxValue);
        }
    }

    private static async Task SetViewAsync(ApiIntegrationHarness harness, string strategy, long documentId)
    {
        await ExecuteAsync(
            harness,
            IsPostgresql(harness)
                ? "CREATE SCHEMA IF NOT EXISTS auth"
                : "IF SCHEMA_ID(N'auth') IS NULL EXEC(N'CREATE SCHEMA [auth]')"
        );
        await ExecuteAsync(harness, $"DROP VIEW IF EXISTS \"auth\".\"{strategy}\"");
        // A retained membership independent of the deleted basis table, as IncludingDeletes requires.
        await ExecuteAsync(
            harness,
            $"CREATE VIEW \"auth\".\"{strategy}\" AS SELECT CAST({documentId} AS bigint) AS \"DocumentId\""
        );
    }

    private static async Task AssertDeleteAsync(
        ApiIntegrationHarness harness,
        string endpoint,
        string path,
        JsonObject key,
        long version
    ) =>
        AssertHistory(
            await HistoryAsync(harness, endpoint, "deletes", version),
            path,
            version,
            ("keyValues", key)
        );

    private static void AssertHistory(
        JsonArray history,
        string path,
        long version,
        params (string Name, JsonObject Key)[] keys
    )
    {
        history.Should().ContainSingle();
        JsonObject expected = new() { ["id"] = Uuid(path).ToString("D"), ["changeVersion"] = version };
        foreach (var (name, key) in keys)
        {
            expected[name] = key.DeepClone();
        }
        AssertJson(history.Single()!, expected);
    }

    private static void AssertJson(JsonNode actual, JsonNode expected) =>
        JsonNode
            .DeepEquals(actual, expected)
            .Should()
            .BeTrue($"expected {expected.ToJsonString()}, received {actual.ToJsonString()}");

    private static async Task<JsonArray> HistoryAsync(
        ApiIntegrationHarness harness,
        string endpoint,
        string operation,
        long version = 0,
        long minimum = 0
    )
    {
        string window =
            version > 0
                ? $"minChangeVersion={version}&maxChangeVersion={version}"
                : $"minChangeVersion={minimum}";
        using HttpResponseMessage response = await harness.HttpClient.GetAsync(
            $"{endpoint}/{operation}?{window}&totalCount=true"
        );
        string body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        JsonArray result = JsonNode.Parse(body)!.AsArray();
        response.Headers.GetValues("Total-Count").Single().Should().Be(result.Count.ToString());
        return result;
    }

    private static async Task<JsonArray> GetArrayAsync(ApiIntegrationHarness harness, string endpoint)
    {
        using HttpResponseMessage response = await harness.HttpClient.GetAsync(endpoint);
        string body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        return JsonNode.Parse(body)!.AsArray();
    }

    private static async Task<long> NewestVersionAsync(ApiIntegrationHarness harness)
    {
        using HttpResponseMessage response = await harness.HttpClient.GetAsync(
            "/changeQueries/v1/availableChangeVersions"
        );
        string body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        return JsonNode.Parse(body)!["newestChangeVersion"]!.GetValue<long>();
    }

    private static async Task<string> EtagAsync(ApiIntegrationHarness harness, string path)
    {
        using HttpResponseMessage response = await harness.HttpClient.GetAsync(path);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        response.TryReadRawEtag(out string etag).Should().BeTrue();
        return etag;
    }

    private static Guid Uuid(string path) => Guid.Parse(path.Split('/')[^1]);

    private static async Task<string> PostAsync(
        ApiIntegrationHarness harness,
        string endpoint,
        JsonObject body,
        HttpStatusCode status = HttpStatusCode.Created
    )
    {
        using HttpResponseMessage response = await SendAsync(harness, HttpMethod.Post, endpoint, body);
        response.StatusCode.Should().Be(status, await response.Content.ReadAsStringAsync());
        response.Headers.Location.Should().NotBeNull();
        return response.Headers.Location!.IsAbsoluteUri
            ? response.Headers.Location.AbsolutePath
            : response.Headers.Location.OriginalString;
    }

    private static async Task PutAsync(ApiIntegrationHarness harness, string path, JsonObject body)
    {
        body["id"] = Uuid(path).ToString("D");
        using HttpResponseMessage response = await SendAsync(harness, HttpMethod.Put, path, body);
        response.StatusCode.Should().Be(HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync());
    }

    private static async Task DeleteAsync(ApiIntegrationHarness harness, string path)
    {
        using HttpResponseMessage response = await harness.HttpClient.DeleteAsync(path);
        response.StatusCode.Should().Be(HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync());
    }

    private static async Task<HttpResponseMessage> SendAsync(
        ApiIntegrationHarness harness,
        HttpMethod method,
        string path,
        JsonObject body
    )
    {
        using HttpRequestMessage request = new(method, path)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        return await harness.HttpClient.SendAsync(request);
    }

    private static DbCommand Command(
        ApiIntegrationHarness harness,
        string sql,
        params (string Name, object Value)[] parameters
    )
    {
        DbCommand command = harness.DbConnection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            DbParameter parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }
        return command;
    }

    private static async Task ExecuteAsync(ApiIntegrationHarness harness, string sql)
    {
        await using DbCommand command = Command(harness, sql);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ScalarAsync(
        ApiIntegrationHarness harness,
        string sql,
        params (string Name, object Value)[] parameters
    )
    {
        await using DbCommand command = Command(harness, sql, parameters);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private sealed record Descriptor(
        string Path,
        string Endpoint,
        string Namespace,
        string CodeValue,
        int DescriptorId,
        long DocumentId,
        short ResourceKeyId,
        long Version,
        object ModifiedAt
    )
    {
        public string Uri => $"{Namespace}#{CodeValue}";
    }
}

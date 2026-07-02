// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.ChangeQueries;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit.ChangeQueries;

[TestFixture]
[Parallelizable]
public class Given_TrackedChangeQueryRowReader
{
    [Test]
    public async Task It_reads_delete_rows_without_total_count()
    {
        var rowId = Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb");
        var reader = Reader(
            InMemoryRelationalResultSet.Create(
                RelationalAccessTestData.CreateRow(
                    ("__Id", rowId),
                    ("__ChangeVersion", 42L),
                    ("schoolId__old", 255901)
                )
            ),
            InMemoryRelationalResultSet.Create(
                RelationalAccessTestData.CreateRow(
                    ("__Id", Guid.Parse("cccccccc-1111-2222-3333-dddddddddddd")),
                    ("__ChangeVersion", 43L),
                    ("schoolId__old", 255902)
                )
            )
        );

        var result = await TrackedChangeQueryRowReader.ReadAsync(
            reader,
            ChangeQueryEndpointOperation.Deletes,
            [ScalarField("schoolId")],
            includesTotalCount: false,
            CancellationToken.None
        );

        result.TotalCount.Should().BeNull();
        result.Items.Should().HaveCount(1);
        JsonObject item = Item(result.Items, 0);
        item["id"]!.GetValue<string>().Should().Be("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb");
        item["changeVersion"]!.GetValue<long>().Should().Be(42L);
        JsonObject keyValues = Object(item, "keyValues");
        keyValues["schoolId"]!.GetValue<int>().Should().Be(255901);
        item.ContainsKey("oldKeyValues").Should().BeFalse();
        item.ContainsKey("newKeyValues").Should().BeFalse();
    }

    [Test]
    public async Task It_reads_total_count_from_first_result_set()
    {
        var reader = Reader(
            InMemoryRelationalResultSet.Create(RelationalAccessTestData.CreateRow(("__TotalCount", 17L))),
            InMemoryRelationalResultSet.Create(
                RelationalAccessTestData.CreateRow(
                    ("__Id", "aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"),
                    ("__ChangeVersion", 42L),
                    ("schoolId__old", 255901)
                )
            )
        );

        var result = await TrackedChangeQueryRowReader.ReadAsync(
            reader,
            ChangeQueryEndpointOperation.Deletes,
            [ScalarField("schoolId")],
            includesTotalCount: true,
            CancellationToken.None
        );

        result.TotalCount.Should().Be(17L);
        result.Items.Should().HaveCount(1);
        Object(Item(result.Items, 0), "keyValues")["schoolId"]!.GetValue<int>().Should().Be(255901);
    }

    [Test]
    public async Task It_returns_zero_for_empty_total_count_result_set_and_reads_second_result_set()
    {
        var reader = Reader(
            InMemoryRelationalResultSet.Create(),
            InMemoryRelationalResultSet.Create(
                RelationalAccessTestData.CreateRow(
                    ("__Id", "aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"),
                    ("__ChangeVersion", 42L),
                    ("schoolId__old", 255901)
                )
            )
        );

        var result = await TrackedChangeQueryRowReader.ReadAsync(
            reader,
            ChangeQueryEndpointOperation.Deletes,
            [ScalarField("schoolId")],
            includesTotalCount: true,
            CancellationToken.None
        );

        result.TotalCount.Should().Be(0L);
        result.Items.Should().HaveCount(1);
        Object(Item(result.Items, 0), "keyValues")["schoolId"]!.GetValue<int>().Should().Be(255901);
    }

    [Test]
    public async Task It_composes_descriptor_namespace_and_code_value()
    {
        var reader = Reader(
            InMemoryRelationalResultSet.Create(
                RelationalAccessTestData.CreateRow(
                    ("__Id", "aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"),
                    ("__ChangeVersion", 42L),
                    ("termDescriptor__old", "uri://ed-fi.org/TermDescriptor"),
                    ("termDescriptor__oldCodeValue", "Fall")
                )
            )
        );

        var result = await TrackedChangeQueryRowReader.ReadAsync(
            reader,
            ChangeQueryEndpointOperation.Deletes,
            [DescriptorField("termDescriptor")],
            includesTotalCount: false,
            CancellationToken.None
        );

        JsonObject keyValues = Object(Item(result.Items, 0), "keyValues");
        keyValues["termDescriptor"]!.GetValue<string>().Should().Be("uri://ed-fi.org/TermDescriptor#Fall");
    }

    [Test]
    public async Task It_returns_null_for_descriptor_when_namespace_or_code_value_is_null()
    {
        var reader = Reader(
            InMemoryRelationalResultSet.Create(
                RelationalAccessTestData.CreateRow(
                    ("__Id", "aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"),
                    ("__ChangeVersion", 42L),
                    ("termDescriptor__old", "uri://ed-fi.org/TermDescriptor"),
                    ("termDescriptor__oldCodeValue", null)
                )
            )
        );

        var result = await TrackedChangeQueryRowReader.ReadAsync(
            reader,
            ChangeQueryEndpointOperation.Deletes,
            [DescriptorField("termDescriptor")],
            includesTotalCount: false,
            CancellationToken.None
        );

        JsonObject keyValues = Object(Item(result.Items, 0), "keyValues");
        keyValues.ContainsKey("termDescriptor").Should().BeTrue();
        keyValues["termDescriptor"].Should().BeNull();
    }

    [Test]
    public async Task It_reads_key_changes_with_old_and_new_key_values()
    {
        var reader = Reader(
            InMemoryRelationalResultSet.Create(
                RelationalAccessTestData.CreateRow(
                    ("__Id", "aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"),
                    ("__ChangeVersion", 42L),
                    ("schoolId__old", 255901),
                    ("schoolId__new", 255902),
                    ("termDescriptor__old", "uri://ed-fi.org/TermDescriptor"),
                    ("termDescriptor__oldCodeValue", "Fall"),
                    ("termDescriptor__new", "uri://ed-fi.org/TermDescriptor"),
                    ("termDescriptor__newCodeValue", "Spring")
                )
            )
        );

        var result = await TrackedChangeQueryRowReader.ReadAsync(
            reader,
            ChangeQueryEndpointOperation.KeyChanges,
            [ScalarField("schoolId"), DescriptorField("termDescriptor")],
            includesTotalCount: false,
            CancellationToken.None
        );

        JsonObject item = Item(result.Items, 0);
        item.ContainsKey("keyValues").Should().BeFalse();

        JsonObject oldKeyValues = Object(item, "oldKeyValues");
        oldKeyValues["schoolId"]!.GetValue<int>().Should().Be(255901);
        oldKeyValues["termDescriptor"]!.GetValue<string>().Should().Be("uri://ed-fi.org/TermDescriptor#Fall");

        JsonObject newKeyValues = Object(item, "newKeyValues");
        newKeyValues["schoolId"]!.GetValue<int>().Should().Be(255902);
        newKeyValues["termDescriptor"]!
            .GetValue<string>()
            .Should()
            .Be("uri://ed-fi.org/TermDescriptor#Spring");
    }

    [Test]
    public async Task It_reads_plan_requested_key_change_row_shape()
    {
        var reader = Reader(
            InMemoryRelationalResultSet.Create(
                RelationalAccessTestData.CreateRow(
                    ("__Id", "11111111-1111-1111-1111-111111111111"),
                    ("__ChangeVersion", 7L),
                    ("schoolId__old", 100),
                    ("schoolId__new", 300)
                )
            )
        );

        var result = await TrackedChangeQueryRowReader.ReadAsync(
            reader,
            ChangeQueryEndpointOperation.KeyChanges,
            [ScalarField("schoolId")],
            includesTotalCount: false,
            CancellationToken.None
        );

        result.TotalCount.Should().BeNull();
        result.Items.Should().HaveCount(1);
        JsonObject item = Item(result.Items, 0);
        item["id"]!.GetValue<string>().Should().Be("11111111-1111-1111-1111-111111111111");
        item["changeVersion"]!.GetValue<long>().Should().Be(7L);
        Object(item, "oldKeyValues")["schoolId"]!.GetValue<int>().Should().Be(100);
        Object(item, "newKeyValues")["schoolId"]!.GetValue<int>().Should().Be(300);
        item.ContainsKey("keyValues").Should().BeFalse();
    }

    [Test]
    public async Task It_formats_scalar_values()
    {
        var guid = Guid.Parse("99999999-1111-2222-3333-444444444444");
        var reader = Reader(
            InMemoryRelationalResultSet.Create(
                RelationalAccessTestData.CreateRow(
                    ("__Id", "aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"),
                    ("__ChangeVersion", 42L),
                    ("dateValue__old", new DateOnly(2026, 6, 18)),
                    ("dateTimeValue__old", new DateTime(2026, 6, 18, 13, 14, 15, DateTimeKind.Utc)),
                    (
                        "unspecifiedDateTimeValue__old",
                        new DateTime(2026, 6, 18, 14, 15, 16, DateTimeKind.Unspecified)
                    ),
                    ("timeValue__old", new TimeOnly(9, 8, 7)),
                    ("decimalValue__old", 123.45m),
                    ("boolValue__old", true),
                    ("guidValue__old", guid),
                    ("nullValue__old", null)
                )
            )
        );

        var result = await TrackedChangeQueryRowReader.ReadAsync(
            reader,
            ChangeQueryEndpointOperation.Deletes,
            [
                ScalarField("dateValue"),
                ScalarField("dateTimeValue"),
                ScalarField("unspecifiedDateTimeValue"),
                ScalarField("timeValue"),
                ScalarField("decimalValue"),
                ScalarField("boolValue"),
                ScalarField("guidValue"),
                ScalarField("nullValue"),
            ],
            includesTotalCount: false,
            CancellationToken.None
        );

        JsonObject keyValues = Object(Item(result.Items, 0), "keyValues");
        keyValues["dateValue"]!.GetValue<string>().Should().Be("2026-06-18");
        keyValues["dateTimeValue"]!.GetValue<string>().Should().Be("2026-06-18T13:14:15Z");
        keyValues["unspecifiedDateTimeValue"]!.GetValue<string>().Should().Be("2026-06-18T14:15:16Z");
        keyValues["timeValue"]!.GetValue<string>().Should().Be("09:08:07");
        keyValues["decimalValue"]!.GetValue<decimal>().Should().Be(123.45m);
        keyValues["boolValue"]!.GetValue<bool>().Should().BeTrue();
        keyValues["guidValue"]!.GetValue<string>().Should().Be("99999999-1111-2222-3333-444444444444");
        keyValues.ContainsKey("nullValue").Should().BeTrue();
        keyValues["nullValue"].Should().BeNull();
    }

    [Test]
    public async Task It_formats_provider_shaped_values_by_scalar_kind()
    {
        var reader = Reader(
            InMemoryRelationalResultSet.Create(
                RelationalAccessTestData.CreateRow(
                    ("__Id", "aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"),
                    ("__ChangeVersion", 42L),
                    (
                        "dateFromDateTime__old",
                        new DateTime(2026, 6, 18, 13, 14, 15, DateTimeKind.Unspecified)
                    ),
                    (
                        "dateFromDateTimeOffset__old",
                        new DateTimeOffset(2026, 6, 19, 23, 45, 50, TimeSpan.FromHours(2))
                    ),
                    (
                        "dateTimeFromDateTimeOffset__old",
                        new DateTimeOffset(2026, 6, 18, 13, 14, 15, TimeSpan.FromHours(2))
                    ),
                    ("timeFromTimeSpan__old", new TimeSpan(9, 8, 7)),
                    (
                        "timeFromDateTimeOffset__old",
                        new DateTimeOffset(2026, 6, 18, 21, 20, 19, TimeSpan.FromHours(-5))
                    )
                )
            )
        );

        var result = await TrackedChangeQueryRowReader.ReadAsync(
            reader,
            ChangeQueryEndpointOperation.Deletes,
            [
                ScalarField("dateFromDateTime", ScalarKind.Date),
                ScalarField("dateFromDateTimeOffset", ScalarKind.Date),
                ScalarField("dateTimeFromDateTimeOffset", ScalarKind.DateTime),
                ScalarField("timeFromTimeSpan", ScalarKind.Time),
                ScalarField("timeFromDateTimeOffset", ScalarKind.Time),
            ],
            includesTotalCount: false,
            CancellationToken.None
        );

        JsonObject keyValues = Object(Item(result.Items, 0), "keyValues");
        keyValues["dateFromDateTime"]!.GetValue<string>().Should().Be("2026-06-18");
        keyValues["dateFromDateTimeOffset"]!.GetValue<string>().Should().Be("2026-06-19");
        keyValues["dateTimeFromDateTimeOffset"]!.GetValue<string>().Should().Be("2026-06-18T11:14:15Z");
        keyValues["timeFromTimeSpan"]!.GetValue<string>().Should().Be("09:08:07");
        keyValues["timeFromDateTimeOffset"]!.GetValue<string>().Should().Be("21:20:19");
    }

    [Test]
    public async Task It_uses_old_and_new_column_scalar_kinds_for_key_changes()
    {
        var reader = Reader(
            InMemoryRelationalResultSet.Create(
                RelationalAccessTestData.CreateRow(
                    ("__Id", "aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"),
                    ("__ChangeVersion", 42L),
                    (
                        "providerShapedValue__old",
                        new DateTime(2026, 6, 18, 13, 14, 15, DateTimeKind.Unspecified)
                    ),
                    ("providerShapedValue__new", new TimeSpan(9, 8, 7))
                )
            )
        );

        var result = await TrackedChangeQueryRowReader.ReadAsync(
            reader,
            ChangeQueryEndpointOperation.KeyChanges,
            [ScalarField("providerShapedValue", ScalarKind.Date, ScalarKind.Time)],
            includesTotalCount: false,
            CancellationToken.None
        );

        JsonObject item = Item(result.Items, 0);
        Object(item, "oldKeyValues")["providerShapedValue"]!.GetValue<string>().Should().Be("2026-06-18");
        Object(item, "newKeyValues")["providerShapedValue"]!.GetValue<string>().Should().Be("09:08:07");
    }

    private static InMemoryRelationalCommandReader Reader(params InMemoryRelationalResultSet[] resultSets) =>
        new(resultSets);

    private static JsonObject Item(JsonArray items, int index) => items[index]!.AsObject();

    private static JsonObject Object(JsonObject parent, string propertyName) =>
        parent[propertyName]!.AsObject();

    private static ChangeQueryResponseField ScalarField(
        string queryFieldName,
        ScalarKind scalarKind = ScalarKind.String
    ) => ScalarField(queryFieldName, scalarKind, scalarKind);

    private static ChangeQueryResponseField ScalarField(
        string queryFieldName,
        ScalarKind oldScalarKind,
        ScalarKind newScalarKind
    )
    {
        TrackedChangeColumnInfo oldColumn = Column(
            queryFieldName,
            ChangeQueryResponseFieldKind.Scalar,
            oldScalarKind
        );
        TrackedChangeColumnInfo newColumn = Column(
            queryFieldName,
            ChangeQueryResponseFieldKind.Scalar,
            newScalarKind
        );
        return new(
            queryFieldName,
            ChangeQueryResponseFieldKind.Scalar,
            oldColumn,
            newColumn,
            OldDescriptorCodeValueColumn: null,
            NewDescriptorCodeValueColumn: null
        );
    }

    private static ChangeQueryResponseField DescriptorField(string queryFieldName)
    {
        TrackedChangeColumnInfo namespaceColumn = Column(
            $"{queryFieldName}Namespace",
            ChangeQueryResponseFieldKind.Descriptor
        );
        TrackedChangeColumnInfo codeValueColumn = Column(
            $"{queryFieldName}CodeValue",
            ChangeQueryResponseFieldKind.Descriptor
        );

        return new(
            queryFieldName,
            ChangeQueryResponseFieldKind.Descriptor,
            namespaceColumn,
            namespaceColumn,
            codeValueColumn,
            codeValueColumn
        );
    }

    private static TrackedChangeColumnInfo Column(
        string columnName,
        ChangeQueryResponseFieldKind fieldKind,
        ScalarKind scalarKind = ScalarKind.String
    ) =>
        new(
            OldColumnName: new DbColumnName($"Old{columnName}"),
            NewColumnName: new DbColumnName($"New{columnName}"),
            SourceJsonPath: $"$.{columnName}",
            CanonicalStorageColumn: null,
            IsOldColumnNullable: false,
            IsNewColumnNullable: true,
            ScalarType: new RelationalScalarType(scalarKind),
            Role: fieldKind is ChangeQueryResponseFieldKind.Descriptor
                ? TrackedChangeColumnRole.DescriptorNamespace
                : TrackedChangeColumnRole.Scalar,
            Origin: TrackedChangeColumnOrigin.Identity
        );
}

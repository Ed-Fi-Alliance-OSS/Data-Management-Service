// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Profile;
using EdFi.DataManagementService.Core.Profile;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

internal static class CollectionSemanticIdentityEqualityTestPlans
{
    // Semantic-identity members in binding order: city (string), program reference
    // (_DocumentId), type descriptor (_DescriptorId), amount (decimal).
    public static TableWritePlan Create(bool cityUsesSqlServerIdentityCollation)
    {
        var cityPath = Path("$.city", "city");
        var programPath = Path("$.programReference", "programReference");
        var typePath = Path("$.typeDescriptor", "typeDescriptor");
        var amountPath = Path("$.amount", "amount");
        var descriptorResource = new QualifiedResourceName("Ed-Fi", "TypeDescriptor");

        DbColumnModel[] columns =
        [
            Column("CollectionItemId", ColumnKind.CollectionKey, null, null),
            Column("School_DocumentId", ColumnKind.ParentKeyPart, null, null),
            Column("Ordinal", ColumnKind.Ordinal, null, null),
            Column(
                "City",
                ColumnKind.Scalar,
                new RelationalScalarType(ScalarKind.String, MaxLength: 30),
                cityPath
            ) with
            {
                UsesSqlServerIdentityCollation = cityUsesSqlServerIdentityCollation,
            },
            Column("Program_DocumentId", ColumnKind.DocumentFk, null, null),
            Column("TypeDescriptor_DescriptorId", ColumnKind.DescriptorFk, new(ScalarKind.Int32), typePath),
            Column(
                "Amount",
                ColumnKind.Scalar,
                new RelationalScalarType(ScalarKind.Decimal, Decimal: (9, 2)),
                amountPath
            ),
        ];

        var tableModel = new DbTableModel(
            Table: new DbTableName(new DbSchemaName("edfi"), "SchoolAddress"),
            JsonScope: new JsonPathExpression(
                "$.addresses[*]",
                [new JsonPathSegment.Property("addresses"), new JsonPathSegment.AnyArrayElement()]
            ),
            Key: new TableKey(
                "PK_SchoolAddress",
                [new DbKeyColumn(new DbColumnName("CollectionItemId"), ColumnKind.CollectionKey)]
            ),
            Columns: columns,
            Constraints: []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                TableKind: DbTableKind.Collection,
                PhysicalRowIdentityColumns: [new DbColumnName("CollectionItemId")],
                RootScopeLocatorColumns: [new DbColumnName("School_DocumentId")],
                ImmediateParentScopeLocatorColumns: [new DbColumnName("School_DocumentId")],
                SemanticIdentityBindings:
                [
                    new CollectionSemanticIdentityBinding(cityPath, columns[3].ColumnName),
                    new CollectionSemanticIdentityBinding(programPath, columns[4].ColumnName),
                    new CollectionSemanticIdentityBinding(typePath, columns[5].ColumnName),
                    new CollectionSemanticIdentityBinding(amountPath, columns[6].ColumnName),
                ]
            ),
        };

        return new TableWritePlan(
            TableModel: tableModel,
            InsertSql: "insert into edfi.\"SchoolAddress\" values (...)",
            UpdateSql: null,
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(100, columns.Length, 1000),
            ColumnBindings:
            [
                new WriteColumnBinding(columns[0], new WriteValueSource.Precomputed(), "CollectionItemId"),
                new WriteColumnBinding(columns[1], new WriteValueSource.DocumentId(), "School_DocumentId"),
                new WriteColumnBinding(columns[2], new WriteValueSource.Ordinal(), "Ordinal"),
                new WriteColumnBinding(
                    columns[3],
                    new WriteValueSource.Scalar(cityPath, columns[3].ScalarType!),
                    "City"
                ),
                new WriteColumnBinding(
                    columns[4],
                    new WriteValueSource.DocumentReference(0),
                    "Program_DocumentId"
                ),
                new WriteColumnBinding(
                    columns[5],
                    new WriteValueSource.DescriptorReference(descriptorResource, typePath, typePath),
                    "TypeDescriptor_DescriptorId"
                ),
                new WriteColumnBinding(
                    columns[6],
                    new WriteValueSource.Scalar(amountPath, columns[6].ScalarType!),
                    "Amount"
                ),
            ],
            KeyUnificationPlans: [],
            CollectionMergePlan: new CollectionMergePlan(
                SemanticIdentityBindings:
                [
                    new CollectionMergeSemanticIdentityBinding(cityPath, 3),
                    new CollectionMergeSemanticIdentityBinding(programPath, 4),
                    new CollectionMergeSemanticIdentityBinding(typePath, 5),
                    new CollectionMergeSemanticIdentityBinding(amountPath, 6),
                ],
                StableRowIdentityBindingIndex: 0,
                UpdateByStableRowIdentitySql: "update edfi.\"SchoolAddress\" set ...",
                DeleteByStableRowIdentitySql: "delete from edfi.\"SchoolAddress\" where ...",
                OrdinalBindingIndex: 2,
                CompareBindingIndexesInOrder: [3, 4, 5, 6, 2]
            ),
            CollectionKeyPreallocationPlan: new CollectionKeyPreallocationPlan(
                new DbColumnName("CollectionItemId"),
                0
            )
        );
    }

    public static object?[] Raw(
        object? city,
        object? program = null,
        object? type = null,
        object? amount = null
    ) => [city, program, type, amount];

    public static ImmutableArray<SemanticIdentityPart> Parts(
        JsonNode? city,
        bool cityPresent = true,
        JsonNode? amount = null
    ) =>
        [
            new("city", city, cityPresent),
            new("programReference", null, false),
            new("typeDescriptor", null, false),
            new("amount", amount, amount is not null),
        ];

    private static JsonPathExpression Path(string canonical, string property) =>
        new(canonical, [new JsonPathSegment.Property(property)]);

    private static DbColumnModel Column(
        string name,
        ColumnKind kind,
        RelationalScalarType? scalarType,
        JsonPathExpression? sourceJsonPath
    ) =>
        new(
            new DbColumnName(name),
            kind,
            scalarType,
            IsNullable: true,
            SourceJsonPath: sourceJsonPath,
            TargetResource: null,
            Storage: new ColumnStorage.Stored()
        );
}

[TestFixture]
public class Given_CollectionSemanticIdentityEquality_For_A_SqlServer_Flagged_String_Member
{
    private CollectionSemanticIdentityEquality _sut = null!;

    [SetUp]
    public void Setup()
    {
        _sut = CollectionSemanticIdentityEquality.For(
            CollectionSemanticIdentityEqualityTestPlans.Create(cityUsesSqlServerIdentityCollation: true)
        );
    }

    [Test]
    public void It_treats_raw_case_variants_as_equal_with_equal_hashes()
    {
        AssertRawEqual(
            CollectionSemanticIdentityEqualityTestPlans.Raw("Austin", 10L, 5, 1.5m),
            CollectionSemanticIdentityEqualityTestPlans.Raw("AUSTIN", 10L, 5, 1.5m)
        );
    }

    [Test]
    public void It_treats_raw_non_ascii_case_variants_as_equal()
    {
        AssertRawEqual(
            CollectionSemanticIdentityEqualityTestPlans.Raw("Montréal"),
            CollectionSemanticIdentityEqualityTestPlans.Raw("MONTRÉAL")
        );
    }

    [Test]
    public void It_keeps_sharp_s_and_double_s_distinct()
    {
        _sut.RawValues.Equals(
                CollectionSemanticIdentityEqualityTestPlans.Raw("Straße"),
                CollectionSemanticIdentityEqualityTestPlans.Raw("Strasse")
            )
            .Should()
            .BeFalse();
    }

    [Test]
    public void It_keeps_the_kelvin_sign_distinct_from_k()
    {
        _sut.RawValues.Equals(
                CollectionSemanticIdentityEqualityTestPlans.Raw("Kelvin"),
                CollectionSemanticIdentityEqualityTestPlans.Raw("kelvin")
            )
            .Should()
            .BeFalse();
    }

    [Test]
    public void It_keeps_reference_and_descriptor_ids_on_id_equality()
    {
        _sut.RawValues.Equals(
                CollectionSemanticIdentityEqualityTestPlans.Raw("Austin", 10L, 5),
                CollectionSemanticIdentityEqualityTestPlans.Raw("Austin", 11L, 5)
            )
            .Should()
            .BeFalse();
        _sut.RawValues.Equals(
                CollectionSemanticIdentityEqualityTestPlans.Raw("Austin", 10L, 5),
                CollectionSemanticIdentityEqualityTestPlans.Raw("Austin", 10L, 6)
            )
            .Should()
            .BeFalse();
    }

    [Test]
    public void It_keeps_decimals_with_distinct_trailing_zeros_equal()
    {
        AssertRawEqual(
            CollectionSemanticIdentityEqualityTestPlans.Raw("Austin", amount: 1.0m),
            CollectionSemanticIdentityEqualityTestPlans.Raw("Austin", amount: 1.00m)
        );
    }

    [Test]
    public void It_collapses_raw_nulls()
    {
        AssertRawEqual(
            CollectionSemanticIdentityEqualityTestPlans.Raw(null),
            CollectionSemanticIdentityEqualityTestPlans.Raw(null)
        );
    }

    [Test]
    public void It_treats_presence_aware_non_ascii_case_variants_as_equal_with_equal_hashes()
    {
        // The flattener builds parts with JsonValue.Create over the raw value; stored rows can
        // arrive JSON-parsed. Both must reach the comparer as strings, not escaped JSON text.
        AssertPresenceAwareEqual(
            CollectionSemanticIdentityEqualityTestPlans.Parts(JsonValue.Create((object)"Montréal")),
            CollectionSemanticIdentityEqualityTestPlans.Parts(JsonNode.Parse("\"MONTRÉAL\""))
        );
    }

    [Test]
    public void It_keeps_presence_aware_sharp_s_and_double_s_distinct()
    {
        _sut.PresenceAware.Equals(
                CollectionSemanticIdentityEqualityTestPlans.Parts(JsonValue.Create("Straße")),
                CollectionSemanticIdentityEqualityTestPlans.Parts(JsonValue.Create("Strasse"))
            )
            .Should()
            .BeFalse();
    }

    [Test]
    public void It_keeps_a_missing_part_distinct_from_an_explicit_null()
    {
        _sut.PresenceAware.Equals(
                CollectionSemanticIdentityEqualityTestPlans.Parts(null, cityPresent: false),
                CollectionSemanticIdentityEqualityTestPlans.Parts(null, cityPresent: true)
            )
            .Should()
            .BeFalse();
    }

    [Test]
    public void It_compares_non_flagged_presence_aware_members_by_json_text()
    {
        _sut.PresenceAware.Equals(
                CollectionSemanticIdentityEqualityTestPlans.Parts(
                    JsonValue.Create("Austin"),
                    amount: JsonValue.Create(1.5m)
                ),
                CollectionSemanticIdentityEqualityTestPlans.Parts(
                    JsonValue.Create("Austin"),
                    amount: JsonValue.Create(2.5m)
                )
            )
            .Should()
            .BeFalse();
    }

    private void AssertRawEqual(object?[] left, object?[] right)
    {
        _sut.RawValues.Equals(left, right).Should().BeTrue();
        _sut.RawValues.GetHashCode(left).Should().Be(_sut.RawValues.GetHashCode(right));
    }

    private void AssertPresenceAwareEqual(
        ImmutableArray<SemanticIdentityPart> left,
        ImmutableArray<SemanticIdentityPart> right
    )
    {
        _sut.PresenceAware.Equals(left, right).Should().BeTrue();
        _sut.PresenceAware.GetHashCode(left).Should().Be(_sut.PresenceAware.GetHashCode(right));
    }
}

[TestFixture]
public class Given_CollectionSemanticIdentityEquality_For_An_Unflagged_String_Member
{
    private CollectionSemanticIdentityEquality _sut = null!;

    [SetUp]
    public void Setup()
    {
        _sut = CollectionSemanticIdentityEquality.For(
            CollectionSemanticIdentityEqualityTestPlans.Create(cityUsesSqlServerIdentityCollation: false)
        );
    }

    [Test]
    public void It_keeps_raw_case_variants_distinct()
    {
        _sut.RawValues.Equals(
                CollectionSemanticIdentityEqualityTestPlans.Raw("Austin"),
                CollectionSemanticIdentityEqualityTestPlans.Raw("AUSTIN")
            )
            .Should()
            .BeFalse();
    }

    [Test]
    public void It_keeps_presence_aware_case_variants_distinct()
    {
        _sut.PresenceAware.Equals(
                CollectionSemanticIdentityEqualityTestPlans.Parts(JsonValue.Create("Austin")),
                CollectionSemanticIdentityEqualityTestPlans.Parts(JsonValue.Create("AUSTIN"))
            )
            .Should()
            .BeFalse();
    }

    [Test]
    public void It_agrees_with_the_object_value_array_comparer()
    {
        object?[][] samples =
        [
            CollectionSemanticIdentityEqualityTestPlans.Raw("Austin", 10L, 5, 1.0m),
            CollectionSemanticIdentityEqualityTestPlans.Raw("Austin", 10L, 5, 1.00m),
            CollectionSemanticIdentityEqualityTestPlans.Raw("AUSTIN", 10L, 5, 1.0m),
            CollectionSemanticIdentityEqualityTestPlans.Raw(null),
        ];

        foreach (var left in samples)
        {
            foreach (var right in samples)
            {
                _sut.RawValues.Equals(left, right)
                    .Should()
                    .Be(ObjectValueArrayComparer.Instance.Equals(left, right));
            }
        }
    }

    [Test]
    public void It_agrees_with_the_semantic_identity_key_for_presence_aware_parts()
    {
        ImmutableArray<SemanticIdentityPart>[] samples =
        [
            CollectionSemanticIdentityEqualityTestPlans.Parts(JsonValue.Create("Austin")),
            CollectionSemanticIdentityEqualityTestPlans.Parts(JsonNode.Parse("\"Austin\"")),
            CollectionSemanticIdentityEqualityTestPlans.Parts(JsonValue.Create("AUSTIN")),
            CollectionSemanticIdentityEqualityTestPlans.Parts(null, cityPresent: false),
            CollectionSemanticIdentityEqualityTestPlans.Parts(null, cityPresent: true),
        ];

        foreach (var left in samples)
        {
            foreach (var right in samples)
            {
                _sut.PresenceAware.Equals(left, right)
                    .Should()
                    .Be(SemanticIdentityKeys.BuildKey(left) == SemanticIdentityKeys.BuildKey(right));
            }
        }
    }
}

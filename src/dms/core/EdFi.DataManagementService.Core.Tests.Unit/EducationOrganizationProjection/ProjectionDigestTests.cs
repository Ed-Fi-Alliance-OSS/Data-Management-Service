// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Buffers.Text;
using System.Text;
using EdFi.DataManagementService.Core.EducationOrganizationProjection;
using FluentAssertions;
using NUnit.Framework;
using static EdFi.DataManagementService.Core.EducationOrganizationProjection.ProjectionItemKind;

namespace EdFi.DataManagementService.Core.Tests.Unit.EducationOrganizationProjection;

/// <summary>
/// Golden vectors for the canonical digest form. Each pins the exact canonical text and its SHA-256,
/// computed independently of this implementation, so any drift in the form — ordering, number
/// formatting, length prefixes, null markers, line endings — fails here before it reaches a cursor.
/// </summary>
[TestFixture]
[Parallelizable]
public class Given_The_Projection_Digest_Golden_Vectors
{
    private const string Header = "edorg-projection-digest:v1\n";

    private static ProjectionItem Item(
        long id,
        ProjectionItemKind kind,
        string name,
        string? shortName = null,
        long? parentId = null
    ) => new(id, kind, name, shortName, parentId);

    private static void AssertVector(ProjectionItem[] items, string expectedText, string expectedHex)
    {
        ProjectionDigest.CanonicalText(items).Should().Be(expectedText);
        Convert.ToHexStringLower(ProjectionDigest.Compute(items)).Should().Be(expectedHex);
    }

    [Test]
    public void It_digests_the_empty_set()
    {
        AssertVector([], Header + "0\n", "59132bba34f2b858390ede6a95ca6c40dd16413b5a5a3661687bc46dde839c23");
    }

    [Test]
    public void It_marks_a_null_short_name_and_a_null_parent_with_a_dash()
    {
        AssertVector(
            [Item(1, StateEducationAgency, "State")],
            Header + "1\n1;SEA;5:State;-;-\n",
            "8f1c3eb43df504bede545965b59ec123aab7b7030ce58947d6fdf88c0df8cda5"
        );
    }

    [Test]
    public void It_length_prefixes_names_containing_separators_line_feeds_and_quotes()
    {
        AssertVector(
            [Item(5, LocalEducationAgency, "A;b:c\nd\"e", "x;y")],
            Header + "1\n5;LEA;9:A;b:c\nd\"e;3:x;y;-\n",
            "bc131096e7b5a5baf6c09efec21e9e8a081a96ae3af1771c1badfbcfcad3493f"
        );
    }

    [Test]
    public void It_distinguishes_an_empty_short_name_from_a_null_one()
    {
        AssertVector(
            [Item(7, School, "School", "")],
            Header + "1\n7;SCH;6:School;0:;-\n",
            "a0e4807011ad9a7e48b7304cd6b12db02ac5714b29bc74856b70c13714cd0c0d"
        );
        AssertVector(
            [Item(7, School, "School", null)],
            Header + "1\n7;SCH;6:School;-;-\n",
            "cfd45e27c6ba073dcd77c057b90cd1d45553e0abeec77655f2adfbca63713271"
        );
    }

    [Test]
    public void It_counts_supplementary_characters_in_utf8_bytes()
    {
        string name = string.Concat(Enumerable.Repeat("\U0001F600", 75));

        AssertVector(
            [Item(8, School, name, name)],
            Header + $"1\n8;SCH;300:{name};300:{name};-\n",
            "ffa4cf0c8c9debce55c5db35e1963d64b58dc8983a956d635cfbb7c41747db19"
        );
    }

    [Test]
    public void It_writes_ids_beyond_int32_and_double_precision_exactly()
    {
        AssertVector(
            [
                Item(1, StateEducationAgency, "One"),
                Item(2147483648, EducationServiceCenter, "Two", parentId: 1),
                Item(9007199254740993, LocalEducationAgency, "Three", parentId: 2147483648),
            ],
            Header
                + "3\n"
                + "1;SEA;3:One;-;-\n"
                + "2147483648;ESC;3:Two;-;1\n"
                + "9007199254740993;LEA;5:Three;-;2147483648\n",
            "884e0a75c79d950504173a700fb4f0090e3d78c8c84d4d1f0c63ae495e736a5c"
        );
    }

    [Test]
    public void It_orders_rows_by_id_regardless_of_supplied_order()
    {
        const string ExpectedText = Header + "2\n10;LEA;1:A;-;-\n20;SCH;1:B;-;10\n";
        const string ExpectedHex = "fcca974fcc22f3122757c29fbfd1b1692dede3655d131c8e0af7a091c4272016";

        AssertVector(
            [Item(20, School, "B", parentId: 10), Item(10, LocalEducationAgency, "A")],
            ExpectedText,
            ExpectedHex
        );
        AssertVector(
            [Item(10, LocalEducationAgency, "A"), Item(20, School, "B", parentId: 10)],
            ExpectedText,
            ExpectedHex
        );
    }

    [Test]
    public void It_writes_signed_int64_boundaries_as_ids_and_as_parent_ids()
    {
        AssertVector(
            [
                Item(9223372036854775807, School, "Max", parentId: 0),
                Item(0, LocalEducationAgency, "Zero", parentId: -1),
                Item(-1, EducationServiceCenter, "MinusOne", parentId: long.MinValue),
                Item(long.MinValue, StateEducationAgency, "Min"),
            ],
            Header
                + "4\n"
                + "-9223372036854775808;SEA;3:Min;-;-\n"
                + "-1;ESC;8:MinusOne;-;-9223372036854775808\n"
                + "0;LEA;4:Zero;-;-1\n"
                + "9223372036854775807;SCH;3:Max;-;0\n",
            "40cc24890ec875f7538a390ec593ffcf00ca3a5901a60b85527fa84f341787dc"
        );
    }

    [Test]
    public void It_writes_the_int64_maximum_as_a_parent_id()
    {
        AssertVector(
            [Item(long.MaxValue, LocalEducationAgency, "L"), Item(5, School, "S", parentId: long.MaxValue)],
            Header + "2\n5;SCH;1:S;-;9223372036854775807\n9223372036854775807;LEA;1:L;-;-\n",
            "7b1660c43a5c5a17ebd776024237a0a7d465f9ad671ea290693471c00df402ac"
        );
    }

    [Test]
    public void It_orders_mixed_negative_and_positive_ids_numerically_not_lexically()
    {
        AssertVector(
            [
                Item(10, School, "Ten"),
                Item(-2, StateEducationAgency, "MinusTwo"),
                Item(9, School, "Nine"),
                Item(-10, StateEducationAgency, "MinusTen"),
                Item(0, EducationServiceCenter, "Zero"),
            ],
            Header
                + "5\n"
                + "-10;SEA;8:MinusTen;-;-\n"
                + "-2;SEA;8:MinusTwo;-;-\n"
                + "0;ESC;4:Zero;-;-\n"
                + "9;SCH;4:Nine;-;-\n"
                + "10;SCH;3:Ten;-;-\n",
            "6c76a9bb7a51e35db67b447e86790269d99b5e1d3cce10d36214fd2a320dc961"
        );
    }

    [Test]
    public void It_reproduces_the_contract_worked_example()
    {
        ProjectionItem[] items =
        [
            Item(1, StateEducationAgency, "Example State Department of Education", "ESDE"),
            Item(10, EducationServiceCenter, "Region 10 Education Service Center", "ESC 10", 1),
            Item(100, LocalEducationAgency, "Grand Bend ISD", "GBISD", 10),
            Item(101, LocalEducationAgency, "Grand Bend North ISD", null, 100),
            Item(100001, School, "Grand Bend High School", "GBHS", 100),
            Item(101001, School, "Grand Bend North Elementary School", null, 101),
            Item(900001, School, "Independent Academy"),
        ];

        AssertVector(
            items,
            Header
                + "7\n"
                + "1;SEA;37:Example State Department of Education;4:ESDE;-\n"
                + "10;ESC;34:Region 10 Education Service Center;6:ESC 10;1\n"
                + "100;LEA;14:Grand Bend ISD;5:GBISD;10\n"
                + "101;LEA;20:Grand Bend North ISD;-;100\n"
                + "100001;SCH;22:Grand Bend High School;4:GBHS;100\n"
                + "101001;SCH;34:Grand Bend North Elementary School;-;101\n"
                + "900001;SCH;19:Independent Academy;-;-\n",
            "19ac413cd349c47ac19bf487e7177f2fdb7768ba2f345f8c4290bbfdcfd8bffa"
        );

        ProjectionDigest
            .ToCursorField(ProjectionDigest.Compute(items))
            .Should()
            .Be("GaxBPNNJxHrBm_SH5xd_L9t3aLovNF-MQpC7_c_Yv_o");
    }

    [Test]
    public void It_does_not_depend_on_the_current_culture()
    {
        ProjectionItem[] items = [Item(-1234567, School, "S", parentId: -7654321)];
        string invariantText = ProjectionDigest.CanonicalText(items);

        System.Globalization.CultureInfo original = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            // This culture's negative sign is U+2212, not '-', so culture-sensitive formatting
            // would change the text.
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("sv-SE");
            (-1L).ToString().Should().NotBe("-1", "the case needs a culture whose negative sign differs");
            ProjectionDigest.CanonicalText(items).Should().Be(invariantText);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = original;
        }

        invariantText.Should().Be(Header + "1\n-1234567;SCH;1:S;-;-7654321\n");
    }

    [Test]
    public void It_encodes_the_cursor_field_as_unpadded_base64url()
    {
        byte[] digest = ProjectionDigest.Compute([]);

        string field = ProjectionDigest.ToCursorField(digest);

        field.Should().HaveLength(43).And.NotContain("=");
        Base64Url.DecodeFromChars(field).Should().Equal(digest);
    }
}

/// <summary>
/// A name that is not well-formed UTF-16 must fail the digest rather than be replaced by U+FFFD,
/// because replacement would give two different malformed names identical canonical bytes.
/// </summary>
[TestFixture]
[Parallelizable]
public class Given_A_Name_That_Is_Not_Well_Formed_Utf16
{
    private const char HighSurrogate = '\uD83D';
    private const char LowSurrogate = '\uDE00';

    /// <summary>
    /// Built at run time: a lone surrogate in an attribute argument is stored as UTF-8 and would reach
    /// the test already replaced by U+FFFD.
    /// </summary>
    private static IEnumerable<TestCaseData> MalformedNames()
    {
        (string Label, string Value)[] values =
        [
            ("a_lone_high_surrogate", $"A{HighSurrogate}B"),
            ("a_lone_low_surrogate", $"A{LowSurrogate}B"),
            ("a_trailing_high_surrogate", $"AB{HighSurrogate}"),
            ("a_leading_low_surrogate", $"{LowSurrogate}AB"),
            ("a_reversed_surrogate_pair", $"{LowSurrogate}{HighSurrogate}"),
        ];

        foreach ((string label, string value) in values)
        {
            yield return new TestCaseData(value, false).SetName($"It_rejects_{label}_in_the_name");
            yield return new TestCaseData(value, true).SetName($"It_rejects_{label}_in_the_short_name");
        }
    }

    private static ProjectionItem[] ItemsWith(string malformed, bool inShortName) =>
        [
            new(1, ProjectionItemKind.StateEducationAgency, "Valid", null, null),
            inShortName
                ? new(2, ProjectionItemKind.School, "Valid", malformed, 1)
                : new(2, ProjectionItemKind.School, malformed, "Valid", 1),
        ];

    [TestCaseSource(nameof(MalformedNames))]
    public void It_fails_the_digest(string malformed, bool inShortName)
    {
        ProjectionItem[] items = ItemsWith(malformed, inShortName);

        Action compute = () => ProjectionDigest.Compute(items);

        compute.Should().Throw<EncoderFallbackException>();
    }

    [TestCaseSource(nameof(MalformedNames))]
    public void It_fails_the_canonical_text(string malformed, bool inShortName)
    {
        ProjectionItem[] items = ItemsWith(malformed, inShortName);

        Action canonicalText = () => ProjectionDigest.CanonicalText(items);

        canonicalText.Should().Throw<EncoderFallbackException>();
    }

    [Test]
    public void It_still_digests_a_well_formed_surrogate_pair()
    {
        string pair = new([HighSurrogate, LowSurrogate]);

        ProjectionDigest
            .CanonicalText([new ProjectionItem(3, ProjectionItemKind.School, pair, null, null)])
            .Should()
            .Be($"edorg-projection-digest:v1\n1\n3;SCH;4:{pair};-;-\n");
    }

    [Test]
    public void It_digests_a_literal_replacement_character_as_three_utf8_bytes()
    {
        ProjectionItem[] items = [new(9, ProjectionItemKind.School, "A\uFFFDB", "\uFFFD", null)];

        ProjectionDigest
            .CanonicalText(items)
            .Should()
            .Be("edorg-projection-digest:v1\n1\n9;SCH;5:A\uFFFDB;3:\uFFFD;-\n");
        Convert
            .ToHexStringLower(ProjectionDigest.Compute(items))
            .Should()
            .Be("5b37236081226e6c166caddd4ebbdce8b811eb629d7291fdbc00e0227c5d8250");
    }
}

/// <summary>
/// The order rows are hashed in, which is part of the digest's contract: ascending id, and equal ids
/// in the order supplied (a stable sort). A sequence already in id order is hashed as supplied and
/// any other is sorted, so both paths are pinned, each with a pair of equal ids.
/// </summary>
[TestFixture]
[Parallelizable]
public class Given_The_Projection_Digest_Row_Order
{
    private static ProjectionItem Item(long id, string name) =>
        new(id, ProjectionItemKind.School, name, null, null);

    [Test]
    public void It_keeps_equal_ids_in_supplied_order_when_already_in_id_order()
    {
        ProjectionItem[] items = [Item(1, "One"), Item(2, "Second"), Item(2, "First"), Item(3, "Three")];

        ProjectionDigest
            .CanonicalText(items)
            .Should()
            .Be(
                "edorg-projection-digest:v1\n4\n1;SCH;3:One;-;-\n2;SCH;6:Second;-;-\n2;SCH;5:First;-;-\n3;SCH;5:Three;-;-\n"
            );
    }

    [Test]
    public void It_keeps_equal_ids_in_supplied_order_when_sorting()
    {
        ProjectionItem[] items = [Item(3, "Three"), Item(2, "Second"), Item(1, "One"), Item(2, "First")];

        ProjectionDigest
            .CanonicalText(items)
            .Should()
            .Be(
                "edorg-projection-digest:v1\n4\n1;SCH;3:One;-;-\n2;SCH;6:Second;-;-\n2;SCH;5:First;-;-\n3;SCH;5:Three;-;-\n"
            );
    }

    [Test]
    public void It_hashes_a_sorted_and_an_unsorted_supply_of_the_same_rows_identically()
    {
        ProjectionItem[] sorted = [Item(-5, "A"), Item(0, "B"), Item(7, "C")];
        ProjectionItem[] unsorted = [Item(7, "C"), Item(-5, "A"), Item(0, "B")];

        ProjectionDigest.Compute(unsorted).Should().Equal(ProjectionDigest.Compute(sorted));
    }

    [Test]
    public void It_returns_a_sequence_already_in_id_order_as_supplied() =>
        ProjectionDigest
            .InAscendingIdOrder([Item(1, "A"), Item(1, "B"), Item(2, "C")])
            .Select(item => item.NameOfInstitution)
            .Should()
            .Equal("A", "B", "C");
}

/// <summary>
/// Every page hashes the whole set, so the digest's own allocations must not grow with the number of
/// rows. Formatting each row into a string and a byte array cost about 470 bytes a row; 10,000 rows
/// would allocate several megabytes.
/// </summary>
[TestFixture]
public class Given_The_Projection_Digest_Of_A_Large_Set
{
    [Test]
    public void It_allocates_a_bounded_amount_whatever_the_row_count()
    {
        ProjectionItem[] items =
        [
            .. Enumerable
                .Range(1, 10_000)
                .Select(id => new ProjectionItem(
                    id,
                    ProjectionItemKind.School,
                    $"Independent School {id:D9}",
                    "Short",
                    id + 100_000
                )),
        ];

        // Warm up so first-call costs are not counted.
        ProjectionDigest.Compute(items);

        long before = GC.GetAllocatedBytesForCurrentThread();
        ProjectionDigest.Compute(items);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        allocated.Should().BeLessThan(64 * 1024);
    }
}

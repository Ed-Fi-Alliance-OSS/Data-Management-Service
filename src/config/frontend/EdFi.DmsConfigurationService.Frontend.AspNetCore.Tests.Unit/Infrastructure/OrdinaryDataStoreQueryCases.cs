// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using EdFi.DmsConfigurationService.DataModel.Model;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Infrastructure;

/// <summary>
/// Shared inputs and independently specified row positions for real-provider repository and HTTP tests.
/// The 0039 lane retains legacy uniqueness. The 0040 lane uses distinct canonical
/// names where legacy rows could tie, and also exercises formerly linguistic equivalents.
/// </summary>
public static class OrdinaryDataStoreQueryCases
{
    public sealed record Row(string Name, string Type = "SQL");

    public sealed record Case(
        string Label,
        Row[] Rows,
        DataStoreQuery Query,
        int[] ExpectedRows,
        int? IdRow = null
    )
    {
        public DataStoreQuery Bind(int[] ids) =>
            new()
            {
                Id = IdRow is int index ? ids[index] : Query.Id,
                Name = Query.Name,
                DataStoreType = Query.DataStoreType,
                OrderBy = Query.OrderBy,
                Direction = Query.Direction,
                Offset = Query.Offset,
                Limit = Query.Limit,
            };

        public override string ToString() => Label;
    }

    public static string Url(DataStoreQuery query)
    {
        List<KeyValuePair<string, string?>> values =
        [
            new("id", query.Id?.ToString(CultureInfo.InvariantCulture)),
            new("name", query.Name),
            new("dataStoreType", query.DataStoreType),
            new("orderBy", query.OrderBy),
            new("direction", query.Direction),
            new("offset", query.Offset?.ToString(CultureInfo.InvariantCulture)),
            new("limit", query.Limit?.ToString(CultureInfo.InvariantCulture)),
        ];
        return "/v3/dataStores/?"
            + string.Join(
                "&",
                values
                    .Where(v => v.Value is not null)
                    .Select(v => v.Key + "=" + Uri.EscapeDataString(v.Value!))
            );
    }

    public static Case WithCanonicalUniqueness(Case test)
    {
        // 0040 prohibits the two legacy within-tenant name ties. Keep every sort/page
        // case with a distinct adjacent key; the 0039 lane still proves real name ties.
        // Type/Id tie cases are unchanged in both lanes.
        if (test.Rows.Length == 9 && test.Rows[6].Name == "\tTie")
        {
            Row[] rows = [.. test.Rows];
            rows[7] = new("\u3000tiebreak");
            return test with
            {
                Rows = rows,
                ExpectedRows = test.Label == "canonical_name_filter_keeps_Id_ties" ? [6] : test.ExpectedRows,
            };
        }
        // Exercise each formerly linguistic equivalent in the same catalog after 0040.
        if (test.Rows is [{ Name: "café" }, { Name: "other" }])
        {
            return test with
            {
                Rows = [.. test.Rows, new("cafe"), new("CAFÉ"), new("cafe\u0301")],
                ExpectedRows = test.Label switch
                {
                    "accent_is_not_folded" => [2],
                    "non_ASCII_case_is_not_folded" => [3],
                    "composed_is_not_decomposed" => [4],
                    _ => test.ExpectedRows,
                },
            };
        }
        return test;
    }

    public static IEnumerable<Case> Cases()
    {
        Row[] accent = [new("café"), new("other", "sql")];
        yield return new("accent_is_not_folded", accent, new() { Name = "cafe" }, []);
        yield return new("encoded_accent_matches", accent, new() { Name = "café" }, [0]);
        yield return new("ASCII_case_alias", accent, new() { Name = "CAFé" }, [0]);
        yield return new("non_ASCII_case_is_not_folded", accent, new() { Name = "CAFÉ" }, []);
        yield return new("composed_is_not_decomposed", accent, new() { Name = "cafe\u0301" }, []);
        yield return new("decomposed_is_preserved", [new("cafe\u0301")], new() { Name = "CAFE\u0301" }, [0]);
        const string whitespace =
            "\u0009\u000A\u000B\u000C\u000D\u0020\u0085\u00A0\u1680\u2000\u2001\u2002\u2003\u2004\u2005\u2006\u2007\u2008\u2009\u200A\u2028\u2029\u202F\u205F\u3000";
        yield return new(
            "all_fixed_edges_are_trimmed",
            [new(whitespace + "CAFé" + whitespace)],
            new() { Name = whitespace + "café" + whitespace },
            [0]
        );
        yield return new("zero_width_space_is_not_trimmed", accent, new() { Name = "\u200Bcafé" }, []);
        yield return new("empty_is_an_equality_filter", [new(""), new("other")], new() { Name = "" }, [0]);
        yield return new(
            "whitespace_only_canonicalizes_to_empty",
            [new(""), new("other")],
            new() { Name = whitespace },
            [0]
        );
        yield return new("no_match_is_empty", accent, new() { Name = "absent" }, []);
        yield return new(
            "filters_intersect",
            accent,
            new()
            {
                Name = " CAFé ",
                DataStoreType = "SQL",
                Limit = 1,
            },
            [0],
            0
        );
        yield return new("id_and_name_must_both_match", accent, new() { Name = "café" }, [], 1);
        yield return new(
            "name_and_type_must_both_match",
            accent,
            new() { Name = "café", DataStoreType = "sql" },
            []
        );
        yield return new("filter_precedes_offset", accent, new() { Name = "café", Offset = 1 }, []);

        Row[] types = [new("first", "SQL"), new("second", "sql"), new("third", "SQL "), new("fourth", "SQL")];
        yield return new("type_case_is_exact", types, new() { DataStoreType = "sql" }, [1]);
        yield return new("type_uppercase_is_exact", types, new() { DataStoreType = "SQL" }, [0, 3]);
        yield return new("type_trailing_space_is_exact", types, new() { DataStoreType = "SQL " }, [2]);
        yield return new("type_leading_space_is_exact", types, new() { DataStoreType = " SQL" }, []);
        yield return new("empty_type_is_not_omitted", types, new() { DataStoreType = "" }, []);
        yield return new(
            "type_filter_precedes_page",
            types,
            new()
            {
                DataStoreType = "SQL",
                Offset = 1,
                Limit = 1,
            },
            [3]
        );
        yield return new(
            "all_filters_precede_page",
            types,
            new()
            {
                Name = "FOURTH",
                DataStoreType = "SQL",
                Limit = 1,
            },
            [3],
            3
        );

        string maximumName = new('x', 256);
        yield return new("maximum_name_matches", [new(maximumName)], new() { Name = maximumName }, [0]);
        yield return new(
            "name_257_is_not_truncated",
            [new(maximumName)],
            new() { Name = maximumName + "y" },
            []
        );
        yield return new(
            "name_over_4000_is_not_truncated",
            [new(maximumName)],
            new() { Name = maximumName + new string('y', 4000) },
            []
        );
        yield return new(
            "long_edges_are_trimmed_before_equality",
            accent,
            new() { Name = new string(' ', 4100) + "CAFé" },
            [0]
        );
        yield return new(
            "type_51_is_not_truncated",
            [new("longtype", new string('x', 50))],
            new() { DataStoreType = new string('x', 51) },
            []
        );
        yield return new(
            "type_over_4000_is_not_truncated",
            types,
            new() { DataStoreType = "SQL" + new string(' ', 4100) },
            []
        );

        Row[] names =
        [
            new("z"),
            new("aa"),
            new("\uE000-bmp"),
            new("\U00010000-supplementary"),
            new("B"),
            new(" \tAlpha\u3000"),
            new("\tTie"),
            new("\u3000tie"),
            new("café"),
        ];
        int[] nameOrder = [1, 5, 4, 8, 6, 7, 0, 2, 3];
        yield return new("name_UTF8_order_and_Id_ties", names, new() { OrderBy = "name" }, nameOrder);
        yield return new(
            "name_descending_reverses_Id_ties",
            names,
            new() { OrderBy = "NaMe", Direction = "DeScEnDiNg" },
            [3, 2, 0, 7, 6, 8, 4, 5, 1]
        );
        yield return new(
            "name_page_crosses_tie",
            names,
            new()
            {
                OrderBy = "name",
                Offset = 3,
                Limit = 3,
            },
            [8, 6, 7]
        );
        yield return new(
            "name_descending_page_crosses_tie",
            names,
            new()
            {
                OrderBy = "name",
                Direction = "desc",
                Offset = 3,
                Limit = 2,
            },
            [7, 6]
        );
        yield return new(
            "canonical_name_filter_keeps_Id_ties",
            names,
            new()
            {
                Name = "TIE",
                OrderBy = "name",
                Direction = "desc",
                Limit = 1,
            },
            [7]
        );
        yield return new(
            "name_maximum_UTF8_bytes_are_not_truncated",
            [new(new string('界', 255) + "b"), new(new string('界', 255) + "a")],
            new() { OrderBy = "name" },
            [1, 0]
        );
        yield return new(
            "supplementary_name_bytes_are_not_truncated",
            [
                new(string.Concat(Enumerable.Repeat("😀", 127)) + "b"),
                new(string.Concat(Enumerable.Repeat("😀", 127)) + "a"),
            ],
            new() { OrderBy = "name" },
            [1, 0]
        );

        string longType = new('界', 49);
        Row[] typeSort =
        [
            new("n0", "sql"),
            new("n1", "SQL "),
            new("n2", "SQL"),
            new("n3", "SQL"),
            new("n4", "\uE000"),
            new("n5", "\U00010000"),
            new("n6", "aa"),
            new("n7", "z"),
            new("n8", longType + "b"),
            new("n9", longType + "a"),
        ];
        yield return new(
            "type_UTF8_order_exact_spaces_and_Id_ties",
            typeSort,
            new() { OrderBy = "dataStoreType", Direction = "AsCeNdInG" },
            [2, 3, 1, 6, 0, 7, 9, 8, 4, 5]
        );
        yield return new(
            "type_descending_reverses_Id_ties",
            typeSort,
            new() { OrderBy = "DATASTORETYPE", Direction = "DESC" },
            [5, 4, 8, 9, 7, 0, 6, 1, 3, 2]
        );
        yield return new(
            "type_page_crosses_tie",
            typeSort,
            new()
            {
                OrderBy = "dataStoreType",
                Offset = 1,
                Limit = 2,
            },
            [3, 1]
        );
        yield return new(
            "type_descending_page_crosses_tie",
            typeSort,
            new()
            {
                OrderBy = "dataStoreType",
                Direction = "descending",
                Offset = 8,
                Limit = 2,
            },
            [3, 2]
        );

        Row[] many = Enumerable.Range(0, 105).Select(i => new Row($"store-{i:D3}")).ToArray();
        yield return new("default_has_no_implicit_limit", many, new(), Enumerable.Range(0, 105).ToArray());
        yield return new(
            "direction_alone_keeps_Id_ascending",
            many,
            new() { Direction = "desc" },
            Enumerable.Range(0, 105).ToArray()
        );
        yield return new(
            "offset_only_is_unbounded",
            many,
            new() { Offset = 2 },
            Enumerable.Range(2, 103).ToArray()
        );
        yield return new("limit_only_starts_at_zero", many, new() { Limit = 2 }, [0, 1]);
        yield return new(
            "Id_descending_page",
            many,
            new()
            {
                OrderBy = "ID",
                Direction = "descending",
                Offset = 1,
                Limit = 2,
            },
            [103, 102]
        );
        yield return new(
            "Id_ascending_alias",
            many,
            new()
            {
                OrderBy = "id",
                Direction = "ASC",
                Limit = 2,
            },
            [0, 1]
        );
        yield return new("offset_past_end_is_empty", many, new() { Offset = 105 }, []);
    }
}

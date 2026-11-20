// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.ManagedDataStores;

/// <summary>The same frozen corpus and boundary cases run against both real database providers.</summary>
public static class CatalogNameComparisonCases
{
    public static IEnumerable<TestCaseData> Vectors()
    {
        using Stream stream = typeof(CatalogNameComparisonCases).Assembly.GetManifestResourceStream(
            "DMS1439.ConformanceFixtures.json"
        )!;
        using JsonDocument document = JsonDocument.Parse(stream);
        JsonElement names = document.RootElement.GetProperty("canonicalNames");
        int index = 0;
        foreach (JsonElement vector in names.GetProperty("vectors").EnumerateArray())
        {
            yield return new TestCaseData(
                vector.GetProperty("input").GetString()!,
                vector.GetProperty("canonical").GetString()!
            ).SetName($"It_computes_shared_vector_{index++}");
        }

        foreach (JsonElement codePoint in names.GetProperty("trimCodePoints").EnumerateArray())
        {
            char whitespace = (char)codePoint.GetInt32();
            yield return new TestCaseData($"{whitespace}Ab{whitespace}", "ab").SetName(
                $"It_trims_edge_U{(int)whitespace:X4}"
            );
            yield return new TestCaseData($"A{whitespace}B", $"a{whitespace}b").SetName(
                $"It_preserves_embedded_U{(int)whitespace:X4}"
            );
        }

        yield return new TestCaseData("", "").SetName("It_preserves_empty_names");
        yield return new TestCaseData(" \t\r\n\u3000", "").SetName("It_trims_whitespace_only_names");
        yield return new TestCaseData("IİıiÉéΣσß", "iİıiÉéΣσß").SetName("It_folds_only_ASCII_letters");
        yield return new TestCaseData(new string('A', 256), new string('a', 256)).SetName(
            "It_preserves_256_UTF16_units"
        );
        yield return new TestCaseData(new string('A', 254) + "😀", new string('a', 254) + "😀").SetName(
            "It_preserves_supplementary_characters_at_the_UTF16_limit"
        );
        yield return new TestCaseData(" " + new string('Z', 254) + "\u3000", new string('z', 254)).SetName(
            "It_trims_maximum_length_names_without_narrowing"
        );
        yield return new TestCaseData(new string('A', 100) + "X", new string('a', 100) + "x").SetName(
            "It_preserves_long_ordinary_names"
        );
    }

    public static IEnumerable<TestCaseData> Pairs()
    {
        using Stream stream = typeof(CatalogNameComparisonCases).Assembly.GetManifestResourceStream(
            "DMS1439.ConformanceFixtures.json"
        )!;
        using JsonDocument document = JsonDocument.Parse(stream);
        int index = 0;
        foreach (
            JsonElement pair in document
                .RootElement.GetProperty("canonicalNames")
                .GetProperty("pairs")
                .EnumerateArray()
        )
        {
            yield return new TestCaseData(
                pair.GetProperty("left").GetString()!,
                pair.GetProperty("right").GetString()!,
                pair.GetProperty("equal").GetBoolean()
            ).SetName($"It_matches_shared_equality_pair_{index++}");
        }
        yield return new TestCaseData("a", "aa", false).SetName("It_distinguishes_prefix_lengths");
        yield return new TestCaseData(new string('a', 255) + "X", new string('a', 255) + "Y", false).SetName(
            "It_compares_the_final_character_of_long_names"
        );
    }
}

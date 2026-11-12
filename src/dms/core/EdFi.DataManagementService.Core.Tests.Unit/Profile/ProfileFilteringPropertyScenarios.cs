// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.Text.Json.Nodes;
using CsCheck;
using EdFi.DataManagementService.Core.Profile;

namespace EdFi.DataManagementService.Core.Tests.Unit.Profile;

internal static class ProfileFilteringPropertyScenarios
{
    internal sealed record Item(bool Visible, int Value, ImmutableArray<int> Children);

    internal sealed record Scenario(int SchoolId, string Text, ImmutableArray<Item> Items);

    public static Gen<Scenario> Generate(string shape) =>
        from id in Gen.Int[1, int.MaxValue]
        from text in Gen.Int[0, 4].Select(i => new[] { "", "雪é", "\"\\", "line\nbreak", "ordinary" }[i])
        from items in (
            from visible in Gen.Bool
            from value in Gen.Int
            from children in Gen.Int.Array[0, 3]
            select new Item(visible, value, children.ToImmutableArray())
        ).Array[shape == "empty" ? 0 : 2, shape == "empty" ? 0 : 6]
        select new Scenario(
            id,
            text,
            items
                .Select(
                    (item, index) =>
                        item with
                        {
                            Visible = shape switch
                            {
                                "hidden" => false,
                                "visible" => true,
                                "mixed" => index % 2 == 0,
                                _ => item.Visible,
                            },
                        }
                )
                .ToImmutableArray()
        );

    public static ContentTypeDefinition BuildProfile(MemberSelection mode, FilterMode filterMode)
    {
        IReadOnlyList<PropertyRule> Fields(params string[] included) =>
            mode == MemberSelection.IncludeOnly
                ? included.Select(n => new PropertyRule(n)).ToArray()
                : [new("secret"), new("kind")];
        var children = new CollectionRule(
            "children",
            mode,
            null,
            Fields("childId", "value"),
            null,
            null,
            null,
            null
        );
        var items = new CollectionRule(
            "items",
            mode,
            null,
            Fields("itemId", "value"),
            null,
            [children],
            null,
            new("kind", filterMode, [filterMode == FilterMode.IncludeOnly ? "shown" : "hidden"])
        );
        return new(
            MemberSelection.IncludeOnly,
            [new("value")],
            [new("details", mode, null, Fields("value"), null, null, null)],
            [items],
            []
        );
    }

    public static IReadOnlyList<CompiledScopeDescriptor> Scopes =>
        [
            new(
                "$",
                ScopeKind.Root,
                null,
                [],
                [],
                [
                    "schoolId",
                    "schoolReference.schoolId",
                    "schoolReference.secret",
                    "value",
                    "secret",
                    "id",
                    "_etag",
                    "_lastModifiedDate",
                ]
            ),
            new("$.details", ScopeKind.NonCollection, "$", [], [], ["value", "secret"]),
            new(
                "$.items[*]",
                ScopeKind.Collection,
                "$",
                [],
                ["itemId"],
                ["itemId", "kind", "value", "secret"]
            ),
            new(
                "$.items[*].children[*]",
                ScopeKind.Collection,
                "$.items[*]",
                ["$.items[*]"],
                ["childId"],
                ["childId", "value", "secret"]
            ),
            new("$.privateItems[*]", ScopeKind.Collection, "$", [], ["privateId"], ["privateId", "secret"]),
        ];

    public static JsonObject Source(Scenario scenario) =>
        new()
        {
            ["schoolId"] = scenario.SchoolId,
            ["schoolReference"] = new JsonObject
            {
                ["schoolId"] = scenario.SchoolId,
                ["secret"] = scenario.Text,
            },
            ["id"] = $"id-{scenario.SchoolId}",
            ["_etag"] = $"\"{scenario.SchoolId}\"",
            ["_lastModifiedDate"] = "2026-11-08T12:00:00Z",
            ["value"] = scenario.Text,
            ["secret"] = scenario.Text,
            ["details"] = new JsonObject { ["value"] = scenario.Text, ["secret"] = scenario.Text },
            ["privateItems"] = new JsonArray(
                new JsonObject { ["privateId"] = 1, ["secret"] = scenario.Text }
            ),
            ["items"] = new JsonArray(
                scenario
                    .Items.Select(
                        (item, index) =>
                            (JsonNode)
                                new JsonObject
                                {
                                    ["itemId"] = index,
                                    ["kind"] = item.Visible ? "shown" : "hidden",
                                    ["value"] = item.Value,
                                    ["secret"] = scenario.Text,
                                    ["children"] = new JsonArray(
                                        item.Children.Select(
                                                (value, childId) =>
                                                    (JsonNode)
                                                        new JsonObject
                                                        {
                                                            ["childId"] = childId,
                                                            ["value"] = value,
                                                            ["secret"] = scenario.Text,
                                                        }
                                            )
                                            .ToArray()
                                    ),
                                }
                    )
                    .ToArray()
            ),
        };

    // Build the oracle from the immutable description, never by filtering the subject's JSON.
    public static JsonObject Expected(Scenario scenario, bool readable)
    {
        var expected = new JsonObject
        {
            ["schoolId"] = scenario.SchoolId,
            ["schoolReference"] = new JsonObject { ["schoolId"] = scenario.SchoolId },
            ["value"] = scenario.Text,
            ["details"] = new JsonObject { ["value"] = scenario.Text },
        };
        if (readable)
        {
            expected["schoolReference"]!["secret"] = scenario.Text;
            expected["id"] = $"id-{scenario.SchoolId}";
            expected["_etag"] = $"\"{scenario.SchoolId}\"";
            expected["_lastModifiedDate"] = "2026-11-08T12:00:00Z";
        }
        var items = new JsonArray();
        foreach (
            var (item, index) in scenario
                .Items.Select((item, index) => (item, index))
                .Where(x => x.item.Visible)
        )
        {
            var projected = new JsonObject { ["itemId"] = index, ["value"] = item.Value };
            if (!readable || item.Children.Length > 0)
            {
                projected["children"] = new JsonArray(
                    item.Children.Select(
                            (value, childId) =>
                                (JsonNode)new JsonObject { ["childId"] = childId, ["value"] = value }
                        )
                        .ToArray()
                );
            }
            items.Add(projected);
        }
        // Readable projection omits empty collections; writable shaping retains them.
        if (!readable || items.Count > 0)
        {
            expected["items"] = items;
        }
        return expected;
    }
}

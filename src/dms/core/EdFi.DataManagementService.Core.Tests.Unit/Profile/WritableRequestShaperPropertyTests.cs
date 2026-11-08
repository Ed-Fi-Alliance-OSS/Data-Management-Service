// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using System.Text.Json.Nodes;
using CsCheck;
using EdFi.DataManagementService.Core.Profile;
using FluentAssertions;
using NUnit.Framework;
using static EdFi.DataManagementService.Core.Tests.Unit.Profile.ProfileFilteringPropertyScenarios;

namespace EdFi.DataManagementService.Core.Tests.Unit.Profile;

[TestFixture]
[Category("ProfileProperty")]
public class Given_Generated_Writable_Profile_Requests
{
    [Test]
    public void It_shapes_visible_members_and_reports_filtered_items_without_mutating_input(
        [Values("empty", "hidden", "visible", "mixed")] string shape,
        [Values(MemberSelection.IncludeOnly, MemberSelection.ExcludeOnly)] MemberSelection mode,
        [Values(FilterMode.IncludeOnly, FilterMode.ExcludeOnly)] FilterMode filterMode
    )
    {
        Generate(shape)
            .Sample(
                scenario =>
                {
                    var source = Source(scenario);
                    var before = source.ToJsonString();
                    var scopes = Scopes;
                    var shaper = new WritableRequestShaper(
                        new ProfileVisibilityClassifier(BuildProfile(mode, filterMode), scopes),
                        new AddressDerivationEngine(scopes),
                        "GeneratedProfile",
                        "School",
                        "PUT",
                        "write",
                        ["$.schoolId", "$.schoolReference.schoolId"]
                    );
                    var result = shaper.Shape(source);
                    var expected = Expected(scenario, readable: false);
                    JsonNode
                        .DeepEquals(result.WritableRequestBody, expected)
                        .Should()
                        .BeTrue($"expected {expected}, got {result.WritableRequestBody}");

                    var visible = scenario
                        .Items.Select((item, index) => (item, index))
                        .Where(x => x.item.Visible)
                        .ToArray();
                    var parents = result
                        .VisibleRequestCollectionItems.Where(i => i.Address.JsonScope == "$.items[*]")
                        .ToArray();
                    parents
                        .Select(i => i.Address.SemanticIdentityInOrder.Single().Value!.GetValue<int>())
                        .Should()
                        .Equal(visible.Select(x => x.index));
                    parents
                        .Select(i => i.RequestJsonPath)
                        .Should()
                        .Equal(visible.Select(x => $"$.items[{x.index}]"));
                    result
                        .VisibleRequestCollectionItems.Select(i => i.Creatable)
                        .Should()
                        .Equal(Enumerable.Repeat(false, visible.Sum(x => 1 + x.item.Children.Length)));
                    result
                        .VisibleRequestCollectionItems.Length.Should()
                        .Be(visible.Sum(x => 1 + x.item.Children.Length));
                    foreach (var (item, index) in visible)
                    {
                        var children = result
                            .VisibleRequestCollectionItems.Where(i =>
                                i.RequestJsonPath.StartsWith(
                                    $"$.items[{index}].children[",
                                    StringComparison.Ordinal
                                )
                            )
                            .ToArray();
                        children
                            .Select(i => i.Address.SemanticIdentityInOrder.Single().Value!.GetValue<int>())
                            .Should()
                            .Equal(Enumerable.Range(0, item.Children.Length));
                        children
                            .Select(i =>
                                i.Address.ParentAddress.AncestorCollectionInstances.Single()
                                    .SemanticIdentityInOrder.Single()
                                    .Value!.GetValue<int>()
                            )
                            .Should()
                            .Equal(Enumerable.Repeat(index, item.Children.Length));
                    }
                    result
                        .ValidationFailures.Select(f =>
                            f.Should()
                                .BeOfType<CollectionValueFilterViolationWritableProfileValidationFailure>()
                                .Subject
                        )
                        .SelectMany(f => f.RequestJsonPaths)
                        .Should()
                        .Equal(
                            scenario
                                .Items.Select((item, index) => (item, index))
                                .Where(x => !x.item.Visible)
                                .Select(x => $"$.items[{x.index}]")
                        );
                    source.ToJsonString().Should().Be(before);
                    result.WritableRequestBody["schoolReference"]!["schoolId"] = -1;
                    result.WritableRequestBody["details"]!["value"] = "changed";
                    source.ToJsonString().Should().Be(before);
                },
                iter: 100,
                threads: 1,
                print: s => JsonSerializer.Serialize(s)
            );
        TestContext.Out.WriteLine($"CsCheck 5.0.1: {shape}, {mode}, {filterMode}: 100 samples");
    }
}

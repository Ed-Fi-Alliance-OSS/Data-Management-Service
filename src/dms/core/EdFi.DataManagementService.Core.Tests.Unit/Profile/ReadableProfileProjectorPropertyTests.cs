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
public class Given_Generated_Readable_Profile_Documents
{
    [Test]
    public void It_filters_before_removing_members_and_preserves_identity_metadata_and_input(
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
                    var expected = Expected(scenario, readable: true);
                    var result = new ReadableProfileProjector().Project(
                        source,
                        BuildProfile(mode, filterMode),
                        new HashSet<string> { "schoolId", "schoolReference" }
                    );
                    JsonNode
                        .DeepEquals(result, expected)
                        .Should()
                        .BeTrue($"expected {expected}, got {result}");
                    source.ToJsonString().Should().Be(before);
                    // Mutating nested output proves the result owns its JSON nodes.
                    result["schoolReference"]!["schoolId"] = -1;
                    result["details"]!["value"] = "changed";
                    source.ToJsonString().Should().Be(before);
                    // No unconditional idempotence assertion: 'kind' was removed after filtering.
                },
                iter: 100,
                threads: 1,
                print: s => JsonSerializer.Serialize(s)
            );
        TestContext.Out.WriteLine($"CsCheck 5.0.1: {shape}, {mode}, {filterMode}: 100 samples");
    }
}

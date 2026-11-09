// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Paging;
using FluentAssertions;
using NUnit.Framework;
using static EdFi.DataManagementService.Core.Tests.Unit.Paging.CursorPagingPropertyScenarios;

namespace EdFi.DataManagementService.Core.Tests.Unit.Paging;

[TestFixture]
[Category("CursorPagingProperty")]
public class Given_Generated_Change_Query_Windows
{
    [Test]
    public void It_uses_window_presence_and_source_kind_and_validates_the_resulting_anchor(
        [Values] EffectiveTargetKind target,
        [Values] bool legacy,
        [Values("absent", "empty", "minimum", "maximum", "both")] string shape
    )
    {
        Check(
            Generate(),
            bounds =>
            {
                var policy = new ChangeQueryPageOrderingPolicy(legacy);
                // Expected modes are a window-shape table, independent of numeric comparisons.
                var expectedLive =
                    !legacy && shape is "maximum" or "both"
                        ? PageOrderingMode.ContentVersion
                        : PageOrderingMode.DocumentId;
                var expectedSnapshot =
                    !legacy && shape is "minimum" or "maximum" or "both"
                        ? PageOrderingMode.ContentVersion
                        : PageOrderingMode.DocumentId;
                var expected = target == EffectiveTargetKind.Snapshot ? expectedSnapshot : expectedLive;
                // Changing magnitude, sign or range direction must not change the anchor.
                foreach (
                    var values in new[]
                    {
                        bounds,
                        new Bounds(long.MinValue, long.MaxValue),
                        new Bounds(long.MaxValue, long.MinValue),
                        new Bounds(0, 0),
                    }
                )
                {
                    var window = shape switch
                    {
                        "absent" or "empty" => ChangeVersionRange.None,
                        "minimum" => new ChangeVersionRange(values.Minimum, null),
                        "maximum" => new ChangeVersionRange(null, values.Maximum),
                        "both" => new ChangeVersionRange(values.Minimum, values.Maximum),
                        _ => throw new InvalidOperationException($"Unknown case: {shape}"),
                    };
                    policy.ResolveForLiveQuery(shape == "absent" ? null : window).Should().Be(expectedLive);
                    policy
                        .ResolveForSnapshotQuery(shape == "absent" ? null : window)
                        .Should()
                        .Be(expectedSnapshot);
                    var resolved = policy.ResolveFor(shape == "absent" ? null : window, target);
                    resolved.Should().Be(expected);
                    // Token bounds need not equal change-version filters: partitions carry their own ceilings.
                    var query = new Dictionary<string, string> { ["pageToken"] = Token(bounds, expected) };
                    CursorRequestValidator
                        .Validate(query, 500, resolved)
                        .Should()
                        .Be(
                            new CursorValidationResult.Valid(
                                new(new(bounds.Minimum, bounds.Maximum), new(500))
                            )
                        );
                    query["pageToken"] = Token(
                        bounds,
                        expected == PageOrderingMode.DocumentId
                            ? PageOrderingMode.ContentVersion
                            : PageOrderingMode.DocumentId
                    );
                    CursorRequestValidator
                        .Validate(query, 500, resolved)
                        .Should()
                        .Be(new CursorValidationResult.Invalid(CursorRequestValidator.InvalidPageToken));
                }
            },
            $"target={target}, legacy={legacy}, shape={shape}"
        );
    }
}

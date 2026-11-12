// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using CsCheck;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Profile;
using EdFi.DataManagementService.Core.Profile;
using FluentAssertions;
using NUnit.Framework;
using static EdFi.DataManagementService.Backend.Tests.Unit.Profile.ProfileCollectionMergeTestDoubles;

namespace EdFi.DataManagementService.Backend.Tests.Unit.Profile;

[TestFixture]
[Category("ProfileProperty")]
public class Given_Generated_Profile_Collection_Plans
{
    // Only immutable descriptions are generated. Every assertion (including shrinking)
    // builds fresh JSON identities, candidates and stored snapshots.
    private sealed record Row(bool Visible, bool Keep, int Priority, bool Creatable);

    private sealed record Scenario(
        ImmutableArray<Row> Rows,
        ImmutableArray<int> InsertPriorities,
        int ParentId,
        bool Nested,
        string Payload
    );

    private static Gen<Scenario> Scenarios(string shape) =>
        from rows in Gen.Select(Gen.Bool, Gen.Bool, Gen.Int[0, 20], Gen.Bool)
            .Select(t => new Row(t.Item1, t.Item2, t.Item3, t.Item4))
            .Array[shape == "empty" ? 0 : 3, shape == "empty" ? 0 : 8]
        from inserts in Gen.Int[0, 20].Array[
            shape == "surplus" ? 9 : 0,
            shape is "empty" or "omitted" ? 0 : 10
        ]
        from parent in Gen.Int[1, 1000]
        from nested in Gen.Bool
        from payload in Gen.Int[0, 4].Select(i => new[] { "", "é雪", "a|b=c", "\"\\", "line\nend" }[i])
        select new Scenario(
            rows.Select(
                    (row, index) =>
                        row with
                        {
                            Visible = shape switch
                            {
                                "hidden" => false,
                                "visible" => true,
                                "mixed" or "surplus" or "omitted" => index % 2 == 1
                                    && index < rows.Length - 1,
                                _ => row.Visible,
                            },
                            Keep = shape != "omitted" && row.Keep,
                        }
                )
                .ToImmutableArray(),
            inserts.ToImmutableArray(),
            parent,
            nested,
            payload
        );

    private static int[] RequestIds(Scenario scenario) =>
        scenario
            .Rows.Select((row, id) => (row, id))
            .Where(x => x.row.Visible && x.row.Keep)
            .Select(x => (Id: x.id, x.row.Priority))
            .Concat(
                scenario.InsertPriorities.Select(
                    (priority, i) => (Id: scenario.Rows.Length + i, Priority: priority)
                )
            )
            .OrderBy(x => x.Priority)
            .ThenByDescending(x => x.Id)
            .Select(x => x.Id)
            .ToArray();

    private static ProfileCollectionScopeInput BuildInput(Scenario scenario)
    {
        var scope = scenario.Nested ? "$.parents[*].items[*]" : "$.items[*]";
        ScopeInstanceAddress Parent() =>
            scenario.Nested
                ? new(
                    "$.parents[*]",
                    [new("$.parents[*]", BuildSemanticIdentityLong("parentId", scenario.ParentId))]
                )
                : RootScopeAddress();
        ImmutableArray<SemanticIdentityPart> Identity(int id) =>
            [
                new("itemId", JsonValue.Create(id), true),
                new("code", JsonValue.Create(scenario.Payload), true),
            ];
        var requestIds = RequestIds(scenario);
        return new(
            scope,
            Parent(),
            requestIds
                .Select((id, order) => BuildCollectionWriteCandidate(scope, Identity(id), order * 2 + 1))
                .ToImmutableArray(),
            requestIds
                .Select(
                    (id, order) =>
                        new VisibleRequestCollectionItem(
                            new(scope, Parent(), Identity(id)),
                            id >= scenario.Rows.Length || scenario.Rows[id].Creatable,
                            $"$.items[{order}]"
                        )
                )
                .ToImmutableArray(),
            scenario
                .Rows.Select((row, id) => (row, id))
                .Where(x => x.row.Visible)
                .Select(x => new VisibleStoredCollectionRow(
                    new(scope, Parent(), Identity(x.id)),
                    ["secret", "nested.secret"]
                ))
                .ToImmutableArray(),
            scenario
                .Rows.Select(
                    (_, id) =>
                        BuildCurrentCollectionRowSnapshot(
                            Identity(id),
                            id * 3 + 1,
                            scenario.ParentId * 100L + id
                        ) with
                        {
                            ProjectedCurrentRow = new(
                                [new FlattenedWriteValue.Literal(scenario.Payload + id)],
                                [new FlattenedWriteValue.Literal(id)]
                            ),
                            CurrentRowByColumnName = new Dictionary<DbColumnName, object?>
                            {
                                [new("Secret")] = scenario.Payload + id,
                            },
                        }
                )
                .ToImmutableArray()
        );
    }

    [TestCase("empty")]
    [TestCase("omitted")]
    [TestCase("hidden")]
    [TestCase("visible")]
    [TestCase("mixed")]
    [TestCase("surplus")]
    [TestCase("random")]
    public void It_preserves_hidden_rows_and_merges_visible_items_in_request_order(string shape)
    {
        var counts = new Dictionary<string, int>();
        Scenarios(shape)
            .Sample(
                scenario =>
                {
                    var input = BuildInput(scenario);
                    var before = SnapshotRows(input);
                    var sequence = ProfileCollectionPlanner
                        .Plan(input)
                        .Should()
                        .BeOfType<ProfileCollectionPlanResult.Success>()
                        .Subject.Plan.Sequence;
                    var requestIds = RequestIds(scenario);
                    var visibleCount = scenario.Rows.Count(r => r.Visible);

                    // Independent slot oracle: assign hidden rows a boundary in request order,
                    // then sort boundaries and request positions. No planner identity-key helpers.
                    var expected = scenario
                        .Rows.Select((row, id) => (row, id))
                        .Where(x => !x.row.Visible)
                        .Select(x =>
                        {
                            var preceding = scenario.Rows.Take(x.id).Count(r => r.Visible);
                            var boundary =
                                preceding == visibleCount && visibleCount > 0
                                    ? requestIds.Length
                                    : Math.Min(preceding, requestIds.Length);
                            return (Position: boundary, Kind: 0, Id: x.id, Label: $"H:{x.id}");
                        })
                        .Concat(
                            requestIds.Select(
                                (id, position) =>
                                    (
                                        Position: position,
                                        Kind: 1,
                                        Id: id,
                                        Label: $"{(id < scenario.Rows.Length ? "M" : "I")}:{id}"
                                    )
                            )
                        )
                        .OrderBy(x => x.Position)
                        .ThenBy(x => x.Kind)
                        .ThenBy(x => x.Id)
                        .Select(x => x.Label);

                    sequence
                        .Select(entry =>
                            entry switch
                            {
                                ProfileCollectionPlanEntry.HiddenPreserveEntry h =>
                                    $"H:{h.StoredRow.SemanticIdentityInOrder[0].Value!.GetValue<int>()}",
                                ProfileCollectionPlanEntry.MatchedUpdateEntry m =>
                                    $"M:{m.RequestCandidate.SemanticIdentityInOrder[0].Value!.GetValue<int>()}",
                                ProfileCollectionPlanEntry.VisibleInsertEntry i =>
                                    $"I:{i.RequestCandidate.SemanticIdentityInOrder[0].Value!.GetValue<int>()}",
                                _ => throw new AssertionException("Unexpected plan entry"),
                            }
                        )
                        .Should()
                        .Equal(expected);

                    foreach (var entry in sequence)
                    {
                        if (entry is ProfileCollectionPlanEntry.HiddenPreserveEntry hidden)
                        {
                            var id = hidden.StoredRow.SemanticIdentityInOrder[0].Value!.GetValue<int>();
                            hidden.StoredRow.Should().BeSameAs(input.CurrentRows[id]);
                        }
                        if (entry is ProfileCollectionPlanEntry.MatchedUpdateEntry matched)
                        {
                            var id = matched
                                .RequestCandidate.SemanticIdentityInOrder[0]
                                .Value!.GetValue<int>();
                            matched.StoredRow.Should().BeSameAs(input.CurrentRows[id]);
                            matched.HiddenMemberPaths.Should().Equal("secret", "nested.secret");
                            matched
                                .RequestCandidate.Should()
                                .BeSameAs(input.RequestCandidates[Array.IndexOf(requestIds, id)]);
                        }
                        if (entry is ProfileCollectionPlanEntry.VisibleInsertEntry inserted)
                        {
                            var id = inserted
                                .RequestCandidate.SemanticIdentityInOrder[0]
                                .Value!.GetValue<int>();
                            inserted
                                .RequestCandidate.Should()
                                .BeSameAs(input.RequestCandidates[Array.IndexOf(requestIds, id)]);
                        }
                    }
                    SnapshotRows(input).Should().Be(before);
                    var bucket =
                        $"{shape}; {(scenario.Nested ? "nested" : "root")}; {(requestIds.Length > visibleCount ? "surplus" : "no surplus")}";
                    counts[bucket] = counts.GetValueOrDefault(bucket) + 1;
                },
                iter: 200,
                threads: 1,
                print: s => JsonSerializer.Serialize(s)
            );
        foreach (var (bucket, count) in counts.OrderBy(x => x.Key))
        {
            TestContext.Out.WriteLine($"CsCheck 5.0.1: {bucket}: {count}");
        }
    }

    private static string SnapshotRows(ProfileCollectionScopeInput input) =>
        JsonSerializer.Serialize(
            input.CurrentRows.Select(row => new
            {
                row.StableRowIdentity,
                row.SemanticIdentityInOrder,
                row.StoredOrdinal,
                Values = row.ProjectedCurrentRow.Values.Select(v => ((FlattenedWriteValue.Literal)v).Value),
                ComparableValues = row.ProjectedCurrentRow.ComparableValues.Select(v =>
                    ((FlattenedWriteValue.Literal)v).Value
                ),
                Columns = row.CurrentRowByColumnName.ToDictionary(p => p.Key.Value, p => p.Value),
            })
        );

    [Test]
    public void It_rejects_an_unmatched_non_creatable_item()
    {
        Scenarios("surplus")
            .Sample(
                scenario =>
                {
                    var input = BuildInput(scenario);
                    var index = Array.FindIndex(RequestIds(scenario), id => id >= scenario.Rows.Length);
                    input = input with
                    {
                        VisibleRequestItems = input.VisibleRequestItems.SetItem(
                            index,
                            input.VisibleRequestItems[index] with
                            {
                                Creatable = false,
                            }
                        ),
                    };
                    ProfileCollectionPlanner
                        .Plan(input)
                        .Should()
                        .BeOfType<ProfileCollectionPlanResult.CreatabilityRejection>();
                },
                iter: 200,
                threads: 1,
                print: s => JsonSerializer.Serialize(s)
            );
    }

    [TestCase("candidate scope", "pre-scoped input: JsonScope mismatch")]
    [TestCase("request scope", "pre-scoped input: JsonScope mismatch")]
    [TestCase("stored scope", "pre-scoped input: JsonScope mismatch")]
    [TestCase("request parent", "pre-scoped input: parent scope mismatch")]
    [TestCase("stored parent", "pre-scoped input: parent scope mismatch")]
    [TestCase("request ancestor", "pre-scoped input: parent scope mismatch")]
    [TestCase("stored ancestor", "pre-scoped input: parent scope mismatch")]
    [TestCase("current duplicate", "current row identity uniqueness")]
    [TestCase("stored duplicate", "duplicate visible stored row")]
    [TestCase("stored coverage", "reverse stored coverage")]
    [TestCase("stored order", "order consistency: stored")]
    [TestCase("candidate duplicate", "duplicate visible request candidate")]
    [TestCase("request duplicate", "duplicate visible request item")]
    [TestCase("request coverage", "request-side coverage")]
    [TestCase("orphan candidate", "request-side coverage: orphan candidate")]
    [TestCase("request order", "order consistency: request")]
    public void It_fails_closed_when_one_contract_invariant_is_corrupted(string corruption, string invariant)
    {
        Scenarios("visible")
            .Sample(
                scenario =>
                {
                    // At least three matched entries survive every shrink, so corruption never
                    // becomes vacuous and never requires filtering/retrying generated inputs.
                    scenario = scenario with
                    {
                        Rows = scenario.Rows.Select(r => r with { Keep = true }).ToImmutableArray(),
                        Nested = scenario.Nested || corruption.EndsWith("ancestor", StringComparison.Ordinal),
                    };
                    var input = BuildInput(scenario);
                    ProfileCollectionPlanner
                        .Plan(input)
                        .Should()
                        .BeOfType<ProfileCollectionPlanResult.Success>();
                    var request = input.VisibleRequestItems[0];
                    var stored = input.VisibleStoredRows[0];
                    var wrongParent = corruption.EndsWith("ancestor", StringComparison.Ordinal)
                        ? new ScopeInstanceAddress(
                            "$.parents[*]",
                            [
                                new(
                                    "$.parents[*]",
                                    BuildSemanticIdentityLong("parentId", scenario.ParentId + 1)
                                ),
                            ]
                        )
                        : new ScopeInstanceAddress("$.other", []);
                    var corrupt = corruption switch
                    {
                        "candidate scope" => input with
                        {
                            RequestCandidates = input.RequestCandidates.SetItem(
                                0,
                                BuildCollectionWriteCandidate(
                                    "$.other[*]",
                                    request.Address.SemanticIdentityInOrder,
                                    1
                                )
                            ),
                        },
                        "request scope" => input with
                        {
                            VisibleRequestItems = input.VisibleRequestItems.SetItem(
                                0,
                                request with
                                {
                                    Address = request.Address with { JsonScope = "$.other[*]" },
                                }
                            ),
                        },
                        "stored scope" => input with
                        {
                            VisibleStoredRows = input.VisibleStoredRows.SetItem(
                                0,
                                stored with
                                {
                                    Address = stored.Address with { JsonScope = "$.other[*]" },
                                }
                            ),
                        },
                        "request parent" or "request ancestor" => input with
                        {
                            VisibleRequestItems = input.VisibleRequestItems.SetItem(
                                0,
                                request with
                                {
                                    Address = request.Address with { ParentAddress = wrongParent },
                                }
                            ),
                        },
                        "stored parent" or "stored ancestor" => input with
                        {
                            VisibleStoredRows = input.VisibleStoredRows.SetItem(
                                0,
                                stored with
                                {
                                    Address = stored.Address with { ParentAddress = wrongParent },
                                }
                            ),
                        },
                        "current duplicate" => input with
                        {
                            CurrentRows = input.CurrentRows.Add(input.CurrentRows[0]),
                        },
                        "stored duplicate" => input with
                        {
                            VisibleStoredRows = input.VisibleStoredRows.Add(stored),
                        },
                        "stored coverage" => input with { CurrentRows = input.CurrentRows.RemoveAt(0) },
                        "stored order" => input with
                        {
                            VisibleStoredRows = input.VisibleStoredRows.Reverse().ToImmutableArray(),
                        },
                        "candidate duplicate" => input with
                        {
                            RequestCandidates = input.RequestCandidates.Add(input.RequestCandidates[0]),
                        },
                        "request duplicate" => input with
                        {
                            VisibleRequestItems = input.VisibleRequestItems.Add(request),
                        },
                        "request coverage" => input with
                        {
                            RequestCandidates = input.RequestCandidates.RemoveAt(0),
                        },
                        "orphan candidate" => input with
                        {
                            VisibleRequestItems = input.VisibleRequestItems.RemoveAt(0),
                        },
                        "request order" => input with
                        {
                            VisibleRequestItems = input.VisibleRequestItems.Reverse().ToImmutableArray(),
                        },
                        _ => throw new AssertionException("Unknown corruption"),
                    };
                    Action act = () => ProfileCollectionPlanner.Plan(corrupt);
                    var exception = act.Should()
                        .ThrowExactly<ProfilePlannerContractMismatchException>()
                        .Which;
                    exception.InvariantName.Should().Be(invariant);
                    exception.JsonScope.Should().Be(input.JsonScope);
                },
                iter: 100,
                threads: 1,
                print: s => JsonSerializer.Serialize(s)
            );
    }
}

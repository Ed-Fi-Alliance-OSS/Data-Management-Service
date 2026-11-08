// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.Text.Json.Nodes;
using CsCheck;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

internal static class RelationalDocumentPropertyScenarios
{
    internal const string AddressScope = "$.addresses[*]";
    internal const string PeriodScope = "$.addresses[*].periods[*]";
    internal const string AlignedScope = "$._ext.sample.addresses[*]._ext.sample";
    internal const string SponsorScope = AlignedScope + ".sponsorReferences[*]";

    internal sealed record Address(int Periods, int Sponsors, bool Extension);

    internal sealed record Scenario(
        int SchoolId,
        int IdOffset,
        string Text,
        ImmutableArray<Address> Addresses
    );

    internal static Gen<Scenario> Generate(string shape) =>
        from schoolId in Gen.Int[3, int.MaxValue]
        from offset in Gen.Int[1, 10000]
        from text in Gen.Int[0, 4].Select(i => new[] { "plain", "é雪", "a|b=c", "\"\\", "line\nend" }[i])
        from addresses in Gen.Select(Gen.Int[0, 3], Gen.Int[0, 3], Gen.Bool)
            .Select(t => new Address(t.Item1, t.Item2, t.Item3))
            .Array[shape == "empty" ? 0 : 3, shape == "empty" ? 0 : 5]
        select new Scenario(
            schoolId,
            offset,
            text,
            addresses
                .Select(
                    (a, i) =>
                        shape switch
                        {
                            "dense" => new Address(3, 3, true),
                            "mixed" => a with { Extension = i % 2 == 0, Periods = i < 2 ? 3 * i : a.Periods },
                            _ => a,
                        }
                )
                .ToImmutableArray()
        );

    internal static RelationalResourceModel Model(SqlDialect dialect) =>
        RuntimePlanFixtureModelSetBuilder
            .Build(
                "Fixtures/runtime-plan-compilation/focused-stable-key/positive/extension-child-collections/fixture.manifest.json",
                dialect
            )
            .ConcreteResourcesInNameOrder.Single(r =>
                r.ResourceKey.Resource == new QualifiedResourceName("Ed-Fi", "School")
            )
            .RelationalModel;

    // IDs deliberately exceed Int32, and decrease as request/stored ordinal increases.
    internal static long DocumentId(Scenario s, int document) =>
        5_000_000_000L + s.IdOffset + document * 1000;

    internal static long AddressId(Scenario s, int document, int address) =>
        DocumentId(s, document) * 100 + 50 - address;

    internal static long ProgramId(Scenario s, int sponsor) => 9_000_000_000L + s.IdOffset + sponsor;

    internal static string City(Scenario s, int document, int address) => $"{s.Text}-{document}-{address}";

    internal static string Period(Scenario s, int document, int address, int period) =>
        $"p-{s.Text}-{document}-{address}-{period}";

    internal static string Program(Scenario s, int sponsor) => $"r-{s.Text}-{sponsor}";

    internal static string Zone(Scenario s, int document, int address) => $"z-{s.Text}-{document}-{address}";

    internal static string Campus(Scenario s, int document) => $"c-{s.Text}-{document}";

    // The semantic oracle knows the fixture's API shape, not production traversal or column bindings.
    // Empty optional collections are omitted on reads; writes additionally exercise explicit empty arrays.
    internal static JsonObject Document(
        Scenario s,
        int document,
        bool extensions,
        bool explicitEmptyArrays = false
    )
    {
        var result = new JsonObject { ["schoolId"] = s.SchoolId - document };
        var addresses = new JsonArray();
        for (var a = 0; a < s.Addresses.Length; a++)
        {
            var spec = s.Addresses[a];
            var address = new JsonObject { ["city"] = City(s, document, a) };
            if (spec.Periods > 0 || explicitEmptyArrays)
            {
                address["periods"] = new JsonArray(
                    Enumerable
                        .Range(0, spec.Periods)
                        .Select(p => (JsonNode)new JsonObject { ["periodName"] = Period(s, document, a, p) })
                        .ToArray()
                );
            }
            if (extensions && spec.Extension)
            {
                var extension = new JsonObject { ["zone"] = Zone(s, document, a) };
                if (spec.Sponsors > 0 || explicitEmptyArrays)
                {
                    extension["sponsorReferences"] = new JsonArray(
                        Enumerable
                            .Range(0, spec.Sponsors)
                            .Select(p =>
                                (JsonNode)
                                    new JsonObject
                                    {
                                        ["programReference"] = new JsonObject
                                        {
                                            ["programName"] = Program(s, p),
                                        },
                                    }
                            )
                            .ToArray()
                    );
                }
                address["_ext"] = new JsonObject { ["sample"] = extension };
            }
            addresses.Add(address);
        }
        if (addresses.Count > 0 || explicitEmptyArrays)
        {
            result["addresses"] = addresses;
        }
        if (extensions)
        {
            result["_ext"] = new JsonObject
            {
                ["sample"] = new JsonObject { ["campusCode"] = Campus(s, document) },
            };
        }
        return result;
    }

    // This fixture's write schema uses a root-level mirror for collection extensions.
    // Reconstitution intentionally emits those extensions beneath the corresponding address.
    internal static JsonObject WriteDocument(Scenario s, bool extensions)
    {
        var result = Document(s, 0, extensions, explicitEmptyArrays: true);
        if (extensions && s.Addresses.Length > 0)
        {
            var mirrors = new JsonArray();
            foreach (var address in result["addresses"]!.AsArray())
            {
                var mirror = new JsonObject();
                if (address!["_ext"] is JsonNode extension)
                {
                    address.AsObject().Remove("_ext");
                    mirror["_ext"] = extension;
                }
                mirrors.Add(mirror);
            }
            result["_ext"]!["sample"]!["addresses"] = mirrors;
        }
        return result;
    }

    internal static ResolvedReferenceSet References(Scenario s, bool extensions)
    {
        var references = new Dictionary<JsonPath, ResolvedDocumentReference>();
        for (var a = 0; a < s.Addresses.Length; a++)
        {
            if (!extensions || !s.Addresses[a].Extension)
            {
                continue;
            }
            for (var p = 0; p < s.Addresses[a].Sponsors; p++)
            {
                var path = new JsonPath(
                    $"$._ext.sample.addresses[{a}]._ext.sample.sponsorReferences[{p}].programReference"
                );
                references.Add(
                    path,
                    new ResolvedDocumentReference(
                        new DocumentReference(
                            new BaseResourceInfo(
                                new ProjectName("Ed-Fi"),
                                new ResourceName("Program"),
                                false
                            ),
                            new DocumentIdentity([
                                new DocumentIdentityElement(new JsonPath("$.programName"), Program(s, p)),
                            ]),
                            path
                        ),
                        ProgramId(s, p),
                        2
                    )
                );
            }
        }
        return ResolvedReferenceSets.Create(successfulDocumentReferencesByPath: references);
    }

    internal static HydratedPage Page(
        ResourceReadPlan plan,
        Scenario s,
        bool extensions,
        int[] documents,
        bool reverseRows
    )
    {
        var rows = plan.TablePlansInDependencyOrder.ToDictionary(
            t => t.TableModel.JsonScope.Canonical,
            _ => new List<object?[]>()
        );
        void Add(string scope, params (string Name, object Value)[] values)
        {
            var table = plan
                .TablePlansInDependencyOrder.Single(t => t.TableModel.JsonScope.Canonical == scope)
                .TableModel;
            var byName = values.ToDictionary(v => v.Name, v => v.Value);
            // Fail if the known-valid fixture changes; never silently drop a supplied value.
            foreach (var name in byName.Keys)
            {
                if (!table.Columns.Any(c => c.ColumnName.Value == name))
                {
                    throw new InvalidOperationException($"Unknown fixture column {scope}: {name}");
                }
            }
            rows[scope]
                .Add(table.Columns.Select(c => byName.GetValueOrDefault(c.ColumnName.Value)).ToArray());
        }
        foreach (var d in documents)
        {
            var id = DocumentId(s, d);
            Add("$", ("DocumentId", id), ("SchoolId", s.SchoolId - d));
            if (extensions)
            {
                Add("$._ext.sample", ("DocumentId", id), ("CampusCode", Campus(s, d)));
            }
            for (var a = 0; a < s.Addresses.Length; a++)
            {
                var spec = s.Addresses[a];
                var addressId = AddressId(s, d, a);
                Add(
                    AddressScope,
                    ("CollectionItemId", addressId),
                    ("School_DocumentId", id),
                    ("Ordinal", a * 3 + 2),
                    ("City", City(s, d, a))
                );
                for (var p = 0; p < spec.Periods; p++)
                {
                    Add(
                        PeriodScope,
                        ("CollectionItemId", addressId * 100 + 20 - p),
                        ("School_DocumentId", id),
                        ("ParentCollectionItemId", addressId),
                        ("Ordinal", p * 2 + 1),
                        ("PeriodName", Period(s, d, a, p))
                    );
                }
                if (!extensions || !spec.Extension)
                {
                    continue;
                }
                Add(
                    AlignedScope,
                    ("BaseCollectionItemId", addressId),
                    ("School_DocumentId", id),
                    ("Zone", Zone(s, d, a))
                );
                for (var p = 0; p < spec.Sponsors; p++)
                {
                    Add(
                        SponsorScope,
                        ("CollectionItemId", addressId * 100 + 40 - p),
                        ("School_DocumentId", id),
                        ("BaseCollectionItemId", addressId),
                        ("Ordinal", p * 4 + 1),
                        ("Program_DocumentId", ProgramId(s, p)),
                        ("Program_ProgramName", Program(s, p))
                    );
                }
            }
        }
        return new HydratedPage(
            null,
            documents
                .Select(d => new DocumentMetadataRow(
                    DocumentId(s, d),
                    Guid.Empty,
                    1,
                    DateTimeOffset.UnixEpoch,
                    1
                ))
                .ToArray(),
            plan.TablePlansInDependencyOrder.Select(t => new HydratedTableRows(
                    t.TableModel,
                    (
                        reverseRows
                            ? rows[t.TableModel.JsonScope.Canonical].AsEnumerable().Reverse()
                            : rows[t.TableModel.JsonScope.Canonical]
                    ).ToArray()
                ))
                .ToArray(),
            []
        );
    }
}

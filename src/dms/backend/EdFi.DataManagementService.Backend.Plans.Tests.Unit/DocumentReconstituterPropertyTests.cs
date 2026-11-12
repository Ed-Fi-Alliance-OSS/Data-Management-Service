// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using System.Text.Json.Nodes;
using CsCheck;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;
using FluentAssertions;
using NUnit.Framework;
using static EdFi.DataManagementService.Backend.Plans.Tests.Unit.RelationalDocumentPropertyScenarios;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

[TestFixture(SqlDialect.Pgsql)]
[TestFixture(SqlDialect.Mssql)]
[Category("RelationalDocumentProperty")]
public class Given_Generated_Hydrated_Documents(SqlDialect dialect)
{
    private ResourceReadPlan _plan = null!;

    [OneTimeSetUp]
    public void Setup() => _plan = new ReadPlanCompiler(dialect).Compile(Model(dialect));

    [Test]
    public void It_reconstitutes_expected_values_in_stored_ordinal_order_and_isolates_documents(
        [Values("empty", "mixed", "dense")] string shape,
        [Values(false, true)] bool extensions
    )
    {
        Generate(shape)
            .Sample(
                s =>
                {
                    // Metadata order is deliberately neither DocumentId order nor reversed row order.
                    int[] documentOrder = [2, 0, 1];
                    var page = Page(_plan, s, extensions, documentOrder, reverseRows: true);
                    var before = JsonSerializer.Serialize(
                        page.TableRowsInDependencyOrder.Select(t => t.Rows)
                    );
                    var actual = DocumentReconstituter.ReconstitutePage(_plan, page);
                    actual.Should().HaveCount(documentOrder.Length);
                    for (var i = 0; i < documentOrder.Length; i++)
                    {
                        var d = documentOrder[i];
                        var expected = Document(s, d, extensions);
                        AssertDocument(actual[i], expected);
                        var single = DocumentReconstituter.Reconstitute(
                            DocumentId(s, d),
                            _plan,
                            page.TableRowsInDependencyOrder,
                            new Dictionary<int, string>()
                        );
                        AssertDocument(single, expected);
                        AssertDocument(single, actual[i]);

                        var isolated = Page(_plan, s, extensions, [d], reverseRows: false);
                        AssertDocument(
                            DocumentReconstituter.ReconstitutePage(_plan, isolated).Single(),
                            expected
                        );
                    }
                    JsonSerializer
                        .Serialize(page.TableRowsInDependencyOrder.Select(t => t.Rows))
                        .Should()
                        .Be(before);
                },
                iter: 100,
                threads: 1,
                print: s => JsonSerializer.Serialize(s)
            );
        TestContext.Out.WriteLine(
            $"CsCheck 5.0.1: {dialect}, shape={shape}, extensions={extensions}: 100 samples, 3 documents each; reversed rows, gapped ordinals, descending stable IDs"
        );
    }

    private static void AssertDocument(JsonNode actual, JsonNode expected) =>
        JsonNode
            .DeepEquals(actual, expected)
            .Should()
            .BeTrue("expected {0}, actual {1}", expected.ToJsonString(), actual.ToJsonString());
}

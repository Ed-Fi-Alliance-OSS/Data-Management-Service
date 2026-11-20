// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Profile;
using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Profile;
using FluentAssertions;
using NUnit.Framework;
using static EdFi.DataManagementService.Backend.Tests.Unit.Profile.ProfileTestDoubles;

namespace EdFi.DataManagementService.Backend.Tests.Unit.Profile;

[TestFixture]
public class Given_nested_descriptor_ancestors_with_an_omitted_child_outside_the_request_cache
{
    [Test]
    public void It_deletes_the_visible_subtree_and_preserves_the_hidden_parent_subtree()
    {
        const string parentUri = "uri://ed-fi.org/AddressTypeDescriptor#Physical";
        const string childUri = "uri://ed-fi.org/AddressTypeDescriptor#Mailing";
        var (basePlan, baseParents, baseChildren, grandchildren) =
            ThreeLevelTopologyBuilders.BuildThreeLevelPlan();
        var root = basePlan.TablePlansInDependencyOrder[0];
        var parents = WithDescriptorIdentity(baseParents);
        var children = WithDescriptorIdentity(baseChildren);
        var plan = basePlan with
        {
            TablePlansInDependencyOrder = [root, parents, children, grandchildren],
            Model = basePlan.Model with
            {
                TablesInDependencyOrder =
                [
                    root.TableModel,
                    parents.TableModel,
                    children.TableModel,
                    grandchildren.TableModel,
                ],
                DescriptorEdgeSources = [DescriptorEdge(parents), DescriptorEdge(children)],
            },
        };

        // Only the visible parent is in the request cache. Its omitted child must be
        // resolved from stored rows in the partition keyed by the canonical parent ID.
        var body = new JsonObject
        {
            ["parents"] = new JsonArray(
                new JsonObject { ["identityField0"] = parentUri, ["children"] = new JsonArray() }
            ),
        };
        var request = NestedTopologyBuilders.BuildRequest(
            body,
            [NestedTopologyBuilders.BuildParentRequestItem(parentUri, 0, creatable: false)]
        );
        var parentCandidate = new CollectionWriteCandidate(
            parents,
            [0],
            0,
            [
                new FlattenedWriteValue.Literal(null),
                new FlattenedWriteValue.Literal(null),
                new FlattenedWriteValue.Literal(null),
                new FlattenedWriteValue.Literal(42),
            ],
            [42],
            SemanticIdentityTestHelpers.InferSemanticIdentityInOrderForTests(parents, [42])
        );
        var flattened = NestedTopologyBuilders.BuildFlattenedWriteSet(root, [parentCandidate]);
        var grandchildAddress = ThreeLevelTopologyBuilders
            .BuildGrandchildRequestItem(parentUri, childUri, "removed", 0, 0, 0)
            .Address;
        var context = NestedTopologyBuilders.BuildContext(
            request,
            [
                NestedTopologyBuilders.BuildParentStoredRow(parentUri),
                NestedTopologyBuilders.BuildChildStoredRow(parentUri, childUri),
                new VisibleStoredCollectionRow(grandchildAddress, []),
            ]
        );
        var current = ThreeLevelTopologyBuilders.BuildCurrentState(
            root,
            parents,
            children,
            grandchildren,
            documentId: 345L,
            parentRows:
            [
                [11L, 345L, 0, 42],
                [12L, 345L, 1, 99],
            ],
            childRows:
            [
                [21L, 11L, 0, 77],
                [22L, 12L, 0, 88],
            ],
            grandchildRows:
            [
                [31L, 21L, 0, "removed"],
                [32L, 22L, 0, "preserved"],
            ]
        );
        var reference = DescriptorCanonicalizeBuilders
            .BuildResolvedReferenceSetWithDescriptor(parentUri, 42)
            .SuccessfulDescriptorReferencesByPath.Values.Single();
        var parentPath = new JsonPath("$.parents[0].identityField0");
        var references = ResolvedReferenceSets.Create(
            successfulDescriptorReferencesByPath: new Dictionary<JsonPath, ResolvedDescriptorReference>
            {
                [parentPath] = reference with { Reference = reference.Reference with { Path = parentPath } },
            }
        );

        var outcome = BuildProfileSynthesizer()
            .Synthesize(
                new RelationalWriteProfileMergeRequest(
                    plan,
                    flattened,
                    body,
                    current,
                    request,
                    context,
                    references
                )
            );

        outcome.IsRejection.Should().BeFalse();
        outcome.ValidationFailure.Should().BeNull();
        var tables = outcome.MergeResult!.TablesInDependencyOrder;
        tables
            .Select(t => t.TableWritePlan.TableModel.Table)
            .Should()
            .Equal(
                root.TableModel.Table,
                parents.TableModel.Table,
                children.TableModel.Table,
                grandchildren.TableModel.Table
            );
        // The deleted child is inventoried for deletion. Grandchildren are visited
        // only under retained children, including the hidden parent's subtree.
        object?[][][] expectedCurrent =
        [
            [
                [345L],
            ],
            [
                [11L, 345L, 0, 42],
                [12L, 345L, 1, 99],
            ],
            [
                [21L, 11L, 0, 77],
                [22L, 12L, 0, 88],
            ],
            [
                [32L, 22L, 0, "preserved"],
            ],
        ];
        object?[][][] expectedMerged =
        [
            [
                [345L],
            ],
            [
                [11L, 345L, 0, 42],
                [12L, 345L, 1, 99],
            ],
            [
                [22L, 12L, 0, 88],
            ],
            [
                [32L, 22L, 0, "preserved"],
            ],
        ];
        for (var tableIndex = 0; tableIndex < tables.Length; tableIndex++)
        {
            AssertRows(tables[tableIndex].CurrentRows, expectedCurrent[tableIndex]);
            AssertRows(tables[tableIndex].MergedRows, expectedMerged[tableIndex]);
        }
    }

    private static void AssertRows(IReadOnlyList<RelationalWriteMergedTableRow> actual, object?[][] expected)
    {
        actual.Should().HaveCount(expected.Length);
        for (var rowIndex = 0; rowIndex < expected.Length; rowIndex++)
        {
            actual[rowIndex]
                .Values.Should()
                .Equal(expected[rowIndex].Select(value => new FlattenedWriteValue.Literal(value)));
        }
    }

    private static TableWritePlan WithDescriptorIdentity(TableWritePlan source)
    {
        var path = new JsonPathExpression($"{source.TableModel.JsonScope.Canonical}.identityField0", []);
        var column = source.TableModel.Columns[3] with
        {
            Kind = ColumnKind.DescriptorFk,
            ScalarType = null,
            SourceJsonPath = path,
            TargetResource = DescriptorCanonicalizeBuilders.AddressTypeDescriptorResource,
        };
        return source with
        {
            TableModel = source.TableModel with { Columns = [.. source.TableModel.Columns.Take(3), column] },
            ColumnBindings = source.ColumnBindings.SetItem(
                3,
                new WriteColumnBinding(
                    column,
                    new WriteValueSource.DescriptorReference(
                        DescriptorCanonicalizeBuilders.AddressTypeDescriptorResource,
                        path,
                        DescriptorValuePath: null
                    ),
                    "IdentityField0"
                )
            ),
        };
    }

    private static DescriptorEdgeSource DescriptorEdge(TableWritePlan table) =>
        new(
            false,
            table.TableModel.Columns[3].SourceJsonPath!.Value,
            table.TableModel.Table,
            table.TableModel.Columns[3].ColumnName,
            DescriptorCanonicalizeBuilders.AddressTypeDescriptorResource
        );
}

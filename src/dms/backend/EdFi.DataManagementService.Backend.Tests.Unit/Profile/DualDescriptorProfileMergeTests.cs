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
public class Given_A_Profile_Collection_With_Two_Descriptor_Identity_Parts
{
    [TestCase(false)]
    [TestCase(true)]
    public void It_matches_or_removes_the_complete_stored_identity(bool omitStoredRow)
    {
        var (basePlan, baseCollection) = DescriptorCanonicalizeBuilders.BuildPlan();
        var secondPath = new JsonPathExpression("$.addresses[*].secondaryAddressTypeDescriptor", []);
        var secondRelativePath = new JsonPathExpression("$.secondaryAddressTypeDescriptor", []);
        var secondColumn = baseCollection.TableModel.Columns[3] with
        {
            ColumnName = new DbColumnName("SecondaryAddressTypeDescriptor_Id"),
            SourceJsonPath = secondPath,
        };
        var collectionModel = baseCollection.TableModel with
        {
            Columns = [.. baseCollection.TableModel.Columns, secondColumn],
            IdentityMetadata = baseCollection.TableModel.IdentityMetadata with
            {
                SemanticIdentityBindings =
                [
                    .. baseCollection.TableModel.IdentityMetadata.SemanticIdentityBindings,
                    new CollectionSemanticIdentityBinding(secondRelativePath, secondColumn.ColumnName),
                ],
            },
        };
        var collection = baseCollection with
        {
            TableModel = collectionModel,
            ColumnBindings =
            [
                .. baseCollection.ColumnBindings,
                new WriteColumnBinding(
                    secondColumn,
                    new WriteValueSource.DescriptorReference(
                        DescriptorCanonicalizeBuilders.AddressTypeDescriptorResource,
                        secondPath,
                        DescriptorValuePath: null
                    ),
                    "SecondaryAddressTypeDescriptor_Id"
                ),
            ],
            BulkInsertBatching = new BulkInsertBatchingInfo(1000, 5, 65535),
            CollectionMergePlan = baseCollection.CollectionMergePlan! with
            {
                SemanticIdentityBindings =
                [
                    .. baseCollection.CollectionMergePlan!.SemanticIdentityBindings,
                    new CollectionMergeSemanticIdentityBinding(secondRelativePath, 4),
                ],
                CompareBindingIndexesInOrder = [3, 4, 2],
            },
        };
        var root = basePlan.TablePlansInDependencyOrder[0];
        var plan = basePlan with
        {
            Model = basePlan.Model with
            {
                TablesInDependencyOrder = [root.TableModel, collectionModel],
                DescriptorEdgeSources =
                [
                    .. basePlan.Model.DescriptorEdgeSources,
                    new DescriptorEdgeSource(
                        false,
                        secondPath,
                        collectionModel.Table,
                        secondColumn.ColumnName,
                        DescriptorCanonicalizeBuilders.AddressTypeDescriptorResource
                    ),
                ],
            },
            TablePlansInDependencyOrder = [root, collection],
        };
        const string secondUri = "uri://ed-fi.org/AddressTypeDescriptor#Mailing";
        const int secondId = 99;
        var body = new JsonObject
        {
            ["addresses"] = omitStoredRow
                ? new JsonArray()
                : new JsonArray(
                    new JsonObject
                    {
                        ["addressTypeDescriptor"] = DescriptorCanonicalizeBuilders.AddressTypeUri,
                        ["secondaryAddressTypeDescriptor"] = secondUri,
                    }
                ),
        };
        var firstReferences = DescriptorCanonicalizeBuilders.BuildResolvedReferenceSetWithDescriptor(
            DescriptorCanonicalizeBuilders.AddressTypeUri,
            DescriptorCanonicalizeBuilders.AddressTypeId
        );
        var secondReference = firstReferences.SuccessfulDescriptorReferencesByPath.Values.Single();
        var concreteSecondPath = new JsonPath("$.addresses[0].secondaryAddressTypeDescriptor");
        var references = omitStoredRow
            ? ResolvedReferenceSets.Create()
            : firstReferences with
            {
                SuccessfulDescriptorReferencesByPath = new Dictionary<JsonPath, ResolvedDescriptorReference>(
                    firstReferences.SuccessfulDescriptorReferencesByPath
                )
                {
                    [concreteSecondPath] = secondReference with
                    {
                        DescriptorId = secondId,
                        DocumentId = 5000000000L + secondId,
                        Reference = secondReference.Reference with
                        {
                            Path = concreteSecondPath,
                            DocumentIdentity = new DocumentIdentity([
                                new DocumentIdentityElement(new JsonPath("$.descriptor"), secondUri),
                            ]),
                        },
                    },
                },
            };
        var item = DescriptorCanonicalizeBuilders.BuildRequestItemWithUri(
            DescriptorCanonicalizeBuilders.AddressTypeUri,
            creatable: false
        );
        item = item with
        {
            Address = item.Address with
            {
                SemanticIdentityInOrder =
                [
                    .. item.Address.SemanticIdentityInOrder,
                    new SemanticIdentityPart(secondRelativePath.Canonical, JsonValue.Create(secondUri), true),
                ],
            },
        };
        var request = new ProfileAppliedWriteRequest(
            body,
            true,
            [
                new RequestScopeState(
                    new ScopeInstanceAddress("$", []),
                    ProfileVisibilityKind.VisiblePresent,
                    true
                ),
            ],
            omitStoredRow ? [] : [item]
        );
        var context = new ProfileAppliedWriteContext(
            request,
            new JsonObject(),
            [],
            [new VisibleStoredCollectionRow(item.Address, [])]
        );
        var candidate = new CollectionWriteCandidate(
            collection,
            [0],
            0,
            [
                new FlattenedWriteValue.Literal(null),
                new FlattenedWriteValue.Literal(null),
                new FlattenedWriteValue.Literal(null),
                new FlattenedWriteValue.Literal(42),
                new FlattenedWriteValue.Literal(secondId),
            ],
            [42, secondId],
            SemanticIdentityTestHelpers.InferSemanticIdentityInOrderForTests(collection, [42, secondId])
        );
        var flattened = new FlattenedWriteSet(
            new RootWriteRowBuffer(
                root,
                [new FlattenedWriteValue.Literal(345L)],
                collectionCandidates: omitStoredRow ? [] : [candidate]
            )
        );
        var current = DescriptorCanonicalizeBuilders.BuildCurrentState(
            root,
            collection,
            345L,
            collectionRows:
            [
                [1L, 345L, 0, 42, secondId],
            ]
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
        var state = outcome.MergeResult!.TablesInDependencyOrder[1];
        FlattenedWriteValue[] expected =
        [
            new FlattenedWriteValue.Literal(1L),
            new FlattenedWriteValue.Literal(345L),
            new FlattenedWriteValue.Literal(0),
            new FlattenedWriteValue.Literal(42),
            new FlattenedWriteValue.Literal(secondId),
        ];
        state.CurrentRows.Should().ContainSingle().Which.Values.Should().Equal(expected);
        if (omitStoredRow)
        {
            state.MergedRows.Should().BeEmpty();
        }
        else
        {
            state.MergedRows.Should().ContainSingle().Which.Values.Should().Equal(expected);
        }
    }
}

// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using CsCheck;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using NUnit.Framework;
using static EdFi.DataManagementService.Backend.Plans.Tests.Unit.RelationalDocumentPropertyScenarios;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

[TestFixture(SqlDialect.Pgsql)]
[TestFixture(SqlDialect.Mssql)]
[Category("RelationalDocumentProperty")]
public class Given_Generated_Relational_Write_Documents(SqlDialect dialect)
{
    private ResourceWritePlan _plan = null!;

    [OneTimeSetUp]
    public void Setup() => _plan = new WritePlanCompiler(dialect).Compile(Model(dialect));

    [Test]
    public void It_flattens_exact_values_references_ordinals_and_parent_tokens_without_mutating_input(
        [Values("empty", "mixed", "dense")] string shape,
        [Values(false, true)] bool extensions,
        [Values(false, true)] bool newDocument
    )
    {
        Generate(shape)
            .Sample(
                s =>
                {
                    var body = WriteDocument(s, extensions);
                    var before = body.ToJsonString();
                    var uuid = new DocumentUuid(Guid.Parse("11111111-1111-1111-1111-111111111111"));
                    RelationalWriteTargetContext target = newDocument
                        ? new RelationalWriteTargetContext.CreateNew(uuid)
                        : new RelationalWriteTargetContext.ExistingDocument(DocumentId(s, 0), uuid);
                    FlattenedWriteValue rootId = newDocument
                        ? FlattenedWriteValue.UnresolvedRootDocumentId.Instance
                        : new FlattenedWriteValue.Literal(DocumentId(s, 0));
                    var root = new RelationalWriteFlattener()
                        .Flatten(
                            new FlatteningInput(
                                RelationalWriteOperationKind.Post,
                                target,
                                _plan,
                                body,
                                References(s, extensions),
                                validateStorageCollapsedCollectionIdentityUniqueness: true
                            )
                        )
                        .RootRow;
                    AssertValues(
                        root.TableWritePlan,
                        root.Values,
                        ("DocumentId", rootId),
                        ("SchoolId", s.SchoolId)
                    );
                    root.RootExtensionRows.Should().HaveCount(extensions ? 1 : 0);
                    if (extensions)
                    {
                        var extension = root.RootExtensionRows.Single();
                        extension.TableWritePlan.TableModel.JsonScope.Canonical.Should().Be("$._ext.sample");
                        AssertValues(
                            extension.TableWritePlan,
                            extension.Values,
                            ("DocumentId", rootId),
                            ("CampusCode", Campus(s, 0))
                        );
                        extension.CollectionCandidates.Should().BeEmpty();
                    }
                    var tokens = new HashSet<Guid>();
                    root.CollectionCandidates.Should().HaveCount(s.Addresses.Length);
                    for (var a = 0; a < s.Addresses.Length; a++)
                    {
                        var spec = s.Addresses[a];
                        var address = root.CollectionCandidates[a];
                        var addressToken = AssertCandidate(address, AddressScope, [a], City(s, 0, a), tokens);
                        AssertValues(
                            address.TableWritePlan,
                            address.Values,
                            ("CollectionItemId", addressToken),
                            ("School_DocumentId", rootId),
                            ("Ordinal", a),
                            ("City", City(s, 0, a))
                        );
                        address.CollectionCandidates.Should().HaveCount(spec.Periods);
                        for (var p = 0; p < spec.Periods; p++)
                        {
                            var period = address.CollectionCandidates[p];
                            var token = AssertCandidate(
                                period,
                                PeriodScope,
                                [a, p],
                                Period(s, 0, a, p),
                                tokens
                            );
                            AssertValues(
                                period.TableWritePlan,
                                period.Values,
                                ("CollectionItemId", token),
                                ("School_DocumentId", rootId),
                                ("ParentCollectionItemId", addressToken),
                                ("Ordinal", p),
                                ("PeriodName", Period(s, 0, a, p))
                            );
                            period.CollectionCandidates.Should().BeEmpty();
                            period.AttachedAlignedScopeData.Should().BeEmpty();
                        }
                        address
                            .AttachedAlignedScopeData.Should()
                            .HaveCount(extensions && spec.Extension ? 1 : 0);
                        if (!extensions || !spec.Extension)
                        {
                            continue;
                        }
                        var aligned = address.AttachedAlignedScopeData.Single();
                        aligned.TableWritePlan.TableModel.JsonScope.Canonical.Should().Be(AlignedScope);
                        AssertValues(
                            aligned.TableWritePlan,
                            aligned.Values,
                            ("BaseCollectionItemId", addressToken),
                            ("School_DocumentId", rootId),
                            ("Zone", Zone(s, 0, a))
                        );
                        aligned.CollectionCandidates.Should().HaveCount(spec.Sponsors);
                        for (var p = 0; p < spec.Sponsors; p++)
                        {
                            var sponsor = aligned.CollectionCandidates[p];
                            var token = AssertCandidate(
                                sponsor,
                                SponsorScope,
                                [a, p],
                                ProgramId(s, p),
                                tokens
                            );
                            AssertValues(
                                sponsor.TableWritePlan,
                                sponsor.Values,
                                ("CollectionItemId", token),
                                ("School_DocumentId", rootId),
                                ("BaseCollectionItemId", addressToken),
                                ("Ordinal", p),
                                ("Program_DocumentId", ProgramId(s, p)),
                                ("Program_ProgramName", Program(s, p))
                            );
                            sponsor.CollectionCandidates.Should().BeEmpty();
                            sponsor.AttachedAlignedScopeData.Should().BeEmpty();
                        }
                    }
                    body.ToJsonString().Should().Be(before);
                },
                iter: 100,
                threads: 1,
                print: s => JsonSerializer.Serialize(s)
            );
        TestContext.Out.WriteLine(
            $"CsCheck 5.0.1: {dialect}, shape={shape}, extensions={extensions}, new={newDocument}: 100 samples"
        );
    }

    private static FlattenedWriteValue.UnresolvedCollectionItemId AssertCandidate(
        CollectionWriteCandidate candidate,
        string scope,
        int[] path,
        object semanticIdentity,
        HashSet<Guid> tokens
    )
    {
        candidate.TableWritePlan.TableModel.JsonScope.Canonical.Should().Be(scope);
        candidate.OrdinalPath.Should().Equal(path);
        candidate.RequestOrder.Should().Be(path[^1]);
        candidate.SemanticIdentityValues.Should().Equal(semanticIdentity);
        var index = candidate
            .TableWritePlan.ColumnBindings.Select((b, i) => (b, i))
            .Single(x => x.b.Column.ColumnName.Value == "CollectionItemId")
            .i;
        var token = candidate
            .Values[index]
            .Should()
            .BeOfType<FlattenedWriteValue.UnresolvedCollectionItemId>()
            .Subject;
        tokens.Add(token.Token).Should().BeTrue("each new collection row needs a distinct persistence token");
        return token;
    }

    private static void AssertValues(
        TableWritePlan plan,
        IReadOnlyList<FlattenedWriteValue> actual,
        params (string Name, object Value)[] expected
    )
    {
        var byName = expected.ToDictionary(
            x => x.Name,
            x => x.Value is FlattenedWriteValue value ? value : new FlattenedWriteValue.Literal(x.Value)
        );
        plan.ColumnBindings.Select(b => b.Column.ColumnName.Value).Should().BeEquivalentTo(byName.Keys);
        actual.Should().Equal(plan.ColumnBindings.Select(b => byName[b.Column.ColumnName.Value]));
    }
}

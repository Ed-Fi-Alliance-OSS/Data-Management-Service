// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

[TestFixture]
public class Given_DescriptorProjectionExecutor_With_No_Plans
{
    private IReadOnlyDictionary<long, string> _result = null!;

    [SetUp]
    public void SetUp()
    {
        _result = DescriptorProjectionExecutor.BuildLookupFromPlans([], []);
    }

    [Test]
    public void It_should_return_an_empty_dictionary()
    {
        _result.Should().BeEmpty();
    }
}

[TestFixture]
public class Given_DescriptorProjectionExecutor_With_Resolved_Pairs
{
    private IReadOnlyDictionary<long, string> _result = null!;

    private static readonly DescriptorProjectionPlan _dummyPlan = new(
        SelectByKeysetSql: "SELECT DescriptorId, Uri FROM dms.Descriptor;",
        ResultShape: new DescriptorProjectionResultShape(DescriptorIdOrdinal: 0, UriOrdinal: 1),
        SourcesInOrder:
        [
            new DescriptorProjectionSource(
                DescriptorValuePath: new JsonPathExpression(
                    "$.gradeLevelDescriptor",
                    [new JsonPathSegment.Property("gradeLevelDescriptor")]
                ),
                Table: new DbTableName(new DbSchemaName("edfi"), "GradingPeriod"),
                DescriptorResource: new QualifiedResourceName("Ed-Fi", "GradeLevelDescriptor"),
                DescriptorIdColumnOrdinal: 3
            ),
        ]
    );

    [SetUp]
    public void SetUp()
    {
        IReadOnlyList<(long DescriptorId, string Uri)> pairs =
        [
            (101L, "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"),
            (202L, "uri://ed-fi.org/GradeLevelDescriptor#Eleventh Grade"),
        ];

        _result = DescriptorProjectionExecutor.BuildLookupFromPlans([_dummyPlan], pairs);
    }

    [Test]
    public void It_should_contain_both_entries()
    {
        _result.Should().HaveCount(2);
    }

    [Test]
    public void It_should_resolve_the_first_descriptor_id_to_the_correct_uri()
    {
        _result[101L].Should().Be("uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade");
    }

    [Test]
    public void It_should_resolve_the_second_descriptor_id_to_the_correct_uri()
    {
        _result[202L].Should().Be("uri://ed-fi.org/GradeLevelDescriptor#Eleventh Grade");
    }
}

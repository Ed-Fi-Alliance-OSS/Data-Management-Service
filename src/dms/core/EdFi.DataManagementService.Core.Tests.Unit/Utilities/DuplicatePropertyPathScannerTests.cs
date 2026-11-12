// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text;
using EdFi.DataManagementService.Core.Utilities;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Utilities;

[TestFixtureSource(nameof(Cases))]
public class Given_Duplicate_Property_Path_Scanning(string json, bool expectedFound, string expectedPath)
{
    private bool _found;
    private string _path = string.Empty;
    private bool _emptyFound;
    private string _emptyPath = string.Empty;
    private bool _repeatedFound;
    private string _repeatedPath = string.Empty;

    private static IEnumerable<object[]> Cases()
    {
        yield return
        [
            """{"studentUniqueId":"1","addresses":[{"city":"a","streetNumberName":"b"},{"city":"x","city":"y"}]}""",
            true,
            "$.addresses[1].city",
        ];
        yield return
        [
            """{"studentUniqueId":"1","addresses":[{"city":"a"}],"electronicMails":[{"x":"1","x":"2"}]}""",
            true,
            "$.electronicMails[0].x",
        ];
        yield return
        [
            """[{"LastSurname":"a"},{"BirthLocation":{"City":"x","City":"y"}}]""",
            true,
            "$[1].BirthLocation.City",
        ];

        (string Sequence, string Suffix)[] matrix =
        [
            ("""{"x":1,"x":2}""", "[0].x"),
            ("""{"before":1,"x":1,"x":2}""", "[0].x"),
            ("""{"a":1,"b":2},{"before":0,"x":1,"x":2}""", "[1].x"),
            ("""{},{"before":1,"groups":[{"a":1},{"before":0,"x":1,"x":2}]}""", "[1].groups[1].x"),
        ];
        foreach (var (sequence, suffix) in matrix)
        {
            yield return ["{\"items\":[" + sequence + "]}", true, "$.items" + suffix];
            yield return ["[" + sequence + "]", true, "$" + suffix];
        }

        yield return ["""{"a":{"x":1,"x":2},"b":1,"b":2}""", true, "$.a.x"];
        yield return ["""{"x":1,"\u0078":1}""", true, "$.x"];
        yield return ["""{"a.b":1,"a.b":2}""", true, "$.a.b"];
        yield return ["""{"X":1,"x":2}""", false, string.Empty];
        yield return ["""[{"x":1},{"x":2}]""", false, string.Empty];
        yield return ["""[{"child":{},"list":[],"x":1,"x":2}]""", true, "$[0].x"];
        yield return ["""{"matrix":[[],[{}, {"x":1,"x":2}]]}""", true, "$.matrix[1][1].x"];
        yield return ["""{"a":[1,"s",true,false,null,{},[]],"b":[{"x":1,"x":2}]}""", true, "$.b[0].x"];
        yield return ["""{"mixed":[1,"s",true,false,null,{},[],{"x":1,"x":2}]}""", true, "$.mixed[7].x"];
        yield return ["""{"x":1,"x":1}""", true, "$.x"];
        yield return ["{}", false, string.Empty];
        yield return ["[]", false, string.Empty];
    }

    [SetUp]
    public void Setup()
    {
        _found = DuplicatePropertyPathScanner.TryFindFirstDuplicatePropertyPath(
            Encoding.UTF8.GetBytes(json),
            out _path
        );
        _emptyFound = DuplicatePropertyPathScanner.TryFindFirstDuplicatePropertyPath("{}"u8, out _emptyPath);
        _repeatedFound = DuplicatePropertyPathScanner.TryFindFirstDuplicatePropertyPath(
            Encoding.UTF8.GetBytes(json),
            out _repeatedPath
        );
    }

    [Test]
    public void It_reports_whether_a_duplicate_exists() => _found.Should().Be(expectedFound);

    [Test]
    public void It_reports_the_exact_first_duplicate_path() => _path.Should().Be(expectedPath);

    [Test]
    public void It_does_not_find_a_duplicate_in_an_independent_scan() => _emptyFound.Should().BeFalse();

    [Test]
    public void It_does_not_carry_a_path_into_an_independent_scan() => _emptyPath.Should().BeEmpty();

    [Test]
    public void It_reports_the_same_result_on_a_repeated_scan() => _repeatedFound.Should().Be(expectedFound);

    [Test]
    public void It_reports_the_same_path_on_a_repeated_scan() => _repeatedPath.Should().Be(expectedPath);
}

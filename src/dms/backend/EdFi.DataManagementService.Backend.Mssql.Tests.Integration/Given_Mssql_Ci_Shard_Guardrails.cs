// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Reflection;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

[TestFixture]
[Category(MssqlCiShards.Shard4)]
public class Given_Mssql_Ci_Shard_Guardrails
{
    private static readonly string[] _supportedShardCategories =
    [
        MssqlCiShards.Shard1,
        MssqlCiShards.Shard2,
        MssqlCiShards.Shard3,
        MssqlCiShards.Shard4,
    ];

    private static readonly HashSet<string> _nunitTestAttributeNames =
    [
        nameof(TestAttribute),
        nameof(TestCaseAttribute),
        nameof(TestCaseSourceAttribute),
        nameof(TheoryAttribute),
    ];

    [Test]
    public void It_assigns_exactly_one_mssql_ci_shard_category_to_every_sharded_test_fixture()
    {
        string[] offendingFixtures =
        [
            .. EnumerateConcreteTestFixtures()
                .Where(type => !IsExplicit(type))
                .Select(type => new { Type = type, Shards = GetShardCategories(type) })
                .Where(x => x.Shards.Length != 1)
                .Select(x => $"{x.Type.FullName} ({FormatShardCategories(x.Shards)})"),
        ];

        offendingFixtures
            .Should()
            .BeEmpty(
                "every MSSQL integration fixture PR CI runs must carry exactly one MssqlCiShards.* category so the shards never drop or duplicate coverage"
            );
    }

    [Test]
    public void It_assigns_exactly_one_effective_mssql_ci_shard_category_to_every_sharded_test_method()
    {
        string[] offendingTestMethods =
        [
            .. EnumerateShardedTestMethods()
                .Where(x => x.Shards.Length != 1)
                .Select(x => $"{x.Type.FullName}.{x.Method.Name} ({FormatShardCategories(x.Shards)})"),
        ];

        offendingTestMethods
            .Should()
            .BeEmpty(
                "each MSSQL integration test method PR CI runs must resolve to exactly one MssqlCiShards.* category after fixture and method categories are combined"
            );
    }

    /// <summary>
    /// A <c>Category=MssqlCiShardN</c> filter puts the NUnit adapter into explicit-run mode, so a shard
    /// category on an explicit test would make PR CI run the very test that opted out of it.
    /// </summary>
    [Test]
    public void It_keeps_explicit_tests_out_of_every_mssql_ci_shard()
    {
        string[] offendingExplicitTests =
        [
            .. EnumerateExplicitTests()
                .Where(x => x.Shards.Length != 0)
                .Select(x => $"{x.Name} ({FormatShardCategories(x.Shards)})"),
        ];

        offendingExplicitTests
            .Should()
            .BeEmpty(
                "an explicit MSSQL integration test must carry no MssqlCiShards.* category, because the shard filter would otherwise run it in PR CI"
            );
    }

    private static IEnumerable<(
        Type Type,
        MethodInfo Method,
        string[] Shards
    )> EnumerateShardedTestMethods() =>
        EnumerateConcreteTestFixtures()
            .Where(type => !IsExplicit(type))
            .SelectMany(type =>
                EnumerateTestMethods(type)
                    .Where(method => !IsExplicit(method))
                    .Select(method =>
                        (
                            Type: type,
                            Method: method,
                            Shards: GetShardCategories(type)
                                .Concat(GetShardCategories(method))
                                .Distinct(StringComparer.Ordinal)
                                .OrderBy(category => category, StringComparer.Ordinal)
                                .ToArray()
                        )
                    )
            );

    private static IEnumerable<(string Name, string[] Shards)> EnumerateExplicitTests() =>
        EnumerateConcreteTestFixtures()
            .SelectMany(type =>
                IsExplicit(type)
                    ? [(Name: DescribeType(type), Shards: GetShardCategories(type))]
                    : EnumerateTestMethods(type)
                        .Where(IsExplicit)
                        .Select(method =>
                            (Name: $"{DescribeType(type)}.{method.Name}", Shards: GetShardCategories(method))
                        )
            );

    private static string DescribeType(Type type) => type.FullName ?? type.Name;

    private static bool IsExplicit(MemberInfo member) =>
        member.GetCustomAttributes<ExplicitAttribute>(inherit: true).Any();

    private static IEnumerable<Type> EnumerateConcreteTestFixtures() =>
        Assembly
            .GetExecutingAssembly()
            .GetTypes()
            .Where(type => type is { IsAbstract: false, IsClass: true })
            .Where(type =>
                type.GetCustomAttributes<TestFixtureAttribute>(inherit: true).Any()
                || EnumerateTestMethods(type).Any()
            )
            .OrderBy(type => type.FullName, StringComparer.Ordinal);

    private static IEnumerable<MethodInfo> EnumerateTestMethods(Type type) =>
        type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(method =>
                Array.Exists(
                    method.GetCustomAttributes(inherit: true),
                    attribute => _nunitTestAttributeNames.Contains(attribute.GetType().Name)
                )
            )
            .OrderBy(method => method.Name, StringComparer.Ordinal);

    private static string[] GetShardCategories(MemberInfo member) =>
        [
            .. member
                .GetCustomAttributes<CategoryAttribute>(inherit: true)
                .Select(category => category.Name)
                .Where(category => _supportedShardCategories.Contains(category))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(category => category, StringComparer.Ordinal),
        ];

    private static string FormatShardCategories(IReadOnlyCollection<string> shardCategories) =>
        shardCategories.Count == 0 ? "none" : string.Join(", ", shardCategories);
}

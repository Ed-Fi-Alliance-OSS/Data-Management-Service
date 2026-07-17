// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.Reflection;

namespace EdFi.DataManagementService.Backend.Tests.Common.Parity;

/// <summary>The relational engine whose catalog coverage/locations a resolution pass validates.</summary>
public enum ParityEngine
{
    Pgsql,
    Mssql,
}

/// <summary>
/// Reflection-based resolver that validates the parity catalog's declared covered locations against a
/// concrete test assembly. Each owning assembly runs a thin meta-test that passes its own
/// <see cref="Assembly.GetExecutingAssembly"/>; this resolver inspects that assembly's classes and
/// method attributes itself (it never accepts adapter-supplied existence flags). For every covered
/// location it requires exactly one class whose <see cref="Type.Name"/> equals the catalog
/// <c>Fixture</c> and exactly one method per catalog method name carrying NUnit's <c>[Test]</c>
/// attribute. The <c>File</c> field is diagnostic only — resolution never depends on file names.
/// </summary>
public static class ParityCatalogResolution
{
    private const string NUnitTestAttributeFullName = "NUnit.Framework.TestAttribute";

    /// <summary>
    /// Resolves every <see cref="ParityLayer.Profile"/> / <see cref="ParityLayer.NoProfile"/> row that is
    /// covered for <paramref name="engine"/> against <paramref name="assembly"/>, returning an actionable
    /// violation per unresolved target (empty when all covered locations resolve).
    /// </summary>
    public static IReadOnlyList<string> ResolveBackendCoveredLocations(ParityEngine engine, Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        List<string> violations = [];

        foreach (
            ParityScenario scenario in ParityScenarioCatalog.All.Where(scenario =>
                scenario.Layer is ParityLayer.Profile or ParityLayer.NoProfile
            )
        )
        {
            ResolveEngineLocations(scenario, engine, assembly, violations);
        }

        return violations;
    }

    /// <summary>
    /// Resolves every <see cref="ParityLayer.Api"/> row's covered PostgreSQL and SQL Server locations
    /// independently against the API <paramref name="assembly"/>, returning an actionable violation per
    /// unresolved target (empty when all covered locations resolve).
    /// </summary>
    public static IReadOnlyList<string> ResolveApiCoveredLocations(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        List<string> violations = [];

        foreach (
            ParityScenario scenario in ParityScenarioCatalog.All.Where(scenario =>
                scenario.Layer == ParityLayer.Api
            )
        )
        {
            ResolveEngineLocations(scenario, ParityEngine.Pgsql, assembly, violations);
            ResolveEngineLocations(scenario, ParityEngine.Mssql, assembly, violations);
        }

        return violations;
    }

    private static void ResolveEngineLocations(
        ParityScenario scenario,
        ParityEngine engine,
        Assembly assembly,
        List<string> violations
    )
    {
        (bool covered, ImmutableArray<ScenarioLocation> locations, string engineLabel) = engine switch
        {
            ParityEngine.Pgsql => (
                scenario.PgsqlCoverage == EngineCoverage.Covered,
                scenario.PgsqlLocations,
                "PostgreSQL"
            ),
            ParityEngine.Mssql => (
                scenario.MssqlCoverage == EngineCoverage.Covered,
                scenario.MssqlLocations,
                "SQL Server"
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(engine), engine, "Unknown parity engine."),
        };

        if (!covered)
        {
            return;
        }

        foreach (ScenarioLocation location in locations)
        {
            ResolveLocation(scenario.Id, engineLabel, location, assembly, violations);
        }
    }

    private static void ResolveLocation(
        string scenarioId,
        string engineLabel,
        ScenarioLocation location,
        Assembly assembly,
        List<string> violations
    )
    {
        Type[] fixtures = [.. assembly.GetTypes().Where(type => type.Name == location.Fixture)];

        if (fixtures.Length != 1)
        {
            violations.Add(
                $"{scenarioId} [{engineLabel}] {location.File}::{location.Fixture}: expected exactly one class named "
                    + $"'{location.Fixture}' in assembly '{assembly.GetName().Name}', but found {fixtures.Length}."
            );
            return;
        }

        Type fixture = fixtures[0];

        if (!fixture.IsClass || fixture.IsAbstract)
        {
            violations.Add(
                $"{scenarioId} [{engineLabel}] {location.File}::{location.Fixture}: '{location.Fixture}' resolved to a type "
                    + $"in assembly '{assembly.GetName().Name}' that is not a concrete class."
            );
            return;
        }

        foreach (string methodName in location.Methods)
        {
            MethodInfo[] methods =
            [
                .. fixture
                    .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                    .Where(method => method.Name == methodName),
            ];

            if (methods.Length != 1)
            {
                violations.Add(
                    $"{scenarioId} [{engineLabel}] {location.File}::{location.Fixture}::{methodName}: expected exactly one "
                        + $"method named '{methodName}' on '{location.Fixture}', but found {methods.Length}."
                );
                continue;
            }

            bool isTestMethod = Array.Exists(
                methods[0].GetCustomAttributes(inherit: true),
                attribute =>
                    string.Equals(
                        attribute.GetType().FullName,
                        NUnitTestAttributeFullName,
                        StringComparison.Ordinal
                    )
            );

            if (!isTestMethod)
            {
                violations.Add(
                    $"{scenarioId} [{engineLabel}] {location.File}::{location.Fixture}::{methodName}: method exists but is not marked [Test]."
                );
            }
        }
    }
}

// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.RelationalModel;

/// <summary>
/// Orchestrates ordered set-level passes to derive a complete relational model set.
/// </summary>
public sealed class DerivedRelationalModelSetBuilder
{
    private readonly IReadOnlyList<IRelationalModelSetPass> _passes;

    /// <summary>
    /// Creates a new builder with a deterministic pass ordering.
    /// </summary>
    /// <param name="passes">The passes to execute in order.</param>
    public DerivedRelationalModelSetBuilder(IReadOnlyList<IRelationalModelSetPass> passes)
    {
        ArgumentNullException.ThrowIfNull(passes);

        if (passes.Count == 0)
        {
            _passes = Array.Empty<IRelationalModelSetPass>();
            return;
        }

        if (passes.Any(pass => pass is null))
        {
            throw new ArgumentException("Pass list cannot contain null entries.", nameof(passes));
        }

        _passes = OrderPasses(passes);
    }

    /// <summary>
    /// Runs the configured passes and returns a derived relational model set.
    /// </summary>
    /// <param name="effectiveSchemaSet">The normalized effective schema set.</param>
    /// <param name="dialect">The target SQL dialect.</param>
    /// <param name="dialectRules">Shared dialect rules for derivation.</param>
    public DerivedRelationalModelSet Build(
        EffectiveSchemaSet effectiveSchemaSet,
        SqlDialect dialect,
        ISqlDialectRules dialectRules
    )
    {
        ArgumentNullException.ThrowIfNull(effectiveSchemaSet);
        ArgumentNullException.ThrowIfNull(dialectRules);

        if (dialectRules.Dialect != dialect)
        {
            throw new InvalidOperationException(
                $"Dialect mismatch: requested {dialect} but dialect rules target {dialectRules.Dialect}."
            );
        }

        var context = new RelationalModelSetBuilderContext(effectiveSchemaSet, dialect, dialectRules);

        foreach (var pass in _passes)
        {
            pass.Execute(context);
        }

        return context.BuildResult();
    }

    /// <summary>
    /// Validates pass order uniqueness and returns the pass list sorted by <see cref="IRelationalModelSetPass.Order"/>.
    /// </summary>
    /// <param name="passes">The unordered pass list.</param>
    /// <returns>A deterministically ordered pass list.</returns>
    private static IReadOnlyList<IRelationalModelSetPass> OrderPasses(
        IReadOnlyList<IRelationalModelSetPass> passes
    )
    {
        var passEntries = passes
            .Select(pass => new PassEntry(pass, pass.Order, pass.GetType().FullName ?? pass.GetType().Name))
            .ToArray();

        if (passEntries.Any(pass => string.IsNullOrWhiteSpace(pass.TypeName)))
        {
            throw new InvalidOperationException("Pass type name must be non-empty.");
        }

        var duplicateOrders = passEntries
            .GroupBy(entry => entry.Order)
            .Where(group => group.Count() > 1)
            .Select(group =>
                $"Order {group.Key}: "
                + string.Join(
                    ", ",
                    group
                        .Select(entry => entry.TypeName)
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(name => name, StringComparer.Ordinal)
                )
            )
            .ToArray();

        if (duplicateOrders.Length > 0)
        {
            throw new InvalidOperationException(
                "Duplicate pass order values detected: " + string.Join("; ", duplicateOrders)
            );
        }

        var orderedPasses = passEntries.OrderBy(entry => entry.Order).Select(entry => entry.Pass).ToArray();

        return orderedPasses;
    }

    /// <summary>
    /// Materialized pass metadata used for deterministic ordering and diagnostics.
    /// </summary>
    private sealed record PassEntry(IRelationalModelSetPass Pass, int Order, string TypeName);
}

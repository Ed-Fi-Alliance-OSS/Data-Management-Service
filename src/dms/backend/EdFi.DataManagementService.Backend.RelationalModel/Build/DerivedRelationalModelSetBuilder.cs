// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.RelationalModel.Build;

/// <summary>
/// Orchestrates ordered set-level passes to derive a complete relational model set.
/// </summary>
public sealed class DerivedRelationalModelSetBuilder
{
    private readonly IReadOnlyList<IRelationalModelSetPass> _passes;

    /// <summary>
    /// Creates a new builder that executes passes in the supplied order.
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

        _passes = passes.ToArray();
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
}

// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.RelationalModel;

/// <summary>
/// A single ordered pass that participates in deriving a set-level relational model.
/// </summary>
public interface IRelationalModelSetPass
{
    /// <summary>
    /// The explicit order for this pass; lower values execute first. Order values must be unique
    /// across the pass list because the builder does not use type names as a tie-breaker.
    /// </summary>
    int Order { get; }

    /// <summary>
    /// Executes the pass, reading inputs from and writing outputs to the supplied context.
    /// </summary>
    /// <param name="context">The shared builder context for the current derivation.</param>
    void Execute(RelationalModelSetBuilderContext context);
}

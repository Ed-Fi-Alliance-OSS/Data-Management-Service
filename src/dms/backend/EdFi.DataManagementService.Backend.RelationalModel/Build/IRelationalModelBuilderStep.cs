// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.RelationalModel;

/// <summary>
/// A single step in the relational model derivation pipeline.
/// </summary>
public interface IRelationalModelBuilderStep
{
    /// <summary>
    /// Executes the step, reading inputs from and writing outputs to the supplied context.
    /// </summary>
    /// <param name="context">The mutable builder context for the current pipeline run.</param>
    void Execute(RelationalModelBuilderContext context);
}

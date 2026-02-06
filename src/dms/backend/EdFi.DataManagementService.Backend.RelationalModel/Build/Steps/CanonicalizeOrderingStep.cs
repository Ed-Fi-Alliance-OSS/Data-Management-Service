// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.RelationalModel.Build.Steps;

/// <summary>
/// Normalizes ordering of tables, columns, constraints, and related metadata to ensure deterministic
/// relational model output regardless of source enumeration order.
/// </summary>
public sealed class CanonicalizeOrderingStep : IRelationalModelBuilderStep
{
    /// <summary>
    /// Applies canonical ordering rules to the current <see cref="RelationalModelBuilderContext"/>,
    /// including table ordering, column and constraint ordering, document reference bindings,
    /// descriptor edges, and extension sites.
    /// </summary>
    /// <param name="context">The relational model builder context to canonicalize.</param>
    public void Execute(RelationalModelBuilderContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var resourceModel =
            context.ResourceModel
            ?? throw new InvalidOperationException(
                "Resource model must be provided before canonicalizing ordering."
            );

        context.ResourceModel = RelationalModelCanonicalization.CanonicalizeResourceModel(resourceModel);

        var orderedExtensionSites = RelationalModelCanonicalization.CanonicalizeExtensionSites(
            context.ExtensionSites
        );
        context.ExtensionSites.Clear();
        context.ExtensionSites.AddRange(orderedExtensionSites);
    }
}

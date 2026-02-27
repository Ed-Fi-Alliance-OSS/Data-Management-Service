// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.External.Plans;

/// <summary>
/// Deterministic resolution helpers for write-plan document-reference value sources.
/// </summary>
public static class WritePlanDocumentReferenceConventions
{
    /// <summary>
    /// Resolves the model binding referenced by a document-reference value source.
    /// </summary>
    /// <param name="writePlan">The resource write plan that owns the binding inventory.</param>
    /// <param name="source">The document-reference value source.</param>
    public static DocumentReferenceBinding ResolveBinding(
        ResourceWritePlan writePlan,
        WriteValueSource.DocumentReference source
    )
    {
        ArgumentNullException.ThrowIfNull(source);

        return ResolveBinding(writePlan, source.BindingIndex);
    }

    /// <summary>
    /// Resolves one document-reference binding by index from the write-plan model inventory.
    /// </summary>
    /// <param name="writePlan">The resource write plan that owns the binding inventory.</param>
    /// <param name="bindingIndex">The index into <see cref="RelationalResourceModel.DocumentReferenceBindings" />.</param>
    public static DocumentReferenceBinding ResolveBinding(ResourceWritePlan writePlan, int bindingIndex)
    {
        ArgumentNullException.ThrowIfNull(writePlan);

        var bindings = writePlan.Model.DocumentReferenceBindings;

        if ((uint)bindingIndex >= (uint)bindings.Count)
        {
            throw new ArgumentOutOfRangeException(
                nameof(bindingIndex),
                bindingIndex,
                $"Document-reference binding index '{bindingIndex}' is out of range for "
                    + $"'{nameof(RelationalResourceModel.DocumentReferenceBindings)}' (count: {bindings.Count})."
            );
        }

        return bindings[bindingIndex];
    }
}

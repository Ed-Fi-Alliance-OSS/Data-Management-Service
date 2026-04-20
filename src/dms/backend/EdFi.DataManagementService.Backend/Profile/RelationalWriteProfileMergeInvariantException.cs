// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.Profile;

/// <summary>
/// Thrown by <see cref="RelationalWriteProfileMergeRequest"/> only for the
/// Slice 2 shape-drift case: the flattener produced a non-root buffer
/// (RootExtensionRows or CollectionCandidates) even though upstream slice
/// fencing classified the shape as root-table-only. The exception carries
/// the family that escaped so the executor can surface the correct slice-fence
/// failure instead of collapsing every invariant into the same family.
///
/// Other constructor preconditions (root-table mismatch, request-instance
/// mismatch, current-state/context pairing) are caller-wiring bugs and stay
/// as <see cref="ArgumentException"/>.
/// </summary>
public sealed class RelationalWriteProfileMergeInvariantException : Exception
{
    public RelationalWriteProfileMergeInvariantException(RequiredSliceFamily escapedFamily, string message)
        : base(message)
    {
        EscapedFamily = escapedFamily;
    }

    public RelationalWriteProfileMergeInvariantException(
        RequiredSliceFamily escapedFamily,
        string message,
        Exception inner
    )
        : base(message, inner)
    {
        EscapedFamily = escapedFamily;
    }

    public RequiredSliceFamily EscapedFamily { get; }
}

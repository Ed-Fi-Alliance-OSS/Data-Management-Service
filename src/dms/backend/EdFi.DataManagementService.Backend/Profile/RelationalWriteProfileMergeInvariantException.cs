// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.Profile;

/// <summary>
/// Thrown by <see cref="RelationalWriteProfileMergeRequest"/> only for the
/// Slice 2 shape-drift case: the flattener produced a non-root buffer
/// (RootExtensionRows or CollectionCandidates) even though upstream slice
/// fencing classified the shape as root-table-only. The executor catches
/// this exception and maps it to a deterministic slice-fence failure so a
/// fence regression does not surface as an unhandled 500.
///
/// Other constructor preconditions (root-table mismatch, request-instance
/// mismatch, current-state/context pairing) are caller-wiring bugs and stay
/// as <see cref="ArgumentException"/>.
/// </summary>
public sealed class RelationalWriteProfileMergeInvariantException : Exception
{
    public RelationalWriteProfileMergeInvariantException(string message)
        : base(message) { }

    public RelationalWriteProfileMergeInvariantException(string message, Exception inner)
        : base(message, inner) { }
}

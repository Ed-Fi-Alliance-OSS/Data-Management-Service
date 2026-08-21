// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.CustomValidation;

/// <summary>
/// The write pipeline a custom validator is being invoked from.
/// Named after DMS's own pipeline vocabulary (CreateUpsertPipeline/UpsertHandler for POST,
/// CreateUpdatePipeline/UpdateByIdHandler for PUT) rather than after the HTTP verbs, since DMS's own
/// request-method type is internal and unavailable to this public contract.
/// There is deliberately no zero-valued "unspecified" member, so <c>default</c> is
/// <see cref="Upsert"/> rather than a sentinel. A new member is therefore appended, never inserted:
/// inserting one would renumber the rest and silently change the meaning of anything already
/// compiled against this contract.
/// </summary>
public enum CustomValidationOperation
{
    /// <summary>
    /// The write arrived through the upsert (POST) pipeline.
    /// </summary>
    Upsert,

    /// <summary>
    /// The write arrived through the update-by-id (PUT) pipeline.
    /// </summary>
    Update,
}

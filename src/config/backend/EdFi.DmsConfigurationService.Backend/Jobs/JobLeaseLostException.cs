// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.Jobs;

/// <summary>
/// Thrown by a fence when this execution no longer owns its job, or its ownership is uncertain (spec D-5).
/// The handler must stop; nothing the fence ran was committed.
/// </summary>
public sealed class JobLeaseLostException()
    : Exception("The job lease was lost or its ownership is uncertain.");

// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.Jobs;

/// <summary>
/// Thrown by a fence when the job row lock could not be acquired within <c>FenceLockWait</c> (spec D-5).
/// Ownership is unchanged, so this is transient.
/// </summary>
public sealed class JobFenceUnavailableException()
    : Exception("The job fence could not acquire the job row lock in time.");

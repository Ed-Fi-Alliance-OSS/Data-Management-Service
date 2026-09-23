// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.Jobs;

/// <summary>A job to enqueue: its registered type, payload version, and serialized payload (spec D-13).</summary>
public sealed record JobEnqueueCommand(string JobType, short PayloadVersion, string PayloadJson);

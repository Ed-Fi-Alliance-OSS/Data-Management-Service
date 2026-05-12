// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Frontend.AspNetCore.Infrastructure;

namespace EdFi.DataManagementService.Tests.Integration.Doubles;

/// <summary>
/// Replaces <see cref="EnvironmentStartupProcessExit"/> so that fatal startup failures
/// during a test surface as a rethrown exception caught by NUnit's setup pipeline
/// instead of terminating the test host with <see cref="Environment.Exit(int)"/>.
/// The captured <see cref="LastExitCode"/> is exposed for diagnostic assertions.
/// </summary>
internal sealed class NonExitingStartupProcessExit : IStartupProcessExit
{
    public int? LastExitCode { get; private set; }

    public void Exit(int exitCode) => LastExitCode = exitCode;
}

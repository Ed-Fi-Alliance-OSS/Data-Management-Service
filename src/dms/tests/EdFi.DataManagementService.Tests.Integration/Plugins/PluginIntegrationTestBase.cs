// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Tests.Integration.Plugins;

/// <summary>
/// Carries the category that puts the plugin host-integration cases in a CI lane of their own.
/// </summary>
/// <remarks>
/// <para>
/// It holds no state and no lifecycle, deliberately. Every case here arranges its own plugin root,
/// allowlist and configuration, and there is nothing they share to lift; what they do share is the
/// need to be selected by a test filter, and this is the only thing a fixture has to derive from to
/// get that.
/// </para>
/// <para>
/// A category rather than the two that already exist: both database lanes filter on
/// <c>ApiIntegration</c> plus a dialect category, and these cases boot against external doubles with
/// no database at all. Carrying <c>ApiIntegration</c> would put each of them in both database lanes
/// for no reason; carrying neither, which is where they started, left them built by CI and run by
/// nothing.
/// </para>
/// </remarks>
[Category("PluginIntegration")]
public abstract class PluginIntegrationTestBase;

// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Catalog;

/// <summary>
/// The catalog's startup verdict (7.3 items 2 and 3): one reason per failed probe, and the engine version each
/// template's authenticated manifest declares, for every template that resolved. Readiness compares the latter
/// with the live server (<see cref="DmsEngineCompatibility"/>).
/// </summary>
public sealed record TemplateCatalogValidation(
    IReadOnlyList<DmsReadinessReason> Reasons,
    IReadOnlyList<TemplateEngineRequirement> EngineRequirements
);

/// <summary>The <c>engineVersion</c> of <paramref name="Template"/>'s authenticated manifest.</summary>
public sealed record TemplateEngineRequirement(DmsDatabaseTemplate Template, string EngineVersion);

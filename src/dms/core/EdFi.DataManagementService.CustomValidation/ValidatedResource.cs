// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.CustomValidation;

/// <summary>
/// A single resource that a custom validator declares itself applicable to, identified by the
/// MetaEd project it belongs to and its resource name within that project.
/// Both are plain strings rather than branded types so that this package carries no dependency on
/// DMS's internal model; DMS maps its own types onto these where it invokes a validator.
/// </summary>
/// <param name="ProjectName">
/// The MetaEd project name the resource belongs to, e.g. "Ed-Fi" for a data standard entity.
/// </param>
/// <param name="ResourceName">
/// The resource name within the project. Typically the corresponding MetaEd entity name, except
/// that descriptors carry a "Descriptor" suffix.
/// </param>
public sealed record ValidatedResource(string ProjectName, string ResourceName);

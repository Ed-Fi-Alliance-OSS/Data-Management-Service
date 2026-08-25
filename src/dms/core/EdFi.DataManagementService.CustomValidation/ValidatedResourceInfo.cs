// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.CustomValidation;

/// <summary>
/// Which resource the document being validated belongs to, for the request in hand.
/// A validator may declare several resources in <see cref="ICustomResourceValidator.AppliesTo"/> and
/// is invoked for all of them, so this is how it tells them apart.
///
/// This is a projection of what DMS knows about a resource, not DMS's own model, and it is
/// deliberately narrow: it carries identity and version, and none of the write-path policy DMS
/// tracks alongside them. Narrow is the safe direction to be wrong in, because DMS constructs this
/// type and implementers only read it, so a field can be added later without breaking anyone,
/// whereas removing one could not.
/// </summary>
/// <param name="ProjectName">
/// The MetaEd project name the resource belongs to, e.g. "Ed-Fi" for a data standard entity.
/// </param>
/// <param name="ResourceName">
/// The resource name within the project. Descriptors carry a "Descriptor" suffix.
/// </param>
/// <param name="ResourceVersion">
/// The semantic version of the project the resource belongs to, for a rule that has to differ across
/// Data Standard versions.
/// </param>
public sealed record ValidatedResourceInfo(string ProjectName, string ResourceName, string ResourceVersion);

// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Core.Model;

/// <summary>
/// A simple tuple containing the identity JsonPath and corresponding document value
/// that makes up part of a document identity.
/// </summary>
internal record DocumentIdentityElement(IJsonPath IdentityJsonPath, string IdentityValue) : IDocumentIdentityElement;

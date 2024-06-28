// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.External.Model;

/// <summary>
/// A DocumentIdentity is an array of key-value pairs that represents the complete identity of an Ed-Fi document.
/// In API documents, these are always a list of document elements from the top level of the document
/// (never nested in sub-objects, and never collections). The keys are DocumentObjectKeys. A DocumentIdentity
/// must maintain a strict ordering.
///
/// This can be an array of key-value pairs because many documents have multiple values as part of their identity.
/// </summary>
public record DocumentIdentity(DocumentIdentityElement[] DocumentIdentityElements)
{
    // Use this synthetic, hardcoded identity JsonPath for all descriptors
    public static readonly JsonPath DescriptorIdentityJsonPath = new("$.descriptor");
}

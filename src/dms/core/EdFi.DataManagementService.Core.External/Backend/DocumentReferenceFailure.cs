// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Core.External.Backend;

public enum DocumentReferenceFailureReason
{
    Missing,
    IncompatibleTargetType,
}

public sealed record DocumentReferenceFailure(
    JsonPath Path,
    BaseResourceInfo TargetResource,
    DocumentIdentity DocumentIdentity,
    DocumentReferenceFailureReason Reason
)
{
    public static DocumentReferenceFailure From(
        DocumentReference documentReference,
        DocumentReferenceFailureReason reason
    ) =>
        new(
            Path: documentReference.Path,
            TargetResource: documentReference.ResourceInfo,
            DocumentIdentity: documentReference.DocumentIdentity,
            Reason: reason
        );
}

// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.Security;

public static class AuthorizationStrategyNameConstants
{
    public const string NamespaceBased = "NamespaceBased";
    public const string RelationshipsWithEdOrgsOnly = "RelationshipsWithEdOrgsOnly";
    public const string RelationshipsWithEdOrgsAndPeople = "RelationshipsWithEdOrgsAndPeople";
    public const string RelationshipsWithStudentsOnly = "RelationshipsWithStudentsOnly";
    public const string RelationshipsWithStudentsOnlyThroughResponsibility =
        "RelationshipsWithStudentsOnlyThroughResponsibility";
    public const string NoFurtherAuthorizationRequired = "NoFurtherAuthorizationRequired";
}

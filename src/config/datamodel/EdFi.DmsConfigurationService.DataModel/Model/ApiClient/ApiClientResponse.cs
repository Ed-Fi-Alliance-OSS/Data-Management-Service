// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.ComponentModel;

namespace EdFi.DmsConfigurationService.DataModel.Model.ApiClient;

public class ApiClientResponse
{
    public int Id { get; set; }
    public int ApplicationId { get; set; }
    public required string ClientId { get; set; }
    public required Guid ClientUuid { get; set; }
    public required string Name { get; set; }
    public required bool IsApproved { get; set; }
    public int? CreatorOwnershipTokenId { get; set; }
    public List<int> OwnershipTokenIds { get; set; } = [];
    public List<int> DataStoreIds { get; set; } = [];

    [Description(
        "Whether the API client inherits its Application's education organizations and follows "
            + "later changes to them; false when it is narrowed to its own list."
    )]
    public bool InheritsApplicationEducationOrganizations { get; set; }

    [Description(
        "The API client's effective education organization ids, in ascending order: its "
            + "Application's current list when it inherits, its own list when it is narrowed. Never "
            + "null; an empty array means no education organization access, which a narrowed client "
            + "reaches only when every one of its education organizations is removed from its "
            + "Application."
    )]
    public List<long> EducationOrganizationIds { get; set; } = [];
}

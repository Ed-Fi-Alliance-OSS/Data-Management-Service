// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Core.Model;

// A string type branded as a DocumentUuid, which is a UUID that identifies a document.
// A UUID string is of the form 00000000-0000-4000-8000-000000000000
internal record struct DocumentUuid(string Value) : IDocumentUuid;

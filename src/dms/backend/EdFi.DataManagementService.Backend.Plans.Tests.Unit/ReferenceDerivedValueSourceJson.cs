// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using EdFi.DataManagementService.Backend.External.Plans;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

internal static class ReferenceDerivedValueSourceJson
{
    public static ReferenceDerivedValueSourceDto Encode(ReferenceDerivedValueSourceMetadata source)
    {
        ArgumentNullException.ThrowIfNull(source);

        return new ReferenceDerivedValueSourceDto(
            BindingIndex: source.BindingIndex,
            ReferenceObjectPath: source.ReferenceObjectPath.Canonical,
            IdentityJsonPath: source.IdentityJsonPath.Canonical,
            ReferenceJsonPath: source.ReferenceJsonPath.Canonical
        );
    }

    public static void Write(Utf8JsonWriter writer, ReferenceDerivedValueSourceDto value)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);

        writer.WriteStartObject();
        writer.WriteNumber("binding_index", value.BindingIndex);
        writer.WriteString("reference_object_path", value.ReferenceObjectPath);
        writer.WriteString("identity_json_path", value.IdentityJsonPath);
        writer.WriteString("reference_json_path", value.ReferenceJsonPath);
        writer.WriteEndObject();
    }

    public static void Write(Utf8JsonWriter writer, ReferenceDerivedValueSourceMetadata value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Write(writer, Encode(value));
    }
}

// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Tests.E2E.Management;

public class KafkaTestConfiguration
{
    public string BootstrapServers { get; set; } = "localhost:9092";

    public string ConsumerGroupId { get; set; } = $"dms-e2e-tests-{Guid.NewGuid().ToString("N")[..8]}";

    public string[] Topics { get; set; } = ["edfi.dms.document"];

    public static bool IsEnabled => true;
}

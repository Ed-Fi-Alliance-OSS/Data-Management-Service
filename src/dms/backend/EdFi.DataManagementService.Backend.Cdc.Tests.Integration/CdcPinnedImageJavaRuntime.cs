// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Integration;

internal static class CdcPinnedImageJavaRuntime
{
    internal const string ClassPathScript = """
        class_path="$(find /kafka /opt/kafka /usr/share/java /usr/share/confluent-hub-components /debezium -name '*.jar' 2>/dev/null | tr '\n' ':')"
        test -n "${class_path}"
        """;

    internal const string ClassProbeSource = """
        class CdcTemplateClassProbe {
            public static void main(String[] args) {
                try {
                    for (String name : args) Class.forName(name);
                } catch (ClassNotFoundException failure) {
                    System.exit(41);
                } catch (LinkageError failure) {
                    System.exit(42);
                }
            }
        }
        """;
}

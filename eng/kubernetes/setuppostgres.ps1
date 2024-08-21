# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

$sourcePort="8083"
$sinkPort="8084"

$podName = kubectl get pods -o=name | grep postgres- | sed "s/^.\{4\}//"

Write-Host $podName

kubectl exec $podName -- /bin/sh -c "echo `"host replication postgres kafka-connect-source trust`" >> /var/lib/postgresql/data/pg_hba.conf"
kubectl exec $podName -- /bin/sh -c "echo `"wal_level = logical`" >> /var/lib/postgresql/data/postgresql.conf"

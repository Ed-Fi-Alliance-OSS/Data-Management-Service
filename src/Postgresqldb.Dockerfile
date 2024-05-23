# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

FROM postgres:16.2-alpine3.19@sha256:b89a4e92591810eac1fbce6107485d7c6b9449df51c1ccfcfed514a7fdd69955

ENV POSTGRES_USER=${POSTGRES_USER}
ENV POSTGRES_PASSWORD=${POSTGRES_PASSWORD}
ENV POSTGRES_DB=postgres

USER postgres

EXPOSE 5432

CMD ["postgres"]

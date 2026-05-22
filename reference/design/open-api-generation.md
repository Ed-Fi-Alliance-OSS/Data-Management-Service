# DMS Feature: Generate OpenAPI Documentation

OpenAPI documentation for the Data Management Service (DMS) can be generated
through two methods.

The first method involves a translator that converts the `ApiSchema.json` file
into an OpenAPI specification, implemented using .NET and the
`Microsoft.OpenApi` library. The translator can run as a standalone tool or be
integrated directly into the DMS application.

The second method involves generating the OpenAPI specification alongside the
`ApiSchema.json` file within the MetaEd application.

This document covers method one.

## Approach

The translator is implemented in .NET (C#) using types from the
`Microsoft.OpenApi.Models` namespace.

It parses `ApiSchema.json` to extract the information needed to populate OpenAPI
models: `OpenApiPaths`, `OpenApiComponents`, `OpenApiOperation`,
`OpenApiPathItem`, and `OpenApiResponses`. These are assembled into an
`OpenApiDocument` and serialized as OpenAPI 3.0 JSON.

## Implementation

The tool is implemented as a standalone CLI in
`src/dms/clis/EdFi.DataManagementService.OpenApiGenerator`. It accepts a core
`ApiSchema.json` path and an optional extension schema path as command-line
arguments, and writes the generated OpenAPI 3.0 JSON to standard output.

# DMS Feature: Generate OpenAPI Documentation

Generating OpenAPI documentation for the Data Management Service (DMS) can be
accomplished through two methods.

The first method involves implementing a translator capable of converting the
`ApiSchema.json` file into an OpenAPI specification. This translator can be
developed using .NET technologies and leveraging the capabilities provided by
the `Microsoft.OpenApi` library. The translator can be integrated directly into
the DMS application or developed as a standalone tool.

The second method implies generating OpenAPI specification file(s) alongside the
generation of the `ApiSchema.json` file within the MetaEd application.

For the purpose of this document, we will explain the details of method one.

## Approach

The OpenAPI documentation translator will be developed using .NET and the C#
programming language. The necessary models for this task are available within the
`Microsoft.OpenApi.Models` namespace.

This approach involves parsing and reading the contents of the `ApiSchema.json`
file to extract the pertinent details required for generating OpenAPI models.

OpenAPI models such as `OpenApiPaths`, `OpenApiComponents`, `OpenApiOperation`,
`OpenApiPathItem`, and `OpenApiResponses` serve as representations of different
aspects of the API.

Once all the necessary instances of these models are generated, they are
assembled into an `OpenApiDocument` object, which can then be serialized into
JSON format adhering to OpenAPI version 3.0.

A sample translator implementation can be found in the
[spike-open-api-generation](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/tree/spike-open-api-generation)
branch.

## Remaining Work

To fully implement the tool for generating OpenAPI documentation for the DMS,
the following tasks need to be addressed:

1. **Refactor the sample code** — Break down the sample code into multiple
   methods and classes to improve readability, maintainability, and reusability.
   Consider organizing the code into logical components such as parsers,
   generators, and serializers.

2. **Implement paths and operations for all HTTP actions** — Expand the
   functionality to cover all HTTP actions (GET, POST, PUT, DELETE, etc.).
   Implement logic to generate OpenAPI paths and operations for each HTTP
   action, handling parameters, requests, and responses accordingly.

3. **Handle ref types, descriptors, and links** — Include logic to handle
   reference types, descriptors, and links specified in the `ApiSchema.json`
   file. Ensure that referenced types and descriptors are properly resolved as
   schemas and included in the generated OpenAPI documentation.

4. **Define appropriate responses** — Define and include appropriate responses
   for each HTTP action based on the expected behavior and outcomes of the API
   endpoints. Ensure that responses include relevant content and status codes.

5. **Add security schema definition** — Define security schemas and requirements
   for accessing the API endpoints.

# Getting Started Using the Ed-Fi Data Management Service Platform

This file is a lab walkthrough on using the Ed-Fi Data Management (DMS)
Platform. These instructions rely on a compatible `docker compose` command, for
example coming from Docker Desktop or Podman. See the [docs/](./docs/) for
additional developer information.

There are two parts to the lab:

* This markdown file provides context and instructions on running the DMS Platform.
* File `getting-started.http` provides annotated HTTP commands demonstrating how
  to interact with the DMS and the DMS Configuration Service.

> [!NOTE]
> Goals for this document:
>
> * Describe architecture
> * Get started with Docker Desktop or Podman
> * Create client credentials using the Configuration Service
> * Authenticate with DMS using the Discovery API
> * Create some thing
> * Point out how to auto-generate client SDK from Open API specification.
> * Can HTTP requests be copied and pasted as Python, etc.?
> * Note that this works best with the [Rest Client
>   extension](https://marketplace.visualstudio.com/items?itemName=humao.rest-client).
>   Maybe others that work with .http?

## Pre-Requisites

These instructions have been tested in Windows with current (April, 2025)
versions of both Docker Desktop and Podman. This repository uses PowerShell for
scripting, which _should_ work on any OS where PowerShell Core 7+ is installed.

> [!TIP]
> If using Podman without Docker in Windows, you can create either
>
> 1. Find and replace "docker compose" with "podman compose" in the `eng/docker-compose` directory, or
> 2. In Windows, create a `docker.cmd` file containing the following command:
>    `podman ...` and add the location of this file into your Path environment
>    variables.

> [!WARNING]
> I forgot the command, need to look it up.

The companion file [getting-started.http] can be executed in VS Code with the
`humao.rest-client` or similar extension. Visual Studio and Rider also have
support for this file format. This file contains all of the HTTP commands found
this lab exercise. In VS Code with Rest Client, you can generate Curl commands
from the `.http` file or generate code snippets in over a dozen languages.

We use the `bierner.markdown-mermaid` extension in VS Code for viewing Mermaid
diagrams in Code's built-in Markdown preview tool.

## Context

Elasticsearch is also available, as an alternative to OpenSearch shown in the C4
deployment diagram below. In summary:

1. There are two custom .NET applications in this repository:
   1. The Data Management Service (DMS), which is an "Ed-Fi API" application. It
      supports the following API definitions: Ed-Fi Resources API, Ed-Fi
      Descriptors API, and Ed-Fi Discovery API. It includes Ed-Fi Data Standard
      5.2 out of the box. It will be capable of supporting other Data Standard
      versions at a future date.
   2. The DMS Configuration Service, which implements a form of the Ed-Fi
      Management API, whose specification is derived from the legacy Ed-Fi Admin
      API 2 application.
2. Both systems rely on the open-source Keycloak identity provider for
   authentication and OAuth 2.0 compatible token management.
3. Both systems use PostgreSQL for online transaction processing (OLTP) data
   storage. All Ed-Fi Resources and Descriptors are stored together in a single
   table, called `dms.document`.
4. The DMS's database is replicated to OpenSearch via:
   1. Change data capture (CDC) using Debezium, which reads the transaction log
      to copy data from `dms.document`...
   2. and pushes these records into a Kafka topic called `edfi.dms.document`.
   3. A connector reads from the Kafka topic and writes the data into either
      OpenSearch or Elasticsearch (OpenSearch is the default).
5. The DMS uses the search database to support the "GET all" and "GET by query"
   HTTP requests.
6. There are also optional user interfaces for viewing data in Kafka or
   OpenSearch (not shown below).

```mermaid
C4Deployment
    Deployment_Node(network, "Private Network") {
        Deployment_Node(dms, "DMS Services") {
            Container(keycloak, "Keycloak")
            Container(dms, "Data Management Service")
            Container(config, "Configuration Service")
        }
        Deployment_Node(odsapi_c, "Kafka Services") {
            ContainerDb(kafka, "Kafka Server")
            Container(source, "Kafka Source Connector")
            Container(sink, "Kafka Sink Connector")
        }
        Deployment_Node(db, "PostgreSQL Databases") {
            ContainerDb(dmsdb, "DMS")
            ContainerDb(configdb, "DMS Config")
        }
        Deployment_Node(os, "OpenSearch") {
            ContainerDb(open, "OpenSearch")
        }
    }
    Rel(dms, dmsdb, "read/write")
    Rel(config, configdb, "read/write")
    Rel(dms, keycloak, "discover")
    Rel(config, keycloak, "discover")
    Rel(source, dmsdb, "read")
    Rel(source, kafka, "replicate")
    Rel(sink, kafka, "read")
    Rel(sink, open, "write")
    Rel(dms, open, "query")
    UpdateLayoutConfig($c4ShapeInRow="2", $c4BoundaryInRow="4")
```

## Start the Containers

In a terminal, switch to the `eng/docker-compose` directory. Create a new file
`.env` as a copy of `.env.example`. There is no need to modify the file for
local execution. However, please change the passwords if using for anything
other than firewalled local development.

```powershell
cd Data-Management-Service/eng/docker-compose
cp .env.example .env
```

Now, start all of the required services, building from source code, with the
following command. The .NET SDK is not required, as the build will occur inside
a container.

```powershell
./start-local-dms.ps1 -EnableConfig -EnableSearchEngineUI
```

This may take around a minute to startup. This script not only starts the
containers, it also calls an additional script for configuring Keycloak.

> [!TIP]
> Add `-SearchEngine ElasticSearch` to run Elasticsearch instead of OpenSearch.

Once started, try the following HTTP request, which will load the Ed-Fi
Discovery API endpoint from the DMS.

```http
curl http://localhost:8080
```

## Interacting with the Two Services

Please open [getting-started.http](./getting-started.http) for detailed
instructions and sample HTTP commands. If using the Rest Client extension, you
can right-click on any command to generate a Curl equivalent or a code snippet
in one of more than a dozen supported languages, including C# and Python.

## Modifying the Configuration

Explore the [.env](./eng/docker-compose/.env) file you just create to see what
configuration options are available; however, most of them should not be
altered. After editing the `.env`, stop and then restart the containers.

## Stopping the Containers

When you are ready to stop the containers, append the `-d` ("down") flag to the
command:

```powershell
./start-local-dms.ps1 -EnableConfig -EnableSearchEngineUI -d
```

And to shut down and delete all data, add the `-v` ("volumes") flag. This is
useful when you need to start over with a clean slate.

```powershell
./start-local-dms.ps1 -EnableConfig -EnableSearchEngineUI -d -v
```

## Create Client Credentials Using the Configuration Service

```http
###
@configToken={{configTokenRequest.response.body.access_token}}

###
# @name createVendor
POST http://localhost:{{configPort}}/v2/vendors
Content-Type: application/json
Authorization: bearer {{configToken}}

{
    "company": "Demo Vendor",
    "contactName": "George Washington",
    "contactEmailAddress": "george@example.com",
    "namespacePrefixes": "uri://ed-fi.org
}
```

```http
###
@vendorLocation={{createVendor.response.headers.location}}

### Retrieve the vendor so that we can extract the Id
# @name getVendor
GET {{vendorLocation}}
Authorization: bearer {{configToken}}
```

> [!WARNING]
> Provide a link to something else to give more information about namespaces?
> Or explain right here?

Next, create an Application. The response to this request will have the newly
generated `client_id` and `client_secret` needed for accessing the DMS.

```http
###
# @name edOrgApplication
POST http://localhost:{{configPort}}/v2/applications
Content-Type: application/json
Authorization: bearer {{configToken}}

{
    "vendorId": {{vendorId}},
    "applicationName": "For ed orgs",
    "claimSetName": "E2E-RelationshipsWithEdOrgsOnlyClaimSet",
    "educationOrganizationIds": [ 255, 255901 ]
}
```

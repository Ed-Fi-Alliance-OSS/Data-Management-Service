# Docker files details

## Dockerfile

The [Dockerfile](../src/Dockerfile) in `src` builds the Ed-Fi Data Management
Service (DMS) image directly from source code, and [Nuget.Dockerfile](../src/Nuget.Dockerfile)
build the image from the pre-built NuGet packages. Both versions include both the API
application and the database installer. The source code version is primarily used in local and 
end-to-end testing; the NuGet package version is intended for production-like environments
and for publishing to [Docker Hub](https://hub.docker.com/u/edfialliance).

The runtime base image is `mcr.microsoft.com/dotnet/aspnet` on Alpine, running
.NET 8. This image is maintained by Microsoft and considered trusted. Use of the
Alpine distribution means that the final image will be smaller than with other
distributions, and the Alpine images tend to have fewer vulnerabilities.

> [!TIP]
> There is no separate container for the database images. If you start the DMS
> container with `NEEDS_DATABASE_SETUP=true` _and_ with valid administrative
> credentials, it will install the database tables instead of running the API
> application. You can start any PostgreSQL container, or run PostgreSQL at
> any other location that is accessible from inside your Docker network.

## Environment Variables

The following environment variables are needed when starting the Data Management
Service container.

```none
NEED_DATABASE_SETUP=<Flag (true or false) to decide whether the DMS database setup needs to be executed as part of the container setup>
POSTGRES_ADMIN_USER=<Admin user to use with database setup>
POSTGRES_ADMIN_PASSWORD=<Admin password to use with database setup>
POSTGRES_USER=<Non-admin user to use for accessing the database from the DMS application>
POSTGRES_PASSWORD=<Non-admin user password>
POSTGRES_PORT=<Port for postgres server Eg. 5432>
POSTGRES_HOST=<DNS or IP address of the PostgreSQL Server, i.e. sql.somedns.org Eg. 172.25.32.1>
LOG_LEVEL=<serilog log level i.e. Information>
OAUTH_TOKEN_ENDPOINT=<Authentication service url>
BYPASS_STRING_COERCION=<Boolean whether to bypass coercion of boolean and numeric values represented as strings to their natural type. Eg. "true" = true>
```

For example, you might have a `.env` file like the following:

```none
NEED_DATABASE_SETUP=true
POSTGRES_ADMIN_USER=postgres
POSTGRES_ADMIN_PASSWORD=P@ssw0rd53
POSTGRES_USER=dms
POSTGRES_PASSWORD=P@ssw0rd
POSTGRES_PORT=5432
POSTGRES_HOST=localhost
LOG_LEVEL=Information
OAUTH_TOKEN_ENDPOINT=http://localhost:8080/oauth/token
BYPASS_STRING_COERCION=false
```

## Orchestration

We provide a Kubernetes-orchestrated local deployment out of the box. See
[kubernetes/README.md](../src/deployments/kubernetes/) for detailed
instructions.

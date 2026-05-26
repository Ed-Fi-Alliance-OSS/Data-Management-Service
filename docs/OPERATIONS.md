# Operational Concerns

These notes generally apply to any application in the DMS platform.

## Configuration

See [Configuration](./CONFIGURATION.md)

## Deployment

The DMS software is designed for operation on-premises or using cloud-based
managed services. However, the Ed-Fi Alliance will not necessarily provide
detailed deployment orchestration for various environments.

The applications are built in a container-first fashion. A Kubernetes topology,
and potentially Docker Compose topology, is provided for basic testing and
demonstration purposes. These artifacts might be useful for production
deployments into Kubernetes. Anyone using them as such should review carefully,
particularly with respect to security concerns.

Although the application testing process will focus on the container-based
integration, these applications should be able to run on "bare metal" (or
virtual machine) without a container.

## Logging

See [Logging Policy](./LOGGING.md)

## Observability

Observability is closely related to logging, but goes beyond it. Open Telemetry
is an emerging standard for observability.

The following article provides additional information about Open Telemetry and
how it might be useful in the DMS platform. The article references the Project
Meadowlark application stack, but is equally applicable here: [What Is Open
Telemetry?](https://github.com/Ed-Fi-Exchange-OSS/Meadowlark/blob/main/docs/design/open-telemetry/README.md)

## Security

### Transport Encryption

Those who are hosting the application are strongly encouraged to use TLS binding
at least at the gateway level. When running a container network, mutual TLS will
provide greater security in case someone is able to elevate privileges on one of
the services.

### Rate Limiting

Rate limiting should be employed to limit both denial of service (DoS) attacks
and brute-force authentication attempts. While the application gateway is the
best place to apply rate limiting, the Ed-Fi Data Management Service and Ed-Fi
Configuration Service will both have built-in rate limiting capabilities to fall
back on.

### Authentication and Authorization

See [Roles and Scopes](./ROLES-SCOPES.md)

# Client credential expiration and rotation

Keycloak does not support client secret rotation as a default feature. However,
secret rotation is available as a preview feature, but it is disabled by
default. To enable secret rotation, administrators need to manually configure
and activate this feature within Keycloak settings.

## Enable client secret rotation feature on docker

To enable client secret rotation in Keycloak when running in Docker, add this
under environment in your docker-compose.yml

```yml
  environment:
      KC_FEATURES: client-secret-rotation
```

## Configure client secret rotation on Keycloak

### Create a Client Profile with Secret Rotation Executor

* Navigate to Realm Settings > Client Policies > Profiles.
* Click on Create client profile and provide a name and description.
* After saving, add an executor of type `secret-rotation`.
* Configure the executor with parameters such as:
  1. Secret Expiration: Maximum duration (in seconds) a secret remains valid.
  2. Rotated Secret Expiration: Duration (in seconds) a rotated (previous)
     secret remains valid.
  3. Remain Expiration Time: Time window (in seconds) before secret expiration
     during which updates trigger rotation.

### Create a Client Policy

* Still under Client Policies, navigate to the Policies tab.
* Click on Create client policy and provide a name and description.
* Add conditions to specify which clients the policy applies to (e.g., clients
  with a specific access type or role).
* Associate the previously created client profile with this policy.

### Apply the Policy to Existing Clients

* During the creation of new clients, if the client secret rotation policy is
  active, the behavior will be applied automatically.

* For existing clients to adopt the new secret rotation behavior, an update
action is required: Navigate to `Clients > [Select Client] > Credentials` tab.
Click on Regenerate Secret to trigger the rotation mechanism as per the defined
policy.

For detailed guidance, refer to the [Keycloak Server Administration Guide on
Client Secret
Rotation](https://www.keycloak.org/docs/latest/server_admin/index.html#_proc-secret-rotation).

## Customize rotation policy based on the client type/ role

Keycloak allows you to define policies that only apply to clients with a
specific role. This ensures that only selected clients follow the secret
rotation rules.

For the role-based condition, follow the steps to [Create a Client Profile with
Secret Rotation
Executor](#create-a-client-profile-with-secret-rotation-executor) create profile. Once the
profile setup is complete, proceed with the following steps:

### Client Policy with Client Role Condition

* Go to Realm Settings > Client Policies > Policies.
* Click Create Client Policy and provide a name (e.g., "Enforce Secret Rotation
  by Role").
* Under Conditions, add:
  * Client Role Condition → Select a specific client role (e.g., "dms-client" or
    "config-service-app").
* Under Profiles, select the previously created profile ("Secret Rotation
  Policy").
* Click Save.

## Changes on DMS side for enforcing secret rotation

No changes are needed on the DMS application. Whenever a user attempts to use an
expired secret with the following token endpoint:

* <http://localhost:8045/realms/edfi/protocol/openid-connect/token>

Keycloak will automatically reject the request and return an error response,
ensuring that expired secrets are not used for authentication.

> [!NOTE]
> A Client Secret Rotation Policy can be created for the realm by
> updating the existing script
> [setup-keycloak.ps1](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/blob/main/eng/docker-compose/setup-keycloak.ps1)
> to enforce secret expiration and rotation.

## Changes on CMS side for enforcing secret rotation

The vendor application create response will also include secret_expiry, similar to token expiry.
We can make this configurable in the CMS appsettings (eg., 30 days)

  ```json

  {
      "id": 3,
      "key": "361994b9-5a74-4f14-bd9e-6a0bc6309d10",
      "secret": "mPbyZnj2CqH3RvnzOBVYDJZE5INBi88e",
      "secret_expires_on": "2025-04-01T00:00:00Z"
  }
  ```

The `secret_expires_on` will be useful for vendor applications to track the
expiration of their client secrets, enabling them to proactively update
credentials before expiration and prevent authentication failures.

## Seamless secret rotation

Develop a PowerShell script or console application to assist administrators in
managing seamless secret rotation. The script/application will:

* Retrieve client secrets and check their creation dates.
* Identify both expired secrets and secrets that are about to expire within a
  configurable number of days.
* Rotate secrets using the Keycloak API endpoints or
  `/v2/applications/{{id}}/reset-credential` CMS endpoint.
* Optionally delete old secrets after rotation to maintain security.
* Provide configurable settings for the rotation threshold, allowing proactive
  credential updates before expiration.
* Schedule the script/ application execution for continuous compliance.

For more details on the Keycloak API endpoints, please refer to the [Keycloak
REST API](https://www.keycloak.org/docs-api/latest/rest-api/index.html).

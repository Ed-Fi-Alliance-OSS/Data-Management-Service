# Kubernetes Deployment

This folder provides a basic setup of a set of
[Kubernetes](https://kubernetes.io/) files to setup a cluster.

## Local Development

For local development, you need to use
[minikube](https://minikube.sigs.k8s.io/docs/start/).

* After installing, run `minikube start` to setup minikube in your local
  environment.
* Set the docker environment inside minikube:

`eval $(minikube docker-env)` for Unix shells or `minikube docker-env |
Invoke-Expression` in PowerShell

* From the location of the [Dockerfile](src\Dockerfile), Build the data
management service image: `docker build -t
local/edfi-data-management-service:latest .`

> [!WARNING]
> The [deployment file](./data-management-service-deployment.yaml#L21) has a
> policy of Never pull, which means that will use the locally built image instead of
> trying to pull the image from Docker Hub which is the default behavior, remove
> once the image is published to the registry.

* Set the terminal in the */deployments/kubernetes* folder.
* Create an app-secret.yaml file with a encrypted password, see
  [Secrets](https://kubernetes.io/docs/concepts/configuration/secret/) for more
  information.
* Run `kubectl apply -f .` to apply all files.
* After done, inspect with `kubectl get pods`, and verify that all pods have
  status **RUNNING** (This can take a couple of minutes).

This will start the kubernetes infrastructure to run without exposing any
connection to the external network. When installing in a cloud provider the
clouds Load Balancing service will take care of making the connection to the
cluster, by opening a connection to the
[data-management-service](data-management-service.yaml).

This container has the type LoadBalancer, meaning that this is the entry point
for the load balancer provider.

To test this in the local environment, we need to open *tunnel* between the
local network and the Kubernetes cluster. To do so, run `minikube service
data-management-service --url`.

Copy the URL and connect to the Data Management Service.

### Useful commands

| Command                                         | Description                      |
| ----------------------------------------------- | ---------------------------------|
| `minikube start`                                | Start minikube cluster           |
| `minikube delete`                               | Clean minikube cluster           |
| `kubectl get pods`                              | Get all pods                     |
| `kubectl get deployments`                       | Get all deployments              |
| `kubectl get services`                          | Get all services                 |
| `kubectl describe service postgres`             | Get description of a service     |
| `kubectl exec -it POD_NAME -- psql -U postgres` | Execute a command in a pod       |
| `kubectl logs POD_NAME`                         | Get the log information of a pod |

> [!NOTE]
> In Kubernetes you can reference another pod by IP address or by hostname,
> where the host name is the name of the pod.

> [!IMPORTANT]
> At the moment, the postgres infrastructure is only for demo purposes and does not connect
> to the Data Management Service.

## File Description

The folder includes a series of YAML files to handle the Kubernetes Setup, this
includes:

* Data Management Service and Deployment: Building and deployment of Data
  Management Service pod
* Postgres Service, Deployment and Persistent Volume Claim: Building, deployment
  and volumes for Postgres pod.
* Config Map: Environment variables
* Secrets: Encrypted secret values example.

## Troubleshooting

### Network has an untrusted certificate

If you are running this on a corporate network with an untrusted certificate,
then:

1. Get a copy of the certificate in PEM format (typically a file ending in
   `.pem`, `.crt`, or `.cer`).
2. Copy that to `~/.minikube/certs` (Linxu) or
   `c:\users\<YOUR-USER>\.minikube\certs` (Windows).
3. If you previously started minikube, then `minikube delete`.
4. Run `minikube start --embed-certs`.

### Warning Message About Docker Context

If you get a message from `minikube` stating: `Unable to resolve the current
Docker CLI context "default"`:

1. `docker context list`
2. If Docker Desktop is running under a context called `desktop-linux`, then run
   `docker context use desktop-linux`.
3. Otherwise, reconnect to whatever other named instance is there (likely
   "default"): `docker context use default`.
4. Delete the minikube instance and start over.

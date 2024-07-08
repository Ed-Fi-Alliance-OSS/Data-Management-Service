# Kubernetes Deployment

This folder provides a basic setup of a set of
[Kubernetes](https://kubernetes.io/) files to setup a cluster.

## Initial Kubernetes Setup

## Option 1: Docker Desktop

Open Docker Desktop and click the Settings icon ⚙️. Look for Kubernetes on the
left. Then enable Kubernetes.

If you have previously used minikube, then change the Docker context in kubectl
using

```shell
kubectl config get-contexts
kubectl config use-context docker-desktop
```

For more information, see [Deploy on Kubernetes with Docker
Desktop](https://docs.docker.com/desktop/kubernetes/)

## Option 2: Minikube

1. Install [minikube](https://minikube.sigs.k8s.io/docs/start/).
2. After installing, run `minikube start` to setup minikube in your local
   environment.
3. Set the docker environment inside minikube:
   * PowerShell users: `minikube docker-env | Invoke-Expression`
   * Bash users: `eval $(minikube docker-env)`
  
Also see [Minikube Troubleshooting](#minikube-troubleshooting) below.
  
## Initializing Pods

### Build a Local Image

First, build a locally-tagged image of the Data Management Service. Two ways:

1. From `src` directory:

   ```shell
   docker build -t local/edfi-data-management-service:latest .
   ```

2. Or use the PowerShell script from the base directory:

   ```shell
   ./build.ps1 DockerBuild
   ```

### Configure the Deployment

In `eng/kubernetes/<folder>` where `<folder>` could be development or production:

> [!TIP]
> development folder will use the local data-management-service docker image `image: local/edfi-data-management-service:latest`;
> production folder will pull the published image from the docker hub `image: edfialliance/data-management-service:pre`

* Create an app-secret.yaml file with a encrypted password, see
  [Secrets](https://kubernetes.io/docs/concepts/configuration/secret/) for more
  information.
* Run `kubectl apply -f .` to apply all files.
* After done, inspect with `kubectl get pods`, and verify that all pods have
  status **RUNNING** (This can take a couple of minutes).

> [!TIP]
> The [deployment file](./data-management-service-deployment.yaml#L21) has a
> policy of Never pull, which means that will use the locally built image instead of
> trying to pull the image from Docker Hub which is the default behavior, remove
> once the image is published to the registry.

This will start the kubernetes infrastructure to run without exposing any
connection to the _external_ network. When installing in a cloud provider the
clouds Load Balancing service will take care of making the connection to the
cluster, by opening a connection to the
[data-management-service](data-management-service.yaml).

This container has the type LoadBalancer, meaning that this is the entry point
for the load balancer provider. Access the Data Management Service at base
address [http://localhost:8080/](http://localhost:8080/.)

> [!TIP]
> If using minikube, you may need to open a tunnel between the local network
> and the Kubernetes cluster: `minikube service data-management-service --url`.
> The command will run continuously until you cancel it. Copy the URL displayed
> in the terminal and use it to connect to the Data Management Service.

## Useful Commands

| Command                                                            | Description                                         |
| -------------------------------------------------------------------| ----------------------------------------------------|
| `minikube start`                                                   | Start minikube cluster                              |
| `minikube delete`                                                  | Clean minikube cluster                              |
| `kubectl get pods`                                                 | Get all pods                                        |
| `kubectl get deployments`                                          | Get all deployments                                 |
| `kubectl get services`                                             | Get all services                                    |
| `kubectl describe service postgres`                                | Get description of a service                        |
| `kubectl exec -it POD_NAME -- psql -U postgres`                    | Execute a command in a pod                          |
| `kubectl logs POD_NAME`                                            | Get the log information of a pod                    |
| `kubectl set image deployment/my-deployment mycontainer=myimage`   | Update the current image for an existing deployment |

> [!NOTE]
> In Kubernetes you can reference another pod by IP address or by hostname,
> where the host name is the name of the pod.

## File Description

The folder includes a series of YAML files to handle the Kubernetes Setup, this
includes:

* Data Management Service and Deployment: Building and deployment of Data
  Management Service pod
* Postgres Service, Deployment and Persistent Volume Claim: Building, deployment
  and volumes for Postgres pod.
* Config Map: Environment variables
* Secrets: Encrypted secret values example.

## Minikube Troubleshooting

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

### Pods image status

If you are facing the following errors with the Pods Status `kubectl get pods`

`ErrImagePull` or `ImagePullBackOff`

1. Review the status of the image, if you are using the local version make sure the image already exist; and if you are using the production configuration make sure the `image name` is valid. 
2. Another useful command to get more details about the Pod Status `kubectl describe <POD_NAME>`

# Chaos Mesh (Chaos Engineering)

[Chaos Mesh](https://chaos-mesh.org/) can be deployed to the cluster to inject faults and test resilience.

## Install Chaos Mesh

```bash
helm repo add chaos-mesh https://charts.chaos-mesh.org
helm repo update
helm install chaos-mesh chaos-mesh/chaos-mesh \
  -n=chaos-mesh --create-namespace \
  --set dashboard.service.type=LoadBalancer \
  --set dashboard.service.port=80
```

## Access the Chaos Mesh Dashboard

For kind clusters, use port-forwarding:

```bash
kubectl port-forward svc/chaos-dashboard -n chaos-mesh 2333:2333
```

Then open `http://localhost:2333` in your browser.

## Service Account & Token

The service account, cluster role binding, and long-lived token secret are managed as part of the demo manifests in `kind/demo-services/chaos-mesh-secret.yaml`. They are applied automatically when you run:

```bash
make deploy-kind
```

The token is stored in the `chaos-mesh-account-token` Secret in the `catalyst-order-workflow-demo` namespace and injected into the notification service pod as `CHAOS_MESH_TOKEN`.

To retrieve the token (e.g. to log into the dashboard):

```bash
kubectl get secret chaos-mesh-account-token \
  -n catalyst-order-workflow-demo \
  -o jsonpath='{.data.token}' | base64 -d
```

## Chaos Experiment

The experiment definition is stored in the `chaos-experiment-config` ConfigMap (`kind/demo-services/chaos-experiment-config.yaml`) and mounted into the notification service at `/etc/chaos/experiment.json`. To change the experiment, edit the ConfigMap and redeploy — no code changes required.

The default experiment kills all pods with label `app=inventory-service` in the `catalyst-order-workflow-demo` namespace.

## Triggering Chaos from the UI

The notification service dashboard has a lightning bolt button next to the Inventory Service status indicator. Clicking it starts or stops the experiment by calling `POST /chaos/start` or `DELETE /chaos/stop` on the notification service.

## Triggering Chaos Manually

```bash
# Start
kubectl exec -n catalyst-order-workflow-demo deploy/notification-service -- \
  curl -s -X POST http://localhost:8080/chaos/start

# Stop
kubectl exec -n catalyst-order-workflow-demo deploy/notification-service -- \
  curl -s -X DELETE http://localhost:8080/chaos/stop
```

## Cleanup

If you previously created resources manually in the `chaos-mesh` namespace, remove them:

```bash
kubectl delete serviceaccount chaos-mesh-account -n chaos-mesh
kubectl delete clusterrolebinding chaos-mesh-account-binding
```

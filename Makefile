CLUSTER_NAME  := catalyst-demo
REGISTRY ?= localhost:5001
TAG ?= latest
KIND_CONTEXT ?= kind-catalyst-demo

SERVICES = order-manager inventory-service notification-service
DOCKERFILES = OrderManager/Dockerfile InventoryService/Dockerfile NotificationService/Dockerfile

.PHONY: all build push build-push deploy-kind create-order \
	build-order-manager build-inventory-service build-notification-service \
	push-order-manager push-inventory-service push-notification-service \
	setup kind-create dapr-install redis-install redis-wait chaos-mesh-install chaos-mesh-wait clean clean-deploy \
	redis-flush

NAMESPACE ?= catalyst-order-workflow-demo

all: build-push deploy-kind

# Individual build targets
build-order-manager:
	docker build -t $(REGISTRY)/order-manager:$(TAG) -f OrderManager/Dockerfile .

build-inventory-service:
	docker build -t $(REGISTRY)/inventory-service:$(TAG) -f InventoryService/Dockerfile .

build-notification-service:
	docker build -t $(REGISTRY)/notification-service:$(TAG) -f NotificationService/Dockerfile .

# Individual push targets
push-order-manager:
	docker push $(REGISTRY)/order-manager:$(TAG)

push-inventory-service:
	docker push $(REGISTRY)/inventory-service:$(TAG)

push-notification-service:
	docker push $(REGISTRY)/notification-service:$(TAG)

# Aggregate targets
build: build-order-manager build-inventory-service build-notification-service

push: push-order-manager push-inventory-service push-notification-service

build-push: build push

deploy-kind:
	kubectl config use-context $(KIND_CONTEXT)
	kubectl apply -f kind/demo-services/

create-order:
	kubectl config use-context $(KIND_CONTEXT)
	kubectl run curl-order --rm -i --restart=Never \
		--namespace=$(NAMESPACE) \
		--image=curlimages/curl -- \
		curl -s -X POST http://order-manager.$(NAMESPACE).svc.cluster.local/order \
		-H "Content-Type: application/json" \
		-d '{"customerId":"customer-1","items":[{"productId":"prod-001","quantity":2,"price":29.99}]}'

# ── Cluster setup internals ──────────────────────────────────────────────────
setup: kind-create dapr-install redis-install redis-wait chaos-mesh-install chaos-mesh-wait
	@echo ""
	@echo "✓ Cluster ready. Run: make build push deploy-kind"

kind-create:
	@if kind get clusters 2>/dev/null | grep -q "^$(CLUSTER_NAME)$$"; then \
		echo "Kind cluster '$(CLUSTER_NAME)' already exists"; \
	else \
		echo "Creating kind cluster '$(CLUSTER_NAME)' with local registry..."; \
		CLUSTER_NAME=$(CLUSTER_NAME) ./kind/kind-with-registry.sh; \
	fi

dapr-install:
	@echo "Installing Dapr on Kubernetes..."
	dapr init --runtime-version 1.17.1 --kubernetes --wait
	@echo "✓ Dapr installed"

redis-install:
	@echo "Installing Redis via Helm..."
	@helm repo add bitnami https://charts.bitnami.com/bitnami 2>/dev/null || true
	@helm repo update bitnami 2>/dev/null || true
	@if helm list -n default | grep -q "^redis"; then \
		echo "Redis already installed"; \
	else \
		helm install redis bitnami/redis \
			--set auth.enabled=false \
			--set architecture=standalone \
			--set "master.resources.requests.memory=256Mi" \
			--set "master.resources.limits.memory=512Mi" \
			--set "master.extraFlags={--maxmemory 400mb,--maxmemory-policy allkeys-lru,--save ''}" \
			--namespace default; \
	fi

redis-wait:
	@echo "Waiting for Redis to be ready..."
	kubectl rollout status statefulset/redis-master --timeout=120s -n default
	@echo "✓ Redis ready"

redis-flush:
	@echo "Flushing all Redis keys..."
	kubectl exec -n default statefulset/redis-master -- \
		sh -c 'redis-cli --scan | xargs -r redis-cli DEL'
	@echo "✓ Redis flushed"

chaos-mesh-install:
	@echo "Installing Chaos Mesh..."
	helm repo add chaos-mesh https://charts.chaos-mesh.org
	helm repo update chaos-mesh
	helm install chaos-mesh chaos-mesh/chaos-mesh \
	-n=chaos-mesh --create-namespace \
	--set dashboard.service.type=ClusterIP
	kubectl patch service chaos-dashboard -n chaos-mesh --type='json' \
	-p='[{"op":"replace","path":"/spec/ports/0/port","value":80}]'
	@echo "✓ Chaos Mesh installed"

chaos-mesh-wait:
	@echo "Waiting for Chaos Mesh to be ready..."
	kubectl rollout status deployment/chaos-controller-manager --timeout=120s -n chaos-mesh
	kubectl rollout status deployment/chaos-dashboard --timeout=120s -n chaos-mesh
	@echo "✓ Chaos Mesh ready"

# ── Cleanup ──────────────────────────────────────────────────────────────────

clean:
	@echo "Deleting kind cluster '$(CLUSTER_NAME)'..."
	kind delete cluster --name $(CLUSTER_NAME)
	@echo "✓ Cluster deleted"

clean-deploy:
	kubectl delete -f k8s/ --ignore-not-found
	kubectl delete -f components/ --ignore-not-found
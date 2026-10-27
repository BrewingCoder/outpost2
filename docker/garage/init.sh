#!/bin/sh
# Idempotent Garage bootstrap: single-node layout, fixed dev key, buckets.
# Runs as a one-shot container against the garage service.
set -e

G="/garage -c /etc/garage.toml"
KEY_ID="${OP2_S3_ACCESS_KEY:-GK0000000000000000000op2}"
KEY_SECRET="${OP2_S3_SECRET_KEY:-0000000000000000000000000000000000000000000000000000000000op2dev}"

echo "waiting for garage rpc..."
i=0
until $G status >/dev/null 2>&1; do
  i=$((i + 1))
  [ "$i" -gt 60 ] && echo "garage never came up" && $G status && exit 1
  sleep 1
done
echo "garage is up"

# --- layout: assign this node a zone and capacity, once ---
if $G layout show 2>/dev/null | grep -qi "^==== CURRENT CLUSTER LAYOUT" && \
   $G layout show 2>/dev/null | grep -qiE "dc1"; then
  echo "layout already applied"
else
  NODE=$($G status | awk '/==== HEALTHY NODES ====/{f=1;next} f && NF {print $1; exit}')
  echo "assigning layout to node $NODE"
  $G layout assign -z dc1 -c 10G "$NODE"
  VER=$($G layout show | awk '/version/{print $NF; exit}')
  $G layout apply --version "${VER:-1}"
  echo "layout applied"
fi

# --- key: import a FIXED dev key so .env and appsettings stay stable across rebuilds ---
if $G key info "$KEY_ID" >/dev/null 2>&1; then
  echo "key $KEY_ID already exists"
else
  echo "importing dev key $KEY_ID"
  $G key import --yes -n op2-dev "$KEY_ID" "$KEY_SECRET"
fi

# --- buckets ---
for B in op2-staging op2-attachments; do
  if $G bucket info "$B" >/dev/null 2>&1; then
    echo "bucket $B exists"
  else
    $G bucket create "$B"
    echo "bucket $B created"
  fi
  $G bucket allow --read --write --owner "$B" --key "$KEY_ID" >/dev/null
done

echo "garage ready: buckets op2-staging, op2-attachments; key $KEY_ID"
$G bucket list

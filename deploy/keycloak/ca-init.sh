#!/bin/sh
set -e

CA_SRC=/caddy-data/caddy/pki/authorities/local/root.crt
CA_DST=/ca-bundle/ca-certificates.crt

echo "Waiting for Caddy root CA..."
i=0
while [ ! -f "$CA_SRC" ]; do
  i=`expr $i + 1`
  if [ "$i" -ge 120 ]; then
    echo "FAIL: Caddy root CA not found after 120s"
    exit 1
  fi
  sleep 1
done

echo "Caddy root CA found, building bundle..."
cp /etc/ssl/certs/ca-certificates.crt "$CA_DST"
cat "$CA_SRC" >> "$CA_DST"
echo "CA bundle ready with Caddy root appended"
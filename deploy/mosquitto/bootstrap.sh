#!/bin/sh
# Throwaway Mosquitto broker with a self-signed TLS identity, generated fresh
# on every start so there is no cert volume to manage. Shared by
# docker-compose.yml and deploy/docker-compose.prod.yml; the mTLS lab needs
# certificates signed by the lab CA and uses deploy/mtls/mosquitto.conf instead.

apk add --no-cache openssl > /dev/null 2>&1
openssl req -x509 -newkey rsa:2048 -keyout /tmp/server.key \
  -out /tmp/server.crt -days 3650 -nodes -subj '/CN=mosquitto' \
  > /dev/null 2>&1
chmod 644 /tmp/server.key /tmp/server.crt

cat > /tmp/mosquitto.conf << 'CONF'
listener 1883
allow_anonymous true

listener 8883
allow_anonymous true
certfile /tmp/server.crt
keyfile /tmp/server.key

listener 9001
protocol websockets
allow_anonymous true

listener 9002
protocol websockets
allow_anonymous true
certfile /tmp/server.crt
keyfile /tmp/server.key

log_dest stdout
CONF

exec mosquitto -c /tmp/mosquitto.conf
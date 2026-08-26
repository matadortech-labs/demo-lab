# NGINX Target Components

The NGINX side has two controlled operations:

- `keyfactor-nginx-deploy` promotes staged certificate/key material, validates the NGINX configuration, reloads NGINX, and runs proof collection.
- `nginx-cert-status` compares staged, active-on-disk, and TLS-served certificate state and emits JSON/text evidence.

The full `/etc/nginx` snapshot from the source rebuild package is intentionally not published. Use the minimal vhost example in `config/` and adapt it to the target application. TLS 1.0/1.1 are intentionally not enabled in the public example.

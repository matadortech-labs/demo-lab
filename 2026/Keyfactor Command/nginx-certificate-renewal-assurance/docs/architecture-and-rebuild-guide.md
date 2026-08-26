# Architecture and Rebuild Guide

## Purpose

The renewal-assurance workflow closes the gap between **certificate issuance** and **application proof**. A successful issuance or certificate-store deployment does not by itself prove that the live application endpoint is serving the renewed certificate. This implementation adds a validation control loop around the native renewal process.

## Component responsibilities

### Keyfactor Command

Command initiates the workflow from a scheduled expiration alert, captures pre-renewal state, performs the native renewal and certificate-store push, waits at an automation gate, consumes current inventory/metadata, and sends the final assurance report.

Relevant repository assets:

- `command/exports/workflow.json`
- `command/exports/expiration-alert.json`
- `command/exports/certificate-collection.json`
- `command/exports/metadata-fields-KFValidation.json`
- `command/workflow-scripts/`
- `command/config/ApplicationRegistry.example.json`
- `command/job-complete-handler/EndpointValidationReleaseHandler.manifest.example.json`

### Universal Orchestrator

Universal Orchestrator runs the `Custom.EndpointValidation` job. The job invokes the validation profile, obtains target proof from NGINX, compares the served certificate with Command inventory, and writes validation metadata back to Command.

Relevant repository assets:

- `orchestrator/source/EndpointValidationCustomJob/`
- `orchestrator/scripts/`
- `orchestrator/config/ApplicationRegistry.example.json`
- `orchestrator/templates/`

### NGINX target

The target exposes only controlled local operations. `keyfactor-nginx-deploy` promotes staged certificate/key material into the active paths, validates the NGINX configuration, reloads NGINX, and invokes `nginx-cert-status`. `nginx-cert-status` compares staged, active-on-disk, and live-served certificate state.

Relevant repository assets:

- `nginx/scripts/keyfactor-nginx-deploy`
- `nginx/scripts/nginx-cert-status`
- `nginx/config/nginx-renewal-demo.conf.example`
- `nginx/sudoers/`

## End-to-end sequence

1. A scheduled expiration alert selects the NGINX certificate and launches the workflow.
2. `CapturePreRenewalEndpointCertificate` records the currently served endpoint certificate.
3. Command performs the native expiration renewal with certificate-store deployment enabled.
4. The workflow enters `WaitForEndpointValidation`.
5. The Command-side job-completion handler schedules the `EndpointValidation` custom job after the relevant store/inventory activity completes.
6. Universal Orchestrator invokes the configured validation profile.
7. The orchestrator reaches the NGINX host over SSH and runs `sudo -n /usr/local/bin/nginx-cert-status`.
8. The proof script validates staged vs. active vs. served certificate state.
9. Orchestrator compares endpoint proof with current Command inventory and updates `KFValidation_*` metadata.
10. The Command handler verifies the job result and releases the workflow gate only when the required validation conditions are satisfied.
11. Command prepares current inventory/metadata for presentation.
12. The workflow sends the application TLS renewal assurance report.

## Rebuild order

### 1. NGINX

Install the scripts:

```bash
sudo install -o root -g root -m 0755 nginx/scripts/nginx-cert-status /usr/local/bin/nginx-cert-status
sudo install -o root -g root -m 0750 nginx/scripts/keyfactor-nginx-deploy /usr/local/sbin/keyfactor-nginx-deploy
```

Create the expected directories and adapt certificate paths for the target application. Configure only the required `sudoers` commands and install the orchestrator's **public** SSH key for the validation account.

### 2. Universal Orchestrator

Copy the scripts under `orchestrator/scripts/` to the configured validation-script directory. Build/install the custom extension from `orchestrator/source/EndpointValidationCustomJob/` using the Keyfactor Orchestrator assemblies present on the target host. Recreate Command API credential material locally; never copy credentials from another environment.

Create the `EndpointValidation` custom job type using the reference JSON under `orchestrator/templates/`.

### 3. Keyfactor Command

Deploy the wrapper/local workflow scripts and create a profile-specific `ApplicationRegistry.json` from the supplied example. Configure the Command-side job completion handler from the example manifest, substituting the actual job type/store mappings created in the target environment.

Create the `KFValidation_*` metadata fields, NGINX certificate store, certificate collection, and expiration alert. Recreate/import the workflow using `command/exports/workflow.json`, updating instance-specific IDs and authentication references.

## Validation criteria

A PASS should mean more than “TLS connected.” At minimum verify:

- endpoint reachability
- TLS handshake success
- certificate capture from the live endpoint
- hostname/SAN suitability as required by the profile
- expected renewed certificate identity
- match between served certificate and Command inventory
- successful metadata update
- successful release of the workflow gate

## Production considerations

This repository is a demonstration/reference implementation. Before production use, review credential storage, SSH trust, least-privilege sudoers rules, timeout/retry behavior, API authentication, CA trust validation, exception handling, logging retention, workflow concurrency, and operational rollback behavior for the target environment.

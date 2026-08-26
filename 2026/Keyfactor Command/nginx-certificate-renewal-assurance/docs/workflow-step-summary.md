# Workflow Step Summary

## 1. Start-NOOP

Provides a clean workflow entry point and routes directly to pre-renewal capture.

## 2. Capture Pre-Renewal Endpoint Certificate

Runs the imported `CapturePreRenewalEndpointCertificate` wrapper. The profile-driven local script resolves the validation profile and captures the certificate currently served by the NGINX endpoint before renewal. It returns correlation and pre-renewal certificate evidence for downstream comparison.

## 3. Renew Certificate on NGINX Endpoint

Uses Keyfactor's native expiration-renewal workflow extension with certificate-store deployment enabled so the renewed certificate is pushed to the associated NGINX certificate store.

## 4. Wait for Endpoint Validation

Uses a workflow approval/gate step as an automation control point. The Command-side `EndpointValidationReleaseHandler` releases this gate only after the custom endpoint-validation job satisfies the configured result requirements.

## 5. Prepare Validation Profile

Normalizes the profile variables required by subsequent REST and email/reporting steps. The profile keeps host, store and application context out of hard-coded workflow logic.

## 6. Prepare Validation REST Data

Retrieves current certificate-store inventory data from Keyfactor Command after endpoint validation completes.

## 7. Prepare Validation Email Data

Combines current Command inventory, `KFValidation_*` metadata, and pre-renewal evidence. It formats the application-owner report and calculates whether the endpoint changed from the previous certificate to the renewed certificate.

## 8. Send Application TLS Renewal Assurance Report

Sends the business-facing assurance report. Public examples use `pki-admin@example.com` and `security-reviewer@example.com`; configure target recipients for the real implementation.

## 9. End-NOOP

Provides a clear terminal workflow step for successful completion and troubleshooting.

## Supporting automation outside the visual workflow

The Command-side job-completion handler observes relevant job completion events, schedules the Universal Orchestrator `EndpointValidation` job at the correct point, verifies the returned result/evidence, and releases the suspended workflow gate.

The NGINX target writes only current local proof output for operational troubleshooting. Command workflow variables and `KFValidation_*` metadata provide the in-flight and persisted application-centric evidence used by the assurance workflow.

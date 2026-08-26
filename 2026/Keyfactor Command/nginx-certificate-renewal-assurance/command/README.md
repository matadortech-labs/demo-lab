# Keyfactor Command Components

This directory contains the public-safe Command portion of the use case.

- `exports/` - sanitized reference exports for workflow, alert, certificate collection, and metadata fields.
- `workflow-scripts/` - the current wrapper/local pre-renewal capture implementation.
- `config/` - a fictional profile configuration example.
- `job-complete-handler/` - sanitized handler manifest plus the C# source project.

The older `CapturePreRenewalEndpointCertificate.ps1` credential model from the source package is intentionally omitted. The wrapper + local profile-driven model is the implementation represented here.

The `EndpointValidationReleaseHandler` C# source project is included. Compiled DLL and PDB artifacts are intentionally excluded from this public source tree.

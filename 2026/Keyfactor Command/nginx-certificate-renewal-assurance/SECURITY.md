# Security and Publication Guidance

This repository is intentionally structured so environment-specific secrets and runtime evidence are not required in source control.

## Never commit

- TLS private keys (`*.key`, PFX/P12, or PEM private-key material)
- SSH private keys
- Command API credentials, encrypted credential stores, or decryption keys
- Authentication tokens or session material
- Runtime evidence caches containing production identifiers
- Full host inventory or package-collector output
- Debug symbols (`*.pdb`)

## Example data

Hostnames, email addresses, GUIDs, certificate identifiers, and other environment-specific values in this public package are fictional examples. Replace them with values from the target environment during implementation.

## Credential handling

The scripts expect credentials to be recreated locally in protected paths. Secret files should be ACL-restricted and should not be copied into this repository.

## Reporting a problem

Do not include real credentials, private keys, certificate key material, or sensitive infrastructure details in a public issue. Reproduce the issue with synthetic/example values whenever possible.

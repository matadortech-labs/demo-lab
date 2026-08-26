# EndpointValidationReleaseHandler

This directory contains the Command-side `IOrchestratorJobCompleteHandler` reference implementation used by the renewal-assurance workflow.

## Contents

- `EndpointValidationReleaseHandler.manifest.example.json` - public-safe handler configuration template.
- `source/EndpointValidationReleaseHandler/EndpointValidationReleaseHandler.cs` - handler source.
- `source/EndpointValidationReleaseHandler/EndpointValidationReleaseHandler.csproj` - build project.

The public source intentionally excludes compiled DLL/PDB artifacts. The project references Keyfactor platform assemblies from their standard Windows installation paths, so build it on a compatible Keyfactor Command system or adjust the `HintPath` values for your build environment.

The two fallback job-type GUIDs in the public C# source are synthetic examples. Configure the actual `RfpemInventoryJobTypeId`, `EndpointValidationJobTypeId`, and `JobTypes` values in the handler manifest for the target Command environment.

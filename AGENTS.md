## Learned User Preferences
- Verify reported findings against the current code before changing them, and only fix issues that are still present.
- When diagnosing CI or test failures, reproduce them locally and rerun enough times to check for flakiness instead of relying only on logs or a single pass.

## Learned Workspace Facts
- Use `make test` for Boltz plugin local test validation; it prepares the debug plugin config and required Docker-backed environment variables that raw `dotnet test` can miss.
- Boltz integration tests depend on the local `regtest` Docker stack being up, including the Boltz backend services, before local results are meaningful.
- This repo uses pinned submodules, so if local behavior differs from CI, compare against the exact submodule commits the main repo points to.

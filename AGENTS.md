# Release policy

The repository owner tests BuffStrap builds and decides when to publish a stable release. Agents must not create or push stable tags (`vX.Y.Z`) and must not publish stable releases.

Pre-release test builds are the owner's testing channel: agents may publish a **pre-release** tagged `vX.Y.Z-alpha.N` for the version in `BuffStrap.Version.props`, so the owner can download it from `https://buff.is-cool.dev/alpha`, test it, and then ask for the stable release. A pre-release is never treated as a release: setup, the site's normal download and the auto-updater ignore pre-releases.

# Version policy

Version steps are small. One step is `0.0.1` (for example v1.0.1 → v1.0.2), and a larger update may step up to `0.0.3` at most (for example v1.0.2 → v1.0.5). Never skip a minor or major version (no v1.0.2 → v1.1.0) without the repository owner explicitly asking for it.

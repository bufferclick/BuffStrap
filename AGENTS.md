# Release policy

The repository owner tests BuffStrap builds and decides when to publish a stable release. Agents must not create or push stable tags (`vX.Y.Z`) and must not publish stable releases.

Pre-release test builds are the owner's testing channel: agents may publish a **pre-release** tagged `vX.Y.Z-alpha.N` for the version in `BuffStrap.Version.props`, so the owner can download it from `https://buff.is-cool.dev/alpha`, test it, and then ask for the stable release. A pre-release is never treated as a release: setup, the site's normal download and the auto-updater ignore pre-releases.

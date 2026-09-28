# publishables

Building in **Release** creates the Thunderstore package here:

- `BannerShare-<version>.zip` - upload this to Thunderstore
- `BannerShare\` - the same contents unzipped, for checking

Contents come from `BannerShare\Package` (manifest.json, icon.png, README.md, CHANGELOG.md)
plus the built DLL in `plugins\`. The version in the zip name is read from manifest.json,
so bump it there (and PluginVersion in BannerShare.cs) before a release.

Everything in this folder except this README is ignored by git.

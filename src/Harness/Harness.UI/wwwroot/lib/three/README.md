# Vendored three.js (r170)

The only vendored third-party JavaScript in Harness.UI. Used by the A2UI `Model3D` viewer iframe
(`model3d-viewer.js` in this directory, loaded by `Components/Meta/A2ui/A2uiModel3D.razor.js`).

- Package: `three@0.170.0` (MIT, see `LICENSE`).
- Artefact: `dyson-three-0.170.0.min.js`, one classic IIFE that sets `window.DysonThree = { THREE, GLTFLoader, FBXLoader, OrbitControls }`.
  It is a classic script, not an ES module, because the viewer iframe is `sandbox="allow-scripts"` (opaque origin) and
  module scripts are fetched in CORS mode, which the static files do not answer.
- Entry: `entry.js` (re-exports; GLTFLoader pulls BufferGeometryUtils, FBXLoader pulls fflate, NURBSCurve, NURBSUtils).
- Not included: Draco, Meshopt and KTX2 decoders.

No npm or package.json is part of the repo build. To rebuild (one-off, any temp directory):

```powershell
npm pack three@0.170.0
mkdir node_modules\three; tar -xzf three-0.170.0.tgz -C node_modules\three --strip-components=1
copy <repo>\src\Harness\Harness.UI\wwwroot\lib\three\entry.js .
npx --yes esbuild@0.24.0 entry.js --bundle --minify --format=iife --global-name=DysonThree --outfile=dyson-three-0.170.0.min.js
```

Built SHA-256 (Windows, Node 24, esbuild 0.24.0): `042E1218A94037AD8B8E1121B2719BF267D05BCCB34D52A98EE18F94926A3A63`.

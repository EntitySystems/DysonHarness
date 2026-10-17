# Third-party notices

DysonHarness itself is licensed under AGPL-3.0 (see [LICENSE](LICENSE)). This file lists third-party
code that is incorporated into, or derived for, DysonHarness under its own license.

## Speech runtime (downloaded on demand, not redistributed)

Settings → Voice downloads these from their public sources (`packaging/speech/manifest.json`) into the
app-data folder. None of them ship in the zip, the MSI, or this repository. A copy of this list is
written to `licenses/NOTICES.md` in the speech folder.

- CPython 3.12.15 (python-build-standalone): PSF License.
- PyTorch 2.14.1+cpu: BSD-3-Clause.
- `fermion-research` 0.2.11: Apache-2.0.
- Phonon-2 weights (Fermion Research, derived from NVIDIA Parakeet TDT 0.6B v3): CC-BY-4.0.
- `pocket-tts` 3.3.0 code (Kyutai): MIT.
- Pocket TTS weights and the `alba` voice embedding (`kyutai/pocket-tts-without-voice-cloning`, Kyutai): CC-BY-4.0.
- libsndfile inside the upstream `soundfile` wheel: LGPL-2.1.
- Other wheels keep their own license files in each wheel's `dist-info`.

## router-for-me/CLIProxyAPI (MIT)

Source: <https://github.com/router-for-me/CLIProxyAPI>, ported at commit
`97f244b8ddb9cbf564b6e6faab0159102cca8617` (branch `main`, 2026-09-30).

Scope: the native xAI / Grok Build provider (`src/Harness/Harness.Engine/Providers/Xai/`) is a port
and derivative of CLIProxyAPI's xAI support, namely the OIDC discovery and device-code OAuth flow,
token refresh, credential JSON shape, the Grok CLI chat-proxy request headers, Responses request
sanitization, `encrypted_content` validation, error classification and the bundled model table
(`internal/auth/xai/*`, `internal/runtime/executor/xai_executor*.go`,
`internal/signature/grok_validation.go`, `internal/registry/models/models.json`). Each ported C# file
carries a header naming the Go source it derives from.

CLIProxyAPI is used as a separate downloaded binary for the legacy `cliproxy-*` providers; that
binary is not redistributed by this repository.

```
MIT License

Copyright (c) 2025-2005.9 Luis Pater
Copyright (c) 2025.9-present Router-For.ME

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

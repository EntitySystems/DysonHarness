# Third-party notices

DysonHarness itself is licensed under AGPL-3.0 (see [LICENSE](LICENSE)). This file lists third-party
code that is incorporated into, or derived for, DysonHarness under its own license.

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

# Third-Party Notices

QuantumHub itself is licensed under the PolyForm Noncommercial License 1.0.0
(see [LICENSE.md](LICENSE.md)). That license does **not** apply to the
components listed below: each one stays under its own license.

## Distributed with QuantumHub

These components are included in a published build.

### Google.Protobuf

Used by every part of the project as the wire format.

- Website: https://github.com/protocolbuffers/protobuf
- License: BSD-3-Clause

```
Copyright 2008 Google Inc.  All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are
met:

    * Redistributions of source code must retain the above copyright
notice, this list of conditions and the following disclaimer.
    * Redistributions in binary form must reproduce the above
copyright notice, this list of conditions and the following disclaimer
in the documentation and/or other materials provided with the
distribution.
    * Neither the name of Google Inc. nor the names of its
contributors may be used to endorse or promote products derived from
this software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
"AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT
OWNER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT
LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,
DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY
THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```

### Microsoft Windows App SDK

Used by the GUI (WinUI 3). Its runtime files are placed next to the GUI by
the build, as the license allows for self-contained deployment.

- Website: https://github.com/microsoft/WindowsAppSDK
- License: Microsoft Software License Terms

A published build carries the full terms and the upstream notices in
`licenses/Microsoft.WindowsAppSDK.license.txt` and
`licenses/Microsoft.WindowsAppSDK.NOTICE.txt`. By using a build that contains
these components you also accept those terms.

### NAudio

Used by the Audio Devices plugin to work with audio devices.

- Website: https://github.com/naudio/NAudio
- License: MIT

```
Copyright 2020 Mark Heath

Permission is hereby granted, free of charge, to any person obtaining a copy of
this software and associated documentation files (the "Software"), to deal in
the Software without restriction, including without limitation the rights to
use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of
the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS
FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR
COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER
IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN
CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
```

### OpenVR and OpenVR.NET

The SteamVR panel ships `openvr_api.dll` by Valve Corporation (BSD 3-Clause, <https://github.com/ValveSoftware/openvr>)
and `OpenVR.NET` by PeriTheGee (MIT, <https://github.com/Flutterish/OpenVR.NET>).

## Used only to build and test

These tools are not part of a published build.

| Component | Purpose | License |
|---|---|---|
| Grpc.Tools | Compiles `.proto` files during the build | Apache-2.0 |
| xunit, xunit.runner.visualstudio | Test framework | Apache-2.0 |
| Microsoft.NET.Test.Sdk | Test runner | MIT |
| DocFX | Generates the documentation site | MIT |

## Runtime

QuantumHub runs on .NET, licensed under MIT. The installer ships a private copy in the `dotnet` folder of the
program, together with its `LICENSE.txt` and `ThirdPartyNotices.txt`, and the setup executable embeds the same runtime.

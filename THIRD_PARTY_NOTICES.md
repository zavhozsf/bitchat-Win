# Third-Party Notices

Bitchat-Win uses the following third-party libraries. They are consumed via
NuGet package references and are not vendored in this repository. Their full
license texts are distributed inside the corresponding NuGet packages.

## Runtime dependencies

| Package | Version | License |
|---|---|---|
| [BouncyCastle.Cryptography](https://www.bouncycastle.org/csharp/) | 2.5.1 | MIT |
| [NAudio](https://github.com/naudio/NAudio) | 2.2.1 | MIT |
| [Hardcodet.NotifyIcon.Wpf](https://github.com/hardcodet/wpf-notifyicon) | 2.0.1 | MIT |

## Test-only dependencies

| Package | Version | License |
|---|---|---|
| [Microsoft.NET.Test.Sdk](https://github.com/microsoft/vstest) | 17.11.1 | MIT |
| [xunit](https://xunit.net/) | 2.9.2 | Apache-2.0 |
| [xunit.runner.visualstudio](https://xunit.net/) | 2.8.2 | Apache-2.0 |

## Protocol compatibility

Bitchat-Win implements the [bitchat](https://github.com/permissionlesstech/bitchat)
wire protocol. The Noise and ChaCha20-Poly1305 implementations were validated
against the official Cacophony test vectors; test vectors embedded in the test
suite originate from the upstream bitchat/Cacophony projects and are used solely
for interoperability verification.

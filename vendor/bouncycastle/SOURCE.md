# Bouncy Castle Cryptography 2.7.0

Official package: https://www.nuget.org/packages/BouncyCastle.Cryptography/2.7.0

Downloaded from https://api.nuget.org/v3-flatcontainer/bouncycastle.cryptography/2.7.0/bouncycastle.cryptography.2.7.0.nupkg

SHA-256: f091ffccab4d03993e660bace277659a79dee0972f54d7f1f4bd46d680966241

Source/API reference: https://github.com/bcgit/bc-csharp/tree/release-2.7.0/crypto/src/crypto/agreement/jpake

J-PAKE: https://www.rfc-editor.org/rfc/rfc8236.html

The public checkout restores the exact version from NuGet.org. A local offline copy may be placed in vendor/packages. src/BorderLess.Core/packages.lock.json records its NuGet content hash. LICENSE.md is distributed beside the application as BouncyCastle-LICENSE.md. Only JPakeParticipant (NIST 3072 / SHA-256) is used, during short-code pairing, with all validation and key-confirmation rounds. Existing TLS and input paths retain the platform cryptography.

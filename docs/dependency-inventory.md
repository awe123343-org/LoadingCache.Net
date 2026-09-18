# Resolved dependency and attribution inventory

Captured 2026-09-16T23:35:07.325577+00:00: 22 existing project restore graphs, 72 distinct NuGet ID/version pairs. Inputs unchanged while captured: **True**.

This is an offline inventory of the actual local resolved graphs, not a fresh restore, a vulnerability scan, or legal approval. Direct means a restore-graph root; SDK auto references and download-only targeting/compiler packs are labelled separately. A dependency's presence in a test/sample/build tool does not make it a shipped library dependency. Existing assets can be historical; final candidate verification must connect them to its restore/build evidence.

Raw graph/nuspec/license copies, all project/TFM usages, dependency edges, archive SHA256/SHA512, source-feed metadata and input hashes: `artifacts/v1-acceptance-20260916/dependencies/final/inventory.json`. Evidence hashes are in the adjacent `evidence-manifest.json`.

## Shipped package graph

- Microsoft.Extensions.DependencyInjection.Abstractions 10.0.12: di-shipped; license `MIT`.

Core has no third-party runtime asset in these resolved graphs; JetBrains.Annotations and ILLink are private compile/build inputs. DI's project reference to core is local source; the external DI dependency above is separate. Package redistribution still requires inspecting final nuspec/archive contents.

## All resolved packages

`build` includes compiler/targeting/download packs and private production build inputs. Full direct/transitive edges and asset paths are retained in raw JSON. SHA256 is of the cached nupkg bytes, not a URL or package ID.

| Package | Version | Usage scopes | License metadata | Nupkg SHA256 |
| --- | --- | --- | --- | --- |
| BenchmarkDotNet | 0.15.8 | benchmark | expression: MIT | `a7c3800476e3ab709b791e50686f1d98d80305282efa305179753ed2ad76bb58` |
| BenchmarkDotNet.Annotations | 0.15.8 | benchmark | expression: MIT | `f667dc3ee1e4eae8b62f945923b5340fc0cbbcbafb2460248b037badb76d4f76` |
| BitFaster.Caching | 2.6.1 | benchmark, tool | file: LICENSE | `d5d4e946406b36f26236c6f140bc69de558aa854f17e6dad32a28bbb67637a20` |
| CommandLineParser | 2.9.1 | benchmark | file: License.md | `02953dcb5c97eb475a4a33e4dca60305e27b5d9072296f21b1ef4d45919c8dea` |
| csharpier | 1.3.0 | build-tool | expression: MIT | `87089e1284137004f2299219ec241258034f06ffa9112860e9c0d75391087b15` |
| FluentAssertions | 7.2.0 | test | expression: Apache-2.0 | `506d45123dac45e2507ffccce2487f5c3b4f08d68b7c676183457110a2c957fb` |
| Gee.External.Capstone | 2.3.0 | benchmark | expression: MIT | `c1d613fc5f122cbef638856ffd0fe17cb31f86558c9e10cd0935b1fb05a53e85` |
| Google.Protobuf | 3.31.1 | sample | expression: BSD-3-Clause | `504727e07f05fb32b40234a6cf5693cb2324b0bc4938635fd8844eb45f83c9d0` |
| Grpc.AspNetCore | 2.83.0 | sample | expression: Apache-2.0 | `b4eea4beaae46ad13f5b6c1236376c1e32ffa50d321a2654a3390708a886ff6b` |
| Grpc.AspNetCore.Server | 2.83.0 | sample | expression: Apache-2.0 | `68d50b09fadf33504e587f919241d66fcb74dc918fbb63e05a704cc1dc87dde5` |
| Grpc.AspNetCore.Server.ClientFactory | 2.83.0 | sample | expression: Apache-2.0 | `f6fda4a625b1fc2ea0badff9150123fdd3c6636062d1af0696989a97d36bbc1b` |
| Grpc.Core.Api | 2.83.0 | sample | expression: Apache-2.0 | `af36bafbf54efacb53abde2ff38efebec4e845eaa4bdc5aca1e9a71122788b3c` |
| Grpc.Net.Client | 2.83.0 | sample | expression: Apache-2.0 | `89c63bda5982217816073a454a7c7fd06e376eed38b4cb032c60653f7be39c9f` |
| Grpc.Net.ClientFactory | 2.83.0 | sample | expression: Apache-2.0 | `68439fefddf53a73b796ec7f918e01038b7d0901b29172eb3865af3e8512b2d2` |
| Grpc.Net.Common | 2.83.0 | sample | expression: Apache-2.0 | `a2d96542f5f019d146ac6b7533f869da22163cb8db6cf2b0e9da8d02443b05e7` |
| Grpc.Tools | 2.83.0 | sample-build-only | expression: Apache-2.0 | `caf6a4beb9ae41865250de0ac43379a1d3f85cbde1fb5aa0fc98738eae47cae5` |
| Iced | 1.21.0 | benchmark | expression: MIT | `d31613617e3ddf91239bbc94a8c587849b4236e8f8a2a2ba59ea2397a1487c7a` |
| JetBrains.Annotations | 2026.2.0 | benchmark-build-only, production-build-only, tool-build-only | expression: MIT | `e34f42b24add688097529946b3db0da657d325f604965955c08430a0359f7669` |
| LoadingCache.LocalValidation | 0.1.0-alpha.localvalidation | test | expression: Apache-2.0 | `31991f1d4e418834a42a88cffadb09b8818a41c4265f6bfdb8345475126a2af4` |
| Microsoft.ApplicationInsights | 2.23.0 | test | expression: MIT | `e6c7f76e0ec26598c7b1e2be1777e839c84486655e8a878fc4c655bd2e918dbd` |
| Microsoft.AspNetCore.App.Ref | 8.0.22 | sdk-download-build-only | expression: MIT | `1acd0dab5290deb807cf07bc1906c24dbc3a8b51467dad3c606c971990e3e96f` |
| Microsoft.CodeAnalysis.Analyzers | 3.11.0 | benchmark | expression: MIT | `850da5e84e8f3b89bb8be66c7c5944c7ef7752c2cfa38218df00e4346d75fd2c` |
| Microsoft.CodeAnalysis.Common | 4.14.0 | benchmark | expression: MIT | `9deff3c47dc6aa8181e0e7a69c4f28244946e666a2fbfebb01268aab098bd811` |
| Microsoft.CodeAnalysis.CSharp | 4.14.0 | benchmark | expression: MIT | `e4cce3dd791860b9300d6875eebd4d11749b5f0c166103e23a6912325828d496` |
| Microsoft.CodeCoverage | 18.0.1 | test | expression: MIT | `1bacb98b21d9dd1dac8652c25bfb93bac8a8fd4a9c9d64fbf57f9401bc6f0d06` |
| Microsoft.Diagnostics.NETCore.Client | 0.2.510501 | benchmark | expression: MIT | `bda612812a323c7d1727e09566af97952c8bd6d2606799045ca330631fb523dc` |
| Microsoft.Diagnostics.Runtime | 3.1.512801 | benchmark | expression: MIT | `9c3134708f685463c4e1a2ac9aaf35a23462cd90bf3a2fb95b837dedb8377380` |
| Microsoft.Diagnostics.Tracing.TraceEvent | 3.1.21 | benchmark | expression: MIT | `664da746a9c1316767e451dec4c386c458aa5fde6c26461d2d57774fc4cc3d3f` |
| Microsoft.DotNet.ILCompiler | 10.0.0 | sample-build-only | expression: MIT | `0df2eb213ee3196e122b35db63c68a164f5e36739306a0edc9e8c8526cf8ff36` |
| Microsoft.DotNet.ILCompiler | 8.0.22 | sample-build-only | expression: MIT | `0b5ef56e772adb24e0ce030fb3c35ac0a3b39476876f6ad91181a94c997ef8c1` |
| Microsoft.DotNet.PlatformAbstractions | 3.1.6 | benchmark | file: LICENSE.TXT | `45f336a978aa7626a63e45ebe080e435cd0865217d89366fe2ea2efc4ef3352d` |
| Microsoft.Extensions.Caching.Abstractions | 10.0.12 | benchmark, tool | expression: MIT | `ee944e3c5912ffc6193624aab6e75d28a449f1d4617cbc92860db2c058c616bc` |
| Microsoft.Extensions.Caching.Memory | 10.0.12 | benchmark, tool | expression: MIT | `79eb1513cf083ed4bfa773621c59203a8225c750378095d71165d6823012d8ca` |
| Microsoft.Extensions.DependencyInjection | 10.0.12 | benchmark, sample, test | expression: MIT | `b979c630191f6c48b548cad866d8224a0ed0e6e7be0aba87f454b57597b63779` |
| Microsoft.Extensions.DependencyInjection.Abstractions | 10.0.12 | benchmark, di-shipped, sample, test, tool | expression: MIT | `4664fab0ac4b56b8935333f59ee9e7801c8ab96de7c57cb3b6eb8192a5a61c55` |
| Microsoft.Extensions.DependencyModel | 8.0.2 | test | expression: MIT | `3f2b8efccc8247d26d62aa40d65fe75c687e58b282ab7e10b805cdf6a3736bd4` |
| Microsoft.Extensions.Logging | 6.0.0 | benchmark | expression: MIT | `f16b1929119f5d6e4cb1790c98d55feac96b930f9c474d3973390e3f6294a939` |
| Microsoft.Extensions.Logging.Abstractions | 10.0.12 | benchmark, tool | expression: MIT | `34eaaa60afb61ed3d86e2e2669eae0c690d0e237e8a7066ba5bdb45a386709e7` |
| Microsoft.Extensions.Options | 10.0.12 | benchmark, tool | expression: MIT | `eec90916f11d84ea41e942b59cbf82f1e32726d87e99be7ee2b2424e31570ff7` |
| Microsoft.Extensions.Primitives | 10.0.12 | benchmark, tool | expression: MIT | `97ee742afe0cb0634558262f925cc112b5882d09db8d169575e559031da63922` |
| Microsoft.Extensions.TimeProvider.Testing | 10.10.0 | test | expression: MIT | `c596a1ff11c452b69e8879d09d0a11680a5500c0f9d559370f3abd18e1ba4cd0` |
| Microsoft.NET.ILLink.Tasks | 10.0.0 | sample-build-only | expression: MIT | `b48b3cb567f4b0924c50803f83ec15df37b0e4c11afba0a244a2dd310beb72b3` |
| Microsoft.NET.ILLink.Tasks | 8.0.22 | production-build-only, sample-build-only | expression: MIT | `a36c2256d874571aace37e247c76b9fa0a887529db2d1fb0d27b02820cc620c9` |
| Microsoft.NET.Test.Sdk | 18.0.1 | test, test-build-only | expression: MIT | `d1cdffae9f5d8b4c3b1395267d187a3ebae6dc07b28bc34e8f96e6da2ea3021d` |
| Microsoft.NETCore.App.Host.osx-arm64 | 8.0.22 | sdk-download-build-only | expression: MIT | `6bd2608cd259985016f5c8bfde52ddaa6de73f9ab4ce3cb8aa962ccdfb91fdd2` |
| Microsoft.NETCore.App.Ref | 8.0.22 | sdk-download-build-only | expression: MIT | `d344884f83e67b6c5e61bf0f51200b0271de38847752527094ad214599338d2e` |
| Microsoft.Testing.Extensions.Telemetry | 2.3.3 | test | expression: MIT | `0e8a35200c270e56eaca0d90ea9f3646e618010ed59099d8e0f9fd30366a08eb` |
| Microsoft.Testing.Extensions.TrxReport.Abstractions | 2.3.3 | test | expression: MIT | `6008074b70742f046f2077abdbce50b92ad808ee45712a405daccbe693470786` |
| Microsoft.Testing.Extensions.VSTestBridge | 2.3.3 | test | expression: MIT | `fd4970504230432359c5d0f48cd715df8649db468b7b6916413bc7dcb3881deb` |
| Microsoft.Testing.Platform | 2.3.3 | test | expression: MIT | `b07d00cab5f49f2d0981592044baef3d720a079e6941601c7790044584b3174a` |
| Microsoft.Testing.Platform.MSBuild | 2.3.3 | test | expression: MIT | `25e537fbc39e6ce0c0da4e8a4a8ae6d8fee98ae41945d4b586124a03075f4890` |
| Microsoft.TestPlatform.ObjectModel | 18.4.0 | test | expression: MIT | `1112f68280da334bd41255b6e4333fe652365a9944e44ea0f668537a06346c2f` |
| Microsoft.TestPlatform.TestHost | 18.0.1 | test | expression: MIT | `39761fe6f838a620ebd74bded1b6766526fe9dbdf23a21dac8957772ee6c315e` |
| Microsoft.Win32.SystemEvents | 6.0.0 | test | expression: MIT | `37d11565b979c35567332c065f26955b34fd961f3889a277683e3c848064d730` |
| Newtonsoft.Json | 13.0.3 | test | expression: MIT | `872fc189e638ab1056555b03aaa38f68bcb54286e221aa646eb1129babf63c77` |
| NUnit | 4.6.1 | test | expression: MIT | `30b7225b4723f4e3b7809e858ea0ff2a9a744b9c8f62c4e9496b514d81b43efa` |
| NUnit3TestAdapter | 6.3.0 | test, test-build-only | expression: MIT | `d5a85ac17eac5d1d6083dcb3862d3709679805e0ad4acaee2028d66099d56cf9` |
| Perfolizer | 0.6.1 | benchmark | expression: MIT | `a8e242eaa1943e46856c54940935814da03f9598204f56b31e1315f5bc1f5f28` |
| Pragmastat | 3.2.4 | benchmark | expression: MIT | `e946de215844919144d015a775a1c701e866811d74dba224ea73e5ac7e58b399` |
| runtime.osx-arm64.Microsoft.DotNet.ILCompiler | 10.0.0 | sdk-download-build-only | expression: MIT | `c23d9554f25c07c0c26dab3fe29fde8ec38aabd4f26a953ad74b8202f662d036` |
| runtime.osx-arm64.Microsoft.DotNet.ILCompiler | 8.0.22 | sdk-download-build-only | expression: MIT | `dae687bd18f08db7788020ab39bb146b2caf121377f3117a9d80633caa0d16f0` |
| System.CodeDom | 9.0.5 | benchmark | expression: MIT | `5c864b069bc80f1c7a87ab5fbc6e70cf6049e8ee1fd48c025e7d660b844b5e56` |
| System.Collections.Immutable | 9.0.0 | benchmark | expression: MIT | `fbaab954c7a87396e6e1616ca15ea705703d755e696bf3b8c96fa039d8bcc9a7` |
| System.Configuration.ConfigurationManager | 6.0.0 | test | expression: MIT | `7cf57aebc09f8bef29356aef1806ab1787dec1d3d5103c25256bc9934cbe0a6b` |
| System.Diagnostics.DiagnosticSource | 10.0.12 | benchmark, tool | expression: MIT | `274a955a69a8730d592e9f7fc25011d6d28ab6beaf3497ad1eb446a28a5d19dc` |
| System.Drawing.Common | 6.0.0 | test | expression: MIT | `ffd11a01b11e3a310b452319688992d4ef059947bc8f85ae154c3554eacfc80a` |
| System.Management | 9.0.5 | benchmark | expression: MIT | `af50527da388d124a5dd27005172180e21239e58ea109e4d731ddf5e3b704da5` |
| System.Reflection.Metadata | 9.0.0 | benchmark | expression: MIT | `6af1166dc0a1ed7829b127ac9d1dff4a0c568bfe82e4ec6347cf497ff49f4634` |
| System.Reflection.TypeExtensions | 4.7.0 | benchmark | expression: MIT | `184b42197c2d3a79187a3495f937e5f83ab21aae634d4695c8bf5e32ea4c1c13` |
| System.Security.Cryptography.ProtectedData | 6.0.0 | test | expression: MIT | `5a2f48f4d6d99694035e04bb2a5d3a44817163ff7aef84bb84a898c3911a6d16` |
| System.Security.Permissions | 6.0.0 | test | expression: MIT | `fcc32fb4558637fbce41f8d774e85eb7582c9cc821ea58790c21e2995b27544b` |
| System.Windows.Extensions | 6.0.0 | test | expression: MIT | `37eaa0d44e850c9f40f4be74c2656ddf13d057c671946d71797805bb13bec9f3` |

## Missing metadata and review boundaries

- {"project": "tools/LoadingCache.ScenarioProbe/LoadingCache.ScenarioProbe.csproj", "reason": "No existing project.assets.json; not restored by this inventory"}
- {"package": "csharpier/1.3.0", "reason": "Raw archive SHA512 differs from its cached .nupkg.sha512 sidecar; distinct from the separately recorded NuGet contentHash. Origin of this metadata discrepancy is not established; no security or tampering conclusion."}

License expressions are reported verbatim, not inferred or approved. File licenses and third-party notices are preserved byte-for-byte; their downstream obligations require review. Raw archive SHA512 is checked against the cached `.nupkg.sha512`; restore-lock hashes are compared separately with NuGet's recorded `.nupkg.metadata` contentHash. These are different recorded values for many signed archives and must not be conflated. This inventory does not independently verify package signatures or recompute NuGet's content-hash algorithm. Missing comparison inputs produce null, not a pass. SDK-installed/shared-framework binaries, SDK Roslyn references, Java benchmark dependencies, Node formatter dependencies and CI actions are outside the NuGet graph and are not silently declared audited here. Existing direct-package/source research remains in THIRD_PARTY_NOTICES.md and docs/upstream-map.md.

## Four source adaptations

The local headers and notices were inspected, not re-downloaded or compared byte-for-byte with every upstream file. All four cite Caffeine stable `836b65c0a83e5d1641ded9c6de578654bc04b2e9`; WindowTinyLfuPolicy additionally records master `d885a95eee51fdfe13f450fd9cba80f58f7e0def`. The root LICENSE contains Apache-2.0; THIRD_PARTY_NOTICES.md records the adaptation scope and modifications. StripedReadBuffer retains Doug Lea / JSR-166 public-domain dedication alongside the Apache notice. This is attribution evidence, not legal clearance.

| Local source | SHA256 | Stable revision / copyright / NOTICE / modification marker |
| --- | --- | --- |
| `src/LoadingCache/Policy/FrequencySketch.cs` | `18957d4cd8a3eb50dc6e48bf42dd739ddd70bbe42bfa7a8211eae5bcaa7f293c` | True, True, True, True |
| `src/LoadingCache/Policy/WindowTinyLfuPolicy.cs` | `939591091ca2cfd3b9bc1e7814a81f9614d9673f064131aa1ea24a043595c639` | True, True, True, True |
| `src/LoadingCache/Expiration/TimerWheel.cs` | `6d10aa0aa5a8acb6f745a8efe75df095466d4dbe2a140cce92356051eb5ba8b5` | True, True, True, True |
| `src/LoadingCache/Maintenance/StripedReadBuffer.cs` | `3df932f4255ae57b29855990282225c9a8f4f4ea4d7a96367ec440252bc412cd` | True, True, True, True |

The pack configuration includes root LICENSE and THIRD_PARTY_NOTICES.md in both package roles. No external package binaries or upstream source files are bundled into the production core package; the four attributed adaptations are compiled as local source. The cached local-validation package's LICENSE/NOTICE bytes are compared with current root files in `localPackageRootNoticeComparison`; a historical cached package is not a final candidate package. Final package/archive equality and actual publication identity remain separate gates.

## Reproduce

```sh
uv run --no-project --offline python tools/dependency-inventory.py --output artifacts/v1-acceptance-20260916/dependencies-new --report docs/dependency-inventory.md
```

Choose a fresh output directory. The script does not modify package caches or run restore/build/test. Preserve prior snapshots when a subsequent restore changes the graph.

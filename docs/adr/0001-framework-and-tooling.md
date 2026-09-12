# ADR-0001: .NET 8 baseline and development tooling

Accepted, 12 September 2026.

Core produces one `net8.0` asset. There is no framework-specific implementation justifying a duplicate net10 asset. Tests and source consumers target `net8.0;net10.0` and must execute on their actual runtimes. The repository pins SDK 10.0.100 and C# 12; that development requirement does not raise the consumer runtime minimum or claim the solution builds with SDK 8.

Tests use NUnit 4.6.1, NUnit3TestAdapter 6.3.0 and Microsoft.NET.Test.Sdk 18.0.1. FluentAssertions 7.2.0 was selected after checking its published Apache-2.0 package licence; it is not represented as the newest major. Removing xUnit must not weaken exact exception, cancellation, single-flight, ownership or shutdown assertions. No compatibility shim is retained.

CSharpier 1.3.0 is pinned in `.config/dotnet-tools.json`; use `dotnet tool restore` and `dotnet csharpier format/check .`. Its .NET 10 tool runtime affects development only. Published dependency metadata, frameworks, hashes and licences are recorded in [dependency evidence](../dependency-evidence.json); direct-package review does not replace transitive/security review.

The support table consulted on 12 September 2026 listed .NET 8 end of support as 10 November 2026 and .NET 10 as 14 November 2028. Retain the requested .NET 8 minimum unless an explicit compatibility policy changes it. Reassess servicing and migration before releases; distinguish Microsoft runtime support from library binary compatibility. [Official support policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core).

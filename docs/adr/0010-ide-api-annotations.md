# ADR-0010: Public API annotations

Accepted, 12 September 2026.

Solution-wide usage analysis cannot see external NuGet consumers or framework-assigned/serialised members. Do not delete public option initialisers, statistics getters, interface members or DI fluent returns merely because the repository has no call site. Dummy usages and broad inspection suppression are not evidence.

Use JetBrains.Annotations 2026.2.0 (MIT) `PublicAPI`/`UsedImplicitly` only for genuine external/framework contracts. Annotate public properties individually where an options type also contains internal test hooks, preserving private/internal dead-code inspection.

Required projects use `PrivateAssets="all" IncludeAssets="compile"`. Do not define `JETBRAINS_ANNOTATIONS`: upstream conditional attributes are omitted from emitted assemblies, so no runtime dependency is intended. Check actual package dependency graphs and assembly references; metadata/hashes are in [annotation evidence](../annotation-dependency-evidence.json). Do not copy annotation source into core.

Apply contravariance only to genuinely input-only loader/expiry parameters; retain parameters representing typed policy identity. Rejected alternatives: whole-file/global suppression, manipulating `IsPackable` to influence IDE guesses, embedded annotation classes and fake consumers.

References: [Rider source annotations](https://www.jetbrains.com/help/rider/Code_Analysis__Annotations_in_Source_Code.html), [attribute semantics](https://www.jetbrains.com/help/rider/Reference__Code_Annotation_Attributes.html), [pinned package](https://www.nuget.org/packages/JetBrains.Annotations/2026.2.0).

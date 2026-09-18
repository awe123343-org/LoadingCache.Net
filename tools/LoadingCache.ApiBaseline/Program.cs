using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

string command = args.Length > 0 ? args[0] : "";
if (command is not ("check" or "write" or "self-test"))
    throw new ArgumentException(
        "Use check, write, or self-test, followed by --reference-directory, --reference, --assembly, --baseline-directory and --evidence options."
    );
var options = new Dictionary<string, List<string>>(StringComparer.Ordinal);
for (int index = 1; index < args.Length; index += 2)
{
    if (index + 1 == args.Length || !args[index].StartsWith("--", StringComparison.Ordinal))
        throw new ArgumentException("Every option requires a value.");
    if (!options.TryGetValue(args[index], out List<string>? values))
        options.Add(args[index], values = []);
    values.Add(args[index + 1]);
}
string[] paths =
[
    .. Values("--reference-directory")
        .SelectMany(directory => Directory.GetFiles(directory, "*.dll"))
        .Concat(Values("--reference"))
        .Concat(Values("--assembly"))
        .Select(Path.GetFullPath)
        .Distinct(StringComparer.Ordinal)
        .Order(StringComparer.Ordinal),
];
if (paths.Length == 0)
    throw new ArgumentException("Provide the target framework reference directory.");
ImmutableArray<MetadataReference> references =
[
    .. paths.Select(path => MetadataReference.CreateFromFile(path)),
];
if (command == "self-test")
{
    Snapshot.SelfTest(references);
    return 0;
}
string baselineDirectory = Single("--baseline-directory");
string evidencePath = Single("--evidence");
string[] targets = [.. Values("--assembly").Select(Path.GetFullPath)];
if (targets.Length == 0)
    throw new ArgumentException("Provide at least one --assembly.");
var compilation = CSharpCompilation.Create(
    "ApiMetadataReader",
    references: references,
    options: Snapshot.Options
);
Dictionary<string, IAssemblySymbol> assemblies = references.ToDictionary(
    reference => reference.Display!,
    reference => (IAssemblySymbol)compilation.GetAssemblyOrModuleSymbol(reference)!,
    StringComparer.Ordinal
);
HashSet<string> assemblyNames = [.. assemblies.Values.Select(assembly => assembly.Identity.Name)];
var results = new List<object>();
bool matched = true;
foreach (string path in targets)
{
    IAssemblySymbol assembly = assemblies[path];
    string[] missing =
    [
        .. assembly
            .Modules.SelectMany(module => module.ReferencedAssemblies)
            .Select(identity => identity.Name)
            .Where(name => !assemblyNames.Contains(name))
            .Distinct(StringComparer.Ordinal),
    ];
    if (missing.Length > 0)
        throw new InvalidOperationException(
            $"Missing metadata references for {path}: {string.Join(", ", missing)}"
        );
    string snapshot = Snapshot.Create(assembly);
    string baseline = Path.Combine(baselineDirectory, assembly.Identity.Name + ".api.txt");
    if (command == "write")
    {
        Directory.CreateDirectory(baselineDirectory);
        File.WriteAllText(baseline, snapshot);
    }
    bool same =
        File.Exists(baseline)
        && File.ReadAllText(baseline).Replace("\r\n", "\n", StringComparison.Ordinal) == snapshot;
    matched &= same;
    if (!same)
    {
        string[] before = File.Exists(baseline) ? File.ReadAllLines(baseline) : [];
        string[] after = snapshot.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        foreach (string removed in before.Except(after, StringComparer.Ordinal))
            Console.Error.WriteLine("- " + removed);
        foreach (string added in after.Except(before, StringComparer.Ordinal))
            Console.Error.WriteLine("+ " + added);
    }
    results.Add(
        new
        {
            assembly = assembly.Identity.Name,
            assemblySha256 = Hash(path),
            baseline,
            matched = same,
            baselineSha256 = File.Exists(baseline) ? Hash(baseline) : null,
        }
    );
}
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(evidencePath))!);
File.WriteAllText(
    evidencePath,
    JsonSerializer.Serialize(
        new
        {
            schemaVersion = 1,
            command,
            matched,
            argv = Environment.GetCommandLineArgs(),
            compilerVersion = typeof(CSharpCompilation).Assembly.GetName().Version?.ToString(),
            compilerSha256 = Hash(typeof(CSharpCompilation).Assembly.Location),
            references = paths.ToDictionary(path => path, Hash, StringComparer.Ordinal),
            results,
        }
    )
);
Console.WriteLine(
    matched ? "API baseline matches." : "API baseline differs; review changes before updating it."
);
return matched ? 0 : 1;

IEnumerable<string> Values(string option) =>
    options.TryGetValue(option, out List<string>? values) ? values : [];
string Single(string option) =>
    Values(option).SingleOrDefault() ?? throw new ArgumentException($"Missing {option}.");
static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

internal static class Snapshot
{
    internal static readonly CSharpCompilationOptions Options = new(
        OutputKind.DynamicallyLinkedLibrary,
        metadataImportOptions: MetadataImportOptions.All,
        nullableContextOptions: NullableContextOptions.Enable
    );
    private static readonly SymbolDisplayFormat Format = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Included,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters
            | SymbolDisplayGenericsOptions.IncludeTypeConstraints
            | SymbolDisplayGenericsOptions.IncludeVariance,
        memberOptions: SymbolDisplayMemberOptions.IncludeAccessibility
            | SymbolDisplayMemberOptions.IncludeModifiers
            | SymbolDisplayMemberOptions.IncludeType
            | SymbolDisplayMemberOptions.IncludeParameters
            | SymbolDisplayMemberOptions.IncludeContainingType
            | SymbolDisplayMemberOptions.IncludeConstantValue
            | SymbolDisplayMemberOptions.IncludeRef,
        delegateStyle: SymbolDisplayDelegateStyle.NameAndSignature,
        extensionMethodStyle: SymbolDisplayExtensionMethodStyle.StaticMethod,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType
            | SymbolDisplayParameterOptions.IncludeName
            | SymbolDisplayParameterOptions.IncludeDefaultValue
            | SymbolDisplayParameterOptions.IncludeParamsRefOut
            | SymbolDisplayParameterOptions.IncludeExtensionThis,
        propertyStyle: SymbolDisplayPropertyStyle.NameOnly,
        kindOptions: SymbolDisplayKindOptions.IncludeTypeKeyword,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers
            | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier
            | SymbolDisplayMiscellaneousOptions.UseSpecialTypes
    );

    internal static string Create(IAssemblySymbol assembly)
    {
        var rows = new SortedSet<string>(StringComparer.Ordinal);
        VisitNamespace(assembly.GlobalNamespace);
        return "# Roslyn metadata API baseline v1: "
            + assembly.Identity.Name
            + "\n"
            + string.Join("\n", rows)
            + "\n";

        void VisitNamespace(INamespaceSymbol space)
        {
            foreach (INamespaceSymbol child in space.GetNamespaceMembers())
                VisitNamespace(child);
            foreach (INamedTypeSymbol type in space.GetTypeMembers())
                VisitType(type);
        }

        void VisitType(INamedTypeSymbol type)
        {
            if (!Visible(type))
                return;
            Add(type);
            string owner = type.ToDisplayString(Format);
            if (type.BaseType is not null)
                rows.Add(owner + " | base: " + type.BaseType.ToDisplayString(Format));
            if (type.EnumUnderlyingType is not null)
                rows.Add(
                    owner + " | enum-underlying: " + type.EnumUnderlyingType.ToDisplayString(Format)
                );
            foreach (INamedTypeSymbol contract in type.Interfaces)
                rows.Add(owner + " | interface: " + contract.ToDisplayString(Format));
            foreach (ISymbol member in type.GetMembers())
            {
                if (member is INamedTypeSymbol nested)
                    VisitType(nested);
                else if (Visible(member))
                    Add(member);
            }
        }

        void Add(ISymbol symbol)
        {
            string signature = symbol.Kind + ": " + symbol.ToDisplayString(Format);
            rows.Add(signature);
            rows.Add(
                signature
                    + $" | accessibility: {symbol.DeclaredAccessibility}; abstract: {symbol.IsAbstract}; sealed: {symbol.IsSealed}; static: {symbol.IsStatic}; virtual: {symbol.IsVirtual}; override: {symbol.IsOverride}"
            );
            switch (symbol)
            {
                case INamedTypeSymbol shape:
                    rows.Add(
                        signature
                            + $" | readonly: {shape.IsReadOnly}; ref-like: {shape.IsRefLikeType}; record: {shape.IsRecord}"
                    );
                    break;
                case IMethodSymbol setter:
                    rows.Add(signature + $" | init-only: {setter.IsInitOnly}");
                    break;
            }
            Attributes(signature, symbol.GetAttributes());
            IEnumerable<IParameterSymbol> parameters = symbol switch
            {
                IMethodSymbol method => method.Parameters,
                IPropertySymbol property => property.Parameters,
                _ => [],
            };
            foreach (IParameterSymbol parameter in parameters)
                Attributes(
                    signature + " | parameter " + parameter.Ordinal,
                    parameter.GetAttributes()
                );
            switch (symbol)
            {
                case IMethodSymbol callable:
                    Attributes(signature + " | return", callable.GetReturnTypeAttributes());
                    foreach (ITypeParameterSymbol parameter in callable.TypeParameters)
                        Attributes(
                            signature + " | type-parameter " + parameter.Ordinal,
                            parameter.GetAttributes()
                        );
                    break;
                case INamedTypeSymbol type:
                    foreach (ITypeParameterSymbol parameter in type.TypeParameters)
                        Attributes(
                            signature + " | type-parameter " + parameter.Ordinal,
                            parameter.GetAttributes()
                        );
                    break;
                case IPropertySymbol property:
                    Accessor(property.GetMethod);
                    Accessor(property.SetMethod);
                    break;
                case IEventSymbol eventSymbol:
                    Accessor(eventSymbol.AddMethod);
                    Accessor(eventSymbol.RemoveMethod);
                    break;
            }
        }

        void Accessor(IMethodSymbol? accessor)
        {
            if (accessor is not null && Visible(accessor))
                Add(accessor);
        }

        void Attributes(string owner, ImmutableArray<AttributeData> attributes)
        {
            foreach (
                AttributeData attribute in attributes.Where(static attribute =>
                    attribute.AttributeClass?.ToDisplayString()
                        is not (
                            "System.Runtime.CompilerServices.NullableAttribute"
                            or "System.Runtime.CompilerServices.NullableContextAttribute"
                            or "System.Runtime.CompilerServices.CompilerGeneratedAttribute"
                            or "System.Runtime.CompilerServices.AsyncStateMachineAttribute"
                            or "System.Runtime.CompilerServices.IteratorStateMachineAttribute"
                            or "System.Runtime.CompilerServices.AsyncIteratorStateMachineAttribute"
                        )
                )
            )
            {
                rows.Add(owner + " | attribute: " + attribute);
            }
        }
    }

    private static bool Visible(ISymbol symbol) =>
        symbol.DeclaredAccessibility
            is Accessibility.Public
                or Accessibility.Protected
                or Accessibility.ProtectedOrInternal;

    internal static void SelfTest(ImmutableArray<MetadataReference> references)
    {
        const string fixture = """
            #nullable enable
            public class Api<T> where T : class
            {
                public string? Read(int count = 7) => null;
                public T? Value { get; protected set; }
                public bool Try([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? value) { value = null; return false; }
                private int Hidden() => 1;
            }
            """;
        string baseline = Read(fixture);
        string[] changes =
        [
            fixture.Replace("string? Read", "string Read", StringComparison.Ordinal),
            fixture.Replace("count = 7", "count = 8", StringComparison.Ordinal),
            fixture.Replace("where T : class", "where T : struct", StringComparison.Ordinal),
            fixture.Replace("count = 7", "length = 7", StringComparison.Ordinal),
            fixture.Replace("protected set", "private set", StringComparison.Ordinal),
            fixture.Replace("NotNullWhen(true)", "NotNullWhen(false)", StringComparison.Ordinal),
            fixture.Replace(
                "public string? Read",
                "protected string? Read",
                StringComparison.Ordinal
            ),
            fixture.Replace(
                "public class Api",
                "public sealed class Api",
                StringComparison.Ordinal
            ),
            fixture.Replace(
                "public class Api",
                "public abstract class Api",
                StringComparison.Ordinal
            ),
            fixture.Replace("where T : class", "where T : notnull", StringComparison.Ordinal),
            fixture.Replace("protected set", "protected init", StringComparison.Ordinal),
        ];
        for (int index = 0; index < changes.Length; index++)
            if (Read(changes[index]) == baseline)
                throw new InvalidOperationException(
                    $"API negative control {index} was not detected."
                );
        if (
            Read(fixture.Replace("Hidden() => 1", "Hidden() => 2", StringComparison.Ordinal))
            != baseline
        )
            throw new InvalidOperationException(
                "Private implementation change altered the baseline."
            );
        Console.WriteLine(
            $"API baseline self-test passed: {changes.Length} contract changes detected; private implementation ignored."
        );

        return;

        string Read(string source)
        {
            var compilation = CSharpCompilation.Create(
                "ApiFixture",
                [CSharpSyntaxTree.ParseText(source)],
                references,
                Options
            );
            using var stream = new MemoryStream();
            var emitted = compilation.Emit(stream);
            if (!emitted.Success)
                throw new InvalidOperationException(string.Join("\n", emitted.Diagnostics));
            MetadataReference metadata = MetadataReference.CreateFromImage(stream.ToArray());
            var reader = CSharpCompilation.Create(
                "Reader",
                references: references.Add(metadata),
                options: Options
            );
            return Create((IAssemblySymbol)reader.GetAssemblyOrModuleSymbol(metadata)!);
        }
    }
}

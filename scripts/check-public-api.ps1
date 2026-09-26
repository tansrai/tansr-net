[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$BaselinePackageDirectory,
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [string]$Configuration = 'Release'
)
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'Use a new public API evidence directory.' }
[IO.Directory]::CreateDirectory($output) | Out-Null
$baseline = [IO.Path]::GetFullPath($BaselinePackageDirectory)
$packages = @('Tansr.Sdk', 'Tansr.Sdk.Windows')
$pairs = @()
foreach ($package in $packages) {
    $archives = @(Get-ChildItem -LiteralPath $baseline -File | Where-Object { $_.Name -match ('^' + [regex]::Escape($package) + '\.[0-9].*\.nupkg$') })
    if ($archives.Count -ne 1) { throw "Require exactly one original candidate for $package." }
    $archive = [IO.Compression.ZipFile]::OpenRead($archives[0].FullName)
    try {
        foreach ($entry in $archive.Entries) {
            if ($entry.FullName -notmatch ('^lib/([^/]+)/' + [regex]::Escape($package) + '\.dll$')) { continue }
            $framework = $Matches[1]
            $old = Join-Path $output ('baseline/' + $framework + '/' + $package + '.dll')
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($old)) | Out-Null
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $old, $false)
            $candidate = Join-Path $repository ('src/' + $package + '/bin/' + $Configuration + '/' + $framework + '/' + $package + '.dll')
            if (-not (Test-Path -LiteralPath $candidate) -and $framework -eq 'net10.0-windows7.0') {
                # NuGet expands the default platform version; the project output uses its declared TFM.
                $candidate = Join-Path $repository ('src/' + $package + '/bin/' + $Configuration + '/net10.0-windows/' + $package + '.dll')
            }
            if (-not (Test-Path -LiteralPath $candidate)) { throw "Build the final candidate before comparing: $candidate" }
            $snapshot = Join-Path $output ('candidate/' + $framework + '/' + $package + '.dll')
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($snapshot)) | Out-Null
            Copy-Item -LiteralPath $candidate -Destination $snapshot
            $pairs += @{ package = $package; framework = $framework; before = $old; after = $snapshot; builtAsset = $candidate; sha256 = (Get-FileHash -LiteralPath $snapshot -Algorithm SHA256).Hash.ToLowerInvariant() }
        }
    } finally { $archive.Dispose() }
}
if ($pairs.Count -ne 4) { throw 'Both original packages must contain all four declared TFM assets.' }
$dependencies = @{}
foreach ($package in $packages) {
    $assets = Get-Content -LiteralPath (Join-Path $repository ('src/' + $package + '/obj/project.assets.json')) -Raw | ConvertFrom-Json -AsHashtable
    foreach ($targetName in @($assets.targets.Keys | Sort-Object)) {
        foreach ($libraryName in $assets.targets[$targetName].Keys) {
            $library = $assets.targets[$targetName][$libraryName]
            if ($library.type -ne 'package' -or -not $library.runtime) { continue }
            foreach ($relative in $library.runtime.Keys) {
                if (-not $relative.EndsWith('.dll', [StringComparison]::OrdinalIgnoreCase)) { continue }
                foreach ($folder in $assets.packageFolders.Keys) {
                    $file = Join-Path $folder ($assets.libraries[$libraryName].path + '/' + $relative)
                    if (Test-Path -LiteralPath $file) {
                        $name = [IO.Path]::GetFileNameWithoutExtension($file)
                        if (-not $dependencies.ContainsKey($name) -or $relative.StartsWith('lib/net10.0/')) { $dependencies[$name] = $file }
                        break
                    }
                }
            }
        }
    }
}
$manifest = @{ baselinePackages = @(Get-ChildItem -LiteralPath $baseline -Filter '*.nupkg' | ForEach-Object { @{ name = $_.Name; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() } }); pairs = $pairs; dependencies = $dependencies }
$manifestPath = Join-Path $output 'input.json'
$manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $manifestPath -Encoding utf8NoBOM
$project = Join-Path $output 'ApiCompatibility.csproj'
@'
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><TreatWarningsAsErrors>true</TreatWarningsAsErrors></PropertyGroup></Project>
'@ | Set-Content -LiteralPath $project -Encoding utf8NoBOM
@'
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;

var manifest = JsonDocument.Parse(File.ReadAllText(args[0]));
var pairs = manifest.RootElement.GetProperty("pairs").EnumerateArray().ToArray();
var dependencies = manifest.RootElement.GetProperty("dependencies").EnumerateObject().ToDictionary(item => item.Name, item => item.Value.GetString()!, StringComparer.OrdinalIgnoreCase);
var results = new List<object>(); var failures = new List<string>();
foreach (var pair in pairs)
{
    string framework = pair.GetProperty("framework").GetString()!;
    string package = pair.GetProperty("package").GetString()!;
    string Core(string field) => pairs.Single(item => item.GetProperty("package").GetString() == "Tansr.Sdk" &&
        item.GetProperty("framework").GetString() == (framework.StartsWith("net10", StringComparison.Ordinal) ? "net10.0" : "netstandard2.0")).GetProperty(field).GetString()!;
    using var before = new Surface(pair.GetProperty("before").GetString()!, Core("before"), dependencies);
    using var after = new Surface(pair.GetProperty("after").GetString()!, Core("after"), dependencies);
    int members = 0; var local = new List<string>();
    foreach (var (name, type) in before.Types)
    {
        if (!after.Types.TryGetValue(name, out var next)) { local.Add("Removed public type: " + name); continue; }
        if (type.IsInterface != next.IsInterface || type.IsValueType != next.IsValueType ||
            !type.IsSealed && next.IsSealed || !type.IsAbstract && next.IsAbstract)
            local.Add("Incompatible public type kind: " + name);
        if (type.IsEnum && Surface.Name(type.GetEnumUnderlyingType()) != Surface.Name(next.GetEnumUnderlyingType())) local.Add("Enum storage changed: " + name);
        if (type.BaseType != null && !Surface.Bases(next).Contains(Surface.Name(type.BaseType))) local.Add("Original base type no longer assignable: " + name);
        foreach (string contract in type.GetInterfaces().Select(Surface.Name))
            if (!next.GetInterfaces().Select(Surface.Name).Contains(contract)) local.Add("Removed implemented interface: " + name + " / " + contract);
        var original = Surface.Members(type); var current = Surface.Members(next); members += original.Count;
        foreach (string member in original) if (!current.Contains(member)) local.Add("Removed/changed public or protected member: " + name + " / " + member);
        if (type.IsInterface || type.IsAbstract)
        {
            var oldRequired = Surface.Required(type); var newRequired = Surface.Required(next);
            foreach (string member in newRequired) if (!oldRequired.Contains(member)) local.Add("New required implementer member: " + name + " / " + member);
        }
    }
    failures.AddRange(local.Select(item => package + "/" + framework + ": " + item));
    results.Add(new { package, framework, publicTypesBefore = before.Types.Count, publicTypesAfter = after.Types.Count, originalMembers = members, failures = local });
}
File.WriteAllText(args[1], JsonSerializer.Serialize(new { results, failures, passed = failures.Count == 0,
    boundary = "Public/protected CLR signatures, optional defaults/names, original base assignability, and added abstract interface obligations. Reflection is not a behavioral or all-source-overload-ambiguity proof; retain targeted old-source and old-binary consumer witnesses." }, new JsonSerializerOptions { WriteIndented = true }));
foreach (string failure in failures) Console.Error.WriteLine(failure);
Console.WriteLine($"API compatibility: {pairs.Length} TFM assets; failures={failures.Count}");
return failures.Count == 0 ? 0 : 1;

sealed class Surface : AssemblyLoadContext, IDisposable
{
    private readonly string core;
    private readonly IReadOnlyDictionary<string, string> dependencies;
    internal IReadOnlyDictionary<string, Type> Types { get; }
    internal Surface(string path, string core, IReadOnlyDictionary<string, string> dependencies) : base(isCollectible: true)
    {
        this.core = core; this.dependencies = dependencies;
        var assembly = LoadFromAssemblyPath(Path.GetFullPath(path));
        Types = assembly.GetExportedTypes().ToDictionary(Name, StringComparer.Ordinal);
    }
    protected override Assembly? Load(AssemblyName name) => name.Name == "Tansr.Sdk" ? LoadFromAssemblyPath(Path.GetFullPath(core))
        : name.Name != null && dependencies.TryGetValue(name.Name, out var path) ? LoadFromAssemblyPath(Path.GetFullPath(path)) : null;
    internal new static string Name(Type type) => type.IsGenericParameter ? (type.DeclaringMethod == null ? "!" : "!!") + type.GenericParameterPosition
        : type.IsByRef ? Name(type.GetElementType()!) + "&" : type.IsPointer ? Name(type.GetElementType()!) + "*"
        : type.IsArray ? Name(type.GetElementType()!) + "[" + new string(',', type.GetArrayRank() - 1) + "]"
        : type.IsGenericType ? type.GetGenericTypeDefinition().FullName + "<" + string.Join(",", type.GetGenericArguments().Select(Name)) + ">" : type.FullName ?? type.Name;
    internal static HashSet<string> Bases(Type type)
    { var result = new HashSet<string>(StringComparer.Ordinal); for (var current = type.BaseType; current != null; current = current.BaseType) result.Add(Name(current)); return result; }
    private static bool Visible(MethodBase method) => method.IsPublic || method.IsFamily || method.IsFamilyOrAssembly;
    private static string Constant(object? value) => value == null ? "null" : value is DBNull ? "DBNull" : value is Missing ? "Missing" : JsonSerializer.Serialize(value, value.GetType());
    private static string Parameters(MethodBase method) => string.Join(",", method.GetParameters().Select(p => Name(p.ParameterType) + " " + p.Name + " " + p.Attributes +
        (p.HasDefaultValue ? "=" + Constant(p.DefaultValue) : "")));
    private static string Constraints(MethodInfo method) => string.Join(";", method.GetGenericArguments().Select(p =>
        p.GenericParameterAttributes + ":" + string.Join(",", p.GetGenericParameterConstraints().Select(Name).Order(StringComparer.Ordinal))));
    private static string Method(MethodInfo method) => (method.IsPublic ? "public " : "protected ") + (method.IsStatic ? "static " : "instance ") +
        // Implicit interface implementations are virtual-final CLR slots, not new overridable members.
        (method.IsVirtual && !method.IsFinal ? "virtual " : "") + method.Name + "(" + Parameters(method) + ")->" + Name(method.ReturnType) + " [" + Constraints(method) + "]";
    internal static HashSet<string> Members(Type type)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        var result = type.GetMethods(flags).Where(Visible).Select(Method).ToHashSet(StringComparer.Ordinal);
        foreach (var constructor in type.GetConstructors(flags).Where(Visible)) result.Add((constructor.IsPublic ? "public " : "protected ") + ".ctor(" + Parameters(constructor) + ")");
        foreach (var field in type.GetFields(flags).Where(f => f.IsPublic || f.IsFamily || f.IsFamilyOrAssembly)) result.Add("field " + field.Name + ":" + Name(field.FieldType) + " " + field.Attributes + (field.IsLiteral ? "=" + Constant(field.GetRawConstantValue()) : ""));
        return result;
    }
    internal static HashSet<string> Required(Type type) => new[] { type }.Concat(type.IsInterface ? type.GetInterfaces() : []).SelectMany(t => t.GetMethods()).Where(m => m.IsAbstract).Select(Method).ToHashSet(StringComparer.Ordinal);
    public void Dispose() => Unload();
}
'@ | Set-Content -LiteralPath (Join-Path $output 'Program.cs') -Encoding utf8NoBOM
& dotnet build $project -c Release --nologo *> (Join-Path $output 'build.log')
if ($LASTEXITCODE -ne 0) { throw 'API comparison helper failed to build; inspect build.log.' }
& dotnet (Join-Path $output 'bin/Release/net10.0/ApiCompatibility.dll') $manifestPath (Join-Path $output 'result.json') *> (Join-Path $output 'comparison.log')
if ($LASTEXITCODE -ne 0) { throw 'Public API compatibility failed; inspect result.json.' }
Get-Content -LiteralPath (Join-Path $output 'comparison.log')

# Compile precisely the same legacy source against both generations, then run the original
# compiled consumer with only the two SDK assemblies replaced. These witnesses address the
# overload ambiguity / inherited getter cases which a signature inventory alone cannot prove.
$consumerSource = @'
using System.Text.Json;
using Tansr.Sdk.Archive;
using Tansr.Sdk.Storage;
using Tansr.Sdk.Windows.Skills;
using Tansr.Sdk.Windows.Storage;

try { _ = new WindowsSkillCatalog(null!, null!); throw new Exception("Expected original null workspace rejection."); }
catch (ArgumentNullException error) when (error.ParamName == "workspace") { }
var publication = new SqliteMemoryPublicationException("store_offline");
if (publication.Code != "store_offline" || publication.Message != "Tansr memory publication: store_offline")
    throw new Exception("Existing arbitrary publication code/message changed.");
IArchiveClient client = new ExistingArchiveClient();
if (client.ReadScope().ValueKind != JsonValueKind.Undefined) throw new Exception("Existing archive client dispatch failed.");
IArchiveStore store = new ExistingArchiveStore();
await store.CloseAsync();
if (!((ExistingArchiveStore)store).Closed) throw new Exception("Existing archive store dispatch failed.");
Console.WriteLine("PASS: legacy two-null constructor, arbitrary exception code/message, original IArchiveClient and IArchiveStore implementers.");

sealed class ExistingArchiveClient : IArchiveClient
{
    public JsonElement ReadScope() => default;
    public JsonElement GetEffectiveLimits(string bindingId) => throw new NotSupportedException();
    public Task<JsonElement> GetCapabilitiesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<JsonElement> GetBindingTargetAsync(JsonElement request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<JsonElement> CreateBindingAsync(JsonElement request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<JsonElement> GetBindingAsync(string bindingId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<JsonElement> GetArchiveStatusAsync(string bindingId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<JsonElement> ReadRecordsAsync(JsonElement request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<JsonElement> ReadArtifactAsync(JsonElement request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<JsonElement> AcknowledgeAsync(JsonElement request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<JsonElement> GetOperationAsync(JsonElement request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<JsonElement> UploadMaterialChunkAsync(JsonElement request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<JsonElement> GetMaterialUploadStatusAsync(JsonElement request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<JsonElement> RespondMaterialsAsync(JsonElement request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<JsonElement> GetMaterialStatusAsync(JsonElement request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<JsonElement> CloseBindingAsync(JsonElement request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}
sealed class ExistingArchiveStore : IArchiveStore
{
    public bool Closed { get; private set; }
    public Task<JsonElement> ReceiveAsync(ArchiveReceiveInput input, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<JsonElement?> PendingAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<JsonElement?> HeadAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<byte[]> BodyAsync(JsonElement artifactReference, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task ConfirmAsync(JsonElement receipt, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<ArchiveRecordPage> ReadRecordsAsync(ArchiveReadRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<JsonElement> CoverageAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task CloseAsync() { Closed = true; return Task.CompletedTask; }
}
'@
$consumerRuns = @()
foreach ($generation in @('before', 'after')) {
    $directory = Join-Path $output ('consumer-' + $generation)
    [IO.Directory]::CreateDirectory($directory) | Out-Null
    $references = @()
    foreach ($package in $packages) {
        $pair = @($pairs | Where-Object { $_.package -eq $package -and $_.framework.StartsWith('net10') })[0]
        $escapedPath = [Security.SecurityElement]::Escape($pair[$generation])
        $references += "<Reference Include=`"$package`"><HintPath>$escapedPath</HintPath><Private>true</Private></Reference>"
    }
    foreach ($dependency in @('System.Text.Json', 'System.Text.Encodings.Web', 'System.IO.Pipelines')) {
        $escapedPath = [Security.SecurityElement]::Escape($dependencies[$dependency])
        $references += "<Reference Include=`"$dependency`"><HintPath>$escapedPath</HintPath><Private>true</Private></Reference>"
    }
    $consumerProject = Join-Path $directory 'LegacyConsumer.csproj'
    @"
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0-windows</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><TreatWarningsAsErrors>true</TreatWarningsAsErrors></PropertyGroup><ItemGroup>$($references -join '')</ItemGroup></Project>
"@ | Set-Content -LiteralPath $consumerProject -Encoding utf8NoBOM
    $consumerSource | Set-Content -LiteralPath (Join-Path $directory 'Program.cs') -Encoding utf8NoBOM
    & dotnet build $consumerProject -c Release --nologo *> (Join-Path $directory 'build.log')
    if ($LASTEXITCODE -ne 0) { throw "Legacy source compatibility failed against $generation; inspect consumer-$generation/build.log." }
    $consumerDll = Join-Path $directory 'bin/Release/net10.0-windows/LegacyConsumer.dll'
    & dotnet $consumerDll *> (Join-Path $directory 'run.log')
    if ($LASTEXITCODE -ne 0) { throw "Legacy source witness failed against $generation; inspect consumer-$generation/run.log." }
    $consumerRuns += @{ generation = $generation; passed = $true; consumerSha256 = (Get-FileHash -LiteralPath $consumerDll -Algorithm SHA256).Hash.ToLowerInvariant() }
}
$binaryDirectory = Join-Path $output 'old-binary-new-sdk'
Copy-Item -LiteralPath (Join-Path $output 'consumer-before/bin/Release/net10.0-windows') -Destination $binaryDirectory -Recurse
foreach ($pair in $pairs | Where-Object { $_.framework.StartsWith('net10') }) {
    Copy-Item -LiteralPath $pair.after -Destination (Join-Path $binaryDirectory ($pair.package + '.dll')) -Force
}
& dotnet (Join-Path $binaryDirectory 'LegacyConsumer.dll') *> (Join-Path $output 'old-binary-new-sdk.log')
if ($LASTEXITCODE -ne 0) { throw 'Unrecompiled old binary failed with the new SDK; inspect old-binary-new-sdk.log.' }
$consumerRuns += @{ generation = 'old-binary-new-sdk'; passed = $true; consumerSha256 = (Get-FileHash -LiteralPath (Join-Path $binaryDirectory 'LegacyConsumer.dll') -Algorithm SHA256).Hash.ToLowerInvariant() }
$consumerRuns | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $output 'consumer-result.json') -Encoding utf8NoBOM
Write-Output 'Legacy consumers: old source/old SDK, old source/new SDK, old binary/new SDK all passed.'

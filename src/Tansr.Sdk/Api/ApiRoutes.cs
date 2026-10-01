using System;

namespace Tansr.Sdk.Api;

/// <summary>Single module for every <c>/api</c> path this SDK emits (UAPI-01 D10: no <c>/v2</c>, <c>/v3/sdk2</c> or
/// <c>/v3/terminal*</c> fallback). The operation table lives in <c>ApiRoutes.generated.cs</c>, produced by
/// <c>scripts/generate-api-routes.mjs</c> from the vendored <c>contract/api-manifest.json</c>.</summary>
public static partial class ApiRoutes
{
    /// <summary>Path prefix shared by every operation.</summary>
    public const string Prefix = "/api";

    /// <summary>Returns the operation with the given manifest name, or null.</summary>
    public static ApiOperation? Find(string name)
    {
        if (name == null) return null;
        foreach (var operation in All) if (string.Equals(operation.Name, name, StringComparison.Ordinal)) return operation;
        return null;
    }

    /// <summary>True when the path (with or without query) is under the <c>/api</c> prefix.</summary>
    public static bool IsApiPath(string path) =>
        path != null && (path == Prefix || path.StartsWith(Prefix + "/", StringComparison.Ordinal) || path.StartsWith(Prefix + "?", StringComparison.Ordinal));

    /// <summary>Finds the manifest operation whose template or alias matches the path, or null.</summary>
    public static ApiOperation? Match(string method, string path)
    {
        if (method == null || path == null) return null;
        foreach (var operation in All)
            if (string.Equals(operation.Method, method, StringComparison.OrdinalIgnoreCase) && operation.Matches(path)) return operation;
        return null;
    }
}

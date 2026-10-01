using System;
using System.Collections.Generic;
using System.Text;

namespace Tansr.Sdk.Api;

/// <summary>One row of the unified <c>/api</c> operation table (api-manifest.json). Instances are
/// created only by the generated <see cref="ApiRoutes"/> module; callers instantiate paths through
/// <see cref="Path"/> and <see cref="Query"/> instead of concatenating literals.</summary>
public sealed class ApiOperation
{
    private readonly string[] placeholders;

    internal ApiOperation(string name, string domain, string? family, string method, string template,
        string[] aliases, string[] query, bool sse, string kind)
    {
        Name = name; Domain = domain; Family = family; Method = method; Template = template;
        Aliases = aliases; QueryParameters = query; Sse = sse; Kind = kind;
        var found = new List<string>();
        foreach (var segment in template.Split('/'))
            if (segment.Length > 1 && segment[0] == ':') found.Add(segment.Substring(1));
        placeholders = found.ToArray();
    }

    /// <summary>Stable operation name, e.g. <c>session.create</c>.</summary>
    public string Name { get; }
    /// <summary>Domain vocabulary value published in <c>tansr-domain</c>.</summary>
    public string Domain { get; }
    /// <summary>Owning family schema (null for discovery aggregates).</summary>
    public string? Family { get; }
    /// <summary>HTTP method.</summary>
    public string Method { get; }
    /// <summary>Canonical <c>/api</c> template with <c>:name</c> placeholders.</summary>
    public string Template { get; }
    /// <summary>Alias templates also accepted by the facade; never emitted by this SDK.</summary>
    public IReadOnlyList<string> Aliases { get; }
    /// <summary>Query parameter whitelist; <see cref="Query"/> rejects anything else.</summary>
    public IReadOnlyList<string> QueryParameters { get; }
    /// <summary>True when the response is a <c>text/event-stream</c>.</summary>
    public bool Sse { get; }
    /// <summary>read, write, delete or stream (stream ⇔ <see cref="Sse"/>).</summary>
    public string Kind { get; }
    /// <summary>Placeholder names in template order.</summary>
    public IReadOnlyList<string> Placeholders => placeholders;
    /// <summary>Literal template prefix up to (and including the slash before) the first placeholder; the whole
    /// template when it has none. Used by guards that check a caller-built path belongs to this operation group.</summary>
    public string Root
    {
        get
        {
            var colon = Template.IndexOf("/:", StringComparison.Ordinal);
            return colon < 0 ? Template : Template.Substring(0, colon + 1);
        }
    }

    /// <summary>Instantiates the template. Every placeholder present in the template must be supplied; a
    /// value supplied for a placeholder the template does not declare is an error. Values are percent-encoded
    /// as single path segments and may not be empty, <c>.</c>, <c>..</c>, or contain control characters.</summary>
    public string Path(string? id = null, string? targetId = null, string? uploadId = null, string? ticketId = null)
    {
        var supplied = new (string Name, string? Value)[] { ("id", id), ("targetId", targetId), ("uploadId", uploadId), ("ticketId", ticketId) };
        foreach (var (name, value) in supplied)
            if (value != null && Array.IndexOf(placeholders, name) < 0)
                throw new ArgumentException("Operation " + Name + " has no :" + name + " placeholder.", name);
        if (placeholders.Length == 0) return Template;
        var builder = new StringBuilder(Template.Length + 64);
        var segments = Template.Split('/');
        for (int i = 0; i < segments.Length; i++)
        {
            if (i > 0) builder.Append('/');
            var segment = segments[i];
            if (segment.Length > 1 && segment[0] == ':')
            {
                var name = segment.Substring(1);
                string? value = null;
                foreach (var pair in supplied) if (pair.Name == name) value = pair.Value;
                builder.Append(Segment(name, value));
            }
            else builder.Append(segment);
        }
        return builder.ToString();
    }

    /// <summary>Builds <c>?a=b&amp;c=d</c> from whitelisted parameters (empty string when none). Names must be in
    /// <see cref="QueryParameters"/> and unique; values are percent-encoded.</summary>
    public string Query(params (string Name, string Value)[] parameters)
    {
        if (parameters == null || parameters.Length == 0) return "";
        var builder = new StringBuilder();
        for (int i = 0; i < parameters.Length; i++)
        {
            var (name, value) = parameters[i];
            if (string.IsNullOrEmpty(name) || !Contains(QueryParameters, name))
                throw new ArgumentException("Operation " + Name + " does not accept query parameter '" + name + "'.", nameof(parameters));
            for (int j = 0; j < i; j++) if (parameters[j].Name == name) throw new ArgumentException("Duplicate query parameter '" + name + "'.", nameof(parameters));
            if (value == null) throw new ArgumentNullException(nameof(parameters), "Query parameter '" + name + "' has no value.");
            builder.Append(i == 0 ? '?' : '&').Append(Uri.EscapeDataString(name)).Append('=').Append(Uri.EscapeDataString(value));
        }
        return builder.ToString();
    }

    /// <summary>True when <paramref name="path"/> (without query) matches the template or one of its aliases.</summary>
    public bool Matches(string path)
    {
        if (path == null) return false;
        var question = path.IndexOf('?');
        if (question >= 0) path = path.Substring(0, question);
        if (MatchesTemplate(Template, path)) return true;
        foreach (var alias in Aliases) if (MatchesTemplate(alias, path)) return true;
        return false;
    }

    private static bool MatchesTemplate(string template, string path)
    {
        var expected = template.Split('/'); var actual = path.Split('/');
        if (expected.Length != actual.Length) return false;
        for (int i = 0; i < expected.Length; i++)
        {
            if (expected[i].Length > 1 && expected[i][0] == ':') { if (actual[i].Length == 0) return false; continue; }
            if (!string.Equals(expected[i], actual[i], StringComparison.Ordinal)) return false;
        }
        return true;
    }

    private static bool Contains(IReadOnlyList<string> values, string name)
    {
        for (int i = 0; i < values.Count; i++) if (string.Equals(values[i], name, StringComparison.Ordinal)) return true;
        return false;
    }

    private string Segment(string name, string? value)
    {
        if (value == null) throw new ArgumentException("Operation " + Name + " requires :" + name + ".", name);
        if (value.Length == 0 || value.Length > 512 || value == "." || value == "..")
            throw new ArgumentException("Invalid value for :" + name + ".", name);
        foreach (var c in value) if (c < 0x20 || c == 0x7f) throw new ArgumentException("Invalid value for :" + name + ".", name);
        return Uri.EscapeDataString(value);
    }

    public override string ToString() => Method + " " + Template;
}

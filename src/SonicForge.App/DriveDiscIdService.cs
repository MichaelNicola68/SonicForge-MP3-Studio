using System.Reflection;
using System.Text.RegularExpressions;

namespace SonicForge.App;

public sealed class DriveDiscIdService
{
    private static readonly Regex DiscIdPattern = new("^[A-Za-z0-9._-]{28}$", RegexOptions.Compiled);

    public async Task<string> ReadAsync(string driveRoot, CancellationToken token = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        try
        {
            return await Task.Run(() => ReadTocOnly(driveRoot), timeout.Token).WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException("Das reine TOC-Lesen wurde nach 12 Sekunden beendet. Das Laufwerk blieb zu lange beschäftigt.");
        }
    }

    private static string ReadTocOnly(string driveRoot)
    {
        var assembly = Assembly.Load("MetaBrainz.MusicBrainz.DiscId");
        var types = GetTypes(assembly);
        var featureType = types.FirstOrDefault(t => t.IsEnum && t.Name.Contains("Feature", StringComparison.OrdinalIgnoreCase));
        object? tocOnly = featureType is null ? null : Enum.Parse(featureType, "TableOfContents", true);

        var methods = types.SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
            .Where(m => m.Name.Contains("Read", StringComparison.OrdinalIgnoreCase) || m.Name.Contains("Open", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(m => featureType is not null && m.GetParameters().Any(p => p.ParameterType == featureType))
            .ThenBy(m => m.GetParameters().Length);

        Exception? last = null;
        foreach (var method in methods)
        {
            if (!TryArguments(method.GetParameters(), driveRoot, featureType, tocOnly, out var args)) continue;
            try
            {
                var result = method.Invoke(null, args);
                var id = FindDiscId(result, new HashSet<object>(ReferenceEqualityComparer.Instance));
                if (id is not null) return id;
            }
            catch (TargetInvocationException ex) { last = ex.InnerException ?? ex; }
            catch (Exception ex) { last = ex; }
        }
        throw new InvalidOperationException("Die DiscId-Bibliothek lieferte keine MusicBrainz-Disc-ID im TOC-Modus.", last);
    }

    private static bool TryArguments(ParameterInfo[] parameters, string driveRoot, Type? featureType, object? tocOnly, out object?[] args)
    {
        args = new object?[parameters.Length];
        var driveUsed = false;
        for (var i = 0; i < parameters.Length; i++)
        {
            var p = parameters[i];
            if (p.ParameterType == typeof(string) && !driveUsed) { args[i] = driveRoot.Trim(); driveUsed = true; continue; }
            if (featureType is not null && p.ParameterType == featureType) { args[i] = tocOnly; continue; }
            if (p.HasDefaultValue) { args[i] = p.DefaultValue; continue; }
            if (p.ParameterType == typeof(CancellationToken)) { args[i] = CancellationToken.None; continue; }
            return false;
        }
        return driveUsed;
    }

    private static string? FindDiscId(object? value, HashSet<object> visited, int depth = 0)
    {
        if (value is null || depth > 3) return null;
        if (value is string text) return DiscIdPattern.IsMatch(text.Trim()) ? text.Trim() : null;
        if (!value.GetType().IsValueType && !visited.Add(value)) return null;
        foreach (var property in value.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
                     .OrderByDescending(p => p.Name.Contains("DiscId", StringComparison.OrdinalIgnoreCase) || p.Name.Equals("Id", StringComparison.OrdinalIgnoreCase)))
        {
            if (property.GetIndexParameters().Length != 0) continue;
            try
            {
                var id = FindDiscId(property.GetValue(value), visited, depth + 1);
                if (id is not null) return id;
            }
            catch { }
        }
        return null;
    }

    private static Type[] GetTypes(Assembly assembly)
    {
        try { return assembly.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t is not null).Cast<Type>().ToArray(); }
    }
}

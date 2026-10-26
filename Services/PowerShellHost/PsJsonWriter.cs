using System.Buffers;
using System.Collections;
using System.Collections.ObjectModel;
using System.Linq.Expressions;
using System.Management.Automation;
using System.Reflection;
using System.Text.Json;

namespace Craft.Services;

/// <summary>
/// Writes a handler's response body as JSON straight from the PowerShell objects. The text is the same as
/// <see cref="PowerShellRunnerService.ConvertPsObjectToJson"/>, which copies the graph into dictionaries and lists
/// first and reads every object through <see cref="PSObject.Properties"/>.
/// </summary>
internal static class PsJsonWriter
{
    /// <summary>The UTF-8 JSON, or null when the body has a shape this writer does not reproduce exactly (the caller
    /// then serializes it the old way) or serializing failed (the old way fails the same).</summary>
    internal static byte[]? TrySerialize(object? obj, JsonSerializerOptions options)
    {
        using var buffer = new ChunkWriter();
        try
        {
            using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = options.Encoder, MaxDepth = options.MaxDepth == 0 ? 64 : options.MaxDepth }))
                WriteTop(writer, obj, options);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
        return buffer.ToArray();
    }

    /// <summary>
    /// Pooled 1 MB chunks. A doubling buffer holds up to twice the output, and both old and new arrays while it
    /// grows, on top of the final array.
    /// </summary>
    private sealed class ChunkWriter : IBufferWriter<byte>, IDisposable
    {
        private const int ChunkSize = 1 << 20;
        private readonly List<(byte[] Array, int Used)> _full = [];
        private byte[] _current = ArrayPool<byte>.Shared.Rent(ChunkSize);
        private int _used;

        public void Advance(int count) => _used += count;

        public Memory<byte> GetMemory(int sizeHint = 0) { Ensure(sizeHint); return _current.AsMemory(_used); }

        public Span<byte> GetSpan(int sizeHint = 0) { Ensure(sizeHint); return _current.AsSpan(_used); }

        private void Ensure(int sizeHint)
        {
            if (_current.Length - _used >= Math.Max(sizeHint, 1)) return;
            _full.Add((_current, _used));
            _current = ArrayPool<byte>.Shared.Rent(Math.Max(sizeHint, ChunkSize));
            _used = 0;
        }

        public byte[] ToArray()
        {
            var length = _used;
            foreach (var (_, used) in _full) length += used;
            var bytes = GC.AllocateUninitializedArray<byte>(length);
            var at = 0;
            foreach (var (array, used) in _full) { array.AsSpan(0, used).CopyTo(bytes.AsSpan(at)); at += used; }
            _current.AsSpan(0, _used).CopyTo(bytes.AsSpan(at));
            return bytes;
        }

        public void Dispose()
        {
            foreach (var (array, _) in _full) ArrayPool<byte>.Shared.Return(array);
            _full.Clear();
            ArrayPool<byte>.Shared.Return(_current);
            _current = [];
        }
    }

    private static void WriteTop(Utf8JsonWriter w, object? obj, JsonSerializerOptions o)
    {
        if (obj == null) { w.WriteNullValue(); return; }
        if (obj is PSObject pso)
        {
            if (pso.BaseObject == null || pso.BaseObject is PSCustomObject) { WriteProperties(w, pso, o, nullIfNone: true); return; }
            if (pso.BaseObject is IDictionary or IList or string or int or long or double or float or bool or decimal) obj = pso.BaseObject;
            else { WriteProperties(w, pso, o, nullIfNone: false); return; }
        }
        if (obj is IDictionary ht) { WriteDictionary(w, ht, o); return; }
        if (obj is IEnumerable e and not string) { WriteArray(w, e, o); return; }
        JsonSerializer.Serialize(w, obj, obj.GetType(), o);
    }

    // A nested value: UnwrapPsValue's cases, written instead of copied.
    private static void WriteValue(Utf8JsonWriter w, object? value, JsonSerializerOptions o)
    {
        switch (value)
        {
            case null: w.WriteNullValue(); return;
            case PSObject pso:
                var b = pso.BaseObject;
                if (b == null || b is PSCustomObject) { WriteProperties(w, pso, o, nullIfNone: true); return; }
                if (b is string or int or long or double or float or bool or decimal) { WriteLeaf(w, b, o); return; }
                if (b is IDictionary d) { WriteDictionary(w, d, o); return; }
                if (b is IEnumerable e and not string) { WriteArray(w, e, o); return; }
                WriteProperties(w, pso, o, nullIfNone: false);
                return;
            case IDictionary dict: WriteDictionary(w, dict, o); return;
            case IEnumerable en and not string: WriteArray(w, en, o); return;
            default: WriteLeaf(w, value, o); return;
        }
    }

    private static void WriteLeaf(Utf8JsonWriter w, object value, JsonSerializerOptions o)
    {
        switch (value)
        {
            // System.Text.Json's own converters for these types make the same writer calls.
            case string s: w.WriteStringValue(s); return;
            case int i: w.WriteNumberValue(i); return;
            case long l: w.WriteNumberValue(l); return;
            case bool f: w.WriteBooleanValue(f); return;
            case double d when double.IsFinite(d): w.WriteNumberValue(d); return;
            case DateTime dt: w.WriteStringValue(dt); return;
            case DateTimeOffset dto: w.WriteStringValue(dto); return;
            case Guid g: w.WriteStringValue(g); return;
            default: JsonSerializer.Serialize(w, value, value.GetType(), o); return;
        }
    }

    // One pass over the properties. With nullIfNone (the old Properties.Any() check) an object without properties is
    // null; the object opens on the first property seen, so one whose getters all throw is still {} as before. A
    // property whose getter throws is skipped, as the old copy did.
    private static void WriteProperties(Utf8JsonWriter w, PSObject pso, JsonSerializerOptions o, bool nullIfNone)
    {
        var open = false;
        if (!nullIfNone) { w.WriteStartObject(); open = true; }
        foreach (var prop in PropertiesOf(pso))
        {
            if (!open) { w.WriteStartObject(); open = true; }
            object? value;
            try { value = prop.Value; }
            catch { continue; }
            w.WritePropertyName(prop.Name);
            WriteValue(w, value, o);
        }
        if (open) w.WriteEndObject(); else w.WriteNullValue();
    }

    // The old copy keyed a Dictionary by Key.ToString(), where non-string keys can collide: those go the old way.
    private static void WriteDictionary(Utf8JsonWriter w, IDictionary dict, JsonSerializerOptions o)
    {
        w.WriteStartObject();
        foreach (DictionaryEntry entry in dict)
        {
            if (entry.Key is not string key) throw new NotSupportedException("non-string dictionary key");
            w.WritePropertyName(key);
            WriteValue(w, entry.Value, o);
        }
        w.WriteEndObject();
    }

    private static void WriteArray(Utf8JsonWriter w, IEnumerable items, JsonSerializerOptions o)
    {
        w.WriteStartArray();
        foreach (var item in items) WriteValue(w, item, o);
        w.WriteEndArray();
    }

    // Enumerating PSObject.Properties builds the reserved member sets and property adapters for every object, which
    // was most of the old cost. A plain PSCustomObject (ConvertFrom-Json, [pscustomobject]@{}) has nothing beyond its
    // instance members, so those are read directly; anything else, or a PowerShell without these internals, keeps
    // the full Properties view.
    private static readonly Func<object, IEnumerable<PSMemberInfo>?>? s_instanceMembers = BuildInstanceMembers();
    private static readonly Func<PSMemberInfo, bool>? s_isHidden = BuildIsHidden();
    // The public TypeNames getter copies the shared read-only list onto the object; InternalTypeNames reads it as is.
    private static readonly Func<PSObject, Collection<string>?>? s_typeNames = BuildTypeNames();

    private static IEnumerable<PSPropertyInfo> PropertiesOf(PSObject pso)
    {
        if (s_instanceMembers == null || s_isHidden == null || s_typeNames == null || pso.BaseObject is not PSCustomObject
            || s_typeNames(pso) is not { Count: 2 } names || names[0] != "System.Management.Automation.PSCustomObject")
            return pso.Properties;
        return InstanceProperties(s_instanceMembers(pso));
    }

    private static IEnumerable<PSPropertyInfo> InstanceProperties(IEnumerable<PSMemberInfo>? members)
    {
        if (members == null) yield break;
        foreach (var member in members)
            // Enumerating Properties once adds the reserved pstypenames to the instance members, marked hidden.
            if (member is PSPropertyInfo property && !s_isHidden!(member)) yield return property;
    }

    private static Func<object, IEnumerable<PSMemberInfo>?>? BuildInstanceMembers()
    {
        try
        {
            var method = typeof(PSObject).GetMethod("HasInstanceMembers", BindingFlags.NonPublic | BindingFlags.Static);
            var parameters = method?.GetParameters();
            if (method == null || parameters!.Length != 2 || !parameters[1].ParameterType.IsByRef) return null;
            var collectionType = parameters[1].ParameterType.GetElementType()!;
            if (!typeof(IEnumerable<PSMemberInfo>).IsAssignableFrom(collectionType)) return null;
            var obj = Expression.Parameter(typeof(object));
            var members = Expression.Variable(collectionType);
            var body = Expression.Block([members],
                Expression.Condition(Expression.Call(method, obj, members),
                    Expression.Convert(members, typeof(IEnumerable<PSMemberInfo>)),
                    Expression.Constant(null, typeof(IEnumerable<PSMemberInfo>))));
            return Expression.Lambda<Func<object, IEnumerable<PSMemberInfo>?>>(body, obj).Compile();
        }
        catch { return null; }
    }

    private static Func<PSMemberInfo, bool>? BuildIsHidden()
    {
        try
        {
            var property = typeof(PSMemberInfo).GetProperty("IsHidden", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
            if (property == null || property.PropertyType != typeof(bool)) return null;
            var member = Expression.Parameter(typeof(PSMemberInfo));
            return Expression.Lambda<Func<PSMemberInfo, bool>>(Expression.Property(member, property), member).Compile();
        }
        catch { return null; }
    }

    private static Func<PSObject, Collection<string>?>? BuildTypeNames()
    {
        try
        {
            var property = typeof(PSObject).GetProperty("InternalTypeNames", BindingFlags.NonPublic | BindingFlags.Instance);
            if (property == null || !typeof(Collection<string>).IsAssignableFrom(property.PropertyType)) return null;
            var pso = Expression.Parameter(typeof(PSObject));
            return Expression.Lambda<Func<PSObject, Collection<string>?>>(Expression.Convert(Expression.Property(pso, property), typeof(Collection<string>)), pso).Compile();
        }
        catch { return null; }
    }
}

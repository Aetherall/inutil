// Inutil.Wire — serialize a game's il2cpp PROXY directly to its wire JSON through System.Text.Json, using the
// [JsonPropertyName] that InteropPatch's wire-attribute pass re-attached from the recovered wiremap. No twin, no
// per-game Newtonsoft: the game's OWN proxy IS the serialization surface (the improved-proxy direction). OPT-IN
// by construction — only members carrying a recovered wire name are written, so Il2CppInterop's own bookkeeping
// (Pointer, WasCollected, ObjectClass) never leaks.
//
// net6: both loaders host a .NET 6 CoreCLR, whose System.Text.Json (6.0) lacks the net7+ contract-customization
// resolver — so opt-in is a JsonConverterFactory scoped to Il2CppObjectBase, NOT a JsonTypeInfo modifier.
//
// Deserialize (wire -> proxy) materializes the native object graph and is deliberately out of scope here — use
// Inutil.Json.To<T> for that.
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Il2CppInterop.Runtime.InteropTypes;

namespace Inutil;

public static class Wire
{
    static readonly JsonSerializerOptions Options = Build();

    static JsonSerializerOptions Build()
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new WireProxyConverterFactory());
        // Enums (1296 in EFT — the largest converter-KIND class) serialize by NAME, the wire form. They marshal
        // to real managed enums, so the BCL converter covers every one game-agnostically — no per-type stamping.
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    /// <summary>Serialize an il2cpp proxy to its wire JSON through the recovered <c>[JsonPropertyName]</c> names.
    /// Fails loud if the proxy carries none (the interop looks unpatched — run inutil-interoppatch).</summary>
    public static string Serialize(Il2CppObjectBase proxy)
    {
        if (proxy is null) throw new ArgumentNullException(nameof(proxy));
        WireLoop.Reset();   // start each top-level serialize with an empty ancestor path
        return JsonSerializer.Serialize(proxy, proxy.GetType(), Options);
    }
}

// ReferenceLoopHandling.Ignore, by hand — net6 System.Text.Json has no ReferenceHandler.IgnoreCycles (net7+). The
// ancestor path is the NATIVE pointers of the proxies currently open on the serialize stack; a value already on it
// is a loop and is skipped. Keyed by Il2CppObjectBase.Pointer, not managed identity, because two proxy wrappers of
// the SAME native object are distinct managed objects. Non-generic + [ThreadStatic] so ONE path is shared across
// every closed WireProxyConverter<T> — the loop crosses types (ItemDescriptor -> GridDescriptor -> ... -> ItemDescriptor).
static class WireLoop
{
    [ThreadStatic] static HashSet<IntPtr>? _path;
    public static HashSet<IntPtr> Path => _path ??= new HashSet<IntPtr>();
    public static void Reset() => _path?.Clear();
}

// Converts EXACTLY the il2cpp proxies (Il2CppObjectBase subtypes) — a plain BCL leaf or a POCO serialises through
// STJ's normal path, so the opt-in (wire-marked members only) applies precisely where bookkeeping must be excluded.
public sealed class WireProxyConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) => typeof(Il2CppObjectBase).IsAssignableFrom(typeToConvert);

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
        => (JsonConverter)Activator.CreateInstance(typeof(WireProxyConverter<>).MakeGenericType(typeToConvert))!;
}

sealed class WireProxyConverter<T> : JsonConverter<T> where T : Il2CppObjectBase
{
    // The opt-in projection of T: only members carrying a recovered [JsonPropertyName] (renames applied, interop
    // bookkeeping excluded because bookkeeping is never marked). Static per closed type. A type absent from the
    // wiremap (marker-less / default-serialized, e.g. EFT.ProfileSettings) has none — see the fail-loud in Write.
    static readonly WireMember[] Members = WireMember.Recovered(typeof(T));

    // Converter KIND "string" (EFT.MongoID) — InteropPatch stamped [WireKind("string")] on the type from the
    // wiremap's typeKinds. Such a type serializes as a BARE STRING (its ToString wire form), NOT a member object,
    // wherever it appears — a scalar member, a dictionary value, a nested field. Read once per closed type.
    static readonly bool StringKind = typeof(T).GetCustomAttribute<WireKindAttribute>()?.Kind == "string";

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
    {
        if (value is null) { writer.WriteNullValue(); return; }
        if (StringKind) { writer.WriteStringValue(value.ToString()); return; }   // MongoID -> "<hex>" (converter kind)
        if (Members.Length == 0) { WriteOpaque(writer, value); return; }         // JToken / un-flipped il2cpp collection / opaque

        HashSet<IntPtr> path = WireLoop.Path;
        IntPtr id = value.Pointer;
        if (!path.Add(id)) { writer.WriteNullValue(); return; }   // reached this object via a loop (e.g. a collection element) -> Ignore
        try
        {
            writer.WriteStartObject();
            foreach (WireMember member in Members)
            {
                object? leaf = member.Get(value);
                if (leaf is Il2CppObjectBase nested && path.Contains(nested.Pointer)) continue;   // member loops back to an ancestor -> omit it (Ignore)
                writer.WritePropertyName(member.Wire);
                WriteLeaf(writer, leaf, options);
            }
            writer.WriteEndObject();
        }
        finally { path.Remove(id); }
    }

    // A member value: a dictionary is written by hand so a KEY that is itself a converter-kind type (a MongoID key,
    // an enum key) becomes its wire string — .NET 6's System.Text.Json has no custom dictionary-key converter
    // (WriteAsPropertyName is net7+), so the built-in path rejects a MongoID key. Everything else defers to STJ by
    // runtime type (a nested proxy recurses through this converter; a List<proxy> becomes an array of them).
    static void WriteLeaf(Utf8JsonWriter writer, object? leaf, JsonSerializerOptions options)
    {
        if (leaf is null) { writer.WriteNullValue(); return; }
        if (leaf is System.Collections.IDictionary dict)
        {
            writer.WriteStartObject();
            foreach (System.Collections.DictionaryEntry entry in dict)
            {
                writer.WritePropertyName(entry.Key?.ToString() ?? "");   // MongoID -> hex, enum -> name, string -> itself
                WriteLeaf(writer, entry.Value, options);
            }
            writer.WriteEndObject();
            return;
        }
        JsonSerializer.Serialize(writer, leaf, leaf.GetType(), options);
    }

    // OPAQUE: a value with no recovered wire members — a raw Newtonsoft JToken, an il2cpp collection natural-typing
    // didn't flip (IList<JToken>, an interface), or a typeKinds=opaque polymorphic type. Only the game's OWN
    // serializer renders these faithfully (they're exactly the shapes recovered metadata can't structurally
    // describe), so delegate that subtree to Inutil.Json.From — which reads the intact NATIVE attributes, so the
    // result is fully wire-correct. With no serializer registered (a game without a Newtonsoft, or genuinely
    // unpatched interop) it fails loud rather than guess.
    static void WriteOpaque(Utf8JsonWriter writer, T value)
    {
        if (!Json.IsSerializerConfigured)
            throw new InvalidOperationException(
                $"Inutil.Wire: {typeof(T).FullName} has no recovered wire members and no game serializer is " +
                "registered to render it as an opaque value — the interop looks unpatched (run inutil-interoppatch), " +
                "or register Inutil.Json.UseSerializer for opaque/raw-JSON members.");
        try
        {
            JsonNode? node = Json.From(value);
            if (node is null) writer.WriteNullValue();
            else node.WriteTo(writer);
        }
        catch
        {
            // A CONTEXT-DEPENDENT opaque value the game serializer cannot render STANDALONE — e.g. an
            // Il2CppSystem.IList<JToken> of JProperties, which is only valid inside its parent object, never as a
            // root array (Newtonsoft: "PropertyName in state ArrayStart"). Degrade to null rather than tear the whole
            // serialize; a rare, pathological shape, and the opaque path is already the last resort.
            writer.WriteNullValue();
        }
    }

    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => throw new NotSupportedException(
            "Inutil.Wire: deserialize (wire -> proxy) materializes the native object graph — use Inutil.Json.To<T> " +
            "(the game's own deserializer) for wire -> live proxy.");
}

readonly struct WireMember
{
    public readonly string Wire;
    public readonly Func<object, object?> Get;
    WireMember(string wire, Func<object, object?> get) { Wire = wire; Get = get; }

    // RECOVERED: only members carrying a [JsonPropertyName] InteropPatch re-attached — the precise, opt-in list
    // for a wiremap type (renames applied, bookkeeping excluded because bookkeeping is never marked).
    public static WireMember[] Recovered(Type type)
    {
        var members = new List<WireMember>();
        foreach (PropertyInfo p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            JsonPropertyNameAttribute? attr = p.GetCustomAttribute<JsonPropertyNameAttribute>();
            if (attr is null || p.GetMethod is null) continue;
            PropertyInfo prop = p;
            members.Add(new WireMember(attr.Name, obj => Read(() => prop.GetValue(obj))));
        }
        foreach (FieldInfo f in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            JsonPropertyNameAttribute? attr = f.GetCustomAttribute<JsonPropertyNameAttribute>();
            if (attr is null) continue;
            FieldInfo field = f;
            members.Add(new WireMember(attr.Name, obj => Read(() => field.GetValue(obj))));
        }
        return members.ToArray();
    }

    static object? Read(Func<object?> get) { try { return get(); } catch { return null; } }
}

// The recovered converter KIND, stamped on a proxy TYPE by InteropPatch from the wiremap's typeKinds. Kind
// "string" (EFT.MongoID) => the type serializes as a bare string; "opaque" => polymorphic raw. Enums are NOT
// stamped — they marshal to managed enums the global JsonStringEnumConverter covers. On the type (not the member)
// so it holds wherever a value of that type appears — member, dictionary value, nested field.
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false)]
public sealed class WireKindAttribute : Attribute
{
    public string Kind { get; }
    public WireKindAttribute(string kind) => Kind = kind;
}

// Inutil.Json — the per-game serialize/deserialize SEAM: (json string <-> il2cpp object) through the GAME's OWN
// converters. A per-game shim registers the actual JsonConvert.Serialize/DeserializeObject call once via
// UseSerializer/UseDeserializer, and this class does only the game-agnostic half (resolve the il2cpp type,
// delegate, Cast) — so deep/polymorphic graphs, enums, and wire converters "just work" with NO shadow DTO
// hierarchy. Wire.WriteOpaque delegates here for a proxy with no recovered wire members (the game's own
// serializer is the only thing that can render those subtrees faithfully).
//
// THE ENGINE STAYS NEWTONSOFT-FREE. Il2CppNewtonsoft is a per-game FRAMEWORK proxy — absent in games that don't
// bundle Newtonsoft (ToyGame included) — so it is NOT compile-referenced here. The coupling is a DELEGATE SEAM: a
// per-game shim (which CAN reference the game's Newtonsoft proxy) registers the actual (de)serialize call once,
// and this class does only the game-agnostic half — so the plumbing is testable on a non-Newtonsoft game via a
// FAKE (de)serializer. A game without one registered fails LOUD.
using System;
using System.Text.Json.Nodes;
using Il2CppInterop.Runtime;                 // Il2CppType.Of<T> — the target il2cpp System.Type
using Il2CppInterop.Runtime.InteropTypes;    // Il2CppObjectBase (.Cast<T>)

namespace Inutil;

public static class Json
{
    // The per-game deserialize seam: (json, targetIl2cppType) -> the deserialized il2cpp object (null round-trips).
    // Registered once at startup by a per-game shim; kept behind Deserializer() so an unconfigured call fails loud.
    static Func<string, Il2CppSystem.Type, Il2CppObjectBase?>? _deserialize;

    // The per-game SERIALIZE seam — the exact mirror: (il2cppObject) -> its wire JSON. The object knows its own
    // runtime type, so (unlike deserialize) no target Type is needed. Registered once by the same per-game shim that
    // supplies the deserializer; kept behind Serializer() so an unconfigured call fails loud. Feeds From<T> — the
    // object-first entry point (mirror of Deserialize()).
    static Func<Il2CppObjectBase, string>? _serialize;

    /// <summary>Register the game's deserializer ONCE at startup. The shim references the game's Newtonsoft proxy and
    /// supplies (json, il2cppType) =&gt; JsonConvert.DeserializeObject(json, il2cppType, gameSettings). Keeps the engine
    /// Newtonsoft-free. A later call replaces the delegate.</summary>
    public static void UseDeserializer(Func<string, Il2CppSystem.Type, Il2CppObjectBase?> deserialize)
        => _deserialize = deserialize ?? throw new ArgumentNullException(nameof(deserialize));

    /// <summary>True once a deserializer is registered (a mod can gate a JSON path on it).</summary>
    public static bool IsConfigured => _deserialize is not null;

    static Func<string, Il2CppSystem.Type, Il2CppObjectBase?> Deserializer()
        => _deserialize ?? throw new InvalidOperationException(
            "Inutil.Json: no deserializer registered — call Inutil.Json.UseDeserializer(...) once at startup " +
            "(a per-game shim supplies the game's JsonConvert.DeserializeObject, keeping the engine Newtonsoft-free).");

    /// <summary>Deserialize a JSON string into the il2cpp type T through the game's registered converters. null if the
    /// deserializer returns null. Throws if no deserializer is registered or T has no il2cpp class.</summary>
    public static T? To<T>(string json) where T : Il2CppObjectBase
    {
        if (json is null) throw new ArgumentNullException(nameof(json));
        Func<string, Il2CppSystem.Type, Il2CppObjectBase?> d = Deserializer();
        Il2CppSystem.Type t = Il2CppType.Of<T>()
            ?? throw new InvalidOperationException(
                $"Inutil.Json.To<{typeof(T).Name}>: Il2CppType.Of<{typeof(T).Name}> is null — the type is not in this game's il2cpp metadata.");
        Il2CppObjectBase? o = d(json, t);
        return o is null ? null : o.Cast<T>();
    }

    /// <summary>Coerce a DOM node into the il2cpp type T. Serializes the node and defers to To&lt;T&gt;(string).
    /// A null node -&gt; null.</summary>
    public static T? To<T>(JsonNode? node) where T : Il2CppObjectBase
        => node is null ? null : To<T>(node.ToJsonString());

    /// <summary>Deserialize a JSON array into a natural T[] through the game's converters — the array twin of
    /// <see cref="To{T}(string)"/>, so a mod drops the Il2CppReferenceArray&lt;T&gt; spelling. Deserializes into the
    /// il2cpp reference array (T is a reference/proxy element) then hands back the managed T[] (op_Implicit). null if
    /// the deserializer returns null. For a VALUE/struct element use the game's struct-array path directly.</summary>
    public static T[]? ToArray<T>(string json) where T : Il2CppObjectBase
    {
        Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<T>? arr =
            To<Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<T>>(json);
        return arr is null ? null : (T[])arr;   // op_Implicit Il2CppReferenceArray<T> -> T[]
    }

    /// <summary>Node twin of <see cref="ToArray{T}(string)"/> — a serve boundary hands its built DOM array straight
    /// to the game's converters (no ToJsonString at the call site). A null node -&gt; null.</summary>
    public static T[]? ToArray<T>(JsonNode? node) where T : Il2CppObjectBase
        => node is null ? null : ToArray<T>(node.ToJsonString());

    /// <summary>Deserialize a JSON object into a natural BCL Dictionary&lt;K,V&gt; through the game's converters — the
    /// dict twin of <see cref="To{T}(string)"/>/<see cref="ToArray{T}(string)"/>, so a mod drops the
    /// Il2CppSystem.Dictionary spelling. Deserializes into the il2cpp Dictionary&lt;K,V&gt; (keys built IL2CPP-SIDE by
    /// the game's dict converter, so a ref-bearing value-type key like MongoID is materialised natively — no
    /// managed-insertion collapse) then hands back the managed Dictionary through the shared Conv engine
    /// (Il2CppMarshal.ToManaged — one marshaller). null if the deserializer returns null.</summary>
    public static System.Collections.Generic.Dictionary<K, V>? ToDict<K, V>(string json) where K : notnull
    {
        Il2CppSystem.Collections.Generic.Dictionary<K, V>? d =
            To<Il2CppSystem.Collections.Generic.Dictionary<K, V>>(json);
        return d is null ? null : Inutil.Marshal.Il2CppMarshal.ToManaged<System.Collections.Generic.Dictionary<K, V>>(d);
    }

    /// <summary>Register the game's serializer ONCE at startup — the mirror of <see cref="UseDeserializer"/>. The shim
    /// references the game's Newtonsoft proxy and supplies (obj) =&gt; JsonConvert.SerializeObject(obj, gameSettings),
    /// keeping the engine Newtonsoft-free. A later call replaces the delegate.</summary>
    public static void UseSerializer(Func<Il2CppObjectBase, string> serialize)
        => _serialize = serialize ?? throw new ArgumentNullException(nameof(serialize));

    /// <summary>True once a serializer is registered (a mod can gate an object-first serialize path on it).</summary>
    public static bool IsSerializerConfigured => _serialize is not null;

    static Func<Il2CppObjectBase, string> Serializer()
        => _serialize ?? throw new InvalidOperationException(
            "Inutil.Json: no serializer registered — call Inutil.Json.UseSerializer(...) once at startup " +
            "(a per-game shim supplies the game's JsonConvert.SerializeObject, keeping the engine Newtonsoft-free).");

    /// <summary>Serialize an il2cpp object into a DOM node through the game's OWN converters — the wire form. A null
    /// object -&gt; null. Throws if no serializer is registered.</summary>
    public static JsonNode? From<T>(T? obj) where T : Il2CppObjectBase
    {
        if (obj is null) return null;
        string json = Serializer()(obj);
        return JsonNode.Parse(json);
    }

    /// <summary>The object-typed twin of <see cref="From{T}(T)"/> for values whose STATIC type is System.Object — e.g.
    /// an interop-flipped object param/property (SendRequest.Params) a hook receives, where the runtime type is an
    /// il2cpp proxy the mod cannot (or should not) name. Keeps mod code il2cpp-spelling-free. A non-il2cpp object
    /// fails LOUD: this seam serializes through the game's converters only (use System.Text.Json for managed data).</summary>
    public static JsonNode? From(object? obj)
    {
        if (obj is null) return null;
        if (obj is not Il2CppObjectBase b)
            throw new ArgumentException(
                $"Inutil.Json.From(object): expected an il2cpp object, got {obj.GetType().FullName} — " +
                "this seam serializes through the GAME's converters; serialize managed objects with System.Text.Json.");
        return JsonNode.Parse(Serializer()(b));
    }
}

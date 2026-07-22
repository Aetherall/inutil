// Metadata-pillar fixture (../../../../docs/contribution/architecture/16-metadata.md): a DTO whose SERIALIZED (wire) names differ from its C# member
// names, tagged with CUSTOM attributes defined here in the toy — NO Newtonsoft dependency. Il2CppInterop STRIPS these
// attributes off the generated proxies, so a running game / a proxy-only generator cannot recover the wire names;
// inutil's own Cpp2IL metadata pass CAN. Proving recovery on a custom attribute IS proving the engine.
using System;
using UnityEngine;

namespace ToyGame
{
    // The wire (serialized) name of a member, when it differs from the C# name (EFT: _id/_tpl/savage/aid).
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
    public sealed class WireNameAttribute : Attribute
    {
        public string Name { get; }
        public WireNameAttribute(string name) { Name = name; }
    }

    // The §7.13 converter taxonomy, mirrored in the toy (order matches inutil's ConverterKind: String/Enum/Opaque).
    public enum WireKind { String, Enum, Opaque }

    // A converter-driven wire shape (the [JsonConverter] analogue): the member re-serializes in this KIND rather
    // than its structural proxy shape (e.g. an enum as its string name).
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
    public sealed class WireConverterAttribute : Attribute
    {
        public WireKind Kind { get; }
        public WireConverterAttribute(WireKind kind) { Kind = kind; }
    }

    // The attributed DTO. Handle serializes as "Nickname", Side as "faction" with an enum converter — wire names a
    // proxy-only tool cannot know. Gold is plain (structural: wire name == C# name). _level is a Unity-persistence
    // marker (a Persisted fact, no wire name). Rooted in Bootstrap.Exercise so IL2CPP keeps it and the attributes.
    [Serializable]
    public class WireProfile
    {
        [WireName("Nickname")] public string Handle;
        [WireName("faction")] [WireConverter(WireKind.Enum)] public Faction Side;
        [SerializeField] int _level;
        public int Gold;

        public WireProfile() { Handle = "anon"; Side = Faction.Scav; _level = 1; Gold = 0; }

        // Keep every member a live, read target so stripping retains them (and the attribute metadata with them).
        public string Describe() => $"{Handle}:{Side}:{_level}:{Gold}";
    }
}

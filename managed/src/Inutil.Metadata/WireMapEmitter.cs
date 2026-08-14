using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Inutil.Schema;

namespace Inutil.Metadata;

// The wire-PROTOCOL MAP emitter (docs/contribution/architecture/16-metadata.md) — the consumer of the
// normalized model. It emits the FULL attributed DTO graph: every SERIALIZED type (one with a
// JSON/Unity serialization marker on some member) and, for each, ALL its members with the wire name (recovered,
// or the member's own name), the converter (+ kind), the persisted flag, and nullability. High value for
// backend/network modders: only ingested il2cpp metadata yields it — a running game sees these attributes STRIPPED.
//
// A thin consumer over the shared WireModel — no second walk, no new dependency. Deterministic: types and members
// are Ordinal-sorted, so the artifact is byte-identical on an idempotent re-run.
public static class WireMapEmitter
{
    public const string FileName = "inutil.wiremap.json";

    public static string EmitJson(WireModel model)
    {
        var serialized = model.SerializedClosure()
            .OrderBy(t => t.FullName, StringComparer.Ordinal);
        // Per-TYPE converter kinds: every type carrying its OWN converter, keyed by its real il2cpp name.
        // Independent of `types` — a converter leaf type may supply a kind without being a serialized DTO
        // (WireMap.KindOf reads it).
        var typeKinds = model.Types
            .Where(t => t.TypeConverterKind is not null)
            .OrderBy(t => t.FullName, StringComparer.Ordinal);

        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();

            w.WriteStartObject("types");
            foreach (WireModelType type in serialized)
            {
                w.WriteStartObject(type.FullName);
                w.WriteStartArray("members");
                foreach (WireModelMember m in type.Members.OrderBy(m => m.Name, StringComparer.Ordinal))
                    WriteMember(w, m);
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndObject();

            w.WriteStartObject("typeKinds");
            foreach (WireModelType type in typeKinds)
                w.WriteString(type.FullName, type.TypeConverterKind);
            w.WriteEndObject();

            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray()) + "\n";   // trailing newline: POSIX + stable idempotency compare
    }

    static void WriteMember(Utf8JsonWriter w, WireModelMember m)
    {
        w.WriteStartObject();
        w.WriteString("name", m.Name);
        w.WriteString("type", m.MemberType);
        // The wire key: the recovered JSON name, or the member's OWN C# name (which IS its wire name). Emitting it
        // (not omitting the member) is what makes this the FULL graph rather than just the annotated fields.
        w.WriteString("wire", m.Fact(FactKind.WireName)?.Value ?? m.Name);

        WireFact? conv = m.Fact(FactKind.Converter);
        if (conv is not null)
        {
            w.WriteStartObject("converter");
            w.WriteString("type", conv.Value);
            w.WriteString("kind", conv.ConverterKind switch
            {
                ConverterKind.String => "string",
                ConverterKind.Enum   => "enum",
                _                    => "opaque",
            });
            w.WriteEndObject();
        }

        if (m.Has(FactKind.Persisted)) w.WriteBoolean("persisted", true);

        WireFact? nul = m.Fact(FactKind.Nullability);
        if (nul is not null)
            w.WriteString("nullability", nul.Value switch   // the [Nullable] flag decoded to a stable label
            {
                "1" => "not-null",
                "2" => "nullable",
                _   => "oblivious",
            });

        w.WriteEndObject();
    }
}

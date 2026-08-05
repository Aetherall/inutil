using System.Collections.Generic;

namespace Inutil.Schema;

// What a proxy patch does that the correspondence registry does NOT describe.
//
// SchemaMarker.Hash addresses a patched interop tree by hashing Families.Default(), and that was sound for exactly
// as long as every rewriter was registry-driven: the registry then WAS the full description of the patch, so hashing
// it was the honest content-address rather than a proxy for one. The equality-pairing pass broke that — it flips on a
// fact about Il2CppInterop's code GENERATION (a hash/equals pair split across two sources), not on any correspondence
// row — so a tree patched before it existed and a tree patched after it would have addressed IDENTICAL. A stale tree
// that reports itself current is the one failure the marker exists to prevent.
//
// So the address folds these rows in too. Adding a non-registry pass means adding its row here, and that is the whole
// obligation: the hash moves, every already-patched tree reads stale, and the loader shim / installer re-patches it.
// A row is an opaque tag — its CONTENT never matters, only that it changes when the patch's behaviour does.
public static class PatchCapabilities
{
    // One row per non-registry capability, tagged with the revision of its behaviour. Bump a tag when the pass starts
    // producing different IL for the same input; add a row when a new non-registry pass lands.
    public static IReadOnlyList<string> All { get; } = new[]
    {
        "equality-pairing/1",   // EqualityRewriter — Equals(object) sourced where GetHashCode came from
        "exact-proxy-types/1",  // PoolRetargetRewriter + the exact-type map — a proxy is built at the object's ACTUAL class
    };
}

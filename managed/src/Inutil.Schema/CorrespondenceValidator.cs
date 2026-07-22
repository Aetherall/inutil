namespace Inutil.Schema;

// The thin seam-boundary guard: validate a Conv tree (each container node stamped with its expected il2cpp
// anchor) against the game's ACTUAL il2cpp element, node-by-node (`Conv.Il2CppType == gameElem`). The RECURSIVE
// form of IBridge.CanBind — it walks the whole nested spelling, so a mod that spells `Task<Dictionary<string,
// int>>` over a game method that returns `Task<List<int>>` is rejected at the mismatched node instead of blowing
// up mid-conversion with an opaque il2cpp error.
//
// Why the tree drives it, not the game type: the game's il2cpp element is always already il2cpp-native, so a gate
// on it is dead; the SHAPE to validate is the one the mod spelled.
//
// Reject-conservatively (fail safe = defer): any mismatch (anchor, or child/argument arity) fails the whole
// bridge with a human-readable reason, and the seam leaves the value wrapper-typed (never a half-bridge). A leaf,
// or a node with no anchor, is unvalidatable and accepts any il2cpp-native type (identity pass-through).
public static class CorrespondenceValidator
{
    public readonly struct Result
    {
        public bool Ok { get; }
        public string? Mismatch { get; }   // null iff Ok; else why the bridge was rejected (for the warn-loud path)
        Result(bool ok, string? mismatch) { Ok = ok; Mismatch = mismatch; }
        public static Result Pass() => new(true, null);
        public static Result Fail(string why) => new(false, why);
    }

    public static Result Matches(Conv node, ITypeRef gameType)
    {
        // Unvalidatable node — a leaf or a container with no correspondence anchor. Identity pass-through accepts
        // any il2cpp-native type; nothing to check, nothing to reject on.
        if (node.Identity || node.Il2CppAnchor is null)
            return Result.Pass();

        // The `Conv.Il2CppType == gameElem` check: the game element's open anchor must be the one the hook
        // spelling expects. (A non-generic game type compares by its FullName; a closed instance by its element.)
        string gameAnchor = gameType.IsGenericInstance ? gameType.ElementFullName : gameType.FullName;
        if (gameAnchor != node.Il2CppAnchor)
            return Result.Fail($"{node.Kind} '{node.Managed}': hook expects il2cpp '{node.Il2CppAnchor}', " +
                               $"game element is '{gameAnchor}'");

        // Arity must line up so each spelled child validates against its OWN game argument (a Dictionary's key vs
        // value, a tuple's positions). An array's element rides in GenericArguments[0] (the seam normalizes
        // Il2CppReferenceArray<T> & siblings to that).
        if (node.Children.Count != gameType.GenericArguments.Count)
            return Result.Fail($"{node.Kind} '{node.Managed}': {node.Children.Count} spelled element(s) vs " +
                               $"{gameType.GenericArguments.Count} on game type '{gameType.FullName}'");

        for (int i = 0; i < node.Children.Count; i++)
        {
            Result child = Matches(node.Children[i], gameType.GenericArguments[i]);
            if (!child.Ok) return child;
        }
        return Result.Pass();
    }
}

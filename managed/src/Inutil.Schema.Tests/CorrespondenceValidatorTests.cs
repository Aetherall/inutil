using Inutil.Schema;

namespace Inutil.Schema.Tests;

// Proves the correspondence-validation guard: a Conv tree stamped with expected il2cpp anchors is matched
// node-by-node against the game's ACTUAL il2cpp element, rejecting a spelling that doesn't line up BEFORE any
// conversion runs — the recursive form of CanBind. Fully synthetic: managed spellings on one side, FakeTypeRef
// game elements on the other, a representative anchor map between. No game, no runtime.
static class CorrespondenceValidatorTests
{
    // Representative open il2cpp anchors per container kind. In the seam these are verified against real proxies;
    // here they only need to be internally consistent to prove the walk. Array is the seam's normalized ref-array
    // anchor (Il2CppReferenceArray<T> & siblings collapse to it); Tuple's arity is appended by DictionaryAnchors.
    static IIl2CppAnchors Anchors() => new DictionaryAnchors(new Dictionary<ConvKind, string>
    {
        [ConvKind.Task]       = GTask,
        [ConvKind.Dictionary] = GDict,
        [ConvKind.List]       = GList,
        [ConvKind.Enumerable] = "Il2CppSystem.Collections.Generic.IEnumerable`1",
        [ConvKind.Nullable]   = "Il2CppSystem.Nullable`1",
        [ConvKind.Array]      = GArr,
        [ConvKind.Tuple]      = "Il2CppSystem.ValueTuple`",
    });

    const string GTask = "Il2CppSystem.Threading.Tasks.Task`1";
    const string GDict = "Il2CppSystem.Collections.Generic.Dictionary`2";
    const string GList = "Il2CppSystem.Collections.Generic.List`1";
    const string GArr  = "Il2CppInterop.Runtime.Il2CppReferenceArray`1";
    const string GStr  = "Il2CppSystem.String";
    const string GInt  = "Il2CppSystem.Int32";

    static ITypeRef G(string element, params ITypeRef[] args) => FakeTypeRef.Generic(element, args);
    static ITypeRef S(string name) => FakeTypeRef.Simple(name);

    public static void Run()
    {
        var shapes = new ReflectionConvShapeSource();
        var anchors = Anchors();
        Conv Build(Type t) => Conv.Build(t, shapes, anchors);

        // ── the tree carries its expected il2cpp anchors (Conv.Il2CppType is populated) ──────────────
        {
            var c = Build(typeof(System.Threading.Tasks.Task<int[]>));
            T.Check("Task node stamped with its il2cpp anchor", c.Il2CppAnchor == GTask);
            T.Check("Array child stamped with its il2cpp anchor", c.Children[0].Il2CppAnchor == GArr);
            T.Check("leaf int carries no anchor (accepts anything)", c.Children[0].Children[0].Il2CppAnchor is null);
        }

        // ── happy path: a deep, multi-child spelling that lines up with the game element ──────────────
        {
            var conv = Build(typeof(System.Threading.Tasks.Task<Dictionary<string, int[]>>));
            var game = G(GTask, G(GDict, S(GStr), G(GArr, S(GInt))));
            T.Check("deep Task<Dictionary<string,int[]>> matches an aligned game element",
                CorrespondenceValidator.Matches(conv, game).Ok);
        }

        // ── outer mismatch: spelled Task, game method actually returns a List ────────────────────────
        {
            var conv = Build(typeof(System.Threading.Tasks.Task<int>));
            var r = CorrespondenceValidator.Matches(conv, G(GList, S(GInt)));
            T.Check("outer family mismatch (Task vs List) is rejected", !r.Ok);
            T.Check("mismatch names both the expected and actual anchor",
                r.Mismatch != null && r.Mismatch.Contains(GTask) && r.Mismatch.Contains(GList));
        }

        // ── nested mismatch: spelled Task<Dictionary<..>>, game has Task<List<..>> ────────────────────
        {
            var conv = Build(typeof(System.Threading.Tasks.Task<Dictionary<string, int>>));
            var r = CorrespondenceValidator.Matches(conv, G(GTask, G(GList, S(GInt))));
            T.Check("nested family mismatch (Dictionary vs List) is rejected", !r.Ok);
            T.Check("nested mismatch is reported at the Dictionary node",
                r.Mismatch != null && r.Mismatch.Contains(GDict));
        }

        // ── leaf accepts ANY il2cpp-native game type: the validator constrains containers, not leaves ─
        {
            var conv = Build(typeof(System.Threading.Tasks.Task<int>));
            var game = G(GTask, S("Il2CppToyGame.SomeEnum"));   // game leaf differs from spelled int — still fine
            T.Check("container matches; a differing leaf type is accepted (runtime owns the leaf)",
                CorrespondenceValidator.Matches(conv, game).Ok);
        }

        // ── tuple arity is baked into the anchor: 2-tuple spelled vs 3-tuple game -> reject ───────────
        {
            var conv = Build(typeof(ValueTuple<int, string>));
            T.Check("2-tuple stamped with the arity-2 anchor", conv.Il2CppAnchor == "Il2CppSystem.ValueTuple`2");
            var game = G("Il2CppSystem.ValueTuple`3", S(GInt), S(GStr), S(GInt));
            T.Check("ValueTuple arity mismatch (2 vs 3) is rejected",
                !CorrespondenceValidator.Matches(conv, game).Ok);
        }

        // ── arity guard: right anchor, wrong number of game arguments ─────────────────────────────────
        {
            var conv = Build(typeof(Dictionary<string, int>));   // 2 children
            var r = CorrespondenceValidator.Matches(conv, G(GDict, S(GStr)));   // only 1 game arg
            T.Check("dictionary child-count vs game arg-count mismatch is rejected", !r.Ok);
            T.Check("arity mismatch is reported", r.Mismatch != null && r.Mismatch.Contains("element"));
        }

        // ── array element correspondence + its mismatch ──────────────────────────────────────────────
        {
            var arr = Build(typeof(int[]));
            T.Check("int[] matches a game ref-array", CorrespondenceValidator.Matches(arr, G(GArr, S(GInt))).Ok);
            T.Check("int[] vs a game List is rejected", !CorrespondenceValidator.Matches(arr, G(GList, S(GInt))).Ok);
        }

        // ── opt-in: an UNANNOTATED tree (Build without anchors) never rejects — validation is inert ───
        {
            var conv = Conv.Build(typeof(System.Threading.Tasks.Task<int>), shapes);   // no anchors passed
            T.Check("unannotated tree accepts even a wrong game element (validation is opt-in)",
                CorrespondenceValidator.Matches(conv, G(GList, S(GStr))).Ok);
        }
    }
}

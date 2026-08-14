using Inutil.Schema;

namespace Inutil.Schema.Tests;

// Proves the recursive conversion tree is GENERIC and COMPOSES — the exact property a single-`inner` signature
// could not provide. Uses synthetic nested BCL shapes (Task<int[]>, Task<Dictionary<string,int[]>>,
// ValueTuple<int[],string>, Task<int[][]>) and a tagging runtime that stamps each leaf with its managed type,
// so a round-trip both reconstructs the value AND proves each element went through its OWN child converter
// (Dictionary key vs value, tuple positions) — all offline.
static class ConvTests
{
    public static void Run()
    {
        var shapes = new ReflectionConvShapeSource();
        var rt = new TagRuntime();

        // ── tree STRUCTURE: multi-level + multi-child ────────────────────────────────────────────
        {
            var c = Conv.Build(typeof(System.Threading.Tasks.Task<int[]>), shapes);
            T.Check("Task<int[]>: Task -> Array -> Leaf(int)",
                c.Kind == ConvKind.Task && c.Children.Count == 1
                && c.Children[0].Kind == ConvKind.Array && c.Children[0].Children[0].Kind == ConvKind.Leaf
                && c.Children[0].Children[0].Managed == typeof(int));
        }
        {
            var c = Conv.Build(typeof(System.Threading.Tasks.Task<Dictionary<string, int>>), shapes);
            var dict = c.Children[0];
            T.Check("Task<Dictionary<string,int>>: Dict node holds TWO children (key, value)",
                dict.Kind == ConvKind.Dictionary && dict.Children.Count == 2
                && dict.Children[0].Managed == typeof(string) && dict.Children[1].Managed == typeof(int));
        }
        {
            var c = Conv.Build(typeof(ValueTuple<int[], string>), shapes);
            T.Check("ValueTuple<int[],string>: Tuple node holds N children (Array, Leaf)",
                c.Kind == ConvKind.Tuple && c.Children.Count == 2
                && c.Children[0].Kind == ConvKind.Array && c.Children[1].Kind == ConvKind.Leaf);
        }
        {
            var c = Conv.Build(typeof(System.Threading.Tasks.Task<int[][]>), shapes);
            T.Check("Task<int[][]>: recursion nests Task -> Array -> Array -> Leaf",
                c.Kind == ConvKind.Task && c.Children[0].Kind == ConvKind.Array
                && c.Children[0].Children[0].Kind == ConvKind.Array
                && c.Children[0].Children[0].Children[0].Kind == ConvKind.Leaf);
        }

        // ── Result mirror family (ConvKind.Result): the per-game reference-wrapper reached through the mirror ──
        // Structure: Task<Inutil.Bridge.Result<int[]>> nests Task -> Result -> Array -> Leaf (the array-element case
        // the game Result<T[]> can't spell). The Result node holds its ONE element child and recurses through it.
        {
            var c = Conv.Build(typeof(System.Threading.Tasks.Task<Inutil.Bridge.Result<int[]>>), shapes);
            T.Check("Task<Result<int[]>>: Task -> Result -> Array -> Leaf(int)",
                c.Kind == ConvKind.Task && c.Children.Count == 1
                && c.Children[0].Kind == ConvKind.Result && c.Children[0].Children.Count == 1
                && c.Children[0].Children[0].Kind == ConvKind.Array
                && c.Children[0].Children[0].Children[0].Managed == typeof(int));
        }
        {
            // Round-trip through the tagging runtime: the Result's element goes through its OWN (array->int) child, so
            // the deep value reconstructs and each leaf carries its managed-type tag — the recursion is real, not a
            // pass-through. (The synthetic runtime models the mirror value as a single-child Bx, like Task/Nullable.)
            var conv = Conv.Build(typeof(System.Threading.Tasks.Task<Inutil.Bridge.Result<int[]>>), shapes);
            object? original = Box(Box(Seq(5, 6, 7)));                 // Task( Result( int[] ) )
            object? il2 = conv.Convert(original, ConvDirection.ToIl2Cpp, rt);
            var resultVal = (Bx)((Bx)il2!).V!;                          // Task -> Result
            T.Check("Result element marshalled by its OWN (int) child", ((Sq)resultVal.V!).Items[0] is Tg { Type: "Int32" });
            T.Check("Task<Result<int[]>> round-trips", DeepEq(original, conv.Convert(il2, ConvDirection.ToManaged, rt)));
        }

        // ── Callback mirror family (ConvKind.Callback): the INBOUND twin of Result — same single-element tree shape ──
        // A hook spells Callback<int[]> for a game Callback<Il2CppReferenceArray<int>> param; the node holds its ONE
        // element child (the Result payload) and recurses through it. STRUCTURE here; the wrap-and-invoke seam is in-game
        // (ContainerBridge.MaterializeCallback), like the Result build/read shim.
        {
            var c = Conv.Build(typeof(Inutil.Bridge.Callback<int[]>), shapes);
            T.Check("Callback<int[]>: Callback -> Array -> Leaf(int)",
                c.Kind == ConvKind.Callback && c.Children.Count == 1
                && c.Children[0].Kind == ConvKind.Array
                && c.Children[0].Children[0].Managed == typeof(int));
        }
        {
            // The mirror's OUTBOUND surface is pure (the seam installs the invoker): Ok/Fail build the right
            // Inutil.Bridge.Result<T>, which the seam then dematerializes + hands to the game callback in-game.
            Inutil.Bridge.Result<int[]>? captured = null;
            var cb = new Inutil.Bridge.Callback<int[]>(r => captured = r);
            cb.Ok(new[] { 9 });
            T.Check("Callback.Ok builds a success Result carrying the value",
                captured is { } ok && ok.Succeed && ok.Value is [9]);
            cb.Fail("offline", 3);
            T.Check("Callback.Fail builds an error Result (no value, error+code set)",
                captured is { } er && !er.Succeed && er.Error == "offline" && er.ErrorCode == 3);
        }

        // ── DelegateIn mirror family (ConvKind.DelegateIn): the inbound-delegate twin — the GENERAL form of Callback ──
        // A hook spells Inutil.Bridge.Action<int[]> for a game System.Action<Il2CppReferenceArray<int>> param; the node
        // holds its arg child(ren) — a Func's LAST child is the return — and recurses through them. STRUCTURE here; the
        // wrap-and-invoke seam (ContainerBridge.MaterializeDelegate) is in-game, like the Callback/Result shim.
        {
            var c = Conv.Build(typeof(Inutil.Bridge.Action<int[]>), shapes);
            T.Check("Action<int[]>: DelegateIn -> Array -> Leaf(int)",
                c.Kind == ConvKind.DelegateIn && c.Children.Count == 1
                && c.Children[0].Kind == ConvKind.Array
                && c.Children[0].Children[0].Managed == typeof(int));
        }
        {
            var c = Conv.Build(typeof(Inutil.Bridge.Func<int, string>), shapes);
            T.Check("Func<int,string>: DelegateIn holds arg + return children (last child = the return)",
                c.Kind == ConvKind.DelegateIn && c.Children.Count == 2
                && c.Children[0].Managed == typeof(int) && c.Children[1].Managed == typeof(string));
        }
        {
            // The mirror's OUTBOUND surface is pure (the seam installs the invoker): Invoke boxes the SPELLED args to
            // object?[] and calls the invoker; a Func casts the invoker's boxed return to TResult; Raw holds the game delegate.
            object?[]? seen = null;
            var act = new Inutil.Bridge.Action<int, string>("raw", a => { seen = a; return null; });
            act.Invoke(7, "x");
            T.Check("Action mirror Invoke boxes its args to the seam invoker; Raw holds the game delegate",
                seen is [7, "x"] && act.Raw is "raw");

            var fn = new Inutil.Bridge.Func<int, string>("raw2", a => "got:" + a[0]);
            T.Check("Func mirror Invoke passes boxed args + casts the invoker's return to TResult",
                fn.Invoke(41) == "got:41" && fn.Raw is "raw2");
        }

        // ── identity fast-path ────────────────────────────────────────────────────────────────────
        T.Check("leaf int is Identity (fast path)", Conv.Build(typeof(int), shapes).Identity);
        T.Check("container Task<int> is NOT Identity", !Conv.Build(typeof(System.Threading.Tasks.Task<int>), shapes).Identity);

        // ── COMPOSITION round-trip through a deep, multi-child shape ─────────────────────────────
        {
            var conv = Conv.Build(typeof(System.Threading.Tasks.Task<Dictionary<string, int[]>>), shapes);
            object? original = Box(Dict(
                Pair("a", Seq(1, 2)),
                Pair("b", Seq(3))));

            object? il2 = conv.Convert(original, ConvDirection.ToIl2Cpp, rt);
            object? back = conv.Convert(il2, ConvDirection.ToManaged, rt);
            T.Check("deep round-trip Task<Dictionary<string,int[]>> reconstructs", DeepEq(original, back));

            // Multi-child correctness: in the il2cpp form the KEY went through the string child and the array
            // VALUES through the int child — DIFFERENT converters. A single shared `inner` would tag both the
            // same. Drill in: box -> dict -> pair0 -> (key Tag "String", value Seq of Tag "Int32").
            var pair0 = ((Kvps)((Bx)il2!).V!).Pairs[0];
            var keyTag = pair0.Key as Tg;
            var firstValTag = ((Sq)pair0.Value!).Items[0] as Tg;
            T.Check("Dict key marshalled by its OWN (string) child", keyTag?.Type == "String");
            T.Check("Dict value elements marshalled by their OWN (int) child", firstValTag?.Type == "Int32");
            T.Check("REGRESSION: key child != value child (multi-child, not one shared inner)",
                keyTag?.Type != firstValTag?.Type);
        }

        // ── tuple positions each recurse through their OWN child ─────────────────────────────────
        {
            var conv = Conv.Build(typeof(ValueTuple<int[], string>), shapes);
            object? original = Tup(Seq(7, 8), "x");
            object? il2 = conv.Convert(original, ConvDirection.ToIl2Cpp, rt);
            var items = ((Tp)il2!).Items;
            T.Check("tuple item0 (int[]) recursed to Tag Int32 elements", ((Sq)items[0]!).Items[0] is Tg { Type: "Int32" });
            T.Check("tuple item1 (string) recursed to Tag String", items[1] is Tg { Type: "String" });
            T.Check("tuple round-trip reconstructs", DeepEq(original, conv.Convert(il2, ConvDirection.ToManaged, rt)));
        }

        // ── null propagation ──────────────────────────────────────────────────────────────────────
        {
            var conv = Conv.Build(typeof(System.Threading.Tasks.Task<int[]>), shapes);
            T.Check("null converts to null (both directions)",
                conv.Convert(null, ConvDirection.ToIl2Cpp, rt) is null && conv.Convert(null, ConvDirection.ToManaged, rt) is null);
        }
    }

    // ── synthetic value reps: leaves become Tg(typeName,value) in il2cpp form; containers are Bx/Sq/Kvps/Tp ──
    sealed class Tg { public string Type = ""; public object? Value; }
    sealed class Bx { public object? V; }                                   // Task<T> / Nullable<T>
    sealed class Sq { public List<object?> Items = new(); }                 // Array / List / Enumerable
    sealed class Kvps { public List<KeyValuePair<object?, object?>> Pairs = new(); }  // Dictionary
    sealed class Tp { public List<object?> Items = new(); }                 // ValueTuple

    static object Box(object? v) => new Bx { V = v };
    static object Seq(params object?[] xs) => new Sq { Items = xs.ToList() };
    static KeyValuePair<object?, object?> Pair(object? k, object? v) => new(k, v);
    static object Dict(params KeyValuePair<object?, object?>[] ps) => new Kvps { Pairs = ps.ToList() };
    static object Tup(params object?[] xs) => new Tp { Items = xs.ToList() };

    sealed class TagRuntime : IConvRuntime
    {
        public object? Leaf(Conv node, object? value, ConvDirection dir)
            => dir == ConvDirection.ToIl2Cpp
                ? (value is null ? null : new Tg { Type = node.Managed.Name, Value = value })
                : (value is Tg t ? t.Value : value);

        public object? Container(Conv node, object? value, ConvDirection dir)
        {
            if (value is null) return null;
            switch (node.Kind)
            {
                case ConvKind.Task or ConvKind.Nullable or ConvKind.Result:   // single-child reference/value wrappers
                    return new Bx { V = node.Children[0].Convert(((Bx)value).V, dir, this) };
                case ConvKind.Array or ConvKind.List or ConvKind.Enumerable:
                    return new Sq { Items = ((Sq)value).Items.Select(x => node.Children[0].Convert(x, dir, this)).ToList() };
                case ConvKind.Dictionary:
                    return new Kvps
                    {
                        Pairs = ((Kvps)value).Pairs.Select(p => new KeyValuePair<object?, object?>(
                            node.Children[0].Convert(p.Key, dir, this),
                            node.Children[1].Convert(p.Value, dir, this))).ToList()
                    };
                case ConvKind.Tuple:
                    return new Tp { Items = ((Tp)value).Items.Select((x, i) => node.Children[i].Convert(x, dir, this)).ToList() };
                default:
                    return value;
            }
        }
    }

    static bool DeepEq(object? a, object? b)
    {
        if (a is null || b is null) return a is null && b is null;
        return (a, b) switch
        {
            (Tg x, Tg y) => x.Type == y.Type && DeepEq(x.Value, y.Value),
            (Bx x, Bx y) => DeepEq(x.V, y.V),
            (Sq x, Sq y) => x.Items.Count == y.Items.Count && x.Items.Zip(y.Items).All(p => DeepEq(p.First, p.Second)),
            (Tp x, Tp y) => x.Items.Count == y.Items.Count && x.Items.Zip(y.Items).All(p => DeepEq(p.First, p.Second)),
            (Kvps x, Kvps y) => x.Pairs.Count == y.Pairs.Count
                && x.Pairs.Zip(y.Pairs).All(p => DeepEq(p.First.Key, p.Second.Key) && DeepEq(p.First.Value, p.Second.Value)),
            _ => a.Equals(b),
        };
    }
}

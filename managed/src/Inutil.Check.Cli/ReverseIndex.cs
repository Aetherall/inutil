// ReverseIndex — a build-wide STATIC reverse-discovery index over the interop set, ported from runtime's
// codegen ReverseIndexEmitter. The forward direction (type -> its members) is just the interop proxies; this
// inverts it: for a target type T it answers
//   ctors      — how to construct a T (or a subtype)            [.ctor signatures]
//   producers  — what hands you a T: methods RETURNING T, fields/props HOLDING T
//   consumers  — what TAKES a T: methods/ctors with a T parameter
//
// Built ONCE in a single O(members) Cecil pass, then queried per type or dumped whole. It is the OFFLINE,
// per-TYPE complement to ot-eval's per-INSTANCE E.* dump: E.* walks one live object you already hold; this
// answers "what produces/consumes an X" precisely when you DON'T have one. Rollup: a reference to X is filed
// under X, X's base classes (so querying a base surfaces obfuscated-subtype producers), and X's generic
// arguments (so querying TransitPoint surfaces a `List<TransitPoint>` holder). Universal roots are excluded.
//
// DIVERGENCE FROM runtime's emitter: inutil is codegen-free — there are no `<Type>Calls.New/Method(...)`
// runnable helpers, so the gap-2↔4 helper annotation is dropped. A mod calls the game member directly through
// the interop proxy. Everything else (rollup, plumbing filters, sort/section format) is identical.

using System.Text;
using Mono.Cecil;

namespace Inutil.Check;

internal static class ReverseIndex
{
    // Filing a reference under these would index ~the whole image under one key — exclude them as keys.
    private static readonly HashSet<string> UniversalRoots = new(StringComparer.Ordinal)
    {
        "System.Object", "System.ValueType", "System.Enum", "System.Void",
        "System.Delegate", "System.MulticastDelegate",
    };

    internal sealed class Index
    {
        public readonly Dictionary<string, List<string>> Ctors = New();
        public readonly Dictionary<string, List<string>> ProducersReturn = New();
        public readonly Dictionary<string, List<string>> ProducersHeld = New();
        public readonly Dictionary<string, List<string>> Consumers = New();
        public readonly Dictionary<string, List<string>> BySimple = New(); // simpleName -> full keys
        public int Types, Members;
        private static Dictionary<string, List<string>> New() => new(StringComparer.Ordinal);

        public bool Has(string key) =>
            Ctors.ContainsKey(key) || ProducersReturn.ContainsKey(key) ||
            ProducersHeld.ContainsKey(key) || Consumers.ContainsKey(key);

        public IEnumerable<string> AllKeys() => Ctors.Keys
            .Concat(ProducersReturn.Keys).Concat(ProducersHeld.Keys).Concat(Consumers.Keys)
            .Distinct(StringComparer.Ordinal);

        public int KeyCount => AllKeys().Count();
    }

    // ---- build -------------------------------------------------------------------------------------

    public static Index Build(List<ModuleDefinition> modules)
    {
        var ix = new Index();
        var rollupCache = new Dictionary<string, string[]>(StringComparer.Ordinal);

        foreach (var mod in modules)
            foreach (var t in Naming.AllTypes(mod))
            {
                ix.Types++;
                if (IsCompilerGenerated(t)) continue;   // closures / iterator+async state machines: pure noise
                string decl = Naming.CleanTypeName(t);

                foreach (var m in t.Methods)
                {
                    if (m.Name == ".cctor") continue;

                    if (m.IsConstructor)
                    {
                        if (IsPlumbingCtor(m)) continue;
                        string sig = $"new {decl}({RenderParams(m)})";
                        foreach (var k in Rollup(t, rollupCache)) Add(ix.Ctors, k, sig);
                        // a ctor that takes a P is itself a consumer of P (you'd pass your P into that same New)
                        string csig = $"{decl}..ctor({RenderParams(m)})";
                        foreach (var p in m.Parameters)
                            foreach (var k in Rollup(p.ParameterType, rollupCache)) Add(ix.Consumers, k, csig);
                        ix.Members++;
                        continue;
                    }

                    if (m.IsGetter || m.IsSetter || m.IsAddOn || m.IsRemoveOn || m.IsFire) continue;
                    if (m.Name.StartsWith("op_", StringComparison.Ordinal)) continue;

                    string stat = m.IsStatic ? "  [static]" : "";
                    if (m.ReturnType != null && m.ReturnType.FullName != "System.Void")
                    {
                        string sig = $"{decl}.{m.Name}({RenderParams(m)}) : {Naming.CleanTypeName(m.ReturnType)}{stat}";
                        foreach (var k in Rollup(m.ReturnType, rollupCache)) Add(ix.ProducersReturn, k, sig);
                    }
                    if (m.HasParameters)
                    {
                        string sig = $"{decl}.{m.Name}({RenderParams(m)}){stat}";
                        foreach (var p in m.Parameters)
                            foreach (var k in Rollup(p.ParameterType, rollupCache)) Add(ix.Consumers, k, sig);
                    }
                    ix.Members++;
                }

                foreach (var p in t.Properties)
                {
                    if (p.HasParameters) continue; // indexer
                    if (p.Name.Contains("k__BackingField")) continue; // interop dup of the real property
                    bool g = p.GetMethod != null, s = p.SetMethod != null;
                    bool st = (p.GetMethod ?? p.SetMethod)?.IsStatic == true;
                    string acc = "{" + (g ? "get;" : "") + (s ? "set;" : "") + "}";
                    string sig = $"{decl}.{p.Name} : {Naming.CleanTypeName(p.PropertyType)}  [prop {acc}{(st ? " static" : "")}]";
                    foreach (var k in Rollup(p.PropertyType, rollupCache)) Add(ix.ProducersHeld, k, sig);
                }

                foreach (var f in t.Fields)
                {
                    if (IsPlumbingField(f, t)) continue;
                    string sig = $"{decl}.{f.Name} : {Naming.CleanTypeName(f.FieldType)}  [field{(f.IsStatic ? " static" : "")}]";
                    foreach (var k in Rollup(f.FieldType, rollupCache)) Add(ix.ProducersHeld, k, sig);
                }
            }

        // simple-name -> full keys, for `TransitPoint` (no namespace) lookups
        foreach (var key in ix.AllKeys())
            Add(ix.BySimple, Naming.SimpleName(key), key);
        foreach (var kv in ix.BySimple.ToList())
            ix.BySimple[kv.Key] = kv.Value.Distinct(StringComparer.Ordinal).ToList();

        return ix;
    }

    // The set of keys a reference is filed under: the type itself, its base-class chain, and (for closed
    // generics/arrays) the element/argument types — minus universal roots.
    private static string[] Rollup(TypeReference t, Dictionary<string, string[]> cache)
    {
        if (t == null) return Array.Empty<string>();
        string fn = t.FullName;
        if (cache.TryGetValue(fn, out var hit)) return hit;
        var keys = new List<string>();
        Collect(t, keys, 0);
        var arr = keys.Where(k => !string.IsNullOrEmpty(k) && !UniversalRoots.Contains(k))
                      .Distinct(StringComparer.Ordinal).ToArray();
        cache[fn] = arr;
        return arr;
    }

    private static void Collect(TypeReference t, List<string> keys, int depth)
    {
        if (t == null || depth > 16) return;
        if (t.IsByReference) { Collect(((ByReferenceType)t).ElementType, keys, depth); return; }
        if (t.IsArray) { Collect(((ArrayType)t).ElementType, keys, depth); return; }
        if (t is GenericInstanceType git)
        {
            keys.Add(Naming.CleanTypeName(git));                       // the closed generic, e.g. List<TransitPoint>
            foreach (var ga in git.GenericArguments) Collect(ga, keys, depth + 1); // and each argument
            return;
        }
        keys.Add(Naming.CleanTypeName(t));
        TypeDefinition? td = null;
        try { td = t.Resolve(); } catch { }
        if (td?.BaseType != null) Collect(td.BaseType, keys, depth + 1);
    }

    // Il2CppInterop adds a `.ctor(IntPtr)` wrapper to every type — that's plumbing (wrap an existing
    // pointer), not a game constructor; the real game ctors are projected with their real signatures.
    private static bool IsPlumbingCtor(MethodDefinition m) =>
        m.Parameters.Count == 1 && m.Parameters[0].ParameterType.FullName == "System.IntPtr";

    // Skip interop plumbing fields (NativeFieldInfoPtr_/NativeMethodInfoPtr_/NativeClassPtr cache fields),
    // property backing fields (the property covers them) and enum literals (self-typed noise).
    private static bool IsPlumbingField(FieldDefinition f, TypeDefinition t)
    {
        if (t.IsEnum) return true;
        string n = f.Name;
        return n.StartsWith("NativeFieldInfoPtr_", StringComparison.Ordinal)
            || n.StartsWith("NativeMethodInfoPtr_", StringComparison.Ordinal)
            || n == "NativeClassPtr" || n == "StaticFields"
            || n.Contains("k__BackingField");
    }

    // Il2CppInterop sanitises C# compiler-generated names (<>c__DisplayClass -> __c__DisplayClass,
    // <Foo>d__38 -> _Foo_d__38). Their members are pure noise in a discovery index — skip the whole type.
    private static bool IsCompilerGenerated(TypeReference t)
    {
        for (var cur = t; cur != null; cur = cur.DeclaringType)
        {
            string n = cur.Name;
            if (n.IndexOf("__DisplayClass", StringComparison.Ordinal) >= 0) return true;
            if (n.IndexOf("_d__", StringComparison.Ordinal) >= 0) return true;     // iterator / async state machine
            if (n == "__c" || n.StartsWith("__c__", StringComparison.Ordinal)) return true; // cached anon-delegate holder
            if (n.IndexOf('<') >= 0 || n.IndexOf('>') >= 0) return true;           // any un-sanitised compiler name
        }
        return false;
    }

    private static string RenderParams(MethodDefinition m) =>
        string.Join(", ", m.Parameters.Select(p =>
        {
            string ty = Naming.CleanTypeName(p.ParameterType);
            return string.IsNullOrEmpty(p.Name) ? ty : ty + " " + p.Name;
        }));

    private static void Add(Dictionary<string, List<string>> map, string key, string val)
    {
        if (!map.TryGetValue(key, out var list)) map[key] = list = new List<string>();
        list.Add(val);
    }

    // ---- query -------------------------------------------------------------------------------------

    public struct QueryResult { public string Text; public int Ctors, Prod, Cons; public string? Resolved, Status; }

    public static QueryResult Query(Index ix, string query, int max)
    {
        string? key = Resolve(ix, query, out var candidates, out string status);
        if (status == "ambiguous")
        {
            var sb0 = new StringBuilder($"'{query}' is ambiguous — {candidates!.Count} types share that name:\n");
            foreach (var c in candidates.Take(40)) sb0.Append("  ").Append(c).Append('\n');
            if (candidates.Count > 40) sb0.Append("  ... +").Append(candidates.Count - 40).Append(" more\n");
            return new QueryResult { Text = sb0.ToString(), Status = "ambiguous" };
        }
        if (key == null)
            return new QueryResult { Text = $"no type matching '{query}' in the reverse index", Status = "notfound" };

        var ctors = Get(ix.Ctors, key);
        var pr = Get(ix.ProducersReturn, key);
        var ph = Get(ix.ProducersHeld, key);
        var co = Get(ix.Consumers, key);

        var sb = new StringBuilder();
        sb.Append(key).Append('\n');
        Section(sb, "ctors", ctors, max);
        Section(sb, "producers (return)", pr, max);
        Section(sb, "producers (held by)", ph, max);
        Section(sb, "consumers (param)", co, max);

        return new QueryResult
        {
            Text = sb.ToString(),
            Ctors = Distinct(ctors), Prod = Distinct(pr) + Distinct(ph), Cons = Distinct(co),
            Resolved = key, Status = "ok",
        };
    }

    private static List<string> Get(Dictionary<string, List<string>> map, string key) =>
        map.TryGetValue(key, out var l) ? l : new List<string>();

    private static int Distinct(List<string> l) => l.Distinct(StringComparer.Ordinal).Count();

    private static string? Resolve(Index ix, string q, out List<string>? candidates, out string status)
    {
        candidates = null; status = "ok";
        if (ix.Has(q)) return q;                                   // exact clean full name
        if (ix.BySimple.TryGetValue(q, out var ks))               // a simple name like "TransitPoint"
        {
            if (ks.Count == 1) return ks[0];
            candidates = ks.OrderBy(x => x, StringComparer.Ordinal).ToList();
            status = "ambiguous";
            return null;
        }
        status = "notfound";
        return null;
    }

    // ---- per-type declared surface (the `methods` verb) ----------------------------------------------

    public struct SurfaceResult { public string Text; public string? Resolved; public string Status; public int Methods; }

    // The DECLARED member surface of ONE type — the exact names + parameter types a Hook<T> method must
    // spell to bind. This is the question every hook starts with ("what is ApplyDamage's exact signature?"),
    // which used to fall between `query` (type-level: ctors/producers/consumers) and the live REPL
    // (instance-level: needs the game up and an object in hand) — answered by grepping reverse-index.txt.
    // Declared-only by design: HookMatch binds against the proxy's own declared methods (including mangled
    // explicit-interface impls and get_/set_ accessors, listed under their REAL, hookable names); inherited
    // members belong to the base type, whose chain is printed so the next query is a copy-paste away.
    public static SurfaceResult TypeSurface(List<ModuleDefinition> modules, string query, int max)
    {
        var matches = new List<TypeDefinition>();
        void Scan(Func<TypeDefinition, string, bool> hit)
        {
            foreach (var mod in modules)
                foreach (var t in Naming.AllTypes(mod))
                {
                    if (IsCompilerGenerated(t)) continue;
                    if (hit(t, Naming.CleanTypeName(t))) matches.Add(t);
                }
        }
        Scan((t, clean) => clean == query || t.FullName == query);
        if (matches.Count == 0) Scan((t, clean) => Naming.SimpleName(clean) == query);

        var distinct = matches.GroupBy(t => Naming.CleanTypeName(t), StringComparer.Ordinal)
                              .Select(g => g.First()).ToList();
        if (distinct.Count == 0) return new SurfaceResult { Text = "", Status = "notfound" };
        if (distinct.Count > 1)
        {
            var amb = new StringBuilder("ambiguous — candidates (re-query with the full name):\n");
            foreach (string c in distinct.Select(Naming.CleanTypeName).OrderBy(x => x, StringComparer.Ordinal))
                amb.Append("  ").Append(c).Append('\n');
            return new SurfaceResult { Text = amb.ToString(), Status = "ambiguous" };
        }

        TypeDefinition td = distinct[0];
        string decl = Naming.CleanTypeName(td);
        var sb = new StringBuilder();
        sb.Append("== ").Append(decl).Append("  (").Append(td.Module.Assembly.Name.Name).Append(") ==\n");
        var bases = new List<string>();
        try { for (TypeReference? b = td.BaseType; b != null; b = b.Resolve()?.BaseType) bases.Add(Naming.CleanTypeName(b)); }
        catch { /* an unresolvable base ends the chain — print what we have */ }
        if (bases.Count > 0)
            sb.Append("   base chain (inherited members live there — query each): ")
              .Append(string.Join(" -> ", bases)).Append('\n');

        var ctors = new List<string>();
        var methods = new List<string>();
        foreach (var m in td.Methods)
        {
            if (m.Name == ".cctor") continue;
            if (m.IsConstructor) { if (!IsPlumbingCtor(m)) ctors.Add($"new {decl}({RenderParams(m)})"); continue; }
            string tags = "";
            if (m.IsStatic) tags += "  [static]";
            if (m.IsGetter || m.IsSetter) tags += "  [accessor]";
            else if (m.IsAddOn || m.IsRemoveOn || m.IsFire) tags += "  [event]";
            else if (m.Name.StartsWith("op_", StringComparison.Ordinal)) tags += "  [operator]";
            if (m.IsAbstract) tags += "  [abstract]"; else if (m.IsVirtual) tags += "  [virtual]";
            string ret = m.ReturnType is null || m.ReturnType.FullName == "System.Void"
                ? "" : $" : {Naming.CleanTypeName(m.ReturnType)}";
            methods.Add($"{m.Name}({RenderParams(m)}){ret}{tags}");
        }
        Section(sb, "declared methods (hook spelling: name + parameter types)", methods, max);
        Section(sb, "ctors", ctors, max);
        return new SurfaceResult { Text = sb.ToString(), Status = "ok", Resolved = decl, Methods = methods.Count };
    }

    private static void Section(StringBuilder sb, string title, List<string> items, int max)
    {
        var d = items.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList();
        sb.Append("== ").Append(title).Append(" (").Append(d.Count).Append(") ==\n");
        if (d.Count == 0) { sb.Append("  (none)\n"); return; }
        foreach (var s in d.Take(max)) sb.Append("  ").Append(s).Append('\n');
        if (d.Count > max) sb.Append("  ... +").Append(d.Count - max).Append(" more (raise OT_QUERY_MAX)\n");
    }

    // ---- full grep catalog -------------------------------------------------------------------------

    // One line per (type, entry): K=ctor  P=produced-by-return  H=held-by  C=consumed-by. Sorted by type
    // then kind, so `grep '<TypeName>' reverse-index.txt` returns the whole reverse picture for a type.
    public static void DumpAll(Index ix, string path, int capPerKind = 1000)
    {
        var sb = new StringBuilder();
        sb.Append("# inutil reverse-discovery index — static, build-wide (interop proxies).\n");
        sb.Append("# K <type>  <ctor>            how to construct it (or a subtype)\n");
        sb.Append("# P <type>  <method:return>   what RETURNS it\n");
        sb.Append("# H <type>  <member>          what HOLDS it (field/prop)\n");
        sb.Append("# C <type>  <method>          what TAKES it as a parameter\n");
        sb.Append($"# {ix.Types:N0} types, {ix.Members:N0} members, {ix.KeyCount:N0} keys.\n\n");

        foreach (var key in ix.AllKeys().OrderBy(x => x, StringComparer.Ordinal))
        {
            Lines(sb, 'K', key, Get(ix.Ctors, key), capPerKind);
            Lines(sb, 'P', key, Get(ix.ProducersReturn, key), capPerKind);
            Lines(sb, 'H', key, Get(ix.ProducersHeld, key), capPerKind);
            Lines(sb, 'C', key, Get(ix.Consumers, key), capPerKind);
        }
        File.WriteAllText(path, sb.ToString());
    }

    private static void Lines(StringBuilder sb, char kind, string key, List<string> items, int cap)
    {
        var d = items.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList();
        foreach (var s in d.Take(cap)) sb.Append(kind).Append(' ').Append(key).Append("  ").Append(s).Append('\n');
        if (d.Count > cap) sb.Append(kind).Append(' ').Append(key).Append("  ... +").Append(d.Count - cap).Append(" more\n");
    }
}

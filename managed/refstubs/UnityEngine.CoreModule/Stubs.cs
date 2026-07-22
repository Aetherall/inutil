// UnityEngine.CoreModule ref-stub — the Unity-core types the inutil BepInEx host plugin's pump references,
// declared with Il2CppInterop's generated shapes (the inheritance chain, ctor set, and HideFlags values all
// matter — the compiler inlines HideAndDontSave=61 into IL), so the plugin builds game-free and binds the real
// UnityEngine.CoreModule at runtime. Bodies never run; `throw null` = the ref-assembly idiom.
using System;

namespace UnityEngine
{
    public class Object : Il2CppSystem.Object
    {
        public Object() : base(IntPtr.Zero) { }
        public Object(IntPtr pointer) : base(pointer) { }
        public HideFlags hideFlags { get => throw null; set => throw null; }
        public static void DontDestroyOnLoad(Object target) => throw null;
    }

    public class Component : Object
    {
        public Component() : base(IntPtr.Zero) { }
        public Component(IntPtr pointer) : base(pointer) { }
    }

    public class Behaviour : Component
    {
        public Behaviour() : base(IntPtr.Zero) { }
        public Behaviour(IntPtr pointer) : base(pointer) { }
    }

    public class MonoBehaviour : Behaviour
    {
        public MonoBehaviour() : base(IntPtr.Zero) { }
        public MonoBehaviour(IntPtr pointer) : base(pointer) { }
    }

    public class GameObject : Object
    {
        public GameObject() : base(IntPtr.Zero) { }
        public GameObject(IntPtr pointer) : base(pointer) { }
        public T AddComponent<T>() where T : Component => throw null;
    }

    // Exact Unity values — the compiler inlines HideAndDontSave (61) into the plugin's IL, so it must match.
    public enum HideFlags
    {
        None = 0,
        HideInHierarchy = 1,
        HideInInspector = 2,
        DontSaveInEditor = 4,
        NotEditable = 8,
        DontSaveInBuild = 16,
        DontUnloadUnusedAsset = 32,
        DontSave = 52,
        HideAndDontSave = 61,
    }
}

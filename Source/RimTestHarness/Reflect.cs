using System;
using System.Linq.Expressions;
using System.Reflection;
using HarmonyLib;

namespace RimTestHarness
{
    /// <summary>Allocation-free member access to types resolved by name (Combat Extended, AMC) via compiled expression trees.</summary>
    public static class Reflect
    {
        public static Type T(string fullName) => AccessTools.TypeByName(fullName);

        public static MemberInfo Member(Type type, string name)
        {
            for (var t = type; t != null; t = t.BaseType)
            {
                var f = t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (f != null) return f;
                var p = t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (p != null) return p;
            }
            return null;
        }

        /// <summary>Func&lt;object, TR&gt; reading a field or property (virtual dispatch for properties); throws if missing.</summary>
        public static Func<object, TR> Getter<TR>(Type type, string name)
        {
            var m = Member(type, name) ?? throw new MissingMemberException(type.FullName, name);
            var p = Expression.Parameter(typeof(object), "o");
            Expression body = Expression.MakeMemberAccess(Expression.Convert(p, m.DeclaringType), m);
            if (body.Type != typeof(TR)) body = Expression.Convert(body, typeof(TR));
            return Expression.Lambda<Func<object, TR>>(body, p).Compile();
        }

        public static Func<object, TR> GetterOrNull<TR>(Type type, string name)
        {
            try { return type == null ? null : Getter<TR>(type, name); } catch { return null; }
        }

        public static Action<object, TV> Setter<TV>(Type type, string name)
        {
            var m = Member(type, name) ?? throw new MissingMemberException(type.FullName, name);
            var p = Expression.Parameter(typeof(object), "o");
            var v = Expression.Parameter(typeof(TV), "v");
            var target = Expression.MakeMemberAccess(Expression.Convert(p, m.DeclaringType), m);
            Expression value = v;
            if (target.Type != typeof(TV)) value = Expression.Convert(v, target.Type);
            return Expression.Lambda<Action<object, TV>>(Expression.Assign(target, value), p, v).Compile();
        }

        public static MethodInfo Method(Type type, string name, params Type[] args)
            => args == null || args.Length == 0 ? AccessTools.Method(type, name) : AccessTools.Method(type, name, args);

        public static object Get(object instance, string name)
        {
            var m = Member(instance.GetType(), name) ?? throw new MissingMemberException(instance.GetType().FullName, name);
            return m is FieldInfo f ? f.GetValue(instance) : ((PropertyInfo)m).GetValue(instance);
        }

        public static void Set(object instance, string name, object value)
        {
            var m = Member(instance.GetType(), name) ?? throw new MissingMemberException(instance.GetType().FullName, name);
            if (m is FieldInfo f) f.SetValue(instance, value); else ((PropertyInfo)m).SetValue(instance, value);
        }

        public static object Call(object instance, string name, params object[] args)
        {
            var mi = FindMethod(instance.GetType(), name, args.Length) ?? throw new MissingMethodException(instance.GetType().FullName, name);
            var ps = mi.GetParameters();
            var full = new object[ps.Length];
            for (int i = 0; i < ps.Length; i++) full[i] = i < args.Length ? args[i] : (ps[i].HasDefaultValue ? ps[i].DefaultValue : null);
            return mi.Invoke(instance, full);
        }

        static MethodInfo FindMethod(Type type, string name, int minArgs)
        {
            for (var t = type; t != null; t = t.BaseType)
            {
                foreach (var m in t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (m.Name == name && m.GetParameters().Length >= minArgs) return m;
                }
            }
            return null;
        }
    }
}

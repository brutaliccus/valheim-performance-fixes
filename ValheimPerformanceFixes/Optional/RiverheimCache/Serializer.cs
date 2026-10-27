using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;

namespace RiverheimCache {
// Reflection object-graph serializer. Breadth-first (no deep recursion), reference-preserving, rejects anything it
// cannot faithfully reproduce (delegates, pointers, unknown BCL/Unity types) by throwing -> caller falls back.
public static class GraphSerializer {
  sealed class RefEq : IEqualityComparer<object> { public new bool Equals(object a, object b) => ReferenceEquals(a, b); public int GetHashCode(object o) => RuntimeHelpers.GetHashCode(o); }
  static readonly Dictionary<Type, FieldInfo[]> fieldCache = new Dictionary<Type, FieldInfo[]>();
  static readonly HashSet<string> okBclGeneric = new HashSet<string> { "System.Collections.Generic.List`1", "System.Collections.Generic.Dictionary`2", "System.Collections.Generic.HashSet`1", "System.Nullable`1", "System.Collections.Generic.KeyValuePair`2", "System.ValueTuple`1", "System.ValueTuple`2", "System.ValueTuple`3", "System.ValueTuple`4", "System.ValueTuple`5", "System.ValueTuple`6", "System.ValueTuple`7" };

  static FieldInfo[] Fields(Type t) {
    lock (fieldCache) {
      if (fieldCache.TryGetValue(t, out var f)) return f;
      var list = new List<FieldInfo>();
      for (var c = t; c != null && c != typeof(object) && c != typeof(ValueType); c = c.BaseType)
        list.InsertRange(0, c.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly).OrderBy(x => x.MetadataToken));
      f = list.ToArray(); fieldCache[t] = f; return f;
    }
  }
  static bool IsDict(Type t) => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Dictionary<,>);
  static bool IsSet(Type t) => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(HashSet<>);

  static void Check(Type t) {
    if (t.IsPointer || t.IsByRef || t == typeof(IntPtr) || t == typeof(UIntPtr) || typeof(Type).IsAssignableFrom(t) || typeof(MemberInfo).IsAssignableFrom(t))
      throw new NotSupportedException("unsupported type " + t);
    if (t.IsArray) { Check(t.GetElementType()); return; }
    if (t.IsGenericType) { foreach (var a in t.GetGenericArguments()) Check(a); }
    var asm = t.Assembly; string an = asm.GetName().Name;
    if (asm == typeof(object).Assembly || an == "System" || an == "System.Core" || an == "netstandard") {
      if (t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(object) || t == typeof(decimal)) return;
      if (t.IsGenericType && okBclGeneric.Contains(t.GetGenericTypeDefinition().FullName)) return;
      throw new NotSupportedException("unsupported BCL type " + t);
    }
    if (typeof(UnityEngine.Object).IsAssignableFrom(t)) throw new NotSupportedException("unity object " + t);
  }

  // ---------------- writer ----------------
  public static byte[] Serialize(object root) {
    var ms = new MemoryStream(); var w = new BinaryWriter(ms);
    var ids = new Dictionary<object, int>(new RefEq()); var queue = new Queue<object>();
    var types = new Dictionary<Type, int>();
    void WType(Type t) {
      if (types.TryGetValue(t, out int id)) { w.Write(id); return; }
      Check(t); id = types.Count; types[t] = id; w.Write(id);
      if (t.IsArray) { w.Write((byte)2); WType(t.GetElementType()); int rank = t.GetArrayRank(); bool sz = t == t.GetElementType().MakeArrayType(); w.Write(sz ? 0 : rank); }
      else if (t.IsGenericType && !t.IsGenericTypeDefinition) { w.Write((byte)1); WType(t.GetGenericTypeDefinition()); var ga = t.GetGenericArguments(); w.Write(ga.Length); foreach (var a in ga) WType(a); }
      else { w.Write((byte)0); w.Write(t.Assembly.GetName().Name); w.Write(t.FullName); }
    }
    void WRef(object o) {
      if (o == null) { w.Write(-1); return; }
      if (ids.TryGetValue(o, out int id)) { w.Write(id); return; }
      id = ids.Count; ids[o] = id; w.Write(id);
      var t = o.GetType();
      if (t.IsPrimitive || t.IsEnum) throw new NotSupportedException("boxed primitive " + t);
      WType(t);
      if (o is string s) { w.Write(s); return; }
      if (o is Delegate dg) {
        if (dg.GetInvocationList().Length != 1) throw new NotSupportedException("multicast delegate");
        var mi = dg.Method; var tgt = dg.Target; byte kind;
        if (tgt == null) kind = 0;
        else { var sf = tgt.GetType().GetField("<>9", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic); if (sf != null && ReferenceEquals(sf.GetValue(null), tgt)) kind = 1; else throw new NotSupportedException("delegate with captured target " + mi); }
        if (mi.DeclaringType.Assembly == typeof(object).Assembly) throw new NotSupportedException("BCL delegate " + mi);
        w.Write(kind); WType(mi.DeclaringType); w.Write(mi.MetadataToken); return;
      }
      if (o is Array a) { w.Write(a.Rank); for (int i = 0; i < a.Rank; i++) w.Write(a.GetLength(i)); }
      if (IsDict(t) || IsSet(t)) {
        var cmp = t.GetProperty("Comparer").GetValue(o);
        var def = typeof(EqualityComparer<>).MakeGenericType(t.GetGenericArguments()[0]).GetProperty("Default").GetValue(null);
        if (cmp.GetType() != def.GetType()) throw new NotSupportedException("custom comparer on " + t);
      }
      queue.Enqueue(o);
    }
    void WVal(Type t, object v) {
      if (t.IsEnum) t = Enum.GetUnderlyingType(t);
      if (t.IsPrimitive) { WPrim(w, t, v); return; }
      if (t == typeof(decimal)) { w.Write((decimal)v); return; }
      if (t.IsValueType) { Check(t); foreach (var f in Fields(t)) WVal(f.FieldType, f.GetValue(v)); return; }
      WRef(v);
    }
    WRef(root);
    while (queue.Count > 0) {
      var o = queue.Dequeue(); var t = o.GetType();
      if (o is Array a) {
        var et = t.GetElementType();
        if (et.IsPrimitive) { var buf = new byte[Buffer.ByteLength(a)]; Buffer.BlockCopy(a, 0, buf, 0, buf.Length); w.Write(buf); }
        else { var idx = new int[a.Rank]; int n = a.Length; for (int k = 0; k < n; k++) { Unflat(a, k, idx); WVal(et, a.GetValue(idx)); } }
      } else if (IsDict(t)) {
        var ga = t.GetGenericArguments(); var d = (IDictionary)o; w.Write(d.Count);
        foreach (DictionaryEntry e in d) { WVal(ga[0], e.Key); WVal(ga[1], e.Value); }
      } else if (IsSet(t)) {
        var ga = t.GetGenericArguments(); var items = ((IEnumerable)o).Cast<object>().ToList(); w.Write(items.Count);
        foreach (var e in items) WVal(ga[0], e);
      } else {
        foreach (var f in Fields(t)) WVal(f.FieldType, f.GetValue(o));
      }
    }
    w.Flush(); return ms.ToArray();
  }

  static void Unflat(Array a, int k, int[] idx) { for (int d = a.Rank - 1; d >= 0; d--) { int len = a.GetLength(d); idx[d] = k % len; k /= len; } }

  static void WPrim(BinaryWriter w, Type t, object v) {
    switch (Type.GetTypeCode(t)) {
      case TypeCode.Boolean: w.Write((bool)v); break; case TypeCode.Char: w.Write((ushort)(char)v); break;
      case TypeCode.SByte: w.Write((sbyte)v); break; case TypeCode.Byte: w.Write((byte)v); break;
      case TypeCode.Int16: w.Write((short)v); break; case TypeCode.UInt16: w.Write((ushort)v); break;
      case TypeCode.Int32: w.Write((int)v); break; case TypeCode.UInt32: w.Write((uint)v); break;
      case TypeCode.Int64: w.Write((long)v); break; case TypeCode.UInt64: w.Write((ulong)v); break;
      case TypeCode.Single: w.Write((float)v); break; case TypeCode.Double: w.Write((double)v); break;
      default: throw new NotSupportedException("primitive " + t);
    }
  }
  static object RPrim(BinaryReader r, Type t) {
    switch (Type.GetTypeCode(t)) {
      case TypeCode.Boolean: return r.ReadBoolean(); case TypeCode.Char: return (char)r.ReadUInt16();
      case TypeCode.SByte: return r.ReadSByte(); case TypeCode.Byte: return r.ReadByte();
      case TypeCode.Int16: return r.ReadInt16(); case TypeCode.UInt16: return r.ReadUInt16();
      case TypeCode.Int32: return r.ReadInt32(); case TypeCode.UInt32: return r.ReadUInt32();
      case TypeCode.Int64: return r.ReadInt64(); case TypeCode.UInt64: return r.ReadUInt64();
      case TypeCode.Single: return r.ReadSingle(); case TypeCode.Double: return r.ReadDouble();
      default: throw new NotSupportedException("primitive " + t);
    }
  }

  // ---------------- reader ----------------
  public static object Deserialize(byte[] data) {
    var r = new BinaryReader(new MemoryStream(data, false));
    var objs = new List<object>(); var queue = new Queue<object>(); var types = new List<Type>();
    var pendingAdds = new List<Action>();
    Type RType() {
      int id = r.ReadInt32(); if (id < types.Count) return types[id];
      if (id != types.Count) throw new InvalidDataException("type id order");
      types.Add(null); Type t; byte kind = r.ReadByte();
      if (kind == 2) { var et = RType(); int rank = r.ReadInt32(); t = rank == 0 ? et.MakeArrayType() : et.MakeArrayType(rank); }
      else if (kind == 1) { var def = RType(); int n = r.ReadInt32(); var ga = new Type[n]; for (int i = 0; i < n; i++) ga[i] = RType(); t = def.MakeGenericType(ga); }
      else { string an = r.ReadString(), fn = r.ReadString(); var asm = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(x => x.GetName().Name == an) ?? throw new InvalidDataException("assembly " + an); t = asm.GetType(fn, true); }
      Check(t); types[id] = t; return t;
    }
    object RRef() {
      int id = r.ReadInt32(); if (id == -1) return null;
      if (id < objs.Count) return objs[id];
      if (id != objs.Count) throw new InvalidDataException("object id order");
      var t = RType(); object o;
      if (t == typeof(string)) { o = r.ReadString(); objs.Add(o); return o; }
      if (typeof(Delegate).IsAssignableFrom(t)) {
        byte kind = r.ReadByte(); var dt = RType(); int tok = r.ReadInt32();
        var mi = (MethodInfo)dt.Module.ResolveMethod(tok, dt.IsGenericType ? dt.GetGenericArguments() : null, null);
        if (dt.IsGenericType) mi = (MethodInfo)MethodBase.GetMethodFromHandle(mi.MethodHandle, dt.TypeHandle);
        object tgt = kind == 0 ? null : dt.GetField("<>9", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).GetValue(null);
        o = tgt == null ? Delegate.CreateDelegate(t, mi) : Delegate.CreateDelegate(t, tgt, mi); objs.Add(o); return o;
      }
      if (t.IsArray) { int rank = r.ReadInt32(); var lens = new int[rank]; for (int i = 0; i < rank; i++) lens[i] = r.ReadInt32(); o = Array.CreateInstance(t.GetElementType(), lens); }
      else if (IsDict(t) || IsSet(t)) o = Activator.CreateInstance(t);
      else o = FormatterServices.GetUninitializedObject(t);
      objs.Add(o); queue.Enqueue(o); return o;
    }
    object RVal(Type t) {
      if (t.IsEnum) return Enum.ToObject(t, RPrim(r, Enum.GetUnderlyingType(t)));
      if (t.IsPrimitive) return RPrim(r, t);
      if (t == typeof(decimal)) return r.ReadDecimal();
      if (t.IsValueType) { var box = FormatterServices.GetUninitializedObject(t); foreach (var f in Fields(t)) f.SetValue(box, RVal(f.FieldType)); return box; }
      return RRef();
    }
    var root = RRef();
    while (queue.Count > 0) {
      var o = queue.Dequeue(); var t = o.GetType();
      if (o is Array a) {
        var et = t.GetElementType();
        if (et.IsPrimitive) { int n = Buffer.ByteLength(a); var buf = r.ReadBytes(n); if (buf.Length != n) throw new EndOfStreamException(); Buffer.BlockCopy(buf, 0, a, 0, n); }
        else { var idx = new int[a.Rank]; int n = a.Length; for (int k = 0; k < n; k++) { Unflat(a, k, idx); a.SetValue(RVal(et), idx); } }
      } else if (IsDict(t)) {
        var ga = t.GetGenericArguments(); int n = r.ReadInt32(); var d = (IDictionary)o; var kv = new List<(object, object)>(n);
        for (int i = 0; i < n; i++) { var k = RVal(ga[0]); var v = RVal(ga[1]); kv.Add((k, v)); }
        pendingAdds.Add(() => { foreach (var (k, v) in kv) d.Add(k, v); });
      } else if (IsSet(t)) {
        var ga = t.GetGenericArguments(); int n = r.ReadInt32(); var items = new List<object>(n); for (int i = 0; i < n; i++) items.Add(RVal(ga[0]));
        var add = t.GetMethod("Add"); pendingAdds.Add(() => { foreach (var e in items) add.Invoke(o, new[] { e }); });
      } else {
        foreach (var f in Fields(t)) f.SetValue(o, RVal(f.FieldType));
      }
    }
    foreach (var p in pendingAdds) p();
    if (r.BaseStream.Position != r.BaseStream.Length) throw new InvalidDataException("trailing data");
    return root;
  }
}
}

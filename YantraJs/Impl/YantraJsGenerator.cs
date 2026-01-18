using System.Reflection;
using System.Runtime.CompilerServices;

namespace YantraJs.Impl;

internal static class YantraJsGenerator
{
    public static T? CloneObject<T>(T? obj)
    {
        if (obj is null)
        {
            return default;
        }
        
        Type concreteTypeOfObj = obj.GetType();
        Type typeOfT = typeof(T);
        
        if (YantraJsCache.AlwaysIgnoredTypes.ContainsKey(concreteTypeOfObj))
        {
            return default;
        }
        
        if (YantraJsSafeTypes.SafeDefKnownTypes.TryGetValue(concreteTypeOfObj, out _))
        {
            return obj;
        }
        
        switch (obj)
        {
            case ValueType:
            {
                Type type = obj.GetType();
                
                if (typeOfT == type)
                {
                    bool hasIgnoredMembers = YantraJsCache.GetOrAddTypeContainsIgnoredMembers(type, YantraJsExprGen.CalcTypeContainsIgnoredMembers);
                    
                    if (hasIgnoredMembers || !YantraJsSafeTypes.CanReturnSameObject(type))
                    {
                        return CloneStructInternal(obj, new YantraJsState());
                    }
                    
                    return obj;
                }

                break;
            }
            case Delegate del:
            {
                Type? targetType = del.Target?.GetType();
            
                if (targetType?.GetCustomAttribute<CompilerGeneratedAttribute>() is not null)
                {
                    return (T?)CloneClassRoot(obj);
                }
            
                return obj;
            }
        }

        return (T?)CloneClassRoot(obj);
    }

    private static object? CloneClassRoot(object? obj)
    {
        if (obj == null)
            return null;

        Func<object, YantraJsState, object>? cloner = (Func<object, YantraJsState, object>?)YantraJsCache.GetOrAddClass(obj.GetType(), t => GenerateCloner(t, true));
        return cloner is null ? obj : cloner(obj, new YantraJsState());
    }

    internal static object? CloneClassInternal(object? obj, YantraJsState jsState)
    {
        if (obj is null)
        {
            return null;
        }
        
        Type objType = obj.GetType();
        
        if (YantraJsCache.IsTypeIgnored(objType))
        {
            return null;
        }

        // Fast-path: if already cloned -> return cached clone
        object? knownRef = jsState.GetKnownRef(obj);
        if (knownRef is not null)
        {
            return knownRef;
        }

        // If there is no cloner (type considered safe to return as-is), return original object
        Func<object, YantraJsState, object>? cloner = (Func<object, YantraJsState, object>?)YantraJsCache.GetOrAddClass(objType, t => GenerateCloner(t, true));
        if (cloner is null)
        {
            return obj;
        }

        if (ShouldUseGeneratedCloner(objType))
        {
            // delegate to generated cloner (preserves special logic and avoids problematic getters)
            return cloner(obj, jsState);
        }

        // Iterative deep clone for user-defined types to avoid stack overflow on long chains / cycles.
        object? rootShallow = CloneClassShallowAndTrack(obj, jsState);
        if (rootShallow is null)
        {
            return null;
        }

        var stack = new Stack<(object From, object To, Type Type)>();
        stack.Push((obj, rootShallow, objType));

        while (stack.Count > 0)
        {
            var (fromObj, toObj, type) = stack.Pop();

            Type? current = type;
            while (current != null && current != typeof(ContextBoundObject))
            {
                foreach (FieldInfo field in current.GetDeclaredFields())
                {
                    ProcessField(field, fromObj, toObj, jsState, stack);
                }

                current = current.BaseType;
            }
        }

        return rootShallow;
    }

    private static bool ShouldUseGeneratedCloner(Type objType)
    {
        if (RequiresSpecializedCloner(objType))
        {
            return true;
        }

        string? ns = objType.Namespace;
        if (ns != null && (ns.StartsWith("System") || ns.StartsWith("Microsoft")))
        {
            return true;
        }

        string asmName = objType.Assembly.FullName ?? string.Empty;
        if (asmName.IndexOf("Newtonsoft", StringComparison.OrdinalIgnoreCase) >= 0 ||
            asmName.IndexOf("NHibernate", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return true;
        }

        string tName = objType.Name;
        if (tName.IndexOf("Immutable", StringComparison.OrdinalIgnoreCase) >= 0 ||
            tName.IndexOf("Concurrent", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return true;
        }

        return objType.GetInterfaces().Any(i => i.IsGenericType && (
            i.GetGenericTypeDefinition() == typeof(IDictionary<,>) ||
            i.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>) ||
            i.GetGenericTypeDefinition() == typeof(ISet<>) ||
            i.GetGenericTypeDefinition() == typeof(IEnumerable<>)
        ));
    }

    private static void ProcessField(FieldInfo field, object fromObj, object toObj, YantraJsState jsState, Stack<(object From, object To, Type Type)> stack)
    {
        Type fieldType = field.FieldType;

        // Delegate-typed fields: handle event backing fields differently than other delegates.
        if (typeof(Delegate).IsAssignableFrom(fieldType))
        {
            HandleDelegateField(field, fromObj, toObj);
            return;
        }

        // skip ignored types
        if (YantraJsCache.IsTypeIgnored(fieldType))
        {
            object? defaultVal = fieldType.IsValueType ? Activator.CreateInstance(fieldType) : null;
            if (field.IsInitOnly)
            {
                YantraJsExprGen.ForceSetField(field, toObj, defaultVal!);
            }
            else
            {
                field.SetValue(toObj, defaultVal);
            }

            return;
        }

        // safe to copy reference/value as-is
        if (YantraJsSafeTypes.CanReturnSameObject(fieldType))
        {
            object? val = field.GetValue(fromObj);
            if (field.IsInitOnly)
            {
                YantraJsExprGen.ForceSetField(field, toObj, val!);
            }
            else
            {
                field.SetValue(toObj, val);
            }

            return;
        }

        // value-type field -> use struct cloner
        if (fieldType.IsValueType)
        {
            object? origVal = field.GetValue(fromObj);
            if (origVal is null)
            {
                if (field.IsInitOnly)
                {
                    YantraJsExprGen.ForceSetField(field, toObj, null!);
                }
                else
                {
                    field.SetValue(toObj, null);
                }
            }
            else
            {
                MethodInfo mi = YantraStatic.DeepClonerGeneratorMethods.CloneStructInternal.MakeGenericMethod(fieldType);
                object? clonedVal = mi.Invoke(null, new object?[] { origVal, jsState });
                if (field.IsInitOnly)
                {
                    YantraJsExprGen.ForceSetField(field, toObj, clonedVal!);
                }
                else
                {
                    field.SetValue(toObj, clonedVal);
                }
            }
            return;
        }

        // reference type
        object? fm = field.GetValue(fromObj);
        if (fm is null)
        {
            if (field.IsInitOnly)
            {
                YantraJsExprGen.ForceSetField(field, toObj, null!);
            }
            else
            {
                field.SetValue(toObj, null);
            }

            return;
        }

        object? known = jsState.GetKnownRef(fm);
        if (known is not null)
        {
            if (field.IsInitOnly)
                YantraJsExprGen.ForceSetField(field, toObj, known);
            else
                field.SetValue(toObj, known);
            return;
        }

        // not seen yet: create shallow clone, register and schedule processing
        object? shallow = CloneClassShallowAndTrack(fm, jsState);
        if (field.IsInitOnly)
        {
            YantraJsExprGen.ForceSetField(field, toObj, shallow!);
        }
        else
        {
            field.SetValue(toObj, shallow);
        }

        if (shallow is not null)
        {
            stack.Push((fm, shallow, fm.GetType()));
        }
    }

    private static void HandleDelegateField(FieldInfo field, object fromObj, object toObj)
    {
        Type declaring = field.DeclaringType ?? field.ReflectedType!;
        bool isEventField = IsEventBackingField(declaring, field.Name);
        if (isEventField)
        {
            // clear event backing fields in the clone to avoid preserving subscribers
            if (field.IsInitOnly)
            {
                YantraJsExprGen.ForceSetField(field, toObj, null!);
            }
            else
            {
                field.SetValue(toObj, null);
            }
        }
        else
        {
            // preserve other delegate fields (copy reference) — needed for LazyRef and similar patterns
            object? val = field.GetValue(fromObj);
            if (field.IsInitOnly)
            {
                YantraJsExprGen.ForceSetField(field, toObj, val!);
            }
            else
            {
                field.SetValue(toObj, val);
            }
        }
    }

    private static bool IsEventBackingField(Type declaringType, string fieldName)
    {
        // Match event names to backing fields conservatively:
        // If there is an event with the same name, treat the field as event backing field.
        return declaringType.GetEvents(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                            .Any(e => e.Name == fieldName);
    }

    internal static object? CloneClassShallowAndTrack(object? obj, YantraJsState jsState)
    {
        if (obj is null)
        {
            return null;
        }

        Type objType = obj.GetType();

        if (YantraJsCache.IsTypeIgnored(objType))
        {
            return null;
        }
        
        if (YantraJsSafeTypes.CanReturnSameObject(objType) && !objType.IsValueType())
        {
            return obj;
        }

        object? knownRef = jsState.GetKnownRef(obj);
        if (knownRef is not null)
        {
            return knownRef;
        }
        
        if (RequiresSpecializedCloner(objType))
        {
            Func<object, YantraJsState, object>? specialCloner = (Func<object, YantraJsState, object>?)YantraJsCache.GetOrAddClass(objType, t => GenerateCloner(t, true));
            if (specialCloner is not null)
            {
                return specialCloner(obj, jsState);
            }
        }
        
        MethodInfo methodInfo = typeof(object).GetPrivateMethod(nameof(MemberwiseClone))!;
        object? shallow = methodInfo.Invoke(obj, null);
        jsState.AddKnownRef(obj, shallow);
        return shallow;
    }
    
    private static bool RequiresSpecializedCloner(Type type)
    {
        return type.IsArray || 
               YantraJsExprGen.IsDictType(type) || 
               YantraJsExprGen.IsSetType(type);
    }

    internal static T CloneStructInternal<T>(T obj, YantraJsState jsState)
    {
        Type typeT = typeof(T);
        Type? underlyingTypeT = Nullable.GetUnderlyingType(typeT);
        
        if (YantraJsCache.AlwaysIgnoredTypes.ContainsKey(typeT) || (underlyingTypeT != null && YantraJsCache.AlwaysIgnoredTypes.ContainsKey(underlyingTypeT)))
        {
            return default;
        }
        
        Func<T, YantraJsState, T>? cloner = GetClonerForValueType<T>();
        return cloner is null ? obj : cloner(obj, jsState);
    }
    
    internal static T[] Clone1DimArraySafeInternal<T>(T[] obj, YantraJsState jsState)
    {
        int l = obj.Length;
        T[] outArray = new T[l];
        jsState.AddKnownRef(obj, outArray);
        Array.Copy(obj, outArray, obj.Length);
        return outArray;
    }

    internal static T[]? Clone1DimArrayStructInternal<T>(T[]? obj, YantraJsState jsState)
    {
        if (obj == null) return null;
        int l = obj.Length;
        T[] outArray = new T[l];
        jsState.AddKnownRef(obj, outArray);
        Func<T, YantraJsState, T> cloner = GetClonerForValueType<T>();
        for (int i = 0; i < l; i++)
            outArray[i] = cloner(obj[i], jsState);

        return outArray;
    }

    internal static T[]? Clone1DimArrayClassInternal<T>(T[]? obj, YantraJsState jsState)
    {
        if (obj == null) return null;
        int l = obj.Length;
        T[] outArray = new T[l];
        jsState.AddKnownRef(obj, outArray);
        for (int i = 0; i < l; i++)
            outArray[i] = (T)CloneClassInternal(obj[i], jsState);

        return outArray;
    }
    
    internal static T[,]? Clone2DimArrayInternal<T>(T[,]? obj, YantraJsState jsState)
    {
        if (obj is null)
        {
            return null;
        }
        
        int lb1 = obj.GetLowerBound(0);
        int lb2 = obj.GetLowerBound(1);
        if (lb1 != 0 || lb2 != 0)
            return (T[,]) CloneAbstractArrayInternal(obj, jsState);

        int l1 = obj.GetLength(0);
        int l2 = obj.GetLength(1);
        T[,] outArray = new T[l1, l2];
        jsState.AddKnownRef(obj, outArray);
        if (YantraJsSafeTypes.CanReturnSameObject(typeof(T)))
        {
            Array.Copy(obj, outArray, obj.Length);
            return outArray;
        }

        if (typeof(T).IsValueType())
        {
            Func<T, YantraJsState, T> cloner = GetClonerForValueType<T>();
            for (int i = 0; i < l1; i++)
                for (int k = 0; k < l2; k++)
                    outArray[i, k] = cloner(obj[i, k], jsState);
        }
        else
        {
            for (int i = 0; i < l1; i++)
                for (int k = 0; k < l2; k++)
                    outArray[i, k] = (T)CloneClassInternal(obj[i, k], jsState);
        }

        return outArray;
    }
    
    internal static Array? CloneAbstractArrayInternal(Array? obj, YantraJsState jsState)
    {
        if (obj == null) return null;
        int rank = obj.Rank;

        int[] lengths = Enumerable.Range(0, rank).Select(obj.GetLength).ToArray();

        int[] lowerBounds = Enumerable.Range(0, rank).Select(obj.GetLowerBound).ToArray();
        int[] idxes = Enumerable.Range(0, rank).Select(obj.GetLowerBound).ToArray();

        Type? elementType = obj.GetType().GetElementType();
        Array outArray = Array.CreateInstance(elementType, lengths, lowerBounds);

        jsState.AddKnownRef(obj, outArray);
        
        if (lengths.Any(x => x == 0))
            return outArray;

        if (YantraJsSafeTypes.CanReturnSameObject(elementType))
        {
            Array.Copy(obj, outArray, obj.Length);
            return outArray;
        }

        int ofs = rank - 1;
        while (true)
        {
            outArray.SetValue(CloneClassInternal(obj.GetValue(idxes), jsState), idxes);
            idxes[ofs]++;

            if (idxes[ofs] >= lowerBounds[ofs] + lengths[ofs])
            {
                do
                {
                    if (ofs == 0) return outArray;
                    idxes[ofs] = lowerBounds[ofs];
                    ofs--;
                    idxes[ofs]++;
                } while (idxes[ofs] >= lowerBounds[ofs] + lengths[ofs]);

                ofs = rank - 1;
            }
        }
    }

    internal static Func<T, YantraJsState, T>? GetClonerForValueType<T>() => (Func<T, YantraJsState, T>?)YantraJsCache.GetOrAddStructAsObject(typeof(T), t => GenerateCloner(t, false));

    private static object? GenerateCloner(Type t, bool asObject)
    {
        if (YantraJsSafeTypes.CanReturnSameObject(t) && asObject && !t.IsValueType())
            return null;

        return YantraJsExprGen.GenClonerInternal(t, asObject);
    }

    public static object? CloneObjectTo(object? objFrom, object? objTo, bool isDeep)
    {
        if (objTo == null) return null;

        if (objFrom == null)
            throw new ArgumentNullException(nameof(objFrom), "Cannot copy null");
        Type type = objFrom.GetType();
        if (!type.IsInstanceOfType(objTo))
            throw new InvalidOperationException("From should be derived from From object, but From object has type " + objFrom.GetType().FullName + " and to " + objTo.GetType().FullName);
        if (objFrom is string)
            throw new InvalidOperationException("Forbidden to clone strings");
        Func<object, object, YantraJsState, object>? cloner = (Func<object, object, YantraJsState, object>?)(isDeep
            ? YantraJsCache.GetOrAddDeepClassTo(type, t => YantraExprGen.GenClonerInternal(t, true))
            : YantraJsCache.GetOrAddShallowClassTo(type, t => YantraExprGen.GenClonerInternal(t, false)));
        return cloner is null ? objTo : cloner(objFrom, objTo, new YantraJsState());
    }
}
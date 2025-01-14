﻿namespace DistIL.Util;

using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

public static class CollectionsExt
{
    public static ImmutableArray<T> EmptyIfDefault<T>(this ImmutableArray<T> array)
        => array.IsDefault ? ImmutableArray<T>.Empty : array;

    /// <summary> Gets a reference to the corresponding key in the dictionary, adding the default value if the key is not present. </summary>
    /// <remarks> The ref is valid until the dictionary is modified. </remarks>
    /// <param name="exists"> Whether a new entry was added. </param>
    /// <exception cref="KeyNotFoundException"></exception>
    public static ref V? GetOrAddRef<K, V>(this Dictionary<K, V> dict, K key, out bool exists)
        where K : notnull
    {
        return ref CollectionsMarshal.GetValueRefOrAddDefault(dict, key, out exists);
    }

    /// <summary> Gets a reference to the corresponding key in the dictionary, adding the default value if the key is not present. </summary>
    /// <remarks> The ref is valid until the dictionary is modified. </remarks>
    /// <exception cref="KeyNotFoundException"></exception>
    public static unsafe ref V? GetOrAddRef<K, V>(this Dictionary<K, V> dict, K key)
        where K : notnull
    {
        return ref CollectionsMarshal.GetValueRefOrAddDefault(dict, key, out _);
    }

    /// <summary> Gets the reference of an entry in the dictionary. </summary>
    /// <remarks> The ref is valid until the dictionary is modified. </remarks>
    /// <exception cref="KeyNotFoundException"></exception>
    public static ref V GetRef<K, V>(this Dictionary<K, V> dict, K key)
        where K : notnull
    {
        ref V ptr = ref CollectionsMarshal.GetValueRefOrNullRef(dict, key);
        if (Unsafe.IsNullRef(ref ptr)) {
            throw new KeyNotFoundException();
        }
        return ref ptr;
    }

    public static Span<T> AsSpan<T>(this List<T> list)
    {
        return CollectionsMarshal.AsSpan(list);
    }
    public static Span<T> AsSpan<T>(this List<T> list, int start)
    {
        return CollectionsMarshal.AsSpan(list).Slice(start);
    }
    public static Span<T> AsSpan<T>(this List<T> list, int start, int length)
    {
        return CollectionsMarshal.AsSpan(list).Slice(start, length);
    }
    
    public static ReadOnlySpan<T> AsSpan<T>(this ImmutableArray<T> list, int start)
    {
        return list.AsSpan(start..);
    }

    /// <summary> Checks if the specified span contains the specified object reference. </summary>
    public static bool ContainsRef<T>(this ReadOnlySpan<T> span, T? value) where T : class
    {
        foreach (var obj in span) {
            if (obj == value) {
                return true;
            }
        }
        return false;
    }


    public static Span<T> Slice<T>(this Span<T> span, AbsRange range) => span.Slice(range.Start, range.Length);
    public static ReadOnlySpan<T> Slice<T>(this ReadOnlySpan<T> span, AbsRange range) => span.Slice(range.Start, range.Length);

    /// <summary> 
    /// Extracts the internal array as an <see cref="ImmutableArray{T}"/> and resets the builder
    /// if the capacity matches the count; otherwise, creates a copy and keep the builder unchanged. 
    /// </summary>
    public static ImmutableArray<T> TakeImmutable<T>(this ImmutableArray<T>.Builder builder)
    {
        return builder.Count == builder.Capacity 
            ? builder.MoveToImmutable()
            : builder.ToImmutable();
    }
}
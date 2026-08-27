using System.Collections.Generic;
using UnityEngine;

public static class SolidColorTextureCache
{
    private static readonly Dictionary<ulong, Texture2D> Cache = new Dictionary<ulong, Texture2D>();

    public static Texture2D Get(Color color, FilterMode filterMode = FilterMode.Bilinear,
        TextureWrapMode wrapMode = TextureWrapMode.Repeat)
    {
        Color32 c32 = color;
        ulong key = BuildKey(c32, filterMode, wrapMode);
        if (Cache.TryGetValue(key, out Texture2D texture) && texture != null)
            return texture;

        texture = new Texture2D(2, 2, TextureFormat.RGBA32, mipChain: false);
        texture.hideFlags = HideFlags.HideAndDontSave;
        texture.filterMode = filterMode;
        texture.wrapMode = wrapMode;

        Color[] pixels = { color, color, color, color };
        texture.SetPixels(pixels);
        texture.Apply();

        Cache[key] = texture;
        return texture;
    }

    private static ulong BuildKey(Color32 color, FilterMode filterMode, TextureWrapMode wrapMode)
    {
        return color.r
            | ((ulong)color.g << 8)
            | ((ulong)color.b << 16)
            | ((ulong)color.a << 24)
            | ((ulong)(byte)filterMode << 32)
            | ((ulong)(byte)wrapMode << 40);
    }
}

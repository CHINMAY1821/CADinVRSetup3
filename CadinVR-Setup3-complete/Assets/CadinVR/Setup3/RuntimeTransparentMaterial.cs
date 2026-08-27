using UnityEngine;
using UnityEngine.Rendering;

public static class RuntimeTransparentMaterial
{
    private static Shader cachedTransparentShader;

    public static Material Create(Color color, bool doubleSided = false)
    {
        Shader shader = GetTransparentShader();
        if (shader == null) return null;

        var material = new Material(shader);
        material.hideFlags = HideFlags.HideAndDontSave;
        Configure(material, color, doubleSided);
        return material;
    }

    static Shader GetTransparentShader()
    {
        if (cachedTransparentShader != null)
            return cachedTransparentShader;

        cachedTransparentShader = Shader.Find("Universal Render Pipeline/Unlit");
        if (cachedTransparentShader == null) cachedTransparentShader = Shader.Find("Universal Render Pipeline/Lit");
        if (cachedTransparentShader == null) cachedTransparentShader = Shader.Find("Sprites/Default");
        if (cachedTransparentShader == null) cachedTransparentShader = Shader.Find("Unlit/Color");
        if (cachedTransparentShader == null) cachedTransparentShader = Shader.Find("Standard");
        return cachedTransparentShader;
    }

    static void Configure(Material material, Color color, bool doubleSided)
    {
        SetColor(material, "_BaseColor", color);
        SetColor(material, "_Color", color);

        SetFloat(material, "_Surface", 1f);
        SetFloat(material, "_Blend", 0f);
        SetFloat(material, "_AlphaClip", 0f);
        SetFloat(material, "_ZWrite", 0f);
        SetFloat(material, "_Cull", doubleSided ? (float)CullMode.Off : (float)CullMode.Back);

        SetInt(material, "_SrcBlend", (int)BlendMode.SrcAlpha);
        SetInt(material, "_DstBlend", (int)BlendMode.OneMinusSrcAlpha);

        material.DisableKeyword("_ALPHATEST_ON");
        material.DisableKeyword("_ALPHAPREMULTIPLY_ON");
        material.EnableKeyword("_ALPHABLEND_ON");
        material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        material.SetOverrideTag("RenderType", "Transparent");
        material.renderQueue = (int)RenderQueue.Transparent;
    }

    static void SetColor(Material material, string property, Color color)
    {
        if (material.HasProperty(property))
            material.SetColor(property, color);
    }

    static void SetFloat(Material material, string property, float value)
    {
        if (material.HasProperty(property))
            material.SetFloat(property, value);
    }

    static void SetInt(Material material, string property, int value)
    {
        if (material.HasProperty(property))
            material.SetInt(property, value);
    }
}

// Shared material instances for one built level. No textures anywhere: flat albedo values, which is exactly
// what reads well when the only illumination is point lights in a dark, foggy level.
//
// URP PORT (v4): materials are created on "Universal Render Pipeline/Lit" and written through URP's property
// names - _BaseColor (NOT _Color), _Smoothness (NOT _Glossiness), _Metallic. Every write is guarded by
// HasProperty, and the shader lookup falls back to the built-in "Standard" shader when URP is not present,
// so the same code builds a sane-looking level under either pipeline. Emission is unchanged between the two:
// _EmissionColor + the "_EMISSION" keyword.
//
// Values are the ones the art direction asked for: floors (0.45,0.45,0.42)/smoothness 0.35,
// walls (0.6,0.6,0.56)/0.15, ceilings (0.3,0.3,0.3), corridor floor slightly darker than room floor.
using System.Collections.Generic;
using UnityEngine;
using LevelGen.Core;

namespace LevelGen.Unity
{
    public sealed class LevelMaterials
    {
        public Material Floor;
        public Material CorridorFloor;
        public Material Wall;
        public Material Ceiling;

        // Muted furniture palette, shared across every prop in the level.
        public Material MattressWhite;   // white-ish bedding
        public Material MetalGreyBlue;   // frames, poles, wheels
        public Material DarkWood;        // desks, benches
        public Material Laminate;        // off-white cabinet / counter carcass
        public Material DarkDetail;      // the thin inset "lines" faked on cabinets

        public Material FixtureBody;     // unlit-looking lamp housing
        public Material SpawnMarker;
        public Material KeyMarker;

        readonly Dictionary<LightTint, Material> emissive = new Dictionary<LightTint, Material>();

        // ---------------------------------------------------------------- URP shader / property ids

        static readonly int BaseColorId  = Shader.PropertyToID("_BaseColor");
        static readonly int ColorId      = Shader.PropertyToID("_Color");
        static readonly int SmoothnessId = Shader.PropertyToID("_Smoothness");
        static readonly int GlossinessId = Shader.PropertyToID("_Glossiness");
        static readonly int MetallicId   = Shader.PropertyToID("_Metallic");
        static readonly int EmissionId   = Shader.PropertyToID("_EmissionColor");

        static Shader litShader;

        /// <summary>
        /// URP Lit when the project renders under URP (this one does), otherwise the built-in Standard shader.
        /// Resolved once and cached; a null result from both lookups is reported loudly because every generated
        /// surface would otherwise render magenta.
        /// </summary>
        public static Shader LitShader
        {
            get
            {
                if (litShader != null) return litShader;
                litShader = Shader.Find("Universal Render Pipeline/Lit");
                if (litShader == null) litShader = Shader.Find("Standard");
                if (litShader == null)
                {
                    litShader = Shader.Find("Diffuse");
                    Debug.LogWarning("[LevelMaterials] Neither 'Universal Render Pipeline/Lit' nor 'Standard' " +
                                     "could be found; generated geometry will not shade correctly.");
                }
                return litShader;
            }
        }

        public static Color TintColor(LightTint tint)
        {
            switch (tint)
            {
                case LightTint.WarmIncandescent: return new Color(1.00f, 0.82f, 0.60f);
                case LightTint.CoolFluorescent:  return new Color(0.80f, 0.92f, 1.00f);
                case LightTint.SicklyGreen:      return new Color(0.60f, 1.00f, 0.60f);
                default:                          return new Color(1.00f, 0.20f, 0.15f);
            }
        }

        /// <summary>
        /// One emissive material per tint. Flicker does NOT write to these - it uses a MaterialPropertyBlock,
        /// so fixtures sharing a tint still flicker independently. _EmissionColor + "_EMISSION" is the same
        /// contract in URP Lit as in Standard, which is why LightFlicker needs no pipeline-specific branch.
        /// </summary>
        public Material Emissive(LightTint tint)
        {
            Material m;
            if (emissive.TryGetValue(tint, out m) && m != null) return m;

            Color c = TintColor(tint);
            m = Make($"GenEmissive_{tint}", c * 0.9f, 0f);
            m.EnableKeyword("_EMISSION");
            if (m.HasProperty(EmissionId)) m.SetColor(EmissionId, c * 1.6f);
            emissive[tint] = m;
            return m;
        }

        public void EnsureAll(Material floorOverride, Material wallOverride, Material corridorOverride,
                              Material ceilingOverride)
        {
            Floor         = floorOverride    != null ? floorOverride    : Make("GenFloor",    new Color(0.45f, 0.45f, 0.42f), 0.35f);
            Wall          = wallOverride     != null ? wallOverride     : Make("GenWall",     new Color(0.60f, 0.60f, 0.56f), 0.15f);
            CorridorFloor = corridorOverride != null ? corridorOverride : Make("GenCorridor", new Color(0.36f, 0.36f, 0.34f), 0.35f);
            Ceiling       = ceilingOverride  != null ? ceilingOverride  : Make("GenCeiling",  new Color(0.30f, 0.30f, 0.30f), 0.10f);

            MattressWhite = Make("GenMattress", new Color(0.82f, 0.82f, 0.78f), 0.12f);
            MetalGreyBlue = Make("GenMetal",    new Color(0.48f, 0.53f, 0.58f), 0.45f);
            DarkWood      = Make("GenWood",     new Color(0.24f, 0.17f, 0.12f), 0.20f);
            Laminate      = Make("GenLaminate", new Color(0.74f, 0.72f, 0.66f), 0.30f);
            DarkDetail    = Make("GenDetail",   new Color(0.13f, 0.13f, 0.14f), 0.20f);
            FixtureBody   = Make("GenFixture",  new Color(0.22f, 0.22f, 0.24f), 0.25f);

            SpawnMarker = Make("GenSpawnMarker", new Color(0.2f, 1f, 0.3f), 0f);
            KeyMarker   = Make("GenKeyMarker",   new Color(1f, 0.9f, 0.2f), 0f);
        }

        /// <summary>
        /// An unlit-textured URP Lit (or Standard) material. <paramref name="smoothness"/> goes to _Smoothness
        /// under URP and _Glossiness under the built-in pipeline; both names mean the same 0..1 value.
        /// </summary>
        public static Material Make(string name, Color color, float smoothness)
        {
            var mat = new Material(LitShader) { name = name };

            // URP Lit's albedo is _BaseColor; Standard's is _Color. Write whichever exists (URP Lit keeps a
            // hidden _Color for compatibility, so writing both keeps inspectors and shader variants in sync).
            if (mat.HasProperty(BaseColorId)) mat.SetColor(BaseColorId, color);
            if (mat.HasProperty(ColorId)) mat.SetColor(ColorId, color);

            if (mat.HasProperty(SmoothnessId)) mat.SetFloat(SmoothnessId, smoothness);
            else if (mat.HasProperty(GlossinessId)) mat.SetFloat(GlossinessId, smoothness);

            if (mat.HasProperty(MetallicId)) mat.SetFloat(MetallicId, 0f);
            return mat;
        }
    }
}

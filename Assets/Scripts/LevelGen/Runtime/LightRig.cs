// Light fixtures from layout data, plus the scene-wide "Silent Hill murk" render settings.
//
// Every LightSource becomes a fixture GameObject (visible housing built from the shared unit meshes) with a
// Point Light child at LightSource.Height. A disabled light still gets its fixture - the housing is there,
// the emission is black and the Light component is switched off - because an unlit lamp hanging in a dark
// room is the horror beat, and an absent lamp is just a bug.
//
// NOTE on the using-alias: LevelGen.Core.LightType and UnityEngine.LightType are both in scope here, so the
// Core enum is aliased and the Unity one is always written out in full.
using UnityEngine;
using LevelGen.Core;
using CoreLightType = LevelGen.Core.LightType;

namespace LevelGen.Unity
{
    public static class LightRig
    {
        /// <summary>Emission multiplier applied to the tint colour on a lit fixture.</summary>
        public const float EmissionGain = 1.6f;

        /// <summary>
        /// Flat very dark ambient + dense exponential-squared fog. The fog is what hides the far end of a
        /// corridor and makes a single working lamp read as the only thing in the world.
        /// </summary>
        public static void ApplyRenderSettings(Color ambient, bool fogEnabled, Color fogColor, float fogDensity)
        {
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = ambient;
            RenderSettings.ambientIntensity = 1f;

            RenderSettings.fog = fogEnabled;
            RenderSettings.fogMode = FogMode.ExponentialSquared;
            RenderSettings.fogColor = fogColor;
            RenderSettings.fogDensity = fogDensity;
        }

        /// <summary>
        /// Horror happens indoors. The scene's Directional Light is kept (so nobody wonders where it went)
        /// but its intensity is zeroed, otherwise it flattens every point light we just placed.
        /// </summary>
        public static int KillDirectionalLights()
        {
            Light[] lights = Object.FindObjectsByType<Light>(FindObjectsInactive.Include);
            int killed = 0;
            for (int i = 0; i < lights.Length; i++)
            {
                if (lights[i] == null || lights[i].type != UnityEngine.LightType.Directional) continue;
                lights[i].intensity = 0f;
                killed++;
            }
            return killed;
        }

        /// <summary>
        /// One fixture. <paramref name="yawDeg"/> orients elongated fixtures (corridor strips) along the
        /// corridor; it is ignored by the radially symmetric ones.
        /// </summary>
        public static GameObject BuildFixture(Transform parent, LightSource src, int index,
                                              float ceilingHeight, LevelMaterials mats, float yawDeg)
        {
            if (src == null) return null;

            var root = new GameObject($"Light_{src.Type}_{index}");
            root.layer = 0;
            root.transform.SetParent(parent, false);
            root.transform.localPosition = new Vector3((float)src.Position.X, 0f, (float)src.Position.Z);
            root.transform.localRotation = Quaternion.Euler(0f, yawDeg, 0f);

            float h = Mathf.Clamp((float)src.Height, 0.1f, Mathf.Max(0.2f, ceilingHeight));
            Color tint = LevelMaterials.TintColor(src.Tint);
            Material lit = mats.Emissive(src.Tint);
            Material housing = mats.FixtureBody;

            MeshRenderer emissive = null;

            switch (src.Type)
            {
                case CoreLightType.CeilingLamp:
                {
                    float shadeTop = h + 0.14f;
                    float cordLen = Mathf.Max(0.02f, ceilingHeight - shadeTop);
                    GenPrimitives.CylPart(root.transform, "Cord", housing,
                        new Vector3(0f, shadeTop + cordLen * 0.5f, 0f), 0.03f, cordLen);
                    // Shallow inverted cone: wide rim at the bottom, narrow throat at the cord.
                    var shade = GenPrimitives.Part(root.transform, "Shade", GenPrimitives.Frustum(0.5f, 0.14f),
                        lit, new Vector3(0f, h + 0.07f, 0f), new Vector3(0.44f, 0.14f, 0.44f), Quaternion.identity);
                    emissive = shade.GetComponent<MeshRenderer>();
                    break;
                }
                case CoreLightType.DeskLamp:
                {
                    float baseY = Mathf.Max(0f, h - 0.30f);
                    GenPrimitives.BoxPart(root.transform, "Base", housing,
                        new Vector3(0f, baseY + 0.02f, 0f), new Vector3(0.14f, 0.04f, 0.14f));
                    float armTop = Mathf.Max(baseY + 0.08f, h - 0.06f);
                    GenPrimitives.CylPart(root.transform, "Arm", housing,
                        new Vector3(0f, (baseY + 0.04f + armTop) * 0.5f, 0f), 0.03f,
                        Mathf.Max(0.04f, armTop - baseY - 0.04f));
                    var shade = GenPrimitives.Part(root.transform, "Shade", GenPrimitives.Frustum(0.5f, 0.12f),
                        lit, new Vector3(0f, h + 0.02f, 0f), new Vector3(0.17f, 0.10f, 0.17f), Quaternion.identity);
                    emissive = shade.GetComponent<MeshRenderer>();
                    break;
                }
                case CoreLightType.WallSconce:
                {
                    GenPrimitives.BoxPart(root.transform, "Bracket", housing,
                        new Vector3(0f, h, 0f), new Vector3(0.17f, 0.26f, 0.10f));
                    var glass = GenPrimitives.Part(root.transform, "Glass", GenPrimitives.Cylinder, lit,
                        new Vector3(0f, h, 0f), new Vector3(0.12f, 0.20f, 0.12f), Quaternion.identity);
                    emissive = glass.GetComponent<MeshRenderer>();
                    break;
                }
                default: // CorridorStrip
                {
                    float y = Mathf.Min(h + 0.05f, ceilingHeight - 0.05f);
                    var strip = GenPrimitives.Part(root.transform, "Strip", GenPrimitives.Box, lit,
                        new Vector3(0f, y, 0f), new Vector3(1.2f, 0.08f, 0.15f), Quaternion.identity);
                    emissive = strip.GetComponent<MeshRenderer>();
                    break;
                }
            }

            // ------------------------------------------------------------ the actual light
            var bulb = new GameObject("Bulb");
            bulb.transform.SetParent(root.transform, false);
            bulb.transform.localPosition = new Vector3(0f, h, 0f);

            var light = bulb.AddComponent<Light>();
            light.type = UnityEngine.LightType.Point;
            light.color = tint;
            light.range = Mathf.Max(0.5f, (float)src.Range);
            light.intensity = Mathf.Max(0f, (float)src.Intensity);
            light.renderMode = LightRenderMode.Auto;

            if (src.Type == CoreLightType.CeilingLamp)
            {
                light.shadows = LightShadows.Soft;
                light.shadowStrength = 0.8f;
                light.shadowBias = 0.05f;
            }
            else
            {
                light.shadows = LightShadows.None;
            }

            light.enabled = src.Enabled;

            // ------------------------------------------------------------ emission / flicker
            var renderers = emissive != null ? new[] { emissive } : new MeshRenderer[0];
            var block = new MaterialPropertyBlock();

            if (!src.Enabled)
            {
                block.SetColor("_EmissionColor", Color.black);
                for (int i = 0; i < renderers.Length; i++) renderers[i].SetPropertyBlock(block);
            }
            else
            {
                block.SetColor("_EmissionColor", tint * EmissionGain);
                for (int i = 0; i < renderers.Length; i++) renderers[i].SetPropertyBlock(block);

                if (src.Flicker)
                    root.AddComponent<LightFlicker>()
                        .Configure(light, renderers, light.intensity, tint, EmissionGain);
            }

            return root;
        }

        /// <summary>
        /// A teammate's light prefab in place of the generated housing, used whenever the active
        /// <see cref="LevelGenProfile"/> maps <see cref="LightSource.Type"/> to a prefab.
        ///
        /// Placement: a CeilingLamp or CorridorStrip has its ROOT put on the ceiling plane
        /// (x, <paramref name="ceilingHeight"/>, z) so a prefab authored to hang downwards from its mount
        /// hangs correctly; a DeskLamp or WallSconce has its root put at the fixture height
        /// (x, LightSource.Height, z), which is where it sits on its prop / on the wall. <paramref name="yawDeg"/>
        /// aligns elongated fixtures with their corridor.
        ///
        /// The Light: the FIRST <see cref="Light"/> found anywhere in the prefab's hierarchy (inactive ones
        /// included) is the fixture's light. When <paramref name="applyDataToLight"/> is set, the generated
        /// colour / intensity / range are copied onto it and ceiling lamps get soft shadows (honoured in URP
        /// Forward+ with additional light shadows enabled on the renderer asset); otherwise the prefab's own
        /// hand-tuned values are kept. Either way a dead fixture has its Light disabled and a flickering one
        /// gets a <see cref="LightFlicker"/>, so the horror beats work on teammate art without extra wiring.
        ///
        /// Flicker emission: any MeshRenderer in the prefab whose shared material has the "_EMISSION" keyword
        /// enabled is driven in step with the light, so a prefab with a self-illuminated lens visibly goes
        /// dark. A prefab with no emissive material simply flickers the light alone.
        /// </summary>
        public static GameObject BuildFixtureFromPrefab(Transform parent, LightSource src, int index,
                                                        GameObject prefab, float ceilingHeight,
                                                        bool applyDataToLight, float yawDeg)
        {
            if (src == null || prefab == null) return null;

            bool ceilingMounted = src.Type == CoreLightType.CeilingLamp || src.Type == CoreLightType.CorridorStrip;
            float h = Mathf.Clamp((float)src.Height, 0.1f, Mathf.Max(0.2f, ceilingHeight));
            float y = ceilingMounted ? Mathf.Max(0.2f, ceilingHeight) : h;

            GameObject root = Object.Instantiate(prefab, parent);
            root.name = $"Light_{src.Type}_{index}_{prefab.name}";
            root.transform.localPosition = new Vector3((float)src.Position.X, y, (float)src.Position.Z);
            root.transform.localRotation = Quaternion.Euler(0f, yawDeg, 0f);

            Light light = root.GetComponentInChildren<Light>(true);
            if (light == null)
            {
                Debug.LogWarning($"[LightRig] light prefab '{prefab.name}' mapped to {src.Type} contains no " +
                                 "Light component; the fixture is placed but emits nothing.");
                return root;
            }

            Color tint = LevelMaterials.TintColor(src.Tint);
            if (applyDataToLight)
            {
                light.color = tint;
                light.range = Mathf.Max(0.5f, (float)src.Range);
                light.intensity = Mathf.Max(0f, (float)src.Intensity);
                if (src.Type == CoreLightType.CeilingLamp)
                {
                    light.shadows = LightShadows.Soft;
                    light.shadowStrength = 0.8f;
                    light.shadowBias = 0.05f;
                }
            }
            else
            {
                tint = light.color;
            }

            light.enabled = src.Enabled;

            MeshRenderer[] renderers = CollectEmissiveRenderers(root);
            if (src.Enabled)
            {
                if (src.Flicker)
                    root.AddComponent<LightFlicker>()
                        .Configure(light, renderers, light.intensity, tint, EmissionGain);
            }
            else if (renderers.Length > 0)
            {
                var block = new MaterialPropertyBlock();
                block.SetColor("_EmissionColor", Color.black);
                for (int i = 0; i < renderers.Length; i++) renderers[i].SetPropertyBlock(block);
            }

            return root;
        }

        /// <summary>MeshRenderers in <paramref name="root"/> whose shared material declares the "_EMISSION" keyword.</summary>
        static MeshRenderer[] CollectEmissiveRenderers(GameObject root)
        {
            MeshRenderer[] all = root.GetComponentsInChildren<MeshRenderer>(true);
            var hits = new System.Collections.Generic.List<MeshRenderer>();
            for (int i = 0; i < all.Length; i++)
            {
                Material m = all[i] != null ? all[i].sharedMaterial : null;
                if (m == null) continue;
                if (m.IsKeywordEnabled("_EMISSION") && m.HasProperty("_EmissionColor")) hits.Add(all[i]);
            }
            return hits.ToArray();
        }
    }
}

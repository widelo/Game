// Deterministic per-fixture flicker. Randomises the point light's intensity every 0.05-0.3 s between 0.3x
// and 1.1x the base value, with occasional 0.2-0.8 s blackouts, and drives the shade's emission colour in
// step so the fixture visibly goes dark with its light.
//
// Two deliberate choices:
//  * the emission is written through a MaterialPropertyBlock, not the material, so every fixture sharing the
//    one emissive material per tint still flickers independently;
//  * the RNG is a System.Random seeded from a hash of the fixture's world position, so a given seed produces
//    the same flicker pattern every run (UnityEngine.Random would be global state and non-reproducible).
using UnityEngine;

namespace LevelGen.Unity
{
    [AddComponentMenu("LevelGen/Light Flicker")]
    public sealed class LightFlicker : MonoBehaviour
    {
        [SerializeField] Light target;
        [SerializeField] MeshRenderer[] emissiveRenderers;
        [SerializeField] float baseIntensity = 1f;
        [SerializeField] Color emissionColor = Color.white;
        [SerializeField] float emissionGain = 1.6f;

        static readonly int EmissionId = Shader.PropertyToID("_EmissionColor");

        System.Random rng;
        MaterialPropertyBlock block;
        float nextSwitchAt;
        float level = 1f;

        public void Configure(Light light, MeshRenderer[] renderers, float intensity, Color emission, float gain)
        {
            target = light;
            emissiveRenderers = renderers;
            baseIntensity = intensity;
            emissionColor = emission;
            emissionGain = gain;
            rng = null; // re-seeded on the next Step from the (now final) transform position
        }

        void OnEnable()
        {
            EnsureRng();
            nextSwitchAt = 0f;
        }

        void EnsureRng()
        {
            if (rng != null) return;
            Vector3 p = transform.position;
            int hash = 17;
            hash = hash * 31 + Mathf.RoundToInt(p.x * 100f);
            hash = hash * 31 + Mathf.RoundToInt(p.y * 100f);
            hash = hash * 31 + Mathf.RoundToInt(p.z * 100f);
            rng = new System.Random(hash);
            if (block == null) block = new MaterialPropertyBlock();
        }

        void Update()
        {
            if (Time.time < nextSwitchAt) return;
            EnsureRng();

            bool blackout = rng.NextDouble() < 0.12;
            if (blackout)
            {
                level = 0f;
                nextSwitchAt = Time.time + 0.2f + (float)rng.NextDouble() * 0.6f;
            }
            else
            {
                level = 0.3f + (float)rng.NextDouble() * 0.8f;   // 0.3x .. 1.1x
                nextSwitchAt = Time.time + 0.05f + (float)rng.NextDouble() * 0.25f;
            }
            Apply();
        }

        void Apply()
        {
            if (target != null) target.intensity = baseIntensity * level;
            if (emissiveRenderers == null) return;

            block.SetColor(EmissionId, emissionColor * (emissionGain * level));
            for (int i = 0; i < emissiveRenderers.Length; i++)
                if (emissiveRenderers[i] != null) emissiveRenderers[i].SetPropertyBlock(block);
        }
    }
}

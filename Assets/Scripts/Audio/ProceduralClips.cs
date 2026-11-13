// Every sound in the game, synthesized at runtime with AudioClip.Create - there are no audio files.
//
// All synthesis is deterministic (a fixed-seed xorshift per clip), so a clip sounds the same on every run.
// Clips are generated lazily on first request and cached by name. Sounds that want variety (Creak, Footstep)
// are cached as a small bank of seeded variants and a call picks one at random.
//
// Cost: roughly 1.1 M samples in total (the stereo drone is most of it); WarmAll() logs the total once.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

namespace Game.Audio
{
    public static class ProceduralClips
    {
        public const int SampleRate = 44100;
        public const int CreakVariants = 4;
        public const int FootstepVariants = 4;

        static readonly Dictionary<string, AudioClip> Cache = new Dictionary<string, AudioClip>();
        static readonly System.Random Picker = new System.Random(462);
        static double _totalMs;
        static bool _loggedTotal;

        /// <summary>Total milliseconds spent synthesizing so far.</summary>
        public static double TotalGenerationMs => _totalMs;

        // ------------------------------------------------------------------ public clips

        /// <summary>Low layered 48-55 Hz drone with slow beating and brown noise, stereo, seamless loop.</summary>
        public static AudioClip DroneLoop(float seconds = 8f)
        {
            string name = "Drone_" + seconds.ToString("0.##");
            return Get(name, () => SynthDrone(name, seconds));
        }

        /// <summary>A door/floor creak; one of a few seeded variants, picked at random.</summary>
        public static AudioClip Creak()
        {
            int v = Picker.Next(CreakVariants);
            return Get("Creak_" + v, () => SynthCreak("Creak_" + v, 1000 + v));
        }

        /// <summary>A footstep. hard = tile (bright), soft = carpet-ish (dull). Random variant.</summary>
        public static AudioClip Footstep(bool hard)
        {
            int v = Picker.Next(FootstepVariants);
            string name = (hard ? "StepHard_" : "StepSoft_") + v;
            return Get(name, () => SynthFootstep(name, hard, (hard ? 2000 : 3000) + v));
        }

        public static AudioClip Heartbeat() => Get("Heartbeat", () => SynthHeartbeat("Heartbeat"));
        public static AudioClip Breath() => Get("Breath", () => SynthBreath("Breath"));
        public static AudioClip Growl() => Get("Growl", () => SynthGrowl("Growl"));
        public static AudioClip Shriek() => Get("Shriek", () => SynthShriek("Shriek"));
        public static AudioClip KeyChime() => Get("KeyChime", () => SynthKeyChime("KeyChime"));
        public static AudioClip GateUnlock() => Get("GateUnlock", () => SynthGateUnlock("GateUnlock"));
        public static AudioClip EscapeSting() => Get("EscapeSting", () => SynthEscape("EscapeSting"));
        public static AudioClip Caught() => Get("Caught", () => SynthCaught("Caught"));

        /// <summary>Generates every clip and variant (cheap; avoids a hitch on the first footstep). Logs the total once.</summary>
        public static void WarmAll()
        {
            DroneLoop();
            for (int v = 0; v < CreakVariants; v++) Get("Creak_" + v, () => SynthCreak("Creak_" + v, 1000 + v));
            for (int v = 0; v < FootstepVariants; v++)
            {
                Get("StepHard_" + v, () => SynthFootstep("StepHard_" + v, true, 2000 + v));
                Get("StepSoft_" + v, () => SynthFootstep("StepSoft_" + v, false, 3000 + v));
            }
            Heartbeat(); Breath(); Growl(); Shriek(); KeyChime(); GateUnlock(); EscapeSting(); Caught();
            if (!_loggedTotal)
            {
                _loggedTotal = true;
                UnityEngine.Debug.Log($"[Audio] synthesized {Cache.Count} clips in {_totalMs:0.0} ms");
            }
        }

        static AudioClip Get(string name, Func<AudioClip> make)
        {
            if (Cache.TryGetValue(name, out AudioClip clip) && clip != null) return clip;
            var sw = Stopwatch.StartNew();
            clip = make();
            sw.Stop();
            _totalMs += sw.Elapsed.TotalMilliseconds;
            clip.hideFlags = HideFlags.DontSave;
            Cache[name] = clip;
            return clip;
        }

        // ------------------------------------------------------------------ synthesizers

        static AudioClip SynthDrone(string name, float seconds)
        {
            int n = Mathf.Max(1, Mathf.RoundToInt(seconds * SampleRate));
            int fade = Mathf.Min(n / 4, SampleRate);       // 1 s crossfade of the tail into the head
            int total = n + fade;
            float loopSeconds = n / (float)SampleRate;
            var rng = new Rng(77);
            var left = new float[total];
            var right = new float[total];

            // Partials snapped to whole cycles per loop so the sines are seamless by construction; the pairs
            // detune by a fraction of a hertz for slow beating.
            float[] baseHz = { 48f, 48.35f, 51.5f, 55f, 55.25f };
            float[] amp = { 0.30f, 0.22f, 0.12f, 0.22f, 0.16f };
            double twoPi = Math.PI * 2.0;
            for (int p = 0; p < baseHz.Length; p++)
            {
                double fL = Snap(baseHz[p], loopSeconds);
                double fR = Snap(baseHz[p] + 0.13f * (p % 2 == 0 ? 1 : -1), loopSeconds);
                double phL = rng.Next01() * twoPi, phR = rng.Next01() * twoPi;
                // Rotating phasors instead of Math.Sin per sample: ~10x cheaper, drift is negligible over 9 s.
                double cL = Math.Cos(twoPi * fL / SampleRate), sL = Math.Sin(twoPi * fL / SampleRate);
                double cR = Math.Cos(twoPi * fR / SampleRate), sR = Math.Sin(twoPi * fR / SampleRate);
                double xL = Math.Cos(phL), yL = Math.Sin(phL), xR = Math.Cos(phR), yR = Math.Sin(phR);
                for (int i = 0; i < total; i++)
                {
                    left[i] += amp[p] * (float)yL;
                    right[i] += amp[p] * (float)yR;
                    double nx = xL * cL - yL * sL; yL = xL * sL + yL * cL; xL = nx;
                    nx = xR * cR - yR * sR; yR = xR * sR + yR * cR; xR = nx;
                }
            }

            // Brown noise (leaky integrated white), low-passed, with a slow swell (one cycle per loop).
            float bL = 0f, bR = 0f, lpL = 0f, lpR = 0f;
            float a = OnePoleCoeff(180f);
            for (int i = 0; i < total; i++)
            {
                bL = bL * 0.995f + rng.NextSigned() * 0.05f;
                bR = bR * 0.995f + rng.NextSigned() * 0.05f;
                lpL += a * (bL - lpL);
                lpR += a * (bR - lpR);
                float swell = 0.75f + 0.25f * (float)Math.Sin(twoPi * (i % n) / n);
                left[i] = left[i] * swell + lpL * 1.4f;
                right[i] = right[i] * swell + lpR * 1.4f;
            }

            var data = new float[n * 2];
            for (int i = 0; i < n; i++)
            {
                float l = left[i], r = right[i];
                if (i < fade)
                {
                    float w = i / (float)fade;            // the head fades in while the overrun tail fades out
                    l = l * w + left[n + i] * (1f - w);
                    r = r * w + right[n + i] * (1f - w);
                }
                data[i * 2] = l;
                data[i * 2 + 1] = r;
            }
            Normalize(data, 0.8f);
            return Make(name, data, 2);
        }

        static double Snap(double hz, double loopSeconds) => Math.Max(1.0, Math.Round(hz * loopSeconds)) / loopSeconds;

        static AudioClip SynthCreak(string name, int seed)
        {
            var rng = new Rng(seed);
            float dur = 0.6f + 0.6f * rng.Next01();
            int n = Samples(dur);
            var d = new float[n];
            float startHz = 700f + 400f * rng.Next01(), endHz = 180f + 120f * rng.Next01();
            float tickStart = 38f + 20f * rng.Next01(), tickEnd = 12f + 8f * rng.Next01();
            var bp = new Biquad();
            double tickPhase = 0;
            for (int i = 0; i < n; i++)
            {
                float u = i / (float)n;
                float centre = Mathf.Lerp(startHz, endHz, u * u * (3f - 2f * u));
                if ((i & 31) == 0) bp.SetBandPass(centre, 9f);
                float s = bp.Process(rng.NextSigned());
                // Stick-slip: the friction "ticks" slow down as the board settles.
                tickPhase += Mathf.Lerp(tickStart, tickEnd, u) / SampleRate;
                float tick = (float)(tickPhase - Math.Floor(tickPhase));
                float stick = Mathf.Exp(-tick * 6f);
                d[i] = s * stick * Envelope(u, 0.08f, 0.25f);
            }
            Normalize(d, 0.8f);
            return Make(name, d, 1);
        }

        static AudioClip SynthFootstep(string name, bool hard, int seed)
        {
            var rng = new Rng(seed);
            int n = Samples(0.09f);
            int attack = Samples(0.002f);
            var d = new float[n];
            float cutoff = hard ? 3200f + 900f * rng.Next01() : 650f + 250f * rng.Next01();
            float tau = hard ? 0.016f : 0.024f;
            float lp1 = 0f, lp2 = 0f, a = OnePoleCoeff(cutoff);
            float thumpHz = hard ? 110f + 30f * rng.Next01() : 70f + 20f * rng.Next01();
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)SampleRate;
                float env = i < attack ? i / (float)attack : Mathf.Exp(-(t - 0.002f) / tau);
                lp1 += a * (rng.NextSigned() - lp1);
                lp2 += a * (lp1 - lp2);
                float noise = hard ? lp1 : lp2;
                float thump = Mathf.Sin(2f * Mathf.PI * thumpHz * t) * Mathf.Exp(-t / 0.03f) * (hard ? 0.3f : 0.6f);
                d[i] = (noise * (hard ? 1f : 2.2f) + thump) * env;
            }
            Normalize(d, hard ? 0.85f : 0.7f);
            return Make(name, d, 1);
        }

        static AudioClip SynthHeartbeat(string name)
        {
            int n = Samples(0.5f);
            var d = new float[n];
            AddThump(d, 0f, 1f);
            AddThump(d, 0.15f, 0.7f);
            Normalize(d, 0.9f);
            return Make(name, d, 1);
        }

        static void AddThump(float[] d, float at, float gain)
        {
            int start = Samples(at);
            int len = Mathf.Min(d.Length - start, Samples(0.14f));
            for (int i = 0; i < len; i++)
            {
                float t = i / (float)SampleRate;
                float hz = 60f * (1f + 0.4f * Mathf.Exp(-t / 0.015f)); // slight pitch drop = "thump"
                float env = Mathf.Min(1f, t / 0.003f) * Mathf.Exp(-t / 0.035f);
                d[start + i] += gain * env * Mathf.Sin(2f * Mathf.PI * hz * t);
            }
        }

        static AudioClip SynthBreath(string name)
        {
            var rng = new Rng(11);
            int n = Samples(1.4f);
            var d = new float[n];
            var bp = new Biquad();
            bp.SetBandPass(700f, 1.2f);
            var bp2 = new Biquad();
            bp2.SetBandPass(1600f, 3f);
            for (int i = 0; i < n; i++)
            {
                float u = i / (float)n;
                // Inhale (0-0.45) then a longer, louder exhale (0.5-1).
                float env = u < 0.45f ? Mathf.Sin(Mathf.PI * u / 0.45f) * 0.55f
                          : u < 0.5f ? 0f
                          : Mathf.Pow(Mathf.Sin(Mathf.PI * (u - 0.5f) / 0.5f), 1.5f);
                float w = rng.NextSigned();
                d[i] = (bp.Process(w) + 0.35f * bp2.Process(w)) * env;
            }
            Normalize(d, 0.7f);
            return Make(name, d, 1);
        }

        static AudioClip SynthGrowl(string name)
        {
            var rng = new Rng(21);
            int n = Samples(0.8f);
            var d = new float[n];
            double phase = 0;
            float lp = 0f, a = OnePoleCoeff(900f), jitter = 0f;
            for (int i = 0; i < n; i++)
            {
                float u = i / (float)n, t = i / (float)SampleRate;
                jitter = jitter * 0.999f + rng.NextSigned() * 0.02f;
                float hz = Mathf.Lerp(70f, 110f, Mathf.Sin(Mathf.PI * u)) + 9f * Mathf.Sin(2f * Mathf.PI * 6.5f * t) + 40f * jitter;
                phase += hz / SampleRate;
                float saw = 2f * (float)(phase - Math.Floor(phase)) - 1f;
                float x = (float)Math.Tanh(3.5f * (saw + 0.25f * rng.NextSigned()));
                lp += a * (x - lp);
                d[i] = lp * Envelope(u, 0.1f, 0.3f);
            }
            Normalize(d, 0.85f);
            return Make(name, d, 1);
        }

        static AudioClip SynthShriek(string name)
        {
            var rng = new Rng(31);
            int n = Samples(1.2f);
            var d = new float[n];
            double phase = 0;
            var bp = new Biquad();
            for (int i = 0; i < n; i++)
            {
                float u = i / (float)n, t = i / (float)SampleRate;
                float hz = 300f * Mathf.Pow(4f, Mathf.Min(1f, u / 0.8f)) * (1f + 0.03f * Mathf.Sin(2f * Mathf.PI * 11f * t));
                phase += hz / SampleRate;
                float ph = (float)(phase - Math.Floor(phase));
                float tone = Mathf.Sin(2f * Mathf.PI * ph) + 0.4f * (2f * ph - 1f);
                if ((i & 31) == 0) bp.SetBandPass(hz * 1.5f, 2f);
                float noise = bp.Process(rng.NextSigned());
                d[i] = (float)Math.Tanh(2f * (tone * 0.6f + noise * 0.8f)) * Envelope(u, 0.05f, 0.3f);
            }
            Normalize(d, 0.8f);
            return Make(name, d, 1);
        }

        static AudioClip SynthKeyChime(string name)
        {
            int n = Samples(1.0f);
            var d = new float[n];
            float[] hz = { 659.25f, 783.99f, 987.77f }; // E5 G5 B5
            for (int k = 0; k < hz.Length; k++)
            {
                int start = Samples(0.11f * k);
                for (int i = start; i < n; i++)
                {
                    float t = (i - start) / (float)SampleRate;
                    float env = Mathf.Min(1f, t / 0.004f) * Mathf.Exp(-t / 0.22f);
                    d[i] += env * (Mathf.Sin(2f * Mathf.PI * hz[k] * t) + 0.2f * Mathf.Sin(2f * Mathf.PI * hz[k] * 2f * t));
                }
            }
            Normalize(d, 0.6f);
            return Make(name, d, 1);
        }

        static AudioClip SynthGateUnlock(string name)
        {
            var rng = new Rng(41);
            int n = Samples(1.1f);
            var d = new float[n];
            float hp = 0f, prev = 0f;
            // Metallic clank: a hard noise transient plus two inharmonic rings.
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)SampleRate;
                float w = rng.NextSigned();
                hp = 0.9f * (hp + w - prev); prev = w;    // crude high-pass keeps the clank bright
                float clank = hp * Mathf.Exp(-t / 0.025f);
                float ring = (Mathf.Sin(2f * Mathf.PI * 800f * t) + 0.7f * Mathf.Sin(2f * Mathf.PI * 1300f * t)
                            + 0.3f * Mathf.Sin(2f * Mathf.PI * 2210f * t)) * Mathf.Exp(-t / 0.18f) * 0.35f;
                d[i] = clank + ring;
            }
            // Low thud as the bolt drops.
            int thudAt = Samples(0.38f);
            for (int i = thudAt; i < n; i++)
            {
                float t = (i - thudAt) / (float)SampleRate;
                float hz = 48f * (1f + 0.5f * Mathf.Exp(-t / 0.02f));
                d[i] += 0.9f * Mathf.Min(1f, t / 0.003f) * Mathf.Exp(-t / 0.09f) * Mathf.Sin(2f * Mathf.PI * hz * t);
            }
            Normalize(d, 0.85f);
            return Make(name, d, 1);
        }

        static AudioClip SynthEscape(string name)
        {
            int n = Samples(1.5f);
            var d = new float[n];
            double p1 = 0, p2 = 0, p3 = 0;
            for (int i = 0; i < n; i++)
            {
                float u = i / (float)n;
                float glide = 1f + 0.06f * u;                         // the whole chord lifts slightly
                p1 += 220f * glide / SampleRate;                      // A3
                p2 += 329.63f * glide / SampleRate;                   // E4, a fifth up, enters a beat later
                p3 += 440f * glide / SampleRate;                      // A4 shimmer at the top
                float swell = Mathf.SmoothStep(0f, 1f, Mathf.Min(1f, u / 0.7f)) * Mathf.Min(1f, (1f - u) / 0.2f);
                float fifthIn = Mathf.Clamp01((u - 0.25f) / 0.3f);
                float s = Mathf.Sin(2f * Mathf.PI * (float)(p1 % 1.0))
                        + fifthIn * 0.8f * Mathf.Sin(2f * Mathf.PI * (float)(p2 % 1.0))
                        + fifthIn * 0.3f * Mathf.Sin(2f * Mathf.PI * (float)(p3 % 1.0));
                d[i] = s * swell;
            }
            Normalize(d, 0.6f);
            return Make(name, d, 1);
        }

        static AudioClip SynthCaught(string name)
        {
            var rng = new Rng(51);
            int n = Samples(1.6f);
            var d = new float[n];
            float lp = 0f, a = OnePoleCoeff(400f);
            for (int i = 0; i < Samples(0.6f); i++)
            {
                float t = i / (float)SampleRate;
                lp += a * (rng.NextSigned() - lp);
                float hz = 38f * (1f + 1.2f * Mathf.Exp(-t / 0.04f));
                d[i] += (1.6f * lp + Mathf.Sin(2f * Mathf.PI * hz * t)) * Mathf.Min(1f, t / 0.002f) * Mathf.Exp(-t / 0.16f);
            }
            AddThump(d, 0.55f, 0.8f); AddThump(d, 0.70f, 0.55f);
            AddThump(d, 1.15f, 0.7f); AddThump(d, 1.30f, 0.45f);
            Normalize(d, 0.95f);
            return Make(name, d, 1);
        }

        // ------------------------------------------------------------------ helpers

        static int Samples(float seconds) => Mathf.Max(1, Mathf.RoundToInt(seconds * SampleRate));

        static AudioClip Make(string name, float[] data, int channels)
        {
            var clip = AudioClip.Create("proc_" + name, data.Length / channels, channels, SampleRate, false);
            clip.SetData(data, 0);
            return clip;
        }

        /// <summary>Attack/release fractions of the clip; a smooth trapezoid.</summary>
        static float Envelope(float u, float attack, float release)
        {
            float e = 1f;
            if (u < attack) e = u / attack;
            else if (u > 1f - release) e = (1f - u) / release;
            return e * e * (3f - 2f * e);
        }

        static float OnePoleCoeff(float cutoffHz) => 1f - Mathf.Exp(-2f * Mathf.PI * cutoffHz / SampleRate);

        static void Normalize(float[] d, float peak)
        {
            float max = 0f;
            for (int i = 0; i < d.Length; i++) { float v = Math.Abs(d[i]); if (v > max) max = v; }
            if (max < 1e-6f) return;
            float g = peak / max;
            for (int i = 0; i < d.Length; i++) d[i] *= g;
        }

        struct Rng
        {
            uint _s;
            public Rng(int seed) { _s = (uint)seed * 2654435761u + 1u; if (_s == 0) _s = 1; }
            public uint NextU() { _s ^= _s << 13; _s ^= _s >> 17; _s ^= _s << 5; return _s; }
            public float Next01() => (NextU() >> 8) * (1f / 16777216f);
            public float NextSigned() => Next01() * 2f - 1f;
        }

        /// <summary>RBJ band-pass biquad (constant 0 dB peak gain).</summary>
        sealed class Biquad
        {
            float _b0, _b2, _a1, _a2, _x1, _x2, _y1, _y2;
            public void SetBandPass(float hz, float q)
            {
                float w = 2f * Mathf.PI * Mathf.Clamp(hz, 20f, SampleRate * 0.45f) / SampleRate;
                float alpha = Mathf.Sin(w) / (2f * q);
                float a0 = 1f + alpha;
                _b0 = alpha / a0; _b2 = -alpha / a0;
                _a1 = -2f * Mathf.Cos(w) / a0; _a2 = (1f - alpha) / a0;
            }
            public float Process(float x)
            {
                float y = _b0 * x + _b2 * _x2 - _a1 * _y1 - _a2 * _y2;
                _x2 = _x1; _x1 = x; _y2 = _y1; _y1 = y;
                return y;
            }
        }
    }
}

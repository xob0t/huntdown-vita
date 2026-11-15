using System.Collections.Generic;
using UnityEngine;

// What a sprite costs the PS Vita's player, by how sprites are set up: a test that adds sprites of its own to whatever
// is on screen and times the frames. With a few hundred of the game's sprites on screen a frame passes 16.7 ms, about
// 50 us of the main thread a sprite, and the game's code is little of it: whether that is per sprite or per draw call
// (sprites batch when one after another in drawing order shares a texture and material) decides what can help.
// Each step adds n small sprites (8x8 screen pixels, so the GPU's fill doesn't count) spread over the view, waits a
// second, then averages the frame time over three (HUNT_BENCH mode n ms), and removes them. Modes:
//   same     one texture, one sorting order: batchable
//   orders   one texture, each sprite its own sorting order: still batchable, in order
//   alt      two textures alternating in sorting order: no two neighbours share one, nothing batches
//   alt0     two textures, one sorting order: whether the engine groups them by texture itself
//   plain    as same, without what a sprite doesn't use: light and reflection probes, shadows, occlusion
//   moving   as same, each sprite moved every frame (its transform changes, as the game's effects do)
//   movingparent  as moving, all children of one object, as the game's sprites are (Gfx.levelSprite)
//   rewrite  as same, each sprite's position set every frame to where it is (the game's resting debris)
//   rewritecheck  as rewrite, set only when it differs from the transform's
//   sprite   as same, each sprite's sprite set every frame to the one it has
//   movinglocal   as moving, through cached transforms' localPosition, without reading it back
//   movinghidden  as movinglocal, the renderers disabled: what the transforms cost on their own
//   movingbare    as movinglocal, objects without a renderer at all
//   nooc     as same, the camera's occlusion culling off (the scene has it on, with no occlusion data)
//   ortho    as same, the camera's transparency sort orthographic
//   deflayer as same, on the Default sorting layer
//   -hunt-sprite-bench <after s>   from after s seconds, every mode at 0, 600 and 1200 sprites (counts that
//                                  take a frame past vsync's 16.7 ms, so the slope between them is the cost)
// Part of HuntCompat; see Compat2018.cs.
namespace HuntCompat
{
    // Static and driven from Application.onBeforeRender, once a frame: a new MonoBehaviour would need a full build (quickcode
    // can't add one the player's data doesn't list).
    public static class SpriteBench
    {
        static readonly string[] Modes = { "same", "nooc", "ortho", "deflayer", "same" };
        static readonly int[] Counts = { 0, 600, 1200 };

        static readonly List<GameObject> made = new List<GameObject>();
        static readonly List<Vector3> positions = new List<Vector3>();
        static readonly List<Transform> transforms = new List<Transform>();
        static Sprite[] sprites;
        static float after, stepStart;
        static int step = -1, frames;
        static bool measuring;

        internal static void Install(string[] args)
        {
            if (!HuntFlags.Has(args, "-hunt-sprite-bench")) return;
            after = HuntFlags.Number(args, "-hunt-sprite-bench", 15f);
            Application.onBeforeRender += Frame;
        }

        static void Frame()
        {
            float now = Time.realtimeSinceStartup;
            if (step < 0)
            {
                if (now < after) return;
                sprites = new[] { MakeSprite(new Color(1f, 0.3f, 0.3f)), MakeSprite(new Color(0.3f, 1f, 0.3f)) };
                Begin(0, now);
                return;
            }
            string mode = Modes[step / Counts.Length];
            if (mode == "movinglocal" || mode == "movinghidden" || mode == "movingbare")
            {
                Vector3 shift = new Vector3(Mathf.Sin(Time.frameCount * 0.2f) * 0.05f, 0f, 0f);
                for (int i = 0; i < transforms.Count; i++) transforms[i].localPosition = positions[i] + shift;
            }
            else if (mode.StartsWith("moving"))
            {
                float wobble = Mathf.Sin(Time.frameCount * 0.2f) * 0.01f;
                foreach (GameObject go in made) if (go.name == "HuntBench") go.transform.position += new Vector3(wobble, 0f, 0f);
            }
            else if (mode == "rewrite" || mode == "rewritecheck")
            {
                for (int i = 0; i < made.Count; i++)
                {
                    Transform t = made[i].transform;
                    Vector3 at = positions[i];
                    if (mode == "rewrite" || t.localPosition != at) t.localPosition = at;
                }
            }
            else if (mode == "sprite")
            {
                foreach (GameObject go in made) go.GetComponent<SpriteRenderer>().sprite = sprites[0];
            }
            if (!measuring)
            {
                if (now - stepStart < 1f) return;
                measuring = true;
                stepStart = now;
                frames = 0;
                return;
            }
            frames++;
            if (now - stepStart < 3f) return;
            float ms = (now - stepStart) * 1000f / frames;
            Debug.Log("HUNT_BENCH " + mode + " " + Counts[step % Counts.Length] + " " + ms.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + " ms");
            foreach (GameObject go in made) Object.Destroy(go);
            made.Clear();
            positions.Clear();
            transforms.Clear();
            if (step + 1 >= Modes.Length * Counts.Length)
            {
                Debug.Log("HUNT_BENCH done");
                Application.onBeforeRender -= Frame;
                return;
            }
            Begin(step + 1, now);
        }

        static void Begin(int next, float now)
        {
            step = next;
            measuring = false;
            stepStart = now;
            string mode = Modes[step / Counts.Length];
            int count = Counts[step % Counts.Length];
            Camera camera = Camera.main;
            camera.useOcclusionCulling = mode != "nooc";
            camera.transparencySortMode = mode == "ortho" ? TransparencySortMode.Orthographic : TransparencySortMode.Default;
            Transform parent = null;
            if (mode == "movingparent")
            {
                var root = new GameObject("HuntBenchRoot");
                Object.DontDestroyOnLoad(root);
                made.Add(root);
                parent = root.transform;
            }
            float size = camera.orthographicSize * 2f / 68f;  // 8 of the 544 screen pixels
            for (int i = 0; i < count; i++)
            {
                var go = new GameObject("HuntBench");
                if (mode == "movingbare")
                {
                    go.transform.position = camera.ViewportToWorldPoint(new Vector3(Random.value, Random.value, 0f));
                    positions.Add(go.transform.localPosition);
                    transforms.Add(go.transform);
                    Object.DontDestroyOnLoad(go);
                    made.Add(go);
                    continue;
                }
                var renderer = go.AddComponent<SpriteRenderer>();
                if (mode == "movinghidden") renderer.enabled = false;
                renderer.sprite = sprites[mode == "alt" || mode == "alt0" ? i % 2 : 0];
                renderer.sortingLayerName = mode == "deflayer" ? "Default" : "Effects";
                renderer.sortingOrder = mode == "alt" || mode == "orders" ? 500 + i : 500;
                if (mode == "plain")
                {
                    renderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
                    renderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
                    renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    renderer.receiveShadows = false;
                    renderer.allowOcclusionWhenDynamic = false;
                    renderer.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
                }
                Vector3 position = camera.ViewportToWorldPoint(new Vector3(Random.value, Random.value, 0f));
                position.z = 0f;
                go.transform.position = position;
                go.transform.localScale = new Vector3(size, size, 1f);
                positions.Add(position);
                transforms.Add(go.transform);
                if (parent != null) go.transform.SetParent(parent, true);
                else Object.DontDestroyOnLoad(go);
                made.Add(go);
            }
        }

        static Sprite MakeSprite(Color color)
        {
            var texture = new Texture2D(8, 8, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point };
            var pixels = new Color[64];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = (i / 8 + i) % 3 == 0 ? Color.clear : color;
            texture.SetPixels(pixels);
            texture.Apply();
            return Sprite.Create(texture, new Rect(0, 0, 8, 8), new Vector2(0.5f, 0.5f), 8f, 0, SpriteMeshType.FullRect);
        }
    }
}

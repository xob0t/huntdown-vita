using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

// HuntCompat's start (Install: every module's defaults and flags) and the measurement probe, which is off
// unless a -hunt-* flag is given (on the PS Vita, in ux0:data/hunt/args.txt; see ProbeFiles). Each of
// Diag's own flags has a line in Help. Heartbeats go to the player log as "HEARTBEAT key=value ...", for
// tools to read.
// Part of HuntCompat; see Compat2018.cs.
namespace HuntCompat
{
    public class Diag : MonoBehaviour
    {
        static readonly System.Globalization.CultureInfo Inv = System.Globalization.CultureInfo.InvariantCulture;

        // The PS Vita's free memory readers (none on other players): name -> UnityEngine.PSVita.Diagnostics method.
        static readonly KeyValuePair<string, MethodInfo>[] FreeMemory = FindFreeMemory();

        static KeyValuePair<string, MethodInfo>[] FindFreeMemory()
        {
            Type diagnostics = AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetType("UnityEngine.PSVita.Diagnostics", false)).FirstOrDefault(t => t != null);
            if (diagnostics == null) return new KeyValuePair<string, MethodInfo>[0];
            return new[] { "LPDDR", "CDRAM", "PHYSCONT" }
                .Select(n => new KeyValuePair<string, MethodInfo>("free" + n, diagnostics.GetMethod("GetFreeMemory" + n, BindingFlags.Static | BindingFlags.Public)))
                .Where(p => p.Value != null).ToArray();
        }
        const string Help =
            "-hunt-scene <name>       load this scene (a build scene's name) 2 s after the first scene loaded\n" +
            "-hunt-heartbeat <s>      every s seconds, log frame times, GC and memory, visible renderers and lights\n" +
            "-hunt-quit-after <s>     quit s seconds after the probe starts\n" +
            "-hunt-shot <s> <path>    save a screenshot s seconds after the last scene load\n" +
            "-hunt-shots <s> <prefix> [after]  a screenshot every s seconds, <prefix>-<seconds>.png, from after seconds on (default 0),\n" +
            "                         for what a run shows over time (each takes ~0.6 s on the PS Vita)\n" +
            "-hunt-no-vsync           vSyncCount 0 and no frame cap, for unsynchronized frame times\n" +
            "-hunt-visible-top        with -hunt-heartbeat, the visible renderers by name and sorting layer (HUNT_VISIBLE), and\n" +
            "                         the screen they cover by name, layer and shader (HUNT_COVER; cover= is the sum)\n" +
            "-hunt-input-log          each joystick button Unity reports pressed or released (HUNT_INPUT)\n" +
            "-hunt-mute               no sound (AudioListener.volume 0, kept there), for test runs on a PC in use\n" +
            "-hunt-stall-dump <s>     crash the player when a frame has taken s seconds, for a core dump with the main thread's stack\n" +
            "-hunt-stall-after <t>    with -hunt-stall-dump, only frames that start t seconds or more after startup (skips earlier stalls)\n" +
            "-hunt-stall-from render  with -hunt-stall-dump, time from just before the engine renders (Application.onBeforeRender), not\n" +
            "                         from Diag's Update: a dump some ms later samples the rendering, not the next frame's scripts\n" +
            "-hunt-census <s>         every s seconds, a HUNT_CENSUS line: the loaded textures, meshes, sprites, clips, materials,\n" +
            "                         GameObjects and AssetBundles, counted (and the textures' and meshes' sizes), runtime-made\n" +
            "                         textures (no name) apart, for what leaks across restarts (takes some ms each time)\n" +
            "-hunt-find <regex> <s>   s seconds after each scene load, log every renderer (active or not) whose hierarchy path matches\n" +
            "                         the regex: active, enabled, isVisible, bounds, mesh, materials and shaders (HUNT_FIND), for\n" +
            "                         what looks missing in play";

        static string[] args;
        static string sceneToLoad;
        static float heartbeat, quitAfter = -1f, shotAfter = -1f;
        static string shotPath, shotsPrefix;
        static float shotsEvery = -1f, lastShots, shotsAfter;
        static bool noVsync, mute, visibleTop, inputLog;
        static System.Text.RegularExpressions.Regex findPattern;
        static float findAfter = -1f;
        static float stallDump = -1f, stallAfter;
        static float census = -1f, lastCensus;
        static volatile float frameStartTime;  // realtimeSinceStartup at frameStartTicks
        static readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        static long frameStartTicks;

        float started, lastBeat, lastLoad, worst;
        int frames, collections;
        long allocated;
        double frameSum;
        bool shotTaken, sceneRequested, found;

        // HuntCompat's start: every module's defaults (the PS Vita's among them) and flags, then the probe's
        // own. Each module on its own: one that throws (a flag it can't read) is logged and the rest go on.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Install()
        {
            args = ProbeFiles.Args;
            Guard("Clock", Clock.Install);
            Guard("Preload", Preload.Start);  // the menus' sheets read ahead (Preload.cs; PS Vita default)
            Guard("Gc", Gc.Install);  // the garbage collector held for safe points (Gc.cs; PS Vita default)
            Guard("Phases", Phases.Install);  // -hunt-phases: the player loop's steps timed (Phases.cs)
            Guard("RewiredTrim", RewiredTrim.Install);  // -hunt-rewired: Rewired's keyboard and mouse (RewiredTrim.cs)
            Guard("SpriteBench", SpriteBench.Install);  // -hunt-sprite-bench: what a sprite costs (SpriteBench.cs)
            // -hunt-load-priority low|belownormal|normal|high: the main thread's time per frame for finishing
            // asynchronous loads (Application.backgroundLoadingPriority; an experiment, the game's own unless given).
            Guard("LoadPriority", a =>
            {
                string priority = HuntFlags.Value(a, "-hunt-load-priority");
                if (priority == null) return;
                Application.backgroundLoadingPriority = priority == "low" ? ThreadPriority.Low : priority == "belownormal" ? ThreadPriority.BelowNormal
                    : priority == "high" ? ThreadPriority.High : ThreadPriority.Normal;
                Debug.Log("HUNT_DIAG backgroundLoadingPriority " + Application.backgroundLoadingPriority);
            });
            // Who ends the player, with the stack of the call: a quit the logs would otherwise not explain.
            Application.quitting += () => Debug.Log("HUNT_QUIT at " + Time.realtimeSinceStartup.ToString("F2", Inv) + " s, frame " +
                                                    Time.frameCount + "\n" + Environment.StackTrace);
            AppDomain.CurrentDomain.UnhandledException += (sender, e) => Debug.Log("HUNT_UNHANDLED " + e.ExceptionObject);
            if (!args.Any(a => a.StartsWith("-hunt-"))) return;
            sceneToLoad = HuntFlags.Value(args, "-hunt-scene");
            heartbeat = HuntFlags.Number(args, "-hunt-heartbeat", 0f);
            quitAfter = HuntFlags.Number(args, "-hunt-quit-after", -1f);
            shotAfter = HuntFlags.Number(args, "-hunt-shot", -1f);
            shotPath = HuntFlags.Word(args, "-hunt-shot", 2);
            shotsEvery = HuntFlags.Number(args, "-hunt-shots", -1f);
            shotsPrefix = HuntFlags.Word(args, "-hunt-shots", 2);
            shotsAfter = HuntFlags.NumberAt(args, "-hunt-shots", 3, 0f);
            noVsync = HuntFlags.Has(args, "-hunt-no-vsync");
            visibleTop = HuntFlags.Has(args, "-hunt-visible-top");
            inputLog = HuntFlags.Has(args, "-hunt-input-log");
            mute = HuntFlags.Has(args, "-hunt-mute");
            if (mute) AudioListener.volume = 0f;
            string find = HuntFlags.Value(args, "-hunt-find");
            if (find != null)
            {
                findPattern = new System.Text.RegularExpressions.Regex(find);
                findAfter = HuntFlags.NumberAt(args, "-hunt-find", 2, 5f);
            }
            census = HuntFlags.Number(args, "-hunt-census", -1f);
            stallDump = HuntFlags.Number(args, "-hunt-stall-dump", -1f);
            stallAfter = HuntFlags.Number(args, "-hunt-stall-after", 0f);
            if (stallDump > 0f) new System.Threading.Thread(WatchForStall) { IsBackground = true, Name = "HuntStallWatch" }.Start();
            stallFromRender = HuntFlags.Value(args, "-hunt-stall-from") == "render";
            if (stallFromRender) Application.onBeforeRender += MarkFrameStart;
            if (HuntFlags.Has(args, "-hunt-help")) Debug.Log("HUNT_DIAG flags:\n" + Help);
            var probe = new GameObject("HuntDiag");
            DontDestroyOnLoad(probe);
            probe.AddComponent<Diag>();
        }

        internal static void Guard(string module, Action install)
        {
            try
            {
                install();
            }
            catch (Exception exception)
            {
                Debug.LogError("HUNT_INSTALL_FAIL " + module + ": " + exception);
            }
        }

        static void Guard(string module, Action<string[]> install)
        {
            Guard(module, () => install(args));
        }

        // -hunt-stall-dump: a frame that never ends in the logs (the PS Vita's 40-60 s frames after a
        // scene load) can't say what it spends its time on, and the console has no sampling profiler.
        // Crashing the player mid-frame leaves a core dump with every thread's stack, the main
        // thread's among them (vita.py crashes --pull --thread main).
        static void WatchForStall()
        {
            while (true)
            {
                System.Threading.Thread.Sleep(Math.Max(10, Math.Min(250, (int)(stallDump * 250f))));  // a quarter of the limit: short stalls too
                long started = System.Threading.Interlocked.Read(ref frameStartTicks);
                if (started == 0) continue;
                double seconds = (clock.ElapsedTicks - started) / (double)System.Diagnostics.Stopwatch.Frequency;
                if (seconds < stallDump || frameStartTime < stallAfter) continue;
                Debug.Log("HUNT_STALL_DUMP a frame has taken " + seconds.ToString("F1") + " s: crashing for a dump");
                ProbeFiles.Flush(3000);  // the queued lines, before the crash loses them
                System.Runtime.InteropServices.Marshal.WriteInt32(new IntPtr(16), 0);  // unmapped: a data abort
                Environment.FailFast("HuntCompat stall dump");
            }
        }

        void Start()
        {
            started = lastBeat = lastLoad = Time.realtimeSinceStartup;
            collections = GC.CollectionCount(0);
            allocated = Gc.Allocated();
            SceneManager.sceneLoaded += (scene, mode) =>
            {
                lastLoad = Time.realtimeSinceStartup;
                shotTaken = false;
                found = false;
                Debug.Log("HUNT_DIAG scene loaded " + scene.name + " (" + mode + ") at " + lastLoad.ToString("F2", Inv) + " s");
            };
            Debug.Log("HUNT_DIAG started: " + string.Join(" ", args.Where(a => a.StartsWith("-hunt-")).ToArray()));
        }

        static float lastUpdateAt = -1f;
        static Diag probe;

        // A screenshot of the frame as shown (-hunt-shot, -hunt-pad-press's shot, -hunt-ab). On the PS Vita
        // ScreenCapture writes nothing (and vitacompanion's screenshot can run out of memory), so there the
        // frame is read back at its end and saved as a PNG (960x544 RGB, about 1.5 MB while it is made).
        internal static void Shot(string path)
        {
            if (Application.platform != RuntimePlatform.PSP2 || probe == null) ScreenCapture.CaptureScreenshot(path);
            else probe.StartCoroutine(SaveFrame(path));
        }

        static System.Collections.IEnumerator SaveFrame(string path)
        {
            yield return new WaitForEndOfFrame();
            var picture = new Texture2D(Screen.width, Screen.height, TextureFormat.RGB24, false);
            try
            {
                picture.ReadPixels(new Rect(0, 0, Screen.width, Screen.height), 0, 0, false);
                picture.Apply(false);
                System.IO.File.WriteAllBytes(path, picture.EncodeToPNG());
            }
            catch (Exception exception)
            {
                Debug.Log("HUNT_DIAG screenshot " + path + " failed: " + exception.Message);
            }
            Destroy(picture);
        }

        void Awake()
        {
            probe = this;
        }

        static bool stallFromRender;

        static void MarkFrameStart()
        {
            frameStartTime = Time.realtimeSinceStartup;
            System.Threading.Interlocked.Exchange(ref frameStartTicks, clock.ElapsedTicks);
        }

        void Update()
        {
            if (!stallFromRender) MarkFrameStart();
            if (mute) AudioListener.volume = 0f;  // the game sets it from its sound settings
            if (noVsync)
            {
                QualitySettings.vSyncCount = 0;
                Application.targetFrameRate = -1;
            }
            float now = Time.realtimeSinceStartup;
            if (inputLog) LogInput();
            if (sceneToLoad != null && !sceneRequested && now - lastLoad >= 2f)
            {
                sceneRequested = true;
                Debug.Log("HUNT_DIAG loading " + sceneToLoad);
                SceneManager.LoadScene(sceneToLoad);
            }
            // By the wall clock: Time.unscaledDeltaTime is clamped to maximumDeltaTime (-hunt-physics-cap).
            float delta = lastUpdateAt >= 0f ? (now - lastUpdateAt) * 1000f : Time.unscaledDeltaTime * 1000f;
            lastUpdateAt = now;
            frames++;
            frameSum += delta;
            if (delta > worst) worst = delta;
            if (heartbeat > 0f && now - lastBeat >= heartbeat) Beat(now);
            if (census > 0f && now - lastCensus >= census)
            {
                lastCensus = now;
                Census(now - started);
            }
            if (shotsEvery > 0f && shotsPrefix != null && now - started >= shotsAfter && now - lastShots >= shotsEvery)
            {
                lastShots = now;
                string path = shotsPrefix + "-" + ((int)(now - started)).ToString("D4") + ".png";
                Shot(path);
                Debug.Log("HUNT_DIAG screenshot " + path);
            }
            if (shotAfter >= 0f && !shotTaken && shotPath != null && now - lastLoad >= shotAfter)
            {
                shotTaken = true;
                Shot(shotPath);
                Debug.Log("HUNT_DIAG screenshot " + shotPath);
            }
            if (findPattern != null && !found && now - lastLoad >= findAfter)
            {
                found = true;
                FindRenderers();
            }
            if (quitAfter >= 0f && now - started >= quitAfter)
            {
                Debug.Log("HUNT_DIAG quitting");
                Application.Quit();
            }
        }

        // -hunt-find: the renderers whose hierarchy path matches, whatever their state.
        static void FindRenderers()
        {
            int count = 0;
            foreach (Renderer renderer in Resources.FindObjectsOfTypeAll<Renderer>())
            {
                if (!renderer.gameObject.scene.IsValid()) continue;  // assets and prefabs
                string path = Hierarchy.Full(renderer.transform);
                if (!findPattern.IsMatch(path)) continue;
                count++;
                var line = new StringBuilder("HUNT_FIND ").Append(path).Append(" [").Append(renderer.gameObject.scene.name).Append("]")
                    .Append(" active=").Append(renderer.gameObject.activeInHierarchy ? 1 : 0)
                    .Append(" enabled=").Append(renderer.enabled ? 1 : 0)
                    .Append(" visible=").Append(renderer.isVisible ? 1 : 0)
                    .Append(" layer=").Append(renderer.gameObject.layer)
                    .Append(" bounds=").Append(renderer.bounds.center.ToString("F2")).Append(renderer.bounds.size.ToString("F2"));
                var spriteRenderer = renderer as SpriteRenderer;
                if (spriteRenderer != null)
                {
                    Sprite sprite = spriteRenderer.sprite;
                    line.Append(" sprite=").Append(sprite == null ? "none" : sprite.name + "@" + (sprite.texture == null ? "?" : sprite.texture.name) +
                        sprite.rect.ToString("F0") + " verts=" + sprite.vertices.Length)
                        .Append(" draw=").Append(spriteRenderer.drawMode).Append(" size=").Append(spriteRenderer.size.ToString("F1"))
                        .Append(" scale=").Append(renderer.transform.lossyScale.ToString("F2"));
                }
                MeshFilter filter = renderer.GetComponent<MeshFilter>();
                if (filter != null)
                {
                    Mesh mesh = filter.sharedMesh;
                    line.Append(" mesh=").Append(mesh == null ? "none" : mesh.name + "(" + mesh.vertexCount + "v," + mesh.subMeshCount + "sub)");
                }
                foreach (Material material in renderer.sharedMaterials)
                    line.Append(" mat=").Append(material == null ? "none" : material.name + "/" + (material.shader == null ? "none" : material.shader.name)
                        + "/q" + material.renderQueue + (material.HasProperty("_Color") ? "/" + material.color.ToString("F2") : ""));
                Debug.Log(line.ToString());
            }
            Debug.Log("HUNT_FIND " + count + " renderers match " + findPattern);
        }

        // -hunt-input-log: each joystick button Unity reports pressed or released (KeyCode.JoystickButton0-19:
        // Unity's own numbering), and each of the game's Rewired actions player 1 starts (Rewired_Core, read by
        // reflection: HuntCompat doesn't reference it), as HUNT_INPUT lines.
        static readonly string[] Actions = { "Jump", "Fire", "Fire2", "Action", "Back", "Start", "Dash", "B8", "B9", "B10" };
        static MethodInfo rewiredButtonDown;
        static object rewiredPlayer;

        static void LogInput()
        {
            for (int i = 0; i < 20; i++)
            {
                var key = (KeyCode)((int)KeyCode.JoystickButton0 + i);
                if (Input.GetKeyDown(key)) Debug.Log("HUNT_INPUT down " + key);
                if (Input.GetKeyUp(key)) Debug.Log("HUNT_INPUT up " + key);
            }
            if (RewiredDirect.Answers(0))
            {
                // Rewired's frame is skipped (compat/RewiredDirect.cs): its actions as the game gets them.
                foreach (string action in Actions)
                    if (RewiredDirect.ButtonDown(0, action)) Debug.Log("HUNT_INPUT action " + action);
                return;
            }
            if (rewiredPlayer == null)
            {
                Type reInput = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("Rewired.ReInput", false)).FirstOrDefault(t => t != null);
                if (reInput == null || !(bool)reInput.GetProperty("isReady").GetValue(null, null)) return;
                object players = reInput.GetProperty("players").GetValue(null, null);
                rewiredPlayer = players.GetType().GetMethod("GetPlayer", new[] { typeof(int) }).Invoke(players, new object[] { 0 });
                rewiredButtonDown = rewiredPlayer.GetType().GetMethod("GetButtonDown", new[] { typeof(string) });
            }
            foreach (string action in Actions)
                if ((bool)rewiredButtonDown.Invoke(rewiredPlayer, new object[] { action })) Debug.Log("HUNT_INPUT action " + action);
        }

        // -hunt-census: what is loaded, counted. Textures by their pixels (MB at 4 bytes a pixel, what the
        // game's uncompressed RGBA32 sheets take), the ones with no name (made at run time: the game's text)
        // apart; meshes by their vertices.
        static void Census(float t)
        {
            int textures = 0, unnamed = 0;
            double pixels = 0, unnamedPixels = 0;
            var byName = new Dictionary<string, int>();
            foreach (Texture2D texture in Resources.FindObjectsOfTypeAll<Texture2D>())
            {
                textures++;
                double size = (double)texture.width * texture.height;
                pixels += size;
                if (!string.IsNullOrEmpty(texture.name))
                {
                    string key = texture.name + "(" + texture.width + "x" + texture.height + ")";
                    int seen;
                    byName.TryGetValue(key, out seen);
                    byName[key] = seen + 1;
                }
                if (string.IsNullOrEmpty(texture.name))
                {
                    unnamed++;
                    unnamedPixels += size;
                }
            }
            int meshes = 0;
            long vertices = 0;
            foreach (Mesh mesh in Resources.FindObjectsOfTypeAll<Mesh>())
            {
                meshes++;
                vertices += mesh.vertexCount;
            }
            int renderTextures = Resources.FindObjectsOfTypeAll<RenderTexture>().Length;
            int sprites = Resources.FindObjectsOfTypeAll<Sprite>().Length;
            int clips = 0, loadedClips = 0;
            foreach (AudioClip clip in Resources.FindObjectsOfTypeAll<AudioClip>())
            {
                clips++;
                if (clip.loadState == AudioDataLoadState.Loaded) loadedClips++;
            }
            int materials = Resources.FindObjectsOfTypeAll<Material>().Length;
            int objects = Resources.FindObjectsOfTypeAll<GameObject>().Length;
            int bundles = Resources.FindObjectsOfTypeAll<AssetBundle>().Length;
            Debug.Log(new StringBuilder("HUNT_CENSUS t=").Append(t.ToString("F1", Inv))
                .Append(" textures=").Append(textures).Append(" textureMB=").Append((pixels * 4 / 1048576.0).ToString("F1", Inv))
                .Append(" unnamedTextures=").Append(unnamed).Append(" unnamedMB=").Append((unnamedPixels * 4 / 1048576.0).ToString("F2", Inv))
                .Append(" renderTextures=").Append(renderTextures)
                .Append(" meshes=").Append(meshes).Append(" meshVerts=").Append(vertices)
                .Append(" sprites=").Append(sprites).Append(" clips=").Append(clips).Append('/').Append(loadedClips)
                .Append(" materials=").Append(materials).Append(" gameObjects=").Append(objects).Append(" bundles=").Append(bundles).ToString());
            // Textures loaded more than once under one name: a sheet read again while its old copy stays.
            var twice = byName.Where(p => p.Value > 1).OrderByDescending(p => p.Value).Take(12).ToArray();
            if (twice.Length > 0)
                Debug.Log("HUNT_CENSUS_DUP " + string.Join(" ", twice.Select(p => p.Key + "=" + p.Value).ToArray()));
        }

        // The share of the screen a world-space box covers, clipped to the screen (1: all of it).
        static float ScreenShare(Camera view, Bounds bounds)
        {
            Vector3 a = view.WorldToScreenPoint(bounds.min), b = view.WorldToScreenPoint(bounds.max);
            float width = Mathf.Min(Mathf.Max(a.x, b.x), Screen.width) - Mathf.Max(Mathf.Min(a.x, b.x), 0f);
            float height = Mathf.Min(Mathf.Max(a.y, b.y), Screen.height) - Mathf.Max(Mathf.Min(a.y, b.y), 0f);
            return width <= 0f || height <= 0f ? 0f : width * height / ((float)Screen.width * Screen.height);
        }

        void Beat(float now)
        {
            int gc = GC.CollectionCount(0);
            int visible = 0, renderers = 0, lit = 0;
            var lights = new Dictionary<LightType, int>();
            foreach (Light light in FindObjectsOfType<Light>())
            {
                if (!light.enabled || light.intensity <= 0f || !light.gameObject.activeInHierarchy) continue;
                lit++;
                int count;
                lights.TryGetValue(light.type, out count);
                lights[light.type] = count + 1;
            }
            var visibleByName = visibleTop ? new Dictionary<string, int>() : null;
            var coverByName = visibleTop ? new Dictionary<string, float>() : null;
            // Screen coverage: the visible renderers' screen areas (their bounds, clipped to the screen) over
            // the screen's, summed: how many times the GPU fills the screen with them, its fill-rate load.
            Camera view = Camera.main ?? (Camera.allCamerasCount > 0 ? Camera.allCameras[0] : null);
            float cover = 0f;
            foreach (Renderer renderer in FindObjectsOfType<Renderer>())
            {
                if (!renderer.enabled) continue;
                renderers++;
                if (!renderer.isVisible) continue;
                visible++;
                float area = view == null ? 0f : ScreenShare(view, renderer.bounds);
                cover += area;
                if (coverByName != null && area > 0f)
                {
                    Material material = renderer.sharedMaterial;
                    string coverKey = new string(renderer.name.Where(c => !char.IsDigit(c)).ToArray()).Replace(' ', '_') + "@" +
                                      renderer.sortingLayerName + "/" + (material == null || material.shader == null ? "none" : material.shader.name.Replace(' ', '_'));
                    float sum;
                    coverByName.TryGetValue(coverKey, out sum);
                    coverByName[coverKey] = sum + area;
                }
                if (visibleByName == null) continue;
                // By name, digits dropped ("Enemy12" and "Enemy3" together), with the sorting layer.
                string key = new string(renderer.name.Where(c => !char.IsDigit(c)).ToArray()).Replace(' ', '_') + "@" + renderer.sortingLayerName;
                int n;
                visibleByName.TryGetValue(key, out n);
                visibleByName[key] = n + 1;
            }
            var line = new StringBuilder("HEARTBEAT");
            line.Append(" t=").Append((now - started).ToString("F1", Inv));
            line.Append(" scene=").Append(SceneManager.GetActiveScene().name.Replace(' ', '_'));
            line.Append(" fps=").Append((frames / (now - lastBeat)).ToString("F1", Inv));
            line.Append(" ms=").Append((frameSum / Math.Max(frames, 1)).ToString("F2", Inv));
            line.Append(" worst=").Append(worst.ToString("F1", Inv));
            line.Append(" cover=").Append(cover.ToString("F2", Inv));
            line.Append(" gc=").Append(gc - collections);
            line.Append(" heapMB=").Append((GC.GetTotalMemory(false) / 1048576.0).ToString("F1", Inv));
            // Allocated per second, the heartbeat's own lines among it (some KB; on the PS Vita, Gc.Allocated).
            long nowAllocated = Gc.Allocated();
            line.Append(" allocKBs=").Append(((nowAllocated - allocated) / 1024.0 / (now - lastBeat)).ToString("F0", Inv));
            // The PS Vita's free memory (UnityEngine.PSVita.Diagnostics, kept by link.xml; read by reflection, as
            // HuntCompat is built for Windows too): main memory, video memory (CDRAM) and physically contiguous.
            foreach (KeyValuePair<string, MethodInfo> free in FreeMemory)
                line.Append(' ').Append(free.Key).Append("MB=").Append((Convert.ToInt64(free.Value.Invoke(null, null)) / 1048576.0).ToString("F1", Inv));
            line.Append(" renderers=").Append(renderers).Append(" visible=").Append(visible);
            // The sources Unity's audio update goes through each frame (AudioManager::Update): all, enabled, playing.
            int sources = 0, enabledSources = 0, playing = 0;
            foreach (AudioSource source in FindObjectsOfType<AudioSource>())
            {
                sources++;
                if (!source.enabled) continue;
                enabledSources++;
                if (source.isPlaying) playing++;
            }
            line.Append(" audioSources=").Append(sources).Append('/').Append(enabledSources).Append('/').Append(playing);
            line.Append(" lights=").Append(lit);
            foreach (KeyValuePair<LightType, int> pair in lights) line.Append(' ').Append(pair.Key).Append('=').Append(pair.Value);
            Debug.Log(line.ToString());
            if (visibleByName != null)
                Debug.Log("HUNT_VISIBLE " + string.Join(" ", visibleByName.OrderByDescending(p => p.Value).Take(16).Select(p => p.Key + "=" + p.Value).ToArray()));
            if (coverByName != null)
                Debug.Log("HUNT_COVER " + string.Join(" ", coverByName.OrderByDescending(p => p.Value).Take(16)
                    .Select(p => p.Key + "=" + p.Value.ToString("F2", Inv)).ToArray()));
            string profile = Prof.Report(frames);  // a HUNT_PROF=1 build's timings (compat/Prof.cs)
            if (profile != null) Debug.Log(profile);
            string phases = Phases.Report(frames);  // -hunt-phases (compat/Phases.cs)
            if (phases != null) Debug.Log(phases);
            lastBeat = now;
            frames = 0;
            frameSum = 0;
            worst = 0f;
            collections = gc;
            allocated = Gc.Allocated();
        }
    }

}

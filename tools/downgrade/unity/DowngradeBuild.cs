using System;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

// Batch-mode entry point for the downgrade pipeline (pipeline.py build). Builds the scenes listed in
// the recovered EditorBuildSettings.
//   -buildOutput <path>           player executable (Windows) or folder (Vita)
//   -buildTarget Win64|PSP2       Unity's flag; PSP2 needs Unity's PS Vita build module and Sony's SDK
//   -scriptingBackend mono|il2cpp Windows only; the Vita target is IL2CPP only
//   -huntScenes <a;b;...>         a test build of only these scenes (file names or paths), with the first
//                                 scene of the build settings, which the player starts
//   -developmentBuild             Unity's development player
//   -codeStage <dir>              PS Vita: save the linker's and IL2CPP's inputs there for quickcode
//   -vitaTitleId <id>             PS Vita: the app's title ID (pipeline.VITA_TITLE_ID)
// The build switches (HUNT_* variables, common.BUILD_SWITCHES; BuildUtil.Switch) choose what the scripts
// here do to the scenes the build writes.
public static class DowngradeBuild
{
    // Homebrew title: XXYYYY-<9 char title ID>_00-<16 char label>.
    const string VitaContentId = "IV0000-{0}_00-HUNTDOWNVITA0000";

    public static void Build()
    {
        string[] args = Environment.GetCommandLineArgs();
        string output = Argument(args, "-buildOutput");
        if (output == null) throw new ArgumentException("Missing -buildOutput <path>");
        string requested = Argument(args, "-buildTarget"); // Unity's own flag; it also selects the import target
        if (requested != "PSP2" && requested != "Win64") throw new ArgumentException("Unsupported -buildTarget " + requested);
        bool vita = requested == "PSP2";
        bool il2cpp = vita || Argument(args, "-scriptingBackend") == "il2cpp";
        string[] scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray();
        // pipeline.py build --scenes: a test build of some scenes. The first stays: the player starts it.
        string only = Argument(args, "-huntScenes");
        if (only != null)
        {
            string[] names = only.Split(';').Select(n => n.Trim()).Where(n => n.Length > 0).ToArray();
            string[] unknown = names.Where(n => !scenes.Any(s => IsScene(s, n))).ToArray();
            if (unknown.Length > 0)
            {
                Debug.LogError("DOWNGRADE_BUILD Failed: no scene " + string.Join(", ", unknown) + " in the build settings");
                EditorApplication.Exit(2);
                return;
            }
            scenes = scenes.Where((s, i) => i == 0 || names.Any(n => IsScene(s, n))).ToArray();
            Debug.Log("DOWNGRADE_SCENES test build of " + string.Join(", ", scenes));
        }

        BuildTarget target = BuildTarget.StandaloneWindows64;
        BuildTargetGroup group = BuildTargetGroup.Standalone;
        if (vita)
        {
            target = BuildTarget.PSP2;
            group = BuildTargetGroup.PSP2;
            if (!BuildPipeline.IsBuildTargetSupported(group, target) ||
                !EditorUserBuildSettings.SwitchActiveBuildTarget(group, target))
            {
                Debug.LogError("DOWNGRADE_BUILD Failed: PS Vita build support is not installed for this editor");
                EditorApplication.Exit(2);
                return;
            }
            string titleId = Argument(args, "-vitaTitleId");
            if (titleId == null || titleId.Length != 9) throw new ArgumentException("Missing -vitaTitleId <9 characters>");
            EditorUserBuildSettings.psp2BuildSubtarget = PSP2BuildSubtarget.PCHosted;
            PlayerSettings.PSVita.contentID = string.Format(VitaContentId, titleId);
            PlayerSettings.PSVita.shortTitle = "Huntdown";
            // The Vita module calls IL2CPP without null and array bounds checks, so a null
            // reference or bad index the PC game survives as a logged exception crashes the
            // player instead. Keep the exception behaviour of the source version (pipeline.IL2CPP_CHECKS,
            // which link runs IL2CPP with: keep the two in step).
            PlayerSettings.SetAdditionalIl2CppArgs("--emit-null-checks --enable-array-bounds-check");
            // The largest application memory budget; the game's art is sized for PCs.
            PlayerSettings.PSVita.memoryExpansionMode = PlayerSettings.PSVita.PSVitaMemoryExpansionMode.ExpandBy109MB;
        }
        PlayerSettings.SetScriptingBackend(group, il2cpp ? ScriptingImplementation.IL2CPP : ScriptingImplementation.Mono2x);
        // The source game shows no Unity splash screen, and the project keeps that setting, but
        // the editor turns the splash back on when it loads a project under a license that
        // cannot hide it. Set it again, and log what the editor kept.
        PlayerSettings.SplashScreen.show = false;
        PlayerSettings.SplashScreen.showUnityLogo = false;
        Debug.Log("DOWNGRADE_SPLASH hasPro=" + UnityEditorInternal.InternalEditorUtility.HasPro() +
                  " show=" + PlayerSettings.SplashScreen.show + " showUnityLogo=" + PlayerSettings.SplashScreen.showUnityLogo);

        // The game's AssetBundles, made for this target before the player (bundle_rules.py has why).
        string bundles = BuildBundles(target);
        if (bundles == null)
        {
            EditorApplication.Exit(1);
            return;
        }

        // pipeline.py build --development: the development player, for the engine's profiler markers.
        BuildOptions options = args.Contains("-developmentBuild") ? BuildOptions.Development : BuildOptions.None;
        string codeStage = vita ? Argument(args, "-codeStage") : null;
        if (codeStage != null && System.IO.Directory.Exists(codeStage)) System.IO.Directory.Delete(codeStage, true);
        var watcher = codeStage == null ? null : new System.Threading.Thread(() => WatchStagedAssemblies(codeStage)) { IsBackground = true };
        if (watcher != null) watcher.Start();
        BuildReport report = BuildPipeline.BuildPlayer(scenes, output, target, options);
        stopWatching = true;
        if (watcher != null) watcher.Join();
        Debug.Log("DOWNGRADE_BUILD " + report.summary.result + " errors=" + report.summary.totalErrors +
                  " development=" + (options == BuildOptions.Development) +
                  " target=" + target + " backend=" + (il2cpp ? "il2cpp" : "mono") +
                  " scenes=" + scenes.Length + " size=" + report.summary.totalSize);
        // How long each step took (the scenes, the shared assets, IL2CPP), for pipeline.py's summary.
        foreach (BuildStep step in report.steps)
            Debug.Log("DOWNGRADE_STEP " + step.duration.TotalSeconds.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) +
                      " " + step.depth + " " + step.name);
        // Build report messages do not all reach the editor log; keep them next to it.
        var messages = report.steps.SelectMany(s => s.messages.Select(m => m.type + "\t" + s.name + "\t" + m.content))
            .Where(m => !m.StartsWith("Log\t"));
        System.IO.File.WriteAllLines(output.TrimEnd('/', System.IO.Path.DirectorySeparatorChar) + ".messages.txt", messages.ToArray());
        if (codeStage != null && report.summary.result == BuildResult.Succeeded) SaveCodeStage(codeStage);
        if (report.summary.result == BuildResult.Succeeded) CopyBundles(bundles, StreamingAssetsOf(output, vita));
        EditorApplication.Exit(report.summary.result == BuildResult.Succeeded ? 0 : 1);
    }

    // The bundles go to a folder of the project's outside Assets (so the editor doesn't import them), one per
    // target; BuildAssetBundles rebuilds only what changed there. Each asset's bundle is its importer's
    // assetBundleName, as the export wrote it. LZ4, as the game's own: a bundle is read in chunks as its
    // assets load, not unpacked whole into memory.
    const string BundleFolder = "HuntBundles";

    static string BuildBundles(BuildTarget target)
    {
        string folder = System.IO.Path.Combine(BundleFolder, target.ToString());
        System.IO.Directory.CreateDirectory(folder);
        var started = DateTime.Now;
        AssetBundleManifest manifest = BuildPipeline.BuildAssetBundles(folder,
            BuildAssetBundleOptions.ChunkBasedCompression | BuildAssetBundleOptions.DeterministicAssetBundle, target);
        if (manifest == null)
        {
            Debug.LogError("DOWNGRADE_BUILD Failed: the AssetBundles did not build");
            return null;
        }
        string[] names = manifest.GetAllAssetBundles();
        // Bundles of an earlier build whose name no asset carries now.
        var current = new System.Collections.Generic.HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        foreach (string file in System.IO.Directory.GetFiles(folder, "*", System.IO.SearchOption.AllDirectories))
        {
            string name = file.Substring(folder.Length + 1).Replace(System.IO.Path.DirectorySeparatorChar, '/');
            if (name.EndsWith(".manifest") || name == target.ToString() || current.Contains(name)) continue;
            System.IO.File.Delete(file);
            Debug.Log("DOWNGRADE_BUNDLES removed stale " + name);
        }
        Debug.Log("DOWNGRADE_BUNDLES " + names.Length + " bundles for " + target + " in " +
                  (DateTime.Now - started).TotalSeconds.ToString("F0") + " s");
        return folder;
    }

    // The player's StreamingAssets: Huntdown_Data/StreamingAssets beside a Windows player's executable, and
    // Media/StreamingAssets in a PS Vita build's folder.
    static string StreamingAssetsOf(string output, bool vita)
    {
        if (vita) return System.IO.Path.Combine(output, System.IO.Path.Combine("Media", "StreamingAssets"));
        string data = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(output), System.IO.Path.GetFileNameWithoutExtension(output) + "_Data");
        return System.IO.Path.Combine(data, "StreamingAssets");
    }

    // The bundles into the player, under their names as paths (the game opens streamingAssetsPath + "/" + name);
    // not the .manifest files or the root bundle, which the game never reads.
    static void CopyBundles(string folder, string streaming)
    {
        string root = System.IO.Path.GetFileName(folder);
        int count = 0;
        long bytes = 0;
        foreach (string file in System.IO.Directory.GetFiles(folder, "*", System.IO.SearchOption.AllDirectories))
        {
            string name = file.Substring(folder.Length + 1);
            if (name.EndsWith(".manifest") || name == root) continue;
            string destination = System.IO.Path.Combine(streaming, name);
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(destination));
            System.IO.File.Copy(file, destination, true);
            count++;
            bytes += new System.IO.FileInfo(file).Length;
        }
        Debug.Log("DOWNGRADE_BUNDLES copied " + count + " bundles (" + (bytes >> 20) + " MB) to " + streaming);
    }

    // pipeline.py quickcode replays the build's UnityLinker and IL2CPP steps (their command lines are
    // in the editor log) on newly rewritten assemblies. Their inputs are in Temp, which the editor
    // deletes when it exits, under their paths relative to Temp in the code stage: the staged
    // assemblies, the link XMLs and the compiled ApplicationInfo.o.
    static volatile bool stopWatching;

    // The staged assemblies exist only inside the platform's post-process, between the editor
    // staging them and the linker replacing them with its output (from tempStrip); no build callback
    // runs there. So a thread copies Temp/StagingArea/Data/Managed whenever it changes, until the
    // linker's tempStrip appears.
    static void WatchStagedAssemblies(string stage)
    {
        string managed = System.IO.Path.GetFullPath(System.IO.Path.Combine("Temp", "StagingArea", "Data", "Managed"));
        string target = System.IO.Path.Combine(stage, "StagingArea", "Data", "Managed");
        string copied = null;
        while (!stopWatching)
        {
            System.Threading.Thread.Sleep(250);
            try
            {
                if (!System.IO.Directory.Exists(managed)) continue;
                // The linker has started: what was copied last is its input. (After it, the editor puts
                // the stripped assemblies in Managed, which are no linker input.)
                if (System.IO.Directory.Exists(System.IO.Path.Combine(managed, "tempStrip"))) return;
                string[] files = System.IO.Directory.GetFiles(managed, "*", System.IO.SearchOption.AllDirectories);
                string state = string.Join("|", files.Select(f => f + ":" + new System.IO.FileInfo(f).Length + ":" +
                                                                   System.IO.File.GetLastWriteTimeUtc(f).Ticks).ToArray());
                if (state == copied) continue;
                if (System.IO.Directory.Exists(target)) System.IO.Directory.Delete(target, true);
                foreach (string file in files)
                {
                    string destination = System.IO.Path.Combine(target, file.Substring(managed.Length + 1));
                    System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(destination));
                    System.IO.File.Copy(file, destination, true);
                }
                copied = state;
            }
            catch (Exception)
            {
                copied = null;  // a file being written or moved; copy again on the next look
            }
        }
    }

    static void SaveCodeStage(string stage)
    {
        string temp = System.IO.Path.GetFullPath("Temp");
        int count = 0;
        foreach (string file in System.IO.Directory.GetFiles(temp, "*", System.IO.SearchOption.AllDirectories))
        {
            string relative = file.Substring(temp.Length + 1);
            string[] parts = relative.Split(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
            bool linkXml = parts.Length == 3 && parts[0] == "StagingArea" && parts[1] == "Data" && relative.EndsWith(".xml");
            if (!linkXml && !relative.EndsWith(".o")) continue;
            string target = System.IO.Path.Combine(stage, relative);
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target));
            System.IO.File.Copy(file, target, true);
            count++;
        }
        string managed = System.IO.Path.Combine(stage, "StagingArea", "Data", "Managed");
        int assemblies = System.IO.Directory.Exists(managed) ? System.IO.Directory.GetFiles(managed, "*.dll", System.IO.SearchOption.AllDirectories).Length : 0;
        Debug.Log("DOWNGRADE_CODE_STAGE saved " + count + " files and " + assemblies + " staged assemblies to " + stage);
    }

    // Whether a scene path is the scene named (its file name, or its path), in any case.
    static bool IsScene(string path, string name)
    {
        return string.Equals(path, name, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(System.IO.Path.GetFileNameWithoutExtension(path), name, StringComparison.OrdinalIgnoreCase);
    }

    static string Argument(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}

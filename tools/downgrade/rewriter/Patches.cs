using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

// Single game methods that call HuntCompat rules: Rewired's platform, the analytics' login, the frame rate, the view size, Resources loads timed.
// Part of the assembly rewriter; see DowngradeAssemblies.cs.
partial class DowngradeAssemblies
{
    // Rewired's InputManager (compiled into the game's firstpass assembly) sets its platform from
    // the build target's defines, so the game's says Windows (Platform 1) and Mono. On the PS Vita
    // Rewired then matched the pad against the Windows entries of its controller database, found
    // nothing, and left it unmapped: no button worked, and the game kept its keyboard glyphs. Each
    // constant stored to platform or scriptingBackend goes through HuntCompat RewiredPlatform, which
    // answers PSVita and IL2CPP there; the database's PS Vita entries match the pad ("PS Vita").
    // Each field is its own rule: the scriptingBackend stores would hide a miss in the platform ones.
    static int PatchRewiredPlatform(ModuleDefinition module)
    {
        TypeDefinition type = GameType(module, "Rewired.InputManager");
        if (type == null) return 0;
        MethodDefinition detect = type.Methods.Single(m => m.Name == "DetectPlatform");
        int platforms = 0, backends = 0;
        foreach (Instruction store in detect.Body.Instructions.ToArray())
        {
            var field = store.Operand as FieldReference;
            if (store.OpCode != OpCodes.Stfld || field == null || (field.Name != "platform" && field.Name != "scriptingBackend")) continue;
            string name = field.Name == "platform" ? "Platform" : "ScriptingBackend";
            InsertBeforeRetarget(detect, store, Instruction.Create(OpCodes.Call, CompatMethod(module, "RewiredPlatform", name)));
            if (field.Name == "platform") platforms++;
            else backends++;
        }
        Count("rewired DetectPlatform platform", platforms);
        Count("rewired DetectPlatform scriptingBackend", backends);
        return platforms + backends;
    }

    // Analytics.LoginWith and LoginWithXbox (coroutines the game starts at launch) return HuntCompat
    // Offline.Nothing instead: no request goes to the developer's stats server, Analytics.isOK stays false,
    // and every other report, which checks it, is skipped (compat/Offline.cs).
    static int PatchAnalyticsLogin(ModuleDefinition module)
    {
        TypeDefinition type = GameType(module, "Analytics");
        if (type == null) return 0;
        int count = 0;
        foreach (MethodDefinition method in type.Methods.Where(m => m.Name == "LoginWith" || m.Name == "LoginWithXbox"))
        {
            var body = new MethodBody(method);
            method.Body = body;
            ILProcessor il = body.GetILProcessor();
            il.Emit(OpCodes.Call, CompatMethod(module, "Offline", "Nothing"));
            il.Emit(OpCodes.Ret);
            count++;
        }
        Count("analytics logins", count == 2 ? 1 : 0);
        return count;
    }

    // Main's frame rate: one fixed mode where HuntCompat FrameRate.Fixed says so (the PS Vita), see
    // compat/FrameRate.cs. Main.Start ends, at each return, with
    //   if (FrameRate.Fixed()) <apply FrameRate.StartFps(this.fps30)>
    // and Main.SetFPS(int) starts with
    //   if (FrameRate.Fixed()) { <apply FrameRate.Choice(inNr)>; return; }
    // where <apply fps> is: fpsAuto = false; fps30 = FrameRate.IsThirty(fps); rate = FrameRate.RateOf(fps);
    // if (game != null) game.fps30 = fps30; FrameRate.Display(fps); ResetFrameTimer().
    static int PatchFrameRate(ModuleDefinition module)
    {
        TypeDefinition main = GameType(module, "Main");
        if (main == null) return 0;
        TypeDefinition game = module.GetType("Game");
        FieldDefinition fpsAuto = main.Fields.Single(f => f.Name == "fpsAuto");
        FieldDefinition fps30 = main.Fields.Single(f => f.Name == "fps30");
        FieldDefinition rate = main.Fields.Single(f => f.Name == "rate");
        FieldDefinition gameField = main.Fields.Single(f => f.Name == "game");
        FieldDefinition gameFps30 = game.Fields.Single(f => f.Name == "fps30");
        MethodDefinition resetTimer = main.Methods.Single(m => m.Name == "ResetFrameTimer" && m.Parameters.Count == 0);
        MethodReference isFixed = CompatMethod(module, "FrameRate", "Fixed");

        // The mode's fields and display for the fps that pushFps leaves on the stack (called three times).
        Func<Func<Instruction[]>, Instruction[]> apply = pushFps =>
        {
            Instruction afterGame = Instruction.Create(OpCodes.Nop);
            var code = new System.Collections.Generic.List<Instruction>
            {
                Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Ldc_I4_0), Instruction.Create(OpCodes.Stfld, fpsAuto),
                Instruction.Create(OpCodes.Ldarg_0),
            };
            code.AddRange(pushFps());
            code.Add(Instruction.Create(OpCodes.Call, CompatMethod(module, "FrameRate", "IsThirty")));
            code.Add(Instruction.Create(OpCodes.Stfld, fps30));
            code.Add(Instruction.Create(OpCodes.Ldarg_0));
            code.AddRange(pushFps());
            code.Add(Instruction.Create(OpCodes.Call, CompatMethod(module, "FrameRate", "RateOf")));
            code.Add(Instruction.Create(OpCodes.Stfld, rate));
            code.Add(Instruction.Create(OpCodes.Ldarg_0));
            code.Add(Instruction.Create(OpCodes.Ldfld, gameField));
            code.Add(Instruction.Create(OpCodes.Brfalse, afterGame));
            code.Add(Instruction.Create(OpCodes.Ldarg_0));
            code.Add(Instruction.Create(OpCodes.Ldfld, gameField));
            code.Add(Instruction.Create(OpCodes.Ldarg_0));
            code.Add(Instruction.Create(OpCodes.Ldfld, fps30));
            code.Add(Instruction.Create(OpCodes.Stfld, gameFps30));
            code.Add(afterGame);
            code.AddRange(pushFps());
            code.Add(Instruction.Create(OpCodes.Call, CompatMethod(module, "FrameRate", "Display")));
            code.Add(Instruction.Create(OpCodes.Ldarg_0));
            code.Add(Instruction.Create(OpCodes.Call, resetTimer));
            return code.ToArray();
        };

        MethodDefinition start = main.Methods.Single(m => m.Name == "Start" && m.Parameters.Count == 0);
        int starts = BeforeRets(start, () =>
        {
            Instruction skip = Instruction.Create(OpCodes.Nop);
            var code = new System.Collections.Generic.List<Instruction> { Instruction.Create(OpCodes.Call, isFixed), Instruction.Create(OpCodes.Brfalse, skip) };
            code.AddRange(apply(() => new[]
            {
                Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Ldfld, fps30),
                Instruction.Create(OpCodes.Call, CompatMethod(module, "FrameRate", "StartFps")),
            }));
            code.Add(skip);
            return code.ToArray();
        });
        Count("frame rate Main.Start", starts);

        MethodDefinition setFps = main.Methods.Single(m => m.Name == "SetFPS" && m.Parameters.Count == 1);
        Instruction first = setFps.Body.Instructions[0];
        var head = new System.Collections.Generic.List<Instruction> { Instruction.Create(OpCodes.Call, isFixed), Instruction.Create(OpCodes.Brfalse, first) };
        head.AddRange(apply(() => new[]
        {
            Instruction.Create(OpCodes.Ldarg_1),
            Instruction.Create(OpCodes.Call, CompatMethod(module, "FrameRate", "Choice")),
        }));
        head.Add(Instruction.Create(OpCodes.Ret));
        Prepend(setFps, head.ToArray());
        Count("frame rate Main.SetFPS", 1);
        return starts + 1;
    }

    // Gfx.GetViewSize(width, height, inMaxView) starts with inMaxView |= HuntCompat View.Maximized(): the
    // game's maximized view, all 480x270 game pixels, on the PS Vita (compat/View.cs).
    static int PatchViewSize(ModuleDefinition module)
    {
        TypeDefinition gfx = GameType(module, "Gfx");
        if (gfx == null) return 0;
        MethodDefinition viewSize = gfx.Methods.Single(m => m.Name == "GetViewSize" && m.Parameters.Count == 3);
        ParameterDefinition maxView = viewSize.Parameters[2];
        Prepend(viewSize,
            Instruction.Create(OpCodes.Ldarg, maxView),
            Instruction.Create(OpCodes.Call, CompatMethod(module, "View", "Maximized")),
            Instruction.Create(OpCodes.Or),
            Instruction.Create(OpCodes.Starg, maxView));
        return 1;
    }

    // MainMenu.StartLoadLevel(inLevel) starts with inLevel = HuntCompat LevelJump.Level(inLevel): the test flag
    // -hunt-level loads another level than the one picked (compat/LevelJump.cs).
    static int PatchLevelJump(ModuleDefinition module)
    {
        TypeDefinition menu = GameType(module, "MainMenu");
        if (menu == null) return 0;
        MethodDefinition start = menu.Methods.Single(m =>
            m.Name == "StartLoadLevel" && m.Parameters.Count == 1 && m.Parameters[0].ParameterType.FullName == "System.String");
        ParameterDefinition level = start.Parameters[0];
        Prepend(start,
            Instruction.Create(OpCodes.Ldarg, level),
            Instruction.Create(OpCodes.Call, CompatMethod(module, "LevelJump", "Level")),
            Instruction.Create(OpCodes.Starg, level));
        return 1;
    }

    // FlyingObjects' constructor sets its debris pool's size (debrisIdleLength = 256); the value goes through
    // HuntCompat Debris.Pool first, which keeps fewer pieces on the PS Vita (compat/Debris.cs).
    static int PatchDebrisPool(ModuleDefinition module)
    {
        TypeDefinition flying = GameType(module, "FlyingObjects");
        if (flying == null) return 0;
        FieldDefinition length = flying.Fields.Single(f => f.Name == "debrisIdleLength");
        MethodReference pool = CompatMethod(module, "Debris", "Pool");
        int count = 0;
        foreach (MethodDefinition constructor in flying.Methods.Where(m => m.IsConstructor && m.HasBody))
        {
            ILProcessor il = constructor.Body.GetILProcessor();
            foreach (Instruction store in constructor.Body.Instructions
                         .Where(i => i.OpCode == OpCodes.Stfld && i.Operand is FieldReference && ((FieldReference)i.Operand).Resolve() == length).ToList())
            {
                il.InsertBefore(store, Instruction.Create(OpCodes.Call, pool));
                count++;
            }
        }
        if (count != 1) throw new InvalidOperationException("FlyingObjects: " + count + " stores of debrisIdleLength in its constructor, not 1");
        return count;
    }

    // Under the build switch HUNT_PROF=1 (common.BUILD_SWITCHES, a code switch), every void FrameEvent, Update,
    // LateUpdate and FixedUpdate of the game's classes runs between HuntCompat Prof.Enter and Prof.Exit (its
    // "Class.Method" name, the start): the start goes in a new local, and the Exit call goes before each ret,
    // where branches to the ret now land (BeforeRets; compat/Prof.cs). Off, nothing changes.
    static readonly HashSet<string> ProfiledMethods = new HashSet<string> { "FrameEvent", "Update", "LateUpdate", "FixedUpdate" };

    static int ProfileFrameMethods(ModuleDefinition module)
    {
        MethodReference enter = CompatMethod(module, "Prof", "Enter");
        MethodReference exit = CompatMethod(module, "Prof", "Exit");
        int count = 0;
        foreach (TypeDefinition type in AllTypes(module.Types))
            foreach (MethodDefinition method in type.Methods)
            {
                if (!method.HasBody || method.IsStatic || method.Parameters.Count != 0 || !ProfiledMethods.Contains(method.Name) ||
                    method.ReturnType.FullName != "System.Void")
                    continue;
                var start = new VariableDefinition(module.TypeSystem.Int64);
                method.Body.Variables.Add(start);
                string name = type.Name + "." + method.Name;
                BeforeRets(method, () => new[]
                {
                    Instruction.Create(OpCodes.Ldstr, name),
                    Instruction.Create(OpCodes.Ldloc, start),
                    Instruction.Create(OpCodes.Call, exit),
                });
                Prepend(method, Instruction.Create(OpCodes.Call, enter), Instruction.Create(OpCodes.Stloc, start));
                count++;
            }
        return count;
    }

    // Every Dictionary<string, AudioClip> indexer read in the game's code (Snd.levelClips["..."]) goes to HuntCompat
    // Clips.Get(dictionary, name), which logs a missing name and throws the same (compat/Clips.cs). Same stack.
    static int RouteClipLookups(ModuleDefinition module)
    {
        MethodReference get = CompatMethod(module, "Clips", "Get");
        int count = 0;
        foreach (TypeDefinition type in AllTypes(module.Types))
            foreach (MethodDefinition method in type.Methods.Where(m => m.HasBody))
                foreach (Instruction call in method.Body.Instructions)
                {
                    var called = call.Operand as MethodReference;
                    if ((call.OpCode != OpCodes.Call && call.OpCode != OpCodes.Callvirt) || called == null || called.Name != "get_Item")
                        continue;
                    var dictionary = called.DeclaringType as GenericInstanceType;
                    if (dictionary == null || dictionary.ElementType.FullName != "System.Collections.Generic.Dictionary`2" ||
                        dictionary.GenericArguments[0].FullName != "System.String" ||
                        dictionary.GenericArguments[1].FullName != "UnityEngine.AudioClip")
                        continue;
                    call.OpCode = OpCodes.Call;
                    call.Operand = get;
                    count++;
                }
        return count;
    }

    // Every Resources.Load and Resources.LoadAll call in the game's code goes to HuntCompat LoadTimer's method of the
    // same name and parameters (generic ones with the same type argument), which times it (compat/LoadTimer.cs).
    static int RouteResourceLoads(ModuleDefinition module)
    {
        TypeDefinition timer = compat["HuntCompat.LoadTimer"];
        int count = 0;
        foreach (TypeDefinition type in AllTypes(module.Types))
            foreach (MethodDefinition method in type.Methods.Where(m => m.HasBody))
                foreach (Instruction call in method.Body.Instructions)
                {
                    var called = call.Operand as MethodReference;
                    if ((call.OpCode != OpCodes.Call && call.OpCode != OpCodes.Callvirt) || called == null ||
                        called.DeclaringType.FullName != "UnityEngine.Resources" || (called.Name != "Load" && called.Name != "LoadAll"))
                        continue;
                    var generic = called as GenericInstanceMethod;
                    MethodReference element = generic != null ? generic.ElementMethod : called;
                    MethodDefinition target = timer.Methods.FirstOrDefault(m =>
                        m.Name == element.Name && m.Parameters.Count == element.Parameters.Count &&
                        m.HasGenericParameters == element.HasGenericParameters &&
                        m.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(element.Parameters.Select(p => p.ParameterType.FullName)));
                    if (target == null) continue;
                    MethodReference replacement = module.ImportReference(target);
                    if (generic != null)
                    {
                        var instance = new GenericInstanceMethod(replacement);
                        foreach (TypeReference argument in generic.GenericArguments) instance.GenericArguments.Add(argument);
                        replacement = instance;
                    }
                    call.OpCode = OpCodes.Call;
                    call.Operand = replacement;
                    count++;
                }
        return count;
    }
}

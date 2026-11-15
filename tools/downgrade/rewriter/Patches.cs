using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

// Single game methods that call HuntCompat rules: Rewired's platform, the analytics' login, the frame rate, the view size, the menus, Resources loads timed.
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

    // Rewired's UnityInputHelper.GetJoystickAxisValueByJoystickId and GetJoystickAxisRawValueByJoystickId(joystick, axis)
    // return 0 at once for a Unity joystick slot with no pad in it, or an axis the PS Vita's pad doesn't have (HuntCompat
    // RewiredInput.Polled; compat/RewiredInput.cs):
    // each read is an Input.GetAxis by name ("Joy3Axis17"), which Unity looks up among the game's 552 axes, and Rewired
    // reads every axis of all four slots the PS Vita reports each frame.
    static int SkipEmptyJoystickAxes(ModuleDefinition module)
    {
        TypeDefinition helper = module.GetType("Rewired.UnityInputHelper");
        if (helper == null) return 0;
        MethodReference polled = CompatMethod(module, "RewiredInput", "Polled");
        int count = 0;
        foreach (MethodDefinition method in helper.Methods.Where(m =>
                     (m.Name == "GetJoystickAxisValueByJoystickId" || m.Name == "GetJoystickAxisRawValueByJoystickId") && m.Parameters.Count == 2))
        {
            Instruction first = method.Body.Instructions[0];
            Prepend(method, Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Ldarg_1), Instruction.Create(OpCodes.Call, polled), Instruction.Create(OpCodes.Brtrue, first),
                    Instruction.Create(OpCodes.Ldc_R4, 0f), Instruction.Create(OpCodes.Ret));
            count++;
        }
        if (count != 2) throw new InvalidOperationException("Rewired.UnityInputHelper: " + count + " joystick axis readers, not 2");
        return count;
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

    // The game's effect pools: where each pool's size is set (a store of the field in the method), the value goes
    // through HuntCompat Pools.Size(size, name) first, which keeps fewer sprites on the PS Vita (compat/Pools.cs).
    // (type, method, field, the pool's name in Pools); each has exactly one such store.
    static readonly string[][] PoolSizes =
    {
        new[] { "FlyingObjects", ".ctor", "debrisIdleLength", "debris" },
        new[] { "FlyingObjects", ".ctor", "bloodIdleLength", "blood" },
        new[] { "FlyingObjects", "StartRain", "itemLength", "rain" },
        new[] { "FlyingObjects", "StartSnow", "itemLength", "snow" },
        new[] { "FlyingObjects", "StartAsh", "itemLength", "ash" },
        new[] { "Background2_2", null, "dustLength", "dust" },  // in whichever method sets it
    };

    static int PatchPoolSizes(ModuleDefinition module)
    {
        MethodReference size = CompatMethod(module, "Pools", "Size");
        int total = 0;
        foreach (string[] pool in PoolSizes)
        {
            TypeDefinition type = GameType(module, pool[0]);
            if (type == null) return 0;
            FieldDefinition field = type.Fields.Single(f => f.Name == pool[2]);
            int count = 0;
            foreach (MethodDefinition method in type.Methods.Where(m => m.HasBody && (pool[1] == null || m.Name == pool[1])))
            {
                ILProcessor il = method.Body.GetILProcessor();
                foreach (Instruction store in method.Body.Instructions
                             .Where(i => i.OpCode == OpCodes.Stfld && i.Operand is FieldReference && ((FieldReference)i.Operand).Resolve() == field).ToList())
                {
                    il.InsertBefore(store, Instruction.Create(OpCodes.Ldstr, pool[3]));
                    il.InsertBefore(store, Instruction.Create(OpCodes.Call, size));
                    count++;
                }
            }
            if (count != 1) throw new InvalidOperationException(pool[0] + "." + (pool[1] ?? "*") + ": " + count + " stores of " + pool[2] + ", not 1");
            total += count;
        }
        return total;
    }

    // Game.InitLevel (a level's start or restart from a checkpoint) and Game.PauseGame(bool) (the pause menu) call
    // HuntCompat Gc.LevelStart and Gc.Paused first: where the PS Vita's held garbage collector collects (compat/Gc.cs).
    static int PatchGcSafePoints(ModuleDefinition module)
    {
        TypeDefinition game = GameType(module, "Game");
        if (game == null) return 0;
        MethodDefinition initLevel = game.Methods.Single(m => m.Name == "InitLevel" && m.Parameters.Count == 0);
        MethodDefinition pause = game.Methods.Single(m =>
            m.Name == "PauseGame" && m.Parameters.Count == 1 && m.Parameters[0].ParameterType.FullName == "System.Boolean");
        Prepend(initLevel, Instruction.Create(OpCodes.Call, CompatMethod(module, "Gc", "LevelStart")));
        Prepend(pause, Instruction.Create(OpCodes.Call, CompatMethod(module, "Gc", "Paused")));
        return 2;
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
                // A coroutine's step: its iterator's MoveNext (the class the compiler makes, <Name>d__N, nested in the
                // game's), named as Type.Name(); what Unity's coroutine step (Update.ScriptRunDelayedDynamicFrameRate,
                // compat/Phases.cs) spends its time on. The value MoveNext returns stays on the stack under Exit's call.
                bool coroutine = method.Name == "MoveNext" && type.IsNested && type.Name.StartsWith("<") &&
                                 type.Interfaces.Any(i => i.InterfaceType.FullName == "System.Collections.IEnumerator");
                if (!method.HasBody || method.IsStatic || method.Parameters.Count != 0 ||
                    !(coroutine || ProfiledMethods.Contains(method.Name) && method.ReturnType.FullName == "System.Void"))
                    continue;
                var start = new VariableDefinition(module.TypeSystem.Int64);
                method.Body.Variables.Add(start);
                string name = coroutine ? type.DeclaringType.Name + "." + type.Name.Substring(1, type.Name.IndexOf('>') - 1) + "()"
                                        : type.Name + "." + method.Name;
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

    // SpriteObject.SetAlpha and SetFade start with HuntCompat Fades.Watch(renderer): a sprite the game fades isn't
    // drawn while its alpha is 0 (compat/Fades.cs).
    static int PatchSpriteFades(ModuleDefinition module)
    {
        TypeDefinition sprite = GameType(module, "SpriteObject");
        if (sprite == null) return 0;
        FieldDefinition renderer = sprite.Fields.Single(f => f.Name == "renderer");
        MethodReference watch = CompatMethod(module, "Fades", "Watch");
        int count = 0;
        foreach (MethodDefinition method in sprite.Methods.Where(m => (m.Name == "SetAlpha" || m.Name == "SetFade") && m.Parameters.Count == 1))
        {
            Prepend(method, Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Ldfld, renderer), Instruction.Create(OpCodes.Call, watch));
            count++;
        }
        if (count != 2) throw new InvalidOperationException("SpriteObject: " + count + " of SetAlpha and SetFade, not 2");
        return count;
    }

    // The game's menus without player two and the resolution menu, where HuntCompat Menus says so (the PS Vita;
    // compat/Menus.cs):
    // - GamePad.GetPlayerHasController(player) and SearchForPlayerController(player, steal) start with
    //   if (!Menus.PlayerTwo(player)) return false;
    // - MainMenu.PromptPlayerTwo(showRemove) starts with if (!Menus.PromptPlayerTwo()) return;
    // - The main menu's StartMenu.Init and the pause menus' PauseMenu.Init and MapPauseMenu.Init number their entries
    //   with num++; 2 PLAYERS' (p2 = num++) and JOIN PLAYER 2's (changePlayer = num++) become
    //   <field> = Menus.TwoPlayerEntry(num), num += Menus.TwoPlayerEntries(). Without p2 the main menu's first entry
    //   is the game's START GAME instead of 1 PLAYER (StartMenu.GetStartGameText).
    // - VideoMenu: Init's entries go through Menus.VideoChoices before the store to menuChoices, each read of
    //   selectedNr through Menus.VideoIndex, and each constant index of menuChoices[...] and UpdateSelection(...)
    //   through Menus.VideoShown.
    static int PatchMenus(ModuleDefinition module)
    {
        TypeDefinition gamepad = GameType(module, "GamePad");
        TypeDefinition mainMenu = GameType(module, "MainMenu");
        TypeDefinition video = GameType(module, "VideoMenu");
        if (gamepad == null || mainMenu == null || video == null) return 0;
        int count = 0;

        MethodReference playerTwo = CompatMethod(module, "Menus", "PlayerTwo");
        foreach (MethodDefinition method in gamepad.Methods.Where(m =>
                     (m.Name == "GetPlayerHasController" || m.Name == "SearchForPlayerController") && m.Parameters.Count >= 1 &&
                     m.Parameters[0].ParameterType.FullName == "System.Int32" && m.ReturnType.FullName == "System.Boolean"))
        {
            Instruction first = method.Body.Instructions[0];
            Prepend(method, Instruction.Create(OpCodes.Ldarg_1), Instruction.Create(OpCodes.Call, playerTwo), Instruction.Create(OpCodes.Brtrue, first),
                    Instruction.Create(OpCodes.Ldc_I4_0), Instruction.Create(OpCodes.Ret));
            count++;
        }
        if (count != 2) throw new InvalidOperationException("GamePad: " + count + " of GetPlayerHasController and SearchForPlayerController, not 2");

        MethodDefinition prompt = mainMenu.Methods.Single(m => m.Name == "PromptPlayerTwo" && m.Parameters.Count == 1);
        ReturnAtStartIf(prompt, OpCodes.Brfalse, Instruction.Create(OpCodes.Call, CompatMethod(module, "Menus", "PromptPlayerTwo")));
        count++;

        foreach (string[] entry in new[] { new[] { "StartMenu", "p2" }, new[] { "PauseMenu", "changePlayer" }, new[] { "MapPauseMenu", "changePlayer" } })
        {
            string name = entry[0];
            TypeDefinition menu = GameType(module, name);
            if (menu == null) return 0;
            MethodDefinition init = menu.Methods.Single(m => m.Name == "Init" && m.Parameters.Count == 1);
            FieldDefinition twoPlayers = menu.Fields.Single(f => f.Name == entry[1]);
            Instruction[] sets = init.Body.Instructions
                .Where(i => i.OpCode == OpCodes.Stfld && ((FieldReference)i.Operand).Resolve() == twoPlayers).ToArray();
            // ldarg.0; ldloc num; dup; ldc.i4.1; add; stloc num; stfld <field>
            Instruction step = sets.Length == 1 ? sets[0].Previous.Previous.Previous : null;
            if (step == null || step.OpCode != OpCodes.Ldc_I4_1 || step.Next.OpCode != OpCodes.Add || !step.Next.Next.OpCode.Name.StartsWith("stloc"))
                throw new InvalidOperationException(name + ".Init: " + entry[1] + " isn't set once by " + entry[1] + " = num++");
            step.OpCode = OpCodes.Call;
            step.Operand = CompatMethod(module, "Menus", "TwoPlayerEntries");
            init.Body.GetILProcessor().InsertBefore(sets[0], Instruction.Create(OpCodes.Call, CompatMethod(module, "Menus", "TwoPlayerEntry")));
            count++;
        }

        MethodReference choices = CompatMethod(module, "Menus", "VideoChoices");
        MethodReference index = CompatMethod(module, "Menus", "VideoIndex");
        MethodReference shown = CompatMethod(module, "Menus", "VideoShown");
        int stores = 0, reads = 0, constants = 0;
        foreach (MethodDefinition method in video.Methods.Where(m => m.HasBody))
        {
            ILProcessor il = method.Body.GetILProcessor();
            foreach (Instruction instruction in method.Body.Instructions.ToArray())
            {
                var field = instruction.Operand as FieldReference;
                if (field != null && field.Name == "menuChoices" && instruction.OpCode == OpCodes.Stfld && method.Name == "Init")
                {
                    il.InsertBefore(instruction, Instruction.Create(OpCodes.Call, choices));
                    stores++;
                }
                else if (field != null && field.Name == "selectedNr" && instruction.OpCode == OpCodes.Ldfld)
                {
                    InsertAfter(il, instruction, Instruction.Create(OpCodes.Call, index));
                    reads++;
                }
                else if (field != null && field.Name == "menuChoices" && instruction.OpCode == OpCodes.Ldfld && IntConstant(instruction.Next))
                {
                    InsertAfter(il, instruction.Next, Instruction.Create(OpCodes.Call, shown));
                    constants++;
                }
                else if (CalledName(instruction) == "UpdateSelection" && IntConstant(instruction.Previous))
                {
                    il.InsertBefore(instruction, Instruction.Create(OpCodes.Call, shown));
                    constants++;
                }
            }
        }
        // Init's one store; the switches of ClickStart, ClickLeft and ClickRight; the entries SetCRT, ToggleVsync and
        // ToggleFrameRate write and update, and SetToDefault's two.
        if (stores != 1 || reads != 3 || constants != 10)
            throw new InvalidOperationException("VideoMenu: " + stores + " stores of its entries (not 1), " + reads + " reads of selectedNr (not 3), " +
                                                constants + " constant indexes (not 10)");
        return count + stores + reads + constants;
    }

    static bool IntConstant(Instruction instruction)
    {
        return instruction != null && (instruction.OpCode == OpCodes.Ldc_I4 || instruction.OpCode == OpCodes.Ldc_I4_S ||
                                       (instruction.OpCode.Code >= Code.Ldc_I4_M1 && instruction.OpCode.Code <= Code.Ldc_I4_8));
    }

    // The game's reads of Input.touchCount and Input.touchSupported go to HuntCompat TouchInput.Count and
    // Supported: the PS Vita's touch screen doesn't switch the game to its phone controls (compat/TouchInput.cs).
    static readonly Dictionary<string, string> TouchReads = new Dictionary<string, string>
    {
        { "get_touchCount", "Count" }, { "get_touchSupported", "Supported" },
    };

    static int RouteTouchInput(ModuleDefinition module)
    {
        int count = 0;
        foreach (TypeDefinition type in AllTypes(module.Types))
            foreach (MethodDefinition method in type.Methods.Where(m => m.HasBody))
                foreach (Instruction call in method.Body.Instructions)
                {
                    var called = call.Operand as MethodReference;
                    string replacement;
                    if (call.OpCode != OpCodes.Call || called == null || called.DeclaringType.FullName != "UnityEngine.Input" ||
                        !TouchReads.TryGetValue(called.Name, out replacement))
                        continue;
                    call.Operand = CompatMethod(module, "TouchInput", replacement);
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

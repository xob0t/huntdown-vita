using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using UnityEngine;

// The game's input on the PS Vita without Rewired's frame. The game reads its controls through Rewired (GamePad:
// GetButton, GetButtonDown, GetButtonUp, GetAxis and GetAnyButton by action name, for its players 0 and 1), and
// Rewired's InputManager_Base.Update rebuilds every controller, player and action each frame to answer them: in a
// HUNT_PROF=1 build (compat/Prof.cs) 1.27 ms of each frame in level 3-3 (artifacts' bench), its reads of the pad
// included, and the game's reads through Rewired's Player about 0.35 ms more, inside Game.Update.
// Once the game's first player has the PS Vita's pad (Rewired's joystick for the PS Vita map, the one the game took
// with SearchForPlayerController) and the second player none, the rewriter's patches (rewriter/Patches.cs,
// AnswerGamePadDirect) have InputManager_Base.Update return at once (SkipUpdate) and GamePad's five reads answered here
// (Answers, then Button, ButtonDown, ButtonUp, Axis, AnyButton) from the pad's Unity buttons and axes, read once per
// frame where Rewired would have read them, and mapped as Rewired's PS Vita map maps them (asset_rules.py: the
// template map's PS Vita copy, SonyPlaystationVita.asset):
//   Cross Jump, Circle Fire2, Square Fire, Triangle Action, L B8, R B9 (the game's dash), Select Back, Start Start
//   (Unity's joystick buttons 0-7); Dash and B10 have no button on the PS Vita.
//   Move Horizontal and Move Vertical: the left stick (Unity's axes 1 and 2, the second inverted) with Rewired's radial
//   dead zone of 0.2 (Axis2DCalibration, InputTools.ApplyRadialDeadZone: below 0.2 nothing, above it rescaled from
//   0.2-1 to 0-1), and the d-pad (axes 6 and 7: right and up positive) as -1 or 1; of the two, the larger.
// Until then (the first seconds, before the pad shows up in Unity's joystick slot 1 and the game takes it) and when it
// stops (the slot empties, the game drops the pad, a second player gets one, a pad that isn't the PS Vita's), Rewired
// runs and answers as before, and catches up with the controllers as it always has. Measured the same way, Rewired's
// Update then takes 0.17 ms (the pad's reads: four Input.GetAxisRaw by name) and Update.ScriptRunBehaviourUpdate
// 2.33 ms instead of 3.78. In check mode, every answer of a level 1-1 run with each button, the d-pad, the left stick
// at full and partial tilts and both pauses was Rewired's but one (of 340000): in the frame after the game takes the pad
// on a press of Cross, Rewired reports Jump released while Cross is still held; the game only clears its jump flag.
//   -hunt-rewired-direct on      as above (the PS Vita's default)
//   -hunt-rewired-direct off     Rewired answers the game as the game does (the default off the PS Vita)
//   -hunt-rewired-direct check   Rewired answers, and each answer is compared with this one: HUNT_REWIRED_DIRECT lines
//                                for the pad's changes and each answer that differs, a count every 600 frames
// HuntCompat doesn't reference Rewired: the players' joysticks are read by reflection, a few times a second.
// Part of HuntCompat; see Compat2018.cs.
namespace HuntCompat
{
    public static class RewiredDirect
    {
        const int Off = 0, On = 1, Check = 2;
        static int mode = -1;

        static int Mode
        {
            get
            {
                if (mode < 0)
                {
                    string value = HuntFlags.Value(ProbeFiles.Args, "-hunt-rewired-direct", "on", "off");
                    mode = value == "check" ? Check : value == "on" ? On : Off;
                    if (mode != Off) Debug.Log("HUNT_REWIRED_DIRECT " + value);
                }
                return mode;
            }
        }

        // The game's actions (Rewired's names, GamePad's constants); the first two are axes.
        static readonly string[] Actions = { "Move Horizontal", "Move Vertical", "Jump", "Fire", "Fire2", "Action", "Back", "Start", "Dash", "B8", "B9", "B10" };
        // Each action's Unity joystick button (-1: none on the PS Vita).
        static readonly int[] ActionButtons = { -1, -1, 0, 2, 1, 3, 6, 7, -1, 4, 5, -1 };
        const int MoveX = 0, MoveY = 1;
        const float DeadZone = 0.2f, ButtonDeadZone = 0.5f;
        static readonly Guid VitaPad = new Guid("d2aef070-7caa-42ff-b2dc-daac7e4a62b4");

        // The pad as last read: the actions' values, and the actions held now and in the frame before (bits).
        static readonly float[] values = new float[12];
        static int held, heldBefore, readFrame = -1;
        static int slot;            // the pad's Unity joystick slot (1-4) while engaged, else 0
        static string[] axisNames;  // Joy<slot>Axis1, 2, 6, 7

        // Rewired's players 0 and 1 (by reflection, once Rewired is ready).
        static Type reInput;
        static object[] helpers;    // each player's ControllerHelper
        static Func<int>[] joystickCounts;
        static PropertyInfo joysticksProperty;
        static object joystick;     // the pad player 0 had when last looked at
        static int lookedAt = -1000, namesAt = -1000;
        static bool failed, engaged;

        // InputManager_Base.Update's start, once a frame: reads the pad, and whether Rewired's frame is skipped.
        public static bool SkipUpdate()
        {
            if (Mode == Off) return false;
            try
            {
                Follow();
            }
            catch (Exception exception)
            {
                failed = true;
                engaged = false;
                Debug.Log("HUNT_REWIRED_DIRECT failed, Rewired answers from now on: " + exception);
            }
            if (engaged) Read();
            return engaged && mode == On;
        }

        // Whether GamePad's read for this player is answered here.
        public static bool Answers(int player)
        {
            return engaged && mode == On;
        }

        public static bool Button(int player, string action)
        {
            int index = player == 0 ? IndexOf(action) : -1;
            return index >= 0 && (held & 1 << index) != 0;
        }

        public static bool ButtonDown(int player, string action)
        {
            int index = player == 0 ? IndexOf(action) : -1;
            return index >= 0 && (held & ~heldBefore & 1 << index) != 0;
        }

        public static bool ButtonUp(int player, string action)
        {
            int index = player == 0 ? IndexOf(action) : -1;
            return index >= 0 && (~held & heldBefore & 1 << index) != 0;
        }

        public static float Axis(int player, string action)
        {
            int index = player == 0 ? IndexOf(action) : -1;
            return index >= 0 ? values[index] : 0f;
        }

        public static bool AnyButton(int player)
        {
            return player == 0 && held != 0;
        }

        static int IndexOf(string action)
        {
            for (int i = 0; i < Actions.Length; i++)
                if (ReferenceEquals(Actions[i], action)) return i;
            for (int i = 0; i < Actions.Length; i++)
                if (Actions[i] == action) return i;
            return -1;
        }

        // Whether the pad is the first player's alone, and in which slot.
        static void Follow()
        {
            if (failed) return;
            if (helpers == null && !FindPlayers()) return;
            int frame = Time.frameCount;
            bool mine = joystickCounts[0]() == 1 && joystickCounts[1]() == 0;
            if (!mine)
            {
                Engage(null, 0);
                return;
            }
            // The pad itself, six times a second (the game may swap pads with the count unchanged).
            if (frame - lookedAt >= 10)
            {
                lookedAt = frame;
                object pad = null;
                int pads = 0;
                foreach (object each in (IEnumerable)joysticksProperty.GetValue(helpers[0], null))
                {
                    pad = each;
                    pads++;
                }
                if (pads != 1) pad = null;
                if (pad != joystick)
                {
                    int unitySlot = 0;
                    if (pad != null && (Guid)Property(pad, "hardwareTypeGuid") == VitaPad) unitySlot = (int)Property(pad, "unityId");
                    if (unitySlot < 1 || unitySlot > 4 || !SlotHasPad(unitySlot)) unitySlot = 0;
                    namesAt = frame;
                    Debug.Log("HUNT_REWIRED_DIRECT player 0's pad: " + (pad == null ? "none" : Property(pad, "name") + " in slot " + Property(pad, "unityId")) +
                              (unitySlot > 0 ? ", read directly" : ", left to Rewired"));
                    Engage(pad, unitySlot);
                }
            }
            // The slot's name, every 2 seconds: a pad that goes (a PS TV's) is left to Rewired, which sees it go; the
            // pad stays left to Rewired until the game's first player has another.
            if (engaged && frame - namesAt >= 120)
            {
                namesAt = frame;
                if (!SlotHasPad(slot))
                {
                    Debug.Log("HUNT_REWIRED_DIRECT slot " + slot + " is empty: Rewired answers");
                    object pad = joystick;
                    Engage(null, 0);
                    joystick = pad;
                }
            }
        }

        static bool SlotHasPad(int unitySlot)
        {
            string[] names = Input.GetJoystickNames();
            return unitySlot <= names.Length && !string.IsNullOrEmpty(names[unitySlot - 1]);
        }

        static void Engage(object pad, int unitySlot)
        {
            joystick = pad;
            if (unitySlot == slot && engaged == unitySlot > 0) return;
            slot = unitySlot;
            engaged = slot > 0;
            if (engaged)
            {
                axisNames = new[] { 1, 2, 6, 7 }.Select(axis => "Joy" + slot + "Axis" + axis).ToArray();
                // The frame before is what Rewired last answered (its own frame hasn't run yet in this one), so that a
                // press or release across the switch is seen once, as Rewired would have seen it.
                readFrame = -1;
                Read();
                heldBefore = RewiredHeld();
            }
            Debug.Log("HUNT_REWIRED_DIRECT " + (engaged ? "reads slot " + slot + (mode == On ? ", Rewired's frame skipped" : "") : "off: Rewired answers"));
        }

        static void Read()
        {
            int frame = Time.frameCount;
            if (frame == readFrame) return;
            readFrame = frame;
            int before = held;
            heldBefore = held;
            int now = 0;
            int firstKey = (int)KeyCode.Joystick1Button0 + 20 * (slot - 1);
            for (int i = 0; i < Actions.Length; i++)
            {
                int button = ActionButtons[i];
                if (button >= 0 && Input.GetKey((KeyCode)(firstKey + button)))
                {
                    now |= 1 << i;
                    values[i] = 1f;
                }
                else values[i] = 0f;
            }
            float x = Input.GetAxisRaw(axisNames[0]), y = -Input.GetAxisRaw(axisNames[1]);
            float padX = Input.GetAxisRaw(axisNames[2]), padY = Input.GetAxisRaw(axisNames[3]);
            float magnitude = Mathf.Sqrt(x * x + y * y);
            if (magnitude < DeadZone)
                x = y = 0f;
            else
            {
                float scale = (magnitude - DeadZone) / (1f - DeadZone) / magnitude;
                x = Mathf.Clamp(x * scale, -1f, 1f);
                y = Mathf.Clamp(y * scale, -1f, 1f);
            }
            values[MoveX] = Larger(x, padX > ButtonDeadZone ? 1f : padX < -ButtonDeadZone ? -1f : 0f);
            values[MoveY] = Larger(y, padY > ButtonDeadZone ? 1f : padY < -ButtonDeadZone ? -1f : 0f);
            // An axis counts as a held button past Rewired's button dead zone, positive values only (the game's
            // configuration: activateActionButtonsOnNegativeValue off); GetAnyButton sees them.
            if (values[MoveX] > ButtonDeadZone) now |= 1 << MoveX;
            if (values[MoveY] > ButtonDeadZone) now |= 1 << MoveY;
            held = now;
            if (mode == Check && (now != before || x != lastX || y != lastY))
                Debug.Log("HUNT_REWIRED_DIRECT pad " + Convert.ToString(now, 2) + " stick " + x.ToString("F3") + "," + y.ToString("F3") +
                          " d-pad " + padX + "," + padY + " at " + frame);
            lastX = x;
            lastY = y;
        }

        static float lastX, lastY;

        // The actions Rewired's player 0 holds as Rewired last answered (Player.GetButton; an axis action past the
        // button dead zone), as bits.
        static object firstPlayer;

        static int RewiredHeld()
        {
            MethodInfo getButton = firstPlayer.GetType().GetMethod("GetButton", new[] { typeof(string) });
            int bits = 0;
            for (int i = 0; i < Actions.Length; i++)
                if ((bool)getButton.Invoke(firstPlayer, new object[] { Actions[i] })) bits |= 1 << i;
            return bits;
        }

        static float Larger(float a, float b)
        {
            return Mathf.Abs(b) > Mathf.Abs(a) ? b : a;
        }

        static bool FindPlayers()
        {
            if (reInput == null)
                reInput = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("Rewired.ReInput", false)).FirstOrDefault(t => t != null);
            if (reInput == null || !(bool)Property(null, "isReady", reInput)) return false;
            object players = Property(null, "players", reInput);
            MethodInfo getPlayer = players.GetType().GetMethod("GetPlayer", new[] { typeof(int) });
            var found = new object[2];
            var counts = new Func<int>[2];
            for (int i = 0; i < 2; i++)
            {
                object player = getPlayer.Invoke(players, new object[] { i });
                if (i == 0) firstPlayer = player;
                found[i] = Property(player, "controllers");
                PropertyInfo count = found[i].GetType().GetProperty("joystickCount");
                object helper = found[i];
                try
                {
                    counts[i] = (Func<int>)Delegate.CreateDelegate(typeof(Func<int>), helper, count.GetGetMethod());
                }
                catch (Exception)
                {
                    counts[i] = () => (int)count.GetValue(helper, null);
                }
            }
            joysticksProperty = found[0].GetType().GetProperty("Joysticks");
            joystickCounts = counts;
            helpers = found;
            return true;
        }

        // A public property's or field's value (Player.controllers is a field).
        static object Property(object target, string name, Type type = null)
        {
            type = type ?? target.GetType();
            const BindingFlags Public = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;
            PropertyInfo property = type.GetProperty(name, Public);
            if (property != null) return property.GetValue(target, null);
            FieldInfo field = type.GetField(name, Public);
            if (field != null) return field.GetValue(target);
            throw new MissingMemberException(type.FullName, name);
        }

        // -hunt-rewired-direct check: GamePad's answer (Rewired's) against this one, before GamePad returns it.
        static int checkedCount, differed, reportedAt;

        public static bool CheckButton(bool rewired, int player, string action) { return Compare(rewired, Button(player, action), player, action, "GetButton"); }

        public static bool CheckButtonDown(bool rewired, int player, string action) { return Compare(rewired, ButtonDown(player, action), player, action, "GetButtonDown"); }

        public static bool CheckButtonUp(bool rewired, int player, string action) { return Compare(rewired, ButtonUp(player, action), player, action, "GetButtonUp"); }

        public static bool CheckAnyButton(bool rewired, int player) { return Compare(rewired, AnyButton(player), player, "", "GetAnyButton"); }

        public static float CheckAxis(float rewired, int player, string action)
        {
            if (mode != Check || !engaged) return rewired;
            float direct = Axis(player, action);
            Count(Mathf.Abs(direct - rewired) > 0.002f, "GetAxis", player, action, rewired.ToString("F3"), direct.ToString("F3"));
            return rewired;
        }

        static bool Compare(bool rewired, bool direct, int player, string action, string read)
        {
            if (mode != Check || !engaged) return rewired;
            Count(direct != rewired, read, player, action, rewired.ToString(), direct.ToString());
            return rewired;
        }

        static void Count(bool differs, string read, int player, string action, string rewired, string direct)
        {
            checkedCount++;
            if (differs && ++differed <= 300)
                Debug.Log("HUNT_REWIRED_DIRECT differs at " + Time.frameCount + ": " + read + "(" + player + ", " + action + ") Rewired " + rewired + ", direct " + direct);
            if (Time.frameCount - reportedAt >= 600)
            {
                reportedAt = Time.frameCount;
                Debug.Log("HUNT_REWIRED_DIRECT checked " + checkedCount + " answers, " + differed + " differed");
            }
        }
    }
}

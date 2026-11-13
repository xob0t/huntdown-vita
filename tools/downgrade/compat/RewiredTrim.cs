using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using UnityEngine;

// Rewired's controllers on the PS Vita. The game ships Rewired's Windows plugin only, so on the PS Vita Rewired reads
// its controllers through Unity's Input, and polls a keyboard and a mouse besides the pad each frame (the game's
// configuration doesn't disable the keyboard for the PS Vita, platformVars_psVita). Measured in a HUNT_PROF=1 build
// (compat/Prof.cs), Rewired's InputManager_Base.Update took 3.5 ms of each frame in level 2-4, more than the game's
// own Game.Update.
// On the PS Vita its keyboard and mouse are disabled (Controller.enabled) once Rewired is ready: 0.8 ms of each frame.
//   -hunt-rewired game   leave them on, as the game does (the default off the PS Vita)
//   -hunt-rewired log    log Rewired's controllers and its components, once it is ready (HUNT_REWIRED)
//   -hunt-rewired trim   besides, disable its keyboard and mouse (the PS Vita's default, without the log)
//   -hunt-rewired pad    besides, disable the joysticks without a hardware name (Unity's empty joystick slots)
// HuntCompat doesn't reference Rewired: everything goes through reflection (link.xml keeps Rewired_Core whole).
// Part of HuntCompat; see Compat2018.cs.
namespace HuntCompat
{
    public static class RewiredTrim
    {
        static string mode;
        static Type reInput;

        internal static void Install(string[] args)
        {
            mode = HuntFlags.Value(args, "-hunt-rewired") ?? (HuntFlags.Vita ? "default" : null);
            if (mode == null || mode == "game") return;
            Application.onBeforeRender += WhenReady;
        }

        static void WhenReady()
        {
            if (reInput == null)
                reInput = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("Rewired.ReInput", false)).FirstOrDefault(t => t != null);
            if (reInput == null || !(bool)Get(null, reInput, "isReady")) return;
            Application.onBeforeRender -= WhenReady;
            try
            {
                Apply();
            }
            catch (Exception exception)
            {
                Debug.Log("HUNT_REWIRED failed: " + exception);
            }
        }

        static void Apply()
        {
            object controllers = Get(null, reInput, "controllers");
            object keyboard = Get(controllers, null, "Keyboard");
            object mouse = Get(controllers, null, "Mouse");
            if (mode == "default")
            {
                Disable(keyboard, mouse);
                return;
            }
            Debug.Log("HUNT_REWIRED keyboard " + Describe(keyboard) + ", mouse " + Describe(mouse) + ", joysticks " + Get(controllers, null, "joystickCount"));
            foreach (object joystick in (IEnumerable)Get(controllers, null, "Joysticks"))
                Debug.Log("HUNT_REWIRED joystick " + Describe(joystick) + " (" + Get(joystick, null, "hardwareName") + ")");
            PropertyInfo all = controllers.GetType().GetProperty("Controllers");
            if (all != null)
                foreach (object controller in (IEnumerable)all.GetValue(controllers, null))
                    Debug.Log("HUNT_REWIRED controller " + Get(controller, null, "type") + " id " + Get(controller, null, "id") + ": " + Describe(controller) +
                              " (" + Get(controller, null, "hardwareName") + ", " + controller.GetType().FullName + ")");
            object players = Get(null, reInput, "players");
            Debug.Log("HUNT_REWIRED players " + Get(players, null, "allPlayerCount"));
            foreach (MonoBehaviour behaviour in UnityEngine.Object.FindObjectsOfType<MonoBehaviour>())
            {
                Type type = behaviour.GetType();
                if (type.Assembly.GetName().Name != "Rewired_Core") continue;
                Debug.Log("HUNT_REWIRED component " + type.FullName + " (" + type.BaseType + ") on " + behaviour.gameObject.name);
            }
            if (mode != "trim" && mode != "pad") return;
            Disable(keyboard, mouse);
            Debug.Log("HUNT_REWIRED keyboard and mouse disabled: " + Describe(keyboard) + ", " + Describe(mouse));
            if (mode != "pad") return;
            foreach (object joystick in ((IEnumerable)Get(controllers, null, "Joysticks")).Cast<object>().ToArray())
            {
                if (!string.IsNullOrEmpty((string)Get(joystick, null, "hardwareName"))) continue;
                joystick.GetType().GetProperty("enabled").SetValue(joystick, false, null);
                Debug.Log("HUNT_REWIRED joystick without a name disabled: " + Describe(joystick));
            }
        }

        static void Disable(params object[] controllers)
        {
            foreach (object controller in controllers)
                if (controller != null) controller.GetType().GetProperty("enabled").SetValue(controller, false, null);
        }

        static string Describe(object controller)
        {
            return controller == null ? "none" : Get(controller, null, "name") + " enabled=" + Get(controller, null, "enabled");
        }

        static object Get(object target, Type type, string name)
        {
            type = type ?? target.GetType();
            const BindingFlags Public = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;
            PropertyInfo property = type.GetProperty(name, Public);
            if (property != null) return property.GetValue(target, null);
            FieldInfo field = type.GetField(name, Public);
            if (field != null) return field.GetValue(target);
            throw new MissingMemberException(type.FullName, name);
        }
    }
}

using BepInEx;
using HarmonyLib;

using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;

using Assets.Code.Game;
using Assets.Code.Game.StageCoach;
using Assets.Code.Map;
using Assets.Code.Combat;
using Assets.Code.Combat.Events;
using Assets.Code.Actor;
using Assets.Code.Actor.Queries;
using Assets.Code.Actor.ActorController;
using Assets.Code.Actor.Events;
using Assets.Code.Affinity.Events;
using Assets.Code.Library;
using Assets.Code.Skill;
using Assets.Code.Skill.Events;
using Assets.Code.Skill.Queries;
using Assets.Code.Utils;
using Assets.Code.UI;
using Assets.Code.UI.Events;
using Assets.Code.Events;
using Assets.Code.Effect;
using Assets.Code.Token;
using Assets.Code.UI.Tooltips;
using Assets.Code.UI.Screens;
using Assets.Code.UI.Widgets;
using Assets.Code.UI.Managers;
using Assets.Code.Campaign;

using Newtonsoft.Json.Linq;

namespace DD2Bridge
{
    public sealed partial class DD2BridgePlugin
    {
        private static string GetTransformPath(UnityEngine.Transform transform)
        {
            if (transform == null)
            {
                return "<null>";
            }

            List<string> parts = new List<string>();
            UnityEngine.Transform current = transform;
            while (current != null)
            {
                parts.Add(current.name);
                current = current.parent;
            }
            parts.Reverse();
            return string.Join("/", parts.ToArray());
        }

        private static object GetMemberValue(object instance, string name)
        {
            if (instance == null || string.IsNullOrEmpty(name))
            {
                return null;
            }

            Type type = instance.GetType();
            BindingFlags flags =
                BindingFlags.Instance |
                BindingFlags.Public |
                BindingFlags.NonPublic;

            PropertyInfo property = type.GetProperty(name, flags);
            if (property != null && property.CanRead &&
                property.GetIndexParameters().Length == 0)
            {
                return property.GetValue(instance, null);
            }

            FieldInfo field = type.GetField(name, flags);
            return field == null ? null : field.GetValue(instance);
        }

        private static object GetStaticMemberValue(Type type, string name)
        {
            if (type == null || string.IsNullOrEmpty(name))
            {
                return null;
            }

            BindingFlags flags =
                BindingFlags.Static |
                BindingFlags.Public |
                BindingFlags.NonPublic;

            PropertyInfo property = type.GetProperty(name, flags);
            if (property != null && property.CanRead &&
                property.GetIndexParameters().Length == 0)
            {
                return property.GetValue(null, null);
            }

            FieldInfo field = type.GetField(name, flags);
            return field == null ? null : field.GetValue(null);
        }

        private static bool GetBooleanMember(
            object instance,
            string name,
            bool fallback)
        {
            object value = GetMemberValue(instance, name);
            if (value == null)
            {
                return fallback;
            }

            try
            {
                return Convert.ToBoolean(value);
            }
            catch
            {
                return fallback;
            }
        }

        private static uint ToUInt32(object value)
        {
            if (value == null)
            {
                return 0u;
            }

            try
            {
                return Convert.ToUInt32(value);
            }
            catch
            {
                return 0u;
            }
        }

        private static string GetStableObjectId(object value)
        {
            if (value == null)
            {
                return "";
            }

            object id = GetMemberValue(value, "Id") ??
                        GetMemberValue(value, "m_Id");

            return id == null ? "" : id.ToString();
        }

        private static JArray BuildStringArray(IEnumerable values)
        {
            JArray result = new JArray();
            if (values == null)
            {
                return result;
            }

            foreach (object value in values)
            {
                if (value != null)
                {
                    result.Add(value.ToString());
                }
            }

            return result;
        }

        private static bool StringArrayContains(JArray values, string expected)
        {
            if (values == null)
            {
                return false;
            }

            foreach (JToken value in values)
            {
                if (string.Equals(
                    value.Value<string>(),
                    expected,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static MethodInfo FindMethodByNameAndParameterCount(
            Type type,
            string name,
            int parameterCount)
        {
            if (type == null)
            {
                return null;
            }

            foreach (MethodInfo method in type.GetMethods(
                BindingFlags.Instance |
                BindingFlags.Public |
                BindingFlags.NonPublic |
                BindingFlags.Static))
            {
                if (method.Name == name &&
                    method.GetParameters().Length == parameterCount)
                {
                    return method;
                }
            }

            return null;
        }

        private static Type FindLoadedTypeBySimpleName(string simpleName)
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    Type[] types = assembly.GetTypes();
                    foreach (Type type in types)
                    {
                        if (type != null && type.Name == simpleName)
                        {
                            return type;
                        }
                    }
                }
                catch (ReflectionTypeLoadException ex)
                {
                    Type[] types = ex.Types;
                    if (types == null)
                    {
                        continue;
                    }

                    foreach (Type type in types)
                    {
                        if (type != null && type.Name == simpleName)
                        {
                            return type;
                        }
                    }
                }
                catch
                {
                    // One optional assembly failing reflection must not break inspect.
                }
            }

            return null;
        }

        private static string TryGetDisplayString(object value)
        {
            if (value == null)
            {
                return "";
            }

            Type type = value.GetType();
            string[] methodNames = new string[]
            {
                "GetName",
                "GetDisplayName",
                "GetDescription"
            };

            foreach (string methodName in methodNames)
            {
                MethodInfo method =
                    FindMethodByNameAndParameterCount(type, methodName, 0);

                if (method == null || method.ReturnType != typeof(string))
                {
                    continue;
                }

                try
                {
                    string text = method.Invoke(value, null) as string;
                    if (!string.IsNullOrEmpty(text))
                    {
                        return text;
                    }
                }
                catch
                {
                }
            }

            string[] memberNames = new string[]
            {
                "Name",
                "m_Name",
                "LocKey",
                "m_LocKey"
            };

            foreach (string memberName in memberNames)
            {
                object member = GetMemberValue(value, memberName);
                if (member != null && !string.IsNullOrEmpty(member.ToString()))
                {
                    return member.ToString();
                }
            }

            return "";
        }

        // v16: stable discrete boundary immediately before ordinary realtime
        // stagecoach travel. The native controller already exposes every fact
        // needed to decide whether the same forward input a player can give is
        // currently legal. Do not expose READY while results UI is still above
        // the driving scene, while GameMode is changing, while input is blocked,
        // or after forward has already latched.
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Debug;
using MegaCrit.Sts2.Core.Logging;

namespace NoSentry;

/// <summary>
/// Works around native crashes in STS2 0.107.1–0.109.x by skipping Sentry's GDExtension
/// shutdown (<see cref="GodotObject.Call"/>) inside <see cref="SentryService.Shutdown"/>
/// while preserving its managed .NET shutdown.
/// </summary>
[HarmonyPatch(typeof(SentryService), nameof(SentryService.Shutdown), new Type[0])]
internal static class SentryGdExtensionShutdown1071WorkaroundPatch
{
    private const string BaseLibHarmonyId = "BaseLib";
    private const string BaseLibSkipPatchTypeName = "BaseLib.Patches.Fixes.SkipSentryShutdownPatch";
    private const string BaseLibSkipMethodName = "SkipShutdown";

    private static readonly Version AffectedHostMinVersion = new(0, 107, 1);
    private static readonly Version AffectedHostMaxExclusiveVersion = new(0, 110, 0);

    private static readonly MethodInfo SkipMethod = AccessTools.DeclaredMethod(
        typeof(SentryGdExtensionShutdown1071WorkaroundPatch),
        nameof(SkipNativeGdExtensionShutdown));

    private static int _logged;

    [HarmonyBefore(BaseLibHarmonyId)]
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var code = instructions.ToList();
        if (!IsAffectedHost())
        {
            return code;
        }

        if (code.Any(static instr => IsCall(instr) && instr.operand is MethodInfo m && IsEquivalentSkipCall(m)))
        {
            return code;
        }

        var patched = false;
        foreach (var instr in code)
        {
            if (IsCall(instr) && instr.operand is MethodInfo method && IsGodotObjectCall(method))
            {
                instr.opcode = OpCodes.Call;
                instr.operand = SkipMethod;
                patched = true;
            }
        }

        if (patched)
        {
            Log.Info("[NoSentry] Applied STS2 0.107.1-0.109.x Sentry GDExtension shutdown workaround");
        }

        return code;
    }

    private static bool IsAffectedHost()
    {
        var rawVersion = ReleaseInfoManager.Instance.ReleaseInfo?.Version;
        if (string.IsNullOrWhiteSpace(rawVersion))
        {
            return false;
        }

        var normalized = rawVersion.TrimStart('v', 'V').Split('-', '+')[0];
        return Version.TryParse(normalized, out var version) &&
               version >= AffectedHostMinVersion &&
               version < AffectedHostMaxExclusiveVersion;
    }

    private static bool IsCall(CodeInstruction instr)
    {
        return instr.opcode == OpCodes.Call || instr.opcode == OpCodes.Callvirt;
    }

    private static bool IsGodotObjectCall(MethodInfo method)
    {
        return method.DeclaringType == typeof(GodotObject) &&
               method.Name == nameof(GodotObject.Call) &&
               method.GetParameters().Select(static p => p.ParameterType)
                   .SequenceEqual(new[] { typeof(StringName), typeof(Variant[]) });
    }

    private static bool IsEquivalentSkipCall(MethodInfo method)
    {
        if (method == SkipMethod)
        {
            return true;
        }

        return string.Equals(method.DeclaringType?.FullName, BaseLibSkipPatchTypeName, StringComparison.Ordinal) &&
               string.Equals(method.Name, BaseLibSkipMethodName, StringComparison.Ordinal) &&
               method.IsStatic &&
               method.ReturnType == typeof(Variant) &&
               method.GetParameters().Select(static p => p.ParameterType)
                   .SequenceEqual(new[] { typeof(GodotObject), typeof(StringName), typeof(Variant[]) });
    }

    private static Variant SkipNativeGdExtensionShutdown(GodotObject instance, StringName method, Variant[] args)
    {
        if (Interlocked.Exchange(ref _logged, 1) == 0)
        {
            Log.Info("[NoSentry] Skipped native Sentry GDExtension shutdown for STS2 0.107.1-0.109.x workaround.");
        }

        return default;
    }
}

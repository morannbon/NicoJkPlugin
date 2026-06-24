using System.Reflection;
using TvAIrPlugin;

namespace NicoJkPlugin;

/// <summary>
/// TvAIr v0.11.315 Plugin Host Contract SDK のHost Contractを任意監査する境界。
/// NicoJkPluginの実働本線（polling:reservation / NicoJK.ini / ch2 / コメント流通）には影響させない。
/// </summary>
internal static class SdkHostContractProbe
{
    public static void LogIfAvailable(IPluginContext context, Action<string> log)
    {
        try
        {
            // Keep the SDK host-contract audit reflection-only.
            // Some TvAIrPlugin.dll builds used by plugin teams do not expose the compile-time
            // IPluginReadContextV5 symbol even when the runtime host may provide GetHostContractInfo().
            // This probe must never make NicoJkPlugin fail to build or load.
            var method = context.GetType().GetMethod("GetHostContractInfo", BindingFlags.Instance | BindingFlags.Public, Type.EmptyTypes);
            if (method is not null)
            {
                var info = method.Invoke(context, null);
                if (info is not null)
                {
                    LogInfo(info, log, "reflection");
                    return;
                }
            }

            log("[SDK] hostContract=unavailable context=host_contract_probe_reflection_unavailable action=continue_without_dependency");
        }
        catch (Exception ex)
        {
            log($"[SDK] hostContract=SKIP error={ex.GetType().Name}: {Safe(ex.Message)} action=continue_without_dependency");
        }
    }

    private static void LogInfo(object info, Action<string> log, string source)
    {
        var version = GetString(info, "ContractVersion");
        var stable = GetCount(info, "StableReadContracts");
        var actions = GetCount(info, "ControlledActionContracts");
        var hidden = GetCount(info, "NotExposedByDesign");
        var tvtest = GetNestedCount(info, "TvTestHeaderReference", "AdoptedConcepts");
        log($"[SDK] hostContract=OK source={source} version={Safe(version)} stableRead={stable} controlledActions={actions} notExposed={hidden} tvTestConcepts={tvtest}");
    }

    private static string? GetString(object target, string name)
        => target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public)?.GetValue(target) as string;

    private static int GetCount(object target, string name)
    {
        var value = target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public)?.GetValue(target);
        return value switch
        {
            System.Collections.ICollection c => c.Count,
            System.Collections.IEnumerable e => e.Cast<object>().Count(),
            _ => 0
        };
    }

    private static int GetNestedCount(object target, string property, string childProperty)
    {
        var nested = target.GetType().GetProperty(property, BindingFlags.Instance | BindingFlags.Public)?.GetValue(target);
        return nested is null ? 0 : GetCount(nested, childProperty);
    }

    private static string Safe(string? value)
        => string.IsNullOrWhiteSpace(value) ? "-" : value.Replace("\r", " ").Replace("\n", " ");
}

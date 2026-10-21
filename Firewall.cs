using System.IO;
using System.Text;

namespace FastShare;

/// <summary>
/// Makes sure Windows Firewall lets other PCs reach FastShare.
///
/// Windows only lets another PC connect *in* to a program the firewall allows, and it keys that
/// on the program's exact path. The prompt it shows the first time FastShare listens is easy to
/// miss or cancel - and cancelling it writes a Block rule, which silently beats any Allow. The
/// result was "can't connect" between two PCs in the same house with nothing in FastShare to
/// say why. So before FastShare starts listening it checks its own path and, if it is not
/// allowed, adds the rule itself (one UAC prompt, through the same elevated PowerShell the
/// Defender exclusions use; FastShare itself stays un-elevated).
/// </summary>
public static class Firewall
{
    public enum State { Allowed, Missing, Blocked, Unknown }

    /// <summary>
    /// Whether inbound TCP and UDP (the local lookup) reach this exe under the profile Windows
    /// is using right now. Read through the firewall's COM interface, which - unlike the
    /// NetSecurity cmdlets - works without elevation and in any display language.
    /// </summary>
    public static State Check(string exe)
    {
        try
        {
            var type = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
            if (type is null) return State.Unknown;
            dynamic policy = Activator.CreateInstance(type)!;

            int current = policy.CurrentProfileTypes;

            // Firewall switched off for the network in use: nothing is blocked, rules or not.
            try
            {
                var anyOn = false;
                foreach (var profile in new[] { 1, 2, 4 })   // domain, private, public
                    if ((current & profile) != 0 && (bool)policy.FirewallEnabled[profile]) anyOn = true;
                if (!anyOn) return State.Allowed;
            }
            catch { /* could not tell: judge by the rules */ }

            bool tcp = false, udp = false, blocked = false;
            var target = Normalise(exe);

            foreach (dynamic rule in policy.Rules)
            {
                if (!(bool)rule.Enabled || (int)rule.Direction != 1) continue;    // 1 = inbound
                if (((int)rule.Profiles & current) == 0) continue;
                string? app = rule.ApplicationName;
                if (string.IsNullOrEmpty(app) || Normalise(app) != target) continue;

                int protocol = rule.Protocol;                                     // 6 TCP, 17 UDP, 256 any
                if ((int)rule.Action == 0) { blocked = true; continue; }         // 0 = block
                if (protocol is 6 or 256) tcp = true;
                if (protocol is 17 or 256) udp = true;
            }

            return blocked ? State.Blocked : tcp && udp ? State.Allowed : State.Missing;
        }
        catch (Exception ex)
        {
            Diagnostics.Log("firewall", "could not read the firewall rules", ex);
            return State.Unknown;
        }
    }

    /// <summary>
    /// Removes any Block rule for this exe and allows it inbound on every profile. Every
    /// profile, not only Private: FastShare's job is accepting connections from the internet,
    /// Windows files many home networks as Public, and every request still has to be approved.
    /// </summary>
    public static DefenderTune.AddResult Allow(string exe)
    {
        var path = exe.Replace("'", "''");
        var script =
            $"$p = '{path}'; " +
            "Get-NetFirewallApplicationFilter -Program $p -ErrorAction SilentlyContinue | Get-NetFirewallRule | " +
            "Where-Object { $_.Direction -eq 'Inbound' -and $_.Action -eq 'Block' } | Remove-NetFirewallRule; " +
            "New-NetFirewallRule -DisplayName 'FastShare' -Description 'Lets other PCs connect to FastShare (added by FastShare).' " +
            "-Direction Inbound -Program $p -Action Allow -Profile Any | Out-Null";

        try
        {
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            DefenderTune.RunPowerShell($"-NoProfile -NonInteractive -EncodedCommand {encoded}",
                elevated: !DefenderTune.IsElevated(), out var exitCode);

            if (exitCode == 0)
            {
                Diagnostics.Log("firewall", $"allowed {exe}");
                return DefenderTune.AddResult.Added;
            }
            Diagnostics.Log("firewall", $"adding the rule failed (exit {exitCode})");
            return DefenderTune.AddResult.Failed;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            Diagnostics.Log("firewall", "cancelled at the UAC prompt");
            return DefenderTune.AddResult.Cancelled;
        }
        catch (Exception ex)
        {
            Diagnostics.Log("firewall", "adding the rule could not be attempted", ex);
            return DefenderTune.AddResult.Failed;
        }
    }

    private static string Normalise(string path)
    {
        try { return Path.GetFullPath(Environment.ExpandEnvironmentVariables(path)).ToLowerInvariant(); }
        catch { return path.ToLowerInvariant(); }
    }
}

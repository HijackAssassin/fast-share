using System.IO;
using System.Windows;

namespace FastShare;

public partial class MainWindow
{
    // =======================================================================
    // Keeping transfer folders excluded from Windows Defender
    // =======================================================================
    //
    // Every folder files move out of (shares) or into (download destinations) is kept excluded,
    // so a transfer never pays for real-time scanning on either end - the biggest cost on
    // folders of many small files.
    //
    // FastShare stays un-elevated (running as admin breaks drag-and-drop with Explorer), so each
    // change goes through a separate elevated PowerShell - one UAC prompt per batch. An
    // un-elevated process also cannot read Defender's exclusion list, so what we have excluded
    // is recorded in config (DefenderExcluded) and that record is what "already excluded"
    // means. When FastShare does happen to run elevated, the real list is read instead and no
    // prompt appears at all.

    private readonly SemaphoreSlim _defenderGate = new(1, 1);

    /// <summary>Declined at the UAC prompt this run: not asked again until next launch.</summary>
    private bool _defenderDeclined;

    /// <summary>
    /// Excludes whichever of these folders are not already covered. Awaited by downloads, so
    /// the exclusion is in place before the first file lands.
    /// </summary>
    private async Task EnsureExcludedAsync(IEnumerable<string> folders, bool downloads = false)
    {
        if (!_config.KeepDefenderExcluded || _defenderDeclined) return;

        var wanted = folders.Where(f => !string.IsNullOrWhiteSpace(f)).ToList();
        if (wanted.Count == 0) return;

        await _defenderGate.WaitAsync();
        try
        {
            var known = DefenderTune.TryReadExclusions() ?? [];
            var missing = DefenderTune.Uncovered(wanted, _config.DefenderExcluded.Concat(known));
            if (missing.Count == 0) return;

            var result = await Task.Run(() => DefenderTune.ChangeExclusions(missing, add: true));
            switch (result)
            {
                case DefenderTune.AddResult.Added:
                    foreach (var folder in missing)
                    {
                        if (!_config.DefenderExcluded.Contains(folder, StringComparer.OrdinalIgnoreCase))
                            _config.DefenderExcluded.Add(folder);
                        if (downloads && !_config.DefenderDownloads.Contains(folder, StringComparer.OrdinalIgnoreCase))
                            _config.DefenderDownloads.Add(folder);
                    }
                    _config.Save();
                    ActivityLog.Note(TransferDirection.Upload,
                        $"Excluded from Windows Defender: {string.Join(", ", missing)}", LogKind.Network);
                    break;

                case DefenderTune.AddResult.Cancelled:
                    _defenderDeclined = true;
                    ActivityLog.Note(TransferDirection.Upload,
                        "Defender exclusion declined — transfers into these folders will be scanned as they arrive",
                        LogKind.Network);
                    break;

                default:
                    ActivityLog.Note(TransferDirection.Upload,
                        "Windows Defender refused the exclusion (it may be managed by policy or turned off)",
                        LogKind.Network);
                    break;
            }
        }
        catch (Exception ex)
        {
            Diagnostics.Log("defender", "keeping folders excluded failed", ex);
        }
        finally
        {
            _defenderGate.Release();
        }
    }

    /// <summary>
    /// Takes the exclusion off a folder that stopped being shared - only if FastShare put it
    /// there, and not if it is also a download destination or still covered by another share.
    /// </summary>
    private async Task UnexcludeAsync(string folder)
    {
        if (!_config.KeepDefenderExcluded) return;

        string key;
        try { key = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)); }
        catch { return; }

        if (!_config.DefenderExcluded.Contains(key, StringComparer.OrdinalIgnoreCase)) return;
        if (_config.DefenderDownloads.Contains(key, StringComparer.OrdinalIgnoreCase)) return;
        if (_config.Folders.Any(f => string.Equals(Path.TrimEndingDirectorySeparator(f), key,
                StringComparison.OrdinalIgnoreCase))) return;

        await _defenderGate.WaitAsync();
        try
        {
            var result = await Task.Run(() => DefenderTune.ChangeExclusions([key], add: false));
            if (result == DefenderTune.AddResult.Added)
            {
                _config.DefenderExcluded.RemoveAll(f => string.Equals(f, key, StringComparison.OrdinalIgnoreCase));
                _config.Save();
                ActivityLog.Note(TransferDirection.Upload,
                    $"Windows Defender scans {key} again (no longer shared)", LogKind.Network);
            }
        }
        catch (Exception ex)
        {
            Diagnostics.Log("defender", "removing an exclusion failed", ex);
        }
        finally
        {
            _defenderGate.Release();
        }
    }
}

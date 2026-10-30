using Mutagen.Bethesda.Synthesis.Settings;

namespace ANDArmorScanner;

public class Settings
{
    [SynthesisTooltip(
        "Plugins skipped entirely. Add mods that already ship hand-authored AND or KID files " +
        "to avoid re-tagging their records.")]
    public HashSet<string> ExcludedPlugins = new(StringComparer.OrdinalIgnoreCase)
    {
        "Advanced Nudity Detection.esp",
        "LingerieMeshes.esp",
        "[Caenarvon] Magecore.esp",
    };

    [SynthesisTooltip(
        "Also apply male-variant AND keywords (AND_Bra_Male, AND_Underwear_Male, etc.) " +
        "when the corresponding male keyword exists in the load order. Off by default: " +
        "most skimpy armor is female-only and AND's male scan ignores female keywords anyway.")]
    public bool TagMaleKeywords = false;

    [SynthesisTooltip(
        "After the patch run, print a list of armor records that carry OCF's own " +
        "OCF_ArmorMainSkimpy (\"Modesty\") keyword but ended up with no AND_ or SLA_ " +
        "keyword at all. These are OCF-flagged skimpy variants the automatic SLA_->AND_ " +
        "mapping can't reach on its own (OCF doesn't encode exposure detail) — hand-add " +
        "them to ANDArmorScanner_Backup_KID.ini instead.")]
    public bool ReportOcfSkimpyCandidates = true;
}

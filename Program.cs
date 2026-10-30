using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Synthesis;
using Noggog;

// AND Armor Scanner — assigns Advanced Nudity Detection keywords based on
// SexLab Arousal (SLA_*) keywords already present on the armor record.
//
// For each armor that has one or more SLA_ keywords but no AND_ keyword yet,
// the patcher looks up each SLA_ entry in SlaToAnd and unions the results.
// Armors with no SLA_ keywords, or already carrying any AND_ keyword, are skipped.
//
// SLA → AND mapping:
//   SLA_BraBikini          → AND_Bra               (bra-area coverage)
//   SLA_PastiesNipples     → AND_NipplePasties      (nipple coverage only)
//   SLA_ArmorHarness       → AND_Bra               (minimal bra-like coverage)
//   SLA_ArmorIllegal       → AND_Bra_NoCover        (occupies slot, zero coverage)
//   SLA_ImpossibleClothes  → AND_NearlyNaked        (body paint / pasties flavour)
//   SLA_ArmorBondage       → AND_ArmorTop_NoCover   (harness/straps, chest exposed)
//
//   SLA_ThongT             → AND_ThongT             (transparent thong, ass exposed)
//   SLA_ThongLowleg        → AND_Thong
//   SLA_ThongString        → AND_Thong
//   SLA_ThongGstring       → AND_CString            (c-string / g-string minimalist)
//   SLA_PantiesThong       → AND_Thong
//   SLA_PastiesCrotch      → AND_VaginaPasties      (genital patch only)
//   SLA_PantiesNormal      → AND_Underwear          (normal underwear)
//   SLA_ArmorHalfNakedBikini → AND_Bra + AND_Thong  (bikini set)
//
//   SLA_PantsNormal        → AND_ArmorBottom        (full genital + ass coverage)
//   SLA_MicroHotPants      → AND_Hotpants           (front only, ass exposed)
//   SLA_FullSkirt          → AND_ArmorBottom
//   SLA_MiniSkirt          → AND_Miniskirt          (dual curtain zone coverage)
//   SLA_MicroSkirt         → AND_Microskirt         (ultra-short dynamic coverage)
//   SLA_HasLeggings        → AND_ArmorBottom
//   SLA_ArmorPartBottom    → AND_ArmorBottom_NoCover (occupies slot, structurally open)
//   SLA_PelvicCurtain      → AND_PelvicCurtain      (opaque front-only drape)
//   SLA_ShowgirlSkirt      → AND_ShowgirlSkirt      (rear only, front exposed)
//
//   SLA_ArmorHalfNaked     → AND_ArmorTop           (solid partial top)
//   SLA_ArmorSpendex       → AND_ArmorTop           (solid form-fitting top)
//   SLA_ArmorLewdLeotard   → AND_ArmorTop_NoCover   (cut-out leotard, chest exposed)
//   SLA_ArmorTransparent   → AND_ArmorTopT          (transparent top layer)
//
// Not mapped (accessories / material / body-slot descriptors with no AND equivalent):
//   SLA_ArmorPretty, SLA_KillerHeels, SLA_BootsHeels, SLA_Piercing*,
//   SLA_Earrings, SLA_ArmorCape*, SLA_ArmorCurtain, SLA_AnalPlug*,
//   SLA_Vaginal*, SLA_ArmorFemaleOnly, SLA_HasSleeves, SLA_HasStockings, SLA_ArmorRubber
//
// OCF (Object Categorization Framework) gating — added when the project was
// restored after accidental deletion. OCF's own keywords describe garment TYPE
// (dress, corset, skirt...) not graduated exposure, so they can't replace the
// SLA_->AND_ table above. They're used only to hard-exclude armor that should
// never get nudity-exposure tags regardless of any stray SLA_ keyword it carries
// (child clothes, quest/uniform disguises), and to flag OCF_ArmorMainSkimpy
// ("Modesty") armors that got nothing auto-tagged, as candidates for manual entry
// in ANDArmorScanner_Backup_KID.ini.

namespace ANDArmorScanner;

public class Program
{
    // Biped slot masks (slot number - 30 = bit index)
    private static readonly BipedObjectFlag SlotBody     = BipedObjectFlag.Body;        // 32 chest/bra
    private static readonly BipedObjectFlag SlotHarness  = (BipedObjectFlag)0x00010000; // 46 harness/fishnet
    private static readonly BipedObjectFlag SlotSkirt    = (BipedObjectFlag)0x00080000; // 49 skirt
    private static readonly BipedObjectFlag SlotGenitals = (BipedObjectFlag)0x00400000; // 52 pants/genitals/thong
    private static readonly BipedObjectFlag SlotLeggings = (BipedObjectFlag)0x01000000; // 54 leggings

    // AND keywords that are only valid when a top slot (32 or 46) is present
    private static readonly HashSet<string> TopKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "AND_Bra", "AND_NipplePasties", "AND_Bra_NoCover",
        "AND_ArmorTop", "AND_ArmorTop_NoCover", "AND_ArmorTopT", "AND_NearlyNaked"
    };

    // AND keywords that are only valid when a bottom slot (49, 52, or 54) is present
    private static readonly HashSet<string> BottomKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "AND_Thong", "AND_ThongT", "AND_CString", "AND_VaginaPasties", "AND_Underwear",
        "AND_ArmorBottom", "AND_ArmorBottom_NoCover", "AND_Hotpants",
        "AND_Miniskirt", "AND_Microskirt", "AND_PelvicCurtain", "AND_ShowgirlSkirt"
    };

    // OCF keywords that hard-exclude an armor from AND_/SLA_ tagging entirely,
    // regardless of what SLA_ keywords it might otherwise carry.
    private static readonly HashSet<string> OcfExclusionEdids = new(StringComparer.OrdinalIgnoreCase)
    {
        "BYOHAdoptionClothesKeyword", // OCF "Child Clothes" — never tag
        "CWMission03ImperialGarb",    // OCF "Uniform" — quest/plot armor
        "CWMission03StormcloakGarb",  // OCF "Uniform"
        "MQ201ThalmorDisguise",       // OCF "Uniform"
    };

    private static readonly Dictionary<string, string[]> SlaToAnd =
        new(StringComparer.OrdinalIgnoreCase)
    {
        // ── Bra / chest-only ─────────────────────────────────────────────
        ["SLA_BraBikini"]            = ["AND_Bra"],                 // Covers bra area, chest exposed
        ["SLA_PastiesNipples"]       = ["AND_NipplePasties"],       // Nipple coverage only, chest exposed
        ["SLA_ArmorHarness"]         = ["AND_Bra"],                 // Assuming minimal bra-like coverage
        ["SLA_ArmorIllegal"]         = ["AND_Bra_NoCover"],         // Structural item providing zero actual coverage [cite: 24]
        ["SLA_ImpossibleClothes"]    = ["AND_NearlyNaked"],         // Body paint/pasties sets flavor [cite: 3]
        ["SLA_ArmorBondage"]         = ["AND_ArmorTop_NoCover"],    // Harnesses/straps that leave chest fully exposed [cite: 8]

        // ── Underwear / pelvis-only ───────────────────────────────────────
        ["SLA_ThongT"]               = ["AND_ThongT"],              // Transparent thong: front covered, ass exposed [cite: 48, 50]
        ["SLA_ThongLowleg"]          = ["AND_Thong"],               // Standard thong profile
        ["SLA_ThongString"]          = ["AND_Thong"],               // String variant
        ["SLA_ThongGstring"]         = ["AND_CString"],             // C-string / G-string minimalist profile
        ["SLA_PantiesThong"]         = ["AND_Thong"],               // Thong profile
        ["SLA_PastiesCrotch"]        = ["AND_VaginaPasties"],       // Genital patch only, ass exposed
        ["SLA_PantiesNormal"]        = ["AND_Underwear"],           // Normal panties

        // ── Bikini = bra + underwear ──────────────────────────────────────
        ["SLA_ArmorHalfNakedBikini"] = ["AND_Bra", "AND_Thong"],    // More accurate than full underwear [cite: 20, 48]

        // ── Bottom coverage ───────────────────────────────────────────────
        ["SLA_PantsNormal"]          = ["AND_ArmorBottom"],         // Solid, full genital/ass coverage
        ["SLA_MicroHotPants"]        = ["AND_Hotpants"],            // Covers front only, ass fully exposed
        ["SLA_FullSkirt"]            = ["AND_ArmorBottom"],         // Treats full length coverage as solid
        ["SLA_MiniSkirt"]            = ["AND_Miniskirt"],           // Dual curtain zone coverage
        ["SLA_MicroSkirt"]           = ["AND_Microskirt"],          // Ultra-short dynamic coverage
        ["SLA_HasLeggings"]          = ["AND_ArmorBottom"],         // Solid structural lower coverage
        ["SLA_ArmorPartBottom"]      = ["AND_ArmorBottom_NoCover"], // Occupies slot but structurally open [cite: 9]
        ["SLA_PelvicCurtain"]        = ["AND_PelvicCurtain"],       // Opaque front-only drape
        ["SLA_ShowgirlSkirt"]        = ["AND_ShowgirlSkirt"],       // Covers rear only, front exposed

        // ── Body / top coverage ───────────────────────────────────────────
        ["SLA_ArmorHalfNaked"]       = ["AND_ArmorTop"],            // Solid partial top
        ["SLA_ArmorSpendex"]         = ["AND_ArmorTop"],            // Solid form-fitting top
        ["SLA_ArmorLewdLeotard"]     = ["AND_ArmorTop_NoCover"],    // Cut-out leotards exposing the chest [cite: 8]
        ["SLA_ArmorTransparent"]     = ["AND_ArmorTopT"],           // Passes scanner logic to underlying layers [cite: 18]
    };

    private static Lazy<Settings> _settings = null!;

    public static async Task<int> Main(string[] args)
    {
        return await SynthesisPipeline.Instance
            .AddPatch<ISkyrimMod, ISkyrimModGetter>(RunPatch)
            .SetAutogeneratedSettings("Settings", "settings.json", out _settings)
            .SetTypicalOpen(GameRelease.SkyrimSE, "ANDArmorScanner.esp")
            .Run(args);
    }

    public static void RunPatch(IPatcherState<ISkyrimMod, ISkyrimModGetter> state)
    {
        var settings = _settings.Value;

        var andKeywords = new Dictionary<string, FormKey>(StringComparer.OrdinalIgnoreCase);
        var slaKeywords = new Dictionary<FormKey, string>();
        var sosRevealingKey = FormKey.Null;
        var ocfSkimpyKey = FormKey.Null;
        var exclusionKeys = new HashSet<FormKey>();

        foreach (var kw in state.LoadOrder.PriorityOrder.Keyword().WinningOverrides())
        {
            if (kw.EditorID == null) continue;
            if (kw.EditorID.StartsWith("AND_", StringComparison.OrdinalIgnoreCase))
                andKeywords[kw.EditorID] = kw.FormKey;
            if (kw.EditorID.StartsWith("SLA_", StringComparison.OrdinalIgnoreCase))
                slaKeywords[kw.FormKey] = kw.EditorID;
            if (kw.EditorID.Equals("SOS_Revealing", StringComparison.OrdinalIgnoreCase))
                sosRevealingKey = kw.FormKey;
            if (kw.EditorID.Equals("OCF_ArmorMainSkimpy", StringComparison.OrdinalIgnoreCase))
                ocfSkimpyKey = kw.FormKey;
            if (OcfExclusionEdids.Contains(kw.EditorID))
                exclusionKeys.Add(kw.FormKey);
        }

        if (andKeywords.Count == 0)
        {
            Console.WriteLine("[ERROR] No AND_ keywords found. Ensure 'Advanced Nudity Detection.esp' is active.");
            return;
        }
        if (sosRevealingKey == FormKey.Null)
            Console.WriteLine("[WARN] SOS_Revealing keyword not found — slot 52 SOS tagging disabled.");
        if (ocfSkimpyKey == FormKey.Null)
            Console.WriteLine("[WARN] OCF_ArmorMainSkimpy keyword not found — manual-KID candidate report disabled.");

        Console.WriteLine($"Found {andKeywords.Count} AND_ keywords, {slaKeywords.Count} SLA_ keywords, {exclusionKeys.Count} OCF exclusion keywords.");

        var andFormKeys = new HashSet<FormKey>(andKeywords.Values);

        int tagged = 0, skippedExcluded = 0, skippedOcfExcluded = 0, skippedNothing = 0;
        var candidatesForManualKid = new List<(ModKey ModKey, string Label)>();

        foreach (var armorCtx in state.LoadOrder.PriorityOrder.Armor().WinningContextOverrides())
        {
            var armor = armorCtx.Record;

            if (armor.MajorFlags.HasFlag(Armor.MajorFlag.NonPlayable)) continue;
            if (armor.BodyTemplate == null) continue;

            bool isOcfExcluded = armor.Keywords?.Any(k => exclusionKeys.Contains(k.FormKey)) == true;
            if (isOcfExcluded)
            {
                skippedOcfExcluded++;
                continue;
            }

            if (settings.ExcludedPlugins.Contains(armorCtx.ModKey.FileName))
            {
                skippedExcluded++;
                continue;
            }

            var slotFlags    = armor.BodyTemplate.FirstPersonFlags;
            bool hasTopSlot  = slotFlags.HasFlag(SlotBody) || slotFlags.HasFlag(SlotHarness);
            bool hasSlot52   = slotFlags.HasFlag(SlotGenitals);
            bool hasBottomSlot = hasSlot52 || slotFlags.HasFlag(SlotSkirt) || slotFlags.HasFlag(SlotLeggings);

            // ── SOS_Revealing: assign to all slot 52 armors that don't have it ──
            bool addSos = hasSlot52
                && sosRevealingKey != FormKey.Null
                && !(armor.Keywords?.Any(k => k.FormKey == sosRevealingKey) == true);

            // ── AND_ keywords: only assign if no AND_ keyword already present ──
            var toAdd = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool hasAnd = armor.Keywords?.Any(k => andFormKeys.Contains(k.FormKey)) == true;
            if (!hasAnd)
            {
                if (armor.Keywords != null)
                {
                    foreach (var kwLink in armor.Keywords)
                    {
                        if (!slaKeywords.TryGetValue(kwLink.FormKey, out var slaEdid)) continue;
                        if (!SlaToAnd.TryGetValue(slaEdid, out var andEdids)) continue;
                        foreach (var andEdid in andEdids)
                            toAdd.Add(andEdid);
                    }
                }

                // Slot guardrails
                toAdd.RemoveWhere(kw =>
                    (TopKeywords.Contains(kw)    && !hasTopSlot) ||
                    (BottomKeywords.Contains(kw) && !hasBottomSlot));

                // Male variants
                if (settings.TagMaleKeywords)
                {
                    foreach (var edid in toAdd.ToList())
                    {
                        var male = edid + "_Male";
                        if (andKeywords.ContainsKey(male))
                            toAdd.Add(male);
                    }
                }
            }

            // ── OCF_ArmorMainSkimpy armors that still got nothing auto-tagged are
            //    candidates for a hand-written KID entry (no SLA_ keyword for the
            //    mapping above to translate). ──
            if (settings.ReportOcfSkimpyCandidates
                && toAdd.Count == 0
                && !hasAnd
                && ocfSkimpyKey != FormKey.Null
                && armor.Keywords?.Any(k => k.FormKey == ocfSkimpyKey) == true)
            {
                candidatesForManualKid.Add((armorCtx.ModKey, armor.EditorID ?? armor.FormKey.ToString()));
            }

            if (toAdd.Count == 0 && !addSos)
            {
                skippedNothing++;
                continue;
            }

            // Resolve AND_ FormKeys
            var resolved = new List<FormLink<IKeyword>>();
            bool anyMissing = false;
            foreach (var edid in toAdd)
            {
                if (andKeywords.TryGetValue(edid, out var fk))
                    resolved.Add(new FormLink<IKeyword>(fk));
                else
                {
                    Console.WriteLine($"  [WARN] AND keyword not in load order: {edid}");
                    anyMissing = true;
                }
            }
            if (anyMissing) continue;

            var patch = state.PatchMod.Armors.GetOrAddAsOverride(armor);
            patch.Keywords ??= new ExtendedList<IFormLinkGetter<IKeywordGetter>>();
            foreach (var link in resolved)
                patch.Keywords.Add(link);
            if (addSos)
                patch.Keywords.Add(new FormLink<IKeyword>(sosRevealingKey));

            var label    = armor.EditorID ?? armor.FormKey.ToString();
            var andPart  = toAdd.Count > 0 ? string.Join(", ", toAdd) : "-";
            var sosPart  = addSos ? " + SOS_Revealing" : "";
            Console.WriteLine($"  [TAG] {armorCtx.ModKey.FileName} :: {label}  →  {andPart}{sosPart}");
            tagged++;
        }

        Console.WriteLine();
        Console.WriteLine("=== AND Armor Scanner complete ===");
        Console.WriteLine($"  Tagged:                       {tagged}");
        Console.WriteLine($"  Nothing to add:               {skippedNothing}");
        Console.WriteLine($"  Excluded plugin:               {skippedExcluded}");
        Console.WriteLine($"  Excluded (OCF child/uniform): {skippedOcfExcluded}");

        if (settings.ReportOcfSkimpyCandidates && candidatesForManualKid.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("=== OCF_ArmorMainSkimpy armors with no AND_/SLA_ coverage (manual KID candidates) ===");
            foreach (var candidate in candidatesForManualKid
                         .OrderBy(c => c.ModKey.FileName.String, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(c => c.Label, StringComparer.OrdinalIgnoreCase))
            {
                Console.WriteLine($"  {candidate.ModKey.FileName} :: {candidate.Label}");
            }
            Console.WriteLine($"  Total: {candidatesForManualKid.Count} — add these to ANDArmorScanner_Backup_KID.ini by hand.");
        }
    }
}

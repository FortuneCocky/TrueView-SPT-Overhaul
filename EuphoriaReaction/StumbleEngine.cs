using System;
using System.Collections.Generic;
using System.Reflection;
using EFT;
using EFT.Ballistics;
using EFT.InventoryLogic;
using UnityEngine;

namespace EuphoriaReaction;

/// <summary>Everything the damage evaluator decided about one hit.</summary>
internal struct StumbleResult
{
    public float Score;        // 0..1 "Stumble Probability Score"
    public float Intensity;    // 0..~1.8, how hard the body is shoved (feeds TriggerProceduralStumble)
    public bool Heavy;         // large-calibre / explosion: stumble is guaranteed
    public bool LegBuckle;     // leg hit over the damage threshold: the knee gives out
    public bool Flinch;        // low-energy flesh hit: brief local flinch only
    public bool Sprinting;     // victim was sprinting when hit
    public bool Always;        // a sniper rifle's hit: stumbles without the roll
    public float Blast;        // a close shotgun blast: the stumble threshold is divided by this (0/1 = none)
    public float Ballistic;    // shove multiplier from the round's momentum (1 = reference round)
    public string Why;        // one-line breakdown for the log
}

/// <summary>
/// Damage evaluation: turns a bullet hit into a Stumble Probability Score.
///
///   score = zone * kinetic * armourFactor * movementFactor            (clamped 0..1)
///
///  zone      how easily that body part unbalances a man (head/legs high, arms low)
///  kinetic   how much momentum the round carries: a caliber weight (table, or derived
///            from raw damage when the ammo can't be read), NOT the post-armour damage
///  armour    blocked  -> blunt-force trauma: the energy was not spent piercing anything, so
///                        it scales with how close the bullet came to penetrating
///                        (penetration power / armour class*10), is mitigated by heavy vests
///                        (class 5/6, less so against big calibres) and by armour condition
///            pierced  -> energy was spent going through the plate (x0.85) but the flesh
///                        damage that reached the body amplifies trauma
///            none     -> flesh hit; cheap rounds only flinch
///  hitbox    the collider the round hit, checked against the armour he wears: a bare hitbox (neck,
///            face, pelvis, sides or chest with no vest over it, limbs) knocks him back harder and
///            is never capped to a flinch; a round his armour stops is a smaller blunt shove
///  movement  sprinting men are easy to tip over, jogging ones less so
///
/// Every game-struct member that is not 100% stable across EFT builds is read through a
/// cached reflection accessor, so a renamed field degrades gracefully to a default instead
/// of breaking the patch. The common path allocates nothing after the first hit.
/// </summary>
internal static class StumbleEngine
{
    private const BindingFlags All = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy;

    // ------------------------------------------------------------ reflection helpers (cached)

    private static readonly Dictionary<(Type, string), MemberInfo> _members = new Dictionary<(Type, string), MemberInfo>();
    private static readonly MemberInfo _none = typeof(StumbleEngine).GetMethod(nameof(Read), BindingFlags.Static | BindingFlags.NonPublic);

    private static object Raw(object o, string name)
    {
        if (o == null) return null;
        Type t = o.GetType();
        if (!_members.TryGetValue((t, name), out MemberInfo m))
        {
            m = (MemberInfo)t.GetField(name, All) ?? t.GetProperty(name, All) ?? _none;
            _members[(t, name)] = m;
        }
        try
        {
            if (m is FieldInfo f) return f.GetValue(o);
            if (m is PropertyInfo p && p.GetIndexParameters().Length == 0) return p.GetValue(o, null);
        }
        catch { }
        return null;
    }

    internal static float Read(object o, string name, float def = 0f)
    {
        object v = Raw(o, name);
        if (v == null) return def;
        try { return Convert.ToSingle(v); } catch { return def; }
    }

    private static string ReadStr(object o, string name)
    {
        object v = Raw(o, name);
        return v == null ? "" : v.ToString();
    }

    private static readonly List<ArmorComponent> _worn = new List<ArmorComponent>(20);

    /// Whether any armour he has on covers this hitbox (vest, plate carrier, helmet, collar ...).
    private static bool Covers(Player victim, EBodyPartColliderType collider)
    {
        try
        {
            _worn.Clear();
            victim.Inventory.GetPutOnArmorsNonAlloc(_worn);
            foreach (ArmorComponent a in _worn)
                if (a != null && a.ShotMatches(collider, (EArmorPlateCollider)0)) return true;
        }
        catch { }
        return false;
    }

    // ------------------------------------------------------------ armour lookup (per victim, cached 2 s)

    private struct ArmourInfo { public float Time, VestClass, VestCond, HelmClass, HelmCond; }
    private static readonly Dictionary<int, ArmourInfo> _armour = new Dictionary<int, ArmourInfo>();
    private static Type _armourComponent;
    private static bool _armourTypeSearched;

    private static Type ArmourComponentType()
    {
        if (_armourTypeSearched) return _armourComponent;
        _armourTypeSearched = true;
        try
        {
            foreach (Type x in typeof(Player).Assembly.GetTypes())
                if (x.Name == "ArmorComponent") { _armourComponent = x; break; }
        }
        catch { }
        return _armourComponent;
    }

    /// <summary>Armour class (0 = none) and condition (0..1) of the item in an equipment slot.</summary>
    private static void SlotArmour(object equipment, string slotName, out float cls, out float cond)
    {
        cls = 0f; cond = 1f;
        try
        {
            MethodInfo gs = equipment.GetType().GetMethod("GetSlot", All);
            if (gs == null) return;
            Type pt = gs.GetParameters()[0].ParameterType;
            object slot = gs.Invoke(equipment, new[] { Enum.Parse(pt, slotName) });
            object item = Raw(slot, "ContainedItem");
            if (item == null) return;
            // The template carries the class; the component carries the live durability.
            cls = Read(Raw(item, "Template"), "ArmorClass", 0f);
            Type ac = ArmourComponentType();
            if (ac == null) return;
            foreach (MethodInfo mi in item.GetType().GetMethods(All))
            {
                if (mi.Name != "GetItemComponent" || !mi.IsGenericMethodDefinition || mi.GetParameters().Length != 0) continue;
                object comp = mi.MakeGenericMethod(ac).Invoke(item, null);
                if (comp == null) break;
                if (cls <= 0f) cls = Read(comp, "ArmorClass", 0f);
                object rep = Raw(comp, "Repairable");
                float max = Read(rep, "MaxDurability", 0f);
                if (max > 0.5f) cond = Mathf.Clamp01(Read(rep, "Durability", max) / max);
                break;
            }
        }
        catch { }
    }

    private static ArmourInfo ArmourOf(Player p)
    {
        int id = p.GetInstanceID();
        if (_armour.TryGetValue(id, out ArmourInfo a) && Time.time - a.Time < 2f) return a;
        a = new ArmourInfo { Time = Time.time, VestCond = 1f, HelmCond = 1f };
        object eq = Raw(p, "Equipment");
        if (eq != null)
        {
            SlotArmour(eq, "ArmorVest", out a.VestClass, out a.VestCond);
            SlotArmour(eq, "Headwear", out a.HelmClass, out a.HelmCond);
        }
        if (_armour.Count > 256) _armour.Clear(); // one per bot ever hit; each only counts for 2 s
        _armour[id] = a;
        return a;
    }

    // ------------------------------------------------------------ calibre weights

    // Substring of the ammo's caliber id -> momentum weight. 1.0 ~ a 7.62x39 round.
    // The big ones (>= HeavyWeight) always unbalance a man, whatever he wears.
    private static readonly (string key, float w)[] Calibres =
    {
        ("9x18", 0.45f), ("9x19", 0.50f), ("9x21", 0.55f), ("9x33", 0.65f), ("9x39", 0.75f),
        ("57x28", 0.40f), ("46x30", 0.40f), ("1143x23", 0.60f), ("762x25", 0.50f), ("762x35", 0.70f),
        ("545x39", 0.55f), ("556x45", 0.60f), ("762x39", 0.80f), ("68x51", 1.00f), ("762x51", 1.10f),
        ("366", 0.85f), ("127x55", 1.15f), ("12g", 0.80f), ("20g", 0.70f), ("23x75", 0.95f),
        ("762x54", 1.50f), ("338", 1.80f), ("86x70", 1.80f), ("127x108", 1.90f),
    };
    private const float HeavyWeight = 1.40f;

    private static float CaliberWeight(string caliber, float rawDamage)
    {
        if (!string.IsNullOrEmpty(caliber))
        {
            string c = caliber.ToLowerInvariant();
            for (int i = 0; i < Calibres.Length; i++)
                if (c.Contains(Calibres[i].key)) return Calibres[i].w;
        }
        // Unknown ammo: derive from damage. 15 dmg ~ 0.5, 45 ~ 0.85, 80+ ~ 1.4+
        return Mathf.Clamp(0.30f + rawDamage / 70f, 0.30f, 1.9f);
    }

    // ------------------------------------------------------------ damage types that never stumble

    private static bool IsDot(string t)
    {
        switch (t)
        {
            case "Bloodloss": case "LightBleeding": case "HeavyBleeding": case "Poison": case "Dehydration":
            case "Exhaustion": case "Medicine": case "Radiation": case "Stimulator": case "Barbed": case "Existence":
            case "Undefined": case "Fall": // a landing is the controller's job
                return true;
        }
        return false;
    }

    // ------------------------------------------------------------ recent hits (dev panel)

    internal static readonly List<string> Recent = new List<string>();
    internal static void Report(string line)
    {
        Recent.Add($"[{Time.time:F1}] {line}");
        if (Recent.Count > 14) Recent.RemoveAt(0);
    }

    // ------------------------------------------------------------ boss / health

    private static MethodInfo _hpMethod;
    private static object _hpArg;
    private static bool _hpSearched, _hpWarned;

    /// Boss test by role name: every "boss*" role plus the cultist priest. Followers/guards are ordinary bots.
    internal static bool IsBoss(Player p)
    {
        try
        {
            object role = BotActions.Get(BotActions.Get(BotActions.Get(BotActions.Get(p, "Profile"), "Info"), "Settings"), "Role");
            string r = role == null ? "" : role.ToString().ToLowerInvariant();
            return r.StartsWith("boss") || r == "sectantpriest";
        }
        catch { return false; }
    }

    /// Whole-body health 0..1 (1 when it cannot be read, so a boss is never stumbled by mistake).
    internal static float HpFraction(Player p)
    {
        try
        {
            object hc = BotActions.Get(p, "HealthController");
            if (hc == null) return 1f;
            if (!_hpSearched)
            {
                _hpSearched = true;
                foreach (MethodInfo m in hc.GetType().GetMethods(All))
                {
                    if (m.Name != "GetBodyPartHealth") continue;
                    ParameterInfo[] ps = m.GetParameters();
                    if (ps.Length < 1 || !ps[0].ParameterType.IsEnum) continue;
                    _hpMethod = m; _hpArg = Enum.Parse(ps[0].ParameterType, "Common");
                    break;
                }
            }
            if (_hpMethod == null)
            {
                if (!_hpWarned) { _hpWarned = true; EuphoriaPlugin.Log("Delirium: boss gate: GetBodyPartHealth not found — bosses will never stumble"); }
                return 1f;
            }
            ParameterInfo[] pr = _hpMethod.GetParameters();
            var args = new object[pr.Length];
            args[0] = _hpArg;
            for (int i = 1; i < pr.Length; i++) args[i] = pr[i].HasDefaultValue ? pr[i].DefaultValue : (pr[i].ParameterType.IsValueType ? Activator.CreateInstance(pr[i].ParameterType) : null);
            object v = _hpMethod.Invoke(hc, args);
            float max = Read(v, "Maximum", 0f), cur = Read(v, "Current", max);
            return max > 0.5f ? Mathf.Clamp01(cur / max) : 1f;
        }
        catch { return 1f; }
    }

    // ------------------------------------------------------------ the evaluator

    /// <summary>
    /// Score one hit. Returns false when the hit can never stumble (bleeding, poison, ...).
    /// </summary>
    public static bool Evaluate(Player victim, DamageInfo di, EBodyPart part, EBodyPartColliderType collider, out StumbleResult r)
    {
        r = default;
        string dtype = di.DamageType.ToString();
        if (IsDot(dtype)) return false;

        EuphoriaPlugin cfg = EuphoriaPlugin.Instance;
        object box = di; // DamageInfo is a struct: box it once for the reflection reads

        // ---- 1. what actually hit him ------------------------------------------------
        float damage = di.Damage;
        float pen = di.PenetrationPower;
        float didBody = Read(box, "DidBodyDamage");       // damage that reached the flesh
        float armourDmg = Read(box, "ArmorDamage");       // durability the plate lost
        bool armourTouched = armourDmg > 0.01f || Read(box, "DidArmorDamage") > 0.01f
                             || ReadStr(box, "BlockedBy").Length > 0 || ReadStr(box, "DeflectedBy").Length > 0;
        bool deflected = ReadStr(box, "DeflectedBy").Length > 0;
        bool pierced = armourTouched && didBody > 0.5f;   // armour hit AND flesh damage came through
        bool blocked = armourTouched && !pierced;         // the plate stopped (or bounced) it

        // Ammo template: the *unmitigated* damage and the caliber id.
        object ammo = Raw(Raw(box, "Weapon"), "CurrentAmmoTemplate");
        float ammoDmg = Read(ammo, "Damage");
        string caliber = ReadStr(ammo, "Caliber");
        float raw = Mathf.Max(Mathf.Max(damage, didBody), ammoDmg); // never score a blocked round by its residue
        if (pen <= 0f) pen = Read(ammo, "PenetrationPower");

        // the hitbox: is anything he wears over it? (None = unknown: the armour result alone decides)
        bool known = collider != EBodyPartColliderType.None;
        bool covered = known && Covers(victim, collider);
        bool open = known && !covered && !armourTouched;

        float cal = CaliberWeight(caliber, raw);
        bool heavy = cal >= HeavyWeight || raw >= 85f;

        // ---- 2. zone: how much each part unbalances him ---------------------------------
        string zoneName = part.ToString();
        bool head = zoneName == "Head";
        bool leg = zoneName == "LeftLeg" || zoneName == "RightLeg";
        bool arm = zoneName == "LeftArm" || zoneName == "RightArm";
        float zone = head ? 0.60f : leg ? 0.50f : arm ? 0.20f : zoneName == "Stomach" ? 0.50f : 0.45f;

        // ---- 3. kinetic term ------------------------------------------------------------
        // 0.55 floor so a pistol round still counts; +0.9*cal so a heavy round roughly doubles it.
        float score = zone * (0.55f + 0.90f * cal);
        string why;

        ArmourInfo ar = ArmourOf(victim);
        float armourClass = head ? ar.HelmClass : ar.VestClass;
        float armourCond = head ? ar.HelmCond : ar.VestCond;

        if (!leg && !arm && blocked)
        {
            // ---- 4a. BLOCKED: blunt-force transfer. Nothing was spent piercing, so the
            // whole impulse arrives. ratio ~1 means the round nearly went through.
            float ratio = Mathf.Clamp01(pen / Mathf.Max(10f, armourClass * 10f + 1f));
            float blunt = Mathf.Lerp(0.55f, 1.35f, ratio);
            // Heavy vests (class 5/6) soak blunt force -- unless the round itself is heavy.
            float vestMit = 1f - 0.09f * Mathf.Max(0f, armourClass - 2f);
            vestMit = Mathf.Lerp(vestMit, 1f, Mathf.Clamp01((cal - 0.9f) / 0.7f));
            // Worn-out plates transmit more.
            float wear = Mathf.Lerp(1.20f, 1f, armourCond);
            float stop = cfg != null ? cfg.ArmourStop.Value : 0.6f; // a stopped round is a smaller shove than a bare hit
            score *= 1.30f * blunt * vestMit * wear * stop;
            if (head) score = Mathf.Max(score * 1.5f, deflected ? 0.70f : 0.55f); // helmet knock / ricochet: disorienting
            why = $"blocked blunt r={ratio:F2} cls={armourClass:F0} mit={vestMit:F2} stop={stop:F2}";
        }
        else if (!leg && !arm && pierced)
        {
            // ---- 4b. PIERCED: energy went into the plate (x0.85), but the wound adds trauma.
            float trauma = 1f + 0.60f * Mathf.Clamp01(didBody / 50f);
            score *= 0.85f * trauma;
            if (head) score = 1f; // a penetrated head that is still alive: he is out on his feet
            why = $"pierced trauma={trauma:F2}";
        }
        else
        {
            // ---- 4c. FLESH (no armour in the way; legs and arms always land here).
            float trauma = 1f + 0.50f * Mathf.Clamp01(raw / 60f);
            score *= trauma;
            float bare = open ? (cfg != null ? cfg.OpenHitbox.Value : 1.5f) : 1f;
            score *= bare;
            // Low-energy, fast rounds hitting flesh: a brief flinch, not a stagger (not on a bare hitbox: nothing took the blow).
            if (!open && !heavy && raw < 30f && !leg) { score = Mathf.Min(score, 0.42f); r.Flinch = true; }
            float legMin = cfg != null ? cfg.LegBuckleDamage.Value : 22f;
            if (leg && raw >= legMin)
            {
                // Legs bypass armour entirely; enough damage and the knee gives out.
                score = Mathf.Max(score, 0.90f);
                r.LegBuckle = true;
            }
            why = $"flesh trauma={trauma:F2}{(open ? $" bare x{bare:F2}" : "")}{(r.LegBuckle ? " LEG" : "")}";
        }

        // ---- 5. explosions / melee ---------------------------------------------------------
        if (dtype == "Explosion" || dtype == "Landmine" || dtype == "GrenadeFragment") { score = Mathf.Max(score, 0.95f); heavy = heavy || dtype == "Explosion"; why += " explosion"; }
        else if (dtype == "Melee") { score = Mathf.Max(score, 0.55f); why += " melee"; }

        // ---- 6. heavy rounds always unbalance -------------------------------------------------
        if (heavy) { score = 1f; why += " HEAVY"; }

        // ---- 6b. a shotgun up close: the whole blast lands as one blow, not as single pellets -------
        float blast = ShotgunBlast(di, victim, out int pellets, out float near);
        if (blast > 1f)
        {
            if (near > 0.3f) r.Flinch = false; // a point-blank blast is no flinch, however small each pellet
            score = Mathf.Clamp01(score * blast + 0.2f * near);
            r.Blast = blast; // and the roll gets easier: the score alone is capped at 1
            why += $" blast x{blast:F2} ({pellets} pellets, close {near:F2})";
        }

        // ---- 6c. a sniper rifle: every hit knocks him off balance -------------------------------
        if (IsSniper(di))
        {
            r.Flinch = false;
            r.Always = true;
            score = 1f;
            why += " SNIPER";
        }

        // ---- 7. movement: sprinting men are easy to knock over ----------------------------------
        Vector3 v = Vector3.zero;
        try { v = victim.Velocity; } catch { }
        v.y = 0f;
        float speed = v.magnitude;
        float sprintBoost = cfg != null ? cfg.SprintStumble.Value : 1.8f;
        bool sprinting = speed >= 4.2f;
        if (sprinting) score = score * sprintBoost + 0.25f;
        else if (speed >= 2.0f) score *= 1.25f;
        r.Sprinting = sprinting;

        r.Score = Mathf.Clamp01(score);
        r.Heavy = heavy;
        // Intensity: flinches barely move him, heavy rounds shove hard.
        float inten = Mathf.Lerp(0.25f, 1.25f, r.Score);
        if (r.Flinch) inten = Mathf.Min(inten, 0.35f);
        if (heavy) inten *= 1.4f;
        if (sprinting) inten *= 1.25f;
        r.Intensity = inten;
        r.Ballistic = BallisticShove(di, victim, blocked && !leg && !arm, pierced && !leg && !arm, out string shove);
        r.Why = $"{zoneName}/{collider}{(covered ? " (armoured)" : known ? " (bare)" : "")} {dtype} raw={raw:F0} pen={pen:F0} cal={cal:F2} v={speed:F1} -> {r.Score:F2} ({why}) {shove}";
        return true;
    }

    // ------------------------------------------------------------ ballistic shove

    /// A shotgun (more than one pellet a shot) up close: how much harder its blast lands (1 = not a
    /// close blast). `near` is 1 at point blank (4 m or less) down to 0 at the config's range.
    internal static float ShotgunBlast(DamageInfo di, Player victim, out int pellets, out float near)
    {
        near = 0f;
        object ammo = Raw(Raw(di, "Weapon"), "CurrentAmmoTemplate");
        pellets = (int)Read(ammo, "ProjectileCount", 1f);
        if (pellets <= 1) return 1f;
        var cfg = EuphoriaPlugin.Instance;
        float range = cfg != null ? cfg.ShotgunRange.Value : 15f, force = cfg != null ? cfg.ShotgunForce.Value : 2.5f;
        float dist = range;
        try { if (di.Player?.iPlayer != null && victim != null) dist = Vector3.Distance(di.Player.iPlayer.Position, victim.Position); } catch { }
        near = 1f - Mathf.Clamp01((dist - 4f) / Mathf.Max(0.5f, range - 4f));
        return 1f + near * (force - 1f);
    }

    /// A bolt-action sniper rifle (EFT's "sniperRifle" class; marksman rifles too when the config says so).
    internal static bool IsSniper(DamageInfo di)
    {
        var cfg = EuphoriaPlugin.Instance;
        if (cfg != null && !cfg.SniperAlways.Value) return false;
        object weapon = Raw(di, "Weapon");
        string cls = (Raw(weapon, "WeapClass") ?? Raw(Raw(weapon, "Template"), "weapClass")) as string;
        if (cls == null) return false;
        return cls == "sniperRifle" || (cls == "marksmanRifle" && (cfg?.SniperMarksman.Value ?? false));
    }

    // Momentum (kg*m/s) that shoves at 1x: roughly 7.62x39 PS at 50 m (7.9 g at ~690 m/s).
    private const float RefMomentum = 5.4f;

    /// <summary>
    /// How hard this round physically shoves, relative to the reference round. Momentum at impact
    /// (bullet mass x muzzle velocity, scaled by the gun's barrel/mod speed factor and bled off over
    /// range by the ballistic coefficient), times how much of it stays in the body: a plate that stops
    /// the round takes all of it, a round that pierces a plate carries some on through, and
    /// high-penetration rounds over-penetrate bare flesh. Damage ("strength") adds wound shock on top.
    /// Returns 1 when the ammo can't be read.
    /// </summary>
    internal static float BallisticShove(DamageInfo di, Player victim, bool blocked, bool pierced, out string why)
    {
        object box = di;
        object weapon = Raw(box, "Weapon");
        object ammo = Raw(weapon, "CurrentAmmoTemplate");
        float massG = Read(ammo, "BulletMassGram"), v0 = Read(ammo, "InitialSpeed");
        if (massG <= 0f || v0 <= 0f) { why = "shove x1.00 (no ammo data)"; return 1f; }

        float speedFactor = Read(weapon, "SpeedFactor", 1f); // barrel length / muzzle devices
        if (speedFactor < 0.3f || speedFactor > 3f) speedFactor = 1f;
        float bc = Mathf.Max(0.05f, Read(ammo, "BallisticCoeficient", 0.3f));
        float dist = 0f;
        try { if (di.Player?.iPlayer != null && victim != null) dist = Vector3.Distance(di.Player.iPlayer.Position, victim.Position); } catch { }
        float v = v0 * speedFactor * Mathf.Exp(-0.00038f / bc * dist);
        float momentum = massG * 0.001f * v;

        float pen = di.PenetrationPower > 0f ? di.PenetrationPower : Read(ammo, "PenetrationPower");
        float keep = blocked ? 0.7f : pierced ? 0.6f : Mathf.Lerp(1f, 0.5f, Mathf.Clamp01((pen - 15f) / 40f));
        float dmg = Mathf.Max(di.Damage, Read(ammo, "Damage"));
        float strength = Mathf.Lerp(0.85f, 1.2f, Mathf.Clamp01(dmg / 90f));

        // a close shotgun blast shoves with most of its pellets' momentum, not one pellet's. EFT's pellet
        // weights are far too light (0.7 g for 12 ga buckshot, a real pellet is ~3.5 g): each pellet gets
        // its share of a real load for the gauge
        float blast = ShotgunBlast(di, victim, out int pellets, out float near);
        if (pellets > 1)
        {
            string cal = Raw(ammo, "Caliber") as string ?? "";
            float load = cal.Contains("12g") ? 32f : cal.Contains("20g") ? 25f : cal.Contains("23x75") ? 45f : massG * pellets;
            massG = Mathf.Max(massG, load / pellets);
            momentum = massG * 0.001f * v * (1f + near * Mathf.Max(0f, pellets * 0.8f - 1f));
        }
        float mul = Mathf.Clamp(Mathf.Pow(momentum * keep / RefMomentum, 0.75f) * strength, 0.25f, 3f);
        if (IsSniper(di)) mul = Mathf.Min(mul * (EuphoriaPlugin.Instance?.SniperForce.Value ?? 1.5f), 3f); // a sniper round hits like a hammer
        if (blast > 1f) mul = Mathf.Min(mul * (1f + (blast - 1f) * 0.5f), 3f); // and the close-range force shoves harder too
        why = $"shove x{mul:F2} (m={massG:F1}g v={v:F0}m/s gun={speedFactor:F2} d={dist:F0}m p={momentum:F1} keep={keep:F2} dmg={dmg:F0}{(pellets > 1 ? $" pellets={pellets}" : "")})";
        return mul;
    }
}

/// <summary>
/// Public face of the stumble system.
/// </summary>
public static class EuphoriaEngine
{
    /// <summary>
    /// Hand the victim's body to the Delirium agent and shove it. <paramref name="intensity"/>:
    /// ~0.2 = a flinch, ~0.7 = a real stagger, 1.2+ = a heavy knock-down.
    /// </summary>
    public static void TriggerProceduralStumble(Player target, Vector3 forceDirection, float intensity)
    {
        Vector3 at = target != null ? target.Transform.position + Vector3.up * 1.3f : Vector3.zero;
        TriggerProceduralStumble(target, forceDirection, intensity, at, false, null, 0f);
    }

    internal static void TriggerProceduralStumble(Player target, Vector3 forceDirection, float intensity, Vector3 hitPoint,
                                                  bool legBuckle, Func<Vector3> aimAt, float aimBackChance, float ballistic = 1f)
    {
        EuphoriaPlugin entry = EuphoriaPlugin.Instance;
        if (entry == null || target == null) return;
        NmBody body = target.GetComponent<NmBody>();
        if (body == null) body = target.gameObject.AddComponent<NmBody>();
        if (!body.Active && !body.Attach(target))
        {
            EuphoriaPlugin.Log($"Delirium: attach failed on {target.ProfileId}");
            UnityEngine.Object.Destroy(body);
            return;
        }
        // intensity -> impulse. 0.25 -> ~0.4x of the engine's default shot tuning, 1.4 -> ~2.8x.
        // Ballistic: the round's momentum sets the size, the score only shapes it (sprint, heavy).
        float t = Mathf.Clamp01((intensity - 0.2f) / 1.2f);
        float push = (entry.BallisticShove.Value ? Mathf.Lerp(0.7f, 2.2f, t) * ballistic : Mathf.Lerp(0.4f, 2.8f, t))
                     * entry.Impulse.Value;
        body.Hit(hitPoint, forceDirection, Mathf.Min(45f * push, 150f), legBuckle, aimAt, aimBackChance);
    }
}

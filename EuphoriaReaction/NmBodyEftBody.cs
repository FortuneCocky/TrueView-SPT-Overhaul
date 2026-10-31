using System;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using static Delirium.Native;

namespace EuphoriaReaction;

/// <summary>
/// The EFT body: the doll keeps its own joints and muscles but takes EFT's own ragdoll weights and limb
/// thickness, read off a living bot (the same skeleton, rigidbodies and HitCollider capsules on every bot).
///  - Weights: EFT has 12 bodies, 76 kg (pelvis 11, thighs 6, calves 4, Spine2 and Spine3 8 each, upper
///    arms, forearms and head 5.6), against the doll's 21 parts and 77 kg (fred.xrt's masses: the build text's 41.9 kg
///    column isn't what a loaded build weighs). Each EFT body's mass is shared
///    over the doll parts it covers in the doll's own proportions (EFT's calf -> the doll's calf and foot),
///    on the finished build, so the stretch to SPT's limbs can't undo it.
///  - Limb thickness: EFT's thigh, calf and arm capsule radii go into the build text before it loads; the
///    lengths are still the doll's, stretched to SPT's bones. The torso (flat boxes and armor plates in
///    EFT) and head keep the doll's capsules: they don't map onto them.
/// </summary>
internal partial class NmBody
{
    private static IntPtr _baseBuildEft; // ped_build.txt with EFT's limb radii (Init)
    private static string _pluginDir;

    /// The base every doll is made from: with EFT's limb radii.
    private static IntPtr ShapeBase => _baseBuildEft != IntPtr.Zero ? _baseBuildEft : _baseBuild;

    /// The doll for this bot: stretched to his bones, on EFT's weights.
    private IntPtr ChooseBuild()
    {
        IntPtr s = ScaledBuild();
        ApplyEftMasses(s);
        return s;
    }

    // EFT HitCollider capsule radii (m): thighs, calves (left/right averaged), upper arms, forearms
    private static readonly (int part, float radius)[] EftRadius =
    {
        (1, 0.100f), (4, 0.100f), (2, 0.091f), (5, 0.091f),
        (14, 0.074f), (18, 0.074f), (15, 0.045f), (19, 0.045f),
    };

    // EFT rigidbody (kg) -> the doll parts it covers
    private static readonly (float kg, int[] parts)[] EftMass =
    {
        (11f, new[] { 0 }),
        (6f, new[] { 1 }), (6f, new[] { 4 }),
        (4f, new[] { 2, 3 }), (4f, new[] { 5, 6 }),
        (8f, new[] { 7, 8 }),                // Spine2: the lower back
        (8f, new[] { 9, 10, 13, 17 }),       // Spine3: chest and clavicles
        (5.625f, new[] { 11, 12 }),          // head and neck
        (5.625f, new[] { 14 }), (5.625f, new[] { 18 }),
        (5.625f, new[] { 15, 16 }), (5.625f, new[] { 19, 20 }), // forearm and hand
    };

    /// Init: ped_build.txt again with EFT's limb radii. Its fields are name, parent, mass, radius, half
    /// length, ...; checked against the loaded build first, so a format that reads otherwise is left alone.
    private static void LoadEftShapes(string dir)
    {
        try
        {
            string[] lines = File.ReadAllLines(Path.Combine(dir, "ped_build.txt"));
            var ci = CultureInfo.InvariantCulture;
            float[] m = new float[1], r = new float[1], h = new float[1];
            var sb = new StringBuilder();
            int part = 0, set = 0;
            foreach (string line in lines)
            {
                string[] t = line.Split(' ');
                if (t.Length < 5 || part >= Parts) { sb.Append(line).Append('\n'); continue; }
                nm_build_part_info(_baseBuild, part, m, r, h, out _);
                // radius only: a loaded build weighs the character file's masses (fred.xrt, ~77 kg), not this column's
                if (Mathf.Abs(float.Parse(t[3], ci) - r[0]) > 1e-4f)
                {
                    EuphoriaPlugin.Log($"Delirium: EFT body: ped_build.txt line {part} doesn't read as radius ({t[3]} vs {r[0]}) - limb shapes left as the doll's");
                    return;
                }
                foreach (var (k, radius) in EftRadius)
                    if (k == part) { t[3] = radius.ToString("R", ci); set++; }
                sb.Append(string.Join(" ", t)).Append('\n');
                part++;
            }
            _baseBuildEft = nm_build_from_text(sb.ToString());
            EuphoriaPlugin.Log(_baseBuildEft != IntPtr.Zero
                ? $"Delirium: EFT body: limb shapes ready ({set} parts on EFT's radii)"
                : "Delirium: EFT body: build text refused (" + LastError() + ") - limb shapes left as the doll's");
        }
        catch (Exception e) { EuphoriaPlugin.Log("Delirium: EFT body shapes: " + e.Message); }
    }

    /// EFT's weights on a finished build (after the stretch or fit).
    private static void ApplyEftMasses(IntPtr b)
    {
        if (b == IntPtr.Zero) return;
        float[] m = new float[1], r = new float[1], h = new float[1];
        float before = 0f, after = 0f;
        var mass = new float[Parts];
        for (int k = 0; k < Parts; k++) { nm_build_part_info(b, k, m, r, h, out _); mass[k] = m[0]; before += m[0]; }
        foreach (var (kg, parts) in EftMass)
        {
            float own = 0f;
            foreach (int k in parts) own += mass[k];
            foreach (int k in parts)
            {
                float share = own > 1e-4f ? mass[k] / own : 1f / parts.Length;
                if (nm_build_set_mass(b, k, kg * share) < 0) { EuphoriaPlugin.Log("Delirium: EFT body masses: " + LastError()); return; }
                after += kg * share;
            }
        }
        if (!_eftMassLogged)
        {
            _eftMassLogged = true;
            nm_build_part_info(b, 1, m, r, h, out _);
            EuphoriaPlugin.Log($"Delirium: EFT body: doll {before:F1} kg -> {after:F1} kg (EFT's weights); thigh radius now {r[0]:F3} m");
        }
    }

    private static bool _eftMassLogged;
}

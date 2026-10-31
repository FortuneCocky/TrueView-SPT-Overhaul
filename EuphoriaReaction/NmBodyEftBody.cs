using System;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using static Delirium.Native;

namespace EuphoriaReaction;

/// <summary>
/// The EFT body: the doll keeps its own joints and muscles but takes EFT's own ragdoll weights, read off
/// a living bot (the same skeleton and rigidbodies on every bot).
///  - Weights: EFT has 12 bodies, 75 kg (pelvis 11, thighs 6, calves 4, Spine2 and Spine3 8 each, upper
///    arms, forearms and head 5.6), against the doll's 21 parts. Each EFT body's mass is shared
///    over the doll parts it covers in the doll's own proportions (EFT's calf -> the doll's calf and foot),
///    on the finished build, so the stretch to SPT's limbs can't undo it.
///  - Limb thickness: body.json already has EFT's thigh, calf and arm capsule radii (make_character.py).
/// </summary>
internal partial class NmBody
{
    private static string _pluginDir;

    /// The base every doll is made from.
    private static IntPtr ShapeBase => _baseBuild;

    /// The doll for this bot: stretched to his bones, on EFT's weights.
    private IntPtr ChooseBuild()
    {
        IntPtr s = ScaledBuild();
        ApplyEftMasses(s);
        return s;
    }

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
